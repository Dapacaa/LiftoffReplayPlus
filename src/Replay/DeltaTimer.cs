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
        private void UpdateReferenceDeltaDisplay(float replayTime)
        {
            var refEntry = referenceEntry;
            if (refEntry == null)
            {
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    if (entry.deltaText != null)
                        SetDeltaText(entry, "", Color.white, false);
                }
                return;
            }

            if (refEntry.pathDistances == null || refEntry.pathDistances.Length == 0)
            {
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    if (entry.deltaText != null)
                        SetDeltaText(entry, "", Color.white, false);
                }
                return;
            }

            float refRaceEnd = refEntry.finishTime > 0f
                ? refEntry.finishTime
                : (refEntry.stateTimes != null && refEntry.stateTimes.Length > 0 ? refEntry.stateTimes[refEntry.stateTimes.Length - 1] - GetGateTimestamp(refEntry) : -1f);

            float effReplayTime = (refRaceEnd > 0f && replayTime > refRaceEnd) ? refRaceEnd : replayTime;

            if (lastDeltaRefEntry != refEntry)
            {
                lastDeltaRefEntry = refEntry;
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    entry.lastRefMatchDist = float.NaN;
                    entry.lastRefMatchReplayTime = float.NaN;
                    entry.lastRefDistNow = float.NaN;
                    entry.lastDeltaRawValue = float.NaN;
                    entry.lastDeltaUpdateTime = -1f;
                    entry.lastDeltaReplayTime = float.NaN;
                }
            }

            float refDist = GetDistanceAlongPath(refEntry, effReplayTime);
            float refLapDist = Mathf.Max(0f, refDist - refEntry.lapStartDistance);

            foreach (var entry in GhostLinesPlugin.entries)
            {
                if (entry.deltaText == null) continue;

                float ghostRaceEnd = entry.finishTime > 0f
                    ? entry.finishTime
                    : (entry.stateTimes != null && entry.stateTimes.Length > 0 ? entry.stateTimes[entry.stateTimes.Length - 1] - GetGateTimestamp(entry) : -1f);

                if (entry == refEntry)
                {
                    float shown = Mathf.Max(0f, replayTime);
                    if (ghostRaceEnd > 0f && shown > ghostRaceEnd)
                        shown = ghostRaceEnd;

                    if (Mathf.Abs(shown - entry.lastDeltaShownValue) >= 0.0005f || string.IsNullOrEmpty(entry.lastShownDeltaText)
                        || entry.deltaText.gameObject.activeSelf != (GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible)
                        || entry.deltaText.text != entry.lastShownDeltaText)
                    {
                        string timeText = shown.ToString("F3");
                        entry.lastDeltaShownValue = shown;
                        entry.lastShownDeltaText = timeText;
                        SetDeltaText(entry, timeText, Color.white, GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible);
                    }
                    continue;
                }

                if (entry.pathDistances == null || entry.pathDistances.Length == 0 || entry.lapTotalDistance <= 0f)
                {
                    if (entry.guiDeltaText != "")
                        SetDeltaText(entry, "", Color.white, false);
                    continue;
                }

                float matchTime = replayTime;
                if (ghostRaceEnd > 0f && matchTime > ghostRaceEnd) matchTime = ghostRaceEnd;

                float entryDist = GetDistanceAlongPath(entry, matchTime);
                float entryLapDist = Mathf.Max(0f, entryDist - entry.lapStartDistance);

                float rawDelta;
                bool lockFinalDelta = false;
                // Treat the recording's final sample as the finish boundary.  The
                // replay clock can advance a frame or two beyond that sample, so
                // lock as soon as the pilot reaches the boundary instead of
                // continuing to derive a live geometric delta from a finished lap.
                if (ghostRaceEnd > 0f && refRaceEnd > 0f && replayTime >= ghostRaceEnd - 0.02f)
                {
                    rawDelta = ghostRaceEnd - refRaceEnd;
                    lockFinalDelta = true;
                }
                else if (refLapDist <= 0f || entryLapDist <= 0f)
                {
                    rawDelta = 0f;
                }
                else
                {
                    Vector3 entryPos;
                    float refTimeAtEntryPos;
                    float refDistAtEntryPos;
                    if (!TryGetDronePosition(entry, matchTime, out entryPos) ||
                        !TryGetNearestRefPathDistance(refEntry, entry, entryPos, matchTime, out refDistAtEntryPos))
                    {
                        rawDelta = float.IsNaN(entry.lastDeltaRawValue) ? 0f : entry.lastDeltaRawValue;
                    }
                    else
                    {
                        refTimeAtEntryPos = GetTimeAtDistance(refEntry, refDistAtEntryPos) - refEntry.lapStartOffset;
                        rawDelta = matchTime - refTimeAtEntryPos;
                    }
                }

                float delta = GetDisplayedDelta(entry, rawDelta, replayTime, lockFinalDelta);

                if (Mathf.Abs(delta - entry.lastDeltaShownValue) < 0.0005f && entry.lastShownDeltaText != null
                    && entry.deltaText.gameObject.activeSelf == (GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible)
                    && entry.deltaText.text == entry.lastShownDeltaText)
                    continue;
                entry.lastDeltaShownValue = delta;
                string sign = delta > 0f ? "+" : "";
                string text = sign + delta.ToString("F3");
                entry.lastShownDeltaText = text;
                Color deltaColor = delta > 0f
                    ? new Color(1f, 0.15f, 0.15f, 1f)
                    : new Color(0.2f, 1f, 0.25f, 1f);
                SetDeltaText(entry, text, deltaColor, GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible);
            }
        }

        private static float GetDisplayedDelta(GhostLineEntry entry, float rawDelta, float replayTime, bool lockFinalDelta)
        {
            // Recordings are sampled at 10 Hz.  The geometric match can therefore
            // alternate between adjacent samples even while the drone is flying
            // smoothly.  Keep the measurement raw, but make only the text move
            // continuously so a one-sample switch is not shown as a 0.1 s jump.
            float now = Time.realtimeSinceStartup;
            float shown = entry.lastDeltaShownValue;
            bool replayRestarted = !float.IsNaN(entry.lastDeltaReplayTime)
                && replayTime < entry.lastDeltaReplayTime - 0.02f;
            bool replayJumped = !float.IsNaN(entry.lastDeltaReplayTime)
                && Mathf.Abs(replayTime - entry.lastDeltaReplayTime) > 0.25f;
            entry.lastDeltaRawValue = rawDelta;
            entry.lastDeltaReplayTime = replayTime;

            // Scrubbing and frame seeks are discontinuities, not flight motion.
            // Show their measured value immediately; reserve the slew limiter for
            // adjacent 10 Hz recording-sample changes during normal playback.
            if (lockFinalDelta || replayRestarted || replayJumped || float.IsNaN(shown) || entry.lastDeltaUpdateTime < 0f)
            {
                entry.lastDeltaUpdateTime = now;
                return rawDelta;
            }

            float elapsed = Mathf.Clamp(now - entry.lastDeltaUpdateTime, 0f, 0.25f);
            entry.lastDeltaUpdateTime = now;

            // A change well beyond one sample step (a crash, a detour) is real:
            // show it at once. A fixed 1.5 s/s slew used to make a 4 s loss creep
            // in over ~2.7 s.
            if (Mathf.Abs(rawDelta - shown) > DELTA_SNAP_THRESHOLD)
                return rawDelta;

            // Sample-step flicker (~0.1 s) is eased out with a short time constant,
            // so the text still tracks a steadily changing delta closely.
            float k = 1f - Mathf.Exp(-elapsed / DELTA_SMOOTH_TIME_CONSTANT);
            return Mathf.Lerp(shown, rawDelta, k);
        }

        private const float DELTA_SNAP_THRESHOLD = 0.35f;
        private const float DELTA_SMOOTH_TIME_CONSTANT = 0.1f;

        private static void SetDeltaText(GhostLineEntry entry, string text, Color color, bool visible)
        {
            if (entry == null || entry.deltaText == null)
                return;

            if (entry.deltaText.text != text)
                entry.deltaText.text = text;
            entry.guiDeltaText = text;
            if (entry.deltaText.color != color)
                entry.deltaText.color = color;
            if (entry.deltaText.gameObject.activeSelf != visible)
                entry.deltaText.gameObject.SetActive(visible);
        }

        public static float GetDistanceAlongPath(GhostLineEntry entry, float time)
        {
            if (entry == null || entry.stateTimes == null || entry.pathDistances == null || entry.stateTimes.Length == 0)
                return 0f;
            float[] times = entry.stateTimes;
            float[] dists = entry.pathDistances;
            float firstT = times[0];
            float queryT = GetGateQueryTimestamp(entry, time);

            if (queryT <= firstT) return 0f;
            if (queryT >= times[times.Length - 1]) return dists[dists.Length - 1];
            int lo = 0, hi = times.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] <= queryT) lo = mid; else hi = mid;
            }
            float duration = times[hi] - times[lo];
            if (Mathf.Approximately(duration, 0f))
                return dists[lo];
            float t = (queryT - times[lo]) / duration;
            return dists[lo] + t * (dists[hi] - dists[lo]);
        }

        public static float GetTimeAtDistance(GhostLineEntry entry, float distance)
        {
            if (entry == null || entry.pathDistances == null || entry.stateTimes == null || entry.pathDistances.Length == 0)
                return 0f;
            float[] dists = entry.pathDistances;
            float[] times = entry.stateTimes;
            float firstT = times[0];
            if (distance <= dists[0]) return 0f;
            if (distance >= dists[dists.Length - 1]) return times[times.Length - 1] - firstT;
            int lo = 0, hi = dists.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (dists[mid] <= distance) lo = mid; else hi = mid;
            }
            float t = Mathf.Approximately(dists[hi], dists[lo]) ? 0f : (distance - dists[lo]) / (dists[hi] - dists[lo]);
            return times[lo] + t * (times[hi] - times[lo]) - firstT;
        }

        private static bool TryGetNearestRefPathDistance(GhostLineEntry refEntry, GhostLineEntry entry, Vector3 pos, float replayTime, out float refDist)
        {
            refDist = 0f;
            if (refEntry == null || entry == null || refEntry.originalPositions == null || refEntry.pathDistances == null || refEntry.stateTimes == null)
                return false;
            var positions = refEntry.originalPositions;
            var dists = refEntry.pathDistances;
            var times = refEntry.stateTimes;
            int n = positions.Length;
            if (n < 2 || dists.Length != n || times.Length != n)
                return false;

            int first = 0;
            if (refEntry.lapStartDistance > 0f)
            {
                int lo = 0, hi = n - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (dists[mid] < refEntry.lapStartDistance) lo = mid + 1; else hi = mid;
                }
                first = Mathf.Clamp(lo - 1, 0, n - 1);
            }
            int last = n - 1;
            int span = last - first + 1;
            float lapDur = Mathf.Max(0.001f, refEntry.finishTime > 0f ? refEntry.finishTime : times[last] - times[first]);
            float statesPerSec = span / lapDur;

            int win = Mathf.Max(48, (int)(statesPerSec * 8f));
            float refDistNow = Mathf.Clamp(GetDistanceAlongPath(refEntry, replayTime), dists[first], dists[last]);
            float entryDistNow = entry.pathDistances != null && entry.pathDistances.Length > 0
                ? GetDistanceAlongPath(entry, replayTime)
                : 0f;
            float entryRaceDist = Mathf.Max(0f, entryDistNow - entry.lapStartDistance);
            float odAnchorDist = Mathf.Clamp(refEntry.lapStartDistance + entryRaceDist, dists[first], dists[last]);
            float refFinishTime = refEntry.finishTime > 0f
                ? refEntry.finishTime
                : (times[last] - GetGateTimestamp(refEntry));
            bool refFinished = refFinishTime > 0f && replayTime > refFinishTime + 0.001f;

            // A previous match is useful only as a tie breaker at an intersection.
            // Do not turn it into the search anchor: a bad match must be able to
            // recover immediately when its geometry no longer fits.
            bool hasContinuity = !float.IsNaN(entry.lastRefMatchDist)
                && !float.IsNaN(entry.lastRefMatchReplayTime)
                && replayTime >= entry.lastRefMatchReplayTime - 0.02f
                && replayTime - entry.lastRefMatchReplayTime <= 0.25f;
            if (!float.IsNaN(entry.lastRefMatchReplayTime) && replayTime < entry.lastRefMatchReplayTime - 0.02f)
            {
                entry.lastRefMatchDist = float.NaN;
                entry.lastRefMatchReplayTime = float.NaN;
            }
            float preferredRaceTime = replayTime;
            if (hasContinuity)
            {
                float previousRaceTime = GetTimeAtDistance(refEntry, entry.lastRefMatchDist) - refEntry.lapStartOffset;
                preferredRaceTime = previousRaceTime + (replayTime - entry.lastRefMatchReplayTime);
            }

            Vector3 entryTangent;
            bool hasEntryTangent = TryGetPathTangentAtReplayTime(entry, replayTime, out entryTangent);

            // Once the reference has finished, its current path distance is pinned
            // at the finish line. Anchor to the other pilot's lap distance instead,
            // otherwise every slower pilot would be matched against the finish
            // section and its delta would appear frozen.
            float primaryAnchorDist = refFinished ? odAnchorDist : refDistNow;
            if (refFinished)
                preferredRaceTime = GetTimeAtDistance(refEntry, primaryAnchorDist) - refEntry.lapStartOffset;

            if (TryRefWindowMatch(refEntry, dists, times, positions, first, last, win, pos,
                primaryAnchorDist, preferredRaceTime, hasEntryTangent ? entryTangent : Vector3.zero, out refDist))
            {
                entry.lastRefMatchDist = refDist;
                entry.lastRefMatchReplayTime = replayTime;
                return true;
            }

            if (TryRefWindowMatch(refEntry, dists, times, positions, first, last, win, pos,
                odAnchorDist, replayTime, hasEntryTangent ? entryTangent : Vector3.zero, out refDist))
            {
                entry.lastRefMatchDist = refDist;
                entry.lastRefMatchReplayTime = replayTime;
                return true;
            }

            return false;
        }

        private static bool TryRefWindowMatch(GhostLineEntry refEntry, float[] dists, float[] times, Vector3[] positions,
            int first, int last, int win, Vector3 pos, float centerDist,
            float preferredRaceTime, Vector3 entryTangent, out float refDist)
        {
            refDist = 0f;
            int loC = first, hiC = last;
            while (loC < hiC)
            {
                int mid = (loC + hiC) / 2;
                if (dists[mid] < centerDist) loC = mid + 1; else hiC = mid;
            }
            int center = loC;
            int segLo = Mathf.Max(first, center - win);
            int segHi = Mathf.Min(last - 1, center + win);
            if (segHi < segLo)
                return false;

            float best = float.MaxValue;
            float bestT = 0f;
            int bestSeg = segLo;
            bool found = false;
            // Invariant across the whole window — hoisted out of the loop (this loop
            // runs up to ~2x win iterations per attempt, per entry, per frame).
            float gateT = GetGateTimestamp(refEntry);
            for (int i = segLo; i <= segHi; i++)
            {
                Vector3 a = positions[i];
                Vector3 b = positions[i + 1];
                Vector3 ab = b - a;
                float lenSqr = ab.sqrMagnitude;
                float t;
                float distSqr;
                if (lenSqr < 1e-12f)
                {
                    t = 0f;
                    distSqr = (a - pos).sqrMagnitude;
                }
                else
                {
                    t = Mathf.Clamp01(Vector3.Dot(pos - a, ab) / lenSqr);
                    distSqr = (a + ab * t - pos).sqrMagnitude;
                }
                float tAbs = times[i] + t * (times[i + 1] - times[i]);
                float raceTimeAtPoint = tAbs - gateT;
                float temporalErr = Mathf.Abs(raceTimeAtPoint - preferredRaceTime);

                // Geometry is the primary answer.  Time continuity is deliberately
                // capped so it settles adjacent 10 Hz candidates without dragging a
                // match onto a nearby future track strand.  Flight direction breaks
                // the otherwise ambiguous case where a course crosses itself.
                float temporalScore = Mathf.Min(temporalErr * temporalErr * 150f, 4f);
                float directionScore = 0f;
                if (entryTangent.sqrMagnitude > 0.0001f && lenSqr > 1e-12f)
                {
                    float alignment = Vector3.Dot(entryTangent, ab.normalized);
                    directionScore = (1f - alignment) * 12f;
                }
                float score = distSqr + temporalScore + directionScore;
                if (score < best) { best = score; bestSeg = i; bestT = t; found = true; }
            }
            if (!found)
                return false;

            refDist = dists[bestSeg] + bestT * (dists[bestSeg + 1] - dists[bestSeg]);
            return true;
        }

        private static bool TryGetPathTangentAtReplayTime(GhostLineEntry entry, float replayTime, out Vector3 tangent)
        {
            tangent = Vector3.zero;
            if (entry == null || entry.stateTimes == null || entry.originalPositions == null || entry.stateTimes.Length < 2
                || entry.stateTimes.Length != entry.originalPositions.Length)
                return false;

            float queryTime = GetGateQueryTimestamp(entry, replayTime);
            float before = Mathf.Max(entry.stateTimes[0], queryTime - 0.075f);
            float after = Mathf.Min(entry.stateTimes[entry.stateTimes.Length - 1], queryTime + 0.075f);
            if (after - before < 0.01f)
                return false;

            if (entry.poseTimes == null || entry.posePositions == null)
                return false;
            tangent = SampleInterpolatedPosition(entry.poseTimes, entry.posePositions, after)
                - SampleInterpolatedPosition(entry.poseTimes, entry.posePositions, before);
            if (tangent.sqrMagnitude < 0.0001f)
            {
                tangent = Vector3.zero;
                return false;
            }
            tangent.Normalize();
            return true;
        }
    }
}
