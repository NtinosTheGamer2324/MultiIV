// ============================ Main.cs ============================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using IVSDKDotNet;
using IVSDKDotNet.Hooking;
using IVSDKDotNet.Enums;
using static IVSDKDotNet.Native.Natives;

namespace MultiIV
{
    public class Main : Script
    {
        // ── Sub-systems ──────────────────────────────────────────────────────────
        private readonly NetworkManager _net = new NetworkManager();
        private readonly PedManager _peds = new PedManager();
        private readonly VehicleManager _vehicles = new VehicleManager();
        private readonly StoryModeManager _storyMode = new StoryModeManager();
        private readonly LoadingScreen _loadingScreen = new LoadingScreen();
        private readonly Mission _mission = new Mission();
        private readonly DiscordRPC _discord = new DiscordRPC("1544712159068553256");
        private readonly UI _ui = new UI();

        // ── Thread-safe main-thread queue ────────────────────────────────────────
        private readonly Queue<Action> _mainQueue = new Queue<Action>();
        private readonly object _queueLock = new object();
        bool isMultiplayerSession = MultiIvConfig.GetMultiIvSession();

        // ── UI state ─────────────────────────────────────────────────────────────
        private bool _menuOpen = false;

        // ── State Machine kinda ──────────────────────────────────────────────────
        private int OnInGameOnce = 0;
        private int DoOnceidk = 0;

        // ── MultiIV ARG Server ONLINE MODE ───────────────────────────────────────
        private string m_server;
        private int m_port;
        private string m_offlinename;
        private string m_password = "";

        // ── Enter-multiplayer flow state (F1 entry point) ────────────────────────
        private bool _showSaveWarning = false;
        private bool _showOnlineConfirm = false;
        private bool _waitingForSaveMenu = false;

        // ── Chat state ───────────────────────────────────────────────────────────
        private readonly List<(string sender, string message)> _chatHistory
            = new List<(string, string)>();
        private const int MaxChatLines = 50;
        private bool _chatInputOpen = false;
        private string _chatDraft = "";

        // ── Position / vehicle send throttle ─────────────────────────────────────
        private int _ticksSinceLastSend = 0;
        private const int SendEveryNTicks = 10;

        // On-foot tracking
        private Vector3 _lastSentPos = new Vector3(float.NaN, float.NaN, float.NaN);
        private float _lastSentHeading = float.NaN;
        private const float MoveThreshold = 0.05f;

        // Vehicle tracking
        private bool _inVehicle = false;
        private int _lastVehicleHandle = 0;
        private Vector3 _lastSentVehPos = new Vector3(float.NaN, float.NaN, float.NaN);
        private float _lastSentVehHeading = float.NaN;

        // Weapon fire tracking (edge-triggered: send once per shot, not per tick)
        private bool _wasShooting = false;

        // ── Heartbeat (keeps the server from idle-kicking us) ────────────────────
        private DateTime _lastForcedSend = DateTime.MinValue;
        private static readonly TimeSpan ForceSendInterval = TimeSpan.FromSeconds(5);

        // ── File ─────────────────────────────────────────────────────────────────
        private static readonly string MultiIVpath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MultiIV");

        private static string GTAIVPath =>
            Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

        private static string MIVGAMEDATA =>
            Path.Combine(MultiIVpath, "gamedata");


        // ── Constructor ──────────────────────────────────────────────────────────

