using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using HarmonyLib;
using Debug = LiftoffGhostLines.ModLog;
using static LiftoffGhostLines.GhostLinesPlugin;
using static LiftoffGhostLines.Prefs;

namespace LiftoffGhostLines
{
    public partial class GhostLinePoller
    {
        private static float GetGateTimestamp(GhostLineEntry entry)
        {
            if (entry == null || entry.stateTimes == null || entry.stateTimes.Length == 0)
                return 0f;
            return entry.stateTimes[0] + entry.lapStartOffset;
        }

        private static float GetGateQueryTimestamp(GhostLineEntry entry, float replayTime)
        {
            if (entry == null || entry.stateTimes == null || entry.stateTimes.Length == 0)
                return 0f;
            float gateTimestamp = GetGateTimestamp(entry);
            return Mathf.Clamp(replayTime + gateTimestamp, gateTimestamp, entry.stateTimes[entry.stateTimes.Length - 1]);
        }

        private bool TryGetDronePosition(GhostLineEntry entry, float replayTime, out Vector3 position)
        {
            position = Vector3.zero;
            var recording = entry.GetRecording();
            if (recording == null || recording.states == null || recording.states.Count == 0)
                return false;

            if (entry.poseTimes == null || entry.posePositions == null || entry.poseTimes.Length == 0 ||
                entry.poseTimes.Length != entry.posePositions.Length)
                return false;

            float queryT = GetGateQueryTimestamp(entry, replayTime);

            if (entry.poseTimes.Length == 1)
            {
                position = entry.posePositions[0];
                return true;
            }

            position = SampleInterpolatedPosition(entry.poseTimes, entry.posePositions, queryT);
            return true;
        }

        // Pose on the mod clock (per-pilot first-gate shift); see FrameContext.cs.
        private bool TryGetDroneTransform(GhostLineEntry entry, float replayTime, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (!HasPoseSamples(entry))
                return false;
            SamplePoseAt(entry, GetGateQueryTimestamp(entry, replayTime), out pos, out rot);
            return true;
        }

        // Pose on the native replay clock (recording start = 0). Only used to
        // match entries against the native drones the game is moving.
        private bool TryGetDroneTransformUnshifted(GhostLineEntry entry, float replayTime, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (!HasPoseSamples(entry))
                return false;
            float firstT = entry.stateTimes[0];
            float lastT = entry.stateTimes[entry.stateTimes.Length - 1];
            SamplePoseAt(entry, Mathf.Clamp(replayTime + firstT, firstT, lastT), out pos, out rot);
            return true;
        }

        private static bool HasPoseSamples(GhostLineEntry entry)
        {
            return entry.stateTimes != null && entry.stateTimes.Length != 0 &&
                entry.poseTimes != null && entry.posePositions != null && entry.poseRotations != null &&
                entry.poseTimes.Length != 0 && entry.poseTimes.Length == entry.posePositions.Length &&
                entry.poseTimes.Length == entry.poseRotations.Length;
        }

        // queryT is a recording timestamp. One binary search serves both the
        // position spline and the rotation slerp.
        private static void SamplePoseAt(GhostLineEntry entry, float queryT, out Vector3 pos, out Quaternion rot)
        {
            float[] times = entry.poseTimes;
            if (times.Length == 1)
            {
                pos = entry.posePositions[0]; rot = entry.poseRotations[0];
                return;
            }
            int hi = FindFirstStateAtOrAfter(times, queryT);
            if (hi <= 0) { pos = entry.posePositions[0]; rot = entry.poseRotations[0]; return; }
            if (hi >= times.Length)
            { pos = entry.posePositions[entry.posePositions.Length - 1]; rot = entry.poseRotations[entry.poseRotations.Length - 1]; return; }
            int lo = hi - 1;
            float t0 = times[lo], t1 = times[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, queryT);
            pos = SampleInterpolatedPositionAt(times, entry.posePositions, hi, queryT);
            rot = SampleInterpolatedRotationAt(times, entry.posePositions, entry.poseRotations, lo, u);
        }

