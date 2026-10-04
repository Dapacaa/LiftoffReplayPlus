using System.Collections.Generic;
using UnityEngine;

namespace LiftoffGhostLines
{
    // Per-frame replay snapshot.
    //
    // Time bases (both in seconds):
    //  * Native replay clock: what the game's replay slider/controller reports and
    //    TryGetReplayTime returns. TryGetDroneTransformUnshifted samples on it
    //    (recording start = 0); used only to match our entries to native drones.
    //  * Mod clock: the native clock shifted per pilot so every pilot starts at
    //    their first gate (GetGateQueryTimestamp). Ghost poses, delta, stick cam
    //    and graphs all sample on it through TryGetDroneTransform / TryGetDroneInput.
    //
    // Frame order: LateUpdate reads the native clock once (stableReplayTime) and
    // derives the smoothed displayReplayTime; BuildFramePoses then samples every
    // mapped ghost at displayReplayTime exactly once. PiPs render right after with
    // those poses applied (RenderPiPsAtFramePoses), the main camera's pre-cull
    // applies the same cached poses, and OnGUI consumers read
    // TryGetFrameReplayTime. Nothing drawn in a frame mixes clocks or smooths a
    // pose twice.
    public partial class GhostLinePoller
    {
        private struct FramePose
        {
            public int frame;
            public bool ok;
            public Vector3 rawPos;
            public Quaternion rawRot;
        }

        private readonly Dictionary<GhostLineEntry, FramePose> framePoses = new Dictionary<GhostLineEntry, FramePose>();
        // Frame of the last snapshot; -1 until the first one of the current replay.
        private int frameSnapshotFrame = -1;

        // The game advances replay time only in FixedUpdate
        // (time += fixedDeltaTime * speed), so the native clock moves in physics-
        // tick steps: several rendered frames see the same time, then it jumps a
        // whole tick. Driving poses from it makes drones surge back and forth at
        // speed. Unity tells us how far this frame is past the last tick
        // (Time.time - Time.fixedTime); extrapolating by that gives the continuous
        // time the native clock is sampling, the same idea as Rigidbody
        // interpolation.
        // Returns whether the native clock is running (so extrapolation applies);
        // lead is legitimately 0 on a frame that lands exactly on a physics tick,
        // so "lead != 0" must never be used as the running test.
        private bool TryGetNativeClockLead(out float lead)
        {
            lead = 0f;
            if (overridePlayback || frameStepPaused || isDraggingHandle || draggingScrubber ||
                draggingTrimStart || draggingTrimEnd || !IsNativeReplayPlaying())
                return false;
            if (nativeSpeedPropInfo == null && nativeSpeedFieldInfo == null)
                return false;
            object race = GetReplayRace();
            if (race == null)
                return false;
            float speed;
            try
            {
                speed = nativeSpeedPropInfo != null
                    ? (float)nativeSpeedPropInfo.GetValue(race, null)
                    : (float)nativeSpeedFieldInfo.GetValue(race);
            }
            catch { return false; }
            float sinceTick = Mathf.Clamp(Time.time - Time.fixedTime, 0f, Time.fixedDeltaTime);
            lead = sinceTick * speed;
            return true;
        }

        private void BuildFramePoses()
        {
            frameSnapshotFrame = Time.frameCount;
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Key != null && pair.Value != null)
                    SampleFramePose(pair.Key);
            }
            UpdateSmoothDroneTrails(displayReplayTime);
        }

        private struct RootPose
        {
            public Transform root;
            public Vector3 pos;
            public Quaternion rot;
        }

        private readonly List<RootPose> pipRootRestore = new List<RootPose>();

        // PiP cameras render here, outside the main camera's pre-cull, so the
        // snapshot poses are put on the drone roots only for these renders and the
        // roots are then put back. The main camera's pre-cull must still see the
        // native root pose: it derives the third-person camera shift from it.
        private void RenderPiPsAtFramePoses()
        {
            pipRootRestore.Clear();
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Key != null && pair.Value != null)
                    pipRootRestore.Add(new RootPose { root = pair.Value, pos = pair.Value.position, rot = pair.Value.rotation });
            }
            ApplyGhostDroneTransforms();
            try { TickPiPWindows(); }
            finally
            {
                for (int i = 0; i < pipRootRestore.Count; i++)
                {
                    if (pipRootRestore[i].root != null)
                        pipRootRestore[i].root.SetPositionAndRotation(pipRootRestore[i].pos, pipRootRestore[i].rot);
                }
                pipRootRestore.Clear();
            }
        }

        private void ClearFrameSnapshot()
        {
            framePoses.Clear();
            frameSnapshotFrame = -1;
        }

        // Replay time for things drawn this frame (stick cam, graphs, PiP docks).
        // Falls back to the live native clock before the first snapshot.
        private bool TryGetFrameReplayTime(out float replayTime)
        {
            if (replayReady && frameSnapshotFrame >= 0)
            {
                replayTime = displayReplayTime;
                return true;
            }
            return TryGetReplayTime(out replayTime);
        }

        // Samples one ghost at displayReplayTime and advances its smoothing, at
        // most once per frame. The smoothed result lives in ghostSmoothedPos/Rot.
        private bool SampleFramePose(GhostLineEntry entry)
        {
            if (entry == null)
                return false;
            int frame = Time.frameCount;
            FramePose fp;
            if (framePoses.TryGetValue(entry, out fp) && fp.frame == frame)
                return fp.ok;
            fp.frame = frame;
            fp.ok = TryGetDroneTransform(entry, displayReplayTime, out fp.rawPos, out fp.rawRot);
            if (fp.ok)
            {
                Vector3 smoothed;
                if (ghostSmoothedPos.TryGetValue(entry, out smoothed))
                    smoothed = Vector3.Lerp(smoothed, fp.rawPos, ghostPosBlend);
                else
                    smoothed = fp.rawPos;
                ghostSmoothedPos[entry] = smoothed;
                Quaternion smoothedRot;
                if (ghostSmoothedRot.TryGetValue(entry, out smoothedRot))
                    smoothedRot = Quaternion.Slerp(smoothedRot, fp.rawRot, ghostPosBlend);
                else
                    smoothedRot = fp.rawRot;
                ghostSmoothedRot[entry] = smoothedRot;
            }
            framePoses[entry] = fp;
            return fp.ok;
        }

        // This frame's smoothed pose, once the snapshot for this frame exists.
        private bool TryGetFramePose(GhostLineEntry entry, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (!replayReady || frameSnapshotFrame != Time.frameCount || !SampleFramePose(entry))
                return false;
            return ghostSmoothedPos.TryGetValue(entry, out pos) && ghostSmoothedRot.TryGetValue(entry, out rot);
        }
    }
}
