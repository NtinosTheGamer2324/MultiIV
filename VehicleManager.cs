// ============================ VehicleManager.cs ============================
using System;
using System.Collections.Generic;
using System.Numerics;
using IVSDKDotNet;
using IVSDKDotNet.Enums;
using static IVSDKDotNet.Native.Natives;

namespace MultiIV
{
    /// <summary>
    /// Owns the lifecycle of all remote player vehicles.
    /// Must only be called from the main game thread.
    /// </summary>
    public class VehicleManager
    {
        public IReadOnlyDictionary<int, RemoteVehicle> Vehicles => _vehicles;

        private readonly Dictionary<int, RemoteVehicle> _vehicles
            = new Dictionary<int, RemoteVehicle>();

        private readonly Dictionary<int, RemoteVehicle> _pendingRemoval
            = new Dictionary<int, RemoteVehicle>();

        private const int RemovalDelayTicks = 60; // roughly ~1 second

        private const float LerpSpeed = 0.2f;
        private const float HeadingLerpSpeed = 0.15f;

        private readonly HashSet<uint> _requestedModels = new HashSet<uint>();
        private readonly HashSet<uint> _loadAttempted = new HashSet<uint>();

        private readonly List<int> _justDestroyed = new List<int>();

        private bool EnsureModelLoaded(uint modelHash)
        {
            if (HAS_MODEL_LOADED((int)modelHash))
                return true;

            if (_requestedModels.Add(modelHash))
                IVStreaming.ScriptRequestModel((int)modelHash);

            if (_loadAttempted.Add(modelHash))
                IVStreaming.LoadAllRequestedModels(false);

            return HAS_MODEL_LOADED((int)modelHash);
        }

        public void UpdateOrSpawn(
            int playerId, uint modelHash,
            float x, float y, float z, float heading,
            float health, int pedHandle)
        {
            try
            {
                if (_vehicles.TryGetValue(playerId, out RemoteVehicle existing))
                {
                    existing.TargetPosition = new Vector3(x, y, z);
                    existing.TargetHeading = heading;
                    existing.Health = health;
                    existing.Buffer.Push(new Vector3(x, y, z), heading);

                    if (pedHandle != 0 && existing.VehicleHandle != 0)
                        EnsurePedSeated(pedHandle, existing.VehicleHandle);

                    return;
                }

                if (_pendingRemoval.TryGetValue(playerId, out RemoteVehicle stalePending))
                {
                    _pendingRemoval.Remove(playerId);
                    ReleaseVehicle(stalePending);
                }

                if (!EnsureModelLoaded(modelHash))
                    return;

                CREATE_CAR((int)modelHash, x, y, z, out int vehHandle, true);
                if (vehHandle == 0) return;

                SET_CAR_AS_MISSION_CAR(vehHandle);
                SET_CAR_HEADING(vehHandle, heading);

                if (pedHandle != 0)
                    EnsurePedSeated(pedHandle, vehHandle);

                var spawnedVeh = new RemoteVehicle
                {
                    OwnerId = playerId,
                    VehicleHandle = vehHandle,
                    ModelHash = modelHash,
                    TargetPosition = new Vector3(x, y, z),
                    TargetHeading = heading,
                    CurrentPosition = new Vector3(x, y, z),
                    CurrentHeading = heading,
                    Health = health
                };
                spawnedVeh.Buffer.Reset(new Vector3(x, y, z), heading);
                _vehicles[playerId] = spawnedVeh;

                IVGame.Console.PrintWarning(
                    $"[MultiIV] Spawned vehicle 0x{modelHash:X} for player {playerId}");
            }
            catch (Exception ex)
            {
                IVGame.Console.PrintError(
                    $"[MultiIV] VehicleManager.UpdateOrSpawn failed (player {playerId}): {ex.Message}");
            }
        }

        public void RemoveForPlayer(int playerId)
        {
            if (!_vehicles.TryGetValue(playerId, out RemoteVehicle rv)) return;

            _vehicles.Remove(playerId);

            rv.RemovalTicksRemaining = RemovalDelayTicks;
            _pendingRemoval[playerId] = rv;
        }

        public void RemoveForPlayerImmediate(int playerId)
        {
            if (_vehicles.TryGetValue(playerId, out RemoteVehicle rv))
            {
                _vehicles.Remove(playerId);
                ReleaseVehicle(rv);
            }

            if (_pendingRemoval.TryGetValue(playerId, out RemoteVehicle pendingRv))
            {
                _pendingRemoval.Remove(playerId);
                ReleaseVehicle(pendingRv);
            }
        }

        public void RemoveAll()
        {
            foreach (var kvp in _vehicles)
                ReleaseVehicle(kvp.Value);
            _vehicles.Clear();

            foreach (var kvp in _pendingRemoval)
                ReleaseVehicle(kvp.Value);
            _pendingRemoval.Clear();
        }

        public void Tick()
        {
            if (_pendingRemoval.Count > 0)
            {
                var resolved = new List<int>();

                foreach (var kvp in _pendingRemoval)
                {
                    RemoteVehicle rv = kvp.Value;
                    rv.RemovalTicksRemaining--;

                    if (rv.RemovalTicksRemaining <= 0)
                    {
                        ReleaseVehicle(rv);
                        resolved.Add(kvp.Key);
                    }
                }

                foreach (int id in resolved)
                    _pendingRemoval.Remove(id);
            }

            foreach (var kvp in _vehicles)
            {
                RemoteVehicle rv = kvp.Value;
                if (rv.VehicleHandle == 0) continue;

                try
                {
                    GET_CAR_HEALTH(rv.VehicleHandle, out uint actualHealth);
                    if (actualHealth == 0)
                    {
                        rv.RemovalTicksRemaining = RemovalDelayTicks;
                        _pendingRemoval[kvp.Key] = rv;
                        _justDestroyed.Add(kvp.Key);
                        IVGame.Console.PrintWarning(
                            $"[MultiIV] Vehicle for player {kvp.Key} destroyed locally — releasing");
                        continue;
                    }

                    // Snapshot interpolation (see SnapshotBuffer): frame-rate independent.
                    if (rv.Buffer.Sample(out Vector3 smoothPos, out float smoothHeading, out Vector3 _))
                    {
                        rv.CurrentPosition = smoothPos;
                        rv.CurrentHeading = smoothHeading;
                    }

                    SET_CAR_COORDINATES(rv.VehicleHandle, rv.CurrentPosition);
                    SET_CAR_HEADING(rv.VehicleHandle, rv.CurrentHeading);
                    SET_CAR_HEALTH(rv.VehicleHandle, (uint)rv.Health);
                }
                catch { }
            }

            foreach (int id in _justDestroyed)
                _vehicles.Remove(id);
            _justDestroyed.Clear();
        }

        private static void ReleaseVehicle(RemoteVehicle rv)
        {
            try
            {
                if (rv.VehicleHandle != 0)
                    MARK_CAR_AS_NO_LONGER_NEEDED(rv.VehicleHandle);
            }
            catch { }
        }

        private static void EnsurePedSeated(int pedHandle, int vehHandle)
        {
            try
            {
                GET_CAR_CHAR_IS_USING(pedHandle, out int currentVeh);
                if (currentVeh != vehHandle)
                    WARP_CHAR_INTO_CAR(pedHandle, vehHandle);
            }
            catch { }
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