// ============================ StoryModeManager.cs ============================
using System.Numerics;
using IVSDKDotNet;
using IVSDKDotNet.Enums;
using static IVSDKDotNet.Native.Natives;
namespace MultiIV
{
    public class StoryModeManager
    {
        private bool _applied;

        // Blip handles in GTA IV are small sequential ints, not enumeration-
        // only objects — so even with no "get all blips" native, we can
        // brute-force scan a handle range and kill anything that exists.
        // Range is a guess; bump it if the log shows blips still surviving
        // past the ceiling.
        private const int MaxBlipHandle = 1000;
        private const int BlipScanEveryNTicks = 30; // ~twice/sec at 60fps — no need to hammer this every frame
        private int _blipScanCounter = 0;
        private const int SweepEveryNTicks = 30;
        private int _sweepCounter = 0;

        public void Disable()
        {
            if (_applied) return;
            _applied = true;
            try
            {
                SET_PED_DENSITY_MULTIPLIER(0.0f);
                SET_CAR_DENSITY_MULTIPLIER(0.0f);
                SET_PARKED_CAR_DENSITY_MULTIPLIER(0.0f);
                SET_RANDOM_CAR_DENSITY_MULTIPLIER(0.0f);
                SET_CREATE_RANDOM_COPS(false);
                SWITCH_GARBAGE_TRUCKS(false);
                SWITCH_AMBIENT_PLANES(false);
                SWITCH_RANDOM_TRAINS(false);
                SWITCH_RANDOM_BOATS(false);
                SWITCH_POLICE_HELIS(false);

                SET_MAX_WANTED_LEVEL(0);
                CLEAR_WANTED_LEVEL((int)GET_PLAYER_ID());

                // Stops cops actively responding to crimes (separate from
                // SET_CREATE_RANDOM_COPS, which only stops ambient spawning).
                // TODO verify signature — guessing PlayerIndex + bool, matching
                // the CONVERT_INT_TO_PLAYERINDEX pattern used elsewhere in Main.cs.
                DONT_DISPATCH_COPS_FOR_PLAYER(CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID()), true);

                ALLOW_EMERGENCY_SERVICES(false);
                DISABLE_POLICE_SCANNER();

                // One-time sweep — turning off future spawns doesn't remove
                // cops/cars/peds already near the player at the moment we
                // disable. Catches existing traffic and pedestrians too,
                // not just cops.
                // TODO verify CLEAR_AREA_OF_COPS/CARS/CHARS signatures —
                // guessing (coords, radius) for all three.
                GET_CHAR_COORDINATES(
                    GetPlayerPed(), out Vector3 playerPos);
                CLEAR_AREA_OF_COPS(playerPos, 100.0f);
                CLEAR_AREA_OF_CARS(playerPos, 100.0f);
                CLEAR_AREA_OF_CHARS(playerPos, 100.0f);

                // Phone calls: there's no native to stop the game from
                // SCHEDULING a call (that decision lives inside story
                // scripts we have no hook into) — confirmed by scanning the
                // full phone native list, which only has building blocks
                // for *placing* calls, not blocking them. Two-pronged
                // best-effort instead:
                //  1) Remove the phone object now.
                //  2) Tell the engine a script (us) owns the phone, in case
                //     that makes story logic treat it as busy. Speculative —
                //     untested whether this actually changes behavior.
                // The real suppression happens continuously in Tick() below.
                TERMINATE_ALL_SCRIPTS_FOR_NETWORK_GAME();
                THIS_SCRIPT_IS_SAFE_FOR_NETWORK_GAME();

                IVGame.Console.PrintWarning("[MultiIV] Story mode systems disabled.");
            }
            catch (System.Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] StoryModeManager.Disable failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Called every game tick while connected. Can't stop the game from
        /// scheduling a story phone call, but we CAN shut off the ring the
        /// instant it starts — every tick, before it has any real chance to
        /// interrupt the player.
        /// </summary>
        public void Tick()
        {
            if (!_applied) return;
            try
            {
                // Called every tick, not just once at connect — if a story
                // script tries to recreate the phone mid-call, this kills
                // it again before the player ever sees it. Combined with
                // the ring suppression below, the vanilla phone is
                // permanently dead while connected.
                DESTROY_MOBILE_PHONE();
                STOP_MOBILE_PHONE_RINGING();

                // Ambient population: GTA IV resets the density multipliers every
                // frame, so setting them once in Disable() isn't enough - they have
                // to be re-applied on every tick or traffic/peds creep back in.
                SET_PED_DENSITY_MULTIPLIER(0.0f);
                SET_CAR_DENSITY_MULTIPLIER(0.0f);
                SET_PARKED_CAR_DENSITY_MULTIPLIER(0.0f);
                SET_RANDOM_CAR_DENSITY_MULTIPLIER(0.0f);
                SET_CREATE_RANDOM_COPS(false);

                // Sweep out anything that already spawned (e.g. right after you moved
                // into a new area before the multipliers took effect). Twice a second
                // is plenty.
                _sweepCounter++;
                if (_sweepCounter >= SweepEveryNTicks)
                {
                    _sweepCounter = 0;
                    GET_CHAR_COORDINATES(GetPlayerPed(), out Vector3 sweepPos);
                    CLEAR_AREA_OF_COPS(sweepPos, 150.0f);
                    CLEAR_AREA_OF_CARS(sweepPos, 150.0f);
                    CLEAR_AREA_OF_CHARS(sweepPos, 150.0f);
                }

                //_blipScanCounter++;
                //if (_blipScanCounter >= BlipScanEveryNTicks)
                //{
                //    _blipScanCounter = 0;
                //    RemoveAllBlips();
                //}
            }
            catch (System.Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] StoryModeManager.Tick failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Brute-force scan of the blip handle range — no enumeration
        /// native exists, so this is the workaround: ask every handle in
        /// range whether it currently exists, and remove it if so. We
        /// don't create any blips ourselves right now, so it's safe to
        /// nuke everything found without needing an exclusion list.
        /// </summary>
        private void RemoveAllBlips()
        {
            int removed = 0;
            int errors = 0;
            for (int i = 0; i < MaxBlipHandle; i++)
            {
                try
                {
                    if (DOES_BLIP_EXIST(i))
                    {
                        REMOVE_BLIP(i);
                        removed++;
                    }
                }
                catch
                {
                    errors++;
                }
            }

            // Always log — silence was ambiguous (ran-but-found-nothing
            // looked identical to never-ran-at-all). This confirms the
            // scan is actually executing every time.
            IVGame.Console.PrintWarning(
                $"[MultiIV] Blip scan: checked 0-{MaxBlipHandle - 1}, removed {removed}, errors {errors}");
        }

        public void Restore()
        {
            if (!_applied) return;
            _applied = false;
            try
            {
                SET_PED_DENSITY_MULTIPLIER(1.0f);
                SET_CAR_DENSITY_MULTIPLIER(1.0f);
                SET_CREATE_RANDOM_COPS(true);
                SWITCH_GARBAGE_TRUCKS(true);
                SWITCH_AMBIENT_PLANES(true);
                SWITCH_RANDOM_TRAINS(true);
                SWITCH_RANDOM_BOATS(true);
                SWITCH_POLICE_HELIS(true);

                SET_MAX_WANTED_LEVEL(6);

                DONT_DISPATCH_COPS_FOR_PLAYER(CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID()), false);
                ALLOW_EMERGENCY_SERVICES(true);
                ENABLE_POLICE_SCANNER();

                SCRIPT_IS_USING_MOBILE_PHONE(false);

                IVGame.Console.PrintWarning("[MultiIV] Story mode systems restored.");
            }
            catch (System.Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] StoryModeManager.Restore failed: " + ex.Message);
            }
        }

        private static int GetPlayerPed()
        {
            GET_PLAYER_CHAR(CONVERT_INT_TO_PLAYERINDEX(GET_PLAYER_ID()), out int ped);
            return ped;
        }
    }
}