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
        private void MaybePatchRaceProbes()
        {
            // The event binding is the preferred capture path; retry its HUD patch
            // until it resolves (Assembly-CSharp may load late).
            if (!GateSplitsBinding.Resolved)
                GhostLinesPlugin.TryPatchGateSplitsHud();
            if (PatchRegistry.RaceTriggerPatched && PatchRegistry.RaceLapTimerPatched)
                return;
            // Retry on a backoff: the target types may not be resolvable on the
            // very first probe. Setting the guard before success used to disable
            // gate capture / lap timers for the whole session on a transient miss.
            if (Time.unscaledTime < racePatchRetryAt)
                return;
            racePatchRetryAt = Time.unscaledTime + 2f;
            if (!PatchRegistry.RaceTriggerPatched)
            {
                try
                {
                    var m = AccessTools.Method(AccessTools.TypeByName("RaceCheckpoint"), "Trigger");
                    if (m != null)
                    {
                        GhostLinesPlugin.harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(GhostLinePoller), nameof(RaceCheckpointTriggerPrefix))));
                        PatchRegistry.RaceTriggerPatched = true;
                        pluginLog.LogInfo("[RACE] patched RaceCheckpoint.Trigger");
                    }
                    else
                        pluginLog.LogInfo("[RACE] RaceCheckpoint.Trigger not found");
                }
                catch (Exception ex) { pluginLog.LogInfo("[RACE] trigger patch error: " + ex.Message); }
            }
            if (!PatchRegistry.RaceLapTimerPatched)
            {
                try
                {
                    var t = AccessTools.TypeByName("DroneHUDModule.LapTimerDefault");
                    bool patchedAny = false;
                    if (t != null)
                    {
                        foreach (var m in AccessTools.GetDeclaredMethods(t))
                        {
                            if (m.Name != "SetTimer")
                                continue;
                            var ps = m.GetParameters();
                            if (ps.Length == 1)
                            {
                                GhostLinesPlugin.harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(GhostLinePoller), nameof(LapTimerSetTimer1Prefix))));
                                patchedAny = true;
                                PatchRegistry.RaceLapTimerPatched = true;
                            }
                            else if (ps.Length == 2)
                            {
                                GhostLinesPlugin.harmony.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(GhostLinePoller), nameof(LapTimerSetTimer2Prefix))));
                                patchedAny = true;
                                PatchRegistry.RaceLapTimerPatched = true;
                            }
                        }
                    }
                    pluginLog.LogInfo("[RACE] lap timer SetTimer hooks: " + (patchedAny ? "ok" : "not found"));
                }
                catch (Exception ex)
                {
                    pluginLog.LogInfo("[RACE] lap timer patch error: " + ex.Message);
                }
            }
        }

        private static System.Reflection.FieldInfo raceCpSubIdField = null;

        private static void RaceCheckpointTriggerPrefix(RaceCheckpoint __instance, object __0)
        {
            var p = poller;
            if (p == null)
                return;
            // Event-driven capture is active; skip the fallback trigger path (and its
            // per-trigger logging) entirely.
            if (GateSplitsBinding.Subscribed)
                return;
            try
            {
                if (raceCpSubIdField == null)
                    raceCpSubIdField = AccessTools.Field(typeof(RaceCheckpoint), "subID");
                string subId = raceCpSubIdField != null ? (raceCpSubIdField.GetValue(__instance) as string) : null;
                int id = __instance.GetInstanceID();
                float now = Time.unscaledTime;
                float last;
                p.raceTrigLastAt.TryGetValue(id, out last);
                if (now - last < 0.25f)
                    return;
                p.raceTrigLastAt[id] = now;
                bool inRoom = false;
                double ptime = -1;
                try
                {
                    inRoom = Photon.Pun.PhotonNetwork.InRoom;
                    if (inRoom)
                        ptime = Photon.Pun.PhotonNetwork.Time;
                }
                catch { }
                pluginLog.LogInfo("[RACE] gate trigger subID='" + (subId ?? "") +
                    "' cp=" + id +
                    " pos=" + __instance.transform.position.ToString("F1") +
                    " t=" + now.ToString("F3") +
                    (inRoom ? " photon=" + ptime.ToString("F3") : "") +
                    " arg=" + (__0 != null ? __0.GetType().Name : "null"));
                p.OnRaceGateTrigger(__instance);
                if (__0 != null && p.raceTrigDumpedTypes.Add(__0.GetType().FullName ?? ""))
                    p.DumpObjectFields("[RACE] trigger-arg", __0);
                if (!p.raceItemDumped)
                {
                    var f = AccessTools.Field(__instance.GetType(), "attachedTrackItem");
                    var v = f != null ? f.GetValue(__instance) : null;
                    if (v != null)
                    {
                        p.raceItemDumped = true;
                        p.DumpObjectFields("[RACE] attachedTrackItem", v);
                    }
                }
            }
            catch { }
        }

        private static void LapTimerSetTimer1Prefix(object __instance, object __0)
        {
            LapTimerSetTimerCommon(__instance, __0, null);
        }

        private static void LapTimerSetTimer2Prefix(object __instance, object __0, object __1)
        {
            LapTimerSetTimerCommon(__instance, __0, __1);
        }

        private static void LapTimerSetTimerCommon(object inst, object t0, object t1)
        {
            var p = poller;
            if (p == null)
                return;
            try
            {
                p.CaptureLapTimerInstance(inst);
                if (t0 != null && p.raceLapDumpedTypes.Add(t0.GetType().FullName ?? ""))
                    p.DumpObjectFields("[RACE] laptimer-time-struct", t0);
                float now = Time.unscaledTime;
                if (now < p.raceLapNextLogAt)
                    return;
                p.raceLapNextLogAt = now + 1f;
                pluginLog.LogInfo("[RACE] lap SetTimer(" + RacePayloadShort(t0) + (t1 != null ? ", " + RacePayloadShort(t1) : "") + ")");
            }
            catch { }
        }

        private void CaptureLapTimerInstance(object inst)
        {
            try
            {
                if (inst == null)
                    return;
                if (raceLapTimerInst != null && raceLapTimerInst.Equals(null))
                    raceLapTimerInst = null;
                if (raceLapTimerInst != null)
                    return;
                var mb = inst as UnityEngine.MonoBehaviour;
                if (mb == null)
                    return;
                raceLapTimerInst = mb;
                var t = mb.GetType();
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                raceLtMin = t.GetField("txtMinutes", bf);
                raceLtSec = t.GetField("txtSeconds", bf);
                raceLtMilli = t.GetField("txtMilliSeconds", bf);
                raceLapScanFails = 0;
            }
            catch { }
        }

        private void DumpObjectFields(string tag, object inst)
        {
            try
            {
                if (inst == null)
                    return;
                var t = inst.GetType();
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var sb = new System.Text.StringBuilder("[RACE] " + tag + " '" + t.Name + "':");
                foreach (var f in t.GetFields(bf))
                {
                    if (f.IsStatic)
                        continue;
                    object v = null;
                    try { v = f.GetValue(inst); } catch { continue; }
                    if (v == null)
                    {
                        sb.Append(' ').Append(f.Name).Append("=null");
                        continue;
                    }
                    var vt = v.GetType();
                    if (vt.IsPrimitive || v is string || v is System.Enum || v is decimal)
                        sb.Append(' ').Append(f.Name).Append('=').Append(v);
                    else if (v is System.Collections.ICollection col)
                        sb.Append(' ').Append(f.Name).Append(".count=").Append(col.Count);
                    else
                        sb.Append(' ').Append(f.Name).Append(':').Append(vt.Name);
                }
                pluginLog.LogInfo(sb.ToString());
            }
            catch { }
        }

        private static object RaceGetMember(object o, string name)
        {
            try
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var t = o.GetType();
                var f = t.GetField(name, bf);
                if (f != null)
                    return f.GetValue(o);
                var p = t.GetProperty(name, bf);
                if (p != null)
                    return p.GetValue(o, null);
            }
            catch { }
            return null;
        }

        private void TryExtractRaceGraph()
        {
            if (raceGraphDumped)
                return;
            try
            {
                var mbs = UnityEngine.Object.FindObjectsOfType<UnityEngine.MonoBehaviour>(false);
                UnityEngine.MonoBehaviour holder = null;
                foreach (var mb in mbs)
                {
                    if (mb != null && mb.GetType().Name == "CurrentContentContainer")
                    {
                        holder = mb;
                        break;
                    }
                }
                if (holder == null)
                {
                    TryGraphFail("CurrentContentContainer not found");
                    return;
                }
                object raceObj = RaceGetMember(holder, "Race");
                if (raceObj == null)
                {
                    TryGraphFail("Race member not found");
                    return;
                }
                System.Collections.IEnumerable passages = null;
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                foreach (var f in raceObj.GetType().GetFields(bf))
                {
                    object v = null;
                    try { v = f.GetValue(raceObj); } catch { continue; }
                    if (v is System.Collections.IEnumerable en && !(v is string))
                    {
                        foreach (var it in en)
                        {
                            if (it is RaceCheckpointPassage)
                            {
                                passages = en;
                                break;
                            }
                        }
                    }
                    if (passages != null)
                        break;
                }
                if (passages == null)
                {
                    TryGraphFail("no passage collection found on race object");
                    return;
                }
                var list = new List<RaceCheckpointPassage>();
                foreach (var it in passages)
                {
                    var ps = it as RaceCheckpointPassage;
                    if (ps != null)
                        list.Add(ps);
                }
                var byId = new Dictionary<string, RaceCheckpointPassage>(StringComparer.Ordinal);
                var referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ps in list)
                {
                    string pid = RaceGetMember(ps, "passageId") as string;
                    var nexts = RaceGetMember(ps, "nextPassageIDs") as System.Collections.Generic.List<string>;
                    if (!string.IsNullOrEmpty(pid))
                        byId[pid] = ps;
                    if (nexts != null)
                        foreach (var nx in nexts)
                            referenced.Add(nx);
                }
                RaceCheckpointPassage start = null;
                foreach (var ps in list)
                {
                    string pid = RaceGetMember(ps, "passageId") as string;
                    var nexts = RaceGetMember(ps, "nextPassageIDs") as System.Collections.Generic.List<string>;
                    if (!string.IsNullOrEmpty(pid) && !referenced.Contains(pid) && nexts != null && nexts.Count > 0)
                    {
                        start = ps;
                        break;
                    }
                }
                if (start == null)
                {
                    TryGraphFail("no start passage found");
                    return;
                }
                var chain = new List<string>();
                var labels = new List<string>();
                var cur2 = start;
                int guard = 0;
                bool reachedFinish = false;
                int firstCpId = RaceGetMember(start, "checkPointID") is int fiv ? fiv : int.MinValue;
                int lastCpId = int.MinValue;
                // Some custom races have no separate Finish passage: the chain loops
                // back to the Start arch. Track visited passages so that closure is
                // recognised as the (merged) finish instead of walking in circles.
                string startPassageId = RaceGetMember(start, "passageId") as string;
                var visitedPassages = new HashSet<string>(System.StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(startPassageId))
                    visitedPassages.Add(startPassageId);
                while (cur2 != null && guard++ < 1024)
                {
                    object cpType = RaceGetMember(cur2, "passageType");
                    int cpId = RaceGetMember(cur2, "checkPointID") is int iv ? iv : -1;
                    string typeStr = cpType != null ? cpType.ToString() : "";
                    chain.Add("cp" + cpId + (typeStr.Length > 0 ? "(" + typeStr + ")" : ""));
                    bool isStart = typeStr == "Start";
                    bool isFinish = typeStr == "Finish";
                    lastCpId = cpId;
                    // Row 0 is always "Start", matching the other label paths.
                    labels.Add(isStart || labels.Count == 0 ? "Start" : isFinish ? "Finish" : RaceGateLabel(labels.Count));
                    if (isFinish)
                    {
                        reachedFinish = true;
                        break;
                    }
                    var nexts = RaceGetMember(cur2, "nextPassageIDs") as System.Collections.Generic.List<string>;
                    string nxt = nexts != null && nexts.Count > 0 ? nexts[0] : null;
                    if (nxt == null)
                        break;
                    if (nxt == startPassageId)
                    {
                        // Loop closed on the Start arch: merged finish.
                        reachedFinish = true;
                        lastCpId = firstCpId;
                        labels.Add("Finish");
                        chain.Add("cp" + firstCpId + "(Finish)");
                        break;
                    }
                    if (!visitedPassages.Add(nxt))
                    {
                        TryGraphFail("chain looped without reaching Finish (visited=" +
                            visitedPassages.Count + ")");
                        return;
                    }
                    cur2 = byId.TryGetValue(nxt, out var np) ? np : null;
                }
                if (!reachedFinish || labels.Count < 2)
                {
                    TryGraphFail("chain did not reach Finish (nodes=" + labels.Count + ")");
                    return;
                }
                bool mergedFinish = firstCpId != int.MinValue && firstCpId == lastCpId;
                string newSig = string.Join(">", labels);
                string oldSig = raceLapGateTotal > 0 && raceGateLabels.Count > 0 ? string.Join(">", raceGateLabels) : null;
                bool trackChanged = oldSig != null && oldSig != newSig;
                pluginLog.LogInfo("[RACE] graph chain: " + string.Join(" -> ", chain) + " | gates=" + labels.Count +
                    (mergedFinish ? " (merged finish=start)" : "") +
                    (trackChanged ? " (TRACK CHANGED)" : ""));
                // Event-path capture owns the route and labels; from the graph only
                // the expected gate count is read (finish-vs-reset detection).
                if (GateSplitsBinding.Subscribed)
                {
                    raceGraphGateCount = labels.Count;
                    raceGraphDumped = true;
                    raceGraphTries = 0;
                    return;
                }
                if (trackChanged)
                {
                    ProbeLog("gate splits: track changed — resetting splits/best");
                    ResetRaceSplitsForScene();
                }
                raceGateLabels.Clear();
                raceGateLabels.AddRange(labels);
                raceLapGateTotal = labels.Count;
                raceGraphGateCount = labels.Count;
                raceMergedFinish = mergedFinish;
                raceGraphDumped = true;
                raceGraphTries = 0;
                UpdateRaceRequired();
            }
            catch (Exception ex)
            {
                pluginLog.LogInfo("[RACE] graph error: " + ex.Message);
                TryGraphFail("error: " + ex.Message);
            }
        }

        private void TryGraphFail(string reason)
        {
            raceGraphTries++;
            if (raceGraphTries <= 3 || raceGraphTries % 10 == 0)
                pluginLog.LogInfo("[RACE] graph retry " + raceGraphTries + ": " + reason);
            if (raceGraphTries >= 60)
            {
                raceGraphDumped = true;
                ProbeLog("gate splits: race graph unavailable, using learned route only");
                return;
            }
            raceGraphRetryAt = Time.unscaledTime + 2f;
        }
    }
}
