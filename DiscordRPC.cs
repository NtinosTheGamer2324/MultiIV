using System;
// Alias the external NuGet namespace to prevent naming conflicts with this class
using DRPC = DiscordRPC;

namespace MultiIV
{
    public class DiscordRPC
    {
        private DRPC.DiscordRpcClient _client;
        private readonly string _clientId;

        public DiscordRPC(string clientId)
        {
            _clientId = clientId;
        }

        public void Initialize()
        {
            try
            {
                _client = new DRPC.DiscordRpcClient(_clientId);
                _client.Initialize();
            }
            catch (Exception)
            {
                // Fails safely if the Discord desktop app is not running
            }
        }

        public void ShowPresence(string content, string state)
        {
            if (_client == null || !_client.IsInitialized)
                return;

            _client.SetPresence(new DRPC.RichPresence
            {
                Details = content,
                State = state,
                Timestamps = DRPC.Timestamps.Now
            });

            // Processes internal queue / keeps the pipe responsive
            _client.Invoke();
        }

        public void StopPresence()
        {
            if (_client != null)
            {
                _client.ClearPresence();
                _client.Dispose();
                _client = null;
            }
        }
    }
}