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
        private void TickRaceProbe()
        {
            // Flush a pending split-store write (cheap no-op when not dirty) even
            // during replay, so a failed immediate save self-heals.
            if (splitStoreDirty)
                SaveSplitStore(false);
            // The whole gate-splits engine is meaningless during replay (the panel is
            // hidden, laps from the replayed flight would be fake data).
            if (replayReady)
                return;
            try
            {
                // One-time hooks/patches are scene-independent and cheap after the
                // first call, so run them even in menus.
                HookRaceEventProbe();
                HookLobbyReturnCallbacks();
                MaybePatchRaceProbes();
                // Everything below is a flight/content-scene concern. Running the
                // gate-splits probes in menus was pure waste (full-scene scans and
                // graph-extraction retries while nothing could ever be captured).
                if (!IsContentSceneActive())
                    return;
                // Scene/track switches must reset (and reload) the split panel
                // immediately instead of waiting for the probe cadence or the first
                // gate of the new race.
                string scene;
                try { scene = SceneManager.GetActiveScene().name; } catch { return; }
                if (string.IsNullOrEmpty(scene))
                    return;
                float now = Time.unscaledTime;
                if (scene != raceProbeLastScene)
                {
                    raceProbeLastScene = scene;
                    ResetRaceSplitsForScene();
                    // Scene-scoped: the next successful extraction re-establishes it.
                    raceGraphGateCount = 0;
                    raceKeyProbeNextAt = 0f;
                    raceProbeNextAt = 0f;
                    // ProbeRaceScene is a scene-wide transform scan + logging, purely
                    // diagnostic for gate splits — debug only.
                    if (ModLog.DebugEnabled)
                        ProbeRaceScene(scene);
                }
                // Race identity resolves as soon as the HUD binds; read it here so a
                // track/race switch updates the panel and restored history before the
                // first crossing instead of on the first gate event.
                if (GateSplitsBinding.Subscribed && now >= raceKeyProbeNextAt)
                {
                    raceKeyProbeNextAt = now + 0.5f;
                    string probeKey = null;
                    try { probeKey = GateSplitsBinding.CurrentRaceKey(); } catch { }
                    if (!string.IsNullOrEmpty(probeKey) && probeKey != raceEventRaceKey)
                    {
                        if (raceEventRaceKey != null)
                            raceGraphGateCount = 0;
                        ResetRaceSplitsForScene();
                        raceProbeNextAt = 0f;
                        raceEventRaceKey = probeKey;
                        LoadPersistedTrack(probeKey);
                    }
                }
                // ReadGameLapSeconds costs reflection + Text reads every frame. When
                // no lap is active, nothing is being captured and both the panel and
                // history window are closed, nothing consumes the value — skip it.
                bool lapTimeWanted = raceCurLap != null || raceCapturePendingAt > 0f || raceHistoryOpen ||
                    (Prefs.gateSplitsConfig != null && Prefs.gateSplitsConfig.Value);
                if (lapTimeWanted)
                    TickRaceSync();
                if (!raceProbeTypesLogged && ModLog.DebugEnabled)
                {
                    // Diagnostic type census (assembly-wide scan); debug only.
                    raceProbeTypesLogged = true;
                    ProbeRaceTypes();
                }
                if (now < raceProbeNextAt)
                    return;
                // Graph extraction costs a scene-wide FindObjectsOfType per try. The
                // event path learns the route from real crossings, but the graph's
                // gate count is still needed (finish-vs-reset detection) — including
                // after a race switch, when the binding is already subscribed. Cap
                // the retries so a broken graph can't cause repeated scene scans.
                bool splitsWanted = Prefs.gateSplitsConfig == null || Prefs.gateSplitsConfig.Value;
                if (!raceGraphDumped && splitsWanted && Time.unscaledTime >= raceGraphRetryAt &&
                    (!GateSplitsBinding.Subscribed || raceGraphTries < 3))
                    TryExtractRaceGraph();
                ApplyProbeCadence(now);
                if (raceEventCounts.Count > 0 && now >= raceEventSummaryAt)
                {
                    raceEventSummaryAt = now + 10f;
                    if (ModLog.DebugEnabled)
                        LogRaceEventSummary();
                }
            }
            catch { }
        }

        // Graph not found yet: retry every 2 s; then back off to 5 s, and 15 s once stable.
        private void ApplyProbeCadence(float now)
        {
            raceProbeStableScans++;
            float interval = !raceGraphDumped
                ? 2f
                : (raceProbeStableScans < 4 ? 5f : 15f);
            raceProbeNextAt = now + interval;
        }

        private void ProbeRaceTypes()
        {
            try
            {
                System.Reflection.Assembly asm = null;
                try { asm = System.Reflection.Assembly.Load("Assembly-CSharp"); } catch { }
                if (asm == null && gameJoinMethod != null)
                {
                    try { asm = gameJoinMethod.DeclaringType.Assembly; } catch { }
                }
                if (asm == null)
                    return;
                System.Type[] types = GameAccess.TypesOf(asm);
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                foreach (var t in types)
                {
                    if (t == null || raceProbeTypes.Count >= 48)
                        continue;
                    string n = t.Name ?? "";
                    string nl = n.ToLowerInvariant();
                    bool hit = nl.Contains("checkpoint") || nl.Contains("passage") || nl.Contains("laptimer") ||
                        nl.Contains("racetimer") || nl.Contains("racemanager") || nl.Contains("racecontroller") ||
                        nl.Contains("raceprogress") || nl.Contains("racesessiondata") ||
                        (nl.Contains("race") && nl.Contains("gate")) || nl == "gate" || nl.EndsWith("gate");
                    if (!hit)
                        continue;
                    raceProbeTypes.Add(t);
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("[RACE] type '").Append(t.FullName).Append("' base='").Append(t.BaseType != null ? t.BaseType.FullName : "?").Append('\'');
                        var ifaces = t.GetInterfaces();
                        if (ifaces != null && ifaces.Length > 0)
                        {
                            sb.Append(" ifaces=[");
                            for (int i = 0; i < ifaces.Length && i < 6; i++)
                                sb.Append(ifaces[i].Name).Append(i + 1 < ifaces.Length && i + 1 < ifaces.Length ? "|" : "");
                            sb.Append(']');
                        }
                        pluginLog.LogInfo(sb.ToString());
                        var fields = t.GetFields(bf);
                        if (fields != null && fields.Length > 0)
                        {
                            var fs = new System.Text.StringBuilder("  fields: ");
                            for (int i = 0; i < fields.Length && i < 24; i++)
                                fs.Append(fields[i].Name).Append(':').Append(fields[i].FieldType.Name).Append(i + 1 < fields.Length && i < fields.Length - 1 && i + 1 < 24 ? ", " : "");
                            pluginLog.LogInfo(fs.ToString());
                        }
                        var methods = t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly);
                        if (methods != null && methods.Length > 0)
                        {
                            var ms = new System.Text.StringBuilder("  methods: ");
                            int shown = 0;
                            for (int i = 0; i < methods.Length && shown < 24; i++)
                            {
                                var m = methods[i];
                                if (m == null || m.IsSpecialName)
                                    continue;
                                ms.Append(m.Name).Append('(');
                                var ps = m.GetParameters();
                                for (int p = 0; p < ps.Length; p++)
                                    ms.Append(ps[p].ParameterType.Name).Append(p + 1 < ps.Length ? "," : "");
                                ms.Append(") ");
                                shown++;
                            }
                            pluginLog.LogInfo(ms.ToString());
                        }
                    }
                    catch { }
                }
                pluginLog.LogInfo("[RACE] type scan done: " + raceProbeTypes.Count + " matched");
            }
            catch (Exception ex) { pluginLog.LogInfo("[RACE] type scan error: " + ex.Message); }
        }

        private void ProbeRaceScene(string scene)
        {
            try
            {
                if (scene == "MainMenu" || scene == "MultiplayerMenu" || scene == "SplashScreen" || scene == "LoadingScreen")
                    return;
                TryExtractRaceGraph();
                var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
                int gateLike = 0;
                var names = new List<string>();
                foreach (var tr in all)
                {
                    if (tr == null)
                        continue;
                    string nn = (tr.name ?? "").ToLowerInvariant();
                    if (nn.Contains("gate") || nn.Contains("checkpoint") || nn.Contains("passage"))
                    {
                        gateLike++;
                        if (names.Count < 10)
                            names.Add(tr.name + (tr.parent != null ? " <" + tr.parent.name + ">" : ""));
                    }
                }
                pluginLog.LogInfo("[RACE] scene '" + scene + "': gate-like transforms=" + gateLike +
                    (names.Count > 0 ? " e.g. " + string.Join(" | ", names) : ""));
            }
            catch (Exception ex) { pluginLog.LogInfo("[RACE] scene scan error: " + ex.Message); }
        }

        private void HookRaceEventProbe()
        {
            if (raceEventProbeHooked)
                return;
            raceEventProbeHooked = true;
            try
            {
                raceEventProbe = new RaceEventProbe(this);
                Photon.Pun.PhotonNetwork.AddCallbackTarget(raceEventProbe);
                pluginLog.LogInfo("[RACE] photon event probe registered");
            }
            catch (Exception ex)
            {
                pluginLog.LogInfo("[RACE] event probe hook error: " + ex.Message);
            }
        }

        private void OnRaceProbeEvent(ExitGames.Client.Photon.EventData ev)
        {
            try
            {
                if (ev == null)
                    return;
                byte code = ev.Code;
                raceEventCounts[code] = raceEventCounts.TryGetValue(code, out int c) ? c + 1 : 1;
                int logged;
                if (!raceEventLogCount.TryGetValue(code, out logged))
                {
                    logged = 0;
                    raceEventLogCount[code] = 0;
                }
                // Payload dumping is a debug diagnostic; the counting above stays
                // because the summary is cheap and only logged when debug is on.
                if (!ModLog.DebugEnabled)
                    return;
                if (logged >= 4 || code == 201 || code == 202 || code == 206)
                    return;
                raceEventLogCount[code] = logged + 1;
                object payload = null;
                try { payload = ev.Parameters; } catch { }
                pluginLog.LogInfo("[RACE] photon event " + code + " payload=" + SummarizeRaceEventPayload(payload));
                if (code == 200 && !raceEv200Dumped)
                {
                    raceEv200Dumped = true;
                    try
                    {
                        if (payload is System.Collections.IDictionary pd)
                        {
                            foreach (System.Collections.DictionaryEntry de in pd)
                            {
                                pluginLog.LogInfo("[RACE] event200 param[" + de.Key + "] type=" + (de.Value != null ? de.Value.GetType().Name : "null"));
                                if (de.Value is System.Collections.IEnumerable ven && !(de.Value is string))
                                {
                                    foreach (var item in ven)
                                        DumpObjectFields("[RACE] event200 item", item);
                                }
                                else
                                    DumpObjectFields("[RACE] event200 value", de.Value);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string SummarizeRaceEventPayload(object data)
        {
            try
            {
                if (data == null)
                    return "null";
                if (data is System.Collections.IDictionary id)
                {
                    var sb = new System.Text.StringBuilder("dict{");
                    int i = 0;
                    foreach (System.Collections.DictionaryEntry de in id)
                    {
                        if (i++ >= 8)
                            break;
                        sb.Append('[').Append(de.Key).Append("]=").Append(RacePayloadShort(de.Value)).Append(' ');
                    }
                    sb.Append('}');
                    return sb.ToString();
                }
                if (data is System.Collections.IEnumerable en && !(data is string))
                {
                    var sb = new System.Text.StringBuilder("arr[");
                    int i = 0;
                    foreach (var item in en)
                    {
                        if (i++ >= 8)
                            break;
                        sb.Append(RacePayloadShort(item)).Append(' ');
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
                return RacePayloadShort(data);
            }
            catch { return data != null ? data.GetType().Name : "null"; }
        }

        private static string RacePayloadShort(object v)
        {
            try
            {
                if (v == null)
                    return "null";
                if (v is System.Collections.IDictionary || v is string)
                {
                    string s = v.ToString();
                    if (s.Length > 40)
                        s = s.Substring(0, 40) + "…";
                    return "'" + s + "'";
                }
                if (v.GetType().IsPrimitive || v is System.Enum)
                    return v.ToString();
                return v.GetType().Name;
            }
            catch { return "?"; }
        }

        private void LogRaceEventSummary()
        {
            try
            {
                var sb = new System.Text.StringBuilder("[RACE] photon events:");
                var keys = new List<byte>(raceEventCounts.Keys);
                keys.Sort();
                foreach (byte k in keys)
                    sb.Append(' ').Append(k).Append("x").Append(raceEventCounts[k]);
                pluginLog.LogInfo(sb.ToString());
            }
            catch { }
        }

        private sealed class RaceEventProbe : Photon.Realtime.IOnEventCallback
        {
            private readonly GhostLinePoller owner;
            public RaceEventProbe(GhostLinePoller owner) { this.owner = owner; }
            public void OnEvent(ExitGames.Client.Photon.EventData photonEvent) { owner.OnRaceProbeEvent(photonEvent); }
        }

        // Matchmaking callbacks on the main client: turns a dead-room rejoin
        // from 20s of silence into an instant, explained fallback.
        private bool matchmakingHooked = false;
        private LobbyReturnCallbacks lobbyReturnCallbacks = null;

        private void HookLobbyReturnCallbacks()
        {
            if (matchmakingHooked)
                return;
            matchmakingHooked = true;
            try
            {
                lobbyReturnCallbacks = new LobbyReturnCallbacks(this);
                Photon.Pun.PhotonNetwork.AddCallbackTarget(lobbyReturnCallbacks);
            }
            catch (Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: matchmaking hook EX " + ex.Message);
            }
        }

        private void OnMatchmakingJoinedRoom()
        {
            pluginLog.LogInfo("[GhostLines] leaderboards: matchmaking confirmed room join" +
                (pendingLobbyReturn != null ? " (return to '" + pendingLobbyReturn + "' in progress)" : ""));
        }

        private void OnMatchmakingJoinRoomFailed(short returnCode, string message)
        {
            pluginLog.LogInfo("[GhostLines] leaderboards: join failed code=" + returnCode + " msg=" + message +
                (pendingLobbyReturn != null ? " — return to '" + pendingLobbyReturn + "' aborted, room is gone" : ""));
            pendingLobbyJoin = null;
            pendingLobbyReturn = null;
            lobbySwitchActive = false;
        }

        private sealed class LobbyReturnCallbacks : Photon.Realtime.IMatchmakingCallbacks
        {
            private readonly GhostLinePoller owner;
            public LobbyReturnCallbacks(GhostLinePoller owner) { this.owner = owner; }
            public void OnJoinedRoom() { owner.OnMatchmakingJoinedRoom(); }
            public void OnJoinRoomFailed(short returnCode, string message) { owner.OnMatchmakingJoinRoomFailed(returnCode, message); }
            public void OnLeftRoom() { }
            public void OnCreatedRoom() { }
            public void OnCreateRoomFailed(short returnCode, string message) { }
            public void OnJoinRandomFailed(short returnCode, string message) { }
            public void OnFriendListUpdate(System.Collections.Generic.List<Photon.Realtime.FriendInfo> friendList) { }
        }

        // ---- Per-gate splits (LiveSplit-style panel) ----
        private sealed class RaceLapAttempt
        {
            public List<string> keys = new List<string>();
            public List<double> times = new List<double>();
        }
        private RaceLapAttempt raceCurLap = null;
        private RaceLapAttempt raceBestLap = null;
        private double raceBestLapTime = -1;
        private int raceLapNumber = 0;
        private readonly List<string> raceCycleKeys = new List<string>();
        private bool raceGraphDumped = false;
        // Expected gate rows from the scene race graph. Deliberately NOT cleared by
        // ResetRaceSplitsForScene (the first gate event resets that) — only the
        // scene-change path clears it, and a successful extraction sets it.
        private int raceGraphGateCount = 0;
        private int raceLapGateTotal = 0;
        private readonly List<string> raceGateLabels = new List<string>();
        private UnityEngine.MonoBehaviour raceLapTimerInst = null;
        private float raceLapTimerNextScanAt = 0f;
        private int raceLapScanFails = 0;
        private int raceProbeStableScans = 0;
        private System.Reflection.FieldInfo raceLtMin = null;
        private System.Reflection.FieldInfo raceLtSec = null;
        private System.Reflection.FieldInfo raceLtMilli = null;
        private float raceGameLapTime = -1f;
        private float raceSyncPrevGameLap = -1f;
        private double raceSyncT0 = -1.0;
        private float raceLastCloseAt = -999f;
        private RaceLapAttempt raceLastDoneLap = null;
        private double raceLastT0 = -1.0;
        private readonly List<string> raceRowKeys = new List<string>();
        private readonly List<int> raceVisibleIdx = new List<int>();
        private string raceFinishKey = null;
        private bool raceMergedFinish = false;
        private float raceCapturePendingAt = -999f;
        private int raceCaptureHave = 0;
        private float raceCapturedLap = -1f;
        private double raceCaptureEndT = 0.0;
        private bool raceCaptureThenNewLap = false;
        private string raceCaptureNewKey = null;
        private double raceCaptureNewTime = 0.0;
        private readonly HashSet<string> raceTrimmedKeys = new HashSet<string>(System.StringComparer.Ordinal);
        // Event-driven capture state (preferred over the trigger-prefix path).
        private string raceEventRaceKey = null;
        private float raceEventPrevLapTime = -1f;
        private float raceEventPrevFrameLap = -1f;
        private bool raceEventLapClosed = false;
        private int raceEventCompletedLen = 0;
        private readonly HashSet<int> raceEventPassed = new HashSet<int>();
        private readonly HashSet<string> raceSeenKeys = new HashSet<string>(System.StringComparer.Ordinal);
        private int raceRequiredCount = 0;
        private float raceGraphRetryAt = 0f;
        private int raceGraphTries = 0;
        private float raceGraphReprobeAt = 0f;
        private float raceOutOfOrderLogAt = -999f;
        private string raceStartKey = null;
        private bool raceEv200Dumped = false;
        private float raceSplitsPanelX = 20f;
        private float raceSplitsPanelY = 150f;
        private float raceSplitsPanelW = 300f;
        private float raceSplitsPanelH = 0f;
        private readonly WindowDrag raceSplitsDrag = new WindowDrag();
        private bool raceSplitsManualSize = false;
        // Manual row scroll (wheel). Snaps back to the live gate on any crossing.
        private bool raceSplitsScrollManual = false;
        private float raceSplitsScroll = 0f;
        private float raceSplitsLastAutoOffset = 0f;
        private bool raceSplitsResizing = false;
        private int raceSplitsResizeEdge = 0;
        private int raceSplitsResizeEdgeHover = 0;
        private Vector2 raceSplitsResizeStartMouse = Vector2.zero;
        private Rect raceSplitsResizeStartRect = new Rect(0f, 0f, 0f, 0f);
        private GUIStyle raceSplitsStyle = null;
        private GUIStyle raceSplitsDeltaStyle = null;
        private static readonly GUIContent raceBarSizeContent = new GUIContent();
        private GUIStyle raceSplitsBigStyle = null;
        private GUIStyle raceSplitsTrimStyle = null;

        private sealed class StoredLap
        {
            public int number;
            public double lapTime;
            public RaceLapAttempt attempt;
        }

        // ---- Persisted split history (per race key) ----
        // Times/deltas only: the gate list (passage ids), every recorded lap, and
        // the fastest lap as the comparison reference. Written through a temp file.
        private sealed class PersistedLap
        {
            public int number;
            public double lapTime;
            public readonly List<string> keys = new List<string>();
            public readonly List<double> times = new List<double>();
        }
        private sealed class PersistedTrack
        {
            public string key;
            public bool mergedFinish;
            public readonly List<string> gates = new List<string>();
            public readonly List<PersistedLap> laps = new List<PersistedLap>();
        }
        private static readonly Dictionary<string, PersistedTrack> splitStore =
            new Dictionary<string, PersistedTrack>(StringComparer.Ordinal);
        private static bool splitStoreLoaded = false;
        private static bool splitStoreDirty = false;
        private static float splitStoreNextSaveAt = 0f;
        private readonly List<StoredLap> raceLapHistory = new List<StoredLap>();
        private int raceBestLapHistoryIdx = -1;
        private bool raceHistoryOpen = false;
        private float raceHistoryX = 350f;
        private float raceHistoryY = 140f;
        private float raceHistoryW = 430f;
        private float raceHistoryH = 0f;
        private readonly WindowDrag raceHistoryDrag = new WindowDrag();
        private bool raceHistoryResizing = false;
        private int raceHistoryResizeEdge = 0;
        private int raceHistoryResizeEdgeHover = 0;
        private Vector2 raceHistoryResizeStartMouse = Vector2.zero;
        private Rect raceHistoryResizeStartRect = new Rect(0f, 0f, 0f, 0f);
        private Vector2 raceHistoryScroll = Vector2.zero;
        private int raceHistoryEditIdx = -1;
        private readonly List<string> raceHistoryEditVals = new List<string>();
        private Vector2 raceHistoryEditScroll = Vector2.zero;
        // Lobby-style scrollbar skin for the lap-history editor list.
        private GUIStyle raceScrollbarStyle = null;
        private GUIStyle raceScrollbarThumbStyle = null;
        private Texture2D raceScrollTrackTex = null;
        private Texture2D raceScrollThumbTex = null;
        private Texture2D raceScrollThumbHoverTex = null;
        private bool raceScrollSkinDark = false;
        private float raceGraphFrom = 0f;
        private float raceGraphSpan = 0f;
        private int raceGraphRowsAt = -1;
        private bool raceGraphPanDrag = false;
        private float raceGraphPanStartX = 0f;
        private float raceGraphPanStartFrom = 0f;
        private bool raceGraphCustom = false;
        private HashSet<int> raceGraphSel = new HashSet<int>();
        private static readonly Color[] RaceHistoryColors = new Color[]
        {
            new Color(0.35f, 0.75f, 1f),
            new Color(1f, 0.75f, 0.25f),
            new Color(0.55f, 1f, 0.45f),
            new Color(1f, 0.45f, 0.75f),
            new Color(0.8f, 0.55f, 1f),
            new Color(0.4f, 1f, 0.9f)
        };
    }
}
