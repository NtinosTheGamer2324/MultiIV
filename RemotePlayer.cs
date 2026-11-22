// ============================ RemotePlayer.cs ============================
using System.Numerics;

namespace MultiIV
{
    /// <summary>
    /// Holds all state for a single remote player visible to us.
    /// </summary>
    public class RemotePlayer
    {
        public int Id;
        public int PlayerSlot;      // GTA IV player slot (1..31)
        public int PlayerIndex;     // Player handle returned by CREATE_PLAYER
        public string Name;
        public int PedHandle;
        public int BlipHandle;

        // Latest position received from the server (snap target)
        public Vector3 TargetPosition;
        public float TargetHeading;

        // Smoothed position we actually move the ped to each tick (lerped toward Target)
        public Vector3 CurrentPosition;
        public float CurrentHeading;

        // Timestamped history used to interpolate smoothly (see SnapshotBuffer)
        public SnapshotBuffer Buffer = new SnapshotBuffer();

        public float Speed;
        public bool IsMoving;
        public bool InVehicle;
        public bool ModelApplied;
    }
}