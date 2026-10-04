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
using static LiftoffGhostLines.Prefs;

namespace LiftoffGhostLines
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class GhostLinesPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.dapa.liftoff.replayplus";
        public const string PluginName = "Liftoff Replay Plus";
        public const string PluginVersion = "1.5.1";

        public static List<GhostLineEntry> entries = new List<GhostLineEntry>();
        internal static List<GhostLineEntry> recordingCandidates = new List<GhostLineEntry>();
        // Number of candidates created since the list was last cleared. Pruning
        // shrinks recordingCandidates, so its Count would reuse colours/names.
        internal static int recordingCandidateSerial = 0;
        internal static int recordingConstructionThreadId = -1;
        internal static int mainThreadId = -1;
        // Manual GC mode state (set in Awake, consumed by the poller's Update).
        internal static bool gcManualActive = false;
        internal static float nextManualCollectAt = 0f;
        // Replay-UI presence heartbeat: the Harmony prefix on ReplayControls.Update
        // only fires while the native replay UI is actually updating, which is the
        // cheap signal that the (expensive) controls search is worth running.
        internal static float replayUiSeenAt = -999f;
        // Set once the ReplayControls.Update prefix is installed: from then on
        // replayUiSeenAt is an authoritative "replay controls exist" signal.
        internal static bool replayUiHeartbeatPatched = false;
        internal static float leaderboardUiSeenAt = -999f;
        internal static readonly object pendingRecordingsLock = new object();
        internal static readonly Queue<System.WeakReference> pendingRecordings = new Queue<System.WeakReference>();
        public static bool smoothingEnabled = true;
        public static bool speedHeatmapEnabled = false;
        public static bool showDeltaDisplay = true;
        public static bool showNameTags = true;
        public static bool linesThroughObjects = false;
        public static float speedHeatmapMaxSpeedMps = 1f;
        public static float speedHeatmapMinSpeedMps = 0f;
        // Bumped whenever the shared speed range changes, so the stick graph's
        // heatmap (which uses the same range as the lines) knows to redraw.
        public static int speedHeatmapRangeRevision;
        internal const int SMOOTH_STEPS = 8;
        internal const int SMOOTH_MAX_POINTS = 16384;
        internal const float METERS_PER_WORLD_UNIT = 1f;
        internal static GhostLinePoller poller;
        internal static Material throughWallTetherMaterial;
        internal static ManualLogSource pluginLog;
        internal static Harmony harmony;
        public static bool blockDroneReset = false;
        // Set only around the mod's own respawn call (drone switch), so the reset
        // gate that holds the drone during replay/MP freecam doesn't swallow it.
        public static bool allowOwnDroneReset = false;
        // True once the PopupInGameCommunityLeaderboard.Awake hook is bound, so the
        // poller can trust it and skip its fallback scene scans entirely.
        internal static bool lbPopupHookInstalled = false;
        internal static bool inputGateActive = false;
        internal static float inputGateLogAt = 0f;
        // Set once per frame by the poller: true while any uGUI text field (game
        // chat, search boxes, etc.) has focus. The input gate must never block
        // input while this is set, or typing into those fields breaks.
        internal static bool textFieldFocused = false;
        internal static int textFieldFocusedLoggedFrame = -1;
        // >0 while executing inside one of the poller's own Unity callbacks. The
        // Harmony input-gate prefix must let our own Input.GetKey reads through;
        // this is a cheap thread-static scope instead of a per-call StackTrace.
        [System.ThreadStatic] internal static int ourInputScope = 0;
        internal static KeyCode[] suppressedInputKeys = new KeyCode[64];
        internal static int suppressedInputKeyCount = 0;
        internal static int suppressedInputLoggedCount = 0;
        internal static readonly string[] BindingIds =
        {
            "playpause", "restart", "xray", "smooth", "rewind", "ff", "nametags",
            "alllines", "linesthrough", "delta", "heatmap", "loop", "graph", "livegraph", "holdzoom",
            "camreset", "freecamforward", "freecamback", "freecamleft", "freecamright",
            "specpilot1", "specpilot2", "specpilot3", "specpilot4", "specpilot5",
            "pip", "mpfreecam", "mpmenu"
        };

        // The config key 1.4.0 and earlier saved a binding under: its label at the
        // time. Only labels renamed since then need an entry here.
        internal static string LegacyBindingKey(string id)
        {
            switch (id)
            {
                case "rewind": return "Rewind Frame";
                case "ff": return "Fast-Forward Frame";
                default: return BindingLabel(id);
            }
        }

        internal static string BindingLabel(string id)
        {
            switch (id)
            {
                case "playpause": return "Play / Pause";
                case "restart": return "Restart Replay";
                case "xray": return "Toggle X-Ray";
                case "smooth": return "Toggle Smoothing";
                // , and . change the playback speed (the game's rewind and
                // fast-forward buttons), they don't step frames.
                case "rewind": return "Slower Playback";
                case "ff": return "Faster Playback";
                case "nametags": return "Toggle Name Tags";
                case "alllines": return "Toggle All Race Lines";
                case "linesthrough": return "Lines Through Objects";
                case "delta": return "Toggle Delta Display";
                case "heatmap": return "Toggle Speed Heatmap";
                case "loop": return "Toggle Loop Replay";
                case "graph": return "Toggle Stick Graph";
                case "livegraph": return "Toggle Live Input Graph";
                case "holdzoom": return "Toggle Zoom Focus";
                case "camreset": return "Reset Camera";
                case "freecamforward": return "Freecam Forward";
                case "freecamback": return "Freecam Back";
                case "freecamleft": return "Freecam Left";
                case "freecamright": return "Freecam Right";
                case "specpilot1": return "Spectate Pilot 1";
                case "specpilot2": return "Spectate Pilot 2";
                case "specpilot3": return "Spectate Pilot 3";
                case "specpilot4": return "Spectate Pilot 4";
                case "specpilot5": return "Spectate Pilot 5";
                case "pip": return "Toggle Picture in Picture";
                case "mpfreecam": return "Toggle Multiplayer Freecam";
                case "mpmenu": return "Toggle Multiplayer Menu";
                default: return id;
            }
        }

        internal static KeyCode GetDefaultBinding(string id)
        {
            switch (id)
            {
                case "playpause": return KeyCode.Space;
                case "restart": return KeyCode.R;
                case "xray": return KeyCode.X;
                case "smooth": return KeyCode.K;
                case "rewind": return KeyCode.Comma;
                case "ff": return KeyCode.Period;
                case "nametags": return KeyCode.N;
                case "alllines": return KeyCode.L;
                case "linesthrough": return KeyCode.J;
                case "delta": return KeyCode.M;
                case "heatmap": return KeyCode.H;
                case "loop": return KeyCode.Slash;
                case "graph": return KeyCode.G;
                case "livegraph": return KeyCode.None;
                case "holdzoom": return KeyCode.Z;
                case "camreset": return KeyCode.V;
                case "freecamforward": return KeyCode.W;
                case "freecamback": return KeyCode.S;
                case "freecamleft": return KeyCode.A;
                case "freecamright": return KeyCode.D;
                case "specpilot1": return KeyCode.Alpha1;
                case "specpilot2": return KeyCode.Alpha2;
                case "specpilot3": return KeyCode.Alpha3;
                case "specpilot4": return KeyCode.Alpha4;
                case "specpilot5": return KeyCode.Alpha5;
                case "pip": return KeyCode.P;
                // Not F / F10: Liftoff uses them for fisheye and the wind display.
                case "mpfreecam": return KeyCode.E;
                case "mpmenu": return KeyCode.F2;
                default: return KeyCode.None;
            }
        }

        public static KeyCode GetBinding(string id)
        {
            ConfigEntry<KeyCode> entry;
            if (bindingConfigs.TryGetValue(id, out entry))
                return entry.Value;
            return GetDefaultBinding(id);
        }

        public static void SetBinding(string id, KeyCode key)
        {
            ConfigEntry<KeyCode> entry;
            if (bindingConfigs.TryGetValue(id, out entry))
                entry.Value = key;
        }

        public static void ResetAllBindings()
        {
            foreach (var kv in bindingConfigs)
                kv.Value.Value = GetDefaultBinding(kv.Key);
            // Drone bindings default to unbound.
            foreach (var kv in droneBindingConfigs)
                kv.Value.Value = KeyCode.None;
        }

        public static string BindingKeyName(string id)
        {
            return FriendlyKeyName(GetBinding(id));
        }

        public static readonly string DroneBindingPrefix = "drone:";

        public static bool IsDroneBindingId(string id)
        {
            return id != null && id.StartsWith(DroneBindingPrefix, StringComparison.Ordinal);
        }

        public static string DroneBindingLocalId(string id)
        {
            if (!IsDroneBindingId(id))
                return null;
            return id.Substring(DroneBindingPrefix.Length);
        }

        public static KeyCode GetDroneBinding(string localId)
        {
            if (string.IsNullOrEmpty(localId))
                return KeyCode.None;
            ConfigEntry<KeyCode> entry;
            if (droneBindingConfigs.TryGetValue(localId, out entry))
                return entry.Value;
            if (preferencesConfig == null)
                return KeyCode.None;
            try
            {
                entry = preferencesConfig.Bind("Drone Bindings", "Drone_" + localId, KeyCode.None,
                    "Key binding to switch to the drone with content id " + localId + ".");
            }
            catch
            {
                return KeyCode.None;
            }
            // Same mouse-button cleanup as the main bindings.
            if (entry.Value >= KeyCode.Mouse0 && entry.Value <= KeyCode.Mouse6)
                entry.Value = KeyCode.None;
            droneBindingConfigs[localId] = entry;
            return entry.Value;
        }

        public static void SetDroneBinding(string localId, KeyCode key)
        {
            if (string.IsNullOrEmpty(localId))
                return;
            GetDroneBinding(localId);
            ConfigEntry<KeyCode> entry;
            if (droneBindingConfigs.TryGetValue(localId, out entry))
                entry.Value = key;
        }

        public static string DroneBindingKeyName(string localId)
        {
            return FriendlyKeyName(GetDroneBinding(localId));
        }

        public static string FriendlyKeyName(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return ((char)('0' + (key - KeyCode.Alpha0))).ToString();
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return "Numpad " + (key - KeyCode.Keypad0);
            return key.ToString();
        }

        internal static Color[] pilotColors = new Color[]
        {
            Color.cyan,
            new Color(1f, 0.5f, 0f),
            Color.magenta,
            new Color(0.5f, 1f, 0f),
        };

        internal const int ChatPresetCount = 4;
        internal static Color[] mpChatColorPresets = new Color[ChatPresetCount];
        internal static GUIStyle mpChatBarStylePlain;
        internal static GUIStyle mpChatBarStyleBold;
        internal static GUIStyle mpChatBarStyleItalic;

        private void Awake()
        {
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            DontDestroyOnLoad(gameObject);
            pluginLog = Logger;
            ModLog.Initialize(
                Logger,
                Config.Bind("Logging", "Enable Debug Logging", false,
                    "Write detailed Liftoff Replay Plus diagnostics to the BepInEx log."));
            preferencesConfig = Config;
            stickCamEnabledConfig = Config.Bind("Preferences", "Stick Cam Enabled", true,
                "Show the stick-cam overlay during replay.");
            stickCamSizeConfig = Config.Bind("Preferences", "Stick Cam Size", 1f,
                "Stick-cam overlay scale. Valid range: 0.5 to 1.8.");
            playbackUiSizeConfig = Config.Bind("Preferences", "Playback UI Size", 1f,
                "Replay playback overlay scale. Valid range: 0.6 to 1.6.");
            darkModeConfig = Config.Bind("Preferences", "Dark Mode", false,
                "Use the dark replay settings-panel theme.");
            autoDownloadWorkshopConfig = Config.Bind("Preferences", "Auto Download Workshop", false,
                "Automatically download the current multiplayer track/race from the Steam workshop.");
            autoJoinLobbyConfig = Config.Bind("Preferences", "Auto Join Selected Lobbies", false,
                "Automatically join a selected lobby room when a slot opens.");
            autoAlertLobbyConfig = Config.Bind("Preferences", "Lobby Slot Alerts", true,
                "Show a prompt when a selected lobby room gets a free slot.");
            mpMenuOpenConfig = Config.Bind("Preferences", "Multiplayer Menu Open", false,
                "Open the multiplayer menu on joining a room (remembers whether you last opened or closed it).");
            gateSplitsConfig = Config.Bind("Preferences", "Gate Splits Panel", false,
                "Show the per-gate split/delta panel during races.");
            gateSplitsSegmentsConfig = Config.Bind("Preferences", "Gate Splits Segments", true,
                "Show gate splits as gate-to-gate segment times (default) instead of cumulative lap times.");
            lapHistoryVsBestConfig = Config.Bind("Preferences", "Lap History vs Best", true,
                "Compare lap-history graph lines against the best lap. Off: plot absolute lap times (all laps together).");
            smoothDroneTrailsConfig = Config.Bind("Preferences", "Smooth Drone Trails", false,
                "Show smooth custom trails behind replay drones.");
            smoothingEnabledConfig = Config.Bind("Preferences", "Race Line Smoothing", true,
                "Use smoothed race lines by default.");
            showDeltaDisplayConfig = Config.Bind("Preferences", "Delta Display", true,
                "Show the global delta display by default.");
            showNameTagsConfig = Config.Bind("Preferences", "Name Tags", true,
                "Show global drone name tags by default.");
            linesThroughObjectsConfig = Config.Bind("Preferences", "Lines Through Objects", false,
                "Draw race lines through track objects by default.");
            xrayEnabledConfig = Config.Bind("Preferences", "Xray", false,
                "Enable Xray by default during replay.");
            settingsPanelOpenConfig = Config.Bind("Preferences", "Settings Panel Open", false,
                "Restore the replay settings panel's open or closed state.");
            stickCamPositionCustomizedConfig = Config.Bind("Preferences", "Stick Cam Position Customized", false,
                "Whether the stick-cam overlay has a saved custom position.");
            stickCamPositionXConfig = Config.Bind("Preferences", "Stick Cam Position X", 0.5f,
                "Saved stick-cam horizontal position as a normalized screen coordinate.");
            stickCamPositionYConfig = Config.Bind("Preferences", "Stick Cam Position Y", 0.8f,
                "Saved stick-cam vertical position as a normalized screen coordinate.");
            playbackUiPositionCustomizedConfig = Config.Bind("Preferences", "Playback UI Position Customized", false,
                "Whether the playback UI has a saved custom position.");
            playbackUiPositionXConfig = Config.Bind("Preferences", "Playback UI Position X", 0.5f,
                "Saved playback UI horizontal position as a normalized screen coordinate.");
            playbackUiPositionYConfig = Config.Bind("Preferences", "Playback UI Position Y", 0.9f,
                "Saved playback UI vertical position as a normalized screen coordinate.");
            settingsPanelPositionCustomizedConfig = Config.Bind("Preferences", "Settings Panel Position Customized", false,
                "Whether the settings panel has a saved custom position.");
            settingsPanelPositionXConfig = Config.Bind("Preferences", "Settings Panel Position X", 0.7f,
                "Saved settings panel horizontal position as a normalized screen coordinate.");
            settingsPanelPositionYConfig = Config.Bind("Preferences", "Settings Panel Position Y", 0.4f,
                "Saved settings panel vertical position as a normalized screen coordinate.");
            pipEnabledConfig = Config.Bind("Preferences", "Picture in Picture Enabled", false,
                "Show picture-in-picture FPV windows for replay pilots during replay.");
            pipRefreshHzConfig = Config.Bind("Preferences", "Picture in Picture Refresh Rate", 30,
                "Picture-in-picture window refresh rate in Hz (15, 30 or 60).");
            pipRefreshAutoConfig = Config.Bind("Preferences", "Picture in Picture Auto Refresh", true,
                "Automatically pick the picture-in-picture refresh rate based on the number of open windows.");
            pipResolutionScaleConfig = Config.Bind("Preferences", "Picture in Picture Resolution Scale", 0.5f,
                "Picture-in-picture render resolution as a fraction of window size (0.25, 0.5, 0.75 or 1.0).");
            pipFarClipConfig = Config.Bind("Preferences", "Picture in Picture Draw Distance", 100f,
                "Picture-in-picture camera far clip distance in meters (25 to 500).");
            pipShowNamesDeltaConfig = Config.Bind("Preferences", "PiP Names and Delta", true,
                "Show pilot name tags and delta text inside picture-in-picture windows.");
            pipExternalMonitorConfig = Config.Bind("Preferences", "PiP External Monitor", false,
                "Show the first open PiP window in a separate draggable desktop window.");
            gcCollectIntervalConfig = Config.Bind("Preferences", "Manual GC Collect Interval", 20f,
                "Seconds between deliberate garbage collections while Manual GC Mode is on. Lower = less memory held, more frequent micro-pauses.");
            gcIncrementalConfig = Config.Bind("Preferences", "Manual GC Mode", false,
                "Take over garbage collection: automatic GC pauses (the periodic flight stutters) are replaced with one deliberate collection every 'Manual GC Collect Interval' seconds. Disable to restore Unity's automatic GC.");
            // The game ships with incremental GC (3 ms slices), but at its ~10-15 MB/s
            // allocation rate the incremental cycles still finish with a ~26-30 ms
            // frame every few seconds in flight. Manual mode replaces those with one
            // deliberate ~30-40 ms collect per interval (measured 2026-09-25: fewer
            // hitches overall, each slightly longer).
            // What the game's own GC is doing, so Manual GC Mode can be judged
            // against it (the player can ship with incremental GC enabled).
            try
            {
                pluginLog.LogInfo("[GhostLines] Unity GC: mode=" + UnityEngine.Scripting.GarbageCollector.GCMode +
                    " incremental=" + UnityEngine.Scripting.GarbageCollector.isIncremental +
                    " sliceMs=" + (UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds / 1000000.0).ToString("F1"));
            }
            catch { }
            if (gcIncrementalConfig.Value)
                pluginLog.LogInfo("[GhostLines] manual GC requested; collectEvery=" +
                    gcCollectIntervalConfig.Value.ToString("F0") + "s");
            ApplyGcMode();
            chatColorPreset1Config = Config.Bind("Preferences", "Chat Color Preset 1", "",
                "Saved chat color preset 1 (hex, e.g. FF80D9). Empty = unset.");
            chatColorPreset2Config = Config.Bind("Preferences", "Chat Color Preset 2", "",
                "Saved chat color preset 2 (hex, e.g. FF80D9). Empty = unset.");
            chatColorPreset3Config = Config.Bind("Preferences", "Chat Color Preset 3", "",
                "Saved chat color preset 3 (hex, e.g. FF80D9). Empty = unset.");
            chatColorPreset4Config = Config.Bind("Preferences", "Chat Color Preset 4", "",
                "Saved chat color preset 4 (hex, e.g. FF80D9). Empty = unset.");

            ConfigEntry<string>[] presetConfigs = new ConfigEntry<string>[] { chatColorPreset1Config, chatColorPreset2Config, chatColorPreset3Config, chatColorPreset4Config };
            for (int i = 0; i < ChatPresetCount; i++)
            {
                string hex = (presetConfigs[i].Value ?? "").Trim().TrimStart('#');
                Color c;
                if (hex.Length > 0 && ColorUtility.TryParseHtmlString("#" + hex, out c))
                    mpChatColorPresets[i] = c;
                else
                    mpChatColorPresets[i] = new Color(0f, 0f, 0f, 0f);
            }

            // Bindings are keyed by their stable id (labels are display text and may
            // change); values saved under the old label keys move over once.
            bindingConfigs.Clear();
            foreach (string id in BindingIds)
            {
                Prefs.MigrateKey(Config, "Bindings", LegacyBindingKey(id), id);
                bindingConfigs[id] = Config.Bind("Bindings", id, GetDefaultBinding(id),
                    "Key binding for " + BindingLabel(id) + ".");
                // The bindings window used to capture mouse clicks as keys, which
                // left actions on Mouse0/Mouse1 and broke clicking and camera look.
                KeyCode saved = bindingConfigs[id].Value;
                if (saved >= KeyCode.Mouse0 && saved <= KeyCode.Mouse6)
                    bindingConfigs[id].Value = GetDefaultBinding(id);
            }
            // Bind above still saved new keys (SaveOnConfigSet was on); from here
            // every change goes through the debounced save.
            Prefs.Attach(Config);
            Prefs.FlushSave();

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(GhostLinesPlugin));
            EnsurePoller();
            PatchLeaderboardControllerStart();
            PatchReplayControlsSpace();
            PatchRewiredInputGate();
            PatchFlightManagerResetGate();
            CameraViewToggle.Apply(harmony);
            ScreenFadeGate.Apply(harmony);
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Plugin Awake; poller attached");
        }

        internal static void PatchLeaderboardControllerStart()        {
            if (harmony == null)
            {
                pluginLog.LogError("[GhostLines] Harmony was not initialized");
                return;
            }

            try
            {
                System.Reflection.Assembly gameAssembly = typeof(LocalGhostStatesRecording).Assembly;
                System.Type controllerType = null;
                foreach (System.Type type in GameAccess.TypesOf(gameAssembly))
                {
                    if (type == null)
                        continue;
                    const System.Reflection.BindingFlags flags =
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
                    if (type.GetField("dropdownLeaderboardType", flags) != null &&
                        type.GetField("dropdownEnvironmentFilter", flags) != null &&
                        type.GetField("dropdownContentFilter", flags) != null &&
                        type.GetField("dropdownLeaderboardArchive", flags) != null)
                    {
                        controllerType = type;
                        break;
                    }
                }

                if (controllerType == null)
                {
                    pluginLog.LogWarning("[GhostLines] Leaderboard controller type was not found");
                    return;
                }

                System.Reflection.MethodInfo start = AccessTools.Method(controllerType, "Start");
                if (start == null)
                {
                    pluginLog.LogWarning("[GhostLines] Leaderboard controller Start method was not found");
                    return;
                }

                harmony.Patch(
                    start,
                    postfix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(OnLeaderboardControllerStarted)));

                System.Reflection.MethodInfo update = AccessTools.Method(controllerType, "Update");
                if (update != null)
                {
                    harmony.Patch(
                        update,
                        postfix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(OnLeaderboardControllerUpdated)));
                    pluginLog.LogInfo("[GhostLines] Patched leaderboard controller Start and Update: " + controllerType.FullName);
                }
                else
                {
                    pluginLog.LogInfo("[GhostLines] Patched leaderboard controller Start: " + controllerType.FullName + "; Update not found");
                }
            }
            catch (System.Exception ex)
            {
                pluginLog.LogError("[GhostLines] Failed to patch leaderboard controller Start: " + ex);
            }

            try
            {
                // Capture the wired recording consumers + entry objects whenever the
                // game populates leaderboard rows (the MultiplayerLeaderboards scene
                // wires them; the MP community popup does not). Zero polling cost.
                System.Reflection.MethodInfo setData = AccessTools.Method(
                    typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "SetData");
                if (setData != null)
                {
                    harmony.Patch(
                        setData,
                        postfix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(CommunityLeaderboardEntrySetDataPostfix)));
                    pluginLog.LogInfo("[GhostLines] Patched CommunityLeaderboardEntry.SetData for replay-consumer capture");
                }
            }
            catch (System.Exception ex)
            {
                pluginLog.LogWarning("[GhostLines] Failed to patch CommunityLeaderboardEntry.SetData: " + ex.Message);
            }

            try
            {
                // The in-game community leaderboard popup is only ever spawned by the
                // game's UI, so its Awake is a free, authoritative "popup exists"
                // signal. The poller used to find it with a scene-wide
                // FindObjectsOfType twice a second (a stutter on big maps).
                System.Reflection.MethodInfo popupAwake = AccessTools.Method(
                    typeof(Liftoff.Multiplayer.Leaderboards.PopupInGameCommunityLeaderboard), "Awake");
                if (popupAwake != null)
                {
                    harmony.Patch(
                        popupAwake,
                        postfix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(LbPopupAwakePostfix)));
                    lbPopupHookInstalled = true;
                    pluginLog.LogInfo("[GhostLines] Patched PopupInGameCommunityLeaderboard.Awake for popup tracking");
                }
            }
            catch (System.Exception ex)
            {
                pluginLog.LogWarning("[GhostLines] Failed to patch popup Awake: " + ex.Message);
            }

            TryPatchGateSplitsHud();
        }

        // The race HUD hands the game's race participant to a settable property; that
        // setter is where the gate-splits event binding attaches. Resolved by shape
        // (the participant types are obfuscated), so it retries until Assembly-CSharp
        // is loaded and the shape is found.
        internal static bool gateSplitsHudPatched = false;
        internal static bool gateSplitsResolveLogged = false;
        internal static float gateSplitsResolveNextAt = 0f;
        // Per-gate trace: debug logging only. Failures go through LogWarning instead.
        internal static void LogGateSplitsMessage(string message)
        {
            if (pluginLog != null && ModLog.DebugEnabled)
                pluginLog.LogInfo(message);
        }

        internal static void TryPatchGateSplitsHud()
        {
            if (gateSplitsHudPatched || harmony == null)
                return;
            // Shape resolution walks every type in Assembly-CSharp; never let a
            // failed resolve retry per frame (the probe calls this from Update).
            if (Time.unscaledTime < gateSplitsResolveNextAt)
                return;
            gateSplitsResolveNextAt = Time.unscaledTime + 2f;
            if (!GateSplitsBinding.Resolve())
            {
                if (!gateSplitsResolveLogged)
                {
                    gateSplitsResolveLogged = true;
                    pluginLog.LogInfo("[GhostLines] gate-splits event binding unavailable; using trigger fallback");
                }
                return;
            }
            try
            {
                var setter = GateSplitsBinding.HudPlayerProperty.GetSetMethod(true);
                if (setter == null)
                {
                    gateSplitsHudPatched = true;
                    pluginLog.LogWarning("[GhostLines] race HUD player property has no setter; gate-splits events disabled");
                    return;
                }
                harmony.Patch(setter, postfix: new HarmonyMethod(
                    typeof(GhostLinesPlugin).GetMethod(nameof(GateSplitsHudPlayerAssigned),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                gateSplitsHudPatched = true;
                pluginLog.LogInfo("[GhostLines] patched race HUD player setter for gate-splits events");
            }
            catch (System.Exception ex)
            {
                gateSplitsHudPatched = true;
                pluginLog.LogWarning("[GhostLines] failed to patch race HUD for gate splits: " + ex.Message);
            }
        }

        internal static void GateSplitsHudPlayerAssigned(object __instance)
        {
            if (__instance == null)
                return;
            try
            {
                object player = GateSplitsBinding.HudPlayerProperty.GetValue(__instance, null);
                GateSplitsBinding.Attach(player);
                GateSplitsBinding.SetHud(player != null ? __instance : null);
            }
            catch { }
        }

        internal static void CommunityLeaderboardEntrySetDataPostfix(
            Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry __instance)
        {
            if (poller != null)
                poller.CaptureLbRow(__instance);
        }

        internal static void LbPopupAwakePostfix(
            Liftoff.Multiplayer.Leaderboards.PopupInGameCommunityLeaderboard __instance)
        {
            if (poller != null)
                poller.NoteLbPopupInstance(__instance);
        }

        internal static bool flightManagerResetPatched = false;
        internal static float lastResetGateBlockLog = -1f;

        internal static void LogResetGateBlock(System.Reflection.MethodBase method)
        {
            if (Time.unscaledTime - lastResetGateBlockLog > 1f)
            {
                lastResetGateBlockLog = Time.unscaledTime;
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] RESETGATE blocked " + method.Name + " flag=" + blockDroneReset);
            }
        }

        internal static void PatchFlightManagerResetGate()
        {
            if (harmony == null || flightManagerResetPatched)
                return;

            try
            {
                System.Type fmType = AccessTools.TypeByName("FlightManager");
                if (fmType != null)
                {
                    System.Reflection.MethodInfo reset0 = AccessTools.Method(fmType, "Reset", System.Type.EmptyTypes);
                    System.Reflection.MethodInfo reset1 = AccessTools.Method(fmType, "Reset",
                        new System.Type[] { typeof(bool) });
                    System.Reflection.MethodInfo force = AccessTools.Method(fmType, "ForceReset");
                    HarmonyMethod gate = new HarmonyMethod(typeof(GhostLinesPlugin), nameof(FlightManagerResetPrefix));
                    if (reset0 != null)
                        harmony.Patch(reset0, prefix: gate);
                    if (reset1 != null)
                        harmony.Patch(reset1, prefix: gate);
                    if (force != null)
                        harmony.Patch(force, prefix: gate);
                    if (reset0 != null || reset1 != null || force != null)
                        pluginLog.LogInfo("[GhostLines] Patched FlightManager reset gate");
                }

                System.Type checkerType = AccessTools.TypeByName("FlyableZoneChecker");
                if (checkerType != null)
                {
                    System.Reflection.MethodInfo checkerUpdate = AccessTools.Method(checkerType, "Update");
                    if (checkerUpdate != null)
                    {
                        harmony.Patch(checkerUpdate,
                            prefix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(ResetGateVoidPrefix)));
                        pluginLog.LogInfo("[GhostLines] Patched FlyableZoneChecker.Update gate");
                    }
                }

                System.Type noReturnType = AccessTools.TypeByName("FlyableZoneNoReturnResult");
                if (noReturnType != null)
                {
                    System.Reflection.MethodInfo noReturn = AccessTools.Method(noReturnType, "OnDidNotReturn");
                    if (noReturn != null)
                    {
                        harmony.Patch(noReturn,
                            prefix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(ResetGateVoidPrefix)));
                        pluginLog.LogInfo("[GhostLines] Patched FlyableZoneNoReturnResult.OnDidNotReturn gate");
                    }
                }

                flightManagerResetPatched = true;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogError("[GhostLines] Failed to patch FlightManager reset gate: " + ex);
            }
        }

        internal static bool FlightManagerResetPrefix(ref object __result, System.Reflection.MethodBase __originalMethod)
        {
            if (!blockDroneReset || allowOwnDroneReset)
                return true;
            __result = null;
            LogResetGateBlock(__originalMethod);
            return false;
        }

        internal static bool ResetGateVoidPrefix(System.Reflection.MethodBase __originalMethod)
        {
            if (!blockDroneReset || allowOwnDroneReset)
                return true;
            LogResetGateBlock(__originalMethod);
            return false;
        }

        internal static void PatchReplayControlsSpace()
        {
            if (harmony == null)
                return;

            try
            {
                System.Reflection.MethodInfo update = AccessTools.Method(typeof(ReplayControls), "Update");
                if (update == null)
                    return;

                harmony.Patch(
                    update,
                    prefix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(ReplayControlsUpdatePrefix)));
                replayUiHeartbeatPatched = true;
                pluginLog.LogInfo("[GhostLines] Patched ReplayControls.Update for native space suppression");

                System.Reflection.MethodInfo cursorRoutine = AccessTools.Method(typeof(ReplayControls), "RoutineMonitorMouseMovement");
                if (cursorRoutine != null)
                {
                    harmony.Patch(
                        cursorRoutine,
                        prefix: new HarmonyMethod(typeof(GhostLinesPlugin), nameof(ReplayControlsCursorRoutinePrefix)));
                    pluginLog.LogInfo("[GhostLines] Patched ReplayControls.RoutineMonitorMouseMovement for fast cursor hiding");
                }
                else
                {
                    pluginLog.LogWarning("[GhostLines] ReplayControls.RoutineMonitorMouseMovement was not found");
                }
            }
            catch (System.Exception ex)
            {
                pluginLog.LogError("[GhostLines] Failed to patch ReplayControls.Update: " + ex);
            }
        }

        internal static void PatchRewiredInputGate()
        {
            if (harmony == null)
                return;

            try
            {
                System.Type playerType = typeof(Rewired.Player);
                HarmonyMethod gateBool = new HarmonyMethod(typeof(GhostLinesPlugin), nameof(GateRewiredBool));
                HarmonyMethod gateFloat = new HarmonyMethod(typeof(GhostLinesPlugin), nameof(GateRewiredFloat));

                string[] buttonNames = new string[]
                {
                    "GetButton", "GetButtonDown", "GetButtonUp",
                    "GetNegativeButton", "GetNegativeButtonDown", "GetNegativeButtonUp"
                };
                foreach (string buttonName in buttonNames)
                {
                    foreach (System.Type[] signature in new System.Type[][]
                    {
                        new System.Type[] { typeof(int) },
                        new System.Type[] { typeof(string) }
                    })
                    {
                        System.Reflection.MethodInfo mi = playerType.GetMethod(buttonName, signature);
                        if (mi != null)
                            harmony.Patch(mi, prefix: gateBool);
                    }
                }

                foreach (string anyName in new string[] { "GetAnyButton", "GetAnyButtonDown", "GetAnyButtonUp" })
                {
                    System.Reflection.MethodInfo mi = playerType.GetMethod(anyName, System.Type.EmptyTypes);
                    if (mi != null)
                        harmony.Patch(mi, prefix: gateBool);
                }

                foreach (string axisName in new string[] { "GetAxis", "GetAxisRaw" })
                {
                    foreach (System.Type[] signature in new System.Type[][]
                    {
                        new System.Type[] { typeof(int) },
                        new System.Type[] { typeof(string) }
                    })
                    {
                        System.Reflection.MethodInfo mi = playerType.GetMethod(axisName, signature);
                        if (mi != null)
                            harmony.Patch(mi, prefix: gateFloat);
                    }
                }

                pluginLog.LogInfo("[GhostLines] Patched Rewired.Player for freecam input suppression");

                HarmonyMethod gateRawKeyEdge = new HarmonyMethod(typeof(GhostLinesPlugin), nameof(GateRawInputKeyEdge));
                HarmonyMethod gateRawKeyHold = new HarmonyMethod(typeof(GhostLinesPlugin), nameof(GateRawInputKeyHold));
                HarmonyMethod gateRawString = new HarmonyMethod(typeof(GhostLinesPlugin), nameof(GateRawInputString));
                foreach (string rawName in new string[] { "GetKeyDown", "GetKeyUp" })
                {
                    System.Reflection.MethodInfo mi = typeof(UnityEngine.Input).GetMethod(rawName, new System.Type[] { typeof(KeyCode) });
                    if (mi != null)
                        harmony.Patch(mi, prefix: gateRawKeyEdge);
                }
                System.Reflection.MethodInfo holdMi = typeof(UnityEngine.Input).GetMethod("GetKey", new System.Type[] { typeof(KeyCode) });
                if (holdMi != null)
                    harmony.Patch(holdMi, prefix: gateRawKeyHold);
                foreach (string rawName in new string[] { "GetKey", "GetKeyDown", "GetKeyUp" })
                {
                    System.Reflection.MethodInfo mi = typeof(UnityEngine.Input).GetMethod(rawName, new System.Type[] { typeof(string) });
                    if (mi != null)
                        harmony.Patch(mi, prefix: gateRawString);
                }
                pluginLog.LogInfo("[GhostLines] Patched UnityEngine.Input raw keys for freecam input suppression");
            }
            catch (System.Exception ex)
            {
                pluginLog.LogError("[GhostLines] Failed to patch Rewired input gate: " + ex);
            }
        }

        internal static bool GateRewiredBool(Rewired.Player __instance, object[] __args, ref bool __result)
        {
            if (textFieldFocused)
                return true;
            if (!inputGateActive)
            {
                if (!ActionOnGuardedKey(__instance, __args))
                    return true;
                __result = false;
                return false;
            }
            // Buttons are gated by action, not key, so this also swallowed the
            // game's Esc (pause) and F1 (hide UI) whenever freecam was on. Those
            // keys never steer anything; let every button through while one is held.
            if (MenuKeyHeld())
                return true;
            __result = false;
            return false;
        }

        // ---- Hotkey guard ----------------------------------------------------
        // A mod hotkey on a key the game also uses (F = fisheye, F10 = wind) used
        // to trigger both. Keys the mod acted on stay "guarded" from their press
        // until released, and any game button action bound to a guarded keyboard
        // key reads as not pressed. Decided at query time, so it works whichever
        // of the game's scripts or the poller runs first in the frame.
        private static readonly List<KeyCode> guardedKeys = new List<KeyCode>();
        private static readonly List<Rewired.ActionElementMap> guardMapScratch = new List<Rewired.ActionElementMap>();
        private static int guardFrame = -1;

        private static void RefreshGuardedKeys()
        {
            int frame = Time.frameCount;
            if (frame == guardFrame)
                return;
            guardFrame = frame;
            // Keep a key through its release frame so GetButtonUp is covered too.
            for (int i = guardedKeys.Count - 1; i >= 0; i--)
            {
                KeyCode k = guardedKeys[i];
                if (!Input.GetKey(k) && !Input.GetKeyUp(k))
                    guardedKeys.RemoveAt(i);
            }
            if (poller != null)
            {
                try { poller.CollectPressedModHotkeys(guardedKeys); }
                catch { }
            }
        }

        private static bool ActionOnGuardedKey(Rewired.Player player, object[] args)
        {
            RefreshGuardedKeys();
            if (guardedKeys.Count == 0 || player == null || args == null || args.Length == 0)
                return false;
            try
            {
                guardMapScratch.Clear();
                object action = args[0];
                if (action is int)
                    player.controllers.maps.GetButtonMapsWithAction(Rewired.ControllerType.Keyboard, 0, (int)action, true, guardMapScratch);
                else if (action is string)
                    player.controllers.maps.GetButtonMapsWithAction(Rewired.ControllerType.Keyboard, 0, (string)action, true, guardMapScratch);
                for (int i = 0; i < guardMapScratch.Count; i++)
                {
                    Rewired.ActionElementMap map = guardMapScratch[i];
                    if (map != null && guardedKeys.Contains(map.keyCode))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static int menuKeyHeldFrame = -1;
        private static bool menuKeyHeld;

        // Esc or an F key held this frame; cached because Rewired is polled
        // many times per frame.
        private static bool MenuKeyHeld()
        {
            int frame = Time.frameCount;
            if (frame != menuKeyHeldFrame)
            {
                menuKeyHeldFrame = frame;
                bool held = Input.GetKey(KeyCode.Escape);
                for (KeyCode k = KeyCode.F1; !held && k <= KeyCode.F12; k++)
                    held = Input.GetKey(k);
                menuKeyHeld = held;
            }
            return menuKeyHeld;
        }

        internal static bool GateRewiredFloat(ref float __result)
        {
            if (!inputGateActive || textFieldFocused)
                return true;
            __result = 0f;
            return false;
        }

        internal static bool IsOurAssemblyCaller()
        {
            // Cheap thread-static scope check: our own Input.GetKey reads happen
            // inside the poller's Unity callbacks (Update/LateUpdate/OnGUI/camera).
            return ourInputScope > 0;
        }

        internal static void LogBlockedInput(string api, KeyCode key)
        {
            if (suppressedInputLoggedCount < 6)
            {
                suppressedInputLoggedCount++;
                string callerName = "?";
                try
                {
                    System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(4, false);
                    System.Diagnostics.StackFrame frame = st.GetFrame(0);
                    if (frame != null && frame.GetMethod() != null)
                        callerName = frame.GetMethod().DeclaringType.Name + "." + frame.GetMethod().Name;
                }
                catch { }
                pluginLog.LogWarning("[GhostLines] Blocked game input " + api + " key=" + key + " caller=" + callerName);
            }
        }

        internal static bool GateRawInputKeyEdge(KeyCode key, ref bool __result)
        {
            if (!inputGateActive || textFieldFocused)
                return true;
            bool inSet = false;
            for (int i = 0; i < suppressedInputKeyCount; i++)
            {
                if (suppressedInputKeys[i] == key)
                {
                    inSet = true;
                    break;
                }
            }
            if (!inSet)
                return true;
            if (IsOurAssemblyCaller())
                return true;
            LogBlockedInput("GetKeyDown/Up", key);
            __result = false;
            return false;
        }

        internal static bool GateRawInputKeyHold(KeyCode key, ref bool __result)
        {
            if (!inputGateActive || textFieldFocused)
                return true;
            bool inSet = false;
            for (int i = 0; i < suppressedInputKeyCount; i++)
            {
                if (suppressedInputKeys[i] == key)
                {
                    inSet = true;
                    break;
                }
            }
            if (!inSet)
                return true;
            if (IsOurAssemblyCaller())
                return true;
            LogBlockedInput("GetKey", key);
            __result = false;
            return false;
        }

        // Maps a Unity key name ("w", "1", "[1]") to its KeyCode; None when the
        // name is not a single letter/digit/keypad-digit key.
        internal static KeyCode KeyCodeFromInputName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return KeyCode.None;
            if (name.Length == 1)
            {
                char c = char.ToLowerInvariant(name[0]);
                if (c >= 'a' && c <= 'z')
                    return (KeyCode)((int)KeyCode.A + (c - 'a'));
                if (c >= '0' && c <= '9')
                    return (KeyCode)((int)KeyCode.Alpha0 + (c - '0'));
            }
            else if (name.Length == 3 && name[0] == '[' && name[2] == ']' && name[1] >= '0' && name[1] <= '9')
                return (KeyCode)((int)KeyCode.Keypad0 + (name[1] - '0'));
            return KeyCode.None;
        }

        internal static bool GateRawInputString(string name, ref bool __result)
        {
            if (!inputGateActive || textFieldFocused)
                return true;
            // Only block names that resolve to a suppressed key, same as the KeyCode overloads.
            KeyCode key = KeyCodeFromInputName(name);
            if (key == KeyCode.None)
                return true;
            bool inSet = false;
            for (int i = 0; i < suppressedInputKeyCount; i++)
            {
                if (suppressedInputKeys[i] == key)
                {
                    inSet = true;
                    break;
                }
            }
            if (!inSet)
                return true;
            if (IsOurAssemblyCaller())
                return true;
            LogBlockedInput("GetKey(string)", key);
            __result = false;
            return false;
        }

        internal static bool ReplayControlsUpdatePrefix()
        {
            // Heartbeat: this prefix only runs while the native replay UI exists.
            GhostLinesPlugin.replayUiSeenAt = Time.unscaledTime;
            if (poller == null || !poller.NativeReplayReady)
                return true;
            if (poller.IsBindingListening)
                return true;
            if (!Input.GetKeyDown(KeyCode.Space))
                return true;
            poller.MarkBindingCaptureFrame();
            return false;
        }        private static bool ReplayControlsCursorRoutinePrefix(ReplayControls __instance, ref System.Collections.IEnumerator __result)
        {
            CanvasGroup canvasGroup = null;
            try
            {
                canvasGroup = AccessTools.Field(typeof(ReplayControls), "canvasGroup").GetValue(__instance) as CanvasGroup;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogWarning("[GhostLines] Could not read ReplayControls.canvasGroup: " + ex.Message);
            }
            __result = ReplayCursorEnforcer(canvasGroup);
            return false;
        }

        internal static System.Collections.IEnumerator ReplayCursorEnforcer(CanvasGroup canvasGroup)
        {
            Vector3 previousMousePosition = Input.mousePosition;
            float timer = 0f;
            const float timeUntilHide = 0.6f;
            const float fadeInSpeed = 8f;
            const float fadeOutSpeed = 1f;
            bool isVisible = true;
            while (true)
            {
                Vector3 currentMouse = Input.mousePosition;
                bool mouseMoved = Vector3.Distance(previousMousePosition, currentMouse) > float.Epsilon ||
                    Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
                bool pluginUiNeedsCursor = poller != null && poller.CursorNeededByPlugin();
                if (mouseMoved || pluginUiNeedsCursor)
                {
                    isVisible = true;
                    timer = 0f;
                }
                else if (isVisible)
                {
                    timer += Time.unscaledDeltaTime;
                    if (timer > timeUntilHide)
                    {
                        isVisible = false;
                        timer = 0f;
                    }
                }
                previousMousePosition = currentMouse;
                if (isVisible)
                {
                    Cursor.visible = true;
                    if (canvasGroup != null)
                    {
                        float alpha = Mathf.Clamp01(canvasGroup.alpha + Time.unscaledDeltaTime * fadeInSpeed);
                        if (Mathf.Abs(alpha - canvasGroup.alpha) > 0.0001f)
                            canvasGroup.alpha = alpha;
                    }
                }
                else
                {
                    Cursor.visible = false;
                    if (canvasGroup != null)
                    {
                        float alpha = Mathf.Clamp01(canvasGroup.alpha - Time.unscaledDeltaTime * fadeOutSpeed);
                        if (Mathf.Abs(alpha - canvasGroup.alpha) > 0.0001f)
                            canvasGroup.alpha = alpha;
                    }
                }
                yield return null;
            }
        }

        internal static void OnLeaderboardControllerStarted(Component __instance)
        {
            // The leaderboard is where "watch replay" is triggered from — arm the
            // controls search for a while after it appears.
            GhostLinesPlugin.leaderboardUiSeenAt = Time.unscaledTime;
            pluginLog.LogInfo("[GhostLines] Leaderboard controller Start postfix: " + __instance.GetType().FullName);
            EnsurePoller();
            if (poller == null || poller.gameObject == null)
            {
                pluginLog.LogError("[GhostLines] Could not attach poller to leaderboard controller");
                return;
            }

            bool attached = false;
            poller.ResetLbCaptureForNewScene();
            try { attached = poller.TryAttachLeaderboardSearch(__instance.gameObject.scene); } catch { }
            poller.NoteLbForceController(__instance);
            poller.PersistLbController(__instance);
            if (!attached)
            {
                try { poller.AttachLeaderboardSearchFromController(__instance); } catch { }
            }
        }

        internal static void OnLeaderboardControllerUpdated(Component __instance)
        {
            EnsurePoller();
            if (poller != null)
            {
                // The persisted controller keeps updating in other scenes; only act
                // on it while the leaderboard screen is loaded (not in a content scene).
                if (GhostLinePoller.IsContentSceneActive() || !GhostLinePoller.IsLeaderboardSceneLoaded())
                    return;
                // Only the live screen's controller may attach. The carried-over one
                // (moved to DontDestroyOnLoad by a popup "Load Replay") also updates
                // here; letting both attach made each call see "a different dropdown"
                // and run CleanupLeaderboardSearch every frame, which cancelled every
                // drag-select before it could start.
                if (__instance == null || __instance.gameObject.scene.name != "MultiplayerLeaderboards")
                    return;
                poller.NoteLbForceController(__instance);
                try { poller.AttachLeaderboardSearchFromController(__instance); } catch { }
            }
        }

        // Lifecycle: this plugin MonoBehaviour is bootstrap only. The game tears
        // its GameObject down right after the chainloader finishes, so it must not
        // own any per-frame work, scene callbacks or teardown (instance Update /
        // sceneLoaded / willRenderCanvases handlers here never ran, and an OnDestroy
        // GC restore flipped GC back to automatic at startup). Everything that has
        // to live for the session is either static (Harmony patches, the
        // OnPollerSceneLoadedStatic hook below) or owned by GhostLinePoller, whose
        // GameObject is DontDestroyOnLoad.

        // Restores Unity's automatic GC if the manual-GC mode was enabled. Safe to
        // call more than once / from either teardown path.
        internal static void RestoreGcMode()
        {
            if (!gcManualActive)
                return;
            try
            {
                UnityEngine.Scripting.GarbageCollector.GCMode =
                    UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
                pluginLog.LogInfo("[GhostLines] GC mode restored to Enabled on teardown");
            }
            catch { }
            gcManualActive = false;
        }

        // Applies the configured manual GC mode. Idempotent; the collect deadline is
        // only set on the transition into manual mode. A spurious teardown early in
        // startup used to leave the whole session on automatic GC (the small
        // periodic stutters), and nothing ever re-applied manual mode.
        internal static float nextGcReapplyAt = 0f;
        internal static void ApplyGcMode()
        {
            nextGcReapplyAt = Time.unscaledTime + 5f;
            if (gcIncrementalConfig == null || !gcIncrementalConfig.Value)
                return;
            try
            {
                var before = UnityEngine.Scripting.GarbageCollector.GCMode;
                UnityEngine.Scripting.GarbageCollector.GCMode =
                    UnityEngine.Scripting.GarbageCollector.Mode.Manual;
                bool active = UnityEngine.Scripting.GarbageCollector.GCMode ==
                    UnityEngine.Scripting.GarbageCollector.Mode.Manual;
                bool transitioned = active && !gcManualActive;
                if (!gcManualActive || active != gcManualActive)
                    pluginLog.LogInfo("[GhostLines] GC mode " + before + " -> " +
                        UnityEngine.Scripting.GarbageCollector.GCMode +
                        " manual=" + active +
                        " collectEvery=" + (gcCollectIntervalConfig != null ? gcCollectIntervalConfig.Value.ToString("F0") : "?") + "s");
                gcManualActive = active;
                // Schedule the first collect only when manual mode is newly entered.
                // Re-scheduling on every call pushed the deadline forward forever
                // whenever this ran each frame, so GC.Collect never ran.
                if (transitioned)
                    nextManualCollectAt = Time.unscaledTime +
                        System.Math.Max(5f, gcCollectIntervalConfig != null ? gcCollectIntervalConfig.Value : 20f);
            }
            catch (System.Exception gcEx)
            {
                pluginLog.LogInfo("[GhostLines] manual GC apply EX " + gcEx.Message);
            }
        }

        internal static bool pollerSceneHandlerRegistered = false;

        // Load-bearing: this static sceneLoaded hook is never unsubscribed on
        // purpose. It is what recreates the poller if its GameObject is ever
        // destroyed, since no instance callback on the plugin survives startup.
        internal static void OnPollerSceneLoadedStatic(Scene scene, LoadSceneMode mode)
        {
            EnsurePoller();
        }

        internal static void EnsurePollerSceneHandler()
        {
            if (pollerSceneHandlerRegistered)
                return;
            pollerSceneHandlerRegistered = true;
            SceneManager.sceneLoaded += OnPollerSceneLoadedStatic;
        }

        // Does not touch GC: it runs every frame from the leaderboard controller
        // Update postfix. GC mode is applied in Awake and re-applied by the poller.
        internal static void EnsurePoller()
        {
            if (poller != null && poller.gameObject != null)
            {
                EnsurePollerSceneHandler();
                return;
            }

            GameObject obj = new GameObject("GhostLinesPoller");
            UnityEngine.Object.DontDestroyOnLoad(obj);

            poller = obj.AddComponent<GhostLinePoller>();
            // One singleton: Instance is valid from creation, not from the first
            // Update (preference saves and pilot-name fallbacks read it earlier).
            GhostLinePoller.Instance = poller;
            poller.LoadSavedPreferences();
            EnsurePollerSceneHandler();
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP EnsurePoller created poller object");
            pluginLog?.LogInfo("[GhostLines] Poller attached to " + obj.name);
        }

        internal static void SaveDisplayPreferences(bool stickCamEnabled, float stickCamSize, float playbackUiSize, bool darkMode)
        {
            if (stickCamEnabledConfig == null)
                return;

            stickCamEnabledConfig.Value = stickCamEnabled;
            stickCamSizeConfig.Value = stickCamSize;
            playbackUiSizeConfig.Value = playbackUiSize;
            darkModeConfig.Value = darkMode;
            Prefs.RequestSave();
        }

        internal static void SaveAutoDownloadWorkshopPref(bool enabled)
        {
            if (autoDownloadWorkshopConfig == null)
                return;
            autoDownloadWorkshopConfig.Value = enabled;
            Prefs.RequestSave();
        }

        internal static void SaveSmoothDroneTrails(bool enabled)
        {
            if (smoothDroneTrailsConfig == null)
                return;
            smoothDroneTrailsConfig.Value = enabled;
            Prefs.RequestSave();
        }

        public static void SaveNameTagsPreference()
        {
            if (showNameTagsConfig != null)
                showNameTagsConfig.Value = showNameTags;
        }

        public static void SaveGlobalDisplayPreferences()
        {
            if (smoothingEnabledConfig == null)
                return;
            smoothingEnabledConfig.Value = smoothingEnabled;
            showDeltaDisplayConfig.Value = showDeltaDisplay;
            // Name Tags is saved only by an explicit toggle (SaveNameTagsPreference):
            // the MP per-pilot tag radios mirror into showNameTags for the session.
            linesThroughObjectsConfig.Value = linesThroughObjects;
            if (GhostLinePoller.Instance != null)
                xrayEnabledConfig.Value = GhostLinePoller.Instance.IsXrayEnabled;
            Prefs.RequestSave();
        }

        public static void LoadGlobalDisplayPreferences(out bool smoothing, out bool delta, out bool nameTags,
            out bool throughObjects, out bool xray)
        {
            smoothing = smoothingEnabledConfig == null || smoothingEnabledConfig.Value;
            delta = showDeltaDisplayConfig == null || showDeltaDisplayConfig.Value;
            nameTags = showNameTagsConfig == null || showNameTagsConfig.Value;
            throughObjects = linesThroughObjectsConfig != null && linesThroughObjectsConfig.Value;
            xray = xrayEnabledConfig != null && xrayEnabledConfig.Value;
        }

        public static void SetDeltaDisplay(bool enabled)
        {
            showDeltaDisplay = enabled;
            SaveGlobalDisplayPreferences();
            poller?.MarkPiPWindowsDirty();
        }

        internal static void SaveSettingsPanelOpen(bool open)
        {
            if (settingsPanelOpenConfig == null)
                return;

            settingsPanelOpenConfig.Value = open;
            if (GhostLinePoller.Instance != null)
                GhostLinePoller.Instance.SetSavedSettingsPanelOpen(open);
            Prefs.RequestSave();
        }

        internal static void SaveOverlayPositions(bool stickCustomized, float stickX, float stickY,
            bool playbackCustomized, float playbackX, float playbackY,
            bool settingsCustomized, float settingsX, float settingsY)
        {
            if (stickCamPositionCustomizedConfig == null)
                return;

            stickCamPositionCustomizedConfig.Value = stickCustomized;
            stickCamPositionXConfig.Value = Mathf.Clamp01(stickX);
            stickCamPositionYConfig.Value = Mathf.Clamp01(stickY);
            playbackUiPositionCustomizedConfig.Value = playbackCustomized;
            playbackUiPositionXConfig.Value = Mathf.Clamp01(playbackX);
            playbackUiPositionYConfig.Value = Mathf.Clamp01(playbackY);
            settingsPanelPositionCustomizedConfig.Value = settingsCustomized;
            settingsPanelPositionXConfig.Value = Mathf.Clamp01(settingsX);
            settingsPanelPositionYConfig.Value = Mathf.Clamp01(settingsY);
            Prefs.RequestSave();
        }



        [HarmonyPatch(typeof(LocalGhostStatesRecording), MethodType.Constructor)]
        [HarmonyPostfix]
        public static void OnRecordingConstructed(LocalGhostStatesRecording __instance)
        {
            int threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int previousThreadId = System.Threading.Interlocked.CompareExchange(
                ref recordingConstructionThreadId, threadId, -1);
            if (previousThreadId >= 0 && previousThreadId != threadId)
                Debug.LogWarning("[GhostLines] Replay recordings were constructed on multiple threads");
            if (__instance == null)
                return;
            if (mainThreadId >= 0 && threadId != mainThreadId)
            {
                lock (pendingRecordingsLock)
                    pendingRecordings.Enqueue(new System.WeakReference(__instance));
                return;
            }
            AddRecordingCandidate(__instance);
        }

        internal static void DrainPendingRecordings()
        {
            while (true)
            {
                System.WeakReference weak = null;
                lock (pendingRecordingsLock)
                {
                    if (pendingRecordings.Count == 0)
                        return;
                    weak = pendingRecordings.Dequeue();
                }
                var recording = weak.Target as LocalGhostStatesRecording;
                if (recording != null)
                    AddRecordingCandidate(recording);
            }
        }

        public static void AddRecordingCandidate(LocalGhostStatesRecording recording)
        {
            if (recording == null)
                return;
            EnsurePoller();
            foreach (var existing in recordingCandidates)
            {
                if (existing != null && object.ReferenceEquals(existing.GetRecording(), recording))
                    return;
            }

            var entry = new GhostLineEntry
            {
                hash = recording.GetHashCode(),
                recordingRef = new System.WeakReference(recording),
                visible = false,
                color = pilotColors[recordingCandidateSerial % pilotColors.Length],
                pilotName = "Pilot " + (recordingCandidateSerial + 1)
            };
            recordingCandidateSerial++;
            string resolvedName = ResolvePilotName(recording, entry.pilotName);
            if (!string.IsNullOrWhiteSpace(resolvedName))
                entry.pilotName = resolvedName;
            Color? resolvedColor = ResolvePilotColor(recording);
            if (resolvedColor.HasValue)
                entry.color = resolvedColor.Value;

            recordingCandidates.Add(entry);
            GhostLineEntry.entriesEpoch++;
            if (poller != null)
                poller.replayEntriesDirty = true;
            if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Replay recording candidate accepted: {entry.pilotName}");
        }

        // The recorder stores a state every 0.1 s on the physics tick, but some
        // states are added off that grid (read from the Transform at frame time,
        // e.g. around gate events). Two states a few ms apart whose positions
        // disagree turn into a huge apparent speed inside the pose spline and make
        // the drone hitch there. Ghost poses use only the regular grid: a state is
        // kept when it is at least half a grid step after the last kept one.
        private const float POSE_MIN_SAMPLE_GAP = 0.05f;

        internal static void BuildPoseSamples(GhostLineEntry entry)
        {
            float[] t = entry.stateTimes;
            int n = t.Length;
            var times = new List<float>(n);
            var pos = new List<Vector3>(n);
            var rot = new List<Quaternion>(n);
            int close = 0;
            float minGap = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    float gap = t[i] - t[i - 1];
                    if (gap < minGap) minGap = gap;
                    if (gap < POSE_MIN_SAMPLE_GAP) close++;
                }
                if (times.Count > 0 && t[i] - times[times.Count - 1] < POSE_MIN_SAMPLE_GAP)
                {
                    // Keep the final state so the pose still ends where the recording ends.
                    if (i == n - 1 && times.Count > 1)
                    {
                        times[times.Count - 1] = t[i];
                        pos[pos.Count - 1] = entry.originalPositions[i];
                        rot[rot.Count - 1] = entry.originalRotations[i];
                    }
                    continue;
                }
                times.Add(t[i]);
                pos.Add(entry.originalPositions[i]);
                rot.Add(entry.originalRotations[i]);
            }
            entry.poseTimes = times.ToArray();
            entry.posePositions = pos.ToArray();
            entry.poseRotations = rot.ToArray();
            float rmsMs, maxMs;
            int retimed = RetimePoseSamples(entry.poseTimes, entry.posePositions, out rmsMs, out maxMs);
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] pose samples " + entry.pilotName + ": states=" + n +
                " offGrid(<" + POSE_MIN_SAMPLE_GAP.ToString("F2") + "s)=" + close +
                " minGap=" + (minGap == float.MaxValue ? "n/a" : (minGap * 1000f).ToString("F1") + "ms") +
                " kept=" + entry.poseTimes.Length +
                " retimed=" + retimed + " adjRms=" + rmsMs.ToString("F2") + "ms adjMax=" + maxMs.ToString("F2") + "ms");
        }

        // A recorded position can belong to a slightly different moment than its
        // timestamp (the drone sim updates the body on its own schedule, the
        // recorder samples on the 0.1 s physics grid). That timing noise shows up
        // as small, irregular speed surges along the path. Positions and rotations
        // stay exactly as recorded; only the knot times are re-estimated so that
        // distance-along-path is a smooth function of time: a local quadratic fit
        // of distance vs time (7 samples, ~0.6 s) gives a smoothed distance curve,
        // and each sample gets the time at which that curve reaches its own
        // distance. Corrections are capped, stop at teleports and skip hovering.
        private const float POSE_RETIME_MAX_ADJUST = 0.03f;
        private const float POSE_TELEPORT_SPEED = 150f;

        private static int RetimePoseSamples(float[] t, Vector3[] p, out float rmsMs, out float maxMs)
        {
            rmsMs = 0f;
            maxMs = 0f;
            int n = t.Length;
            if (n < 7)
                return 0;
            // Arc length, with teleport segments splitting the path into groups.
            float[] d = new float[n];
            int[] group = new int[n];
            for (int i = 1; i < n; i++)
            {
                float h = t[i] - t[i - 1];
                float seg = Vector3.Distance(p[i], p[i - 1]);
                bool teleport = h <= 0.0001f || seg > POSE_TELEPORT_SPEED * h;
                group[i] = teleport ? group[i - 1] + 1 : group[i - 1];
                d[i] = d[i - 1] + (teleport ? 0f : seg);
            }
            // Smoothed distance at each original time (local quadratic least squares).
            float[] ds = new float[n];
            for (int i = 0; i < n; i++)
            {
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
                int count = 0;
                for (int j = i - 3; j <= i + 3; j++)
                {
                    if (j < 0 || j >= n || group[j] != group[i])
                        continue;
                    double x = t[j] - t[i];
                    double x2 = x * x;
                    s0 += 1; s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
                    y0 += d[j]; y1 += d[j] * x; y2 += d[j] * x2;
                    count++;
                }
                ds[i] = d[i];
                if (count < 5)
                    continue;
                // Solve [s0 s1 s2; s1 s2 s3; s2 s3 s4] * [a b c] = [y0 y1 y2] for a.
                double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
                if (System.Math.Abs(det) < 1e-12)
                    continue;
                double detA = y0 * (s2 * s4 - s3 * s3) - s1 * (y1 * s4 - s3 * y2) + s2 * (y1 * s3 - s2 * y2);
                ds[i] = (float)(detA / det);
            }
            // New knot time for each sample: where the smoothed curve reaches d[i].
            float[] nt = new float[n];
            int changed = 0;
            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                nt[i] = t[i];
                bool interior = i > 0 && i < n - 1 && group[i - 1] == group[i] && group[i + 1] == group[i];
                if (!interior)
                    continue;
                float speed = (d[i + 1] - d[i - 1]) / (t[i + 1] - t[i - 1]);
                if (speed < 2f)
                    continue; // hovering/stopped: distance says nothing about time
                // Search the neighbouring smoothed segments for d[i].
                float found = float.NaN;
                for (int j = System.Math.Max(0, i - 2); j < System.Math.Min(n - 1, i + 2); j++)
                {
                    if (group[j] != group[i] || group[j + 1] != group[i])
                        continue;
                    float a = ds[j], b = ds[j + 1];
                    if (b <= a)
                        continue;
                    if (d[i] >= a && d[i] <= b)
                    {
                        found = t[j] + (t[j + 1] - t[j]) * (d[i] - a) / (b - a);
                        break;
                    }
                }
                if (float.IsNaN(found))
                    continue;
                float adj = Mathf.Clamp(found - t[i], -POSE_RETIME_MAX_ADJUST, POSE_RETIME_MAX_ADJUST);
                nt[i] = t[i] + adj;
            }
            // Keep knots strictly increasing (corrections are <= 30 ms against a
            // ~100 ms grid, so this only guards degenerate data).
            for (int i = 1; i < n; i++)
                nt[i] = System.Math.Max(nt[i], nt[i - 1] + 0.005f);
            for (int i = 0; i < n; i++)
            {
                float adjMs = (nt[i] - t[i]) * 1000f;
                if (System.Math.Abs(adjMs) > 0.01f)
                    changed++;
                sumSq += adjMs * adjMs;
                if (System.Math.Abs(adjMs) > maxMs)
                    maxMs = System.Math.Abs(adjMs);
                t[i] = nt[i];
            }
            rmsMs = (float)System.Math.Sqrt(sumSq / n);
            return changed;
        }

        public static void TryBuildLine(GhostLineEntry entry)
        {
            if (entry.built) return;
            var recording = entry.GetRecording();
            if (recording == null) return;

            string resolvedName = CleanPilotName(ResolvePilotName(recording, entry.pilotName));
            if (!string.IsNullOrWhiteSpace(resolvedName) && resolvedName != entry.pilotName)
            {
                entry.pilotName = resolvedName;
                GhostLineEntry.entriesEpoch++;
                if (entry.markerText != null)
                    entry.markerText.text = entry.pilotName;
            }

            var states = recording.states;
            if (states == null || states.Count < 2) return;

            entry.originalPositions = new Vector3[states.Count];
            entry.originalRotations = new Quaternion[states.Count];
            entry.originalInputs = new Vector4[states.Count];
            entry.stateTimes = new float[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                entry.originalPositions[i] = states[i].position;
                entry.originalRotations[i] = states[i].rotation;
                entry.originalInputs[i] = states[i].input;
                entry.stateTimes[i] = states[i].timestamp;
            }

            BuildPoseSamples(entry);

            entry.pathDistances = new float[states.Count];
            entry.pathDistances[0] = 0f;
            for (int i = 1; i < states.Count; i++)
                entry.pathDistances[i] = entry.pathDistances[i - 1] + Vector3.Distance(states[i].position, states[i - 1].position);
            entry.speedSamples = BuildPoseSpeedSamples(entry.poseTimes, entry.posePositions);


            try
            {
                var lapIndices = recording.lapStartIndices;
                if (lapIndices != null && lapIndices.Count > 1 && lapIndices[1] < states.Count)
                {
                    float firstT = states[0].timestamp;
                    float lapT = states[lapIndices[1]].timestamp;
                    entry.lapStartOffset = lapT - firstT;
                    entry.lapStartDistance = entry.pathDistances[lapIndices[1]];
                    entry.lapTotalDistance = entry.pathDistances[states.Count - 1] - entry.lapStartDistance;
                    entry.finishTime = states[states.Count - 1].timestamp - firstT - entry.lapStartOffset;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Lap offset={entry.lapStartOffset:F3}s finish={entry.finishTime:F3}s (index {lapIndices[1]})");
                }
                else
                {
                    entry.lapStartDistance = 0f;
                    entry.lapTotalDistance = entry.pathDistances[states.Count - 1];
                    entry.finishTime = -1f;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] lapStartIndices count={lapIndices?.Count ?? 0}");
                }
            }
            catch { }

            entry.built = true;
            // The stick graph heatmap shares the range, so keep it current even
            // while the line heatmap is off.
            if (speedHeatmapEnabled)
                RebuildAllSpeedHeatmaps();
            else
            {
                UpdateSpeedHeatmapRange();
                RebuildLine(entry);
            }
        }

        public static void RebuildLine(GhostLineEntry entry)
        {
            Vector3[] positions = smoothingEnabled
                ? CatmullRomSmooth(entry.originalPositions, SMOOTH_STEPS)
                : entry.originalPositions;

            LineRenderer line = entry.lineObject != null ? entry.lineObject.GetComponent<LineRenderer>() : null;
            if (line == null)
            {
                DestroyLineResources(entry);

                entry.lineObject = new GameObject("GhostLine_" + entry.hash);
                entry.lineObject.SetActive(entry.visible);

                line = entry.lineObject.AddComponent<LineRenderer>();
                line.positionCount = positions.Length;
                line.startWidth = 0.12f;
                line.endWidth = 0.12f;
                line.useWorldSpace = true;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.textureMode = LineTextureMode.Stretch;

                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null)
                    shader = Shader.Find("Standard");
                if (shader != null)
                {
                    entry.lineMaterial = new Material(shader);
                    entry.lineMaterial.SetInt("_SrcBlend", 5);
                    entry.lineMaterial.SetInt("_DstBlend", 10);
                    entry.lineMaterial.SetInt("_Cull", 0);
                    entry.lineMaterial.SetInt("_ZWrite", 0);
                    entry.lineMaterial.SetInt("_ZTest", linesThroughObjects
                        ? (int)UnityEngine.Rendering.CompareFunction.Always
                        : (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                    entry.lineMaterial.SetColor("_Color", Color.white);
                    entry.lineMaterial.renderQueue = 4000;
                    line.sharedMaterial = entry.lineMaterial;
                }
            }
            else
            {
                if (entry.lineObject != null)
                    entry.lineObject.SetActive(entry.visible);
                if (entry.lineMaterial != null)
                {
                    entry.lineMaterial.SetInt("_ZTest", linesThroughObjects
                        ? (int)UnityEngine.Rendering.CompareFunction.Always
                        : (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                    entry.lineMaterial.SetColor("_Color", Color.white);
                }
            }

            if (speedHeatmapEnabled && ApplySpeedHeatmapTexture(entry, line, positions))
            {
                // Per-vertex colours come from the texture; the gradient stays white.
                line.textureMode = (LineTextureMode)2; // DistributePerSegment: u = vertex index / (count - 1)
                line.colorGradient = CreateSolidGradient(Color.white);
            }
            else if (speedHeatmapEnabled)
            {
                // Fallback when no texture-capable line shader exists: 8 gradient keys.
                line.textureMode = (LineTextureMode)2;
                line.colorGradient = BuildSpeedHeatmapGradient(entry);
            }
            else
            {
                DestroyHeatmapTexture(entry);
                Shader coloredShader = Shader.Find("Hidden/Internal-Colored");
                if (coloredShader != null && entry.lineMaterial != null)
                {
                    entry.lineMaterial.shader = coloredShader;
                    entry.lineMaterial.renderQueue = 4000;
                }
                if (entry.lineMaterial != null)
                    entry.lineMaterial.mainTexture = null;
                line.textureMode = LineTextureMode.Stretch;
                line.colorGradient = CreateSolidGradient(entry.color);
                line.startColor = entry.color;
                line.endColor = entry.color;
            }

            line.positionCount = positions.Length;
            line.SetPositions(positions);
            BuildNameMarker(entry);
        }

        internal static void DestroyLineResources(GhostLineEntry entry)
        {
            if (entry == null) return;

            if (entry.lineObject != null)
                Destroy(entry.lineObject);
            entry.lineObject = null;

            if (entry.lineMaterial != null)
                Destroy(entry.lineMaterial);
            entry.lineMaterial = null;
            DestroyHeatmapTexture(entry);
        }

        internal static void DestroyHeatmapTexture(GhostLineEntry entry)
        {
            if (entry == null || entry.heatmapTex == null)
                return;
            if (entry.lineMaterial != null && entry.lineMaterial.mainTexture == entry.heatmapTex)
                entry.lineMaterial.mainTexture = null;
            Destroy(entry.heatmapTex);
            entry.heatmapTex = null;
        }

        private const int HEATMAP_TEX_MAX_WIDTH = 8192;
        private static Shader heatmapLineShader;
        private static bool heatmapLineShaderResolved;

        // A LineRenderer gradient holds at most 8 colour keys, so a lap's heatmap
        // used to be 8 evenly spaced samples. Instead the line gets a 1-texel-high
        // texture holding the colour at every vertex (DistributePerSegment maps
        // u = vertexIndex / (count - 1)), drawn with UI/Default: it multiplies
        // texture by vertex colour and exposes its depth test as
        // unity_GUIZTestMode, so "Lines Through Objects" still works.
        internal static bool ApplySpeedHeatmapTexture(GhostLineEntry entry, LineRenderer line, Vector3[] positions)
        {
            if (entry.lineMaterial == null || positions == null || positions.Length < 2 ||
                entry.speedSamples == null || entry.posePositions == null || entry.poseTimes == null ||
                entry.posePositions.Length < 2)
                return false;
            if (!heatmapLineShaderResolved)
            {
                heatmapLineShaderResolved = true;
                heatmapLineShader = Shader.Find("UI/Default");
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] heatmap line shader UI/Default found=" + (heatmapLineShader != null));
            }
            if (heatmapLineShader == null)
                return false;

            Color[] vertexColors = ComputeHeatmapVertexColors(entry, positions);
            int n = vertexColors.Length;
            int width = Mathf.Clamp(n, 2, HEATMAP_TEX_MAX_WIDTH);
            if (entry.heatmapTex == null || entry.heatmapTex.width != width)
            {
                DestroyHeatmapTexture(entry);
                entry.heatmapTex = new Texture2D(width, 1, TextureFormat.RGBA32, false, false);
                entry.heatmapTex.wrapMode = TextureWrapMode.Clamp;
                entry.heatmapTex.filterMode = FilterMode.Bilinear;
                entry.heatmapTex.hideFlags = HideFlags.HideAndDontSave;
            }
            Color[] texels = new Color[width];
            for (int x = 0; x < width; x++)
            {
                // Texel centre -> fractional vertex index along the line.
                float f = (x + 0.5f) / width * (n - 1);
                int i0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, n - 1);
                int i1 = Mathf.Min(i0 + 1, n - 1);
                texels[x] = Color.Lerp(vertexColors[i0], vertexColors[i1], f - i0);
            }
            entry.heatmapTex.SetPixels(texels);
            entry.heatmapTex.Apply(false, false);

            Material m = entry.lineMaterial;
            if (m.shader != heatmapLineShader)
                m.shader = heatmapLineShader;
            m.renderQueue = 4000;
            m.mainTexture = entry.heatmapTex;
            m.SetColor("_Color", Color.white);
            m.SetFloat("unity_GUIZTestMode", linesThroughObjects
                ? (int)UnityEngine.Rendering.CompareFunction.Always
                : (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            return true;
        }

        // Colour for every drawn vertex: each vertex is projected onto the pose
        // polyline with a forward-only search (the line follows the recording in
        // order), and the speed is interpolated between that segment's samples.
        internal static Color[] ComputeHeatmapVertexColors(GhostLineEntry entry, Vector3[] positions)
        {
            Vector3[] path = entry.posePositions;
            float[] speeds = entry.speedSamples;
            int segCount = path.Length - 1;
            Color[] colors = new Color[positions.Length];
            Color noData = new Color(0.5f, 0.5f, 0.5f, 1f);
            int seg = 0;
            for (int v = 0; v < positions.Length; v++)
            {
                Vector3 p = positions[v];
                int best = seg;
                float bestT;
                float bestD = DistanceToSegment(p, path[seg], path[seg + 1], out bestT);
                int last = Mathf.Min(segCount - 1, seg + 24);
                for (int s = seg + 1; s <= last; s++)
                {
                    float t;
                    float d = DistanceToSegment(p, path[s], path[s + 1], out t);
                    if (d < bestD - 0.0001f)
                    {
                        bestD = d;
                        bestT = t;
                        best = s;
                    }
                }
                seg = best;
                float a = speeds[best], b = speeds[best + 1];
                float speed = IsFinite(a) && IsFinite(b) ? Mathf.Lerp(a, b, bestT) : IsFinite(a) ? a : b;
                colors[v] = IsFinite(speed) ? SpeedHeatmapColor(NormalizeHeatmapSpeed(speed)) : noData;
            }
            return colors;
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b, out float t)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            t = len2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
            return (a + ab * t - p).sqrMagnitude;
        }

        public static void BuildNameMarker(GhostLineEntry entry)
        {
            if (entry.markerObject == null)
            {
                entry.markerObject = new GameObject("GhostMarker_" + entry.hash);
                entry.markerObject.SetActive(true);

                LineRenderer tether = entry.markerObject.AddComponent<LineRenderer>();
                tether.useWorldSpace = false;
                tether.positionCount = 2;
                tether.startWidth = 0.05f;
                tether.endWidth = 0.05f;
                tether.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tether.receiveShadows = false;
                tether.sharedMaterial = GetThroughWallTetherMaterial();
                entry.markerTether = tether;

                GameObject labelObj = new GameObject("Label");
                labelObj.transform.SetParent(entry.markerObject.transform, false);
                labelObj.transform.localScale = Vector3.one * 0.08f;

                TextMesh text = labelObj.AddComponent<TextMesh>();
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.fontSize = 48;
                text.characterSize = 0.16f;
                entry.markerText = text;

                var renderer = labelObj.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.sortingOrder = 500;
                    renderer.enabled = GhostLinesPlugin.showNameTags;
                }

                GameObject deltaObj = new GameObject("Delta");
                deltaObj.transform.SetParent(labelObj.transform, false);
                deltaObj.transform.localPosition = new Vector3(0f, 0.6f, 0f);
                deltaObj.transform.localScale = Vector3.one;
                TextMesh dt = deltaObj.AddComponent<TextMesh>();
                dt.text = "";
                dt.anchor = TextAnchor.MiddleCenter;
                dt.alignment = TextAlignment.Center;
                dt.fontSize = 36;
                dt.characterSize = 0.14f;
                dt.color = Color.white;
                entry.deltaText = dt;
                var dr = deltaObj.GetComponent<MeshRenderer>();
                if (dr != null)
                {
                    dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    dr.receiveShadows = false;
                    dr.sortingOrder = 501;
                }
            }
            else
            {
                if (entry.markerText != null)
                    entry.markerText.text = entry.pilotName;
                if (entry.markerTether != null)
                {
                    entry.markerTether.startColor = entry.color;
                    entry.markerTether.endColor = entry.color;
                    entry.markerTether.enabled = (GhostLinesPlugin.showNameTags && entry.nameTagVisible) || (GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible);
                }
            }

            int heightIdx = entries.IndexOf(entry);
            entry.markerBaseHeight = 2.8f + (heightIdx >= 0 ? heightIdx * 1.2f : 0f);
        }

        internal static Material GetThroughWallTetherMaterial()
        {
            if (throughWallTetherMaterial != null)
                return throughWallTetherMaterial;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return null;
            throughWallTetherMaterial = new Material(shader);
            throughWallTetherMaterial.hideFlags = HideFlags.HideAndDontSave;
            throughWallTetherMaterial.SetInt("_SrcBlend", 5);
            throughWallTetherMaterial.SetInt("_DstBlend", 10);
            throughWallTetherMaterial.SetInt("_Cull", 0);
            throughWallTetherMaterial.SetInt("_ZWrite", 0);
            throughWallTetherMaterial.SetInt("_ZTest", 8);
            throughWallTetherMaterial.renderQueue = 4000;
            return throughWallTetherMaterial;
        }

        public static string CleanPilotName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return rawName;
            int idx = rawName.LastIndexOf(" - ", System.StringComparison.Ordinal);
            if (idx > 0)
            {
                string after = rawName.Substring(idx + 3);
                if (System.Text.RegularExpressions.Regex.IsMatch(after, @"^\d{1,2}:\d{2}[.:]\d{2,3}$"))
                    return rawName.Substring(0, idx);
            }
            return rawName;
        }

        public static string ResolvePilotName(object recording, string fallback)
        {
            if (recording == null) return fallback;

            string name = FindPilotName(recording, 0, new System.Collections.Generic.HashSet<object>());
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            if (GhostLinePoller.Instance != null)
            {
                try
                {
                    string replayName = GhostLinePoller.Instance.ResolvePilotNameFromReplayState(recording);
                    if (!string.IsNullOrWhiteSpace(replayName))
                        return replayName;
                }
                catch { }
            }

            return fallback;
        }

        public static Color? ResolvePilotColor(object recording)
        {
            if (recording == null) return null;
            return FindPilotColor(recording, 0, new System.Collections.Generic.HashSet<object>());
        }

        internal static Color? FindPilotColor(object obj, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || depth > 3)
                return null;
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return null;
            if (obj is UnityEngine.Object && depth > 0)
                return null;
            if (!visited.Add(obj))
                return null;

            var type = obj.GetType();

            if (type.FullName == "GhostPlayerProfile")
            {
                try
                {
                    var colorProp = type.GetProperty("Color", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (colorProp != null && colorProp.CanRead)
                    {
                        object v = colorProp.GetValue(obj, null);
                        if (v is Color c && c.a > 0.01f && (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f))
                            return c;
                    }
                }
                catch { }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType == typeof(GhostPlayerProfile))
                {
                    object profile = null;
                    try { profile = field.GetValue(obj); } catch { }
                    Color? nested = FindPilotColor(profile, depth + 1, visited);
                    if (nested.HasValue)
                        return nested;
                }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType == typeof(Color))
                {
                    try
                    {
                        Color c = (Color)field.GetValue(obj);
                        if (c.a > 0.01f && (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f))
                            return c;
                    }
                    catch { }
                }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type) || field.FieldType == typeof(Color))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                Color? nested = FindPilotColor(child, depth + 1, visited);
                if (nested.HasValue)
                    return nested;
            }

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                foreach (var item in enumerable)
                {
                    if (walked++ >= 256)
                        break;
                    Color? nested = FindPilotColor(item, depth + 1, visited);
                    if (nested.HasValue)
                        return nested;
                }
            }

            return null;
        }

        internal static string FindPilotName(object obj, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || depth > 2) return null;
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return null;
            if (obj is UnityEngine.Object && depth > 0)
                return null;
            if (!visited.Add(obj))
                return null;

            var type = obj.GetType();

            if (TryGetNickname(obj, type, out string nickname))
                return nickname;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string))
                    continue;

                object child = null;
                try { child = field.GetValue(obj); } catch { }
                string nested = FindPilotName(child, depth + 1, visited);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }

            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                    continue;
                if (prop.PropertyType.IsPrimitive || prop.PropertyType.IsEnum || prop.PropertyType == typeof(string))
                    continue;

                object child = null;
                try { child = prop.GetValue(obj, null); } catch { }
                string nested = FindPilotName(child, depth + 1, visited);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }

            if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                int walked = 0;
                foreach (var item in enumerable)
                {
                    if (walked++ >= 256)
                        break;
                    string nested = FindPilotName(item, depth + 1, visited);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }

            if (type.FullName == "GhostPlayerProfile")
            {
                try
                {
                    var nicknameProp = type.GetProperty("Nickname", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (nicknameProp != null)
                    {
                        string directNickname = nicknameProp.GetValue(obj, null) as string;
                        if (!string.IsNullOrWhiteSpace(directNickname))
                            return directNickname;
                    }
                }
                catch { }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType == typeof(string))
                {
                    string value = null;
                    try { value = field.GetValue(obj) as string; } catch { }
                    if (LooksLikePilotName(field.Name, value))
                        return value;
                }
            }

            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                    continue;

                if (prop.PropertyType == typeof(string))
                {
                    string value = null;
                    try { value = prop.GetValue(obj, null) as string; } catch { }
                    if (LooksLikePilotName(prop.Name, value))
                        return value;
                }
            }

            return null;
        }

        internal static bool TryGetNickname(object obj, System.Type type, out string nickname)
        {
            nickname = null;
            if (obj == null || type == null) return false;

            try
            {
                var nicknameProp = type.GetProperty("Nickname", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (nicknameProp != null && nicknameProp.PropertyType == typeof(string))
                {
                    nickname = nicknameProp.GetValue(obj, null) as string;
                    if (!string.IsNullOrWhiteSpace(nickname))
                        return true;
                }
            }
            catch { }

            try
            {
                var nickField = type.GetField("nickName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (nickField != null && nickField.FieldType == typeof(string))
                {
                    nickname = nickField.GetValue(obj) as string;
                    if (!string.IsNullOrWhiteSpace(nickname))
                        return true;
                }
            }
            catch { }

            return false;
        }

        internal static bool LooksLikePilotName(string memberName, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string lower = memberName != null ? memberName.ToLowerInvariant() : "";
            if (lower.Contains("track") ||
                lower.Contains("race") ||
                lower.Contains("environment") ||
                lower.Contains("gamemode") ||
                lower.Contains("recording") ||
                lower.Contains("creation") ||
                lower.Contains("date") ||
                lower.Contains("time"))
            {
                return false;
            }

            if (lower.Contains("username") ||
                lower.Contains("displayname") ||
                lower.Contains("playername") ||
                lower.Contains("pilotname") ||
                lower.Contains("steamname") ||
                lower.Contains("nickname") ||
                lower.Contains("userid") ||
                lower.Contains("steamid"))
            {
                return true;
            }

            return false;
        }

        public static Vector3[] CatmullRomSmooth(Vector3[] points, int stepsPerSegment)
        {
            if (points == null || points.Length < 2 || stepsPerSegment <= 0) return points;

            List<Vector3> distinctPoints = new List<Vector3>(points.Length) { points[0] };
            for (int i = 1; i < points.Length; i++)
            {
                if ((points[i] - distinctPoints[distinctPoints.Count - 1]).sqrMagnitude > 0.000001f)
                    distinctPoints.Add(points[i]);
            }
            if (distinctPoints.Count < 2) return points;

            points = distinctPoints.ToArray();
            int n = points.Length;
            List<Vector3> result = new List<Vector3>();

            float[] knot = new float[n];
            for (int i = 1; i < n; i++)
                knot[i] = knot[i - 1] + Mathf.Sqrt(Vector3.Distance(points[i], points[i - 1]));

            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p0 = i > 0 ? points[i - 1] : points[i];
                Vector3 p1 = points[i];
                Vector3 p2 = points[i + 1];
                Vector3 p3 = i < n - 2 ? points[i + 2] : points[i + 1];

                float t0 = i > 0 ? knot[i - 1] : knot[i] - (knot[i + 1] - knot[i]);
                float t1 = knot[i];
                float t2 = knot[i + 1];
                float t3 = i < n - 2 ? knot[i + 2] : knot[i + 1] + (knot[i + 1] - knot[i]);

                int steps = stepsPerSegment;
                Vector3 segA = p1 - p0;
                Vector3 segB = p2 - p1;
                if (segA.sqrMagnitude > 1e-10f && segB.sqrMagnitude > 1e-10f)
                {
                    float cos = Mathf.Clamp(Vector3.Dot(segA.normalized, segB.normalized), -1f, 1f);
                    float angleDeg = Mathf.Acos(cos) * Mathf.Rad2Deg;
                    if (angleDeg < 2f)
                        steps = 1;
                    else if (angleDeg < 8f)
                        steps = Mathf.Max(2, stepsPerSegment / 4);
                    else if (angleDeg < 20f)
                        steps = Mathf.Max(3, stepsPerSegment / 2);
                }

                for (int s = 0; s < steps; s++)
                {
                    float u = s / (float)steps;
                    float t = Mathf.Lerp(t1, t2, u);
                    float w0 = (t - t0) / (t1 - t0);
                    float w1 = (t - t1) / (t2 - t1);
                    float w2 = (t - t2) / (t3 - t2);
                    Vector3 a1 = Vector3.LerpUnclamped(p0, p1, w0);
                    Vector3 a2 = Vector3.LerpUnclamped(p1, p2, w1);
                    Vector3 a3 = Vector3.LerpUnclamped(p2, p3, w2);
                    float w01 = (t - t0) / (t2 - t0);
                    float w12 = (t - t1) / (t3 - t1);
                    float w012 = (t - t1) / (t2 - t1);
                    result.Add(Vector3.LerpUnclamped(Vector3.LerpUnclamped(a1, a2, w01), Vector3.LerpUnclamped(a2, a3, w12), w012));
                }
            }
            result.Add(points[n - 1]);
            if (result.Count > SMOOTH_MAX_POINTS)
            {
                int stride = (result.Count + SMOOTH_MAX_POINTS - 1) / SMOOTH_MAX_POINTS;
                List<Vector3> capped = new List<Vector3>(SMOOTH_MAX_POINTS + 1);
                for (int i = 0; i < result.Count - 1; i += stride)
                    capped.Add(result[i]);
                capped.Add(result[result.Count - 1]);
                return capped.ToArray();
            }
            return result.ToArray();
        }

        public static void ToggleSmoothing()
        {
            smoothingEnabled = !smoothingEnabled;
            SaveGlobalDisplayPreferences();
            foreach (var entry in entries)
            {
                if (entry.built) RebuildLine(entry);
            }
            poller?.MarkPiPWindowsDirty();
        }

        public static void ToggleSpeedHeatmap()
        {
            speedHeatmapEnabled = !speedHeatmapEnabled;
            if (speedHeatmapEnabled)
                RebuildAllSpeedHeatmaps();
            else
                foreach (var entry in entries)
                    if (entry.built) RebuildLine(entry);
            poller?.MarkPiPWindowsDirty();
        }

        internal static void UpdateSpeedHeatmapRange()
        {
            speedHeatmapMaxSpeedMps = CalculateSharedHeatmapMaxSpeed();
            speedHeatmapRangeRevision++;
        }

        // Drone speed (m/s) at a replay state time, from the per-pose speed
        // samples the line heatmap uses; NaN where there is no data.
        internal static float SampleDroneSpeedAtTime(GhostLineEntry entry, float time)
        {
            float[] times = entry.poseTimes;
            float[] speeds = entry.speedSamples;
            if (times == null || speeds == null)
                return float.NaN;
            int count = Mathf.Min(times.Length, speeds.Length);
            if (count == 0)
                return float.NaN;
            if (time <= times[0])
                return speeds[0];
            if (time >= times[count - 1])
                return speeds[count - 1];
            int lo = 0, hi = count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] <= time) lo = mid;
                else hi = mid;
            }
            float a = speeds[lo], b = speeds[hi];
            if (!IsFinite(a)) return b;
            if (!IsFinite(b)) return a;
            float span = times[hi] - times[lo];
            return span > 0.0001f ? Mathf.Lerp(a, b, (time - times[lo]) / span) : a;
        }

        internal static void RebuildAllSpeedHeatmaps()
        {
            UpdateSpeedHeatmapRange();
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] heatmap rebuild: entries=" + entries.Count
                + " range=" + (speedHeatmapMinSpeedMps * 3.6f).ToString("F1") + "-"
                + (speedHeatmapMaxSpeedMps * 3.6f).ToString("F1") + " km/h");
            foreach (var entry in entries)
            {
                if (entry.built)
                    RebuildLine(entry);
            }
            poller?.MarkPiPWindowsDirty();
        }

        public static void SetLinesThroughObjects(bool enabled)
        {
            linesThroughObjects = enabled;
            SaveGlobalDisplayPreferences();
            int depthTest = enabled
                ? (int)UnityEngine.Rendering.CompareFunction.Always
                : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
            foreach (var entry in entries)
            {
                if (entry.lineMaterial != null)
                {
                    entry.lineMaterial.SetInt("_ZTest", depthTest);
                    entry.lineMaterial.SetFloat("unity_GUIZTestMode", depthTest);
                }
            }
            poller?.MarkPiPWindowsDirty();
        }

        public static Color SpeedHeatmapColor(float normalizedSpeed)
        {
            // The auto-range already stretches the gradient across the observed
            // speed band, so map linearly for an even red->orange->yellow->green spread.
            float value = Mathf.Clamp01(normalizedSpeed);
            Color slowRed = new Color(1f, 0.02f, 0.06f);
            Color mediumOrange = new Color(1f, 0.35f, 0.02f);
            Color fastYellow = new Color(1f, 0.95f, 0.02f);
            Color fastGreen = new Color(0.05f, 1f, 0.12f);
            if (value < 0.35f)
                return Color.Lerp(slowRed, mediumOrange, value / 0.35f);
            if (value < 0.6f)
                return Color.Lerp(mediumOrange, fastYellow, (value - 0.35f) / 0.25f);
            return Color.Lerp(fastYellow, fastGreen, (value - 0.6f) / 0.4f);
        }

        public static void GetSpeedHeatmapClampRange(out float minMps, out float maxMps)
        {
            float autoMps = Mathf.Max(speedHeatmapMaxSpeedMps, 1f);
            float autoMin = Mathf.Clamp(speedHeatmapMinSpeedMps, 0f, autoMps * 0.95f);
            if (autoMps - autoMin < 0.5f)
                autoMin = Mathf.Max(0f, autoMps - 0.5f);
            minMps = autoMin;
            maxMps = autoMps;
        }

        internal static float NormalizeHeatmapSpeed(float speedMps)
        {
            if (speedHeatmapMaxSpeedMps <= 0.0001f)
                return 0f;
            float minMps, maxMps;
            GetSpeedHeatmapClampRange(out minMps, out maxMps);
            float range = Mathf.Max(maxMps - minMps, 0.0001f);
            return Mathf.Clamp01((speedMps - minMps) / range);
        }

        internal static Gradient BuildSpeedHeatmapGradient(GhostLineEntry entry)
        {
            // Liftoff's Unity version supports up to eight LineRenderer gradient color keys.
            const int keyCount = 8;
            Gradient gradient = new Gradient();
            GradientColorKey[] colorKeys = new GradientColorKey[keyCount];
            GradientAlphaKey[] alphaKeys =
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            };
            float[] poseDistances = null;
            if (entry != null && entry.posePositions != null && entry.posePositions.Length > 1)
            {
                poseDistances = new float[entry.posePositions.Length];
                for (int i = 1; i < poseDistances.Length; i++)
                    poseDistances[i] = poseDistances[i - 1] + Vector3.Distance(entry.posePositions[i], entry.posePositions[i - 1]);
            }
            bool hasData = entry != null && entry.speedSamples != null && poseDistances != null &&
                poseDistances[poseDistances.Length - 1] > 0.001f;
            float totalDistance = hasData ? poseDistances[poseDistances.Length - 1] : 0f;
            for (int i = 0; i < keyCount; i++)
            {
                float normalized = i / (float)(keyCount - 1);
                Color color = new Color(0.5f, 0.5f, 0.5f, 1f);
                if (hasData)
                {
                    float speed = InterpolateSpeedAtDistance(entry.speedSamples, poseDistances, normalized * totalDistance);
                    if (IsFinite(speed))
                        color = SpeedHeatmapColor(NormalizeHeatmapSpeed(speed));
                }
                colorKeys[i] = new GradientColorKey(color, normalized);
            }
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        internal static bool IsPoseTeleport(float[] times, Vector3[] positions, int i)
        {
            float h = times[i + 1] - times[i];
            return h <= 0.0001f ||
                (positions[i + 1] - positions[i]).sqrMagnitude > POSE_TELEPORT_SPEED * POSE_TELEPORT_SPEED * h * h;
        }

        // Speed per pose sample: distance over the neighbouring segments divided
        // by their time span, skipping respawn/reset teleports (which used to add
        // their jump to the path distance and show up as huge speed spikes). A
        // 3-sample median removes any remaining single-sample spike.
        internal static float[] BuildPoseSpeedSamples(float[] times, Vector3[] positions)
        {
            int count = times == null || positions == null ? 0 : Mathf.Min(times.Length, positions.Length);
            float[] speeds = new float[count];
            for (int i = 0; i < count; i++)
            {
                float dist = 0f, dur = 0f;
                if (i > 0 && !IsPoseTeleport(times, positions, i - 1))
                {
                    dist += Vector3.Distance(positions[i], positions[i - 1]);
                    dur += times[i] - times[i - 1];
                }
                if (i < count - 1 && !IsPoseTeleport(times, positions, i))
                {
                    dist += Vector3.Distance(positions[i + 1], positions[i]);
                    dur += times[i + 1] - times[i];
                }
                speeds[i] = dur > 0.0001f ? dist / dur * METERS_PER_WORLD_UNIT : float.NaN;
            }
            float[] filtered = new float[count];
            float[] window = new float[3];
            for (int i = 0; i < count; i++)
            {
                int valid = 0;
                for (int j = Mathf.Max(0, i - 1); j <= Mathf.Min(count - 1, i + 1); j++)
                    if (IsFinite(speeds[j]))
                        window[valid++] = speeds[j];
                if (valid == 0)
                {
                    filtered[i] = float.NaN;
                    continue;
                }
                System.Array.Sort(window, 0, valid);
                filtered[i] = window[valid / 2];
            }
            return filtered;
        }

        // Shared colour range for all pilots: 5th..95th percentile of speed,
        // weighted by distance flown, so a few outliers (launch, crash, a spike)
        // no longer compress the whole lap into one end of the colour scale.
        // Sets speedHeatmapMinSpeedMps and returns the max.
        internal static float CalculateSharedHeatmapMaxSpeed()
        {
            List<Vector2> weightedSpeeds = new List<Vector2>();
            foreach (var entry in entries)
            {
                if (!entry.built || entry.speedSamples == null || entry.posePositions == null || entry.poseTimes == null)
                    continue;
                int count = Mathf.Min(entry.speedSamples.Length, entry.posePositions.Length);
                for (int i = 0; i < count - 1; i++)
                {
                    if (IsPoseTeleport(entry.poseTimes, entry.posePositions, i))
                        continue;
                    float distance = Vector3.Distance(entry.posePositions[i + 1], entry.posePositions[i]);
                    float speedA = entry.speedSamples[i];
                    float speedB = entry.speedSamples[i + 1];
                    float speed = IsFinite(speedA) && IsFinite(speedB)
                        ? (speedA + speedB) * 0.5f
                        : IsFinite(speedA) ? speedA : speedB;
                    if (distance > 0.001f && IsFinite(speed) && speed >= 0f)
                        weightedSpeeds.Add(new Vector2(speed, distance));
                }
            }

            if (weightedSpeeds.Count == 0)
            {
                speedHeatmapMinSpeedMps = 0f;
                return 1f;
            }

            weightedSpeeds.Sort((x, y) => x.x.CompareTo(y.x));
            float totalWeight = 0f;
            foreach (Vector2 sample in weightedSpeeds)
                totalWeight += sample.y;

            float lowPercentile = PercentileByWeight(weightedSpeeds, totalWeight, 0.05f);
            float highPercentile = PercentileByWeight(weightedSpeeds, totalWeight, 0.95f);
            float maxMps = Mathf.Max(highPercentile, 1f);
            speedHeatmapMinSpeedMps = Mathf.Clamp(lowPercentile, 0f, maxMps * 0.95f);
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] heatmap dist: n=" + weightedSpeeds.Count
                + " trueMin=" + (weightedSpeeds[0].x * 3.6f).ToString("F1")
                + " p5=" + (lowPercentile * 3.6f).ToString("F1")
                + " p50=" + (PercentileByWeight(weightedSpeeds, totalWeight, 0.5f) * 3.6f).ToString("F1")
                + " p95=" + (highPercentile * 3.6f).ToString("F1")
                + " trueMax=" + (weightedSpeeds[weightedSpeeds.Count - 1].x * 3.6f).ToString("F1") + " km/h");
            return maxMps;
        }

        internal static float PercentileByWeight(List<Vector2> sorted, float totalWeight, float fraction)
        {
            float accumulated = 0f;
            float target = totalWeight * fraction;
            foreach (Vector2 sample in sorted)
            {
                accumulated += sample.y;
                if (accumulated >= target)
                    return sample.x;
            }
            return sorted[sorted.Count - 1].x;
        }

        internal static float InterpolateSpeedAtDistance(float[] speeds, float[] distances, float query)
        {
            int count = Mathf.Min(speeds.Length, distances.Length);
            if (count == 0)
                return float.NaN;

            int upper = 0;
            int high = count;
            while (upper < high)
            {
                int mid = (upper + high) / 2;
                if (distances[mid] < query) upper = mid + 1;
                else high = mid;
            }

            int lo = upper - 1;
            int hi = upper;
            while (lo >= 0 && !IsFinite(speeds[lo])) lo--;
            while (hi < count && !IsFinite(speeds[hi])) hi++;
            if (lo < 0 && hi >= count)
                return float.NaN;
            if (lo < 0)
                return speeds[hi];
            if (hi >= count)
                return speeds[lo];

            float range = distances[hi] - distances[lo];
            return range > 0.0001f
                ? Mathf.LerpUnclamped(speeds[lo], speeds[hi], (query - distances[lo]) / range)
                : Mathf.Abs(distances[lo] - query) <= Mathf.Abs(distances[hi] - query) ? speeds[lo] : speeds[hi];
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static Gradient CreateSolidGradient(Color color)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    } // GhostLinesPlugin
}