        private static int FindFirstStateAtOrAfter(float[] times, float query)
        {
            int low = 0;
            int high = times.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (times[middle] < query)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private static Vector3 SampleInterpolatedPosition(float[] times, Vector3[] positions, float queryT)
        {
            int n = times.Length;
            if (n == 0) return Vector3.zero;
            if (n == 1) return positions[0];
            if (queryT <= times[0]) return positions[0];
            if (queryT >= times[n - 1]) return positions[n - 1];
            return SampleInterpolatedPositionAt(times, positions, FindFirstStateAtOrAfter(times, queryT), queryT);
        }

        // Recordings hold one state per 0.1 s (the game samples on its physics tick
        // every 100 ms), so at racing speed consecutive states are ~3 m apart and
        // the spline alone decides how smooth a ghost looks between them.
        //
        // Catmull-Rom style cubic Hermite on the real (non-uniform) sample times:
        // the tangent at each state is the time-weighted slope across its two
        // neighbours, so speed and direction are continuous through every state.
        // (The previous per-axis monotone Fritsch-Carlson tangents shrank or
        // zeroed each axis's speed at every state where that axis's slope changed,
        // which made ghosts surge at 10 Hz.) Segments faster than any drone flies
        // are respawn/reset teleports: they are not blended and do not feed the
        // neighbouring tangents, so the spline cannot overshoot around them.
        private const float POSE_TELEPORT_SPEED = 150f;

        private static bool IsTeleportSegment(float[] times, Vector3[] positions, int lo)
        {
            float h = times[lo + 1] - times[lo];
            if (h <= 0.0001f)
                return true;
            return (positions[lo + 1] - positions[lo]).sqrMagnitude > POSE_TELEPORT_SPEED * POSE_TELEPORT_SPEED * h * h;
        }

        // Tangent (units/s) at state k. With a teleport on one side, the other
        // side's slope is used alone.
        private static Vector3 PositionTangentAt(float[] times, Vector3[] positions, int k)
        {
            int n = times.Length;
            bool prevOk = k > 0 && !IsTeleportSegment(times, positions, k - 1);
            bool nextOk = k < n - 1 && !IsTeleportSegment(times, positions, k);
            if (prevOk && nextOk)
                return (positions[k + 1] - positions[k - 1]) / (times[k + 1] - times[k - 1]);
            if (prevOk)
                return (positions[k] - positions[k - 1]) / (times[k] - times[k - 1]);
            if (nextOk)
                return (positions[k + 1] - positions[k]) / (times[k + 1] - times[k]);
            return Vector3.zero;
        }

        // Position between states hi-1 and hi, where hi is
        // FindFirstStateAtOrAfter(times, queryT) (callers that already searched
        // pass it in instead of searching again).
        private static Vector3 SampleInterpolatedPositionAt(float[] times, Vector3[] positions, int hi, float queryT)
        {
            int n = times.Length;
            if (n == 0) return Vector3.zero;
            if (n == 1) return positions[0];
            if (queryT <= times[0]) return positions[0];
            if (queryT >= times[n - 1]) return positions[n - 1];
            if (hi <= 0) return positions[0];
            if (hi >= n) return positions[n - 1];

            int lo = hi - 1;
            float t1 = times[lo];
            float h = times[hi] - t1;
            if (h <= 0.0001f)
                return positions[lo];
            float s = (queryT - t1) / h;
            if (IsTeleportSegment(times, positions, lo))
                return s < 1f ? positions[lo] : positions[hi];

            Vector3 d1 = PositionTangentAt(times, positions, lo);
            Vector3 d2 = PositionTangentAt(times, positions, hi);
            float s2 = s * s;
            float s3 = s2 * s;
            float h00 = 2f * s3 - 3f * s2 + 1f;
            float h10 = s3 - 2f * s2 + s;
            float h01 = -2f * s3 + 3f * s2;
            float h11 = s3 - s2;
            return positions[lo] * h00 + d1 * (h * h10) + positions[hi] * h01 + d2 * (h * h11);
        }

        // Rotation with the same continuity: a component-wise cubic Hermite on
        // hemisphere-aligned quaternions, renormalised. Linear slerp between 10 Hz
        // states changed turn rate abruptly at every state.
        private static Quaternion SampleInterpolatedRotationAt(float[] times, Vector3[] positions, Quaternion[] rotations, int lo, float s)
        {
            int n = times.Length;
            int hi = lo + 1;
            if (IsTeleportSegment(times, positions, lo))
                return s < 1f ? rotations[lo] : rotations[hi];
            Vector4 q1 = QuatToVec(rotations[lo]);
            Vector4 q2 = AlignQuat(QuatToVec(rotations[hi]), q1);
            float t1 = times[lo], t2 = times[hi], h = t2 - t1;
            Vector4 d1, d2;
            bool prevOk = lo > 0 && !IsTeleportSegment(times, positions, lo - 1);
            bool nextOk = hi < n - 1 && !IsTeleportSegment(times, positions, hi);
            if (prevOk)
            {
                Vector4 q0 = AlignQuat(QuatToVec(rotations[lo - 1]), q1);
                d1 = (q2 - q0) / (t2 - times[lo - 1]);
            }
            else
                d1 = (q2 - q1) / h;
            if (nextOk)
            {
                Vector4 q3 = AlignQuat(QuatToVec(rotations[hi + 1]), q2);
                d2 = (q3 - q1) / (times[hi + 1] - t1);
            }
            else
                d2 = (q2 - q1) / h;
            float s2 = s * s;
            float s3 = s2 * s;
            Vector4 q = q1 * (2f * s3 - 3f * s2 + 1f) + d1 * (h * (s3 - 2f * s2 + s)) +
                q2 * (-2f * s3 + 3f * s2) + d2 * (h * (s3 - s2));
            float mag = q.magnitude;
            if (mag < 1e-6f)
                return Quaternion.SlerpUnclamped(rotations[lo], rotations[hi], s);
            q /= mag;
            return new Quaternion(q.x, q.y, q.z, q.w);
        }

        private static Vector4 QuatToVec(Quaternion q)
        {
            return new Vector4(q.x, q.y, q.z, q.w);
        }

        private static Vector4 AlignQuat(Vector4 q, Vector4 reference)
        {
            return Vector4.Dot(q, reference) < 0f ? -q : q;
        }
    }
}
