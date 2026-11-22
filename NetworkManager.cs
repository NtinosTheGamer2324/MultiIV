// ============================ NetworkManager.cs ============================
//
// Reliable-UDP client transport. Mirrors server/network.py (see its header for
// the wire format). Summary:
//   handshake  : CONNECT -> ACCEPT (session id = clientSalt ^ serverSalt)
//   reliable   : ordered, acked, resent  (JoinRequest, ChatMessage)
//   unreliable : positions, vehicle updates, weapon fire
//   keep-alive : PING/PONG; silence for SilenceTimeout = connection lost
// Any way the connection ends (manual, kick, refused, timeout, socket error)
// goes through DisconnectSilent()/Disconnect() and fires OnDisconnected exactly once.
//
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using IVSDKDotNet;

namespace MultiIV
{
    public class NetworkManager
    {
        // ── Public state ────────────────────────────────────────────────────────
        public bool IsConnected { get; private set; }
        public int MyPlayerId { get; private set; } = -1;

        /// <summary>Round-trip time to the server in ms (-1 until measured).</summary>
        public int PingMs { get { return _rttMs; } }

        public event Action<int, string, float, float, float, float, float> OnPlayerUpdate; // id, name, x, y, z, heading, speed
        public event Action<int> OnPlayerLeft;
        public event Action<int> OnJoinAccepted;
        public event Action<string, string> OnChatMessage;   // sender, message
        public event Action<string> OnKicked;        // reason
        public event Action OnDisconnected;          // fired on ANY disconnect path (manual, kick, or error)
        public event Action<int, string, uint, float, float, float, float, float, float>
            OnPlayerVehicleUpdate;   // id, name, modelHash, x,y,z, heading, pedHealth, vehHealth

        public event Action<int> OnPlayerExitVehicle;   // id

        // Server rejected our reported position (anti-cheat) and says where we should be.
        public event Action<float, float, float, float> OnCorrection;   // x, y, z, heading

        public event Action<int, uint, float, float, float, float>
            OnPlayerWeaponFire;   // id, weaponHash, x, y, z, heading

        // ── Protocol constants (must match server/network.py) ───────────────────
        private const byte Magic = 0xA7;
        private const byte ProtoVersion = 1;
        private const byte T_Connect = 1, T_Accept = 2, T_Reject = 3, T_Unrel = 4, T_Rel = 5,
                           T_Ack = 6, T_Ping = 7, T_Pong = 8, T_Disconnect = 9;

        private const int RecvWindow = 1024;
        private const int MaxPending = 256;

        private static readonly TimeSpan HandshakeResend = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(8);    // CONNECT/JoinAccepted must finish within this
        private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(10); // nothing received for this long = lost
        private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);

        private enum State { Idle, Handshaking, Established }

        private class Pending
        {
            public byte[] Datagram;
            public DateTime FirstSent;
            public DateTime LastSent;
            public int Tries;
        }

        // ── Private ─────────────────────────────────────────────────────────────
        private readonly object _lock = new object();
        private UdpClient _udp;
        private IPEndPoint _server;
        private Thread _receiveThread;
        private volatile bool _running;

        private State _state = State.Idle;
        private string _playerName;
        private string _password = "";
        private uint _clientSalt;
        private uint _sessionId;
        private volatile bool _joined;                 // JoinAccepted received
        private DateTime _connectStartUtc;
        private DateTime _lastConnectSendUtc;
        private DateTime _lastRecvUtc;
        private DateTime _lastSendUtc;
        private volatile int _rttMs = -1;

