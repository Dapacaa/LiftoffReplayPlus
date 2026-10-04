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
        // Called from the game's own checkpoint-delta event. Unlike the trigger-prefix
        // path this fires exactly once per crossing, with the game's lap clock read at
        // the same instant, so gate detection and times are both authoritative.
        public void OnRaceGateEvent(string passageId, float lapTime)
        {
            if (replayReady || string.IsNullOrEmpty(passageId) || lapTime < 0f)
                return;

            string key = null;
            try { key = GateSplitsBinding.CurrentRaceKey(); } catch { }
            if (string.IsNullOrEmpty(key))
                key = !string.IsNullOrEmpty(wsRaceItemId) ? wsRaceItemId :
                    !string.IsNullOrEmpty(mpCurrentRaceName) ? mpCurrentRaceName :
                    !string.IsNullOrEmpty(mpCurrentTrackName) ? mpCurrentTrackName : "race";
            if (raceEventRaceKey != key)
            {
                // A key we had seen before changing means a different race in the
                // same scene: the extracted gate expectation no longer applies. The
                // first event of a scene (key still null) keeps it — it was read
                // from the race currently loaded.
                if (raceEventRaceKey != null)
                    raceGraphGateCount = 0;
                ResetRaceSplitsForScene();
                // Let the next probe tick re-read the new race's graph right away
                // instead of waiting out the (up to 15s) stable-scene cadence.
                raceProbeNextAt = 0f;
                raceEventRaceKey = key;
                // Restore saved laps/best/gate list for this race key.
                LoadPersistedTrack(key);
            }

            bool isStartArch = raceRowKeys.Count > 0 && passageId == raceRowKeys[0];
            if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits: gate='" + passageId +
                "' t=" + lapTime.ToString("F3") + " prev=" + raceEventPrevLapTime.ToString("F3") +
                " lapGates=" + (raceCurLap != null ? raceCurLap.keys.Count : -1) +
                " passed=" + raceEventPassed.Count + " slots=" + raceRowKeys.Count +
                (isStartArch ? " startArch" : ""));

            // A lap closed on the frame the clock reset (finish line): the completed
            // attempt is still the display lap. The next event starts the new lap so
            // the finished splits stay visible until then.
            if (raceEventLapClosed)
            {
                raceEventLapClosed = false;
                if (lapTime <= 0.05f || (isStartArch && lapTime <= 1f))
                {
                    BeginEventLap(passageId);
                    raceEventPrevLapTime = 0f;
                    return;
                }
                BeginEventLap(null);
            }

            // The clock sits at/near zero while crossing the start line. The previous
            // lap (if in progress) is complete and a new one begins.
            if (lapTime <= 0.05f || (isStartArch && lapTime <= 1f))
            {
                CloseEventLap(raceEventPrevLapTime);
                BeginEventLap(passageId);
                raceEventPrevLapTime = 0f;
                return;
            }

            // Merged finish arch: the start gate crossed again with the clock running
            // IS the finish. Close immediately so the lap (and Best) registers without
            // needing the next lap's first gate.
            if (isStartArch && raceCurLap != null && raceCurLap.keys.Count > 0 &&
                raceCurLap.keys[0] == passageId && raceEventPassed.Contains(0))
            {
                int passedNow = raceEventPassed.Count;
                int routeLenNow = raceEventCompletedLen > 0 ? raceEventCompletedLen : raceRowKeys.Count;
                int needNow = routeLenNow > 0 ? Mathf.Max(2, (routeLenNow + 1) / 2) : 2;
                if (lapTime > 1f && passedNow >= needNow)
                {
                    CloseEventLap(lapTime);
                    BeginEventLap(passageId);
                    raceEventPrevLapTime = 0f;
                    return;
                }
            }

            // A lower clock reading means the game reset it since the last gate: that
            // last gate was the finish, and this one is the new lap's first gate.
            if (raceEventPrevLapTime > 1f && lapTime < raceEventPrevLapTime - 0.5f)
            {
                CloseEventLap(raceEventPrevLapTime);
                BeginEventLap(null);
            }

            RecordEventGate(passageId, lapTime);
            raceEventPrevLapTime = lapTime;
        }

        private void BeginEventLap(string startPassageId)
        {
            raceCurLap = new RaceLapAttempt();
            raceEventPassed.Clear();
            raceEventLapClosed = false;
            raceSplitsScrollManual = false;
            if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits: new lap (startId=" +
                (string.IsNullOrEmpty(startPassageId) ? "none" : startPassageId) + ")");
            if (string.IsNullOrEmpty(startPassageId))
                return;
            int idx = raceRowKeys.IndexOf(startPassageId);
            if (idx < 0)
            {
                raceRowKeys.Insert(0, startPassageId);
            }
            else if (idx != 0)
            {
                raceRowKeys.RemoveAt(idx);
                raceRowKeys.Insert(0, startPassageId);
            }
            raceCurLap.keys.Add(startPassageId);
            raceCurLap.times.Add(0.0);
            raceEventPassed.Add(0);
            RebuildEventRows();
        }

        private void RecordEventGate(string passageId, float lapTime)
        {
            if (raceCurLap == null)
                BeginEventLap(null);
            int idx = raceRowKeys.IndexOf(passageId);
            if (idx < 0)
            {
                // New gates are only learned while the route isn't confirmed yet; after
                // the first complete lap an unknown gate is a wrong-gate detour and
                // must not grow/shift the route.
                if (raceEventCompletedLen > 0 || raceRowKeys.Count >= 300)
                    return;
                idx = raceRowKeys.Count;
                raceRowKeys.Add(passageId);
                RebuildEventRows();
            }
            else if (raceEventPassed.Contains(idx))
            {
                return; // repeated or backtracked gate
            }
            raceCurLap.keys.Add(passageId);
            raceCurLap.times.Add(lapTime);
            raceEventPassed.Add(idx);
            // A crossing re-anchors the view on the live gate (LiveSplit style);
            // manual inspection is for between gates/laps.
            raceSplitsScrollManual = false;
        }

        private int EventLapNeed()
        {
            int routeLen = raceEventCompletedLen > 0 ? raceEventCompletedLen : raceRowKeys.Count;
            return routeLen > 0 ? Mathf.Max(2, (routeLen + 1) / 2) : 2;
        }

        private bool CloseEventLap(float lapEndTime)
        {
            if (raceCurLap == null)
                return false;
            if (lapEndTime <= 0.5f)
            {
                // No finish crossing seen (e.g. scene change/reset). Nothing to
                // record and nothing worth discarding loudly.
                raceCurLap = null;
                raceEventPassed.Clear();
                return false;
            }
            int passed = raceEventPassed.Count;
            int routeLen = raceEventCompletedLen > 0 ? raceEventCompletedLen : raceRowKeys.Count;
            int need = EventLapNeed();
            if (passed < need || raceCurLap.times.Count < 2)
            {
                if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits: lap NOT counted (passed=" +
                    passed + " need=" + need + " end=" + lapEndTime.ToString("F3") +
                    " times=" + raceCurLap.times.Count + " routeLen=" + routeLen + ")");
                raceCurLap = null;
                raceEventPassed.Clear();
                return false;
            }
            // A finish-line crossing resets the game clock, but so do respawns and
            // race resets — and the half-route rule used to record those as laps,
            // pinning the route (raceEventCompletedLen) to the gates learned so far
            // and permanently rejecting every later gate. Only count a lap when the
            // attempt actually covered the known route (one row of slack for the
            // Start row / merged finish).
            int expected = raceGraphGateCount > 1 ? raceGraphGateCount : raceEventCompletedLen;
            if (expected > 1 && raceCurLap.keys.Count < expected - 1)
            {
                if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits: lap discarded (route " +
                    raceCurLap.keys.Count + "/" + expected + " — reset or skipped gates)");
                raceCurLap = null;
                raceEventPassed.Clear();
                return false;
            }
            // No route expectation at all (graph unavailable and no stored lap yet):
            // a respawn clock reset is indistinguishable from a finish, so require a
            // plausible gate count. Otherwise one-gate respawns recorded fake laps
            // and pinned the route to a 2-gate "track".
            if (expected <= 1 && raceCurLap.keys.Count < 4)
            {
                if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits: lap discarded (route " +
                    raceCurLap.keys.Count + "/? — no route expectation)");
                raceCurLap = null;
                raceEventPassed.Clear();
                return false;
            }
            // Samples are already "seconds into the lap"; if the attempt began mid-lap
            // (no start crossing seen) rebase so the first sample is zero.
            double t0 = raceCurLap.times[0];
            if (t0 > 0.001)
            {
                for (int i = 0; i < raceCurLap.times.Count; i++)
                    raceCurLap.times[i] -= t0;
            }
            if (raceEventCompletedLen < routeLen)
                raceEventCompletedLen = routeLen;
            if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] gate-splits: lap recorded t=" +
                lapEndTime.ToString("F3") + " passed=" + passed + "/" + routeLen);
            raceLapNumber++;
            CloseRaceLap(lapEndTime);
            RebuildEventRows();
            // Keep the finished attempt as the display lap: the panel shows its
            // splits until the next lap's first gate replaces it (LiveSplit style)
            // instead of going blank the moment the line is crossed.
            raceEventPassed.Clear();
            PersistSplitLap();
            return true;
        }

        // Slot ids double as the panel's row keys; labels are index-based (no graph
        // needed): Start, G01.., Finish once the route is confirmed.
        private void RebuildEventRows()
        {
            raceLapGateTotal = raceRowKeys.Count;
            raceVisibleIdx.Clear();
            for (int i = 0; i < raceRowKeys.Count; i++)
                raceVisibleIdx.Add(i);
            raceRequiredCount = raceRowKeys.Count;
            raceGateLabels.Clear();
            for (int i = 0; i < raceRowKeys.Count; i++)
            {
                if (i == 0)
                    raceGateLabels.Add("Start");
                else if (i == raceRowKeys.Count - 1 && raceEventCompletedLen > 0)
                    raceGateLabels.Add("Finish");
                else
                    raceGateLabels.Add(RaceGateLabel(i));
            }
        }

        private void ResetRaceSplitsForScene()
        {
            raceEventRaceKey = null;
            raceEventPrevLapTime = -1f;
            raceEventPrevFrameLap = -1f;
            raceEventLapClosed = false;
            raceEventCompletedLen = 0;
            raceEventPassed.Clear();
            raceSplitsManualSize = false;
            raceSplitsScrollManual = false;
            raceSplitsScroll = 0f;
            raceCurLap = null;
            raceBestLap = null;
            raceBestLapTime = -1;
            raceLapNumber = 0;
            raceCycleKeys.Clear();
            raceGraphDumped = false;
            raceLapGateTotal = 0;
            raceGateLabels.Clear();
            raceLapTimerInst = null;
            raceLapTimerNextScanAt = 0f;
            raceLapScanFails = 0;
            raceProbeStableScans = 0;
            raceLtMin = null;
            raceLtSec = null;
            raceLtMilli = null;
            raceGameLapTime = -1f;
            raceSyncPrevGameLap = -1f;
            raceSyncT0 = -1.0;
            raceLastCloseAt = -999f;
            raceLastDoneLap = null;
            raceLastT0 = -1.0;
            raceRowKeys.Clear();
            raceVisibleIdx.Clear();
            raceFinishKey = null;
            raceMergedFinish = false;
            raceCapturePendingAt = -999f;
            raceCaptureHave = 0;
            raceCapturedLap = -1f;
            raceCaptureEndT = 0.0;
            raceCaptureThenNewLap = false;
            raceCaptureNewKey = null;
            raceCaptureNewTime = 0.0;
            raceTrimmedKeys.Clear();
            raceSeenKeys.Clear();
            raceRequiredCount = 0;
            raceGraphRetryAt = 0f;
            raceGraphTries = 0;
            raceGraphReprobeAt = 0f;
            raceStartKey = null;
            raceLapHistory.Clear();
            raceBestLapHistoryIdx = -1;
            raceHistoryOpen = false;
            raceHistoryDrag.Cancel();
            raceHistoryEditIdx = -1;
            raceHistoryEditVals.Clear();
            raceGraphFrom = 0f;
            raceGraphSpan = 0f;
            raceGraphRowsAt = -1;
            raceGraphPanDrag = false;
            raceGraphCustom = false;
            raceGraphSel.Clear();
            raceGraphScaleInit = false;
            raceGraphDefIdxsCount = -1;
            raceGraphDefIdxsEpoch = -1;
            raceGraphHistoryEpoch++;
            raceTrigLastAt.Clear();
        }

        private static double RaceNowTime()
        {
            try
            {
                if (Photon.Pun.PhotonNetwork.InRoom)
                    return Photon.Pun.PhotonNetwork.Time;
            }
            catch { }
            return Time.unscaledTimeAsDouble;
        }

        private static string RaceGateKey(RaceCheckpoint cp)
        {
            Vector3 pos = cp.transform.position;
            return Mathf.Round(pos.x * 2f).ToString("0") + "," + Mathf.Round(pos.y * 2f).ToString("0") + "," + Mathf.Round(pos.z * 2f).ToString("0");
        }

        private static int ParseLeadingInt(string s)
        {
            if (string.IsNullOrEmpty(s))
                return -1;
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\u00a0'))
                i++;
            if (i >= s.Length || s[i] < '0' || s[i] > '9')
                return -1;
            int v = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                v = v * 10 + (s[i] - '0');
                if (v > 999999)
                    return -1;
                i++;
            }
            return v;
        }

        private static int DigitCount(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;
            int c = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= '0' && s[i] <= '9')
                    c++;
            return c;
        }

        private float ReadGameLapSeconds()
        {
            try
            {
                if (raceLapTimerInst == null || (raceLapTimerInst != null && raceLapTimerInst.Equals(null)))
                {
                    float scanNow = Time.unscaledTime;
                    if (scanNow < raceLapTimerNextScanAt)
                        return -1f;
                    raceLapTimerNextScanAt = scanNow + (raceLapScanFails <= 0 ? 1f : (raceLapScanFails == 1 ? 5f : 20f));
                    var t = AccessTools.TypeByName("DroneHUDModule.LapTimerDefault");
                    if (t == null)
                    {
                        raceLapScanFails++;
                        return -1f;
                    }
                    var found = UnityEngine.Object.FindObjectsOfType(t, false);
                    if (found == null || found.Length == 0)
                    {
                        raceLapScanFails++;
                        return -1f;
                    }
                    raceLapTimerInst = found[0] as UnityEngine.MonoBehaviour;
                    if (raceLapTimerInst == null)
                    {
                        raceLapScanFails++;
                        return -1f;
                    }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    raceLtMin = t.GetField("txtMinutes", bf);
                    raceLtSec = t.GetField("txtSeconds", bf);
                    raceLtMilli = t.GetField("txtMilliSeconds", bf);
                    if (raceLtMin == null || raceLtSec == null || raceLtMilli == null)
                    {
                        raceLapScanFails++;
                        return -1f;
                    }
                    raceLapScanFails = 0;
                }
                var mins = raceLtMin.GetValue(raceLapTimerInst) as UnityEngine.UI.Text;
                var secs = raceLtSec.GetValue(raceLapTimerInst) as UnityEngine.UI.Text;
                var mill = raceLtMilli.GetValue(raceLapTimerInst) as UnityEngine.UI.Text;
                if (mins == null || secs == null || mill == null)
                    return -1f;
                int mi = ParseLeadingInt(mins.text);
                int si = ParseLeadingInt(secs.text);
                int ms = ParseLeadingInt(mill.text);
                if (mi < 0 || si < 0 || ms < 0)
                    return -1f;
                float div = Mathf.Pow(10f, DigitCount(mill.text));
                if (div <= 0f)
                    div = 100f;
                return mi * 60f + si + ms / div;
            }
            catch
            {
                return -1f;
            }
        }

        private void TickRaceSync()
        {
            // The bound participant's timer is the exact game clock; fall back to the
            // HUD readout only when the event binding isn't available.
            float g = GateSplitsBinding.Subscribed ? GateSplitsBinding.CurrentLapTime() : ReadGameLapSeconds();
            if (g < 0f)
                return;
            raceGameLapTime = g;
            if (GateSplitsBinding.Subscribed)
            {
                // The event path owns lap boundaries, but the clock reset (crossing
                // the finish line) happens between gate events. Close the lap on the
                // frame the clock drops so the best time updates at the line instead
                // of waiting for the next lap's first gate.
                if (raceEventPrevFrameLap > 1f && g < raceEventPrevFrameLap - 0.5f &&
                    raceCurLap != null && !raceEventLapClosed)
                {
                    // Clock reset with an attempt in flight: CloseEventLap keeps it
                    // (finish) or discards it (respawn), so an attempt can never span
                    // a reset. The !raceEventLapClosed guard stops a second reset from
                    // re-recording a lap that was just closed and is still displayed.
                    raceEventLapClosed = CloseEventLap(raceEventPrevFrameLap);
                    raceEventPrevLapTime = -1f;
                }
                raceEventPrevFrameLap = g;
                raceSyncPrevGameLap = g;
                return;
            }
            if (raceCapturePendingAt > 0f)
            {
                // Capture window: keep the last non-zero value the game's lap
                // timer shows before it resets — that IS the game's lap time,
                // exact to what Liftoff displays (≤1 frame of quantization).
                if (g > 0.05f)
                    raceCapturedLap = g;
                bool dropped = raceSyncPrevGameLap >= 0f && raceSyncPrevGameLap > 1.0f && g < raceSyncPrevGameLap - 0.6f;
                if (dropped)
                    DoCaptureClose("game lap clock reset");
                else if (Time.unscaledTime - raceCapturePendingAt > 2.5f)
                    DoCaptureClose("lap clock frozen after last gate");
                raceSyncPrevGameLap = g;
                return;
            }
            if (raceSyncPrevGameLap >= 0f && raceSyncPrevGameLap > 1.0f && g < raceSyncPrevGameLap - 0.6f)
                OnGameLapClockRestarted();
            raceSyncPrevGameLap = g;
            if (raceCurLap != null && raceCurLap.times.Count > 0)
            {
                double nowT = RaceNowTime();
                double impliedT0 = nowT - g;
                if (raceSyncT0 < 0 || System.Math.Abs(nowT - raceSyncT0 - g) > 0.25)
                    raceSyncT0 = impliedT0;
            }
        }

        private void OnGameLapClockRestarted()
        {
            if (raceCurLap == null)
                return;
            if (Time.unscaledTime - raceLastCloseAt < 0.6f)
                return;
            int have = raceCurLap.keys.Count;
            int reqR = raceRequiredCount > 0 ? raceRequiredCount : raceLapGateTotal;
            // Require at least half the route (min 2) so a mid-lap drone reset can't
            // record a bogus lap, while a missed/corrected gate still can't drop one.
            int minGates = reqR > 0 ? Mathf.Max(2, (reqR + 1) / 2) : 2;
            if (have < minGates)
            {
                ProbeLog("gate splits: lap attempt discarded (game lap timer restarted at " + have + " gates)");
                raceCurLap = null;
                raceSyncT0 = -1.0;
                return;
            }
            // The game restarted its lap clock: the loop is complete (END tracks
            // reset the clock at Finish, loop tracks at Start). Close it with the
            // attempt's own timestamps even if the graph wasn't ready or a gate was
            // missed — dropping the lap is worse than an approximate split set.
            CloseFinishedRun("game lap timer restarted");
        }

        private void UpdateRaceRequired()
        {
            raceVisibleIdx.Clear();
            if (raceLapGateTotal <= 0)
            {
                raceRequiredCount = 0;
                return;
            }
            for (int i = 0; i < raceLapGateTotal; i++)
            {
                if (i < raceRowKeys.Count && raceTrimmedKeys.Contains(raceRowKeys[i]))
                    continue;
                raceVisibleIdx.Add(i);
            }
            // Merged Finish (Finish arch == Start arch): the boundary crossing itself
            // covers the Finish row, so it never needs its own trigger.
            raceRequiredCount = raceVisibleIdx.Count - (raceMergedFinish ? 1 : 0);
        }

        private void AlignRowKeys(RaceLapAttempt attempt)
        {
            int need = raceLapGateTotal;
            if (raceMergedFinish && need > 0)
                need--;
            if (need <= 0 || attempt == null || attempt.keys.Count != need)
                return;
            bool changed = raceRowKeys.Count != raceLapGateTotal;
            if (!changed)
            {
                for (int i = 0; i < need; i++)
                    if (raceRowKeys[i] != attempt.keys[i])
                    {
                        changed = true;
                        break;
                    }
            }
            raceRowKeys.Clear();
            raceRowKeys.AddRange(attempt.keys);
            if (raceMergedFinish)
                raceRowKeys.Add(raceRowKeys.Count > 0 ? raceRowKeys[0] : "");
            UpdateRaceRequired();
            RebuildRaceLabels();
        }

        // Row 0 is the Start arch, so row i is gate i (first gate = G01). Every
        // label path (event rows, trigger fallback, race graph, display
        // fallbacks) goes through here so a gate always gets the same number.
        private static string RaceGateLabel(int rowIndex)
        {
            return "G" + rowIndex.ToString("00");
        }

        private void RebuildRaceLabels()
        {
            if (raceRowKeys.Count <= 0)
                return;
            raceGateLabels.Clear();
            for (int i = 0; i < raceRowKeys.Count; i++)
            {
                if (i == 0)
                    raceGateLabels.Add("Start");
                else if (raceFinishKey != null && raceRowKeys[i] == raceFinishKey)
                    raceGateLabels.Add("Finish");
                else
                    raceGateLabels.Add(RaceGateLabel(i));
            }
            raceLapGateTotal = raceRowKeys.Count;
        }

        private static int RaceAttemptIndexOf(RaceLapAttempt a, string key)
        {
            if (a == null || key == null)
                return -1;
            for (int i = 0; i < a.keys.Count; i++)
                if (a.keys[i] == key)
                    return i;
            return -1;
        }

        private void CloseFinishedRun(string reason)
        {
            if (raceCurLap == null)
                return;
            raceFinishKey = raceCurLap.keys[raceCurLap.keys.Count - 1];
            AlignRowKeys(raceCurLap);
            double endT = raceCurLap.times[raceCurLap.times.Count - 1];
            raceLapNumber++;
            ProbeLog("gate splits: run complete " + reason);
            CloseRaceLap(endT);
            raceLastCloseAt = Time.unscaledTime;
            raceCurLap = null;
            raceSyncT0 = -1.0;
        }

        private void OnRaceGateTrigger(RaceCheckpoint cp)
        {
            if (replayReady)
                return;
            // The game's own per-gate event is authoritative when bound; the trigger
            // prefix is only a fallback for builds where the binding fails.
            if (GateSplitsBinding.Subscribed)
                return;
            try
            {
                double now = RaceNowTime();
                string key = RaceGateKey(cp);
                raceSeenKeys.Add(key);
                // Boundary re-cross FIRST — Start is always already in the attempt
                // keys, so the duplicate check below must never swallow it.
                if (raceCurLap != null && raceCurLap.keys.Count > 0 && key == raceCurLap.keys[0])
                {
                    int have = raceCurLap.keys.Count;
                    int req = raceRequiredCount > 0 ? raceRequiredCount : raceLapGateTotal;
                    bool fullLap = raceLapGateTotal > 0 && have >= req;
                    // Crossing back over the Start arch is a completed loop even if
                    // the graph wasn't ready or a gate was missed/corrected — the game
                    // restarts its own lap clock here, and losing laps is worse than
                    // an imperfect split set. Require half the route (min 2) so
                    // respawning onto the start line mid-lap can't record a bogus lap.
                    bool plausibleLap = have >= (req > 0 ? Mathf.Max(2, (req + 1) / 2) : 2);
                    if (fullLap || plausibleLap)
                    {
                        if (raceMergedFinish)
                            raceFinishKey = key;
                        else if (raceFinishKey == null)
                            raceFinishKey = raceCurLap.keys[raceCurLap.keys.Count - 1];
                        AlignRowKeys(raceCurLap);
                        bool useCaptured = raceCapturePendingAt > 0f && raceCapturedLap > 0.05f && raceCaptureHave == have;
                        if (raceMergedFinish && raceGameLapTime >= 0f)
                        {
                            // The Finish IS the Start arch: the game completes the
                            // lap on/after this crossing — defer one tick and close
                            // with the exact value its lap timer displays.
                            raceCapturePendingAt = Time.unscaledTime;
                            raceCaptureHave = have;
                            raceCapturedLap = -1f;
                            raceCaptureEndT = now;
                            raceCaptureNewKey = key;
                            raceCaptureNewTime = now;
                            raceCaptureThenNewLap = true;
                            return;
                        }
                        raceLapNumber++;
                        if (useCaptured)
                            CloseRaceLapForced(raceCapturedLap);
                        else
                            // The Start-arch crossing IS the lap boundary, so use the
                            // crossing time. Using the last captured gate time made
                            // laps with missed/corrected gates read impossibly fast.
                            CloseRaceLap(now);
                        raceCapturePendingAt = -999f;
                        raceLastCloseAt = Time.unscaledTime;
                        raceCurLap = new RaceLapAttempt();
                        raceCurLap.keys.Add(key);
                        raceCurLap.times.Add(now);
                        raceSyncT0 = now;
                    }
                    return;
                }
                if (raceCurLap != null && raceCurLap.keys.Contains(key))
                    return;
                // Route-following acceptance on aligned tracks: forward gates are
                // adopted (skipped rows auto-trimmed, unknown branch gates inserted
                // at the expected slot); backward / re-flown gates are ignored.
                if (raceCurLap != null && raceLapGateTotal > 0 && raceRowKeys.Count == raceLapGateTotal)
                {
                    int keyRow = raceRowKeys.IndexOf(key);
                    int nextExpected = -1;
                    for (int vi = 0; vi < raceVisibleIdx.Count; vi++)
                    {
                        int rIdx = raceVisibleIdx[vi];
                        if (rIdx < raceRowKeys.Count && RaceAttemptIndexOf(raceCurLap, raceRowKeys[rIdx]) < 0)
                        {
                            nextExpected = rIdx;
                            break;
                        }
                    }
                    if (keyRow >= 0)
                    {
                        if (nextExpected < 0 || keyRow < nextExpected)
                        {
                            if (Time.unscaledTime - raceOutOfOrderLogAt > 5f)
                            {
                                raceOutOfOrderLogAt = Time.unscaledTime;
                                ProbeLog("gate splits: ignored backward gate " + (keyRow + 1) + " (next " + (nextExpected >= 0 ? nextExpected + 1 : -1) + ")");
                            }
                            return;
                        }
                        if (keyRow > nextExpected)
                        {
                            // A gate crossed ahead of the expected slot means one or
                            // more were skipped/misflown. Do NOT permanently trim the
                            // route: just record this gate where it was actually hit
                            // (blank splits for the skipped rows) and let the lap
                            // close at the boundary. Trimming used to leave gates
                            // blank for the rest of the session.
                            if (Time.unscaledTime - raceOutOfOrderLogAt > 5f)
                            {
                                raceOutOfOrderLogAt = Time.unscaledTime;
                                ProbeLog("gate splits: gate " + (keyRow + 1) + " crossed ahead of " +
                                    (nextExpected + 1) + " (skipped rows stay blank)");
                            }
                        }
                    }
                    else
                    {
                        // The graph defines the authoritative route. An unknown gate
                        // is a wrong-gate detour (or the graph is stale after a track
                        // switch): never grow the route from live triggers — doing so
                        // corrupted the row list and dropped laps. Reprobes cover the
                        // stale-graph case; skipped rows just stay blank for that lap.
                        if (raceGraphDumped && Time.unscaledTime >= raceGraphReprobeAt)
                        {
                            raceGraphReprobeAt = Time.unscaledTime + 5f;
                            raceGraphDumped = false;
                            raceGraphRetryAt = 0f;
                        }
                        if (Time.unscaledTime - raceOutOfOrderLogAt > 5f)
                        {
                            raceOutOfOrderLogAt = Time.unscaledTime;
                            ProbeLog("gate splits: ignored unknown gate (route has " + raceRowKeys.Count + " rows)");
                        }
                    }
                }
                else if (raceLapGateTotal > 0 && raceRowKeys.Count != raceLapGateTotal &&
                    raceGraphDumped && Time.unscaledTime >= raceGraphReprobeAt)
                {
                    // Route not learned yet and the graph may be stale (same-scene
                    // track switch): re-probe periodically until a lap aligns.
                    raceGraphReprobeAt = Time.unscaledTime + 5f;
                    raceGraphDumped = false;
                    raceGraphRetryAt = 0f;
                }
                if (raceCurLap == null)
                {
                    // With a known route, only the real Start arch begins a lap —
                    // otherwise a gate crossed right after spawn/reset mis-anchors
                    // the attempt and the first real lap is lost.
                    bool routeKnownForStart = raceLapGateTotal > 0 &&
                        raceRowKeys.Count == raceLapGateTotal && raceRowKeys.Count > 0;
                    if (routeKnownForStart && key != raceRowKeys[0])
                    {
                        // The Start arch hasn't fired this lap (e.g. the drone spawns
                        // on the line and the game starts its clock without a fresh
                        // trigger). If the game clock is already running, adopt the
                        // in-progress lap instead of ignoring gates until a full loop.
                        if (raceGameLapTime >= 0.25f)
                        {
                            raceStartKey = raceRowKeys[0];
                            raceCurLap = new RaceLapAttempt();
                            double t0 = RaceNowTime() - raceGameLapTime;
                            raceCurLap.keys.Add(raceStartKey);
                            raceCurLap.times.Add(t0);
                            raceSyncT0 = t0;
                            raceLastCloseAt = Time.unscaledTime;
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        // Start gate is the track's identity: if it moved, the track
                        // changed under the same scene — wipe everything immediately.
                        if (raceStartKey != null && raceStartKey != key)
                        {
                            ProbeLog("gate splits: start gate changed — track changed, resetting splits/best");
                            ResetRaceSplitsForScene();
                        }
                        raceStartKey = key;
                        raceCurLap = new RaceLapAttempt();
                        raceSyncT0 = now;
                        // The game restarts its lap clock at this same crossing on
                        // loop tracks — don't let that drop discard this fresh lap.
                        raceLastCloseAt = Time.unscaledTime;
                    }
                }
                raceCurLap.keys.Add(key);
                raceCurLap.times.Add(now);
                int count = raceCurLap.keys.Count;
                int req2 = raceRequiredCount > 0 ? raceRequiredCount : raceLapGateTotal;
                if (raceLapGateTotal > 0 && count == req2 && !raceMergedFinish)
                {
                    // Last gate passed — the game completes the lap on its own
                    // schedule. Watch its lap timer and close with the EXACT
                    // value it displays (sampled every frame until it resets).
                    raceCapturePendingAt = Time.unscaledTime;
                    raceCaptureHave = count;
                    raceCapturedLap = -1f;
                    raceCaptureEndT = now;
                    return;
                }
                if (raceLapGateTotal > 0)
                    return;
                if (count > raceCycleKeys.Count)
                {
                    raceCycleKeys.Clear();
                    raceCycleKeys.AddRange(raceCurLap.keys);
                }
            }
            catch { }
        }

        // Drops the oldest stored laps once history exceeds the cap, rebasing every
        // index that points into raceLapHistory so the best highlight, graph
        // selection and open editor stay on the same laps (manual delete already
        // rebases; auto-trim used to skip this and drift after lap 50).
        private void TrimRaceHistoryOverflow()
        {
            const int cap = 50;
            while (raceLapHistory.Count > cap)
            {
                raceLapHistory.RemoveAt(0);
                if (raceBestLapHistoryIdx == 0)
                    raceBestLapHistoryIdx = -1;
                else if (raceBestLapHistoryIdx > 0)
                    raceBestLapHistoryIdx--;
                if (raceHistoryEditIdx == 0)
                    raceHistoryEditIdx = -1;
                else if (raceHistoryEditIdx > 0)
                    raceHistoryEditIdx--;
                // raceGraphSel is a HashSet<int>, so rebuild with shifted indices.
                var shiftedSel = new List<int>();
                foreach (int sel in raceGraphSel)
                    if (sel > 0)
                        shiftedSel.Add(sel - 1);
                raceGraphSel.Clear();
                foreach (int sel in shiftedSel)
                    raceGraphSel.Add(sel);
            }
            raceGraphHistoryEpoch++;
        }

        private static bool SplitsPersistenceEnabled()
        {
            return Prefs.gateSplitsConfig == null || Prefs.gateSplitsConfig.Value;
        }

        private static string SplitStorePath()
        {
            try { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "LiftoffReplayPlus.splits.tsv"); }
            catch { return null; }
        }

        // Whole-file store: one block per race key (T/G/L rows). Atomic write via a
        // temp file so an interrupted save can't damage the previous history.
        private static void SaveSplitStore(bool force)
        {
            if (!splitStoreDirty)
                return;
            if (!force && Time.unscaledTime < splitStoreNextSaveAt)
                return;
            splitStoreNextSaveAt = Time.unscaledTime + 2f;
            splitStoreDirty = false;
            try
            {
                string path = SplitStorePath();
                if (path == null)
                    return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder("# LiftoffReplayPlus splits v1\n");
                foreach (var kv in splitStore)
                {
                    PersistedTrack t = kv.Value;
                    if (t.laps.Count == 0 && t.gates.Count == 0)
                        continue;
                    sb.Append("T\t").Append(t.key).Append('\t').Append(t.mergedFinish ? '1' : '0').Append('\n');
                    if (t.gates.Count > 0)
                    {
                        sb.Append('G');
                        for (int i = 0; i < t.gates.Count; i++)
                            sb.Append('\t').Append(t.gates[i]);
                        sb.Append('\n');
                    }
                    for (int li = 0; li < t.laps.Count; li++)
                    {
                        PersistedLap lap = t.laps[li];
                        sb.Append("L\t").Append(lap.number.ToString(inv)).Append('\t')
                          .Append(lap.lapTime.ToString("F4", inv));
                        for (int i = 0; i < lap.keys.Count && i < lap.times.Count; i++)
                            sb.Append('\t').Append(lap.keys[i]).Append('=').Append(lap.times[i].ToString("F4", inv));
                        sb.Append('\n');
                    }
                }
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, sb.ToString());
                if (System.IO.File.Exists(path))
                {
                    try { System.IO.File.Replace(tmp, path, null); }
                    catch
                    {
                        System.IO.File.Delete(path);
                        System.IO.File.Move(tmp, path);
                    }
                }
                else
                {
                    System.IO.File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                splitStoreDirty = true;
                if (pluginLog != null)
                    pluginLog.LogWarning("[GhostLines] splits store save failed: " + ex.Message);
            }
        }

        private static void LoadSplitStore()
        {
            if (splitStoreLoaded)
                return;
            splitStoreLoaded = true;
            try
            {
                string path = SplitStorePath();
                if (path == null || !System.IO.File.Exists(path))
                    return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                PersistedTrack cur = null;
                string[] lines = System.IO.File.ReadAllLines(path);
                for (int li = 0; li < lines.Length; li++)
                {
                    string raw = lines[li];
                    if (string.IsNullOrEmpty(raw) || raw[0] == '#')
                        continue;
                    string[] f = raw.Split('\t');
                    if (f.Length < 2)
                        continue;
                    try
                    {
                        if (f[0] == "T")
                        {
                            cur = new PersistedTrack();
                            cur.key = f[1];
                            cur.mergedFinish = f.Length > 2 && f[2] == "1";
                            splitStore[cur.key] = cur;
                        }
                        else if (f[0] == "G" && cur != null)
                        {
                            cur.gates.Clear();
                            for (int i = 1; i < f.Length; i++)
                                if (!string.IsNullOrEmpty(f[i]))
                                    cur.gates.Add(f[i]);
                        }
                        else if (f[0] == "L" && cur != null && f.Length >= 4)
                        {
                            var lap = new PersistedLap();
                            lap.number = int.Parse(f[1], inv);
                            lap.lapTime = double.Parse(f[2], inv);
                            for (int i = 3; i < f.Length; i++)
                            {
                                int eq = f[i].IndexOf('=');
                                if (eq <= 0)
                                    continue;
                                lap.keys.Add(f[i].Substring(0, eq));
                                lap.times.Add(double.Parse(f[i].Substring(eq + 1), inv));
                            }
                            if (lap.keys.Count > 0)
                                cur.laps.Add(lap);
                        }
                    }
                    catch { }
                }
                if (pluginLog != null)
                    pluginLog.LogInfo("[GhostLines] splits store loaded: " + splitStore.Count + " track(s)");
            }
            catch (Exception ex)
            {
                if (pluginLog != null)
                    pluginLog.LogWarning("[GhostLines] splits store load failed: " + ex.Message);
            }
        }

        // Event path only: mirror the session's (capped) history for this race key
        // plus the confirmed gate list, then write through.
        private void PersistSplitLap()
        {
            if (!SplitsPersistenceEnabled())
                return;
            string key = raceEventRaceKey;
            if (string.IsNullOrEmpty(key) || key == "race")
                return;
            LoadSplitStore();
            PersistedTrack t;
            if (!splitStore.TryGetValue(key, out t))
            {
                t = new PersistedTrack();
                t.key = key;
                splitStore[key] = t;
            }
            t.mergedFinish = raceMergedFinish;
            if (raceEventCompletedLen > 0 && raceRowKeys.Count > 0)
            {
                t.gates.Clear();
                t.gates.AddRange(raceRowKeys);
            }
            t.laps.Clear();
            for (int i = 0; i < raceLapHistory.Count; i++)
            {
                StoredLap s = raceLapHistory[i];
                var pl = new PersistedLap();
                pl.number = s.number;
                pl.lapTime = s.lapTime;
                pl.keys.AddRange(s.attempt.keys);
                pl.times.AddRange(s.attempt.times);
                t.laps.Add(pl);
            }
            splitStoreDirty = true;
            SaveSplitStore(false);
        }

        // Restores this race key's laps, best reference and gate list so the split
        // panel compares against the stored best from the first crossing on.
        private void LoadPersistedTrack(string key)
        {
            if (!SplitsPersistenceEnabled())
                return;
            if (string.IsNullOrEmpty(key) || key == "race")
                return;
            LoadSplitStore();
            PersistedTrack t;
            if (!splitStore.TryGetValue(key, out t))
                return;
            raceLapHistory.Clear();
            raceBestLapHistoryIdx = -1;
            int maxNumber = 0;
            for (int i = 0; i < t.laps.Count; i++)
            {
                PersistedLap pl = t.laps[i];
                var stored = new StoredLap();
                stored.number = pl.number;
                stored.lapTime = pl.lapTime;
                stored.attempt = new RaceLapAttempt();
                stored.attempt.keys.AddRange(pl.keys);
                stored.attempt.times.AddRange(pl.times);
                raceLapHistory.Add(stored);
                if (pl.number > maxNumber)
                    maxNumber = pl.number;
            }
            raceLapNumber = maxNumber;
            TrimRaceHistoryOverflow();
            RecomputeBestFromHistory();
            double bestT = raceBestLapTime;
            int bestI = raceBestLapHistoryIdx;
            if (GateSplitsBinding.Subscribed && raceRowKeys.Count == 0 && t.gates.Count > 0)
            {
                raceRowKeys.AddRange(t.gates);
                raceEventCompletedLen = raceRowKeys.Count;
                raceMergedFinish = t.mergedFinish;
                RebuildEventRows();
            }
            raceGraphHistoryEpoch++;
            if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] splits store: loaded " +
                raceLapHistory.Count + " lap(s) for '" + key + "'" +
                (bestI >= 0 ? " (best " + bestT.ToString("F3") + ")" : ""));
        }

        // Reference lap = fastest remaining stored lap (used after load and after
        // deleting the lap that was the reference).
        private void RecomputeBestFromHistory()
        {
            double bestT = -1.0;
            int bestI = -1;
            for (int i = 0; i < raceLapHistory.Count; i++)
            {
                double lt = raceLapHistory[i].lapTime;
                if (lt > 0.0 && (bestT < 0.0 || lt < bestT))
                {
                    bestT = lt;
                    bestI = i;
                }
            }
            if (bestI >= 0)
            {
                StoredLap b = raceLapHistory[bestI];
                raceBestLap = new RaceLapAttempt();
                raceBestLap.keys.AddRange(b.attempt.keys);
                raceBestLap.times.AddRange(b.attempt.times);
                raceBestLapTime = bestT;
                raceBestLapHistoryIdx = bestI;
            }
            else
            {
                raceBestLap = null;
                raceBestLapTime = -1;
                raceBestLapHistoryIdx = -1;
            }
        }

        // Drops this track's saved laps/route (file + memory) and relearns the route
        // from actual crossings — for when a stored or graph-derived route is wrong.
        private void ResetRaceRoute()
        {
            LoadSplitStore();
            string key = raceEventRaceKey;
            bool removed = !string.IsNullOrEmpty(key) && splitStore.Remove(key);
            ResetRaceSplitsForScene();
            // Keep the key so the next gate doesn't look like a race switch.
            raceEventRaceKey = key;
            // Relearn from flight only: the graph-derived count may be the wrong part.
            raceGraphGateCount = 0;
            raceEventCompletedLen = 0;
            raceGraphDumped = true;
            if (removed)
            {
                splitStoreDirty = true;
                SaveSplitStore(true);
            }
            if (ModLog.DebugEnabled) GhostLinesPlugin.LogGateSplitsMessage("[GhostLines] splits store: route reset" +
                (removed ? " (saved data dropped)" : " (nothing saved)") +
                " for '" + (key ?? "?") + "'");
        }

        private void CloseRaceLap(double endTime)
        {
            // Use the lap's own first-gate timestamp — immune to game-lap-clock
            // re-anchoring that happens mid-cycle on END tracks (Finish resets the clock).
            StoreClosedRaceLap(endTime - raceCurLap.times[0], "");
        }

        private void CloseRaceLapForced(double lapSeconds)
        {
            // Lap time taken directly from the game's own lap timer readout.
            StoreClosedRaceLap(lapSeconds, " (game timer)");
        }

        private void StoreClosedRaceLap(double lapT, string source)
        {
            raceLastDoneLap = raceCurLap;
            raceLastT0 = raceCurLap.times[0];
            bool improved = raceBestLapTime < 0 || lapT < raceBestLapTime;
            var stored = new StoredLap();
            stored.number = raceLapNumber;
            stored.lapTime = lapT;
            stored.attempt = new RaceLapAttempt();
            stored.attempt.keys.AddRange(raceCurLap.keys);
            stored.attempt.times.AddRange(raceCurLap.times);
            raceLapHistory.Add(stored);
            TrimRaceHistoryOverflow();
            if (improved)
            {
                raceBestLap = new RaceLapAttempt();
                raceBestLap.keys.AddRange(raceCurLap.keys);
                raceBestLap.times.AddRange(raceCurLap.times);
                raceBestLapTime = lapT;
                raceBestLapHistoryIdx = raceLapHistory.Count - 1;
            }
            ProbeLog("gate splits: lap " + raceLapNumber + " complete " + lapT.ToString("F2") + "s" + source +
                (improved ? " (best)" : "") + " gates=" + raceCurLap.keys.Count);
        }

        private void DoCaptureClose(string reason)
        {
            raceCapturePendingAt = -999f;
            if (raceCurLap == null || raceCurLap.keys.Count != raceCaptureHave)
            {
                // The attempt changed since the capture started (already closed
                // by the boundary) — just handle the deferred new lap if queued.
                raceCaptureThenNewLap = false;
                return;
            }
            if (raceFinishKey == null)
                raceFinishKey = raceCurLap.keys[raceCurLap.keys.Count - 1];
            AlignRowKeys(raceCurLap);
            raceLapNumber++;
            if (raceCapturedLap > 0.05f)
                CloseRaceLapForced(raceCapturedLap);
            else
                CloseRaceLap(raceCaptureEndT);
            ProbeLog("gate splits: closed via " + reason +
                (raceCapturedLap > 0.05f ? " captured=" + raceCapturedLap.ToString("F2") + "s" : " (no capture, fallback)"));
            raceLastCloseAt = Time.unscaledTime;
            raceCurLap = null;
            raceSyncT0 = -1.0;
            if (raceCaptureThenNewLap)
            {
                raceCaptureThenNewLap = false;
                raceCurLap = new RaceLapAttempt();
                raceCurLap.keys.Add(raceCaptureNewKey);
                raceCurLap.times.Add(raceCaptureNewTime);
                raceSyncT0 = raceCaptureNewTime;
            }
        }
    }
}
