// ============================ MISSION.cs ============================
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
using IVSDKDotNet.Native;

namespace MultiIV
{
    internal class Mission
    {

        private int _LocalCharacter = CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID());

        public void startMIVFreeroam()
        {
            SET_MISSION_FLAG(true);
            // FREEROAM MULTI IV ONCE.
            
        }

        public void Tick()
        {
            CLEAR_WANTED_LEVEL(_LocalCharacter);
        }
    }
}