        public Main()
        {
            if (!Directory.Exists(MultiIVpath))
            {
                Directory.CreateDirectory(MultiIVpath);
            }

            if (!File.Exists(MIVGAMEDATA))
            {
                File.WriteAllText(MIVGAMEDATA, "gtapath: " + GTAIVPath);
            }

            if (isMultiplayerSession)
            {
                // ONLINE MODE!
                m_server = MultiIvConfig.GetString("server", "127.0.0.1");
                m_port = MultiIvConfig.GetInt("port", 7777);
                m_offlinename = MultiIvConfig.GetString("offlinename", "NIKO");
                m_password = MultiIvConfig.GetString("password", "");

            }

            Tick += OnTick;
            KeyDown += OnKeyDown;
            OnImGuiRendering += OnImGuiRender;
            Uninitialize += OnUninitialize;

            _net.OnPlayerUpdate += (id, name, x, y, z, h, speed) =>
                Enqueue(() =>
                {
                    _vehicles.RemoveForPlayer(id);
                    _peds.UpdateOrSpawn(id, name, x, y, z, h, speed);
                });

            _net.OnPlayerVehicleUpdate += (id, name, modelHash, x, y, z, h, pedHp, vehHp) =>
                Enqueue(() =>
                {
                    _peds.UpdateOrSpawn(id, name, x, y, z, h);
                    if (_peds.Players.TryGetValue(id, out RemotePlayer rp))
                    {
                        rp.InVehicle = true;
                        _vehicles.UpdateOrSpawn(id, modelHash, x, y, z, h, vehHp, rp.PedHandle);
                    }
                });

            _net.OnPlayerExitVehicle += (id) =>
                Enqueue(() =>
                {
                    _vehicles.RemoveForPlayer(id);
                    if (_peds.Players.TryGetValue(id, out RemotePlayer rp))
                    {
                        rp.InVehicle = false;
                        if (rp.PedHandle != 0 && IS_CHAR_IN_ANY_CAR(rp.PedHandle))
                        {
                            GET_CHAR_COORDINATES(rp.PedHandle, out Vector3 curPos);
                            WARP_CHAR_FROM_CAR_TO_COORD(rp.PedHandle, curPos.X, curPos.Y, curPos.Z);
                        }
                    }
                });

            _net.OnPlayerWeaponFire += (id, weaponHash, x, y, z, heading) =>
                Enqueue(() => _peds.PlayWeaponFire(id, weaponHash, x, y, z, heading));

            _net.OnPlayerLeft += (id) =>
                Enqueue(() =>
                {
                    _vehicles.RemoveForPlayerImmediate(id);
                    _peds.RemovePlayer(id);
                });

            // Loading screen disappears + story mode systems get disabled
            // the moment the server actually accepts us.
            _net.OnJoinAccepted += (id) =>
                Enqueue(() =>
                {
                    _peds.RequestModel();
                    _storyMode.Disable();
                    _loadingScreen.Hide();
                });

            _net.OnChatMessage += (sender, msg) =>
                Enqueue(() => AddChatLine(sender, msg));

            // The server rejected our position (anti-cheat): put the player back where
            // the server last accepted them.
            _net.OnCorrection += (x, y, z, h) =>
                Enqueue(() => ApplyCorrection(x, y, z, h));

            _net.OnKicked += (reason) =>
                Enqueue(() => AddChatLine("Server", $"You were kicked: {reason}"));

            // Single place that fully tears down local state on ANY
            // disconnect path: manual disconnect, kick, or connect failure.
            _net.OnDisconnected += () =>
                Enqueue(() =>
                {
                    FullLocalCleanup();
                    _ui.ShowDisconnectedUI();
                });

            _discord.Initialize();

            SET_THIS_SCRIPT_CAN_REMOVE_BLIPS_CREATED_BY_ANY_SCRIPT(true);
        }
        // ── Helpers ──────────────────────────────────────────────────────────────

        private void Enqueue(Action a)
        {
            lock (_queueLock) _mainQueue.Enqueue(a);
        }

        private void DrainQueue()
        {
            lock (_queueLock)
            {
                int processed = 0;
                while (_mainQueue.Count > 0 && processed < 20)
                {
                    _mainQueue.Dequeue()?.Invoke();
                    processed++;
                }
            }
        }

        private void AddChatLine(string sender, string message)
        {
            _chatHistory.Add((sender, message));
            if (_chatHistory.Count > MaxChatLines)
                _chatHistory.RemoveAt(0);
        }

        /// <summary>
        /// Wipes every remote ped/vehicle, restores story-mode systems and
        /// closes the loading screen. Called whenever we leave a server
        /// for any reason (manual disconnect, kick, or failed connect).
        /// </summary>
        private void FullLocalCleanup()
        {
            _vehicles.RemoveAll();
            _peds.RemoveAll();
            _storyMode.Restore();
            _loadingScreen.Hide();
            _inVehicle = false;
            _lastVehicleHandle = 0;
            _wasShooting = false;
        }

