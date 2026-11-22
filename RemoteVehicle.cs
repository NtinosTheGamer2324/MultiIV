// ============================ RemoteVehicle.cs ============================
using System.Numerics;

namespace MultiIV
{
    /// <summary>
    /// State for a single remotely-owned vehicle.
    /// Stage 3 will promote this to a full NetworkEntity.
    /// </summary>
    public class RemoteVehicle
    {
        public int OwnerId;          // which player drives this
        public int VehicleHandle;    // local game handle
        public uint ModelHash;
        // Interpolation targets (latest from network)
        public Vector3 TargetPosition;
        public float TargetHeading;
        // Smoothed values we actually apply each tick
        public Vector3 CurrentPosition;
        public float CurrentHeading;
        public SnapshotBuffer Buffer = new SnapshotBuffer();
        public float Health;
        // Counts down once this vehicle's owner has gone on-foot/disconnected.
        // We delay actually releasing the vehicle until this hits zero,
        // giving the engine time to finish detaching the ped from the seat
        // naturally (e.g. after a forced ejection) WITHOUT us querying the
        // ped during that unstable window. 0 = not pending removal.
        public int RemovalTicksRemaining;
    }
}