        private ushort _sendSeq;
        private readonly Dictionary<ushort, Pending> _pending = new Dictionary<ushort, Pending>();
        private double _rto = 0.25;                    // seconds
        private double _rtt = -1;                      // seconds, smoothed
        private ushort _recvExpected;
        private readonly Dictionary<ushort, byte[]> _recvBuffer = new Dictionary<ushort, byte[]>();

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // ── Little-endian helpers ───────────────────────────────────────────────
        private static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        private static void PutU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        }
        private static uint GetU32(byte[] b, int o)
        {
            return unchecked((uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)));
        }
        private static ushort GetU16(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        // ── Connect / Disconnect ────────────────────────────────────────────────

        public void Connect(string host, int port, string playerName, string password = "")
        {
            if (IsConnected) return;
            try
            {
                // Resolve hostname — also handles raw IPs like "127.0.0.1".
                // Prefer IPv4 (the server socket is IPv4).
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0)
                    throw new Exception("Could not resolve hostname: " + host);

                IPAddress addr = addrs[0];
                foreach (IPAddress a in addrs)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
                }

                lock (_lock)
                {
                    _udp = new UdpClient(addr.AddressFamily);
                    _udp.Client.Bind(new IPEndPoint(
                        addr.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
                    _server = new IPEndPoint(addr, port);

                    _playerName = playerName;
                    _password = password ?? "";
                    _clientSalt = GetU32(Guid.NewGuid().ToByteArray(), 0);
                    _sessionId = 0;
                    _joined = false;
                    MyPlayerId = -1;
                    _sendSeq = 0;
                    _recvExpected = 0;
                    _pending.Clear();
                    _recvBuffer.Clear();
                    _rtt = -1; _rttMs = -1; _rto = 0.25;

                    DateTime now = DateTime.UtcNow;
                    _connectStartUtc = _lastRecvUtc = _lastSendUtc = now;
                    _lastConnectSendUtc = DateTime.MinValue;
                    _state = State.Handshaking;

                    // Set BEFORE the thread starts so an instant failure
                    // (e.g. connection refused) can still raise OnDisconnected.
                    IsConnected = true;
                    _running = true;
                }

                SendConnect();   // first attempt immediately; the thread keeps retrying

                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MultiIV-Net" };
                _receiveThread.Start();

                IVGame.Console.PrintWarning($"[MultiIV] Connecting to {host}:{port} as '{playerName}'...");
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] Connect failed: " + ex.Message);
                Cleanup();
                OnDisconnected?.Invoke();
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                if (!IsConnected) return;
                if (_state == State.Established)
                    SendDisconnectDatagram("Quit");
                Cleanup();
            }
            IVGame.Console.PrintWarning("[MultiIV] Disconnected.");
            OnDisconnected?.Invoke();
        }

        // Called internally when the connection ends without us asking
        // (kick, timeout, refused, socket error). No outbound packet needed.
        private void DisconnectSilent()
        {
            lock (_lock)
            {
                if (!IsConnected) return;     // already torn down (prevents double-fire)
                Cleanup();
            }
            OnDisconnected?.Invoke();
        }

        private void Cleanup()
        {
            lock (_lock)
            {
                _running = false;
                IsConnected = false;
                _joined = false;
                MyPlayerId = -1;
                _state = State.Idle;
                _pending.Clear();
                _recvBuffer.Clear();
                try { if (_udp != null) _udp.Close(); } catch { }
                _udp = null;
            }
        }

        // ── Low-level send ──────────────────────────────────────────────────────

        private void RawSend(byte[] data)
        {
            UdpClient udp = _udp;
            if (udp == null) return;
            try
            {
                udp.Send(data, data.Length, _server);
                _lastSendUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                if (_running)
                    IVGame.Console.PrintError("[MultiIV] Send failed: " + ex.Message);
            }
        }

        private void SendConnect()
        {
            byte[] d = new byte[7];
            d[0] = Magic; d[1] = T_Connect;
            PutU32(d, 2, _clientSalt);
            d[6] = ProtoVersion;
            _lastConnectSendUtc = DateTime.UtcNow;
            RawSend(d);
        }

        private byte[] BuildSessionDatagram(byte type, byte[] payload, int payloadLen, bool withSeq, ushort seq)
        {
            int header = withSeq ? 8 : 6;
            byte[] d = new byte[header + payloadLen];
            d[0] = Magic; d[1] = type;
            PutU32(d, 2, _sessionId);
            if (withSeq) PutU16(d, 6, seq);
            if (payloadLen > 0) Buffer.BlockCopy(payload, 0, d, header, payloadLen);
            return d;
        }

        private void SendDisconnectDatagram(string reason)
        {
            byte[] r = Encoding.UTF8.GetBytes(reason ?? "");
            byte[] d = BuildSessionDatagram(T_Disconnect, r, r.Length, false, 0);
            for (int i = 0; i < 3; i++) RawSend(d);   // best effort, nothing to ack
        }

        private void SendReliable(byte[] payload)
        {
            lock (_lock)
            {
                if (_state != State.Established) return;
                ushort seq = _sendSeq;
                _sendSeq = (ushort)(_sendSeq + 1);
                byte[] d = BuildSessionDatagram(T_Rel, payload, payload.Length, true, seq);
                DateTime now = DateTime.UtcNow;
                _pending[seq] = new Pending { Datagram = d, FirstSent = now, LastSent = now, Tries = 1 };
                RawSend(d);
            }
        }

        private void SendUnreliable(byte[] payload)
        {
            lock (_lock)
            {
                if (_state != State.Established) return;
                RawSend(BuildSessionDatagram(T_Unrel, payload, payload.Length, false, 0));
            }
        }

        // Chat and the join request must arrive; everything else is superseded by the next update.
        private void SendPacket(Dictionary<string, object> packet)
        {
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(Json.Serialize(packet));
                string type = packet.ContainsKey("Type") ? packet["Type"] as string : null;
                if (type == "JoinRequest" || type == "ChatMessage")
                    SendReliable(payload);
                else
                    SendUnreliable(payload);
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] Send failed: " + ex.Message);
            }
        }

        // ── Send ────────────────────────────────────────────────────────────────

        public void SendPositionUpdate(int playerId, float x, float y, float z, float heading, float speed = 0f)
        {
            SendPacket(new Dictionary<string, object>
            {
                ["Type"] = "PositionUpdate",
                ["PlayerId"] = playerId,
                ["X"] = x,
                ["Y"] = y,
                ["Z"] = z,
                ["Heading"] = heading,
                ["Speed"] = speed
            });
        }

        public void SendChatMessage(int playerId, string message)
        {
            SendPacket(new Dictionary<string, object>
            {
                ["Type"] = "ChatMessage",
                ["PlayerId"] = playerId,
                ["Message"] = message
            });
        }

        public void SendVehicleUpdate(
            int playerId,
            uint modelHash,
            float x, float y, float z, float heading,
            float pedHealth, float vehicleHealth)
        {
            SendPacket(new Dictionary<string, object>
            {
                ["Type"] = "VehicleUpdate",
                ["PlayerId"] = playerId,
                ["ModelHash"] = (long)modelHash,   // JSON has no uint; cast to long
                ["X"] = x,
                ["Y"] = y,
                ["Z"] = z,
                ["Heading"] = heading,
                ["PedHealth"] = pedHealth,
                ["VehicleHealth"] = vehicleHealth
            });
        }

        public void SendWeaponFire(
            int playerId, uint weaponHash,
            float x, float y, float z, float heading)
        {
            SendPacket(new Dictionary<string, object>
            {
                ["Type"] = "WeaponFire",
                ["PlayerId"] = playerId,
                ["WeaponHash"] = (long)weaponHash,   // JSON has no uint; cast to long
                ["X"] = x,
                ["Y"] = y,
                ["Z"] = z,
                ["Heading"] = heading
            });
        }

        // ── Receive loop (own thread) ───────────────────────────────────────────

        private void ReceiveLoop()
        {
            UdpClient udp = _udp;
            while (_running)
            {
                try
                {
                    string failure = Housekeeping(DateTime.UtcNow);
                    if (failure != null)
                    {
                        IVGame.Console.PrintError("[MultiIV] " + failure);
                        DisconnectSilent();
                        break;
                    }

                    // Wait up to 50 ms for data, then loop to run timers.
                    if (!udp.Client.Poll(50000, SelectMode.SelectRead))
                        continue;

                    IPEndPoint remote = null;
                    byte[] data = udp.Receive(ref remote);
                    if (remote == null || !remote.Equals(_server)) continue;   // ignore strangers

                    HandleDatagram(data);
                }
                catch (SocketException sex)
                {
                    if (!_running) break;   // we closed the socket ourselves

                    if (sex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // Windows reports an ICMP "port unreachable" (nothing listening,
                        // e.g. 127.0.0.1:7777 with no server) as WSAECONNRESET on UDP.
                        if (!_joined)
                        {
                            IVGame.Console.PrintError("[MultiIV] Server refused connection (nothing listening).");
                            DisconnectSilent();
                            break;
                        }
                        continue;   // after joining a stray reset is ignored; the silence timeout covers real loss
                    }

                    IVGame.Console.PrintError("[MultiIV] Socket error: " + sex.Message);
                    DisconnectSilent();
                    break;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        IVGame.Console.PrintError("[MultiIV] Receive error: " + ex.Message);
                }
            }
        }

        /// <summary>Timers: handshake retry, reliable resend, keep-alive, timeouts. Returns a failure message or null.</summary>
        private string Housekeeping(DateTime now)
        {
            lock (_lock)
            {
                if (_state == State.Handshaking)
                {
                    if (now - _connectStartUtc > JoinTimeout)
                        return "No response from server (connection timed out).";
                    if (now - _lastConnectSendUtc >= HandshakeResend)
                        SendConnect();
                }
                else if (_state == State.Established)
                {
                    if (!_joined && now - _connectStartUtc > JoinTimeout)
                        return "Server did not accept the join request (timed out).";
                    if (now - _lastRecvUtc > SilenceTimeout)
                        return "Lost connection to server (timed out).";
                    if (_pending.Count > MaxPending)
                        return "Lost connection to server (too many unacknowledged packets).";

                    foreach (KeyValuePair<ushort, Pending> kv in _pending)
                    {
                        Pending p = kv.Value;
                        double interval = Math.Min(1.0, _rto * Math.Pow(1.5, Math.Min(p.Tries - 1, 4)));
                        if ((now - p.LastSent).TotalSeconds >= interval)
                        {
                            p.LastSent = now;
                            p.Tries++;
                            RawSend(p.Datagram);
                        }
                    }

                    if (now - _lastSendUtc >= KeepAlive)
                    {
                        byte[] stamp = new byte[4];
                        PutU32(stamp, 0, unchecked((uint)(Environment.TickCount)));
                        RawSend(BuildSessionDatagram(T_Ping, stamp, 4, false, 0));
                    }
                }
            }
            return null;
        }

        private void HandleDatagram(byte[] d)
        {
            if (d.Length < 2 || d[0] != Magic) return;
            byte type = d[1];
            DateTime now = DateTime.UtcNow;

            // ── Handshake replies (no session id yet) ──
            if (type == T_Accept)
            {
                if (d.Length < 10) return;
                bool sendJoin = false;
                lock (_lock)
                {
                    if (_state == State.Handshaking && GetU32(d, 2) == _clientSalt)
                    {
                        _sessionId = _clientSalt ^ GetU32(d, 6);
                        _state = State.Established;
                        _lastRecvUtc = now;
                        sendJoin = true;
                    }
                }
                if (sendJoin)
                {
                    SendPacket(new Dictionary<string, object>
                    {
                        ["Type"] = "JoinRequest",
                        ["PlayerName"] = _playerName,
                        ["Password"] = _password
                    });
                }
                return;
            }

            if (type == T_Reject)
            {
                if (d.Length < 6 || GetU32(d, 2) != _clientSalt) return;
                string why = d.Length > 6 ? Encoding.UTF8.GetString(d, 6, d.Length - 6) : "Rejected by server.";
                IVGame.Console.PrintError("[MultiIV] Server rejected connection: " + why);
                DisconnectSilent();
                return;
            }

            // ── Everything else carries the session id ──
            if (d.Length < 6) return;

            List<byte[]> deliver = null;
            string kickReason = null;
            bool disconnect = false;

            lock (_lock)
            {
                if (_state != State.Established || GetU32(d, 2) != _sessionId) return;
                _lastRecvUtc = now;

                switch (type)
                {
                    case T_Unrel:
                        deliver = new List<byte[]>();
                        deliver.Add(Slice(d, 6));
                        break;

                    case T_Rel:
                    {
                        if (d.Length < 8) return;
                        ushort seq = GetU16(d, 6);

                        // Always ack, even duplicates: our earlier ack may have been lost.
                        RawSend(MakeAck(seq));

                        ushort ahead = (ushort)(seq - _recvExpected);
                        if (ahead == 0)
                        {
                            deliver = new List<byte[]>();
                            deliver.Add(Slice(d, 8));
                            _recvExpected = (ushort)(_recvExpected + 1);
                            byte[] next;
                            while (_recvBuffer.TryGetValue(_recvExpected, out next))
                            {
                                _recvBuffer.Remove(_recvExpected);
                                deliver.Add(next);
                                _recvExpected = (ushort)(_recvExpected + 1);
                            }
                        }
                        else if (ahead < RecvWindow)
                        {
                            _recvBuffer[seq] = Slice(d, 8);
                        }
                        break;
                    }

                    case T_Ack:
                    {
                        if (d.Length < 8) return;
                        ushort seq = GetU16(d, 6);
                        Pending p;
                        if (_pending.TryGetValue(seq, out p))
                        {
                            _pending.Remove(seq);
                            if (p.Tries == 1)   // only time packets that weren't resent
                            {
                                double sample = (now - p.FirstSent).TotalSeconds;
                                _rtt = _rtt < 0 ? sample : 0.875 * _rtt + 0.125 * sample;
                                _rto = Math.Min(1.0, Math.Max(0.1, _rtt * 2 + 0.02));
                                _rttMs = (int)(_rtt * 1000);
                            }
                        }
                        break;
                    }

                    case T_Ping:
                        if (d.Length >= 10)
                        {
                            byte[] echo = new byte[4];
                            Buffer.BlockCopy(d, 6, echo, 0, 4);
                            RawSend(BuildSessionDatagram(T_Pong, echo, 4, false, 0));
                        }
                        break;

                    case T_Pong:
                        if (d.Length >= 10)
                        {
                            uint stamp = GetU32(d, 6);
                            uint nowMs = unchecked((uint)Environment.TickCount);
                            uint sampleMs = unchecked(nowMs - stamp);
                            if (sampleMs < 10000)
                            {
                                double s = sampleMs / 1000.0;
                                _rtt = _rtt < 0 ? s : 0.875 * _rtt + 0.125 * s;
                                _rttMs = (int)(_rtt * 1000);
                            }
                        }
                        break;

                    case T_Disconnect:
                        kickReason = d.Length > 6 ? Encoding.UTF8.GetString(d, 6, d.Length - 6) : "Disconnected by server.";
                        disconnect = true;
                        break;
                }
            }

            if (disconnect)
            {
                DisconnectSilent();
                OnKicked?.Invoke(kickReason);
                return;
            }

            if (deliver != null)
            {
                foreach (byte[] payload in deliver)
                    HandlePayload(payload);
            }
        }

        private byte[] MakeAck(ushort seq)
        {
            byte[] ack = new byte[2];
            PutU16(ack, 0, seq);
            return BuildSessionDatagram(T_Ack, ack, 2, false, 0);
        }

        private static byte[] Slice(byte[] src, int offset)
        {
            byte[] r = new byte[src.Length - offset];
            Buffer.BlockCopy(src, offset, r, 0, r.Length);
            return r;
        }

        private void HandlePayload(byte[] payload)
        {
            try
            {
                string json = Encoding.UTF8.GetString(payload);
                var packet = Json.Deserialize<Dictionary<string, object>>(json);
                if (packet == null || !packet.ContainsKey("Type")) return;
                HandlePacket(packet["Type"] as string, packet);
            }
            catch (Exception ex)
            {
                if (_running)
                    IVGame.Console.PrintError("[MultiIV] Bad packet: " + ex.Message);
            }
        }

        private void HandlePacket(string type, Dictionary<string, object> packet)
        {
            switch (type)
            {
                case "JoinAccepted":
                    MyPlayerId = Convert.ToInt32(packet["AssignedPlayerId"]);
                    _joined = true;
                    OnJoinAccepted?.Invoke(MyPlayerId);
                    IVGame.Console.PrintWarning("[MultiIV] Joined! ID: " + MyPlayerId);
                    break;

                case "WorldStateUpdate":
                    HandleWorldState(packet);
                    break;

                case "PlayerLeft":
                    OnPlayerLeft?.Invoke(Convert.ToInt32(packet["PlayerId"]));
                    break;

                case "ChatMessage":
                    string sender = packet.ContainsKey("Sender") ? packet["Sender"] as string : "?";
                    string message = packet.ContainsKey("Message") ? packet["Message"] as string : "";
                    OnChatMessage?.Invoke(sender, message);
                    break;

                case "Kicked":
                    string reason = packet.ContainsKey("Reason") ? packet["Reason"] as string : "Kicked by server.";
                    DisconnectSilent();
                    OnKicked?.Invoke(reason);
                    break;

                case "Correction":
                    OnCorrection?.Invoke(
                        Convert.ToSingle(packet["X"]), Convert.ToSingle(packet["Y"]),
                        Convert.ToSingle(packet["Z"]), Convert.ToSingle(packet["Heading"]));
                    break;

                case "WeaponFire":
                    int wfId = Convert.ToInt32(packet["PlayerId"]);
                    if (wfId == MyPlayerId) break; // don't play back our own shot

                    uint wHash = packet.ContainsKey("WeaponHash")
                        ? (uint)Convert.ToInt64(packet["WeaponHash"]) : 0;
                    float wx = Convert.ToSingle(packet["X"]);
                    float wy = Convert.ToSingle(packet["Y"]);
                    float wz = Convert.ToSingle(packet["Z"]);
                    float wh = Convert.ToSingle(packet["Heading"]);

                    OnPlayerWeaponFire?.Invoke(wfId, wHash, wx, wy, wz, wh);
                    break;
            }
        }

        private void HandleWorldState(Dictionary<string, object> packet)
        {
            if (!packet.ContainsKey("Players")) return;
            var players = (System.Collections.ArrayList)packet["Players"];

            foreach (object obj in players)
            {
                var p = (Dictionary<string, object>)obj;

                int id = Convert.ToInt32(p["PlayerId"]);
                float x = Convert.ToSingle(p["X"]);
                float y = Convert.ToSingle(p["Y"]);
                float z = Convert.ToSingle(p["Z"]);
                float heading = Convert.ToSingle(p["Heading"]);
                float health = p.ContainsKey("Health") ? Convert.ToSingle(p["Health"]) : 100f;
                float speed = p.ContainsKey("Speed") ? Convert.ToSingle(p["Speed"]) : 0f;
                string name = p.ContainsKey("PlayerName") ? p["PlayerName"] as string : $"Player{id}";

                if (id == MyPlayerId) continue;

                if (p.ContainsKey("Vehicle") && p["Vehicle"] is Dictionary<string, object> veh)
                {
                    uint modelHash = (uint)Convert.ToInt64(veh["model_hash"]);
                    float vehicleHealth = Convert.ToSingle(veh["vehicle_health"]);

                    OnPlayerVehicleUpdate?.Invoke(
                        id, name, modelHash,
                        x, y, z, heading,
                        health, vehicleHealth);
                }
                else
                {
                    // On foot — make sure any vehicle they were in gets removed
                    OnPlayerExitVehicle?.Invoke(id);
                    OnPlayerUpdate?.Invoke(id, name, x, y, z, heading, speed);
                }
            }
        }
    }
}