        private void ApplyCorrection(float x, float y, float z, float heading)
        {
            try
            {
                GET_PLAYER_CHAR(CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID()), out int ped);

                if (IS_CHAR_IN_ANY_CAR(ped))
                {
                    GET_CAR_CHAR_IS_USING(ped, out int veh);
                    SET_CAR_COORDINATES(veh, new Vector3(x, y, z));
                    SET_CAR_HEADING(veh, heading);
                }
                else
                {
                    SET_CHAR_COORDINATES_DONT_WARP_GANG_NO_OFFSET(ped, x, y, z);
                    SET_CHAR_HEADING(ped, heading);
                }

                // Force the next state update out immediately from the corrected spot.
                _lastSentPos = new Vector3(float.NaN, float.NaN, float.NaN);
                _lastSentVehPos = new Vector3(float.NaN, float.NaN, float.NaN);
                IVGame.Console.PrintWarning("[MultiIV] Server corrected your position.");
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] ApplyCorrection failed: " + ex.Message);
            }
        }

        private static (string host, int port) ParseAddress(string address, int defaultPort)
        {
            if (string.IsNullOrWhiteSpace(address))
                return ("127.0.0.1", defaultPort);

            address = address.Trim();

            if (address.StartsWith("["))
            {
                int closeBracket = address.IndexOf(']');
                if (closeBracket > 0)
                {
                    string ipv6Host = address.Substring(1, closeBracket - 1);
                    string rest = address.Substring(closeBracket + 1);
                    if (rest.StartsWith(":") && int.TryParse(rest.Substring(1), out int ipv6Port))
                        return (ipv6Host, ipv6Port);
                    return (ipv6Host, defaultPort);
                }
            }

            int lastColon = address.LastIndexOf(':');
            if (lastColon > 0)
            {
                string portStr = address.Substring(lastColon + 1);
                if (int.TryParse(portStr, out int parsedPort))
                    return (address.Substring(0, lastColon), parsedPort);
            }

            return (address, defaultPort);
        }

        private bool ShouldForceSend()
        {
            if (DateTime.UtcNow - _lastForcedSend >= ForceSendInterval)
            {
                _lastForcedSend = DateTime.UtcNow;
                return true;
            }
            return false;
        }

        // ── Game tick ────────────────────────────────────────────────────────────

        private void OnTick(object sender, EventArgs e)
        {
            DrainQueue();
            _loadingScreen.Tick();
            _peds.Tick();
            _vehicles.Tick();
            _storyMode.Tick();
            _mission.Tick();

            if (OnInGameOnce == 0)
            {
                if (isMultiplayerSession)
                {
                    _loadingScreen.Show($"Connecting to {m_server}:{m_port}...");
                    _net.Connect(m_server, m_port, m_offlinename, m_password);
                    if (_net.IsConnected == false)
                    {
                        _discord.StopPresence();
                        Environment.Exit(0);
                    }
                    _discord.ShowPresence("Freeroam", "Server: " + m_server + ":" + m_port + " As: " + m_offlinename);
                    _mission.startMIVFreeroam();
                }

                OnInGameOnce = 1;

            }


            if (!_net.IsConnected) return;

            _ticksSinceLastSend++;
            if (_ticksSinceLastSend < SendEveryNTicks) return;
            _ticksSinceLastSend = 0;

            SendOwnState();
        }

        private void SendOwnState()
        {
            if (_net.MyPlayerId == -1) return;

            try
            {
                GET_PLAYER_CHAR(CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID()), out int ped);

                CheckWeaponFire(ped);

                bool inCar = IS_CHAR_IN_ANY_CAR(ped);

                if (inCar)
                {
                    GET_CAR_CHAR_IS_USING(ped, out int veh);
                    GET_DRIVER_OF_CAR(veh, out int driverPed);
                    bool isDriving = (driverPed == ped);

                    if (isDriving)
                    {
                        GET_CAR_COORDINATES(veh, out Vector3 vehPos);
                        GET_CAR_HEADING(veh, out float vehHeading);
                        GET_CAR_HEALTH(veh, out uint vehHealthRaw);
                        float vehHealth = vehHealthRaw;
                        GET_CHAR_HEALTH(ped, out uint pedHpInt);
                        float pedHealth = pedHpInt;

                        bool barelyMoved =
                            _inVehicle &&
                            Vector3.Distance(vehPos, _lastSentVehPos) < MoveThreshold &&
                            Math.Abs(vehHeading - _lastSentVehHeading) < 1f;

                        if (!barelyMoved || ShouldForceSend())
                        {
                            _lastSentVehPos = vehPos;
                            _lastSentVehHeading = vehHeading;
                            _lastVehicleHandle = veh;
                            _inVehicle = true;

                            GET_CAR_MODEL(veh, out uint modelInt);
                            uint modelHash = modelInt;

                            int id = _net.MyPlayerId;
                            float x = vehPos.X, y = vehPos.Y, z = vehPos.Z;
                            float h = vehHeading;
                            float ph = pedHealth, vh = vehHealth;

                            ThreadPool.QueueUserWorkItem(_ =>
                                _net.SendVehicleUpdate(id, modelHash, x, y, z, h, ph, vh));
                        }
                    }
                    else
                    {
                        if (_inVehicle)
                        {
                            _inVehicle = false;
                            _lastVehicleHandle = 0;
                            _lastSentPos = new Vector3(float.NaN, float.NaN, float.NaN);
                            _lastSentHeading = float.NaN;
                            SendOnFootUpdate(ped, force: true);
                        }
                        else
                        {
                            SendOnFootUpdate(ped, force: false);
                        }
                    }
                }
                else
                {
                    if (_inVehicle)
                    {
                        _inVehicle = false;
                        _lastVehicleHandle = 0;
                        _lastSentPos = new Vector3(float.NaN, float.NaN, float.NaN);
                        _lastSentHeading = float.NaN;
                        SendOnFootUpdate(ped, force: true);
                    }
                    else
                    {
                        SendOnFootUpdate(ped, force: false);
                    }
                }
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] SendOwnState: " + ex.Message);
            }
        }

        /// <summary>
        /// Edge-triggered: fires one WeaponFire packet the instant the local
        /// player starts shooting, not every tick they hold the trigger.
        /// Keeps full-auto weapons from flooding the socket.
        /// </summary>
        private void CheckWeaponFire(int ped)
        {
            try
            {
                GET_CURRENT_CHAR_WEAPON(ped, out int weaponHash);
                bool isShooting = IS_CHAR_SHOOTING(ped);

                if (isShooting && !_wasShooting)
                {
                    GET_CHAR_COORDINATES(ped, out Vector3 pos);
                    GET_CHAR_HEADING(ped, out float heading);

                    int id = _net.MyPlayerId;
                    float x = pos.X, y = pos.Y, z = pos.Z, hd = heading;
                    uint wh = (uint)weaponHash;

                    ThreadPool.QueueUserWorkItem(_ =>
                        _net.SendWeaponFire(id, wh, x, y, z, hd));
                }

                _wasShooting = isShooting;
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] CheckWeaponFire: " + ex.Message);
            }
        }

        private void SendOnFootUpdate(int ped, bool force = false)
        {
            GET_CHAR_COORDINATES(ped, out Vector3 pos);
            GET_CHAR_HEADING(ped, out float heading);
            GET_CHAR_SPEED(ped, out float speed);

            bool barelyMoved =
                Vector3.Distance(pos, _lastSentPos) < MoveThreshold &&
                Math.Abs(heading - _lastSentHeading) < 1f;

            if (force || !barelyMoved || ShouldForceSend())
            {
                _lastSentPos = pos;
                _lastSentHeading = heading;

                int id = _net.MyPlayerId;
                float x = pos.X, y = pos.Y, z = pos.Z, hd = heading, spd = speed;
                ThreadPool.QueueUserWorkItem(_ =>
                    _net.SendPositionUpdate(id, x, y, z, hd, spd));
            }
        }

        // ── Input ────────────────────────────────────────────────────────────────

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1 && !_chatInputOpen)
            {
                // If we're already connected/connecting, F1 just toggles the
                // normal MultiIV menu like before.
                if (_net.IsConnected || _loadingScreen.Visible)
                {
                    _menuOpen = !_menuOpen;
                    return;
                }
                return;
            }

            if (e.KeyCode == Keys.T && _net.IsConnected && !_chatInputOpen && !_menuOpen)
            {
                _chatInputOpen = true;
                _chatDraft = "";
                return;
            }

            if (_chatInputOpen)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    _chatInputOpen = false;
                    _chatDraft = "";
                    return;
                }

                if (e.KeyCode == Keys.Return && !string.IsNullOrWhiteSpace(_chatDraft))
                {
                    string msg = _chatDraft.Trim();
                    int id = _net.MyPlayerId;
                    ThreadPool.QueueUserWorkItem(_ =>
                        _net.SendChatMessage(id, msg));
                    _chatDraft = "";
                    _chatInputOpen = false;
                }
            }
        }

        // ── ImGui ────────────────────────────────────────────────────────────────

        private void OnImGuiRender(IntPtr devicePtr, ImGuiIV_DrawingContext ctx)
        {
            // While connecting, the sky-cam is the whole screen — no overlay UI.
            if (_loadingScreen.Visible)
                return;

            DrawChatOverlay(ctx);
            DrawNameTagsOverlay(ctx);

            if (_menuOpen)
            {
                DrawMenu();
                DrawPlayerList();
            }

            _ui.RenderUIDisconnected(_discord, ctx);

            if (_chatInputOpen)
                DrawChatInput();
        }

        // ── Chat overlay ─────────────────────────────────────────────────────────

        private void DrawChatOverlay(ImGuiIV_DrawingContext ctx)
        {
            if (_chatHistory.Count == 0) return;

            GET_SCREEN_RESOLUTION(out int resX, out int resY);

            const int visibleLines = 8;
            const float lineH = 18f;
            const float padX = 24f;
            float baseY = resY - (_chatInputOpen ? 58f : 24f) - visibleLines * lineH;

            int start = Math.Max(0, _chatHistory.Count - visibleLines);

            for (int i = start; i < _chatHistory.Count; i++)
            {
                var (sndr, message) = _chatHistory[i];
                float y = baseY + (i - start) * lineH;
                string line = $"{sndr}: {message}";

                ctx.AddText(new Vector2(padX + 1, y + 1),
                    Color.FromArgb(180, 0, 0, 0), line);

                Color lineColor = sndr == "Server" ? Color.Orange : Color.Yellow;
                ctx.AddText(new Vector2(padX, y), lineColor, line);
            }
        }

        // ── Name tags overlay ─────────────────────────────────────────────────────

        private void DrawNameTagsOverlay(ImGuiIV_DrawingContext ctx)
        {
            if (_peds.Players.Count == 0) return;

            GET_SCREEN_RESOLUTION(out int resX, out int resY);

            foreach (var kvp in _peds.Players)
            {
                RemotePlayer rp = kvp.Value;

                Vector3 basePos = _vehicles.Vehicles.TryGetValue(kvp.Key, out RemoteVehicle rv)
                    ? rv.CurrentPosition
                    : rp.CurrentPosition;

                if (basePos == Vector3.Zero) continue;

                try
                {
                    Vector3 tagPos = new Vector3(basePos.X, basePos.Y, basePos.Z + 1.5f);

                    bool onScreen = GET_VIEWPORT_POSITION_OF_COORD(tagPos, 0, out Vector2 screenPos);
                    if (!onScreen) continue;

                    Vector2 pixelPos = new Vector2(screenPos.X * resX, screenPos.Y * resY);

                    ctx.AddText(new Vector2(pixelPos.X + 1, pixelPos.Y + 1),
                        Color.FromArgb(200, 0, 0, 0), rp.Name);
                    ctx.AddText(pixelPos, Color.Yellow, rp.Name);
                }
                catch { }
            }
        }

        // ── Menu ─────────────────────────────────────────────────────────────────

        private void DrawMenu()
        {
            ImGuiIV.SetNextWindowSize(new Vector2(440, 220), eImGuiCond.Once);
            ImGuiIV.SetNextWindowPos(new Vector2(100, 100), eImGuiCond.Once);
            ImGuiIV.Begin("MultiIV", ref _menuOpen, eImGuiWindowFlags.None);

            ImGuiIV.Text("MultiIV - GTA IV Multiplayer");
            ImGuiIV.Separator();
            ImGuiIV.Spacing();

            ImGuiIV.Text(_net.IsConnected
                ? $"Connected  (ID {_net.MyPlayerId})  Ping: {(_net.PingMs >= 0 ? _net.PingMs + " ms" : "...")}"
                : "Disconnected");
            ImGuiIV.Spacing();

            bool canEdit = !_net.IsConnected;
            if (!canEdit) ImGuiIV.BeginDisabled();

            ImGuiIV.Text("Your Name:");
            ImGuiIV.Text(m_offlinename);
            ImGuiIV.Spacing();
            ImGuiIV.Text("Server Address:");
            ImGuiIV.Text(m_server + ":" + m_port);
            ImGuiIV.Spacing();

            if (!canEdit) ImGuiIV.EndDisabled();

            if (ImGuiIV.Button("Disconnect", new Vector2(200, 30)))
            {
                _net.Disconnect(); // FullLocalCleanup runs via OnDisconnected
                _discord.StopPresence();
                Environment.Exit(0);

            }

            ImGuiIV.End();
        }

        // ── Player list ───────────────────────────────────────────────────────────

        private void DrawPlayerList()
        {
            if (!_net.IsConnected) return;

            ImGuiIV.SetNextWindowSize(new Vector2(240, 200), eImGuiCond.Once);
            ImGuiIV.SetNextWindowPos(new Vector2(560, 100), eImGuiCond.Once);
            bool dummy = true;
            ImGuiIV.Begin("Players", ref dummy,
                eImGuiWindowFlags.NoResize | eImGuiWindowFlags.NoCollapse);

            ImGuiIV.TextColored(Color.LightGreen,
                $"[{_net.MyPlayerId}] {m_offlinename} (you){(_inVehicle ? "  [in vehicle]" : "")}");
            ImGuiIV.Separator();

            if (_peds.Players.Count == 0)
            {
                ImGuiIV.TextDisabled("No other players online.");
            }
            else
            {
                foreach (var kvp in _peds.Players)
                {
                    RemotePlayer rp = kvp.Value;
                    bool inVeh = _vehicles.Vehicles.ContainsKey(kvp.Key);
                    ImGuiIV.Text($"[{rp.Id}] {rp.Name}{(inVeh ? "  [in vehicle]" : "")}");
                }
            }

            ImGuiIV.Spacing();
            ImGuiIV.Separator();
            int total = _peds.Players.Count + 1;
            ImGuiIV.TextDisabled($"{total} player{(total == 1 ? "" : "s")} online");

            ImGuiIV.End();
        }

        // ── Chat input ────────────────────────────────────────────────────────────
        private void DrawChatInput()
        {
            GET_SCREEN_RESOLUTION(out int resX, out int resY);

            ImGuiIV.SetNextWindowPos(new Vector2(20, resY - 48f), eImGuiCond.Always);
            ImGuiIV.SetNextWindowSize(new Vector2(480f, 40f), eImGuiCond.Always);
            ImGuiIV.Begin("##chatinput",
                eImGuiWindowFlags.NoTitleBar |
                eImGuiWindowFlags.NoResize |
                eImGuiWindowFlags.NoMove |
                eImGuiWindowFlags.NoScrollbar);

            ImGuiIV.SetKeyboardFocusHere();
            ImGuiIV.InputText("##chatdraft", ref _chatDraft);

            ImGuiIV.End();
        }



        // ── Shutdown ──────────────────────────────────────────────────────────────

        private void OnUninitialize(object sender, EventArgs e)
        {
            if (_net.IsConnected)
                _net.Disconnect();
            _discord.StopPresence();

        }
    }
}