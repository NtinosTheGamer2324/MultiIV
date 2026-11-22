// ============================ PedManager.cs ============================
using System;
using System.Collections.Generic;
using System.Numerics;
using IVSDKDotNet;
using IVSDKDotNet.Enums;
using static IVSDKDotNet.Native.Natives;

namespace MultiIV
{
    /// <summary>
    /// Owns the lifecycle of all remote players.
    /// Spawns native GTA IV PLAYERS (CREATE_PLAYER) instead of NPC peds,
    /// matching original XLive / GFWL multiplayer behavior.
    /// Must only be called from the main game thread.
    /// </summary>
    public class PedManager
    {
        public IReadOnlyDictionary<int, RemotePlayer> Players => _players;

        private readonly Dictionary<int, RemotePlayer> _players = new Dictionary<int, RemotePlayer>();
        private readonly HashSet<int> _allocatedSlots = new HashSet<int>();

        private static readonly uint ModelHash = RAGE.AtStringHash("M_Y_GTRI_LO_02");
        private bool _modelRequested;
        private bool _modelLoadAttempted;

        private const float LerpSpeed = 0.2f;

        public void RequestModel()
        {
            if (_modelRequested) return;
            try
            {
                IVStreaming.ScriptRequestModel((int)ModelHash);
                _modelRequested = true;
                IVGame.Console.PrintWarning("[MultiIV] Requested remote player model.");
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError("[MultiIV] RequestModel failed: " + ex.Message);
            }
        }

        private bool EnsureModelLoaded()
        {
            if (HAS_MODEL_LOADED((int)ModelHash))
                return true;

            if (!_modelLoadAttempted)
            {
                _modelLoadAttempted = true;
                IVStreaming.LoadAllRequestedModels(false);
            }

            return HAS_MODEL_LOADED((int)ModelHash);
        }

        private int AllocateSlot(int preferredSlot)
        {
            // GTA IV supports up to 32 players (slots 0..31).
            // Slot 0 is reserved for the local player.
            // Remote players must occupy slots 1..31.
            if (preferredSlot >= 1 && preferredSlot <= 31 && !_allocatedSlots.Contains(preferredSlot))
            {
                _allocatedSlots.Add(preferredSlot);
                return preferredSlot;
            }

            for (int i = 1; i <= 31; i++)
            {
                if (!_allocatedSlots.Contains(i))
                {
                    _allocatedSlots.Add(i);
                    return i;
                }
            }

            return -1;
        }

        private void FreeSlot(int slot)
        {
            _allocatedSlots.Remove(slot);
        }

