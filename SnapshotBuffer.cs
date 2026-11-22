// ============================ SnapshotBuffer.cs ============================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace MultiIV
{
    /// <summary>
    /// Snapshot interpolation for one remote entity (player or vehicle).
    ///
    /// Instead of chasing the latest network position with a per-frame lerp
    /// (which depends on frame rate and stutters when packets arrive unevenly),
    /// we keep a short history of timestamped snapshots and render the entity
    /// slightly in the past, blending between the two snapshots that surround
    /// "now - Delay". Movement is then smooth and independent of frame rate.
    ///
    /// Delay adapts to how often distinct updates actually arrive.
    /// </summary>
    public class SnapshotBuffer
    {
        private struct Snap
        {
            public double T;
            public Vector3 Pos;
            public float Heading;
        }

        private const int MaxSnaps = 32;
        private const double MinDelay = 0.10;          // seconds
        private const double MaxDelay = 0.30;
        private const float TeleportDistance = 25f;    // bigger jump than this = don't glide, snap
        private const double StationaryGap = 0.25;     // silence longer than this = entity was standing still

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double Now { get { return Clock.Elapsed.TotalSeconds; } }

        private readonly List<Snap> _snaps = new List<Snap>(MaxSnaps + 1);
        private double _interval = 0.10;               // smoothed time between distinct updates

        public int Count { get { return _snaps.Count; } }

        private double Delay
        {
            get { return Math.Min(MaxDelay, Math.Max(MinDelay, _interval * 1.5 + 0.03)); }
        }

        /// <summary>Add a snapshot received just now. Repeats of the same state are ignored.</summary>
        public void Push(Vector3 pos, float heading)
        {
            double now = Now;

            if (_snaps.Count > 0)
            {
                Snap last = _snaps[_snaps.Count - 1];

                // The server re-broadcasts the last known state every tick even if the
                // client sent nothing new; those repeats carry no information.
                if (Vector3.DistanceSquared(last.Pos, pos) < 1e-6f &&
                    Math.Abs(AngleDiff(last.Heading, heading)) < 0.01f)
                    return;

                if (Vector3.Distance(last.Pos, pos) > TeleportDistance)
                {
                    _snaps.Clear();                    // teleport / respawn: snap, don't glide
                }
                else
                {
                    double dt = now - last.T;
                    if (dt > StationaryGap)
                    {
                        // We were standing still. Without this "hold" point we would
                        // glide from the old spot starting at the OLD timestamp.
                        Snap hold = last;
                        hold.T = now - 0.05;
                        if (hold.T > last.T) _snaps.Add(hold);
                    }
                    else
                    {
                        _interval = 0.8 * _interval + 0.2 * dt;
                    }
                }
            }

            _snaps.Add(new Snap { T = now, Pos = pos, Heading = heading });
            if (_snaps.Count > MaxSnaps)
                _snaps.RemoveRange(0, _snaps.Count - MaxSnaps);
        }

        /// <summary>Forget history and start from this state (spawn, exiting a vehicle, ...).</summary>
        public void Reset(Vector3 pos, float heading)
        {
            _snaps.Clear();
            _snaps.Add(new Snap { T = Now, Pos = pos, Heading = heading });
        }

        /// <summary>
        /// Interpolated state to display this frame. 'ahead' is where the entity is heading
        /// next (the newer surrounding snapshot) - useful as a walk-to target for animations.
        /// Returns false if there is nothing to show yet.
        /// </summary>
        public bool Sample(out Vector3 pos, out float heading, out Vector3 ahead)
        {
            pos = Vector3.Zero; heading = 0f; ahead = Vector3.Zero;
            if (_snaps.Count == 0) return false;

            double t = Now - Delay;

            // Drop snapshots we have fully passed (keep one before 't' to interpolate from).
            while (_snaps.Count > 2 && _snaps[1].T <= t)
                _snaps.RemoveAt(0);

            Snap a = _snaps[0];
            if (_snaps.Count == 1 || t <= a.T)
            {
                pos = a.Pos; heading = a.Heading; ahead = a.Pos;
                return true;
            }

            Snap b = _snaps[1];
            if (t >= b.T)
            {
                // Ran out of data (packet loss / lag spike): hold the newest state
                // rather than extrapolating, which would overshoot when the entity stops.
                pos = b.Pos; heading = b.Heading; ahead = b.Pos;
                return true;
            }

            float f = (float)((t - a.T) / (b.T - a.T));
            pos = Vector3.Lerp(a.Pos, b.Pos, f);
            heading = NormalizeAngle(a.Heading + AngleDiff(a.Heading, b.Heading) * f);
            ahead = b.Pos;
            return true;
        }

        private static float AngleDiff(float from, float to)
        {
            float diff = to - from;
            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;
            return diff;
        }

        private static float NormalizeAngle(float a)
        {
            while (a >= 360f) a -= 360f;
            while (a < 0f) a += 360f;
            return a;
        }
    }
}
