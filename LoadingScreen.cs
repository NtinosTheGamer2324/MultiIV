// ============================ LoadingScreen.cs ============================
using System;
using System.Numerics;
using IVSDKDotNet;
using static IVSDKDotNet.Native.Natives;
namespace MultiIV
{
    /// <summary>
    /// While connecting, parks a script camera above the cloud layer.
    /// No HUD, no overlay — just the sky.
    /// </summary>
    public class LoadingScreen
    {
        public bool Visible { get; private set; }
        private int _camHandle = 0;
        private const float CloudHeight = 1000.0f; // TODO confirm against real sky/cloud altitude

        public void Show(string status)
        {
            if (Visible) return;
            Visible = true;
            try
            {
                GET_PLAYER_CHAR(CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID()), out int ped);
                GET_CHAR_COORDINATES(ped, out Vector3 playerPos);

                Vector3 camPos = new Vector3(playerPos.X, playerPos.Y, CloudHeight);

                // 14 is the standard "default scripted camera" type used
                // throughout GTA IV's own scripts for free-floating cams.
                const uint DefaultScriptedCamType = 14;
                CREATE_CAM(DefaultScriptedCamType, out _camHandle);
                SET_CAM_POS(_camHandle, camPos.X, camPos.Y, camPos.Z);

                // Look straight down at where the player will spawn in.
                // TODO verify POINT_CAM_AT_COORD signature/existence — inferred
                // from UNPOINT_CAM existing for GTA IV, but unconfirmed directly.
                POINT_CAM_AT_COORD(_camHandle, playerPos.X, playerPos.Y, playerPos.Z);

                SET_CAM_ACTIVE(_camHandle, true);

                // No RENDER_SCRIPT_CAMS in GTA IV — ACTIVATE_SCRIPTED_CAMS(bool, bool)
                // is what actually switches the view to script cams. Second
                // param's exact meaning is unconfirmed; false matches the only
                // decompiled call site we've seen so far.
                ACTIVATE_SCRIPTED_CAMS(true, false);
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] LoadingScreen.Show failed: " + ex.Message);
            }
        }

        public void Hide()
        {
            if (!Visible) return;
            Visible = false;
            try
            {
                if (_camHandle != 0)
                {
                    ACTIVATE_SCRIPTED_CAMS(false, false);
                    DESTROY_CAM(_camHandle);
                    _camHandle = 0;
                }
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] LoadingScreen.Hide failed: " + ex.Message);
            }
        }

        public void Tick() { /* camera is static, nothing to update per-frame */ }
    }
}