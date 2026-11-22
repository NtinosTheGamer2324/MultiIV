using IVSDKDotNet;
using IVSDKDotNet.Enums;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text;
using static IVSDKDotNet.Native.Natives;
using System.Threading.Tasks;

namespace MultiIV
{
    public class UI
    {
        private bool g_ShowDisconnected = false;

        // 0-255. Higher = darker background behind the Disconnected window.
        private const int DimAlpha = 160;

        public void ShowDisconnectedUI()
        {
            SET_TIME_SCALE(0.0f); // freeze the world while the disconnected screen is up
            g_ShowDisconnected = true;
        }

        public void HideDisconnectedUI()
        {
            SET_TIME_SCALE(1.0f); // un-freeze
            g_ShowDisconnected = false;
        }

        public void RenderUIDisconnected(DiscordRPC discordRpc, ImGuiIV_DrawingContext ctx)
        {
            if (!g_ShowDisconnected)
                return;

            GET_SCREEN_RESOLUTION(out int resX, out int resY);

            // Dim the whole screen behind the window
            ctx.AddRectFilled(
                new Vector2(0, 0),
                new Vector2(resX, resY),
                Color.FromArgb(DimAlpha, 0, 0, 0),
                0f,
                eImDrawFlags.None);

            // Centre the window on screen
            Vector2 size = new Vector2(440, 220);
            ImGuiIV.SetNextWindowSize(size, eImGuiCond.Once);
            ImGuiIV.SetNextWindowPos(
                new Vector2((resX - size.X) * 0.5f, (resY - size.Y) * 0.5f),
                eImGuiCond.Once);

            // Unique ImGui ID suffix avoids title collision with the main menu
            bool open = true;
            ImGuiIV.Begin("Disconnected##DisconnectedWindow", ref open, eImGuiWindowFlags.None);

            ImGuiIV.Text("You have been disconnected from this session.");

            if (ImGuiIV.Button("Quit"))
            {
                SET_TIME_SCALE(1.0f);
                discordRpc?.StopPresence();
                Environment.Exit(0);
            }

            ImGuiIV.End();
        }
    }
}