        public void UpdateOrSpawn(int playerId, string name, float x, float y, float z, float heading, float speed = 0f)
        {
            try
            {
                if (_players.TryGetValue(playerId, out RemotePlayer existing))
                {
                    if (existing.Name != name)
                    {
                        existing.Name = name;
                        try
                        {
                            if (existing.BlipHandle != 0)
                                CHANGE_BLIP_NAME_FROM_ASCII(existing.BlipHandle, name);
                        }
                        catch { }
                    }

                    existing.TargetPosition = new Vector3(x, y, z);
                    existing.TargetHeading = heading;
                    existing.Speed = speed;
                    existing.Buffer.Push(new Vector3(x, y, z), heading);

                    // If the player was previously in a vehicle and is now on-foot,
                    // warp them out of the vehicle so they don't remain stuck inside.
                    if (existing.InVehicle)
                    {
                        existing.InVehicle = false;
                        existing.Buffer.Reset(new Vector3(x, y, z), heading);
                        if (existing.PedHandle != 0 && IS_CHAR_IN_ANY_CAR(existing.PedHandle))
                        {
                            WARP_CHAR_FROM_CAR_TO_COORD(existing.PedHandle, x, y, z);
                        }
                    }

                    return;
                }

                // Make sure we requested the custom model
                RequestModel();

                int slot = AllocateSlot(playerId);
                if (slot == -1)
                {
                    IVGame.Console.PrintError($"[MultiIV] Cannot spawn player '{name}' (ID {playerId}): all 31 player slots are full!");
                    return;
                }

                // CREATE_PLAYER natively creates a player entity with CPlayerInfo & CPlayerPed
                CREATE_PLAYER((uint)slot, x, y, z, out int playerIndex);
                GET_PLAYER_CHAR(playerIndex, out int handle);

                if (handle == 0)
                {
                    IVGame.Console.PrintError($"[MultiIV] CREATE_PLAYER failed to create character for '{name}' (slot {slot})");
                    FreeSlot(slot);
                    return;
                }

                // Configure the remote player
                SET_PLAYER_CONTROL(playerIndex, false);
                SET_CHAR_AS_MISSION_CHAR(handle);
                SET_CHAR_HEADING(handle, heading);
                SET_PLAYER_NEVER_GETS_TIRED(playerIndex, true);
                SET_CHAR_CAN_BE_SHOT_IN_VEHICLE(handle, true);
                SET_PED_HEEDS_THE_EVERYONE_IGNORE_PLAYER_FLAG(handle, false);

                bool modelApplied = false;
                if (HAS_MODEL_LOADED((int)ModelHash))
                {
                    try
                    {
                        CHANGE_PLAYER_MODEL(playerIndex, (uint)ModelHash);
                        GET_PLAYER_CHAR(playerIndex, out handle);
                        SET_PLAYER_CONTROL(playerIndex, false);
                        SET_CHAR_AS_MISSION_CHAR(handle);
                        SET_CHAR_HEADING(handle, heading);
                        modelApplied = true;
                    }
                    catch (Exception ex)
                    {
                        IVGame.Console.PrintError("[MultiIV] CHANGE_PLAYER_MODEL failed: " + ex.Message);
                    }
                }

                int blipHandle = 0;
                try
                {
                    ADD_BLIP_FOR_CHAR(handle, out blipHandle);
                    CHANGE_BLIP_COLOUR(blipHandle, 3); // Blue for other players
                    CHANGE_BLIP_NAME_FROM_ASCII(blipHandle, name);
                }
                catch (Exception ex)
                {
                    IVGame.Console.PrintError($"[MultiIV] ADD_BLIP_FOR_CHAR failed for player {playerId}: " + ex.Message);
                }

                var spawned = new RemotePlayer
                {
                    Id = playerId,
                    PlayerSlot = slot,
                    PlayerIndex = playerIndex,
                    Name = name,
                    PedHandle = handle,
                    BlipHandle = blipHandle,
                    TargetPosition = new Vector3(x, y, z),
                    TargetHeading = heading,
                    CurrentPosition = new Vector3(x, y, z),
                    CurrentHeading = heading,
                    Speed = speed,
                    IsMoving = false,
                    InVehicle = false,
                    ModelApplied = modelApplied
                };
                spawned.Buffer.Reset(new Vector3(x, y, z), heading);
                _players[playerId] = spawned;

                IVGame.Console.PrintWarning($"[MultiIV] Spawned PLAYER for '{name}' (ID {playerId}, Slot {slot}, Ped {handle})");
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError($"[MultiIV] UpdateOrSpawn failed for player {playerId}: " + ex.Message);
            }
        }

