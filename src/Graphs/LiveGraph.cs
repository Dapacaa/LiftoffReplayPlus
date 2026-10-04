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
        private GhostLineEntry PickLiveGraphPilot()
        {
            if (spectatedEntry != null)
                return spectatedEntry;
            for (int i = 0; i < GhostLinesPlugin.entries.Count; i++)
            {
                var entry = GhostLinesPlugin.entries[i];
                if (entry.GetRecording() != null)
                    return entry;
            }
            for (int i = 0; i < recordingCandidates.Count; i++)
            {
                var candidate = recordingCandidates[i];
                if (candidate.GetRecording() != null && candidate.GetRecording().states != null)
                    return candidate;
            }
            if (GhostLinesPlugin.entries.Count > 0)
                return GhostLinesPlugin.entries[0];
            if (recordingCandidates.Count > 0)
                return recordingCandidates[0];
            return null;
        }

        private MultiplayerPipTarget PickLiveGraphMpTarget()
        {
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                MultiplayerPipTarget target = multiplayerPipTargets[i];
                if (target == null || target.root == null)
                    continue;
                EnsureMpTargetSyncAccess(target);
                if (target.syncComponent != null)
                    return target;
            }
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                if (multiplayerPipTargets[i] != null && multiplayerPipTargets[i].root != null)
                    return multiplayerPipTargets[i];
            }
            return null;
        }

        private bool IsMpLiveGraphOpen(MultiplayerPipTarget t)
        {
            for (int i = 0; i < liveInputGraphs.Count; i++)
            {
                if (liveInputGraphs[i].mpTarget == t)
                    return true;
            }
            return false;
        }

        private void SetMpLiveGraphOpen(MultiplayerPipTarget t, bool open)
        {
            if (t == null)
                return;
            bool isOpen = IsMpLiveGraphOpen(t);
            if (open == isOpen)
                return;
            if (open)
            {
                OpenLiveGraphFor(t);
                return;
            }
            for (int i = liveInputGraphs.Count - 1; i >= 0; i--)
            {
                if (liveInputGraphs[i].mpTarget == t)
                {
                    LiveGraphState existing = liveInputGraphs[i];
                    liveInputGraphs.RemoveAt(i);
                    if (existing.traceTex != null)
                        Destroy(existing.traceTex);
                }
            }
        }

        private void OpenLiveGraphFor(MultiplayerPipTarget target)
        {
            for (int i = liveInputGraphs.Count - 1; i >= 0; i--)
            {
                if (liveInputGraphs[i].mpTarget == target)
                {
                    LiveGraphState existing = liveInputGraphs[i];
                    liveInputGraphs.RemoveAt(i);
                    liveInputGraphs.Add(existing);
                    existing.fadeAmount = 1f;
                    return;
                }
            }

            LiveGraphState state = new LiveGraphState();
            state.mpTarget = target;
            state.mpPilotName = target != null ? target.playerName : null;
            state.fadeAmount = 1f;
            int count = liveInputGraphs.Count;
            state.rect = new Rect(
                Screen.width * 0.5f - state.rect.width * 0.5f + count * STICK_GRAPH_OPEN_OFFSET,
                Screen.height * 0.5f - state.rect.height * 0.5f + count * STICK_GRAPH_OPEN_OFFSET,
                state.rect.width,
                state.rect.height);
            liveInputGraphs.Add(state);
        }

        private void SetLiveGraphOpen(bool open)
        {
            if (open)
            {
                if (liveInputGraphs.Count > 0)
                    return;
                MultiplayerPipTarget target = PickLiveGraphMpTarget();
                if (target != null)
                {
                    OpenLiveGraphFor(target);
                    return;
                }
                GhostLineEntry pilot = PickLiveGraphPilot();
                if (pilot == null)
                {
                    OpenLiveGraphFor((GhostLineEntry)null);
                    return;
                }
                OpenLiveGraphFor(pilot);
            }
            else
            {
                for (int i = liveInputGraphs.Count - 1; i >= 0; i--)
                {
                    if (liveInputGraphs[i].traceTex != null)
                        Destroy(liveInputGraphs[i].traceTex);
                }
                liveInputGraphs.Clear();
            }
        }

        private void OpenLiveGraphFor(GhostLineEntry pilot)
        {
            for (int i = liveInputGraphs.Count - 1; i >= 0; i--)
            {
                if (liveInputGraphs[i].pilot == pilot)
                {
                    LiveGraphState existing = liveInputGraphs[i];
                    liveInputGraphs.RemoveAt(i);
                    liveInputGraphs.Add(existing);
                    existing.fadeAmount = 1f;
                    return;
                }
            }

            LiveGraphState state = new LiveGraphState();
            state.pilot = pilot;
            state.fadeAmount = 1f;
            int count = liveInputGraphs.Count;
            state.rect = new Rect(
                Screen.width * 0.5f - state.rect.width * 0.5f + count * STICK_GRAPH_OPEN_OFFSET,
                Screen.height * 0.5f - state.rect.height * 0.5f + count * STICK_GRAPH_OPEN_OFFSET,
                state.rect.width,
                state.rect.height);
            liveInputGraphs.Add(state);
        }

        private float SampleGraphChannel(LiveGraphState g, float stateTime, int channel)
        {
            if (g.mpTarget != null || g.mpSampleCount > 1)
                return SampleMpChannel(g, stateTime, channel);
            return SampleLiveChannel(g.pilot, stateTime, channel);
        }

        private float SampleMpChannel(LiveGraphState g, float stateTime, int channel)
        {
            bool gap;
            return SampleMpChannelGap(g, stateTime, channel, out gap);
        }

        // Live MP input samples kept per graph (~200 s at 20 Hz).
        private const int MP_SAMPLE_RING = 4096;
        private const float MP_SAMPLE_GAP_SECONDS = 0.5f;
        private const float MP_LIVE_AUTO_SWITCH_SECONDS = 5f;
        private const float MP_TARGET_RETAIN_SECONDS = 8f;

        private float SampleMpChannelGap(LiveGraphState g, float stateTime, int channel, out bool gap)
        {
            gap = false;
            int count = g.mpSampleCount;
            if (count < 1)
            {
                gap = true;
                return 0f;
            }
            float target = Time.unscaledTime - stateTime;
            int head = g.mpSampleHead;
            int baseIdx = (head - count + MP_SAMPLE_RING) % MP_SAMPLE_RING;
            if (target <= g.mpSampleTimes[baseIdx])
                return InputSampler.Channel(g.mpSamples[baseIdx], channel);
            int newestIdx = (head - 1 + MP_SAMPLE_RING) % MP_SAMPLE_RING;
            if (target >= g.mpSampleTimes[newestIdx])
            {
                gap = Time.unscaledTime - g.mpSampleTimes[newestIdx] > MP_SAMPLE_GAP_SECONDS;
                return InputSampler.Channel(g.mpSamples[newestIdx], channel);
            }
            int low = 0;
            int high = count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                int idxMid = (baseIdx + mid) % MP_SAMPLE_RING;
                if (g.mpSampleTimes[idxMid] <= target)
                    low = mid;
                else
                    high = mid - 1;
            }
            int idxLo = (baseIdx + low) % MP_SAMPLE_RING;
            int idxHi = (baseIdx + low + 1) % MP_SAMPLE_RING;
            float tLo = g.mpSampleTimes[idxLo];
            float tHi = g.mpSampleTimes[idxHi];
            gap = tHi - tLo > MP_SAMPLE_GAP_SECONDS;
            float f = (tHi > tLo) ? Mathf.Clamp01((target - tLo) / (tHi - tLo)) : 1f;
            return Mathf.Lerp(
                InputSampler.Channel(g.mpSamples[idxLo], channel),
                InputSampler.Channel(g.mpSamples[idxHi], channel),
                f);
        }

        private float SampleGraphChannelGap(LiveGraphState g, float stateTime, int channel, out bool gap)
        {
            if (g.mpTarget != null || g.mpSampleCount > 1)
                return SampleMpChannelGap(g, stateTime, channel, out gap);
            gap = false;
            return SampleLiveChannel(g.pilot, stateTime, channel);
        }

        // Scrolling trace: the newest sample at the right edge, `history` seconds
        // back at the left; columns across a data gap are drawn red.
        private void BuildLiveTraceTexture(LiveGraphState g, int tw, int th, int history)
        {
            Color32 signal = GetLiveGraphSignalColor(g);
            Color32 gapColor = new Color(0.95f, 0.25f, 0.25f);
            int channel = g.channel;
            TraceTexture.Build(ref g.traceTex, ref g.tracePixels, tw, th, 1f / tw,
                (float f, out Color32 color) =>
                {
                    bool gap;
                    float value = SampleGraphChannelGap(g, history * (1f - f), channel, out gap);
                    color = gap ? gapColor : signal;
                    return value;
                });
            g.traceLastBuildTime = Time.unscaledTime;
            g.traceCachePilot = g.pilot;
            g.traceCacheMpTarget = g.mpTarget;
            g.traceCacheChannel = g.channel;
            g.traceCachePlotW = tw;
            g.traceCachePlotH = th;
            g.traceCacheHistory = history;
        }

        private void ResetMpGraphData(LiveGraphState g)
        {
            g.mpSampleCount = 0;
            g.mpSampleHead = 0;
            g.lastMpSampleTime = 0f;
            g.mpColor = Color.clear;
            ResetMpGraphSync(g);
        }

        private void ResetMpGraphSync(LiveGraphState g)
        {
            g.inputLogged = false;
            g.mpReadSuccess = 0;
            g.mpReadFail = 0;
            g.mpSettleDone = false;
            g.traceCacheMpTarget = null;
            g.traceCacheStateCount = 0;
        }

        private void CaptureMpSamples(LiveGraphState g)
        {
            if (g.mpTarget == null)
                return;
            if (g.mpTarget.syncComponent == null)
            {
                EnsureMpTargetSyncAccess(g.mpTarget);
                return;
            }
            if (g.mpTarget.root != g.mpTarget.syncRoot || g.mpTarget.photonView != g.mpTarget.syncView)
            {
                g.mpTarget.syncComponent = null;
                g.mpTarget.lastSyncAttempt = 0f;
                g.mpReadFail = 0;
                return;
            }
            if (!g.mpSettleDone && g.mpTarget.resolvedAt > 0f &&
                Time.unscaledTime - g.mpTarget.resolvedAt > 2f)
            {
                g.mpSettleDone = true;
                g.mpTarget.syncComponent = null;
                g.mpTarget.lastSyncAttempt = 0f;
                return;
            }
            float now = Time.unscaledTime;
            if (now - g.lastMpSampleTime < 0.05f)
                return;
            g.lastMpSampleTime = now;
            if (g.mpColor.a == 0f)
            {
                g.mpColor = g.mpTarget.pilotColor;
            }
            Vector4 input;
            if (!TryGetMpInput(g.mpTarget, out input))
            {
                g.mpReadFail++;
                if (g.mpReadFail >= 3)
                {
                    g.mpReadFail = 0;
                    g.mpTarget.syncComponent = null;
                    g.mpTarget.lastSyncAttempt = 0f;
                }
                return;
            }
            g.mpReadSuccess++;
            if (!g.inputLogged)
            {
                g.inputLogged = true;
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC firstInput name='" + g.mpTarget.playerName +
                    "' x=" + input.x.ToString("F2") + " y=" + input.y.ToString("F2") +
                    " z=" + input.z.ToString("F2") + " w=" + input.w.ToString("F2"));
            }
            if (g.mpSamples == null)
            {
                g.mpSamples = new Vector4[MP_SAMPLE_RING];
                g.mpSampleTimes = new float[MP_SAMPLE_RING];
            }
            g.mpSamples[g.mpSampleHead] = input;
            g.mpSampleTimes[g.mpSampleHead] = now;
            g.mpSampleHead = (g.mpSampleHead + 1) % MP_SAMPLE_RING;
            if (g.mpSampleCount < MP_SAMPLE_RING)
                g.mpSampleCount++;
        }

        private float SampleLiveChannel(GhostLineEntry entry, float stateTime, int channel)
        {
            var recording = entry.GetRecording();
            if (recording == null || recording.states == null || recording.states.Count < 2)
                return 0f;
            var states = recording.states;
            int count = states.Count;
            if (count == 0)
                return 0f;

            float lastT = states[count - 1].timestamp;
            float firstT = states[0].timestamp;
            float target = lastT - stateTime;
            if (target <= firstT)
                return InputSampler.Channel(states[0].input, channel);
            if (target >= lastT)
                return InputSampler.Channel(states[count - 1].input, channel);

            int low = 0;
            int high = count - 1;
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (states[mid].timestamp <= target) low = mid;
                else high = mid;
            }

            float t0 = states[low].timestamp;
            float t1 = states[high].timestamp;
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, target);
            Vector4 input = Vector4.LerpUnclamped(states[low].input, states[high].input, u);
            return InputSampler.Channel(input, channel);
        }

        private void EnsureMpTargetSyncAccess(MultiplayerPipTarget target)
        {
            if (target == null || target.root == null)
                return;
            if (target.syncComponent != null)
                return;
            if (Time.unscaledTime - target.lastSyncAttempt < 3f)
                return;
            target.lastSyncAttempt = Time.unscaledTime;
            Component observed = FindObservedSyncComponent(target.photonView);
            if (observed != null)
            {
                System.Reflection.FieldInfo stateField = FindPlaybackStateField(observed.GetType());
                System.Reflection.FieldInfo containerField = null;
                if (stateField == null)
                {
                    System.Reflection.FieldInfo[] fields = null;
                    try
                    {
                        fields = observed.GetType().GetFields(System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    }
                    catch { }
                    if (fields != null)
                    {
                        for (int fi = 0; fi < fields.Length; fi++)
                        {
                            if (fields[fi] == null || fields[fi].FieldType == null)
                                continue;
                            System.Reflection.FieldInfo nested = FindPlaybackStateField(fields[fi].FieldType);
                            if (nested != null)
                            {
                                containerField = fields[fi];
                                stateField = nested;
                                break;
                            }
                        }
                    }
                }
                System.Reflection.FieldInfo inputField = stateField != null ? FindInputField(stateField.FieldType) : null;
                if (stateField != null && inputField != null)
                {
                    target.syncComponent = observed;
                    target.playbackStateField = stateField;
                    target.containerField = containerField;
                    target.inputField = inputField;
                    target.resolvedAt = Time.unscaledTime;
                    target.syncRoot = target.root;
                    target.syncView = target.photonView;
                    if (target.lastVerifiedComponent != (object)observed)
                    {
                        target.lastVerifiedComponent = observed;
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC ok(observed) name='" + target.playerName + "' comp=" +
                            observed.GetType().Name + " stateField=" + stateField.Name + " inputField=" + inputField.Name +
                            " nested=" + (containerField != null));
                    }
                    return;
                }
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC observed-comp-no-input name='" + target.playerName +
                    "' comp=" + observed.GetType().Name + " hasState=" + (stateField != null));
            }
            if (ResolveMpTargetBySceneMatch(target))
                return;
            Component[] comps = target.root.GetComponentsInChildren<Component>(true);
            if (comps == null || comps.Length == 0)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC fail name='" + target.playerName +
                    "' root=" + target.root.name + " rootRoot=" + GetGoPath(target.root) + " comps=0");
                return;
            }
            foreach (Component comp in comps)
            {
                if (comp == null)
                    continue;
                System.Type t = comp.GetType();
                if (t == null)
                    continue;
                if (!IsPhotonSyncComponent(t))
                    continue;
                System.Reflection.FieldInfo stateField = FindPlaybackStateField(t);
                if (stateField == null)
                    continue;
                System.Reflection.FieldInfo inputField = FindInputField(stateField.FieldType);
                if (inputField == null)
                    continue;
                target.syncComponent = comp;
                target.playbackStateField = stateField;
                target.inputField = inputField;
                target.resolvedAt = Time.unscaledTime;
                target.syncRoot = target.root;
                target.syncView = target.photonView;
                if (target.lastVerifiedComponent != (object)comp)
                {
                    target.lastVerifiedComponent = comp;
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC ok name='" + target.playerName + "' comp=" + t.Name +
                        " stateField=" + stateField.Name + " inputField=" + inputField.Name);
                }
                return;
            }
            foreach (Component comp in comps)
            {
                if (comp == null)
                    continue;
                System.Type t = comp.GetType();
                if (t == null)
                    continue;
                System.Reflection.FieldInfo stateField = t.GetField("state",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (stateField == null)
                    stateField = t.GetField("playbackState",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                if (stateField == null || stateField.FieldType.Name != "PlaybackState")
                    continue;
                System.Reflection.FieldInfo inputField = FindInputField(stateField.FieldType);
                if (inputField == null)
                    continue;
                target.syncComponent = comp;
                target.playbackStateField = stateField;
                target.inputField = inputField;
                target.resolvedAt = Time.unscaledTime;
                target.syncRoot = target.root;
                target.syncView = target.photonView;
                if (target.lastVerifiedComponent != (object)comp)
                {
                    target.lastVerifiedComponent = comp;
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC ok(fallback) name='" + target.playerName + "' comp=" + t.Name +
                        " stateField=" + stateField.Name + " inputField=" + inputField.Name);
                }
                return;
            }
            if (Time.unscaledTime - target.lastFailLog > 10f)
            {
                target.lastFailLog = Time.unscaledTime;
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC fail name='" + target.playerName + "' comps=" + comps.Length);
            }
        }

        private float nextGraphDiagLog = -1f;

        private void LogGraphDiag(LiveGraphState g, bool waitingData)
        {
            if (Time.unscaledTime < nextGraphDiagLog)
                return;
            nextGraphDiagLog = Time.unscaledTime + 3f;
            if (g == null)
                return;
            string sampleAge = "none";
            string sampleVals = "";
            if (g.mpSampleCount > 0)
            {
                int newest = (g.mpSampleHead - 1 + MP_SAMPLE_RING) % MP_SAMPLE_RING;
                sampleAge = (Time.unscaledTime - g.mpSampleTimes[newest]).ToString("F2");
                Vector4 v = g.mpSamples[newest];
                sampleVals = " v=" + v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2") + "," + v.w.ToString("F2");
                if (g.mpSampleCount > 2)
                    sampleVals += " throttle-2s=" + SampleMpChannel(g, 2f, 0).ToString("F2");
            }
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE GRAPH diag pilot=" + (g.mpPilotName ?? "-") +
                " target=" + (g.mpTarget != null ? g.mpTarget.playerName : "-") +
                " sync=" + (g.mpTarget != null && g.mpTarget.syncComponent != null) +
                " n=" + g.mpSampleCount + " succ=" + g.mpReadSuccess + " fail=" + g.mpReadFail +
                " wait=" + waitingData +
                " buildAge=" + (Time.unscaledTime - g.traceLastBuildTime).ToString("F2") +
                " sampleAge=" + sampleAge + sampleVals);
        }

        private static System.Type cachedPlaybackStateType = null;

        // Cached hit or backed-off miss (GameAccess): this runs inside per-component
        // loops on a retry timer, so an uncached miss meant repeated full scans.
        private static System.Type ResolvePlaybackStateType()
        {
            if (cachedPlaybackStateType == null)
                cachedPlaybackStateType = GameAccess.FindAnyType("PlaybackState");
            return cachedPlaybackStateType;
        }

        private bool ResolveMpTargetBySceneMatch(MultiplayerPipTarget target)
        {
            if (target == null)
                return false;
            if (Time.unscaledTime - target.lastSceneAttempt < 3f)
                return false;
            target.lastSceneAttempt = Time.unscaledTime;
            try
            {
                Transform top = target.root.root;
                MonoBehaviour[] all = top.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (MonoBehaviour comp in all)
                {
                    if (comp == null || comp.gameObject == null)
                        continue;
                    System.Type t = comp.GetType();
                    if (t == null)
                        continue;
                    System.Reflection.FieldInfo stateField = FindPlaybackStateField(t);
                    if (stateField == null)
                        continue;
                    System.Reflection.FieldInfo inputField = FindInputField(stateField.FieldType);
                    if (inputField == null)
                    {
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC scene-no-input name='" + target.playerName +
                            "' comp=" + t.Name + " go=" + comp.gameObject.name);
                        continue;
                    }
                    target.syncComponent = comp;
                    target.playbackStateField = stateField;
                    target.containerField = null;
                    target.inputField = inputField;
                    target.resolvedAt = Time.unscaledTime;
                    target.syncRoot = target.root;
                    target.syncView = target.photonView;
                    if (target.lastVerifiedComponent != (object)comp)
                    {
                        target.lastVerifiedComponent = comp;
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC ok(scene) name='" + target.playerName + "' comp=" +
                            t.Name + " go=" + comp.gameObject.name + " stateField=" + stateField.Name +
                            " inputField=" + inputField.Name);
                    }
                    return true;
                }
            }
            catch (System.Exception sceneEx)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC scene EX " + sceneEx);
            }
            return false;
        }

        private Component FindObservedSyncComponent(Photon.Pun.PhotonView view)
        {
            if (view == null)
                return null;
            try
            {
                List<Component> list = view.ObservedComponents;
                if (list == null)
                    return null;
                foreach (object item in list)
                {
                    Component comp = item as Component;
                    if (comp == null)
                        continue;
                    if (FindPlaybackStateField(comp.GetType()) != null)
                        return comp;
                }
                foreach (object item in list)
                {
                    Component comp = item as Component;
                    if (comp == null)
                        continue;
                    if (IsPhotonSyncComponent(comp.GetType()))
                        return comp;
                }
            }
            catch
            {
            }
            return null;
        }

        private string GetGoPath(Transform t)
        {
            try
            {
                string path = t.name;
                Transform cur = t.parent;
                int depth = 0;
                while (cur != null && depth < 12)
                {
                    path = cur.name + "/" + path;
                    cur = cur.parent;
                    depth++;
                }
                return path;
            }
            catch
            {
                return "?";
            }
        }

        private bool IsPhotonSyncComponent(System.Type t)
        {
            System.Type[] ifaces;
            try
            {
                ifaces = t.GetInterfaces();
            }
            catch
            {
                return false;
            }
            if (ifaces == null)
                return false;
            for (int i = 0; i < ifaces.Length; i++)
            {
                if (ifaces[i] != null && ifaces[i].Name != null &&
                    ifaces[i].Name.IndexOf("IPunObservable", System.StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private System.Reflection.FieldInfo FindPlaybackStateField(System.Type t)
        {
            System.Type playbackStateType = ResolvePlaybackStateType();
            System.Type cur = t;
            while (cur != null && cur != typeof(object))
            {
                System.Reflection.FieldInfo[] fields = null;
                try
                {
                    fields = cur.GetFields(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly);
                }
                catch
                {
                    fields = null;
                }
                if (fields != null)
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (fields[i] == null || fields[i].FieldType == null)
                            continue;
                        if ((playbackStateType != null && fields[i].FieldType == playbackStateType) ||
                            fields[i].FieldType.Name == "PlaybackState")
                            return fields[i];
                    }
                }
                cur = cur.BaseType;
            }
            return null;
        }

        private System.Reflection.FieldInfo FindInputField(System.Type playbackStateType)
        {
            System.Reflection.FieldInfo inputField = null;
            try
            {
                inputField = playbackStateType.GetField("input",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            }
            catch { }
            if (inputField != null)
                return inputField;
            System.Reflection.FieldInfo[] fields;
            try
            {
                fields = playbackStateType.GetFields(System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            catch
            {
                return null;
            }
            if (fields == null)
                return null;
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i] != null && fields[i].FieldType != null &&
                    fields[i].FieldType == typeof(Vector4))
                    return fields[i];
            }
            return null;
        }

        private bool TryGetMpInput(MultiplayerPipTarget target, out Vector4 input)
        {
            input = Vector4.zero;
            if (target == null || target.syncComponent == null)
                return false;
            UnityEngine.Object unityComp = target.syncComponent as UnityEngine.Object;
            if (unityComp != null && unityComp == null)
                return false;
            try
            {
                object source = target.syncComponent;
                if (target.containerField != null)
                {
                    source = target.containerField.GetValue(target.syncComponent);
                    if (source == null)
                        return false;
                }
                object state = target.playbackStateField.GetValue(source);
                if (state == null)
                    return false;
                input = (Vector4)target.inputField.GetValue(state);
                return true;
            }
            catch (System.Exception readEx)
            {
                if (!target.readExLogged)
                {
                    target.readExLogged = true;
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE SYNC read EX name='" + target.playerName + "' " + readEx);
                }
                return false;
            }
        }

        private int mpInputCacheFrame = -1;
        private readonly Dictionary<MultiplayerPipTarget, Vector4> mpInputCache = new Dictionary<MultiplayerPipTarget, Vector4>();
        private readonly List<MultiplayerPipTarget> mpInputCacheValid = new List<MultiplayerPipTarget>();
        private readonly List<MultiplayerPipTarget> mpInputCacheStale = new List<MultiplayerPipTarget>();

        // Per-frame memo for the reflection-heavy remote input read: IMGUI draws run
        // on every event (2-3x/frame) but the value only changes once per frame.
        private bool TryGetMpInputCached(MultiplayerPipTarget target, out Vector4 input)
        {
            input = Vector4.zero;
            if (target == null)
                return false;
            if (mpInputCacheFrame != Time.frameCount)
            {
                mpInputCacheFrame = Time.frameCount;
                mpInputCacheValid.Clear();
                // Prune departed targets so the cache does not pin them forever.
                if (mpInputCache.Count > multiplayerPipTargets.Count)
                {
                    mpInputCacheStale.Clear();
                    foreach (var k in mpInputCache.Keys)
                    {
                        if (!multiplayerPipTargets.Contains(k))
                            mpInputCacheStale.Add(k);
                    }
                    for (int i = 0; i < mpInputCacheStale.Count; i++)
                        mpInputCache.Remove(mpInputCacheStale[i]);
                }
            }
            if (mpInputCacheValid.Contains(target))
            {
                input = mpInputCache[target];
                return true;
            }
            if (TryGetMpInput(target, out input))
            {
                mpInputCache[target] = input;
                mpInputCacheValid.Add(target);
                return true;
            }
            return false;
        }

        private void DrawLiveGraph(bool frontOnly = false)
        {
            if (liveInputGraphs.Count == 0)
                return;

            EnsureSettingsStyles();
            EnsureSettingsTextures();
            EnsureIcons();

            Event e = Event.current;
            liveGraphPilotOptions.Clear();
            foreach (GhostLineEntry entry in GhostLinesPlugin.entries)
            {
                if (entry.GetRecording() != null && entry.GetRecording().states != null)
                    liveGraphPilotOptions.Add(entry);
            }
            foreach (GhostLineEntry candidate in recordingCandidates)
            {
                if (candidate.GetRecording() != null && candidate.GetRecording().states != null &&
                    !liveGraphPilotOptions.Contains(candidate))
                    liveGraphPilotOptions.Add(candidate);
            }
            liveGraphMpPilotOptions.Clear();
            for (int ti = 0; ti < multiplayerPipTargets.Count; ti++)
            {
                MultiplayerPipTarget target = multiplayerPipTargets[ti];
                if (target == null || target.root == null)
                    continue;
                liveGraphMpPilotOptions.Add(target);
            }
            if (liveGraphPilotOptions.Count > 0 || liveGraphMpPilotOptions.Count > 0)
            {
                for (int pi = 0; pi < liveInputGraphs.Count; pi++)
                {
                    LiveGraphState gv = liveInputGraphs[pi];
                    if (gv.mpTarget != null && gv.mpTarget.root != null)
                        continue;
                    if (gv.mpPilotName != null || (gv.mpTarget != null && gv.mpTarget.ownerActor != 0))
                    {
                        MultiplayerPipTarget match = null;
                        for (int ti = 0; ti < liveGraphMpPilotOptions.Count; ti++)
                        {
                            MultiplayerPipTarget opt = liveGraphMpPilotOptions[ti];
                            if (opt == null || opt.root == null)
                                continue;
                            if (gv.mpTarget != null && gv.mpTarget.ownerActor != 0 &&
                                opt.ownerActor != 0 && opt.ownerActor != gv.mpTarget.ownerActor)
                                continue;
                            bool actorMatch = gv.mpTarget != null && gv.mpTarget.ownerActor != 0 &&
                                opt.ownerActor == gv.mpTarget.ownerActor;
                            bool nameMatch = gv.mpPilotName != null && opt.playerName == gv.mpPilotName;
                            if (!actorMatch && !nameMatch)
                                continue;
                            if (match == null || opt.firstSeenTime > match.firstSeenTime)
                            {
                                match = opt;
                            }
                        }
                        if (match != null)
                        {
                            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE GRAPH reattach '" + gv.mpPilotName + "'");
                            gv.mpTarget = match;
                            ResetMpGraphSync(gv);
                            gv.waitPilotSince = 0f;
                        }
                        else if (gv.mpSampleCount >= 2 && gv.waitPilotSince > 0f &&
                            Time.unscaledTime - gv.waitPilotSince > MP_LIVE_AUTO_SWITCH_SECONDS)
                        {
                            string departed = gv.mpPilotName;
                            MultiplayerPipTarget switchTarget = null;
                            for (int si = 0; si < liveGraphMpPilotOptions.Count; si++)
                            {
                                MultiplayerPipTarget cand = liveGraphMpPilotOptions[si];
                                if (cand != null && cand.root != null &&
                                    cand.playerName != departed)
                                {
                                    switchTarget = cand;
                                    break;
                                }
                            }
                            if (switchTarget != null)
                            {
                                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE GRAPH auto-switch from '" +
                                    departed + "' to '" + switchTarget.playerName + "'");
                                gv.mpTarget = switchTarget;
                                gv.mpPilotName = switchTarget.playerName;
                                ResetMpGraphData(gv);
                                gv.waitPilotSince = 0f;
                            }
                        }
                        continue;
                    }
                    if (gv.pilot == null && gv.mpTarget == null)
                    {
                        if (liveGraphMpPilotOptions.Count > 0)
                        {
                            gv.mpTarget = liveGraphMpPilotOptions[0];
                            gv.mpPilotName = gv.mpTarget.playerName;
                            ResetMpGraphData(gv);
                        }
                        else if (liveGraphPilotOptions.Count > 0)
                            gv.pilot = liveGraphPilotOptions[0];
                    }
                }
            }
            if (liveGraphActivated != null && liveInputGraphs.Contains(liveGraphActivated))
            {
                liveInputGraphs.Remove(liveGraphActivated);
                liveInputGraphs.Add(liveGraphActivated);
                liveGraphActivated = null;
            }
            for (int gi = 0; gi < liveInputGraphs.Count; gi++)
            {
                LiveGraphState g = liveInputGraphs[gi];
                bool stale = false;
                if (g.mpTarget != null)
                {
                    if (g.mpTarget.root == null || !multiplayerPipTargets.Contains(g.mpTarget))
                    {
                        MultiplayerPipTarget replacement = null;
                        for (int ri = 0; ri < multiplayerPipTargets.Count; ri++)
                        {
                            MultiplayerPipTarget cand = multiplayerPipTargets[ri];
                            if (cand == null || cand.root == null || cand == g.mpTarget)
                                continue;
                            if (cand.playerName == g.mpTarget.playerName &&
                                (replacement == null || cand.firstSeenTime > replacement.firstSeenTime))
                            {
                                replacement = cand;
                            }
                        }
                        if (replacement != null)
                        {
                            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE GRAPH retarget same-pilot '" +
                                g.mpTarget.playerName + "'");
                            g.mpTarget = replacement;
                            ResetMpGraphSync(g);
                            g.waitPilotSince = 0f;
                        }
                        else
                        {
                            g.mpPilotName = g.mpTarget.playerName;
                            g.mpTarget = null;
                            g.waitPilotSince = Time.unscaledTime;
                            ResetMpGraphSync(g);
                            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE GRAPH wait-pilot '" + g.mpPilotName + "'");
                        }
                    }
                }
                else if (g.pilot != null &&
                    (!GhostLinesPlugin.entries.Contains(g.pilot) && !recordingCandidates.Contains(g.pilot) ||
                     g.pilot.GetRecording() == null))
                {
                    stale = true;
                }
                if (IsLiveGraphDocked(g))
                {
                    CaptureMpSamples(g);
                    continue;
                }
                if (g.mpTarget != null && g.mpTarget.root != null && !stale)
                {
                    MultiplayerPipTarget newer = null;
                    for (int ti = 0; ti < multiplayerPipTargets.Count; ti++)
                    {
                        MultiplayerPipTarget cand = multiplayerPipTargets[ti];
                        if (cand == null || cand.root == null || cand == g.mpTarget)
                            continue;
                        if (cand.playerName == g.mpTarget.playerName &&
                            cand.firstSeenTime > g.mpTarget.firstSeenTime &&
                            (newer == null || cand.firstSeenTime > newer.firstSeenTime))
                        {
                            newer = cand;
                        }
                    }
                    if (newer != null)
                    {
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LIVE GRAPH retarget newer '" + g.mpTarget.playerName + "'");
                        g.mpTarget = newer;
                        ResetMpGraphSync(g);
                        g.waitPilotSince = 0f;
                    }
                }
                if (stale)
                {
                    if (g.traceTex != null)
                    {
                        Destroy(g.traceTex);
                        g.traceTex = null;
                    }
                    liveInputGraphs.RemoveAt(gi);
                    gi--;
                    continue;
                }
                if (g.rect.x < 0f)
                {
                    g.rect = new Rect(
                        Screen.width - g.rect.width - 24f,
                        120f,
                        g.rect.width,
                        g.rect.height);
                }
                g.rect = ClampGraphRect(g.rect);

                Rect panel = g.rect;
                float alpha = g.fadeAmount;
                if (alpha < 0.01f)
                    continue;

                bool covered = false;
                for (int j = gi + 1; j < liveInputGraphs.Count; j++)
                {
                    if (liveInputGraphs[j].fadeAmount > 0.01f && liveInputGraphs[j].rect.Contains(e.mousePosition))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered && IsUiBlockedFor(UI_LAYER_STICKGRAPH, e.mousePosition))
                    covered = true;

                Color old = GUI.color;

                GUI.color = new Color(0f, 0f, 0f, 0.12f * alpha);
                GUI.DrawTexture(new Rect(panel.x + 3f, panel.y + 3f, panel.width - 6f, panel.height), whiteTex);
                GUI.color = ThemePanelBg(alpha);
                GUI.DrawTexture(panel, whiteTex);
                GUI.color = ThemePanelBorder(alpha);
                GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), whiteTex);
                GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), whiteTex);

                float titleH = 30f;
                Rect close = new Rect(panel.xMax - 38f, panel.y + 4f, 28f, 22f);
                // Even 6px gaps across the header controls (right to left):
                // throttle dropdown, history, plus, pin, close.
                const float hdrGap = 6f;
                Rect pin = new Rect(close.x - hdrGap - 22f, panel.y + 6f, 22f, 18f);
                Rect plusBtn = new Rect(pin.x - hdrGap - 22f, panel.y + 6f, 22f, 18f);
                float histW = 42f;
                Rect histBtn = new Rect(plusBtn.x - hdrGap - histW, panel.y + 6f, histW, 18f);
                float ddW = 96f;
                Rect ddBtn = new Rect(histBtn.x - hdrGap - ddW, panel.y + 6f, ddW, 18f);
                Rect pilotBtn = new Rect(panel.x + 12f, panel.y + 6f,
                    Mathf.Max(20f, ddBtn.x - hdrGap - (panel.x + 12f)), 18f);
                bool lapMode = g.mpTarget == null && g.pilot != null && replayReady &&
                    g.pilot.originalInputs != null && g.pilot.stateTimes != null && g.pilot.stateTimes.Length >= 2;
                float lapStartT = 0f;
                float lapEndT = 0f;
                if (lapMode)
                {
                    lapEndT = g.pilot.stateTimes[g.pilot.stateTimes.Length - 1];
                    lapStartT = Mathf.Clamp(GetGateTimestamp(g.pilot), g.pilot.stateTimes[0], lapEndT);
                    ddBtn = new Rect(ddBtn.x, ddBtn.y, (plusBtn.x - 6f) - ddBtn.x, 18f);
                }
                bool waitingSynthetic = g.mpTarget == null && g.mpPilotName != null &&
                    !liveGraphMpPilotOptions.Exists(o => o != null && o.playerName == g.mpPilotName);
                int synthCount = waitingSynthetic ? 1 : 0;
                int pilotOptCount = PilotDropdownRows(pilotBtn, liveGraphPilotOptions.Count + liveGraphMpPilotOptions.Count + synthCount);

                bool inPilotList = false;
                if (g.pilotDropdown)
                {
                    for (int i = 0; i < pilotOptCount; i++)
                    {
                        if (new Rect(pilotBtn.x, pilotBtn.yMax + 2f + 20f * i, pilotBtn.width, 20f).Contains(e.mousePosition))
                        {
                            inPilotList = true;
                            break;
                        }
                    }
                }
                bool inTitleControls = close.Contains(e.mousePosition) || pin.Contains(e.mousePosition) ||
                    plusBtn.Contains(e.mousePosition) ||
                    ddBtn.Contains(e.mousePosition) || (!lapMode && histBtn.Contains(e.mousePosition)) || inPilotList;

                g.edgeHover = inTitleControls || g.channelDropdown || g.pilotDropdown
                    ? 0
                    : ComputeWindowResizeEdge(panel, e.mousePosition, false);
                DrawWindowResizeBars(panel, g.resizing ? g.resizeEdge : g.edgeHover, alpha);

                if (!covered && e.type == EventType.MouseDown && e.button == 0 && g.edgeHover != 0)
                {
                    g.resizing = true;
                    g.resizeEdge = g.edgeHover;
                    g.resizeStartMouse = e.mousePosition;
                    g.resizeStartRect = g.rect;
                    g.dragCandidate = false;
                    g.dragging = false;
                    g.channelDropdown = false;
                    g.pilotDropdown = false;
                    g.historyDropdown = false;
                    liveGraphActivated = g;
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                    e.Use();
                }
                if (g.resizing && e.type == EventType.MouseDrag && e.button == 0)
                {
                    g.rect = ApplyWindowResizeDrag(g.resizeEdge, g.resizeStartMouse, g.resizeStartRect,
                        e.mousePosition, STICK_GRAPH_MIN_WIDTH, STICK_GRAPH_MIN_HEIGHT);
                    g.rect = ClampGraphRect(g.rect);
                    e.Use();
                }
                if (g.resizing && e.type == EventType.MouseUp && e.button == 0)
                {
                    g.resizing = false;
                    g.resizeEdge = 0;
                    g.traceCachePlotW = -1;
                    g.traceCachePlotH = -1;
                    e.Use();
                }

                bool inDropdownList = false;
                if (g.channelDropdown)
                {
                    for (int i = 0; i < stickGraphChannelNames.Length; i++)
                    {
                        if (GetStickGraphOptionRect(ddBtn, i).Contains(e.mousePosition))
                        {
                            inDropdownList = true;
                            break;
                        }
                    }
                }
                if (!inDropdownList && g.historyDropdown)
                {
                    for (int i = 0; i < LIVE_HISTORY_OPTIONS.Length; i++)
                    {
                        if (new Rect(histBtn.x, histBtn.yMax + 2f + 20f * i, histBtn.width, 20f).Contains(e.mousePosition))
                        {
                            inDropdownList = true;
                            break;
                        }
                    }
                }
                if (!covered && e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) &&
                    !close.Contains(e.mousePosition) && !pin.Contains(e.mousePosition) && !plusBtn.Contains(e.mousePosition) &&
                    !ddBtn.Contains(e.mousePosition) &&
                    (!lapMode || !histBtn.Contains(e.mousePosition)) &&
                    !inDropdownList && !inPilotList && g.edgeHover == 0)
                {
                    g.dragCandidate = true;
                    g.dragOffset = e.mousePosition - new Vector2(panel.x, panel.y);
                    liveGraphActivated = g;
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                }
                if (g.dragging && e.type == EventType.MouseDrag && e.button == 0)
                {
                    g.rect.x = e.mousePosition.x - g.dragOffset.x;
                    g.rect.y = e.mousePosition.y - g.dragOffset.y;
                    g.rect = ClampGraphRect(g.rect);
                    e.Use();
                }
                if (g.dragging && e.type == EventType.MouseUp && e.button == 0)
                {
                    g.dragging = false;
                    if (g.mpTarget != null)
                    {
                        MultiplayerPipWindow dropTarget = FindMpPipWindowForDock(e.mousePosition);
                        if (dropTarget != null)
                        {
                            int dropSlot = GetPipDockSlotAt(dropTarget.rect, e.mousePosition);
                            if (dropSlot >= 0)
                                DockGraphInto(dropTarget.docked, g, dropSlot);
                        }
                    }
                    else
                    {
                        PiPWindow dropTargetReplay = FindReplayPipWindowForDock(e.mousePosition);
                        if (dropTargetReplay != null)
                        {
                            int dropSlotReplay = GetPipDockSlotAt(dropTargetReplay.rect, e.mousePosition);
                            if (dropSlotReplay >= 0)
                                DockGraphInto(dropTargetReplay.docked, g, dropSlotReplay);
                        }
                    }
                    e.Use();
                }

                string pilotLabel;
                if (g.mpTarget != null)
                    pilotLabel = string.IsNullOrEmpty(g.mpTarget.playerName) ? "Pilot" : g.mpTarget.playerName;
                else if (g.mpPilotName != null)
                    pilotLabel = g.mpPilotName;
                else
                    pilotLabel = g.pilot == null || string.IsNullOrEmpty(g.pilot.pilotName) ? "Pilot" : g.pilot.pilotName;
                bool pilotHover = pilotBtn.Contains(e.mousePosition);
                GUI.color = pilotHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
                GUI.DrawTexture(pilotBtn, whiteTex);
                GUI.color = ThemeButtonBorder(alpha);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.y, pilotBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.yMax - 1f, pilotBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.y, 1f, pilotBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.xMax - 1f, pilotBtn.y, 1f, pilotBtn.height), whiteTex);
                GUI.color = ThemeLabel(alpha);
                GUI.Label(new Rect(pilotBtn.x + 6f, pilotBtn.y, pilotBtn.width - 22f, pilotBtn.height), pilotLabel, settingsSmallStyle);
                if (!covered && e.type == EventType.MouseDown && e.button == 0 && pilotBtn.Contains(e.mousePosition))
                {
                    g.pilotDropdown = !g.pilotDropdown;
                    g.channelDropdown = false;
                    g.historyDropdown = false;
                    g.dragCandidate = false;
                    liveGraphActivated = g;
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                    settingsControlPressed = true;
                    e.Use();
                }

                int history = LIVE_HISTORY_OPTIONS[Mathf.Clamp(g.historyIndex, 0, LIVE_HISTORY_OPTIONS.Length - 1)];
                if (!lapMode)
                {
                    bool histHover = histBtn.Contains(e.mousePosition);
                    GUI.color = histHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
                    GUI.DrawTexture(histBtn, whiteTex);
                    GUI.color = ThemeButtonBorder(alpha);
                    GUI.DrawTexture(new Rect(histBtn.x, histBtn.y, histBtn.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(histBtn.x, histBtn.yMax - 1f, histBtn.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(histBtn.x, histBtn.y, 1f, histBtn.height), whiteTex);
                    GUI.DrawTexture(new Rect(histBtn.xMax - 1f, histBtn.y, 1f, histBtn.height), whiteTex);
                    GUI.color = ThemeLabel(alpha);
                    GUI.Label(histBtn, history + "s", settingsSmallCenterStyle);
                    if (!covered && e.type == EventType.MouseDown && e.button == 0 && histBtn.Contains(e.mousePosition))
                    {
                        g.historyDropdown = !g.historyDropdown;
                        g.channelDropdown = false;
                        g.pilotDropdown = false;
                        g.dragCandidate = false;
                        liveGraphActivated = g;
                        uiFrontLayer = UI_LAYER_STICKGRAPH;
                        settingsControlPressed = true;
                        e.Use();
                    }
                }

                bool plusHover = plusBtn.Contains(e.mousePosition);
                GUI.color = plusHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
                GUI.DrawTexture(plusBtn, whiteTex);
                GUI.color = ThemeButtonBorder(alpha);
                GUI.DrawTexture(new Rect(plusBtn.x, plusBtn.y, plusBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(plusBtn.x, plusBtn.yMax - 1f, plusBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(plusBtn.x, plusBtn.y, 1f, plusBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(plusBtn.xMax - 1f, plusBtn.y, 1f, plusBtn.height), whiteTex);
                GUI.color = ThemeLabel(alpha);
                GUI.Label(plusBtn, "+", settingsPlusStyle);
                if (!covered && e.type == EventType.MouseDown && e.button == 0 && plusBtn.Contains(e.mousePosition))
                {
                    LiveGraphState copy = new LiveGraphState();
                    copy.pilot = g.pilot;
                    copy.mpTarget = g.mpTarget;
                    copy.mpPilotName = g.mpPilotName;
                    copy.channel = g.channel;
                    copy.historyIndex = g.historyIndex;
                    copy.fadeAmount = 1f;
                    copy.rect = new Rect(
                        g.rect.x + STICK_GRAPH_OPEN_OFFSET,
                        g.rect.y + STICK_GRAPH_OPEN_OFFSET,
                        g.rect.width,
                        g.rect.height);
                    copy.rect = ClampGraphRect(copy.rect);
                    liveInputGraphs.Add(copy);
                    liveGraphActivated = copy;
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                    settingsControlPressed = true;
                    g.dragCandidate = false;
                    e.Use();
                }

                if (DrawStyledToggleButton(pin, iconPin, g.pinned, alpha, !covered, UI_LAYER_STICKGRAPH))
                {
                    g.pinned = !g.pinned;
                    liveGraphActivated = g;
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                }
                GUI.color = ThemeCloseLabel(alpha);
                // Nudged up so the glyph centres on the pin/plus buttons.
                GUI.Label(new Rect(close.x, close.y - 3f, close.width, close.height),
                    "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);
                if (!covered && e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
                {
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                    if (g.traceTex != null)
                    {
                        Destroy(g.traceTex);
                        g.traceTex = null;
                    }
                    liveInputGraphs.RemoveAt(gi);
                    gi--;
                    e.Use();
                    GUI.color = old;
                    continue;
                }

                var recording = g.pilot != null ? g.pilot.GetRecording() : null;
                var states = recording != null ? recording.states : null;
                bool waitingData;
                if (lapMode)
                    waitingData = false;
                else if (g.mpTarget != null || g.mpSampleCount > 1)
                    waitingData = g.mpSampleCount < 2;
                else
                    waitingData = g.pilot == null || states == null || states.Count < 2;

                CaptureMpSamples(g);
                LogGraphDiag(g, waitingData);

                float yLblW = 46f;
                float timeLblH = 16f;
                Rect plot = new Rect(
                    panel.x + 8f + yLblW,
                    panel.y + titleH + 6f,
                    panel.width - 8f - 6f - yLblW,
                    panel.height - titleH - 6f - 4f - timeLblH);
                Rect timeLblRow = new Rect(plot.x, plot.yMax + 2f, plot.width, timeLblH);

                GUI.color = darkMode
                    ? new Color(0.08f, 0.08f, 0.09f, 0.8f * alpha)
                    : new Color(0.94f, 0.94f, 0.94f, 0.8f * alpha);
                GUI.DrawTexture(plot, whiteTex);
                GUI.color = ThemePanelBorder(alpha);
                GUI.DrawTexture(new Rect(plot.x, plot.y, plot.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(plot.x, plot.yMax - 1f, plot.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(plot.x, plot.y, 1f, plot.height), whiteTex);
                GUI.DrawTexture(new Rect(plot.xMax - 1f, plot.y, 1f, plot.height), whiteTex);
                if (waitingData)
                {
                    GUI.color = ThemeDimLabel(alpha);
                    GUI.Label(plot, "Waiting for input data...", settingsSmallCenterStyle);
                }

                bool throttleAxis = g.channel == 0;
                for (int i = 0; i < 5; i++)
                {
                    float yv = plot.y + plot.height * (i / 4f);
                    GUI.color = i == 2 ? ThemePanelBorder(alpha) : ThemeTrack(alpha);
                    GUI.DrawTexture(new Rect(plot.x, yv, plot.width, 1f), whiteTex);
                    string labelText;
                    if (throttleAxis)
                        labelText = (100 - i * 25).ToString("0") + "%";
                    else
                    {
                        int labelVal = 100 - i * 50;
                        labelText = (labelVal > 0 ? "+" : "") + labelVal + "%";
                    }
                    float labelY = yv - 8f;
                    labelY = Mathf.Clamp(labelY, panel.y + 2f, panel.y + panel.height - 18f);
                    GUI.color = ThemeDimLabel(alpha);
                    GUI.Label(new Rect(plot.x - yLblW + 2f, labelY, yLblW - 6f, 16f), labelText, settingsAxisLabelStyle);
                }

                if (!waitingData)
                {
                    Color signalColor;
                    if (g.mpTarget != null || g.mpColor.a > 0f)
                    {
                        signalColor = g.mpColor.a > 0f ? g.mpColor : new Color(0.25f, 0.62f, 0.95f);
                    }
                    else
                    {
                        signalColor = darkMode
                            ? Color.Lerp(g.pilot.color, new Color(1f, 1f, 1f), 0.25f)
                            : Color.Lerp(g.pilot.color, new Color(0f, 0f, 0f), 0.4f);
                    }
                    int tw = Mathf.Max(1, (int)plot.width);
                    int th = Mathf.Max(1, (int)plot.height);
                    bool traceLive = g.mpTarget != null || g.mpSampleCount > 1;
                    bool traceContentChanged = g.traceCachePilot != g.pilot || g.traceCacheMpTarget != g.mpTarget ||
                        g.traceCacheChannel != g.channel ||
                        g.traceCacheHistory != history;
                    bool traceSizeChanged = g.traceCachePlotW != tw || g.traceCachePlotH != th;
                    float traceStaleInterval = traceLive ? 0.1f : 0.06f;
                    bool traceStale = Time.unscaledTime - g.traceLastBuildTime > traceStaleInterval;
                    if (lapMode)
                    {
                        bool lapRebuild = g.traceTex == null || g.traceCachePilot != g.pilot ||
                            g.traceCacheChannel != g.channel || g.traceCachePlotW != tw || g.traceCachePlotH != th;
                        if (lapRebuild)
                        {
                            BuildLapTraceTexture(g.pilot, g.channel, tw, th, lapStartT, lapEndT - lapStartT,
                                ref g.traceTex, ref g.tracePixels);
                            g.traceLastBuildTime = Time.unscaledTime;
                            g.traceCachePilot = g.pilot;
                            g.traceCacheMpTarget = null;
                            g.traceCacheChannel = g.channel;
                            g.traceCachePlotW = tw;
                            g.traceCachePlotH = th;
                        }
                    }
                    else if ((traceContentChanged || traceSizeChanged || traceLive) && traceStale)
                    {
                        BuildLiveTraceTexture(g, tw, th, history);
                    }
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    Rect traceDraw = new Rect(Mathf.Round(plot.x), Mathf.Round(plot.y), tw, th);
                    GUI.DrawTexture(traceDraw, g.traceTex);

                    if (lapMode)
                    {
                        float lgQuery = lapStartT;
                        if (TryGetFrameReplayTime(out float lgRt))
                            lgQuery = GetGateQueryTimestamp(g.pilot, lgRt);
                        if (lgQuery >= lapStartT && lgQuery <= lapEndT)
                        {
                            float lgPlayX = plot.x + (lgQuery - lapStartT) / Mathf.Max(lapEndT - lapStartT, 0.001f) * plot.width;
                            Color lgPlayColor = darkMode
                                ? new Color(1f, 1f, 1f, 0.85f * alpha)
                                : new Color(0.12f, 0.12f, 0.12f, 0.85f * alpha);
                            GUI.color = lgPlayColor;
                            GUI.DrawTexture(new Rect(lgPlayX, plot.y, 1f, plot.height), whiteTex);
                            GUI.color = new Color(lgPlayColor.r, lgPlayColor.g, lgPlayColor.b, 0.4f * alpha);
                            GUI.DrawTexture(new Rect(lgPlayX + 1f, plot.y, 1f, plot.height), whiteTex);
                        }
                    }
                    else
                    {
                        float nowVal = SampleGraphChannel(g, 0f, g.channel);
                        int nowPy = Mathf.Clamp((int)((1f - (nowVal + 1f) * 0.5f) * th), 0, th - 1);
                        GUI.color = new Color(0.3f, 0.3f, 0.3f, 0.6f * alpha);
                        GUI.DrawTexture(new Rect(plot.x, plot.y + nowPy, plot.width, 1f), whiteTex);
                    }
                }

                GUI.color = ThemeDimLabel(alpha);
                if (lapMode)
                {
                    GUI.Label(new Rect(timeLblRow.x, timeLblRow.y, 60f, timeLblRow.height), "0s", settingsSmallStyle);
                    GUI.Label(new Rect(timeLblRow.xMax - 60f, timeLblRow.y, 60f, timeLblRow.height),
                        (lapEndT - lapStartT).ToString("0.0") + "s", settingsSmallStyle);
                    GUI.Label(new Rect(timeLblRow.x + timeLblRow.width * 0.5f - 30f, timeLblRow.y, 60f, timeLblRow.height),
                        ((lapEndT - lapStartT) * 0.5f).ToString("0.0") + "s", settingsSmallCenterStyle);
                }
                else
                {
                    GUI.Label(new Rect(timeLblRow.x, timeLblRow.y, 60f, timeLblRow.height), "-" + history + "s", settingsSmallStyle);
                    GUI.Label(new Rect(timeLblRow.x + timeLblRow.width * 0.5f - 30f, timeLblRow.y, 60f, timeLblRow.height),
                        (history * 0.5f).ToString("0.0") + "s", settingsSmallCenterStyle);
                    GUI.Label(new Rect(timeLblRow.xMax - 60f, timeLblRow.y, 60f, timeLblRow.height),
                        "now", settingsSmallStyle);
                }

                int channelIndex = Mathf.Clamp(g.channel, 0, stickGraphChannelNames.Length - 1);
                bool ddHover = ddBtn.Contains(e.mousePosition);
                GUI.color = ddHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
                GUI.DrawTexture(ddBtn, whiteTex);
                GUI.color = ThemeButtonBorder(alpha);
                GUI.DrawTexture(new Rect(ddBtn.x, ddBtn.y, ddBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(ddBtn.x, ddBtn.yMax - 1f, ddBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(ddBtn.x, ddBtn.y, 1f, ddBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(ddBtn.xMax - 1f, ddBtn.y, 1f, ddBtn.height), whiteTex);
                GUI.color = ThemeLabel(alpha);
                GUI.Label(new Rect(ddBtn.x + 6f, ddBtn.y, ddBtn.width - 12f, ddBtn.height), stickGraphChannelNames[channelIndex], settingsSmallStyle);
                if (!covered && e.type == EventType.MouseDown && e.button == 0 && ddBtn.Contains(e.mousePosition))
                {
                    g.channelDropdown = !g.channelDropdown;
                    g.pilotDropdown = false;
                    g.historyDropdown = false;
                    g.dragCandidate = false;
                    liveGraphActivated = g;
                    settingsControlPressed = true;
                    e.Use();
                }

                if (g.historyDropdown)
                {
                    float hOptH = 20f;
                    for (int i = 0; i < LIVE_HISTORY_OPTIONS.Length; i++)
                    {
                        Rect hOptRect = new Rect(histBtn.x, histBtn.yMax + 2f + hOptH * i, histBtn.width, hOptH);
                        bool hOptHover = hOptRect.Contains(e.mousePosition);
                        GUI.color = i == g.historyIndex
                            ? new Color(0.25f, 0.62f, 0.95f, alpha)
                            : hOptHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeOptionBg(alpha);
                        GUI.DrawTexture(hOptRect, whiteTex);
                        GUI.color = ThemeOptionBorder(alpha);
                        GUI.DrawTexture(new Rect(hOptRect.x, hOptRect.y, hOptRect.width, 1f), whiteTex);
                        GUI.DrawTexture(new Rect(hOptRect.x, hOptRect.yMax - 1f, hOptRect.width, 1f), whiteTex);
                        GUI.color = i == g.historyIndex ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
                        GUI.Label(hOptRect, LIVE_HISTORY_OPTIONS[i] + "s", settingsSmallCenterStyle);
                        if (e.type == EventType.MouseUp && e.button == 0 && hOptRect.Contains(e.mousePosition))
                        {
                            g.historyIndex = i;
                            g.historyDropdown = false;
                            settingsControlPressed = false;
                            e.Use();
                        }
                    }
                    if (e.type == EventType.MouseDown && e.button == 0 && !histBtn.Contains(e.mousePosition))
                    {
                        bool hInList = false;
                        for (int i = 0; i < LIVE_HISTORY_OPTIONS.Length; i++)
                        {
                            if (new Rect(histBtn.x, histBtn.yMax + 2f + hOptH * i, histBtn.width, hOptH).Contains(e.mousePosition))
                            {
                                hInList = true;
                                break;
                            }
                        }
                        if (!hInList)
                            g.historyDropdown = false;
                    }
                }

                if (g.channelDropdown)
                {
                    float optH = 20f;
                    for (int i = 0; i < stickGraphChannelNames.Length; i++)
                    {
                        Rect optRect = new Rect(ddBtn.x, ddBtn.yMax + 2f + optH * i, ddBtn.width, optH);
                        bool optHover = optRect.Contains(e.mousePosition);
                        GUI.color = i == channelIndex
                            ? new Color(0.25f, 0.62f, 0.95f, alpha)
                            : optHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeOptionBg(alpha);
                        GUI.DrawTexture(optRect, whiteTex);
                        GUI.color = ThemeOptionBorder(alpha);
                        GUI.DrawTexture(new Rect(optRect.x, optRect.y, optRect.width, 1f), whiteTex);
                        GUI.DrawTexture(new Rect(optRect.x, optRect.yMax - 1f, optRect.width, 1f), whiteTex);
                        GUI.color = i == channelIndex ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
                        GUI.Label(optRect, stickGraphChannelNames[i], settingsSmallStyle);
                        if (e.type == EventType.MouseUp && e.button == 0 && optRect.Contains(e.mousePosition))
                        {
                            g.channel = i;
                            g.channelDropdown = false;
                            settingsControlPressed = false;
                            e.Use();
                        }
                    }
                    if (e.type == EventType.MouseDown && e.button == 0 && !ddBtn.Contains(e.mousePosition))
                    {
                        bool inList = false;
                        for (int i = 0; i < stickGraphChannelNames.Length; i++)
                        {
                            if (GetStickGraphOptionRect(ddBtn, i).Contains(e.mousePosition))
                            {
                                inList = true;
                                break;
                            }
                        }
                        if (!inList)
                            g.channelDropdown = false;
                    }
                }

                if (g.pilotDropdown)
                {
                    for (int i = 0; i < pilotOptCount; i++)
                    {
                        Rect optRect = new Rect(pilotBtn.x, pilotBtn.yMax + 2f + 20f * i, pilotBtn.width, 20f);
                        bool optHover = optRect.Contains(e.mousePosition);
                        bool optSelected;
                        int realIdx = i - synthCount;
                        if (waitingSynthetic && i == 0)
                        {
                            string optName = g.mpPilotName + " (waiting)";
                            optSelected = true;
                            GUI.color = optSelected
                                ? new Color(0.25f, 0.62f, 0.95f, alpha)
                                : optHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeOptionBg(alpha);
                            GUI.DrawTexture(optRect, whiteTex);
                            GUI.color = ThemeOptionBorder(alpha);
                            GUI.DrawTexture(new Rect(optRect.x, optRect.y, optRect.width, 1f), whiteTex);
                            GUI.DrawTexture(new Rect(optRect.x, optRect.yMax - 1f, optRect.width, 1f), whiteTex);
                            GUI.color = new Color(1f, 1f, 1f, 0.7f * alpha);
                            GUI.Label(optRect, optName, settingsSmallStyle);
                            if (e.type == EventType.MouseUp && e.button == 0 && optRect.Contains(e.mousePosition))
                            {
                                g.pilotDropdown = false;
                                settingsControlPressed = false;
                                e.Use();
                            }
                        }
                        else if (realIdx >= 0 && realIdx < liveGraphMpPilotOptions.Count)
                        {
                            MultiplayerPipTarget optTarget = liveGraphMpPilotOptions[realIdx];
                            string optName = string.IsNullOrEmpty(optTarget.playerName) ? "Pilot" : optTarget.playerName;
                            optSelected = optTarget == g.mpTarget;
                            GUI.color = optSelected
                                ? new Color(0.25f, 0.62f, 0.95f, alpha)
                                : optHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeOptionBg(alpha);
                            GUI.DrawTexture(optRect, whiteTex);
                            GUI.color = ThemeOptionBorder(alpha);
                            GUI.DrawTexture(new Rect(optRect.x, optRect.y, optRect.width, 1f), whiteTex);
                            GUI.DrawTexture(new Rect(optRect.x, optRect.yMax - 1f, optRect.width, 1f), whiteTex);
                            GUI.color = optSelected ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
                            GUI.Label(optRect, optName, settingsSmallStyle);
                            if (e.type == EventType.MouseUp && e.button == 0 && optRect.Contains(e.mousePosition))
                            {
                                g.mpTarget = optTarget;
                                g.mpPilotName = optTarget.playerName;
                                g.pilot = null;
                                g.fadeAmount = 1f;
                                g.pilotDropdown = false;
                                settingsControlPressed = false;
                                ResetMpGraphData(g);
                                e.Use();
                            }
                        }
                        else
                        {
                            int replayIdx = realIdx - liveGraphMpPilotOptions.Count;
                            GhostLineEntry optPilot = liveGraphPilotOptions[replayIdx];
                            string optName = string.IsNullOrEmpty(optPilot.pilotName) ? "Pilot" : optPilot.pilotName;
                            optSelected = optPilot == g.pilot;
                            GUI.color = optSelected
                                ? new Color(0.25f, 0.62f, 0.95f, alpha)
                                : optHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeOptionBg(alpha);
                            GUI.DrawTexture(optRect, whiteTex);
                            GUI.color = ThemeOptionBorder(alpha);
                            GUI.DrawTexture(new Rect(optRect.x, optRect.y, optRect.width, 1f), whiteTex);
                            GUI.DrawTexture(new Rect(optRect.x, optRect.yMax - 1f, optRect.width, 1f), whiteTex);
                            GUI.color = optSelected ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
                            GUI.Label(optRect, optName, settingsSmallStyle);
                            if (e.type == EventType.MouseUp && e.button == 0 && optRect.Contains(e.mousePosition))
                            {
                                g.pilot = optPilot;
                                g.mpTarget = null;
                                g.mpPilotName = null;
                                ResetMpGraphData(g);
                                g.fadeAmount = 1f;
                                g.pilotDropdown = false;
                                settingsControlPressed = false;
                                e.Use();
                            }
                        }
                    }
                    if (e.type == EventType.MouseDown && e.button == 0 && !pilotBtn.Contains(e.mousePosition) && !inPilotList)
                        g.pilotDropdown = false;
                }

                if (e.type == EventType.MouseDown && e.button == 0 && g.dragCandidate)
                {
                    g.dragging = true;
                    g.dragCandidate = false;
                }
                if (e.type == EventType.MouseUp && e.button == 0)
                    settingsControlPressed = false;

                GUI.color = old;
            }
        }
    }
}