        public void Tick()
        {
            foreach (var kvp in _players)
            {
                RemotePlayer rp = kvp.Value;
                if (rp.PedHandle == 0) continue;

                try
                {
                    // Apply model once loaded if not already applied
                    if (!rp.ModelApplied && HAS_MODEL_LOADED((int)ModelHash))
                    {
                        try
                        {
                            CHANGE_PLAYER_MODEL(rp.PlayerIndex, (uint)ModelHash);
                            GET_PLAYER_CHAR(rp.PlayerIndex, out rp.PedHandle);
                            SET_PLAYER_CONTROL(rp.PlayerIndex, false);
                            SET_CHAR_AS_MISSION_CHAR(rp.PedHandle);
                            rp.ModelApplied = true;
                            IVGame.Console.PrintWarning($"[MultiIV] Applied model to player '{rp.Name}'");
                        }
                        catch { }
                    }

                    // If seated in a vehicle, the vehicle handles the position; do not override with on-foot coords
                    if (rp.InVehicle || IS_CHAR_IN_ANY_CAR(rp.PedHandle))
                    {
                        continue;
                    }

                    // Snapshot interpolation: show the player slightly in the past, blended
                    // between the two surrounding network snapshots (frame-rate independent).
                    if (rp.Buffer.Sample(out Vector3 smoothPos, out float smoothHeading, out Vector3 aheadPos))
                    {
                        rp.CurrentPosition = smoothPos;
                        rp.CurrentHeading = smoothHeading;
                        rp.TargetPosition = aheadPos;   // walk animation heads toward the next snapshot
                    }

                    // Crucial: Use NO_OFFSET so GTA IV does not apply its default +1.0m to +1.5m teleport offset
                    // which was causing other players to hover/float ~5 units in the air.
                    SET_CHAR_COORDINATES_DONT_WARP_GANG_NO_OFFSET(
                        rp.PedHandle,
                        rp.CurrentPosition.X,
                        rp.CurrentPosition.Y,
                        rp.CurrentPosition.Z
                    );
                    SET_CHAR_HEADING(rp.PedHandle, rp.CurrentHeading);

                    // Movement animations
                    float distToTarget = Vector3.Distance(rp.CurrentPosition, rp.TargetPosition);
                    bool shouldMove = distToTarget > 0.08f || rp.Speed > 0.2f;

                    if (shouldMove)
                    {
                        uint speedMode = 1; // Walk
                        if (rp.Speed > 5.5f || distToTarget > 1.5f)
                            speedMode = 3; // Sprint
                        else if (rp.Speed > 2.5f || distToTarget > 0.5f)
                            speedMode = 2; // Run

                        _TASK_GO_STRAIGHT_TO_COORD(
                            rp.PedHandle,
                            rp.TargetPosition.X,
                            rp.TargetPosition.Y,
                            rp.TargetPosition.Z,
                            speedMode,
                            500
                        );
                        rp.IsMoving = true;
                    }
                    else if (rp.IsMoving)
                    {
                        CLEAR_CHAR_TASKS(rp.PedHandle);
                        rp.IsMoving = false;
                    }
                }
                catch { /* ped may have been deleted by the engine */ }
            }
        }

        public void PlayWeaponFire(int playerId, uint weaponHash, float x, float y, float z, float heading)
        {
            if (!_players.TryGetValue(playerId, out RemotePlayer rp)) return;
            if (rp.PedHandle == 0) return;

            try
            {
                float rad = heading * ((float)Math.PI / 180f);
                float dirX = (float)-Math.Sin(rad);
                float dirY = (float)Math.Cos(rad);

                int targetX = (int)(x + dirX * 50f);
                int targetY = (int)(y + dirY * 50f);
                int targetZ = (int)z;

                if (weaponHash != 0)
                {
                    GIVE_WEAPON_TO_CHAR(rp.PedHandle, (int)weaponHash, 50, false);
                    SET_CURRENT_CHAR_WEAPON(rp.PedHandle, (int)weaponHash, true);
                }

                _TASK_SHOOT_AT_COORD(rp.PedHandle, targetX, targetY, targetZ, 400, 0);
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError($"[MultiIV] PlayWeaponFire failed for player {playerId}: " + ex.Message);
            }
        }

        public void RemovePlayer(int playerId)
        {
            if (!_players.TryGetValue(playerId, out RemotePlayer rp))
                return;

            try
            {
                if (rp.PedHandle != 0)
                {
                    if (IS_CHAR_IN_ANY_CAR(rp.PedHandle))
                    {
                        WARP_CHAR_FROM_CAR_TO_COORD(rp.PedHandle, 0f, 0f, 0f);
                    }
                    DELETE_CHAR(ref rp.PedHandle);
                }

                if (rp.BlipHandle != 0)
                    REMOVE_BLIP(rp.BlipHandle);
            }
            catch { }

            FreeSlot(rp.PlayerSlot);
            _players.Remove(playerId);
            IVGame.Console.PrintWarning($"[MultiIV] Removed player '{rp.Name}' (ID {playerId}, Slot {rp.PlayerSlot})");
        }

        public void RemoveAll()
        {
            foreach (var kvp in _players)
            {
                RemotePlayer rp = kvp.Value;
                try
                {
                    if (rp.PedHandle != 0)
                    {
                        if (IS_CHAR_IN_ANY_CAR(rp.PedHandle))
                        {
                            WARP_CHAR_FROM_CAR_TO_COORD(rp.PedHandle, 0f, 0f, 0f);
                        }
                        DELETE_CHAR(ref rp.PedHandle);
                    }

                    if (rp.BlipHandle != 0)
                        REMOVE_BLIP(rp.BlipHandle);
                }
                catch { }
            }
            _players.Clear();
            _allocatedSlots.Clear();
        }

        private static float LerpAngle(float current, float target, float t)
        {
            float diff = target - current;
            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;
            return current + diff * t;
        }
    }
}