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

namespace LiftoffGhostLines
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class GhostLinesPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.dapa.liftoff.replayplus";
        public const string PluginName = "Liftoff Replay Plus";
        public const string PluginVersion = "1.2.1";

        public static List<GhostLineEntry> entries = new List<GhostLineEntry>();
        private static List<GhostLineEntry> recordingCandidates = new List<GhostLineEntry>();
        private static int recordingConstructionThreadId = -1;
        public static bool smoothingEnabled = true;
        public static bool speedHeatmapEnabled = false;
        public static bool showDeltaDisplay = true;
        public static bool showNameTags = true;
        public static bool linesThroughObjects = false;
        public static float speedHeatmapMaxSpeedMps = 1f;
        public static float speedHeatmapMinSpeedMps = 0f;
        private const int SMOOTH_STEPS = 8;
        private const float METERS_PER_WORLD_UNIT = 1f;
        private const float SPEED_SAMPLE_WINDOW_SECONDS = 0.12f;
        private const int SPEED_HEATMAP_TEXTURE_WIDTH = 1024;
        private static GhostLinePoller poller;
        private static Material throughWallTetherMaterial;
        private static ManualLogSource pluginLog;
        private static Harmony harmony;
        private static ConfigFile preferencesConfig;
        private static ConfigEntry<bool> stickCamEnabledConfig;
        private static ConfigEntry<float> stickCamSizeConfig;
        private static ConfigEntry<float> playbackUiSizeConfig;
        private static ConfigEntry<bool> darkModeConfig;
        private static ConfigEntry<bool> settingsPanelOpenConfig;
        private static ConfigEntry<bool> stickCamPositionCustomizedConfig;
        private static ConfigEntry<float> stickCamPositionXConfig;
        private static ConfigEntry<float> stickCamPositionYConfig;
        private static ConfigEntry<bool> playbackUiPositionCustomizedConfig;
        private static ConfigEntry<float> playbackUiPositionXConfig;
        private static ConfigEntry<float> playbackUiPositionYConfig;
        private static ConfigEntry<bool> settingsPanelPositionCustomizedConfig;
        private static ConfigEntry<float> settingsPanelPositionXConfig;
        private static ConfigEntry<float> settingsPanelPositionYConfig;
        private static Dictionary<string, ConfigEntry<KeyCode>> bindingConfigs = new Dictionary<string, ConfigEntry<KeyCode>>();
        private static readonly string[] BindingIds =
        {
            "playpause", "restart", "xray", "smooth", "rewind", "ff", "nametags",
            "alllines", "linesthrough", "delta", "heatmap", "loop", "holdzoom",
            "camreset", "freecamforward", "freecamback", "freecamleft", "freecamright"
        };

        private static string BindingLabel(string id)
        {
            switch (id)
            {
                case "playpause": return "Play / Pause";
                case "restart": return "Restart Replay";
                case "xray": return "Toggle X-Ray";
                case "smooth": return "Toggle Smoothing";
                case "rewind": return "Rewind Frame";
                case "ff": return "Fast-Forward Frame";
                case "nametags": return "Toggle Name Tags";
                case "alllines": return "Toggle All Race Lines";
                case "linesthrough": return "Lines Through Objects";
                case "delta": return "Toggle Delta Display";
                case "heatmap": return "Toggle Speed Heatmap";
                case "loop": return "Toggle Loop Replay";
                case "holdzoom": return "Toggle Zoom Focus";
                case "camreset": return "Reset Camera";
                case "freecamforward": return "Freecam Forward";
                case "freecamback": return "Freecam Back";
                case "freecamleft": return "Freecam Left";
                case "freecamright": return "Freecam Right";
                default: return id;
            }
        }

        private static KeyCode GetDefaultBinding(string id)
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
                case "holdzoom": return KeyCode.Z;
                case "camreset": return KeyCode.V;
                case "freecamforward": return KeyCode.W;
                case "freecamback": return KeyCode.S;
                case "freecamleft": return KeyCode.A;
                case "freecamright": return KeyCode.D;
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
        }

        public static string BindingKeyName(string id)
        {
            return GetBinding(id).ToString();
        }
        private bool leaderboardScenePending;
        private Scene pendingLeaderboardScene;

        private static Color[] pilotColors = new Color[]
        {
            Color.cyan,
            new Color(1f, 0.5f, 0f),
            Color.magenta,
            new Color(0.5f, 1f, 0f),
        };

        private void Awake()
        {
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

            bindingConfigs.Clear();
            foreach (string id in BindingIds)
                bindingConfigs[id] = Config.Bind("Bindings", BindingLabel(id), GetDefaultBinding(id),
                    "Key binding for " + BindingLabel(id) + ".");

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(GhostLinesPlugin));
            EnsurePoller();
            PatchLeaderboardControllerStart();
            PatchReplayControlsSpace();
            SceneManager.sceneLoaded += OnPluginSceneLoaded;
            SceneManager.sceneUnloaded += OnPluginSceneUnloaded;
            Canvas.willRenderCanvases += OnPluginWillRenderCanvases;
            Debug.Log("[GhostLines] Plugin Awake; poller attached");
        }

        private static void PatchLeaderboardControllerStart()
        {
            if (harmony == null)
            {
                pluginLog.LogError("[GhostLines] Harmony was not initialized");
                return;
            }

            try
            {
                System.Reflection.Assembly gameAssembly = typeof(LocalGhostStatesRecording).Assembly;
                System.Type controllerType = null;
                foreach (System.Type type in gameAssembly.GetTypes())
                {
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
        }

        private static void PatchReplayControlsSpace()
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
                pluginLog.LogInfo("[GhostLines] Patched ReplayControls.Update for native space suppression");
            }
            catch (System.Exception ex)
            {
                pluginLog.LogError("[GhostLines] Failed to patch ReplayControls.Update: " + ex);
            }
        }

        private static bool ReplayControlsUpdatePrefix()
        {
            if (poller == null || !poller.NativeReplayReady)
                return true;
            if (poller.IsBindingListening)
                return true;
            if (!Input.GetKeyDown(KeyCode.Space))
                return true;
            poller.MarkBindingCaptureFrame();
            return false;
        }

        private static void OnLeaderboardControllerStarted(Component __instance)
        {
            pluginLog.LogInfo("[GhostLines] Leaderboard controller Start postfix: " + __instance.GetType().FullName);
            EnsurePoller();
            if (poller == null || poller.gameObject == null)
            {
                pluginLog.LogError("[GhostLines] Could not attach poller to leaderboard controller");
                return;
            }

            bool attached = poller.TryAttachLeaderboardSearch(__instance.gameObject.scene);
            if (!attached)
                poller.AttachLeaderboardSearchFromController(__instance);
        }

        private static void OnLeaderboardControllerUpdated(Component __instance)
        {
            EnsurePoller();
            if (poller != null)
                poller.AttachLeaderboardSearchFromController(__instance);
        }

        private void Update()
        {
            if (poller != null)
                poller.TickLeaderboardSearch();
        }

        private void OnPluginSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == null || scene.name.IndexOf("Leaderboard", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;

            pendingLeaderboardScene = scene;
            leaderboardScenePending = true;
            Debug.Log("[GhostLines] Leaderboard scene callback: " + scene.name);
            if (poller != null && poller.TryAttachLeaderboardSearch(scene))
                leaderboardScenePending = false;
        }

        private void OnPluginSceneUnloaded(Scene scene)
        {
            if (scene.name == null || scene.name.IndexOf("Leaderboard", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;

            leaderboardScenePending = false;
            if (poller != null)
                poller.CleanupLeaderboardSearchFromPlugin();
        }

        private void OnPluginWillRenderCanvases()
        {
            if (poller == null)
                return;

            poller.EnforceNativeHudVisibility();
            poller.EnforceNativeSpectatedTextStyle();

            if (!leaderboardScenePending)
                return;

            if (poller.TryAttachLeaderboardSearch(pendingLeaderboardScene))
                leaderboardScenePending = false;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnPluginSceneLoaded;
            SceneManager.sceneUnloaded -= OnPluginSceneUnloaded;
            Canvas.willRenderCanvases -= OnPluginWillRenderCanvases;
        }

        private static void EnsurePoller()
        {
            if (poller != null && poller.gameObject != null)
                return;

            GameObject obj = new GameObject("GhostLinesPoller");
            Object.DontDestroyOnLoad(obj);

            poller = obj.AddComponent<GhostLinePoller>();
            poller.LoadSavedPreferences();
            pluginLog?.LogInfo("[GhostLines] Poller attached to " + obj.name);
        }

        private static void SaveDisplayPreferences(bool stickCamEnabled, float stickCamSize, float playbackUiSize, bool darkMode)
        {
            if (stickCamEnabledConfig == null)
                return;

            stickCamEnabledConfig.Value = stickCamEnabled;
            stickCamSizeConfig.Value = stickCamSize;
            playbackUiSizeConfig.Value = playbackUiSize;
            darkModeConfig.Value = darkMode;
            preferencesConfig?.Save();
        }

        private static void SaveSettingsPanelOpen(bool open)
        {
            if (settingsPanelOpenConfig == null)
                return;

            settingsPanelOpenConfig.Value = open;
            if (GhostLinePoller.Instance != null)
                GhostLinePoller.Instance.SetSavedSettingsPanelOpen(open);
            preferencesConfig?.Save();
        }

        private static void SaveOverlayPositions(bool stickCustomized, float stickX, float stickY,
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
            preferencesConfig?.Save();
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
            AddRecordingCandidate(__instance);
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
                color = pilotColors[recordingCandidates.Count % pilotColors.Length],
                pilotName = "Pilot " + (recordingCandidates.Count + 1)
            };
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
            Debug.Log($"[GhostLines] Replay recording candidate accepted: {entry.pilotName}");
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

            entry.pathDistances = new float[states.Count];
            entry.pathDistances[0] = 0f;
            for (int i = 1; i < states.Count; i++)
                entry.pathDistances[i] = entry.pathDistances[i - 1] + Vector3.Distance(states[i].position, states[i - 1].position);
            entry.speedSamples = BuildSpeedSamples(entry.originalPositions, entry.stateTimes, entry.pathDistances);


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
                    Debug.Log($"[GhostLines] Lap offset={entry.lapStartOffset:F3}s finish={entry.finishTime:F3}s (index {lapIndices[1]})");
                }
                else
                {
                    entry.lapStartDistance = 0f;
                    entry.lapTotalDistance = entry.pathDistances[states.Count - 1];
                    entry.finishTime = -1f;
                    Debug.Log($"[GhostLines] lapStartIndices count={lapIndices?.Count ?? 0}");
                }
            }
            catch { }

            entry.built = true;
            if (speedHeatmapEnabled)
                RebuildAllSpeedHeatmaps();
            else
                RebuildLine(entry);
        }

        public static void RebuildLine(GhostLineEntry entry)
        {
            Vector3[] positions = smoothingEnabled
                ? CatmullRomSmooth(entry.originalPositions, SMOOTH_STEPS)
                : entry.originalPositions;

            DestroyLineResources(entry);

            entry.lineObject = new GameObject("GhostLine_" + entry.hash);
            entry.lineObject.SetActive(entry.visible);

            LineRenderer line = entry.lineObject.AddComponent<LineRenderer>();
            line.positionCount = positions.Length;
            line.startWidth = 0.12f;
            line.endWidth = 0.12f;
            line.useWorldSpace = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.textureMode = LineTextureMode.Stretch;
            if (speedHeatmapEnabled)
            {
                line.startColor = SpeedHeatmapColor(0f);
                line.endColor = SpeedHeatmapColor(1f);
            }
            else
            {
                line.startColor = entry.color;
                line.endColor = entry.color;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null && speedHeatmapEnabled)
                shader = Shader.Find("Sprites/Default");
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

            line.SetPositions(positions);
            if (speedHeatmapEnabled)
                line.colorGradient = BuildSpeedHeatmapGradient(entry);
            BuildNameMarker(entry);
        }

        private static void DestroyLineResources(GhostLineEntry entry)
        {
            if (entry == null) return;

            if (entry.lineObject != null)
                Destroy(entry.lineObject);
            entry.lineObject = null;

            if (entry.lineMaterial != null)
                Destroy(entry.lineMaterial);
            entry.lineMaterial = null;

            if (entry.heatmapTexture != null)
                Destroy(entry.heatmapTexture);
            entry.heatmapTexture = null;
        }

        public static void BuildNameMarker(GhostLineEntry entry)
        {
            if (entry.markerObject != null)
                Destroy(entry.markerObject);

            entry.markerObject = new GameObject("GhostMarker_" + entry.hash);
            entry.markerObject.SetActive(true);

            LineRenderer tether = entry.markerObject.AddComponent<LineRenderer>();
            tether.useWorldSpace = false;
            tether.positionCount = 2;
            tether.startWidth = 0.05f;
            tether.endWidth = 0.05f;
            tether.startColor = entry.color;
            tether.endColor = entry.color;
            tether.enabled = GhostLinesPlugin.showNameTags || GhostLinesPlugin.showDeltaDisplay;
            tether.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tether.receiveShadows = false;
            tether.sharedMaterial = GetThroughWallTetherMaterial();

            int heightIdx = entries.IndexOf(entry);
            float markerHeight = 2.8f + (heightIdx >= 0 ? heightIdx * 1.2f : 0f);

            GameObject labelObj = new GameObject("Label");
            labelObj.transform.SetParent(entry.markerObject.transform, false);
            labelObj.transform.localPosition = new Vector3(0f, markerHeight, 0f);
            labelObj.transform.localScale = Vector3.one * 0.08f;

            TextMesh text = labelObj.AddComponent<TextMesh>();
            text.text = entry.pilotName;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 48;
            text.characterSize = 0.16f;
            text.color = entry.color;

            var renderer = labelObj.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sortingOrder = 500;
                renderer.enabled = GhostLinesPlugin.showNameTags;
            }

            entry.markerText = text;
            entry.markerTether = tether;
            entry.markerBaseHeight = markerHeight;

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

        private static Material GetThroughWallTetherMaterial()
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

            string name = FindPilotName(recording, 0);
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

        private static Color? FindPilotColor(object obj, int depth, System.Collections.Generic.HashSet<object> visited)
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

        private static string FindPilotName(object obj, int depth)
        {
            if (obj == null || depth > 2) return null;

            var type = obj.GetType();

            if (TryGetNickname(obj, type, out string nickname))
                return nickname;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string))
                    continue;

                object child = null;
                try { child = field.GetValue(obj); } catch { }
                string nested = FindPilotName(child, depth + 1);
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
                string nested = FindPilotName(child, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }

            if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                foreach (var item in enumerable)
                {
                    string nested = FindPilotName(item, depth + 1);
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

        private static bool TryGetNickname(object obj, System.Type type, out string nickname)
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

        private static bool LooksLikePilotName(string memberName, string value)
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

                for (int s = 0; s < stepsPerSegment; s++)
                {
                    float u = s / (float)stepsPerSegment;
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
            return result.ToArray();
        }

        public static void ToggleSmoothing()
        {
            smoothingEnabled = !smoothingEnabled;
            foreach (var entry in entries)
            {
                if (entry.built) RebuildLine(entry);
            }
        }

        public static void ToggleSpeedHeatmap()
        {
            speedHeatmapEnabled = !speedHeatmapEnabled;
            if (speedHeatmapEnabled)
                RebuildAllSpeedHeatmaps();
            else
                foreach (var entry in entries)
                    if (entry.built) RebuildLine(entry);
        }

        private static void RebuildAllSpeedHeatmaps()
        {
            speedHeatmapMaxSpeedMps = CalculateSharedHeatmapMaxSpeed();
            foreach (var entry in entries)
            {
                if (entry.built)
                    RebuildLine(entry);
            }
        }

        public static void SetLinesThroughObjects(bool enabled)
        {
            linesThroughObjects = enabled;
            int depthTest = enabled
                ? (int)UnityEngine.Rendering.CompareFunction.Always
                : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
            foreach (var entry in entries)
            {
                if (entry.lineMaterial != null)
                    entry.lineMaterial.SetInt("_ZTest", depthTest);
            }
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
            float spread = autoMps - autoMin;
            if (spread < autoMps * 0.25f)
            {
                autoMin = 0f;
                autoMps = Mathf.Max(autoMps, 1f);
            }
            minMps = autoMin;
            maxMps = autoMps;
        }

        private static float NormalizeHeatmapSpeed(float speedMps)
        {
            if (speedHeatmapMaxSpeedMps <= 0.0001f)
                return 0f;
            float minMps, maxMps;
            GetSpeedHeatmapClampRange(out minMps, out maxMps);
            float range = Mathf.Max(maxMps - minMps, 0.0001f);
            return Mathf.Clamp01((speedMps - minMps) / range);
        }

        private static Gradient BuildSpeedHeatmapGradient(GhostLineEntry entry)
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
            bool hasData = entry != null && entry.speedSamples != null && entry.pathDistances != null &&
                entry.pathDistances.Length > 0 && entry.pathDistances[entry.pathDistances.Length - 1] > 0.001f;
            float totalDistance = hasData ? entry.pathDistances[entry.pathDistances.Length - 1] : 0f;
            for (int i = 0; i < keyCount; i++)
            {
                float normalized = i / (float)(keyCount - 1);
                Color color = new Color(0.5f, 0.5f, 0.5f, 1f);
                if (hasData)
                {
                    float speed = InterpolateSpeedAtDistance(entry.speedSamples, entry.pathDistances, normalized * totalDistance);
                    if (IsFinite(speed))
                        color = SpeedHeatmapColor(NormalizeHeatmapSpeed(speed));
                }
                colorKeys[i] = new GradientColorKey(color, normalized);
            }
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        private static float[] BuildSpeedSamples(Vector3[] positions, float[] times, float[] distances)
        {
            int count = positions == null || times == null || distances == null
                ? 0
                : Mathf.Min(positions.Length, Mathf.Min(times.Length, distances.Length));
            float[] speeds = new float[count];
            for (int i = 0; i < count; i++)
                speeds[i] = float.NaN;

            if (count < 2)
                return speeds;

            for (int i = 0; i < count; i++)
            {
                float before = Mathf.Max(times[0], times[i] - SPEED_SAMPLE_WINDOW_SECONDS);
                float after = Mathf.Min(times[count - 1], times[i] + SPEED_SAMPLE_WINDOW_SECONDS);
                float duration = after - before;
                if (duration <= 0.0001f || !IsFinite(before) || !IsFinite(after))
                    continue;

                float distanceBefore = InterpolateDistanceAtTime(times, distances, before);
                float distanceAfter = InterpolateDistanceAtTime(times, distances, after);
                float speed = (distanceAfter - distanceBefore) / duration;
                speed *= METERS_PER_WORLD_UNIT;
                if (IsFinite(speed) && speed >= 0f)
                    speeds[i] = speed;
            }

            float[] filtered = new float[count];
            for (int i = 0; i < count; i++)
            {
                float[] window = new float[5];
                int valid = 0;
                int first = Mathf.Max(0, i - 2);
                int last = Mathf.Min(count - 1, i + 2);
                for (int j = first; j <= last; j++)
                {
                    if (IsFinite(speeds[j]))
                        window[valid++] = speeds[j];
                }
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

        private static float InterpolateDistanceAtTime(float[] times, float[] distances, float query)
        {
            int count = Mathf.Min(times.Length, distances.Length);
            if (count == 0)
                return 0f;
            if (query <= times[0])
                return distances[0];
            if (query >= times[count - 1])
                return distances[count - 1];

            int lo = 0;
            int hi = count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] <= query) lo = mid;
                else hi = mid;
            }
            float duration = times[hi] - times[lo];
            if (duration <= 0.0001f)
                return distances[lo];
            return Mathf.LerpUnclamped(distances[lo], distances[hi], (query - times[lo]) / duration);
        }

        private static float CalculateSharedHeatmapMaxSpeed()
        {
            List<Vector2> weightedSpeeds = new List<Vector2>();
            foreach (var entry in entries)
            {
                if (!entry.built || entry.speedSamples == null || entry.pathDistances == null)
                    continue;
                int count = Mathf.Min(entry.speedSamples.Length, entry.pathDistances.Length);
                for (int i = 0; i < count - 1; i++)
                {
                    float distance = entry.pathDistances[i + 1] - entry.pathDistances[i];
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

            weightedSpeeds.Sort((a, b) => a.x.CompareTo(b.x));
            float totalWeight = 0f;
            foreach (Vector2 sample in weightedSpeeds)
                totalWeight += sample.y;

            float lowPercentile = weightedSpeeds[0].x;
            float highPercentile = weightedSpeeds[weightedSpeeds.Count - 1].x;
            float accumulated = 0f;
            float lowTarget = totalWeight * 0.2f;
            foreach (Vector2 sample in weightedSpeeds)
            {
                accumulated += sample.y;
                if (accumulated >= lowTarget)
                {
                    lowPercentile = sample.x;
                    break;
                }
            }
            accumulated = 0f;
            float highTarget = totalWeight * 0.95f;
            foreach (Vector2 sample in weightedSpeeds)
            {
                accumulated += sample.y;
                if (accumulated >= highTarget)
                {
                    highPercentile = sample.x;
                    break;
                }
            }

            float roundedKmh = Mathf.Ceil(highPercentile * 3.6f / 5f) * 5f;
            float maxMps = Mathf.Max(roundedKmh / 3.6f, 1f);
            speedHeatmapMinSpeedMps = Mathf.Clamp(lowPercentile, 0f, maxMps * 0.95f);
            return maxMps;
        }

        private static Texture2D BuildSpeedHeatmapTexture(GhostLineEntry entry)
        {
            Texture2D texture = new Texture2D(SPEED_HEATMAP_TEXTURE_WIDTH, 1, TextureFormat.RGBA32, false);
            texture.name = "GhostLineSpeedHeatmap_" + entry.hash;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            bool hasData = entry.speedSamples != null && entry.pathDistances != null;
            float totalDistance = hasData && entry.pathDistances.Length > 0
                ? entry.pathDistances[entry.pathDistances.Length - 1]
                : 0f;
            hasData = hasData && totalDistance > 0.001f;

            for (int x = 0; x < SPEED_HEATMAP_TEXTURE_WIDTH; x++)
            {
                Color color = new Color(0.5f, 0.5f, 0.5f, 1f);
                if (hasData)
                {
                    float distance = ((x + 0.5f) / SPEED_HEATMAP_TEXTURE_WIDTH) * totalDistance;
                    float speed = InterpolateSpeedAtDistance(entry.speedSamples, entry.pathDistances, distance);
                    if (IsFinite(speed))
                        color = SpeedHeatmapColor(NormalizeHeatmapSpeed(speed));
                }
                texture.SetPixel(x, 0, color);
            }
            texture.Apply(false, false);
            return texture;
        }

        private static float InterpolateSpeedAtDistance(float[] speeds, float[] distances, float query)
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

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Gradient CreateSolidGradient(Color color)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

    public class GhostLineEntry
    {
        public System.WeakReference recordingRef;
        public LocalGhostStatesRecording GetRecording()
        {
            return recordingRef != null && recordingRef.IsAlive ? recordingRef.Target as LocalGhostStatesRecording : null;
        }
        public int hash;
        public string pilotName;
        public Color color;
        public bool visible = true;
        public GameObject lineObject;
        public Material lineMaterial;
        public GameObject markerObject;
        public LineRenderer markerTether;
        public TextMesh markerText;
        public Vector3[] originalPositions;
        public Quaternion[] originalRotations;
        public Vector4[] originalInputs;
        public float[] stateTimes;
        public bool built;
        public float lapStartOffset;
        public float finishTime = -1f;
        public float markerBaseHeight = 2.8f;
        public float[] pathDistances;
        public float[] speedSamples;
        public Texture2D heatmapTexture;
        public float lapStartDistance;
        public float lapTotalDistance;
        public TextMesh deltaText;
        public string guiDeltaText = "";
        public string resolvedReplayName;
        public bool replayNameResolved = false;
        public Color? resolvedReplayColor;
        public bool replayColorResolved = false;
        public string lastShownDeltaText = "";
        public float lastDeltaShownValue = float.NaN;
        public static int entriesEpoch;
    }

public class GhostLinePoller : MonoBehaviour
    {
        private float nextCheck = 0f;
        private Rect settingsRect = new Rect(-1f, -1f, 304f, 360f);
        private bool settingsOpen = false;
        private bool settingsDragging = false;
        private bool settingsDragCandidate = false;
        private bool settingsPanelPositionCustomized = false;
        private bool settingsControlPressed = false;
        private Vector2 settingsDragOffset;
        private Texture2D whiteTex;
        private GameObject replayControlsObj;
        private CanvasGroup controlsCG;
        private bool controlsHidden = false;
        private bool controlsStateCaptured = false;
        private bool controlsCanvasGroupAdded = false;
        private float originalControlsAlpha;
        private bool originalControlsInteractable;
        private bool originalControlsBlocksRaycasts;
        private static readonly System.Reflection.FieldInfo droneHudModuleParentField =
            AccessTools.Field(typeof(DroneHUD), "moduleParent");
        private DroneHUD nativeDroneHud;
        private Canvas nativeHudModuleParent;
        private bool nativeHudModuleParentStateCaptured;
        private bool nativeHudModuleParentOriginalEnabled;
        private float nextNativeHudResolve = 0f;
        private float nextNativeStandingsResolve = 0f;
        private int nativeStandingsSceneHandle = int.MinValue;
        private bool nativeStandingsSceneValid = false;
        private List<Text> nativeStandingsTexts = new List<Text>();
        private Dictionary<Text, FontStyle> nativeStandingsBaseStyles = new Dictionary<Text, FontStyle>();
        private Dictionary<Text, string> nativeStandingsTextLastContent = new Dictionary<Text, string>();
        private Dictionary<Text, GhostLineEntry> nativeStandingsEntryCache = new Dictionary<Text, GhostLineEntry>();
        private int nativeStandingsEntriesEpochSeen = int.MinValue;
        internal bool replayEntriesDirty = true;
        private object lastSyncedReplayRace;
        private System.Reflection.FieldInfo replayRaceFieldInfo;
        private System.Reflection.PropertyInfo replayRacePropInfo;
        private System.Reflection.FieldInfo trackedRootFieldInfo;
        private System.Reflection.FieldInfo trackedCameraMountFieldInfo;
        private System.Reflection.PropertyInfo sliderMinValuePropInfo;
        private System.Reflection.PropertyInfo sliderMaxValuePropInfo;
        private System.Reflection.PropertyInfo controllerMaxTimePropInfo;
        private System.Reflection.FieldInfo flightCamImplFieldInfo;
        private System.Reflection.PropertyInfo currentCameraModePropInfo;
        private System.Reflection.PropertyInfo sliderValueProp;
        private Component sliderComponent;
        private float sliderSearchTimer = 0f;
        private float scrubberX = -1f;
        private float scrubberY = -1f;
        private bool scrubberPositionCustomized = false;
        private int overlayScreenWidth = -1;
        private int overlayScreenHeight = -1;
        private bool draggingScrubber = false;
        private float stickCamX = -1f;
        private float stickCamY = -1f;
        private bool stickCamPositionCustomized = false;
        private bool draggingStickCam = false;
        private Vector2 dragStartStickCamPos;
        private float savedStickCamPositionX = 0.5f;
        private float savedStickCamPositionY = 0.8f;
        private bool savedStickCamPositionCustomized = false;
        private float savedPlaybackUiPositionX = 0.5f;
        private float savedPlaybackUiPositionY = 0.9f;
        private bool savedPlaybackUiPositionCustomized = false;
        private float savedSettingsPanelPositionX = 0.7f;
        private float savedSettingsPanelPositionY = 0.4f;
        private bool savedSettingsPanelPositionCustomized = false;
        private bool savedSettingsPanelOpen = false;

        public void SetSavedSettingsPanelOpen(bool open)
        {
            savedSettingsPanelOpen = open;
        }
        private bool isDraggingHandle = false;
        private bool resumePlaybackAfterSeek = false;
        private float playbackSpeed = 1f;
        private bool overridePlayback = false;
        private Vector2 dragStartMouse;
        private Vector2 dragStartBarPos;
        private float fadeAmount = 1f;
        private float settingsFadeAmount = 1f;
        private Vector2 lastMousePos;
        private float mouseStillTimer = 0f;
        private bool playbackMouseCaptured = false;
        private bool playbackPinned = false;
        private bool settingsPinned = false;
        private bool bindingsOpen = false;
        private bool bindingsPinned = false;
        private bool bindingsDragging = false;
        public bool NativeReplayReady { get { return replayReady; } }
        public bool IsBindingListening { get { return bindingListeningFor != null; } }
        public void MarkBindingCaptureFrame() { bindingCaptureSuppressFrame = Time.frameCount; }
        private string bindingListeningFor = null;
        private bool bindingListeningJustStarted = false;
        private int bindingCaptureSuppressFrame = -1;
        private Rect bindingsWindowRect = new Rect(0f, 0f, 0f, 0f);
        private float bindingsFadeAmount = 0f;
        private bool bindingsDragCandidate = false;
        private Vector2 bindingsDragOffset = Vector2.zero;
        private class StickGraphState
        {
            public GhostLineEntry pilot;
            public Rect rect = new Rect(-1f, -1f, 460f, STICK_GRAPH_MIN_HEIGHT);
            public bool pinned;
            public bool dragging;
            public bool dragCandidate;
            public Vector2 dragOffset;
            public float fadeAmount;
            public int channel;
            public bool channelDropdown;
            public bool pilotDropdown;
            public bool heatmap;
            public bool resizing;
            public int resizeEdge;
            public int edgeHover;
            public Vector2 resizeStartMouse;
            public Rect resizeStartRect;
            public Texture2D traceTex;
            public int traceCachePlotW;
            public int traceCachePlotH;
            public GhostLineEntry traceCachePilot;
            public int traceCacheChannel;
            public bool traceCacheHeatmap;
            public Vector4[] traceCacheInputs;
            public float traceCacheLapStart;
            public float traceCacheLapDuration;
        }

        private List<StickGraphState> stickGraphs = new List<StickGraphState>();
        private StickGraphState stickGraphActivated;
        private readonly List<GhostLineEntry> stickGraphPilotOptions = new List<GhostLineEntry>();
        private const float STICK_GRAPH_MIN_WIDTH = 320f;
        private const float STICK_GRAPH_MIN_HEIGHT = 200f;
        private const float STICK_GRAPH_EDGE_THICK = 6f;
        private const float STICK_GRAPH_CORNER_GRAB = 14f;
        private const int STICK_GRAPH_EDGE_RIGHT = 1;
        private const int STICK_GRAPH_EDGE_BOTTOM = 2;
        private const int STICK_GRAPH_EDGE_LEFT = 4;
        private const int STICK_GRAPH_EDGE_TOP = 8;
        private const float STICK_GRAPH_OPEN_OFFSET = 28f;
        private float lastStickCamClickTime = -1f;
        private Vector2 lastStickCamClickPos;
        private static readonly string[] stickGraphChannelNames = { "Throttle", "Yaw", "Roll", "Pitch" };
        private float replayEnterTime = 0f;
        private bool replayReady = false;
        private object activeReplayRace;
        private float nextReplaySync = 0f;
        private float nextReplayControlsSearch = 0f;
        private int frameStepHeldDirection = 0;
        private float frameStepNextRepeat = 0f;
        private bool frameStepPaused = false;
        private const float FRAME_STEP_SECONDS = 1f / 60f;
        private const float FRAME_STEP_INITIAL_DELAY = 0.25f;
        private const float FRAME_STEP_REPEAT_INTERVAL = 1f / 30f;
        private float leaderboardSearchTimer = 0f;
        private GameObject leaderboardSearchInputObject;
        private InputField leaderboardSearchInput;
        private Dropdown leaderboardContentDropdown;
        private Dropdown leaderboardEnvironmentDropdown;
        private int leaderboardEnvironmentValue = -1;
        private bool leaderboardContentRefreshPending = false;
        private string leaderboardPendingContentSignature = "";
        private int leaderboardPendingContentStableFrames = 0;
        private Component leaderboardController;
        private List<Dropdown.OptionData> leaderboardContentOptions = new List<Dropdown.OptionData>();
        private string leaderboardDisplayedSignature = "";
        private string leaderboardSearchSelectedText = "";
        private Text leaderboardOriginalContentText;
        private bool leaderboardOriginalContentTextActive;
        private bool leaderboardOriginalDropdownEnabled;
        private int leaderboardOriginalDropdownValue;
        private bool leaderboardSearchStarting;
        private bool leaderboardSearchRefocusPending;
        private bool leaderboardSearchFocusLock;
        private System.Collections.IEnumerator leaderboardSearchRefocusCoroutine;
        private System.Reflection.FieldInfo leaderboardDropdownPopupField;
        private System.Reflection.FieldInfo leaderboardDropdownItemsField;
        private System.Reflection.PropertyInfo leaderboardPopupItemRectProperty;
        private System.Reflection.PropertyInfo leaderboardPopupItemTextProperty;
        private bool leaderboardPopupLayoutCached;
        private float leaderboardPopupFirstItemX;
        private float leaderboardPopupLastItemY;
        private float leaderboardPopupRowHeight;
        private float leaderboardPopupBaseContentHeight;
        private bool leaderboardSearchUpdating = false;
        private bool leaderboardNotFoundLogged = false;
        private bool leaderboardSearchReady = false;
        private List<Toggle> leaderboardSelectedTogglesQueue = new List<Toggle>();
        private HashSet<Toggle> leaderboardDragProcessedToggles = new HashSet<Toggle>();
        private bool isLeaderboardDragSelecting = false;
        private bool leaderboardDragStartWasSelected = false;
        private int leaderboardDragStartRowIndex = -1;
        private int leaderboardDragLastRowIndex = -1;
        private ScrollRect leaderboardCachedScrollRect = null;
        private bool leaderboardScrollRectWasEnabled = true;
        private Transform leaderboardCachedScrollViewContent = null;
        private float leaderboardScrollViewContentSearchTimer = 0f;
        private bool leaderboardScrollViewFallbackExhausted = false;
        private bool isPlaying = false;
        private bool restartRequested = false;
        private bool restartPlaybackPending = false;
        private bool pendingPlayToggle = false;
        private Component replayController;
        private System.Reflection.MethodInfo togglePlayPauseMethod;
        private System.Reflection.MethodInfo attachToDroneMethod;
        private System.Type attachToDroneParameterType;
        private System.Reflection.PropertyInfo timeProp;
        private bool cameraDumped = false;
        private bool noTargetLogged = false;
        private Transform orbitTarget;
        private float orbitDist = 0f;
        private float orbitYaw = 0f;
        private float orbitPitch = 0f;
        private bool isOrbiting = false;
        private bool cameraLookDragActive = false;
        private bool cameraLookSampleValid = false;
        private Vector2 lastCameraLookMouse;
        private int cameraLookScreenWidth = -1;
        private int cameraLookScreenHeight = -1;
        private int cameraRawMouseAxesState = 0;
        private float cameraRawMouseScaleX = 1f;
        private float cameraRawMouseScaleY = 1f;
        private struct DroneLabelHitbox
        {
            public GhostLineEntry entry;
            public Rect rect;
            public float depth;
        }
        private List<DroneLabelHitbox> droneLabelHitboxes = new List<DroneLabelHitbox>();
        private static readonly Vector3[] scratchLabelCorners = new Vector3[8];
        private GhostLineEntry droneLabelClickCandidate;
        private GhostLineEntry hoveredDroneLabelEntry;
        private Vector2 droneLabelClickStart;
        private Rect currentPlaybackOverlayRect;
        private Rect currentStickCamOverlayRect;
        private bool currentPlaybackOverlayRectValid;
        private bool currentStickCamOverlayRectValid;
        private bool pendingCameraViewRestore;
        private bool pendingFreeCamViewRestore;
        private bool pendingOrbitViewRestore;
        private Vector3 pendingFreeCamPosition;
        private float pendingFreeCamYaw;
        private float pendingFreeCamPitch;
        private float pendingOrbitDist;
        private float pendingOrbitYaw;
        private float pendingOrbitPitch;
        private bool pendingOrbiting;
        private bool customCameraActive = false;
        private bool startedFromFpv = false;
        private float pendingThirdPersonScroll = 0f;
        private bool freeCamActive = false;
        private Component flightCamCtrl;
        private System.Reflection.PropertyInfo inThirdPersonProp;
        private System.Reflection.PropertyInfo stuntCameraRefProp;
        private System.Reflection.PropertyInfo trackingDroneDataProp;
        private object flightCamImpl = null;
        private System.Reflection.MethodInfo cameraModeSetMethod = null;
        private System.Type cameraModeEnumType = null;
        private int lastNativeCameraMode = 0;
        private int pendingCameraModeRestore = 0;
        private int replayEntryCameraMode = 0;
        private const int CAMERA_MODE_FIRST_PERSON = 1;
        private const int CAMERA_MODE_THIRD_PERSON_CLOSE = 2;
        private const int CAMERA_MODE_THIRD_PERSON_FAR = 3;
        private bool fpvOnReplayPending = false;
        private float fpvForceTimeout = 0f;
        private bool fpvForceLogged = false;
        private bool referenceAutoInitialized = false;
        private bool xrayEnabled = false;
        private bool darkMode = false;
        private bool stickCamEnabled = true;
        private float stickCamSize = 1f;
        private float playbackUiSize = 1f;
        private bool stickSizeDragging = false;
        private bool uiSizeDragging = false;
        private bool xrayStructuresBuilt = false;
        private List<XrayStructure> xrayStructures = new List<XrayStructure>();
        private List<XrayStructure> xrayVisibleStructures = new List<XrayStructure>();
        private List<XrayStructure> xrayFadingVisible = new List<XrayStructure>();
        private Collider[] xrayColliderBuffer = new Collider[2048];
        private Vector3 xrayStructureCenter;
        private bool xrayStructureCenterValid = false;
        private List<MeshFilter> xrayDroneFilters = new List<MeshFilter>();
        private Transform xrayDroneRoot;
        private Mesh xrayMapMesh;
        private List<Vector3> xrayMapVertices = new List<Vector3>(XRAY_MAX_LINES * 2);
        private List<Color> xrayMapColors = new List<Color>(XRAY_MAX_LINES * 2);
        private List<int> xrayMapIndices = new List<int>(XRAY_MAX_LINES * 2);
        private bool xrayMapCacheValid = false;
        private int xrayStructureRevision = 0;
        private int xrayMapCachedStructureRevision = -1;
        private Vector3 xrayMapCachedDronePosition;
        private Vector3[] xrayDroneBoundsCorners = new Vector3[8];
        private Material xrayWireMaterial;
        private const int XRAY_MAX_LINES = 12000;
        private const float XRAY_FADE_SPEED = 8f;
        private const float XRAY_FADE_BUFFER = 5f;
        private bool xraySeedInstant;

        public void LoadSavedPreferences()
        {
            stickCamEnabled = stickCamEnabledConfig.Value;
            stickCamSize = ClampSavedScale(stickCamSizeConfig.Value, 0.5f, 1.8f, 1f);
            playbackUiSize = ClampSavedScale(playbackUiSizeConfig.Value, 0.6f, 1.6f, 1f);
            darkMode = darkModeConfig.Value;
            savedSettingsPanelOpen = settingsPanelOpenConfig.Value;
            savedStickCamPositionCustomized = stickCamPositionCustomizedConfig.Value;
            savedStickCamPositionX = Mathf.Clamp01(ClampSavedScale(stickCamPositionXConfig.Value, 0f, 1f, 0.5f));
            savedStickCamPositionY = Mathf.Clamp01(ClampSavedScale(stickCamPositionYConfig.Value, 0f, 1f, 0.8f));
            savedPlaybackUiPositionCustomized = playbackUiPositionCustomizedConfig.Value;
            savedPlaybackUiPositionX = Mathf.Clamp01(ClampSavedScale(playbackUiPositionXConfig.Value, 0f, 1f, 0.5f));
            savedPlaybackUiPositionY = Mathf.Clamp01(ClampSavedScale(playbackUiPositionYConfig.Value, 0f, 1f, 0.9f));
            savedSettingsPanelPositionCustomized = settingsPanelPositionCustomizedConfig.Value;
            savedSettingsPanelPositionX = Mathf.Clamp01(ClampSavedScale(settingsPanelPositionXConfig.Value, 0f, 1f, 0.7f));
            savedSettingsPanelPositionY = Mathf.Clamp01(ClampSavedScale(settingsPanelPositionYConfig.Value, 0f, 1f, 0.4f));
            settingsPanelPositionCustomized = savedSettingsPanelPositionCustomized;
            stickCamPositionCustomized = savedStickCamPositionCustomized;
            scrubberPositionCustomized = savedPlaybackUiPositionCustomized;
        }

        private static float ClampSavedScale(float value, float min, float max, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);
        }

        private void SaveDisplayPreferences()
        {
            GhostLinesPlugin.SaveDisplayPreferences(stickCamEnabled, stickCamSize, playbackUiSize, darkMode);
        }

        private void SaveCurrentOverlayPositions()
        {
            float stickX = savedStickCamPositionX;
            float stickY = savedStickCamPositionY;
            float playbackX = savedPlaybackUiPositionX;
            float playbackY = savedPlaybackUiPositionY;
            float settingsX = savedSettingsPanelPositionX;
            float settingsY = savedSettingsPanelPositionY;

            if (stickCamPositionCustomized && Screen.width > 0 && Screen.height > 0 && stickCamX >= 0f && stickCamY >= 0f)
            {
                stickX = stickCamX / Screen.width;
                stickY = stickCamY / Screen.height;
                savedStickCamPositionCustomized = true;
            }
            if (scrubberPositionCustomized && Screen.width > 0 && Screen.height > 0 && scrubberX >= 0f && scrubberY >= 0f)
            {
                playbackX = scrubberX / Screen.width;
                playbackY = scrubberY / Screen.height;
                savedPlaybackUiPositionCustomized = true;
            }
            if (settingsPanelPositionCustomized && settingsRect.x >= 0f && settingsRect.y >= 0f && Screen.width > 0 && Screen.height > 0)
            {
                settingsX = settingsRect.x / Screen.width;
                settingsY = settingsRect.y / Screen.height;
                savedSettingsPanelPositionCustomized = true;
            }

            savedStickCamPositionX = Mathf.Clamp01(stickX);
            savedStickCamPositionY = Mathf.Clamp01(stickY);
            savedPlaybackUiPositionX = Mathf.Clamp01(playbackX);
            savedPlaybackUiPositionY = Mathf.Clamp01(playbackY);
            savedSettingsPanelPositionX = Mathf.Clamp01(settingsX);
            savedSettingsPanelPositionY = Mathf.Clamp01(settingsY);
            GhostLinesPlugin.SaveOverlayPositions(
                savedStickCamPositionCustomized,
                savedStickCamPositionX,
                savedStickCamPositionY,
                savedPlaybackUiPositionCustomized,
                savedPlaybackUiPositionX,
                savedPlaybackUiPositionY,
                savedSettingsPanelPositionCustomized,
                savedSettingsPanelPositionX,
                savedSettingsPanelPositionY);
        }

        private static float SnapOverlayToHorizontalCenter(float x)
        {
            float center = Screen.width * 0.5f;
            return Mathf.Abs(x - center) <= 24f ? center : x;
        }
        private const float XRAY_QUERY_RADIUS = 20f;
        private const float XRAY_RENDER_RADIUS = 15f;
        private const float XRAY_REBUILD_DISTANCE = 1.5f;
        private Vector3 freeCamPosition;
        private float freeCamYaw = 0f;
        private float freeCamPitch = 0f;
        private const float FREE_CAM_SPEED = 6f;
        private const float FREE_CAM_SHIFT_MAX_SPEED = 48f;
        private const float FREE_CAM_SHIFT_ACCELERATION = 18f;
        private const float FREE_CAM_SHIFT_DECELERATION = 72f;
        private const float FREE_CAM_MOUSE_SENSITIVITY = 0.3f;
        private float freeCamCurrentSpeed = FREE_CAM_SPEED;
        private const float HOLD_ZOOM_FOV_SCALE = 0.35f;
        private const float HOLD_ZOOM_MIN_FOV = 10f;
        private const float HOLD_ZOOM_EXTRA_MIN_FOV = 2f;
        private const float HOLD_ZOOM_SMOOTH_TAU = 0.22f;
        private const float HOLD_ZOOM_SCROLL_STEP = 6f;
        private const float HOLD_ZOOM_SCROLL_RATIO = 1.39f;
        private bool holdZoomFovCaptured = false;
        private float holdZoomNormalFov = 60f;
        private float holdZoomFovBias = 0f;
        private float holdZoomCurrentFov = 60f;
        private float holdZoomSmoothFov = 60f;
        private bool holdZoomActive = false;
        private float stableReplayTime = 0f;
        private float prevReplayTime = 0f;
        private float replayTimeDelta = 0f;
        private float predictionLead = 0f;
        private float displayReplayTime = 0f;
        private float zoomTransitionUntil = 0f;
        private float zoomTransitionStartYaw = 0f;
        private float zoomTransitionStartPitch = 0f;
        private const float ZOOM_TRANSITION_DURATION = 0.25f;
        private const float DRONE_MARKER_HEIGHT = 2.8f;
        private GhostLineEntry spectatedEntry;
        public GhostLineEntry referenceEntry;
        public static GhostLinePoller Instance;
        private Dictionary<GhostLineEntry, Transform> ghostDroneMap = new Dictionary<GhostLineEntry, Transform>();
        private Dictionary<GhostLineEntry, Vector3> ghostSmoothedPos = new Dictionary<GhostLineEntry, Vector3>();
        private Dictionary<GhostLineEntry, Quaternion> ghostSmoothedRot = new Dictionary<GhostLineEntry, Quaternion>();
        private float ghostPosBlend = 0f;
        private Dictionary<GhostLineEntry, Transform> ghostCamMountMap = new Dictionary<GhostLineEntry, Transform>();
        private class GhostTrailState
        {
            public TrailRenderer trail;
            public bool enabled;
            public bool emitting;
        }

        private struct XraySegment
        {
            public Vector3 a;
            public Vector3 b;

            public XraySegment(Vector3 a, Vector3 b)
            {
                this.a = a;
                this.b = b;
            }
        }

        private class XrayStructure
        {
            public Bounds bounds;
            public List<XraySegment> segments;
            public float currentAlpha;
            public int colliderId;
        }
        private static readonly int[] xrayBoxEdgePairs =
        {
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        };
        private Dictionary<GhostLineEntry, List<GhostTrailState>> ghostTrailStates = new Dictionary<GhostLineEntry, List<GhostTrailState>>();
        private float ghostDroneSearchTimer = 0f;
        private int ghostMatchAttempts = 0;
        private const int GHOST_MATCH_MAX_ATTEMPTS = 15;
        private const float GHOST_MATCH_RETRY_INTERVAL = 2f;
        private bool recordingConstructionThreadChecked = false;
        private HashSet<Transform> cameraMountLoggedRoots = new HashSet<Transform>();
        private bool lateIsThirdPerson = true;
        private Vector3 ghostDronePos;
        private bool ghostDronePosValid = false;
        private Texture2D iconPlay;
        private Texture2D iconPause;
        private Texture2D iconRewind;
        private Texture2D iconFF;
        private Texture2D iconLoop;
        private Texture2D iconSettings;
        private Texture2D iconPin;
        private Texture2D iconBindings;
        private bool iconsInitialized;
        private bool loopReplayEnabled = false;
        private Texture2D stickRingTex;
        private GUIStyle settingsLabelStyle;
        private GUIStyle settingsSmallStyle;
        private GUIStyle settingsSmallCenterStyle;
        private GUIStyle settingsAxisLabelStyle;
        private GUIStyle settingsHeaderStyle;
        private GUIStyle settingsSectionStyle;
        private GUIStyle settingsPlusStyle;
        private GUIStyle settingsCloseStyle;
        private GUIStyle playbackTimeLeftStyle;
        private GUIStyle playbackTimeRightStyle;
        private GUIStyle playbackSpeedStyle;
        private Texture2D targetRingTex;
        private Texture2D targetDotTex;
        private Texture2D targetFillTex;

        private void RestoreGhostTrails()
        {
            foreach (var pair in ghostTrailStates)
            {
                if (pair.Value == null) continue;
                foreach (var state in pair.Value)
                {
                    if (state == null || state.trail == null) continue;
                    state.trail.enabled = state.enabled;
                    state.trail.emitting = state.emitting;
                }
            }
            ghostTrailStates.Clear();
        }

        private void CacheGhostTrails(GhostLineEntry entry, Transform drone)
        {
            if (entry == null || drone == null)
                return;

            if (ghostTrailStates.ContainsKey(entry))
            {
                RestoreGhostTrailsForEntry(entry);
                ghostTrailStates.Remove(entry);
            }

            var states = new List<GhostTrailState>();
            foreach (var trail in drone.GetComponentsInChildren<TrailRenderer>(true))
            {
                if (trail == null) continue;
                states.Add(new GhostTrailState
                {
                    trail = trail,
                    enabled = trail.enabled,
                    emitting = trail.emitting
                });
                trail.Clear();
                trail.emitting = false;
                trail.enabled = false;
            }
            ghostTrailStates[entry] = states;
            SetGhostTrailsSuppressed(entry, true);
        }

        private void RestoreGhostTrailsForEntry(GhostLineEntry entry)
        {
            if (!ghostTrailStates.TryGetValue(entry, out var states) || states == null)
                return;
            foreach (var state in states)
            {
                if (state == null || state.trail == null) continue;
                state.trail.enabled = state.enabled;
                state.trail.emitting = state.emitting;
            }
        }

        private void SetGhostTrailsSuppressed(GhostLineEntry entry, bool suppressed)
        {
            if (!ghostTrailStates.TryGetValue(entry, out var states) || states == null)
                return;

            foreach (var state in states)
            {
                if (state == null || state.trail == null) continue;
                if (suppressed)
                {
                    state.trail.Clear();
                    state.trail.emitting = false;
                    state.trail.enabled = false;
                }
                else
                {
                    state.trail.enabled = state.enabled;
                    state.trail.emitting = state.emitting;
                }
            }
        }

        private void SetAllRaceLinesVisible(bool visible)
        {
            foreach (var entry in GhostLinesPlugin.entries)
                SetRaceLineVisible(entry, visible);
        }

        private void SetRaceLineVisible(GhostLineEntry entry, bool visible)
        {
            if (entry == null) return;
            entry.visible = visible;
            if (entry.lineObject != null)
                entry.lineObject.SetActive(visible);
            if (entry.markerObject != null)
                entry.markerObject.SetActive(true);
            if (entry.markerTether != null)
                entry.markerTether.enabled = GhostLinesPlugin.showNameTags || GhostLinesPlugin.showDeltaDisplay;
            SetGhostTrailsSuppressed(entry, true);
        }

        private void DestroyActiveEntry(GhostLineEntry entry)
        {
            if (entry == null) return;
            Transform mappedRoot;
            if (ghostDroneMap.TryGetValue(entry, out mappedRoot))
            {
                if (xrayDroneRoot == mappedRoot)
                {
                    xrayDroneRoot = null;
                    xrayDroneFilters.Clear();
                    InvalidateXrayMapCache();
                }
                if (mappedRoot != null)
                    cameraMountLoggedRoots.Remove(mappedRoot);
                ghostDroneMap.Remove(entry);
            }
            ghostCamMountMap.Remove(entry);
            RestoreGhostTrailsForEntry(entry);
            ghostTrailStates.Remove(entry);
            GhostLinesPlugin.DestroyLineResources(entry);
            if (entry.markerObject != null) UnityEngine.Object.Destroy(entry.markerObject);
            entry.markerObject = null;
            entry.markerTether = null;
            entry.markerText = null;
            entry.deltaText = null;
            entry.guiDeltaText = "";
            entry.visible = false;
            entry.built = false;
            if (spectatedEntry == entry)
                spectatedEntry = null;
            if (referenceEntry == entry)
                referenceEntry = null;
            if (hoveredDroneLabelEntry == entry)
                SetHoveredDroneLabel(null);
            if (droneLabelClickCandidate == entry)
                droneLabelClickCandidate = null;
        }

        private void ClearActiveReplayEntries()
        {
            RestoreGhostTrails();
            foreach (var entry in GhostLinesPlugin.entries)
                DestroyActiveEntry(entry);
            GhostLinesPlugin.entries.Clear();
            GhostLineEntry.entriesEpoch++;
            ghostDroneMap.Clear();
            ghostSmoothedPos.Clear();
            ghostSmoothedRot.Clear();
            ghostCamMountMap.Clear();
            spectatedEntry = null;
            referenceEntry = null;
            referenceAutoInitialized = false;
            ghostDroneSearchTimer = 0f;
            ghostMatchAttempts = 0;
            ghostDronePosValid = false;
        }

        private object GetReplayRace()
        {
            if (replayController == null)
                return null;
            try
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                System.Type type = replayController.GetType();
                if (replayRaceFieldInfo == null && replayRacePropInfo == null)
                {
                    replayRaceFieldInfo = type.GetField("replayRace", flags);
                    if (replayRaceFieldInfo == null)
                        replayRacePropInfo = type.GetProperty("replayRace", flags);
                }
                if (replayRaceFieldInfo != null)
                    return replayRaceFieldInfo.GetValue(replayController);
                if (replayRacePropInfo != null && replayRacePropInfo.CanRead)
                    return replayRacePropInfo.GetValue(replayController, null);
                return null;
            }
            catch { return null; }
        }

        private bool ContainsReplayRecording(object obj, object recording, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || recording == null || depth > 6)
                return false;
            if (object.ReferenceEquals(obj, recording))
                return true;
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return false;
            if (obj is System.Array && obj.GetType().GetElementType() == typeof(byte))
                return false;
            if (obj is UnityEngine.Object && depth > 0)
                return false;
            if (!visited.Add(obj))
                return false;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                try
                {
                    foreach (var item in enumerable)
                    {
                        if (walked++ >= 256)
                            break;
                        if (ContainsReplayRecording(item, recording, depth + 1, visited))
                            return true;
                    }
                }
                catch { }
            }

            var fields = obj.GetType().GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                if (ContainsReplayRecording(child, recording, depth + 1, visited))
                    return true;
            }
            return false;
        }

        private void CollectReplayRecordingCandidates(object obj, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || depth > 6)
                return;
            if (obj is LocalGhostStatesRecording recording)
            {
                GhostLinesPlugin.AddRecordingCandidate(recording);
                return;
            }
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return;
            if (obj is System.Array && obj.GetType().GetElementType() == typeof(byte))
                return;
            if (obj is UnityEngine.Object && depth > 0)
                return;
            if (!visited.Add(obj))
                return;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                try
                {
                    foreach (var item in enumerable)
                    {
                        if (walked++ >= 256)
                            break;
                        CollectReplayRecordingCandidates(item, depth + 1, visited);
                    }
                }
                catch { }
            }

            var fields = obj.GetType().GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                CollectReplayRecordingCandidates(child, depth + 1, visited);
            }
        }

        private bool IsCandidateReadyForReplay(GhostLineEntry candidate, object replayRace)
        {
            if (candidate == null || candidate.GetRecording() == null || replayRace == null)
                return false;
            var recording = candidate.GetRecording();
            if (recording.states == null || recording.states.Count < 2)
                return false;
            if (object.ReferenceEquals(replayRace, replayController))
            {
                try
                {
                    return (recording.GameMode & LevelFlags.Replay) != 0;
                }
                catch { return false; }
            }
            return ContainsReplayRecording(replayRace, recording, 0, new System.Collections.Generic.HashSet<object>());
        }

        private void SyncActiveReplayEntries()
        {
            if (!replayReady || activeReplayRace == null)
                return;

            bool raceChanged = !object.ReferenceEquals(lastSyncedReplayRace, activeReplayRace);
            if (raceChanged)
                lastSyncedReplayRace = activeReplayRace;

            if (raceChanged || replayEntriesDirty)
            {
                replayEntriesDirty = false;
                for (int i = GhostLinesPlugin.entries.Count - 1; i >= 0; i--)
                {
                    var entry = GhostLinesPlugin.entries[i];
                    if (!IsCandidateReadyForReplay(entry, activeReplayRace))
                    {
                        DestroyActiveEntry(entry);
                        GhostLinesPlugin.entries.RemoveAt(i);
                        GhostLineEntry.entriesEpoch++;
                    }
                }

                foreach (var candidate in recordingCandidates)
                {
                    if (!IsCandidateReadyForReplay(candidate, activeReplayRace))
                        continue;
                    if (!GhostLinesPlugin.entries.Contains(candidate))
                    {
                        GhostLinesPlugin.entries.Add(candidate);
                        GhostLineEntry.entriesEpoch++;
                    }
                    GhostLinesPlugin.TryBuildLine(candidate);
                }
            }
            else
            {
                for (int i = GhostLinesPlugin.entries.Count - 1; i >= 0; i--)
                {
                    var entry = GhostLinesPlugin.entries[i];
                    if (entry.GetRecording() == null)
                    {
                        DestroyActiveEntry(entry);
                        GhostLinesPlugin.entries.RemoveAt(i);
                        GhostLineEntry.entriesEpoch++;
                    }
                }

                foreach (var candidate in recordingCandidates)
                {
                    if (candidate.GetRecording() == null)
                        continue;
                    if (!GhostLinesPlugin.entries.Contains(candidate))
                    {
                        GhostLinesPlugin.entries.Add(candidate);
                        GhostLineEntry.entriesEpoch++;
                    }
                    GhostLinesPlugin.TryBuildLine(candidate);
                }
            }
        }

        private bool TryGetFullyLoadedReplay(out object replayRace)
        {
            replayRace = null;
            FindReplayControls();
            if (replayControlsObj == null || !replayControlsObj.activeInHierarchy)
                return false;
            CacheReplayController();
            if (replayController == null || timeProp == null || !timeProp.CanRead)
                return false;

            replayRace = GetReplayRace();
            if (replayRace == null)
            {
                try
                {
                    var maxProp = replayController.GetType().GetProperty("MaximumTimelineTime",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (maxProp == null || (float)maxProp.GetValue(replayController, null) <= 0.01f)
                        return false;
                }
                catch { return false; }
                replayRace = replayController;
            }
            return true;
        }

        private void FindReplayControls()
        {
            if (replayControlsObj != null && replayControlsObj.activeInHierarchy)
                return;
            if (Time.time < nextReplayControlsSearch)
                return;
            nextReplayControlsSearch = Time.time + 1f;
            ReplayControls[] controls = UnityEngine.Object.FindObjectsOfType<ReplayControls>(true);
            foreach (var control in controls)
            {
                if (control == null || !control.gameObject.scene.IsValid())
                    continue;

                GameObject root = control.gameObject;
                Transform current = control.transform;
                while (current != null)
                {
                    if (current.name.IndexOf("ReplayControlsUI", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        root = current.gameObject;
                        break;
                    }
                    current = current.parent;
                }

                bool changed = replayController != control || replayControlsObj != root;
                replayController = control;
                BindReplayControls(root);
                if (changed)
                    Debug.Log($"[GhostLines] Found ReplayControls component on {control.gameObject.name}");
                return;
            }
        }

        private void BindReplayControls(GameObject controlsRoot)
        {
            if (controlsRoot == null)
                return;

            bool shouldHide = controlsHidden || replayReady;
            if (replayControlsObj != controlsRoot)
            {
                RestoreReplayControlsUI();
                controlsCG = null;
                controlsHidden = shouldHide;
            }

            replayControlsObj = controlsRoot;
            if (controlsCG == null)
            {
                controlsCG = replayControlsObj.GetComponent<CanvasGroup>();
                controlsCanvasGroupAdded = false;
            }

            if (controlsHidden)
                SetReplayControlsHidden(true);
        }

        private void CaptureReplayControlsState()
        {
            if (controlsCG == null || controlsStateCaptured)
                return;

            originalControlsAlpha = controlsCG.alpha;
            originalControlsInteractable = controlsCG.interactable;
            originalControlsBlocksRaycasts = controlsCG.blocksRaycasts;
            controlsStateCaptured = true;
        }

        private void SetReplayControlsHidden(bool hidden)
        {
            if (controlsCG == null && replayControlsObj != null)
            {
                controlsCG = replayControlsObj.AddComponent<CanvasGroup>();
                controlsCanvasGroupAdded = true;
            }
            if (controlsCG == null)
                return;

            CaptureReplayControlsState();
            controlsHidden = hidden;
            controlsCG.alpha = hidden ? 0f : 1f;
            controlsCG.interactable = !hidden;
            controlsCG.blocksRaycasts = !hidden;
        }

        public void EnforceNativeHudVisibility()
        {
            if (!replayReady)
                return;

            if (nativeHudModuleParent == null)
            {
                nativeHudModuleParentStateCaptured = false;
                if (Time.unscaledTime < nextNativeHudResolve)
                    return;

                nextNativeHudResolve = Time.unscaledTime + 0.5f;
                if (nativeDroneHud == null)
                    nativeDroneHud = DroneHUD.use;
                if (nativeDroneHud == null || droneHudModuleParentField == null)
                    return;

                nativeHudModuleParent = droneHudModuleParentField.GetValue(nativeDroneHud) as Canvas;
                if (nativeHudModuleParent == null)
                    return;

                nativeHudModuleParentOriginalEnabled = nativeHudModuleParent.enabled;
                nativeHudModuleParentStateCaptured = true;
            }

            nativeHudModuleParent.enabled = false;
        }

        public void EnforceNativeSpectatedTextStyle()
        {
            if (!replayReady)
            {
                RestoreNativeStandingsTextStyles();
                return;
            }

            if (nativeStandingsEntriesEpochSeen != GhostLineEntry.entriesEpoch)
            {
                nativeStandingsEntriesEpochSeen = GhostLineEntry.entriesEpoch;
                nativeStandingsTextLastContent.Clear();
                nativeStandingsEntryCache.Clear();
            }

            if (Time.unscaledTime >= nextNativeStandingsResolve)
            {
                nextNativeStandingsResolve = Time.unscaledTime + 0.5f;
                UnityEngine.SceneManagement.Scene activeScene =
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                bool sceneChanged = !nativeStandingsSceneValid ||
                    activeScene.handle != nativeStandingsSceneHandle;
                bool hasDeadTexts = false;
                for (int i = 0; i < nativeStandingsTexts.Count; i++)
                {
                    if (nativeStandingsTexts[i] == null)
                    {
                        hasDeadTexts = true;
                        break;
                    }
                }
                if (sceneChanged || hasDeadTexts || nativeStandingsTexts.Count == 0)
                {
                    nativeStandingsSceneHandle = activeScene.handle;
                    nativeStandingsSceneValid = true;
                    nativeStandingsBaseStyles.Clear();
                    ResolveNativeStandingsTexts();
                }
            }

            foreach (Text text in nativeStandingsTexts)
            {
                if (text == null)
                    continue;

                string content = text.text;
                string lastContent;
                GhostLineEntry entry;
                if (!nativeStandingsTextLastContent.TryGetValue(text, out lastContent) || lastContent != content)
                {
                    entry = FindEntryForNativeStandingsText(content);
                    nativeStandingsTextLastContent[text] = content;
                    nativeStandingsEntryCache[text] = entry;
                }
                else
                {
                    nativeStandingsEntryCache.TryGetValue(text, out entry);
                }

                FontStyle baseStyle;
                if (!nativeStandingsBaseStyles.TryGetValue(text, out baseStyle))
                    baseStyle = NormalizeNativeStandingsStyle(text.fontStyle);
                FontStyle desiredStyle = entry != null && entry == spectatedEntry
                    ? MakeBoldNativeStandingsStyle(baseStyle)
                    : baseStyle;
                if (text.fontStyle != desiredStyle)
                {
                    text.fontStyle = desiredStyle;
                    text.SetVerticesDirty();
                }
            }
        }

        private void ResolveNativeStandingsTexts()
        {
            foreach (Text staleText in nativeStandingsBaseStyles.Keys)
            {
                if (staleText == null)
                    continue;
                nativeStandingsTextLastContent.Remove(staleText);
                nativeStandingsEntryCache.Remove(staleText);
            }
            nativeStandingsBaseStyles.Clear();
            Text[] allTexts = UnityEngine.Object.FindObjectsOfType<Text>(true);
            List<Text> resolved = new List<Text>();
            foreach (Text text in allTexts)
            {
                if (text == null || text.gameObject.name != "TextPrimaryInfo")
                    continue;

                bool inPlayerActivity = false;
                string path = text.gameObject.name;
                for (Transform current = text.transform.parent; current != null; current = current.parent)
                {
                    path = current.name + "/" + path;
                    if (current.name == "PlayerActivityBar" || current.name.StartsWith("PlayerActivity", System.StringComparison.Ordinal))
                    {
                        inPlayerActivity = true;
                        break;
                    }
                }
                if (!inPlayerActivity && path.IndexOf("PlayerActivityBar/", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                resolved.Add(text);
                if (!nativeStandingsBaseStyles.ContainsKey(text))
                    nativeStandingsBaseStyles[text] = NormalizeNativeStandingsStyle(text.fontStyle);
            }

            nativeStandingsTexts = resolved;
        }

        private GhostLineEntry FindEntryForNativeStandingsText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string displayedName = NormalizeNativeStandingsName(text);

            foreach (GhostLineEntry entry in GhostLinesPlugin.entries)
            {
                string pilotName = NormalizeNativeStandingsName(GhostLinesPlugin.CleanPilotName(entry.pilotName));
                if (!string.IsNullOrWhiteSpace(pilotName) &&
                    (string.Equals(displayedName, pilotName, System.StringComparison.OrdinalIgnoreCase) ||
                     displayedName.StartsWith(pilotName + " - ", System.StringComparison.OrdinalIgnoreCase)))
                    return entry;
            }
            return null;
        }

        private static string NormalizeNativeStandingsName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string result = System.Text.RegularExpressions.Regex.Replace(value, "<.*?>", "");
            result = result.Replace('\u00A0', ' ').Trim();
            int suffixIndex = result.IndexOf(" - ", System.StringComparison.Ordinal);
            if (suffixIndex >= 0)
                result = result.Substring(0, suffixIndex).Trim();
            return result;
        }

        private static FontStyle NormalizeNativeStandingsStyle(FontStyle style)
        {
            return (style & FontStyle.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;
        }

        private static FontStyle MakeBoldNativeStandingsStyle(FontStyle style)
        {
            return (style & FontStyle.Italic) != 0 ? FontStyle.BoldAndItalic : FontStyle.Bold;
        }

        private void RestoreNativeStandingsTextStyles()
        {
            foreach (KeyValuePair<Text, FontStyle> pair in nativeStandingsBaseStyles)
            {
                if (pair.Key != null)
                {
                    if (pair.Key.fontStyle != pair.Value)
                    {
                        pair.Key.fontStyle = pair.Value;
                        pair.Key.SetVerticesDirty();
                    }
                }
            }
            nativeStandingsTexts.Clear();
            nativeStandingsBaseStyles.Clear();
            nextNativeStandingsResolve = 0f;
        }

        private void RestoreNativeHudVisibility()
        {
            if (nativeHudModuleParentStateCaptured && nativeHudModuleParent != null)
                nativeHudModuleParent.enabled = nativeHudModuleParentOriginalEnabled;
            nativeHudModuleParentStateCaptured = false;
            nativeHudModuleParent = null;
            nativeDroneHud = null;
            nextNativeHudResolve = 0f;
        }

        private void RestoreReplayControlsUI()
        {
            controlsHidden = false;
            if (controlsCG != null && controlsStateCaptured)
            {
                controlsCG.alpha = originalControlsAlpha;
                controlsCG.interactable = originalControlsInteractable;
                controlsCG.blocksRaycasts = originalControlsBlocksRaycasts;
            }
            if (controlsCG != null && controlsCanvasGroupAdded)
            {
                Destroy(controlsCG);
                controlsCG = null;
            }

            controlsStateCaptured = false;
            controlsCanvasGroupAdded = false;
        }

        private void DeactivateReplay()
        {
            RestoreHoldZoom();
            RestoreNativeStandingsTextStyles();
            int cameraModeToRestore = replayEntryCameraMode != 0
                ? replayEntryCameraMode
                : pendingCameraModeRestore != 0 ? pendingCameraModeRestore : lastNativeCameraMode;
            if (cameraModeToRestore != 0)
                SetNativeCameraMode(cameraModeToRestore, false);
            RestoreReplayControlsUI();
            RestoreNativeHudVisibility();
            ClearActiveReplayEntries();
            recordingCandidates.Clear();
            replayReady = false;
            activeReplayRace = null;
            settingsOpen = false;
            playbackMouseCaptured = false;
            playbackPinned = false;
            settingsPinned = false;
            settingsFadeAmount = 0f;
            stickGraphs.Clear();
            xrayEnabled = false;
            xrayStructuresBuilt = false;
            xrayStructures.Clear();
            xrayVisibleStructures.Clear();
            xrayFadingVisible.Clear();
            xrayStructureCenterValid = false;
            xraySeedInstant = false;
            InvalidateXrayMapCache();
            xrayDroneFilters.Clear();
            xrayDroneRoot = null;
            overridePlayback = false;
            playbackSpeed = 1f;
            isPlaying = false;
            frameStepPaused = false;
            restartRequested = false;
            restartPlaybackPending = false;
            loopReplayEnabled = false;
            pendingCameraViewRestore = false;
            pendingFreeCamViewRestore = false;
            pendingOrbitViewRestore = false;
            pendingPlayToggle = false;
            fpvOnReplayPending = false;
            customCameraActive = false;
            startedFromFpv = false;
            pendingThirdPersonScroll = 0f;
            freeCamActive = false;
            isOrbiting = false;
            CancelCameraLook();
            lastNativeCameraMode = 0;
            pendingCameraModeRestore = 0;
            replayEntryCameraMode = 0;
            draggingStickCam = false;
        }

        private void UpdateReplayLifecycle()
        {
            object replayRace;
            if (!TryGetFullyLoadedReplay(out replayRace))
            {
                if (replayReady)
                    DeactivateReplay();
                return;
            }

            if (!replayReady || !object.ReferenceEquals(activeReplayRace, replayRace))
            {
                if (replayReady)
                    DeactivateReplay();
                activeReplayRace = replayRace;
                replayReady = true;
                settingsOpen = savedSettingsPanelOpen;
                settingsFadeAmount = settingsOpen ? 1f : 0f;
                replayEnterTime = Time.time;
                CaptureReplayEntryCameraMode();
                fpvOnReplayPending = true;
                fpvForceTimeout = Time.time + 3f;
                SetReplayControlsHidden(true);
                CollectReplayRecordingCandidates(replayRace, 0, new System.Collections.Generic.HashSet<object>());
                SyncActiveReplayEntries();
                Debug.Log("[GhostLines] Replay fully loaded; activating Ghost Lines");
            }

            if (Time.time >= nextReplaySync)
            {
                nextReplaySync = Time.time + 0.5f;
                SyncActiveReplayEntries();
            }
        }

        private void InitializeDefaultReferenceEntry()
        {
            if (referenceAutoInitialized) return;
            if (referenceEntry != null)
            {
                referenceAutoInitialized = true;
                return;
            }
            if (spectatedEntry == null) return;
            referenceEntry = spectatedEntry;
            referenceAutoInitialized = true;
            Debug.Log($"[GhostLines] Default delta comparison: {referenceEntry.pilotName}");
        }

        public void ResetState()
        {
            RestoreHoldZoom();
            RestoreNativeStandingsTextStyles();
            CleanupLeaderboardSearch();
            if (replayReady)
                DeactivateReplay();
            else
            {
                RestoreNativeHudVisibility();
                RestoreReplayControlsUI();
                ClearActiveReplayEntries();
            }
            replayReady = false;
            activeReplayRace = null;
            nextReplaySync = 0f;
            replayControlsObj = null;
            controlsCG = null;
            controlsHidden = false;
            settingsOpen = false;
            playbackMouseCaptured = false;
            playbackPinned = false;
            settingsPinned = false;
            settingsFadeAmount = 0f;
            stickGraphs.Clear();
            settingsDragging = false;
            settingsRect = new Rect(-1f, -1f, 304f, 360f);
            sliderComponent = null;
            sliderValueProp = null;
            replayController = null;
            togglePlayPauseMethod = null;
            attachToDroneMethod = null;
            attachToDroneParameterType = null;
            timeProp = null;
            overridePlayback = false;
            playbackSpeed = 1f;
            isPlaying = false;
            frameStepPaused = false;
            restartRequested = false;
            restartPlaybackPending = false;
            loopReplayEnabled = false;
            pendingCameraViewRestore = false;
            pendingFreeCamViewRestore = false;
            pendingOrbitViewRestore = false;
            pendingPlayToggle = false;
            flightCamCtrl = null;
            inThirdPersonProp = null;
            stuntCameraRefProp = null;
            trackingDroneDataProp = null;
            flightCamImpl = null;
            cameraModeSetMethod = null;
            cameraModeEnumType = null;
            lastNativeCameraMode = 0;
            pendingCameraModeRestore = 0;
            replayEntryCameraMode = 0;
            fpvOnReplayPending = false;
            fpvForceLogged = false;
            referenceAutoInitialized = false;
            xrayEnabled = false;
            xrayStructuresBuilt = false;
            xrayStructures.Clear();
            xrayVisibleStructures.Clear();
            xrayFadingVisible.Clear();
            xrayStructureCenterValid = false;
            xraySeedInstant = false;
            InvalidateXrayMapCache();
            xrayDroneFilters.Clear();
            xrayDroneRoot = null;
            orbitTarget = null;
            isOrbiting = false;
            CancelCameraLook();
            customCameraActive = false;
            startedFromFpv = false;
            pendingThirdPersonScroll = 0f;
            freeCamActive = false;
            cameraDumped = false;
            noTargetLogged = false;
            spectatedEntry = null;
            referenceEntry = null;
            ghostDroneMap.Clear();
            ghostSmoothedPos.Clear();
            ghostSmoothedRot.Clear();
            ghostCamMountMap.Clear();
            ghostDroneSearchTimer = 0f;
            ghostMatchAttempts = 0;
            cameraMountLoggedRoots.Clear();
            lateIsThirdPerson = true;
            ghostDronePosValid = false;
            ghostDronePos = Vector3.zero;
        }

        private void UpdateHoldZoom()
        {
            Camera camera = Camera.main;
            if (camera == null)
                return;

            bool zoomKeyDown = bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("holdzoom"));
            if (zoomKeyDown)
                holdZoomActive = !holdZoomActive;
            bool zoomHeld = holdZoomActive;
            if (!zoomHeld && !holdZoomFovCaptured)
                return;

            bool fpvView = !freeCamActive && GetCurrentNativeCameraMode() == CAMERA_MODE_FIRST_PERSON;
            if (fpvView)
            {
                if (holdZoomFovCaptured)
                {
                    camera.fieldOfView = holdZoomNormalFov;
                    holdZoomFovCaptured = false;
                }
                return;
            }

            if (zoomHeld && !holdZoomFovCaptured)
            {
                holdZoomNormalFov = camera.fieldOfView;
                holdZoomFovBias = 0f;
                holdZoomSmoothFov = camera.fieldOfView;
                holdZoomFovCaptured = true;
            }
            if (!holdZoomFovCaptured)
                return;

            float baseFov;
            if (zoomHeld && freeCamActive && spectatedEntry != null)
            {
                float droneDist = 0f;
                Vector3 dronePos;
                if (!ghostSmoothedPos.TryGetValue(spectatedEntry, out dronePos))
                    TryGetDronePosition(spectatedEntry, displayReplayTime + predictionLead, out dronePos);
                droneDist = Vector3.Distance(freeCamPosition, dronePos);
                float farFactor = 1f - Mathf.Exp(-droneDist / 8f);
                baseFov = Mathf.Lerp(holdZoomNormalFov, HOLD_ZOOM_MIN_FOV, farFactor);
            }
            else
            {
                baseFov = zoomHeld ? holdZoomNormalFov * HOLD_ZOOM_FOV_SCALE : holdZoomNormalFov;
            }

            if (zoomHeld)
            {
                float scroll = Input.mouseScrollDelta.y;
                if (scroll != 0f)
                {
                    float logRatio = Mathf.Log(HOLD_ZOOM_SCROLL_RATIO);
                    float biasFloor = Mathf.Log(HOLD_ZOOM_EXTRA_MIN_FOV / baseFov) / logRatio * HOLD_ZOOM_SCROLL_STEP;
                    float biasCeil = Mathf.Log(holdZoomNormalFov / baseFov) / logRatio * HOLD_ZOOM_SCROLL_STEP;
                    holdZoomFovBias = Mathf.Clamp(
                        holdZoomFovBias - scroll * HOLD_ZOOM_SCROLL_STEP,
                        Mathf.Min(biasFloor, biasCeil),
                        Mathf.Max(biasFloor, biasCeil));
                }
            }

            float scrollFactor = Mathf.Pow(HOLD_ZOOM_SCROLL_RATIO, holdZoomFovBias / HOLD_ZOOM_SCROLL_STEP);
            float targetFov;
            if (zoomHeld && freeCamActive && spectatedEntry != null)
            {
                targetFov = Mathf.Clamp(baseFov * scrollFactor, HOLD_ZOOM_EXTRA_MIN_FOV, holdZoomNormalFov);
            }
            else
            {
                targetFov = zoomHeld
                    ? Mathf.Clamp(baseFov * scrollFactor, HOLD_ZOOM_EXTRA_MIN_FOV, holdZoomNormalFov)
                    : holdZoomNormalFov;
            }
            float smoothK = 1f - Mathf.Exp(-Time.deltaTime / HOLD_ZOOM_SMOOTH_TAU);
            holdZoomSmoothFov = Mathf.Lerp(holdZoomSmoothFov, targetFov, smoothK);
            camera.fieldOfView = holdZoomSmoothFov;
            holdZoomCurrentFov = holdZoomSmoothFov;

            if (!zoomHeld && Mathf.Abs(camera.fieldOfView - holdZoomNormalFov) < 0.5f)
            {
                camera.fieldOfView = holdZoomNormalFov;
                holdZoomFovCaptured = false;
            }
        }

        private void UpdateFreeCamFocus(Vector3 pilotPos)
        {
            Vector3 toPilot = pilotPos - freeCamPosition;
            float dist = toPilot.magnitude;
            if (dist < 0.0001f)
                return;
            float targetYaw = Mathf.Atan2(toPilot.x, toPilot.z) * Mathf.Rad2Deg;
            float targetPitch = -Mathf.Asin(Mathf.Clamp(toPilot.y / dist, -1f, 1f)) * Mathf.Rad2Deg;
            if (Time.time < zoomTransitionUntil)
            {
                float remaining = Mathf.Clamp01((zoomTransitionUntil - Time.time) / ZOOM_TRANSITION_DURATION);
                float eased = 1f - remaining * remaining;
                freeCamYaw = Mathf.LerpAngle(zoomTransitionStartYaw, targetYaw, eased);
                freeCamPitch = Mathf.Lerp(zoomTransitionStartPitch, targetPitch, eased);
            }
            else
            {
                freeCamYaw = targetYaw;
                freeCamPitch = targetPitch;
            }
        }

        private void RestoreHoldZoom()
        {
            holdZoomActive = false;
            if (!holdZoomFovCaptured)
                return;

            if (Camera.main != null)
                Camera.main.fieldOfView = holdZoomNormalFov;
            holdZoomFovCaptured = false;
        }

        private void Start()
        {
            Debug.Log("[GhostLines] GhostLinePoller started");
            Camera.onPreCull += OnCameraPreRender;
            Camera.onPostRender += OnCameraPostRender;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreRender;
            Camera.onPostRender -= OnCameraPostRender;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            CleanupLeaderboardSearch();
            if (xrayWireMaterial != null) Destroy(xrayWireMaterial);
            xrayWireMaterial = null;
            if (xrayMapMesh != null) Destroy(xrayMapMesh);
            xrayMapMesh = null;
            DestroyTexture(ref whiteTex);
            DestroyTexture(ref stickRingTex);
            DestroyTexture(ref iconPlay);
            DestroyTexture(ref iconPause);
            DestroyTexture(ref iconRewind);
            DestroyTexture(ref iconFF);
            DestroyTexture(ref iconLoop);
            DestroyTexture(ref iconSettings);
            DestroyTexture(ref iconPin);
            DestroyTexture(ref iconBindings);
            DestroyTexture(ref targetRingTex);
            DestroyTexture(ref targetDotTex);
            DestroyTexture(ref targetFillTex);
            iconsInitialized = false;
            xrayStructures.Clear();
            xrayStructuresBuilt = false;
            xrayVisibleStructures.Clear();
            xrayFadingVisible.Clear();
            xrayStructureCenterValid = false;
            xraySeedInstant = false;
            InvalidateXrayMapCache();
            xrayDroneFilters.Clear();
            xrayDroneRoot = null;
            RestoreReplayControlsUI();
            RestoreNativeHudVisibility();
            RestoreNativeStandingsTextStyles();
            RestoreHoldZoom();
            CancelCameraLook();
            ClearActiveReplayEntries();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            SaveCurrentOverlayPositions();
            SaveDisplayPreferences();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                isOrbiting = false;
                CancelCameraLook();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                isOrbiting = false;
                CancelCameraLook();
            }
        }

        private void OnDisable()
        {
            isOrbiting = false;
            RestoreHoldZoom();
            CancelCameraLook();
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture != null)
                Destroy(texture);
            texture = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetState();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (replayReady)
                DeactivateReplay();
            Debug.Log("[GhostLines] Scene unloaded; keeping recording candidates");
        }

        public string ResolvePilotNameFromReplayState(object recording)
        {
            if (recording == null || replayController == null)
                return null;

            object replayRace = GetReplayRace();
            if (replayRace == null)
                return null;

            return FindRecordedPlayerName(replayRace, recording, 0, new System.Collections.Generic.HashSet<object>());
        }

        private string FindRecordedPlayerName(object obj, object recording, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || recording == null || depth > 4)
                return null;

            if (object.ReferenceEquals(obj, recording))
                return null;

            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return null;
            if (obj is UnityEngine.Object && depth > 0)
                return null;
            if (!visited.Add(obj))
                return null;

            var type = obj.GetType();
            System.Reflection.FieldInfo profileField = null;
            System.Reflection.FieldInfo recordingField = null;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType == typeof(GhostPlayerProfile))
                    profileField = field;
                else if (field.FieldType == typeof(LocalGhostStatesRecording))
                    recordingField = field;
            }

            if (profileField != null && recordingField != null)
            {
                try
                {
                    var linkedRecording = recordingField.GetValue(obj);
                    if (object.ReferenceEquals(linkedRecording, recording))
                    {
                        var profile = profileField.GetValue(obj) as GhostPlayerProfile;
                        if (profile != null)
                        {
                            string nickname = null;
                            try { nickname = profile.Nickname; } catch { }
                            if (!string.IsNullOrWhiteSpace(nickname))
                                return nickname;
                        }
                    }
                }
                catch { }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                string nested = FindRecordedPlayerName(child, recording, depth + 1, visited);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                foreach (var item in enumerable)
                {
                    if (walked++ >= 256)
                        break;
                    string nested = FindRecordedPlayerName(item, recording, depth + 1, visited);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }

            return null;
        }

        public Color? ResolvePilotColorFromReplayState(object recording)
        {
            if (recording == null || replayController == null)
                return null;

            object replayRace = GetReplayRace();
            if (replayRace == null)
                return null;

            return FindRecordedPlayerColor(replayRace, recording, 0, new System.Collections.Generic.HashSet<object>());
        }

        private Color? FindRecordedPlayerColor(object obj, object recording, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || recording == null || depth > 4)
                return null;

            if (object.ReferenceEquals(obj, recording))
                return null;

            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return null;
            if (obj is UnityEngine.Object && depth > 0)
                return null;
            if (!visited.Add(obj))
                return null;

            var type = obj.GetType();
            System.Reflection.FieldInfo profileField = null;
            System.Reflection.FieldInfo recordingField = null;
            System.Reflection.FieldInfo directColorField = null;

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType == typeof(GhostPlayerProfile))
                    profileField = field;
                else if (field.FieldType == typeof(LocalGhostStatesRecording))
                    recordingField = field;
                else if (field.FieldType == typeof(Color))
                    directColorField = field;
            }

            if (profileField != null && recordingField != null)
            {
                try
                {
                    var linkedRecording = recordingField.GetValue(obj);
                    if (object.ReferenceEquals(linkedRecording, recording))
                    {
                        if (directColorField != null)
                        {
                            try
                            {
                                Color c = (Color)directColorField.GetValue(obj);
                                if (c.a > 0.01f && (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f))
                                    return c;
                            }
                            catch { }
                        }
                        var profile = profileField.GetValue(obj) as GhostPlayerProfile;
                        if (profile != null)
                        {
                            try { return profile.Color; } catch { }
                        }
                    }
                }
                catch { }
            }

            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                Color? nested = FindRecordedPlayerColor(child, recording, depth + 1, visited);
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
                    Color? nested = FindRecordedPlayerColor(item, recording, depth + 1, visited);
                    if (nested.HasValue)
                        return nested;
                }
            }

            return null;
        }

        public bool TryAttachLeaderboardSearch(Scene scene)
        {
            if (leaderboardSearchInput != null)
                return true;
            if (!scene.IsValid() || !scene.isLoaded)
                return false;

            const string targetPath =
                "LeaderboardSelection/panelFilters/Content/FilterPanel/panelDropdownFilters/ContentFilter/dropdownTrack";
            Transform target = null;
            GameObject[] roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null)
                    continue;
                Transform canvas = root.name == "Canvas" ? root.transform : root.transform.Find("Canvas");
                if (canvas == null)
                    continue;
                target = canvas.Find(targetPath);
                if (target != null)
                    break;
            }

            if (target == null)
                return false;

            Dropdown dropdown = target.GetComponent<Dropdown>();
            if (dropdown == null)
                dropdown = target.GetComponentInChildren<Dropdown>(true);
            if (dropdown == null)
                return false;

            leaderboardController = dropdown.GetComponentInParent<MonoBehaviour>();
            leaderboardContentDropdown = dropdown;
            SetLeaderboardEnvironmentDropdown(GetLeaderboardEnvironmentDropdown(leaderboardController));
            leaderboardNotFoundLogged = false;
            Debug.Log("[GhostLines] Direct leaderboard dropdown found: " + GetTransformPath(dropdown.transform));
            CreateLeaderboardSearchRow();
            return leaderboardSearchInput != null;
        }

        public bool AttachLeaderboardSearchFromController(Component controller)
        {
            if (controller == null)
                return false;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            System.Reflection.FieldInfo field = controller.GetType().GetField("dropdownContentFilter", flags);
            Dropdown dropdown = field != null ? field.GetValue(controller) as Dropdown : null;
            if (dropdown == null)
            {
                if (!leaderboardNotFoundLogged)
                {
                    leaderboardNotFoundLogged = true;
                    Debug.Log("[GhostLines] Leaderboard controller has no content dropdown yet");
                }
                return false;
            }

            if (leaderboardSearchReady && leaderboardContentDropdown != null && leaderboardContentDropdown != dropdown)
                CleanupLeaderboardSearch();

            leaderboardController = controller;
            leaderboardContentDropdown = dropdown;
            SetLeaderboardEnvironmentDropdown(GetLeaderboardEnvironmentDropdown(controller));
            leaderboardNotFoundLogged = false;
            if (leaderboardSearchReady && leaderboardSearchInput != null)
                return true;
            Debug.Log("[GhostLines] Attaching from controller dropdown: " + GetTransformPath(dropdown.transform));
            CreateLeaderboardSearchRow();
            return leaderboardSearchInput != null;
        }

        private Dropdown GetLeaderboardEnvironmentDropdown(Component controller)
        {
            if (controller == null)
                return null;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            System.Reflection.FieldInfo field = controller.GetType().GetField("dropdownEnvironmentFilter", flags);
            return field != null ? field.GetValue(controller) as Dropdown : null;
        }

        private void SetLeaderboardEnvironmentDropdown(Dropdown dropdown)
        {
            if (leaderboardEnvironmentDropdown == dropdown)
            {
                if (dropdown != null && leaderboardEnvironmentValue != dropdown.value)
                    OnLeaderboardEnvironmentChanged(dropdown.value);
                return;
            }

            if (leaderboardEnvironmentDropdown != null)
                leaderboardEnvironmentDropdown.onValueChanged.RemoveListener(OnLeaderboardEnvironmentChanged);

            leaderboardEnvironmentDropdown = dropdown;
            leaderboardEnvironmentValue = dropdown != null ? dropdown.value : -1;
            if (dropdown != null)
                dropdown.onValueChanged.AddListener(OnLeaderboardEnvironmentChanged);
        }

        private void OnLeaderboardEnvironmentChanged(int value)
        {
            leaderboardEnvironmentValue = value;
            leaderboardContentRefreshPending = true;
            leaderboardPendingContentSignature = "";
            leaderboardPendingContentStableFrames = 0;
            leaderboardContentOptions.Clear();
            leaderboardDisplayedSignature = "";
            leaderboardSearchFocusLock = false;
            leaderboardSearchSelectedText = "";
            HideLeaderboardSearchPopup();

            if (leaderboardSearchInput != null)
            {
                leaderboardSearchUpdating = true;
                leaderboardSearchInput.text = "";
                leaderboardSearchUpdating = false;
            }

            leaderboardSearchTimer = 0f;
            Debug.Log("[GhostLines] Leaderboard environment changed; refreshing content tracks");
        }

        public void CleanupLeaderboardSearchFromPlugin()
        {
            CleanupLeaderboardSearch();
        }

        public void TickLeaderboardSearch()
        {
            if (!IsLeaderboardSceneLoaded())
            {
                if (leaderboardContentDropdown != null || leaderboardSearchInput != null || leaderboardController != null)
                    CleanupLeaderboardSearch();
                return;
            }

            if (leaderboardContentDropdown == null &&
                (leaderboardSearchInput != null || leaderboardController != null))
            {
                CleanupLeaderboardSearch();
            }

            if (!leaderboardContentRefreshPending && Time.unscaledTime < leaderboardSearchTimer)
                return;
            leaderboardSearchTimer = Time.unscaledTime + (leaderboardContentRefreshPending ? 0.05f : 1f);

            if (leaderboardContentDropdown == null || leaderboardEnvironmentDropdown == null)
                FindLeaderboardContentDropdown();
            if (leaderboardContentDropdown == null)
                return;

            if (leaderboardEnvironmentDropdown != null &&
                leaderboardEnvironmentValue != leaderboardEnvironmentDropdown.value)
            {
                OnLeaderboardEnvironmentChanged(leaderboardEnvironmentDropdown.value);
            }

            if (leaderboardSearchInput == null)
                CreateLeaderboardSearchRow();
            if (leaderboardSearchInput == null)
                return;

            string signature = GetDropdownSignature(leaderboardContentDropdown.options);
            if (leaderboardContentRefreshPending)
            {
                if (signature == leaderboardPendingContentSignature)
                    leaderboardPendingContentStableFrames++;
                else
                {
                    leaderboardPendingContentSignature = signature;
                    leaderboardPendingContentStableFrames = 0;
                }

                if (leaderboardPendingContentStableFrames < 2)
                    return;

                leaderboardContentOptions = new List<Dropdown.OptionData>(leaderboardContentDropdown.options);
                leaderboardContentRefreshPending = false;
                ApplyLeaderboardSearch();
            }
            else if (signature != leaderboardDisplayedSignature && !leaderboardSearchUpdating)
            {
                leaderboardContentOptions = new List<Dropdown.OptionData>(leaderboardContentDropdown.options);
                ApplyLeaderboardSearch();
            }
        }

        private static bool IsLeaderboardSceneLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name.IndexOf("Leaderboard", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private void FindLeaderboardContentDropdown()
        {
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>(true);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            int contentFilterFields = 0;
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;
                System.Type type = behaviour.GetType();
                var contentField = type.GetField("dropdownContentFilter", flags);
                if (contentField == null)
                    continue;
                contentFilterFields++;

                if (type.GetField("dropdownLeaderboardType", flags) == null ||
                    type.GetField("dropdownEnvironmentFilter", flags) == null ||
                    type.GetField("dropdownLeaderboardArchive", flags) == null)
                    continue;

                try
                {
                    Dropdown dropdown = contentField.GetValue(behaviour) as Dropdown;
                    if (dropdown != null)
                    {
                        leaderboardController = behaviour;
                        leaderboardContentDropdown = dropdown;
                        SetLeaderboardEnvironmentDropdown(GetLeaderboardEnvironmentDropdown(behaviour));
                        Debug.Log("[GhostLines] Found leaderboard controller: " + type.FullName +
                            "; dropdown=" + GetTransformPath(dropdown.transform));
                        return;
                    }
                }
                catch { }
            }
            if (!leaderboardNotFoundLogged)
            {
                leaderboardNotFoundLogged = true;
                Debug.Log("[GhostLines] Leaderboard controller NOT found: behaviours=" + behaviours.Length +
                    ", content-filter fields=" + contentFilterFields);
            }
        }

        private string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            System.Text.StringBuilder path = new System.Text.StringBuilder(transform.name);
            Transform current = transform.parent;
            while (current != null)
            {
                path.Insert(0, current.name + "/");
                current = current.parent;
            }
            return path.ToString();
        }

        private void CreateLeaderboardSearchRow()
        {
            if (leaderboardContentDropdown == null || leaderboardSearchInputObject != null || leaderboardSearchReady)
                return;

            GameObject dropdownObject = leaderboardContentDropdown.gameObject;
            Graphic sourceGraphic = leaderboardContentDropdown.targetGraphic;
            leaderboardOriginalDropdownEnabled = leaderboardContentDropdown.enabled;
            leaderboardOriginalDropdownValue = leaderboardContentDropdown.value;
            Text inputText = leaderboardContentDropdown.captionText;
            if (inputText == null)
                inputText = dropdownObject.GetComponentInChildren<Text>(true);
            if (inputText == null)
            {
                Debug.Log("[GhostLines] Cannot create search input: content dropdown has no caption Text");
                CleanupLeaderboardSearch();
                return;
            }

            leaderboardOriginalContentText = inputText;
            leaderboardOriginalContentTextActive = inputText.gameObject.activeSelf;
            inputText.gameObject.SetActive(false);

            GameObject inputObject = new GameObject(
                "GhostLines_LeaderboardContentSearchInput",
                typeof(RectTransform),
                typeof(Image),
                typeof(InputField),
                typeof(LeaderboardSearchInputRelay));
            inputObject.transform.SetParent(dropdownObject.transform, false);
            inputObject.transform.SetAsLastSibling();
            RectTransform inputRect = inputObject.GetComponent<RectTransform>();
            inputRect.anchorMin = Vector2.zero;
            inputRect.anchorMax = Vector2.one;
            inputRect.offsetMin = new Vector2(10f, 0f);
            inputRect.offsetMax = new Vector2(-34f, 0f);
            inputRect.pivot = new Vector2(0.5f, 0.5f);

            Image inputBackground = inputObject.GetComponent<Image>();
            inputBackground.color = new Color(1f, 1f, 1f, 0.001f);
            inputBackground.raycastTarget = true;

            Text searchText = CreateSearchText(inputText, inputObject.transform, "SearchText");
            searchText.text = "";
            searchText.raycastTarget = false;

            Text placeholder = CreateSearchText(inputText, inputObject.transform, "SearchPlaceholder");
            placeholder.text = "Search content...";
            placeholder.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            placeholder.raycastTarget = false;

            InputField input = inputObject.GetComponent<InputField>();
            input.targetGraphic = inputBackground;
            input.textComponent = searchText;
            input.placeholder = placeholder;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 128;
            input.interactable = true;
            input.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            input.text = GetCurrentLeaderboardContentText();
            input.onValueChanged.AddListener(OnLeaderboardSearchChanged);
            leaderboardContentDropdown.onValueChanged.AddListener(OnLeaderboardContentSelected);
            leaderboardSearchInput = input;
            leaderboardSearchInputObject = inputObject;
            LeaderboardSearchInputRelay relay = inputObject.GetComponent<LeaderboardSearchInputRelay>();
            relay.owner = this;

            leaderboardContentOptions = new List<Dropdown.OptionData>(leaderboardContentDropdown.options);
            leaderboardDisplayedSignature = GetDropdownSignature(leaderboardContentDropdown.options);
            leaderboardSearchSelectedText = GetCurrentLeaderboardContentText();
            leaderboardSearchReady = true;
            RectTransform dropdownRect = dropdownObject.GetComponent<RectTransform>();
            Debug.Log("[GhostLines] Added autocomplete input over original content dropdown: dropdown=" +
                (dropdownRect != null ? dropdownRect.rect.size.ToString() : "<no-rect>") +
                "; input=" + inputRect.rect.size.ToString());
        }

        private Text CreateSearchText(Text source, Transform parent, string name)
        {
            GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(14f, 0f);
            rect.offsetMax = new Vector2(-14f, 0f);
            Text text = textObject.GetComponent<Text>();
            text.font = source.font;
            text.fontSize = source.fontSize;
            text.fontStyle = source.fontStyle;
            text.alignment = source.alignment;
            text.horizontalOverflow = source.horizontalOverflow;
            text.verticalOverflow = source.verticalOverflow;
            text.lineSpacing = source.lineSpacing;
            text.supportRichText = source.supportRichText;
            text.color = source.color;
            text.raycastTarget = false;
            return text;
        }

        private void OnLeaderboardSearchChanged(string value)
        {
            if (leaderboardSearchUpdating)
                return;
            if (!leaderboardSearchUpdating)
                leaderboardSearchFocusLock = true;
            bool keepInputFocus = leaderboardSearchInput != null &&
                (leaderboardSearchInput.isFocused ||
                 (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == leaderboardSearchInput.gameObject));
            ApplyLeaderboardSearch();
            OpenLeaderboardSearchDropdown(keepInputFocus);
        }

        private string GetCurrentLeaderboardContentText()
        {
            if (leaderboardContentDropdown == null || leaderboardContentDropdown.options.Count == 0)
                return "";
            int index = Mathf.Clamp(leaderboardContentDropdown.value, 0, leaderboardContentDropdown.options.Count - 1);
            Dropdown.OptionData option = leaderboardContentDropdown.options[index];
            return option != null ? option.text : "";
        }

        private void BeginLeaderboardSearchInput()
        {
            if (leaderboardSearchInput == null || leaderboardSearchStarting)
                return;

            leaderboardSearchFocusLock = true;
            leaderboardSearchStarting = true;
            try
            {
                if (string.Equals(leaderboardSearchInput.text, leaderboardSearchSelectedText, System.StringComparison.Ordinal))
                {
                    leaderboardSearchUpdating = true;
                    leaderboardSearchInput.text = "";
                    leaderboardSearchUpdating = false;
                    ApplyLeaderboardSearch();
                }

                leaderboardSearchInput.ActivateInputField();
                OpenLeaderboardSearchDropdown(true);
            }
            finally
            {
                leaderboardSearchStarting = false;
            }
        }

        private void OpenLeaderboardSearchDropdown(bool keepInputFocus)
        {
            if (leaderboardContentDropdown == null || !leaderboardContentDropdown.IsActive() ||
                !leaderboardContentDropdown.IsInteractable())
                return;

            try
            {
                GameObject popup;
                System.Collections.IList items;
                if (!TryGetLeaderboardPopup(out popup, out items))
                {
                    leaderboardPopupLayoutCached = false;
                    leaderboardContentDropdown.Show();
                }

                if (keepInputFocus)
                    leaderboardSearchRefocusPending = true;

                ApplyLeaderboardSearch();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[GhostLines] Could not open filtered content dropdown: " + ex.Message);
            }
        }

        private void HideLeaderboardSearchPopup()
        {
            if (leaderboardContentDropdown != null)
            {
                try { leaderboardContentDropdown.Hide(); } catch { }
            }

            leaderboardPopupLayoutCached = false;
            leaderboardPopupFirstItemX = 0f;
            leaderboardPopupLastItemY = 0f;
            leaderboardPopupRowHeight = 0f;
            leaderboardPopupBaseContentHeight = 0f;
            leaderboardSearchRefocusPending = false;
            if (leaderboardSearchRefocusCoroutine != null)
            {
                StopCoroutine(leaderboardSearchRefocusCoroutine);
                leaderboardSearchRefocusCoroutine = null;
            }
        }

        private void OnLeaderboardContentSelected(int value)
        {
            if (leaderboardContentDropdown == null || leaderboardSearchInput == null || leaderboardSearchUpdating)
                return;
            if (value < 0 || value >= leaderboardContentDropdown.options.Count)
                return;

            leaderboardSearchFocusLock = false;

            Dropdown.OptionData selectedOption = leaderboardContentDropdown.options[value];
            leaderboardSearchSelectedText = selectedOption != null ? selectedOption.text : "";

            leaderboardSearchUpdating = true;
            try
            {
                leaderboardSearchInput.text = leaderboardSearchSelectedText;
                leaderboardOriginalDropdownValue = value;
                leaderboardDisplayedSignature = GetDropdownSignature(leaderboardContentDropdown.options);
                leaderboardPopupLayoutCached = false;
            }
            finally
            {
                leaderboardSearchUpdating = false;
            }
        }

        private void ApplyLeaderboardSearch()
        {
            if (leaderboardContentDropdown == null || leaderboardSearchInput == null || leaderboardSearchUpdating)
                return;

            leaderboardSearchUpdating = true;
            try
            {
                string query = (leaderboardSearchInput.text ?? "").Trim();
                List<Dropdown.OptionData> filtered = new List<Dropdown.OptionData>();
                foreach (var option in leaderboardContentOptions)
                {
                    int rank = GetSearchRank(option != null ? option.text : "", query);
                    if (rank >= 0)
                        filtered.Add(option);
                }

                for (int i = 1; i < filtered.Count; i++)
                {
                    Dropdown.OptionData current = filtered[i];
                    int currentRank = GetSearchRank(current != null ? current.text : "", query);
                    int j = i - 1;
                    while (j >= 0 && GetSearchRank(filtered[j] != null ? filtered[j].text : "", query) > currentRank)
                    {
                        filtered[j + 1] = filtered[j];
                        j--;
                    }
                    filtered[j + 1] = current;
                }

                string fullSignature = GetDropdownSignature(leaderboardContentOptions);
                leaderboardDisplayedSignature = fullSignature;
                RefreshLeaderboardPopup(filtered);
            }
            finally
            {
                leaderboardSearchUpdating = false;
            }
        }

        private bool TryGetLeaderboardPopup(out GameObject popup, out System.Collections.IList items)
        {
            popup = null;
            items = null;
            if (leaderboardContentDropdown == null)
                return false;

            if (leaderboardDropdownPopupField == null || leaderboardDropdownItemsField == null)
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                System.Type dropdownType = typeof(Dropdown);
                leaderboardDropdownPopupField = dropdownType.GetField("m_Dropdown", flags);
                leaderboardDropdownItemsField = dropdownType.GetField("m_Items", flags);

                System.Type itemType = dropdownType.GetNestedType("DropdownItem",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (itemType != null)
                {
                    leaderboardPopupItemRectProperty = itemType.GetProperty("rectTransform", flags | System.Reflection.BindingFlags.Public);
                    leaderboardPopupItemTextProperty = itemType.GetProperty("text", flags | System.Reflection.BindingFlags.Public);
                }
            }

            if (leaderboardDropdownPopupField == null || leaderboardDropdownItemsField == null)
                return false;

            try
            {
                popup = leaderboardDropdownPopupField.GetValue(leaderboardContentDropdown) as GameObject;
                items = leaderboardDropdownItemsField.GetValue(leaderboardContentDropdown) as System.Collections.IList;
                return popup != null && items != null && items.Count > 0;
            }
            catch
            {
                popup = null;
                items = null;
                return false;
            }
        }

        private void RefreshLeaderboardPopup(List<Dropdown.OptionData> filtered)
        {
            GameObject popup;
            System.Collections.IList items;
            if (!TryGetLeaderboardPopup(out popup, out items) || leaderboardPopupItemRectProperty == null)
                return;

            if (items.Count != leaderboardContentOptions.Count)
            {
                HideLeaderboardSearchPopup();
                return;
            }

            List<int> visibleIndices = new List<int>(filtered.Count);
            foreach (Dropdown.OptionData option in filtered)
            {
                int originalIndex = leaderboardContentOptions.IndexOf(option);
                if (originalIndex >= 0 && !visibleIndices.Contains(originalIndex))
                    visibleIndices.Add(originalIndex);
            }

            RectTransform popupRect = popup.GetComponent<RectTransform>();
            RectTransform firstItemRect = GetLeaderboardPopupItemRect(items[0]);
            RectTransform contentRect = firstItemRect != null ? firstItemRect.parent as RectTransform : null;
            if (popupRect == null || firstItemRect == null || contentRect == null)
                return;

            RectTransform lastItemRect = GetLeaderboardPopupItemRect(items[items.Count - 1]);
            if (lastItemRect == null)
                return;

            if (!leaderboardPopupLayoutCached)
            {
                leaderboardPopupLayoutCached = true;
                leaderboardPopupFirstItemX = firstItemRect.anchoredPosition.x;
                leaderboardPopupLastItemY = lastItemRect.anchoredPosition.y;
                leaderboardPopupRowHeight = Mathf.Max(firstItemRect.rect.height, 1f);
                leaderboardPopupBaseContentHeight = contentRect.rect.height - leaderboardPopupRowHeight * items.Count;
            }

            for (int i = 0; i < items.Count; i++)
            {
                RectTransform itemRect = GetLeaderboardPopupItemRect(items[i]);
                if (itemRect == null)
                    continue;

                bool visible = visibleIndices.Contains(i);
                itemRect.gameObject.SetActive(visible);
                if (!visible)
                    continue;

                int visiblePosition = visibleIndices.IndexOf(i);
                itemRect.anchoredPosition = new Vector2(
                    leaderboardPopupFirstItemX,
                    leaderboardPopupLastItemY + leaderboardPopupRowHeight * (visibleIndices.Count - 1 - visiblePosition));

                if (leaderboardPopupItemTextProperty != null)
                {
                    Text itemText = leaderboardPopupItemTextProperty.GetValue(items[i], null) as Text;
                    if (itemText != null)
                        itemText.text = leaderboardContentOptions[i] != null ? leaderboardContentOptions[i].text : "";
                }
            }

            float contentHeight = Mathf.Max(
                0f,
                Mathf.Max(0f, leaderboardPopupBaseContentHeight) + leaderboardPopupRowHeight * visibleIndices.Count);
            contentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);

            ScrollRect scrollRect = popup.GetComponentInChildren<ScrollRect>(true);
            if (scrollRect != null && scrollRect.content != null && scrollRect.content != contentRect)
                scrollRect.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 1f;
        }

        private RectTransform GetLeaderboardPopupItemRect(object item)
        {
            if (item == null || leaderboardPopupItemRectProperty == null)
                return null;
            try
            {
                return leaderboardPopupItemRectProperty.GetValue(item, null) as RectTransform;
            }
            catch
            {
                return null;
            }
        }

        private int GetSearchRank(string text, string query)
        {
            if (string.IsNullOrEmpty(query))
                return 0;

            text = text ?? "";
            if (string.Equals(text, query, System.StringComparison.OrdinalIgnoreCase))
                return 0;
            if (text.StartsWith(query, System.StringComparison.OrdinalIgnoreCase))
                return 1;

            string[] words = text.Split(new[] { ' ', '-', '_', '/', '\\', '.', '(', ')' },
                System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string word in words)
            {
                if (word.StartsWith(query, System.StringComparison.OrdinalIgnoreCase))
                    return 2;
            }

            return text.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : -1;
        }

        private string GetDropdownSignature(List<Dropdown.OptionData> options)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            foreach (var option in options)
                signature.Append(option != null ? option.text : "").Append('\u001f');
            return signature.ToString();
        }

        private void CleanupLeaderboardSearch()
        {
            if (leaderboardSearchInput != null)
                leaderboardSearchInput.onValueChanged.RemoveListener(OnLeaderboardSearchChanged);
            if (leaderboardEnvironmentDropdown != null)
                leaderboardEnvironmentDropdown.onValueChanged.RemoveListener(OnLeaderboardEnvironmentChanged);
            if (leaderboardContentDropdown != null)
            {
                leaderboardContentDropdown.onValueChanged.RemoveListener(OnLeaderboardContentSelected);
                HideLeaderboardSearchPopup();
                leaderboardContentDropdown.enabled = leaderboardOriginalDropdownEnabled;
            }
            if (leaderboardOriginalContentText != null)
                leaderboardOriginalContentText.gameObject.SetActive(leaderboardOriginalContentTextActive);
            if (leaderboardSearchInputObject != null)
                Destroy(leaderboardSearchInputObject);
            leaderboardSearchInputObject = null;
            leaderboardSearchInput = null;
            leaderboardContentDropdown = null;
            leaderboardEnvironmentDropdown = null;
            leaderboardEnvironmentValue = -1;
            leaderboardContentRefreshPending = false;
            leaderboardPendingContentSignature = "";
            leaderboardPendingContentStableFrames = 0;
            leaderboardController = null;
            leaderboardContentOptions.Clear();
            leaderboardDisplayedSignature = "";
            leaderboardSearchSelectedText = "";
            leaderboardOriginalContentText = null;
            leaderboardOriginalContentTextActive = false;
            leaderboardOriginalDropdownEnabled = false;
            leaderboardOriginalDropdownValue = 0;
            leaderboardSearchStarting = false;
            leaderboardSearchRefocusPending = false;
            leaderboardSearchFocusLock = false;
            leaderboardPopupLayoutCached = false;
            leaderboardPopupFirstItemX = 0f;
            leaderboardPopupLastItemY = 0f;
            leaderboardPopupRowHeight = 0f;
            leaderboardPopupBaseContentHeight = 0f;
            if (leaderboardSearchRefocusCoroutine != null)
            {
                StopCoroutine(leaderboardSearchRefocusCoroutine);
                leaderboardSearchRefocusCoroutine = null;
            }
            EndLeaderboardDragSelection();
            leaderboardSelectedTogglesQueue.Clear();
            leaderboardCachedScrollViewContent = null;
            leaderboardCachedScrollRect = null;
            leaderboardScrollViewFallbackExhausted = false;
            leaderboardSearchUpdating = false;
            leaderboardSearchTimer = 0f;
            leaderboardNotFoundLogged = false;
            leaderboardSearchReady = false;
        }

        private struct LeaderboardRowItem
        {
            public int index;
            public Toggle toggle;
            public RectTransform rowRect;
            public RectTransform toggleRect;
        }

        private Transform FindLeaderboardScrollViewContent()
        {
            if (leaderboardCachedScrollViewContent != null)
            {
                if (leaderboardCachedScrollViewContent.gameObject.activeInHierarchy)
                    return leaderboardCachedScrollViewContent;
                return null;
            }

            if (Time.unscaledTime < leaderboardScrollViewContentSearchTimer)
                return null;
            leaderboardScrollViewContentSearchTimer = Time.unscaledTime + 0.5f;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                GameObject[] roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    Transform target = root.transform.Find("LeaderboardSelection/panelLeaderboard/Content/Scroll View/Viewport/scrollViewContent");
                    if (target == null && root.name == "Canvas")
                    {
                        target = root.transform.Find("LeaderboardSelection/panelLeaderboard/Content/Scroll View/Viewport/scrollViewContent");
                    }
                    if (target != null)
                    {
                        leaderboardCachedScrollViewContent = target;
                        leaderboardCachedScrollRect = target.GetComponentInParent<ScrollRect>();
                        if (target.gameObject.activeInHierarchy)
                            return target;
                        return null;
                    }
                }
            }

            if (!leaderboardScrollViewFallbackExhausted)
            {
                leaderboardScrollViewFallbackExhausted = true;
                GameObject[] allObjects = FindObjectsOfType<GameObject>();
                foreach (var go in allObjects)
                {
                    if (go.name == "scrollViewContent" && go.activeInHierarchy)
                    {
                        if (go.transform.parent != null && go.transform.parent.name == "Viewport")
                        {
                            leaderboardCachedScrollViewContent = go.transform;
                            leaderboardCachedScrollRect = go.GetComponentInParent<ScrollRect>();
                            return go.transform;
                        }
                    }
                }
            }

            return null;
        }

        private void TickLeaderboardDragSelection()
        {
            if (!IsLeaderboardSceneLoaded())
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
                return;
            }
            Transform content = FindLeaderboardScrollViewContent();
            if (content == null || !content.gameObject.activeInHierarchy)
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
                return;
            }

            List<LeaderboardRowItem> rows = new List<LeaderboardRowItem>();
            int childCount = content.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = content.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy) continue;

                Toggle toggle = child.GetComponentInChildren<Toggle>(true);
                if (toggle == null || !toggle.gameObject.activeInHierarchy) continue;

                RectTransform rowRect = child as RectTransform;
                RectTransform toggleRect = toggle.GetComponent<RectTransform>();

                rows.Add(new LeaderboardRowItem
                {
                    index = rows.Count,
                    toggle = toggle,
                    rowRect = rowRect,
                    toggleRect = toggleRect
                });
            }

            if (rows.Count == 0)
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
                return;
            }

            for (int i = leaderboardSelectedTogglesQueue.Count - 1; i >= 0; i--)
            {
                Toggle t = leaderboardSelectedTogglesQueue[i];
                if (t == null || !t.gameObject.activeInHierarchy)
                {
                    leaderboardSelectedTogglesQueue.RemoveAt(i);
                }
            }

            foreach (var r in rows)
            {
                if (r.toggle.isOn)
                {
                    if (!leaderboardSelectedTogglesQueue.Contains(r.toggle))
                    {
                        leaderboardSelectedTogglesQueue.Add(r.toggle);
                        if (leaderboardSelectedTogglesQueue.Count > 5)
                        {
                            Toggle oldest = leaderboardSelectedTogglesQueue[0];
                            leaderboardSelectedTogglesQueue.RemoveAt(0);
                            if (oldest != null && oldest.isOn)
                            {
                                oldest.isOn = false;
                            }
                        }
                    }
                }
                else
                {
                    if (leaderboardSelectedTogglesQueue.Contains(r.toggle))
                    {
                        leaderboardSelectedTogglesQueue.Remove(r.toggle);
                    }
                }
            }

            Vector2 mousePos = Input.mousePosition;

            bool IsMouseOverRowToggleRegion(LeaderboardRowItem row)
            {
                if (row.toggleRect == null) return false;
                Canvas canvas = row.toggleRect.GetComponentInParent<Canvas>();
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;

                if (RectTransformUtility.RectangleContainsScreenPoint(row.toggleRect, mousePos, cam))
                    return true;

                if (row.rowRect != null)
                {
                    Vector3[] rowCorners = new Vector3[4];
                    row.rowRect.GetWorldCorners(rowCorners);

                    float yMin = Mathf.Min(rowCorners[0].y, rowCorners[2].y);
                    float yMax = Mathf.Max(rowCorners[0].y, rowCorners[2].y);

                    Vector3[] toggleCorners = new Vector3[4];
                    row.toggleRect.GetWorldCorners(toggleCorners);
                    float xToggleCenter = (toggleCorners[0].x + toggleCorners[2].x) * 0.5f;

                    float xMin = xToggleCenter - 50f;
                    float xMax = xToggleCenter + 50f;

                    if (mousePos.x >= xMin && mousePos.x <= xMax && mousePos.y >= yMin && mousePos.y <= yMax)
                        return true;
                }

                return false;
            }

            int GetRowIndexUnderMouse()
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (IsMouseOverRowToggleRegion(rows[i]))
                        return i;
                }
                return -1;
            }

            if (Input.GetMouseButtonDown(0))
            {
                int hitIndex = GetRowIndexUnderMouse();
                if (hitIndex >= 0)
                {
                    isLeaderboardDragSelecting = true;
                    leaderboardDragProcessedToggles.Clear();
                    leaderboardDragStartRowIndex = hitIndex;
                    leaderboardDragLastRowIndex = hitIndex;
                    leaderboardDragStartWasSelected = rows[hitIndex].toggle.isOn;

                    // Drop the EventSystem selection right away so Unity's focus
                    // ring doesn't stick to the started control while dragging.
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                    {
                        GameObject selected = EventSystem.current.currentSelectedGameObject;
                        if (selected.GetComponentInChildren<Toggle>(true) != null ||
                            selected.GetComponent<Toggle>() != null)
                        {
                            EventSystem.current.SetSelectedGameObject(null);
                        }
                    }

                    if (leaderboardCachedScrollRect != null)
                    {
                        leaderboardScrollRectWasEnabled = leaderboardCachedScrollRect.enabled;
                        leaderboardCachedScrollRect.enabled = false;
                    }

                    // Do NOT pre-mark the start row as processed and do NOT set
                    // isOn here. On a plain click the native Unity Toggle toggles
                    // it once; on a drag the range loop below picks it up.
                }
            }

            if (isLeaderboardDragSelecting && Input.GetMouseButton(0))
            {
                int currentIndex = GetRowIndexUnderMouse();
                if (currentIndex < 0)
                {
                    float bestDist = float.MaxValue;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].rowRect != null)
                        {
                            Vector3[] corners = new Vector3[4];
                            rows[i].rowRect.GetWorldCorners(corners);
                            float yMin = Mathf.Min(corners[0].y, corners[2].y);
                            float yMax = Mathf.Max(corners[0].y, corners[2].y);
                            if (mousePos.y >= yMin && mousePos.y <= yMax)
                            {
                                currentIndex = i;
                                break;
                            }
                            float dist = Mathf.Min(Mathf.Abs(mousePos.y - yMin), Mathf.Abs(mousePos.y - yMax));
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                currentIndex = i;
                            }
                        }
                    }
                }

                // Only drag-select when the pointer actually reached a different
                // row. A plain single click (no movement) is handled by the native
                // toggle, so we must not toggling it here (that would double-toggle).
                if (currentIndex != leaderboardDragStartRowIndex)
                {
                    if (currentIndex >= 0 && leaderboardDragLastRowIndex >= 0)
                    {
                        int minR = Mathf.Min(leaderboardDragLastRowIndex, currentIndex);
                        int maxR = Mathf.Max(leaderboardDragLastRowIndex, currentIndex);

                        if (currentIndex >= leaderboardDragLastRowIndex)
                        {
                            for (int r = minR; r <= maxR; r++)
                            {
                                ApplyDragRowToggle(rows[r]);
                            }
                        }
                        else
                        {
                            for (int r = maxR; r >= minR; r--)
                            {
                                ApplyDragRowToggle(rows[r]);
                            }
                        }

                        leaderboardDragLastRowIndex = currentIndex;
                    }
                }
                else if (currentIndex >= 0)
                {
                    leaderboardDragLastRowIndex = currentIndex;
                }
            }

            if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0))
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
            }
        }

        private void SelectRowToggle(LeaderboardRowItem row)
        {
            Toggle t = row.toggle;
            if (t == null) return;

            if (leaderboardDragProcessedToggles.Contains(t))
                return;
            leaderboardDragProcessedToggles.Add(t);

            if (!t.isOn)
            {
                if (leaderboardSelectedTogglesQueue.Count >= 5)
                {
                    while (leaderboardSelectedTogglesQueue.Count >= 5)
                    {
                        Toggle oldest = leaderboardSelectedTogglesQueue[0];
                        leaderboardSelectedTogglesQueue.RemoveAt(0);
                        if (oldest != null && oldest.isOn)
                        {
                            oldest.isOn = false;
                        }
                    }
                }

                t.isOn = true;
                if (!leaderboardSelectedTogglesQueue.Contains(t))
                {
                    leaderboardSelectedTogglesQueue.Add(t);
                }
            }
        }

        private void ApplyDragRowToggle(LeaderboardRowItem row)
        {
            if (leaderboardDragStartWasSelected)
                UnselectRowToggle(row);
            else
                SelectRowToggle(row);
        }

        private void UnselectRowToggle(LeaderboardRowItem row)
        {
            Toggle t = row.toggle;
            if (t == null) return;

            if (leaderboardDragProcessedToggles.Contains(t))
                return;
            leaderboardDragProcessedToggles.Add(t);

            if (t.isOn)
            {
                t.isOn = false;
                leaderboardSelectedTogglesQueue.Remove(t);
            }
        }

        private void EndLeaderboardDragSelection()
        {
            isLeaderboardDragSelecting = false;
            leaderboardDragProcessedToggles.Clear();
            leaderboardDragStartRowIndex = -1;
            leaderboardDragLastRowIndex = -1;

            // Drop the EventSystem selection so the Unity focus ring around
            // the last toggled row doesn't stay highlighted.
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                GameObject selected = EventSystem.current.currentSelectedGameObject;
                if (selected.GetComponentInChildren<Toggle>(true) != null ||
                    selected.GetComponent<Toggle>() != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            }

            if (leaderboardCachedScrollRect != null)
            {
                leaderboardCachedScrollRect.enabled = leaderboardScrollRectWasEnabled;
            }
        }

        private class LeaderboardSearchInputRelay : MonoBehaviour, IPointerClickHandler
        {
            public GhostLinePoller owner;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (owner != null)
                    owner.BeginLeaderboardSearchInput();
            }
        }

        private void RefocusLeaderboardSearchInput()
        {
            if (!leaderboardSearchRefocusPending || leaderboardSearchInput == null)
                return;

            leaderboardSearchRefocusPending = false;
            if (!leaderboardSearchInput.isActiveAndEnabled)
                return;

            if (EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject != leaderboardSearchInput.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(leaderboardSearchInput.gameObject);
            }

            leaderboardSearchInput.ActivateInputField();
            ScheduleLeaderboardSearchSelectionCorrection();
        }

        private void ScheduleLeaderboardSearchSelectionCorrection()
        {
            if (leaderboardSearchRefocusCoroutine != null)
                StopCoroutine(leaderboardSearchRefocusCoroutine);
            leaderboardSearchRefocusCoroutine = FinishLeaderboardSearchRefocus();
            StartCoroutine(leaderboardSearchRefocusCoroutine);
        }

        private System.Collections.IEnumerator FinishLeaderboardSearchRefocus()
        {
            yield return new WaitForEndOfFrame();

            if (leaderboardSearchInput != null && leaderboardSearchInput.isActiveAndEnabled)
            {
                leaderboardSearchInput.MoveTextEnd(false);
                int end = leaderboardSearchInput.text != null ? leaderboardSearchInput.text.Length : 0;
                leaderboardSearchInput.caretPosition = end;
                leaderboardSearchInput.selectionAnchorPosition = end;
                leaderboardSearchInput.selectionFocusPosition = end;
            }

            leaderboardSearchRefocusCoroutine = null;
        }

        private void MaintainLeaderboardSearchFocus()
        {
            if (!leaderboardSearchFocusLock || leaderboardSearchInput == null || EventSystem.current == null)
                return;

            GameObject popup;
            System.Collections.IList items;
            if (!TryGetLeaderboardPopup(out popup, out items))
                return;

            if (EventSystem.current.currentSelectedGameObject == leaderboardSearchInput.gameObject)
                return;

            EventSystem.current.SetSelectedGameObject(leaderboardSearchInput.gameObject);
            ScheduleLeaderboardSearchSelectionCorrection();
        }

        private void VerifyRecordingConstructionThread()
        {
            if (recordingConstructionThreadChecked || GhostLinesPlugin.recordingConstructionThreadId < 0)
                return;

            recordingConstructionThreadChecked = true;
            if (GhostLinesPlugin.recordingConstructionThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId)
                Debug.LogWarning("[GhostLines] Replay recordings were constructed off the Unity main thread");
        }

        private void TickBindingsKeyCapture()
        {
            if (bindingListeningFor == null)
                return;
            if (bindingListeningJustStarted)
            {
                bindingListeningJustStarted = false;
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                bindingListeningFor = null;
                bindingCaptureSuppressFrame = Time.frameCount;
                return;
            }

            KeyCode pressed = DetectPressedKey();
            if (pressed == KeyCode.None)
                return;

            GhostLinesPlugin.SetBinding(bindingListeningFor, pressed);
            bindingListeningFor = null;
            bindingCaptureSuppressFrame = Time.frameCount;
        }

        private static KeyCode DetectPressedKey()
        {
            foreach (KeyCode code in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (code == KeyCode.None)
                    continue;
                if (code == KeyCode.LeftShift || code == KeyCode.RightShift ||
                    code == KeyCode.LeftControl || code == KeyCode.RightControl ||
                    code == KeyCode.LeftAlt || code == KeyCode.RightAlt ||
                    code == KeyCode.LeftCommand || code == KeyCode.RightCommand)
                    continue;
                if (Input.GetKeyDown(code))
                    return code;
            }
            return KeyCode.None;
        }

        private void Update()
        {
            if (Instance == null)
            {
                Instance = this;
                Debug.Log("[GhostLines] GhostLinePoller first Update");
            }

            TickBindingsKeyCapture();
            TickLeaderboardSearch();
            TickLeaderboardDragSelection();
            RefocusLeaderboardSearchInput();
            MaintainLeaderboardSearchFocus();

            for (int i = recordingCandidates.Count - 1; i >= 0; i--)
            {
                if (recordingCandidates[i] == null || recordingCandidates[i].GetRecording() == null)
                {
                    recordingCandidates.RemoveAt(i);
                    replayEntriesDirty = true;
                }
            }            // Cleanup dead entries
            for (int i = GhostLinesPlugin.entries.Count - 1; i >= 0; i--)
            {
                var entry = GhostLinesPlugin.entries[i];
                if (entry.GetRecording() == null)
                {
                    DestroyActiveEntry(entry);
                    GhostLinesPlugin.entries.RemoveAt(i);
                    replayEntriesDirty = true;
                    GhostLineEntry.entriesEpoch++;
                }
            }

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("smooth")))
                GhostLinesPlugin.ToggleSmoothing();

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("restart")))
                restartRequested = true;

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("xray")))
                SetXrayEnabled(!xrayEnabled);

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("playpause")))
                RequestPlayButton();

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(KeyCode.G))
                OpenStickGraph();

            if (replayControlsObj == null)
            {
                GameObject btn = Time.time < nextReplayControlsSearch ? null : GameObject.Find("btnPlay");
                if (btn != null)
                {
                    Transform t = btn.transform;
                    while (t != null)
                    {
                        if (t.name.Contains("ReplayControlsUI"))
                        {
                            BindReplayControls(t.gameObject);
                            Debug.Log($"[GhostLines] Found ReplayControlsUI via btnPlay");

                            Transform pauseT = replayControlsObj.transform.Find("VLayout/Controls/btnPlay/ImgPause");
                            if (pauseT != null)
                            {
                                isPlaying = pauseT.gameObject.activeSelf;
                                Debug.Log($"[GhostLines] Initial isPlaying={isPlaying}");
                            }
                            break;
                        }
                        t = t.parent;
                    }
                }
            }

            if (replayControlsObj != null)
            {
                if (sliderComponent == null)
                {
                    sliderSearchTimer += Time.deltaTime;
                    if (sliderSearchTimer >= 3f)
                    {
                        sliderSearchTimer = 0f;
                        FindSliderReflection();
                    }
                }
            }

            if (replayControlsObj != null && replayController == null)
                CacheReplayController();
            UpdateReplayLifecycle();
            if (!replayReady)
                return;

            HandleFrameStepInput();

            if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame)
            {
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("rewind")))
                ClickGameButton("btnRewind");
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("ff")))
                ClickGameButton("btnFastForward");
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("nametags")))
                SetNameTagsVisible(!GhostLinesPlugin.showNameTags);
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("alllines")))
            {
                bool anyOn = false;
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    if (entry.visible)
                    {
                        anyOn = true;
                        break;
                    }
                }
                SetAllRaceLinesVisible(!anyOn);
            }
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("linesthrough")))
                GhostLinesPlugin.SetLinesThroughObjects(!GhostLinesPlugin.linesThroughObjects);
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("delta")))
                GhostLinesPlugin.showDeltaDisplay = !GhostLinesPlugin.showDeltaDisplay;
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("heatmap")))
                GhostLinesPlugin.ToggleSpeedHeatmap();
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("loop")))
                loopReplayEnabled = !loopReplayEnabled;
            }

            int pilotKey = GetPilotNumberKey();
            if (pilotKey > 0)
                TrySpectatePilot(pilotKey - 1, true);

            if (replayControlsObj != null && replayController != null && ghostMatchAttempts < GHOST_MATCH_MAX_ATTEMPTS
                && GhostLinesPlugin.entries.Count > 0 && ghostDroneMap.Count < GhostLinesPlugin.entries.Count)
            {
                ghostDroneSearchTimer += Time.deltaTime;
                if (ghostDroneSearchTimer >= GHOST_MATCH_RETRY_INTERVAL)
                {
                    ghostDroneSearchTimer = 0f;
                    MatchGhostDrones();
                    ghostMatchAttempts++;
                }
            }

            if (playbackMouseCaptured && !Input.GetMouseButton(0) &&
                !draggingScrubber && !isDraggingHandle)
            {
                playbackMouseCaptured = false;
            }
            if (draggingScrubber || isDraggingHandle)
                CancelCameraLook();

            // --- Orbit input: must run EVERY frame, not behind the 1s gate ---
            if (!playbackMouseCaptured && !draggingScrubber && !isDraggingHandle)
            {
            if (orbitTarget == null)
                TryInitCamera();

            if (flightCamCtrl != null && stuntCameraRefProp != null && orbitTarget != null)
            {
                Transform currentTarget = stuntCameraRefProp.GetValue(flightCamCtrl, null) as Transform;
                if (currentTarget != null && currentTarget != orbitTarget)
                {
                    bool preserveCustomCamera = customCameraActive;
                    bool preserveFpvOrbit = startedFromFpv;
                    bool preserveOrbiting = isOrbiting;
                    bool restoreCameraView = pendingCameraViewRestore;
                    bool restoreFreeCamView = pendingFreeCamViewRestore;
                    bool restoreOrbitView = pendingOrbitViewRestore;
                    Vector3 savedFreePosition = pendingFreeCamPosition;
                    float savedFreeYaw = pendingFreeCamYaw;
                    float savedFreePitch = pendingFreeCamPitch;
                    float savedOrbitDist = pendingOrbitDist;
                    float savedOrbitYaw = pendingOrbitYaw;
                    float savedOrbitPitch = pendingOrbitPitch;
                    bool savedOrbiting = pendingOrbiting;
                    pendingCameraViewRestore = false;
                    pendingFreeCamViewRestore = false;
                    pendingOrbitViewRestore = false;
                    orbitTarget = currentTarget;
                    customCameraActive = preserveCustomCamera;
                    startedFromFpv = preserveCustomCamera && preserveFpvOrbit;
                    isOrbiting = preserveCustomCamera && preserveOrbiting;
                    if (restoreCameraView && restoreFreeCamView)
                    {
                        freeCamPosition = savedFreePosition;
                        freeCamYaw = savedFreeYaw;
                        freeCamPitch = savedFreePitch;
                    }
                    else if (restoreCameraView && restoreOrbitView)
                    {
                        orbitDist = savedOrbitDist;
                        orbitYaw = savedOrbitYaw;
                        orbitPitch = savedOrbitPitch;
                        isOrbiting = savedOrbiting;
                    }
                    else if (Camera.main != null)
                    {
                        Vector3 diff = Camera.main.transform.position - orbitTarget.position;
                        if (diff.sqrMagnitude < 0.0001f)
                            diff = Vector3.back * 3f;
                        orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                        Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                        orbitYaw = rot.eulerAngles.y;
                        orbitPitch = rot.eulerAngles.x;
                        if (orbitPitch > 180f) orbitPitch -= 360f;
                    }
                    Debug.Log(preserveCustomCamera
                        ? "[GhostLines] Camera target changed; preserving custom orbit"
                        : "[GhostLines] Pilot switch detected, camera reset");
                    Transform switchedRoot;
                    Transform switchedMount;
                    TryGetNativeTrackedTransforms(out switchedRoot, out switchedMount);
                    spectatedEntry = FindEntryForNativeRoot(switchedRoot);
                    if (spectatedEntry == null)
                    {
                        float rt;
                        float bestDist = float.MaxValue;
                        Vector3 refPos = switchedRoot != null ? switchedRoot.position : orbitTarget.position;
                        if (TryGetReplayTime(out rt))
                        {
                            foreach (var e in GhostLinesPlugin.entries)
                            {
                                Vector3 dp; Quaternion dr;
                                if (TryGetDroneTransformUnshifted(e, rt, out dp, out dr))
                                {
                                    float d = Vector3.Distance(dp, refPos);
                                    if (d < bestDist) { bestDist = d; spectatedEntry = e; }
                                }
                            }
                        }
                    }
                    InitializeDefaultReferenceEntry();
                    UpdateDroneMarkers();
                }
            }


            bool requestedFreeCam = !freeCamActive && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && (
                Input.GetKeyDown(GhostLinesPlugin.GetBinding("freecamforward")) ||
                Input.GetKeyDown(GhostLinesPlugin.GetBinding("freecamleft")) ||
                Input.GetKeyDown(GhostLinesPlugin.GetBinding("freecamback")) ||
                Input.GetKeyDown(GhostLinesPlugin.GetBinding("freecamright")));

            if (requestedFreeCam)
                EnterFreeCam();

            if (Time.frameCount != bindingCaptureSuppressFrame && bindingListeningFor == null && Input.GetKeyDown(GhostLinesPlugin.GetBinding("camreset")) && (freeCamActive || customCameraActive))
            {
                pendingCameraModeRestore = lastNativeCameraMode;
                ResetToDroneCamera();
            }

            if (freeCamActive)
            {
                HandleFreeCamInput();
            }
            else if (orbitTarget != null && Camera.main != null)
            {
                Vector3 pivot = ghostDronePosValid ? ghostDronePos : orbitTarget.position;

                // Scroll zoom
                bool usingPendingThirdPersonScroll = !Mathf.Approximately(pendingThirdPersonScroll, 0f);
                float scroll = usingPendingThirdPersonScroll ? pendingThirdPersonScroll : Input.mouseScrollDelta.y;
                pendingThirdPersonScroll = 0f;
                if (scroll != 0f)
                {
                    int nativeMode = usingPendingThirdPersonScroll ? CAMERA_MODE_THIRD_PERSON_CLOSE : GetCurrentNativeCameraMode();
                    bool nativeFirstPerson = nativeMode == CAMERA_MODE_FIRST_PERSON;

                    // Native FPV has no usable orbit distance. Hand the wheel input to
                    // the game's third-person camera, then consume it on the next frame.
                    if (!customCameraActive && nativeFirstPerson && scroll < 0f)
                    {
                        if (lastNativeCameraMode == 0)
                            lastNativeCameraMode = nativeMode;
                        startedFromFpv = true;
                        SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
                        pendingThirdPersonScroll = scroll;
                        Debug.Log("[GhostLines] FPV scroll-out: switching to third-person close");
                        scroll = 0f;
                    }
                    else if (!customCameraActive && nativeFirstPerson && scroll > 0f)
                    {
                        // Do not create a custom camera while native FPV is active.
                        scroll = 0f;
                    }

                    if (scroll != 0f)
                    {
                        if (!customCameraActive && !usingPendingThirdPersonScroll)
                            startedFromFpv = false;

                        if (!customCameraActive && Camera.main != null && orbitTarget != null)
                        {
                            Vector3 diff = Camera.main.transform.position - pivot;
                            if (diff.sqrMagnitude < 0.0001f)
                                diff = Vector3.back * 3f;
                            orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                            Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                            orbitYaw = rot.eulerAngles.y;
                            orbitPitch = rot.eulerAngles.x;
                            if (orbitPitch > 180f) orbitPitch -= 360f;
                        }

                        orbitDist -= scroll * 2f;
                        orbitDist = Mathf.Clamp(orbitDist, 0.5f, 200f);
                        if (!customCameraActive && lastNativeCameraMode == 0)
                            lastNativeCameraMode = GetCurrentNativeCameraMode();
                        if (customCameraActive && scroll > 0f && orbitDist <= (startedFromFpv ? 1.5f : 0.9f))
                        {
                            customCameraActive = false;
                            isOrbiting = false;
                            CancelCameraLook();
                            startedFromFpv = false;
                            if (lastNativeCameraMode != 0)
                            {
                                pendingCameraModeRestore = lastNativeCameraMode;
                                lastNativeCameraMode = 0;
                            }
                            Debug.Log("[GhostLines] Scrolled in; returned to native camera");
                        }
                        else
                        {
                            customCameraActive = true;
                            Debug.Log($"[GhostLines] Scroll zoom: dist={orbitDist:F2}");
                        }
                    }
                }

                // Right-click down -> start orbit
                if (Input.GetMouseButtonDown(1))
                {
                    bool startingFromNative = !customCameraActive;
                    int nativeMode = startingFromNative ? GetCurrentNativeCameraMode() : 0;
                    if (startingFromNative && lastNativeCameraMode == 0)
                        lastNativeCameraMode = nativeMode;
                    isOrbiting = true;
                    customCameraActive = true;
                    if (startingFromNative)
                    {
                        Vector3 diff = Camera.main.transform.position - pivot;
                        if (diff.sqrMagnitude < 0.0001f) diff = Vector3.back * 3f;
                        startedFromFpv = nativeMode == CAMERA_MODE_FIRST_PERSON;
                        if (startedFromFpv)
                        {
                            orbitDist = 3f;
                            Quaternion fpvRot = Quaternion.LookRotation(Camera.main.transform.forward);
                            Vector3 fpvEuler = fpvRot.eulerAngles;
                            orbitYaw = fpvEuler.y;
                            orbitPitch = fpvEuler.x;
                            SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
                        }
                        else
                        {
                            orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                            Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                            orbitYaw = rot.eulerAngles.y;
                            orbitPitch = rot.eulerAngles.x;
                        }
                        if (orbitPitch > 180f) orbitPitch -= 360f;
                    }

                    BeginCameraLookDrag();
                    Debug.Log(startingFromNative
                        ? $"[GhostLines] Orbit START dist={orbitDist:F2} yaw={orbitYaw:F1} pitch={orbitPitch:F1}"
                        : $"[GhostLines] Orbit DRAG START dist={orbitDist:F2} yaw={orbitYaw:F1} pitch={orbitPitch:F1}");
                }

                // Right-click held -> compute orbit delta
                if (Input.GetMouseButton(1) && isOrbiting)
                {
                    Vector2 delta = ReadCameraLookDelta();

                    orbitYaw += delta.x * 0.3f;
                    orbitPitch -= delta.y * 0.3f;
                    orbitPitch = Mathf.Clamp(orbitPitch, -85f, 85f);
                }

                // Right-click up -> stop orbit
                if (Input.GetMouseButtonUp(1) && isOrbiting)
                {
                    isOrbiting = false;
                    CancelCameraLook();
                    Debug.Log("[GhostLines] Orbit END, view locked");
                }

                // Middle-click -> reset to default camera
                if (Input.GetMouseButtonDown(2))
                {
                    if (Camera.main != null && orbitTarget != null)
                    {
                        Vector3 diff = Camera.main.transform.position - pivot;
                        if (diff.sqrMagnitude < 0.0001f) diff = Vector3.back * 3f;
                        orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                        Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                        orbitYaw = rot.eulerAngles.y;
                        orbitPitch = rot.eulerAngles.x;
                        if (orbitPitch > 180f) orbitPitch -= 360f;
                    }
                    customCameraActive = false;
                    isOrbiting = false;
                    CancelCameraLook();
                    startedFromFpv = false;
                    if (lastNativeCameraMode != 0)
                    {
                        pendingCameraModeRestore = lastNativeCameraMode;
                        lastNativeCameraMode = 0;
                    }
                    Debug.Log("[GhostLines] Middle click reset");
                }
            }
            }

            // --- Slow polling (1/sec) for non-input tasks ---
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;

            if (sliderComponent != null && sliderValueProp != null)
            {
                float cur = (float)sliderValueProp.GetValue(sliderComponent, null);
                var maxProp = GetSliderMaxValueProp();
                float maxVal = maxProp != null ? (float)maxProp.GetValue(sliderComponent, null) : 1f;
                if (cur >= maxVal - 0.001f)
                    isPlaying = false;
            }

            foreach (var entry in GhostLinesPlugin.entries)
                GhostLinesPlugin.TryBuildLine(entry);

            if (replayController != null)
            {
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    if (!entry.replayColorResolved)
                    {
                        Color? realColor = ResolvePilotColorFromReplayState(entry.GetRecording());
                        if (realColor.HasValue)
                        {
                            entry.resolvedReplayColor = realColor.Value;
                            entry.replayColorResolved = true;
                        }
                    }
                    bool nameChanged = false;
                    if (!entry.replayNameResolved && entry.pilotName != null && entry.pilotName.StartsWith("Pilot "))
                    {
                        string realName = GhostLinesPlugin.CleanPilotName(ResolvePilotNameFromReplayState(entry.GetRecording()));
                        if (!string.IsNullOrWhiteSpace(realName))
                        {
                            entry.resolvedReplayName = realName;
                            entry.replayNameResolved = true;
                            entry.pilotName = realName;
                            GhostLineEntry.entriesEpoch++;
                            nameChanged = true;
                        }
                    }
                    bool colorChanged = entry.replayColorResolved && entry.resolvedReplayColor.Value != entry.color;
                    if (colorChanged)
                    {
                        entry.color = entry.resolvedReplayColor.Value;
                        GhostLinesPlugin.RebuildLine(entry);
                        if (entry.markerText != null && nameChanged)
                            entry.markerText.text = entry.pilotName;
                    }
                    else if (nameChanged && entry.markerText != null)
                    {
                        entry.markerText.text = entry.pilotName;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (!replayReady)
                return;

            if (TryGetReplayTime(out float stableRt))
                stableReplayTime = stableRt;
            replayTimeDelta = prevReplayTime > 0f
                ? Mathf.Max(0f, stableReplayTime - prevReplayTime)
                : 0f;
            prevReplayTime = stableReplayTime;
            float rawLead = replayTimeDelta <= 0.5f ? replayTimeDelta : 0f;
            predictionLead = Mathf.Lerp(predictionLead, rawLead, 0.3f);
            float ghostBlend = 1f - Mathf.Exp(-Time.deltaTime / 0.02f);
            displayReplayTime = Mathf.Abs(stableReplayTime - displayReplayTime) > 0.25f
                ? stableReplayTime
                : Mathf.Lerp(displayReplayTime, stableReplayTime, ghostBlend);
            ghostPosBlend = 1f - Mathf.Exp(-Time.deltaTime / 0.025f);
            UpdateHoldZoom();
            RequestLoopedReplayAtEnd();
            EnforceEffectiveReplayEnd();

            bool restartScheduledThisFrame = false;
            if (restartRequested)
            {
                restartRequested = false;
                frameStepPaused = false;
                float restartSpeed = playbackSpeed;
                bool restartWithOverride = !Mathf.Approximately(restartSpeed, 1f);
                if (sliderComponent != null && sliderValueProp != null)
                {
                    float restartTime = restartSpeed < 0f
                        ? GetLoopReplayEnd(GetNativeReplayMaximum(0f))
                        : 0f;
                    sliderValueProp.SetValue(sliderComponent, restartTime, null);
                }
                overridePlayback = restartWithOverride;
                isPlaying = true;
                restartPlaybackPending = !restartWithOverride;
                restartScheduledThisFrame = true;
                Debug.Log("[GhostLines] Replay restart requested");
            }

            if (pendingPlayToggle)
            {
                pendingPlayToggle = false;
                if (overridePlayback)
                {
                    isPlaying = !isPlaying;
                    Debug.Log($"[GhostLines] Override play/pause, now isPlaying={isPlaying}");
                }
            }

            if (overridePlayback)
                SetNativeReplayPlaying(false);

            if (controlsCG != null && controlsHidden)
            {
                controlsCG.alpha = 0f;
                controlsCG.interactable = false;
                controlsCG.blocksRaycasts = false;
            }

            if (flightCamCtrl != null && inThirdPersonProp != null)
            {
                try { lateIsThirdPerson = (bool)inThirdPersonProp.GetValue(flightCamCtrl, null); } catch { }
                if (!lateIsThirdPerson && !startedFromFpv) customCameraActive = false;
            }

            if (frameStepPaused)
            {
                SetNativeReplayPlaying(false);
                isPlaying = false;
            }
            else if (replayControlsObj != null && !overridePlayback)
            {
                isPlaying = !IsReplayFinished() && IsNativeReplayPlaying();
            }

            if (restartPlaybackPending && !restartScheduledThisFrame)
            {
                restartPlaybackPending = false;
                SetNativeReplayPlaying(true);
                isPlaying = true;
                Debug.Log("[GhostLines] Replay restarted and playing");
            }

            if (pendingCameraModeRestore != 0)
            {
                SetNativeCameraMode(pendingCameraModeRestore, false);
                pendingCameraModeRestore = 0;
            }

            if (fpvOnReplayPending && !freeCamActive && !customCameraActive)
            {
                CaptureReplayEntryCameraMode();
                if (GetCurrentNativeCameraMode() == CAMERA_MODE_FIRST_PERSON || Time.time > fpvForceTimeout)
                {
                    fpvOnReplayPending = false;
                }
                else if (cameraModeSetMethod != null)
                {
                    SetNativeCameraMode(CAMERA_MODE_FIRST_PERSON, false);
                    if (!fpvForceLogged)
                    {
                        fpvForceLogged = true;
                        Debug.Log("[GhostLines] Forcing FPV at replay entry");
                    }
                }
            }

            EnforceNativeHudVisibility();

            if (overridePlayback && isPlaying && sliderComponent != null && sliderValueProp != null && !isDraggingHandle)
            {
                float cur = (float)sliderValueProp.GetValue(sliderComponent, null);
                var maxP = GetSliderMaxValueProp();
                var minP = GetSliderMinValueProp();
                float maxV = maxP != null ? (float)maxP.GetValue(sliderComponent, null) : 1f;
                float minV = minP != null ? (float)minP.GetValue(sliderComponent, null) : 0f;
                maxV = GetEffectiveReplayEnd(maxV);
                float next = cur + playbackSpeed * Time.deltaTime;
                float loopEnd = GetLoopReplayEnd(maxV);
                if (loopReplayEnabled && playbackSpeed > 0f && next >= loopEnd)
                {
                    next = loopEnd;
                    restartRequested = true;
                }
                if (next >= maxV || next <= minV)
                {
                    next = Mathf.Clamp(next, minV, maxV);
                    isPlaying = false;
                    SetNativeReplayPlaying(false);
                }
                sliderValueProp.SetValue(sliderComponent, next, null);
            }

            if (freeCamActive && Camera.main != null)
            {
                Camera.main.transform.position = freeCamPosition;
                Camera.main.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                UpdateDroneMarkers();
                EnforceNativeSpectatedTextStyle();
                return;
            }

            UpdateDroneMarkers();
            EnforceNativeSpectatedTextStyle();
        }

        private void ApplyGhostDroneTransforms(float replayTime)
        {
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Key == null || pair.Value == null)
                    continue;

                SetGhostTrailsSuppressed(pair.Key, true);
                Vector3 position;
                Quaternion rotation;
                if (TryGetDroneTransform(pair.Key, replayTime, out position, out rotation))
                {
                    Vector3 smoothed;
                    if (ghostSmoothedPos.TryGetValue(pair.Key, out smoothed))
                        smoothed = Vector3.Lerp(smoothed, position, ghostPosBlend);
                    else
                        smoothed = position;
                    ghostSmoothedPos[pair.Key] = smoothed;
                    Quaternion smoothedRot;
                    if (ghostSmoothedRot.TryGetValue(pair.Key, out smoothedRot))
                        smoothedRot = Quaternion.Slerp(smoothedRot, rotation, ghostPosBlend);
                    else
                        smoothedRot = rotation;
                    ghostSmoothedRot[pair.Key] = smoothedRot;
                    pair.Value.SetPositionAndRotation(smoothed, smoothedRot);
                }
            }
        }

        private GhostLineEntry FindEntryForNativeRoot(Transform root)
        {
            if (root == null)
                return null;
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Value == root)
                    return pair.Key;
            }
            return null;
        }

        private bool TryGetNativeTrackedTransforms(out Transform root, out Transform cameraMount)
        {
            root = null;
            cameraMount = null;
            if (flightCamCtrl == null || trackingDroneDataProp == null)
                return false;

            object data = null;
            try { data = trackingDroneDataProp.GetValue(flightCamCtrl, null); } catch { }
            if (data == null)
                return false;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var type = data.GetType();
            if (trackedRootFieldInfo == null && trackedCameraMountFieldInfo == null)
            {
                trackedRootFieldInfo = type.GetField("root", flags);
                trackedCameraMountFieldInfo = type.GetField("cameraParentPoint", flags);
            }
            if (trackedRootFieldInfo != null)
                root = trackedRootFieldInfo.GetValue(data) as Transform;
            if (trackedCameraMountFieldInfo != null)
                cameraMount = trackedCameraMountFieldInfo.GetValue(data) as Transform;
            return root != null || cameraMount != null;
        }

        private void EnsureXrayMaterials()
        {
            if (xrayWireMaterial != null)
                return;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                return;

            if (xrayWireMaterial == null)
            {
                xrayWireMaterial = new Material(shader);
                xrayWireMaterial.hideFlags = HideFlags.HideAndDontSave;
                xrayWireMaterial.SetInt("_SrcBlend", 5);
                xrayWireMaterial.SetInt("_DstBlend", 10);
                xrayWireMaterial.SetInt("_Cull", 0);
                xrayWireMaterial.SetInt("_ZWrite", 0);
                xrayWireMaterial.SetInt("_ZTest", 8);
                xrayWireMaterial.SetColor("_Color", Color.white);
            }
        }

        private bool IsXrayExcluded(Transform transform)
        {
            if (transform == null) return true;
            if (transform.GetComponent<LineRenderer>() != null || transform.GetComponent<TrailRenderer>() != null)
                return true;
            if (transform.GetComponentInParent<Canvas>() != null)
                return true;
            if (transform.name.StartsWith("GhostLine_") || transform.name.StartsWith("GhostMarker_"))
                return true;

            if (transform.GetComponentInParent<Rigidbody>() != null)
                return true;

            for (Transform current = transform; current != null; current = current.parent)
            {
                string name = current.name.ToLowerInvariant();
                if (name.Contains("fixation") || name.Contains("dquad") || name.Contains("cameraredot")
                    || name.Contains("camera nano") || name.Contains("soupape") || name.Contains("soldering")
                    || name.Contains("propeller") || name.Contains("prop_") || name.Contains("motor"))
                    return true;
            }

            foreach (var pair in ghostDroneMap)
            {
                if (pair.Value != null && transform.IsChildOf(pair.Value))
                    return true;
            }
            return false;
        }

        private bool TryGetXrayDronePosition(out Vector3 position)
        {
            if (ghostDronePosValid)
            {
                position = ghostDronePos;
                return true;
            }

            Transform root;
            if (spectatedEntry != null && ghostDroneMap.TryGetValue(spectatedEntry, out root) && root != null)
            {
                position = root.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private readonly HashSet<int> xrayQueryIds = new HashSet<int>();

        private void RefreshXrayStructures(Vector3 dronePosition)
        {
            if (xrayStructuresBuilt && xrayStructureCenterValid &&
                (dronePosition - xrayStructureCenter).sqrMagnitude < XRAY_REBUILD_DISTANCE * XRAY_REBUILD_DISTANCE)
                return;

            xrayStructuresBuilt = true;
            xrayStructureCenter = dronePosition;
            xrayStructureCenterValid = true;

            xrayQueryIds.Clear();
            int colliderCount = Physics.OverlapSphereNonAlloc(
                dronePosition,
                XRAY_QUERY_RADIUS,
                xrayColliderBuffer,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);
            for (int i = 0; i < colliderCount; i++)
            {
                Collider collider = xrayColliderBuffer[i];
                if (collider == null || !collider.enabled || collider.isTrigger || IsXrayExcluded(collider.transform))
                    continue;
                if (collider.attachedRigidbody != null)
                    continue;
                Bounds bounds = collider.bounds;
                if (Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) < 0.25f)
                    continue;
                int id = collider.GetInstanceID();
                if (!xrayQueryIds.Add(id))
                    continue;
                bool found = false;
                for (int j = 0; j < xrayStructures.Count; j++)
                {
                    if (xrayStructures[j].colliderId == id)
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                    continue;
                XrayStructure structure = BuildXrayStructure(collider);
                if (structure == null || structure.segments == null || structure.segments.Count == 0)
                    continue;
                structure.colliderId = id;
                structure.currentAlpha = xraySeedInstant ? 1f : 0f;
                xrayStructures.Add(structure);
            }

            for (int i = xrayStructures.Count - 1; i >= 0; i--)
            {
                XrayStructure s = xrayStructures[i];
                if (!xrayQueryIds.Contains(s.colliderId) && s.currentAlpha <= 0.01f)
                    xrayStructures.RemoveAt(i);
            }

            xrayStructures.Sort((a, b) =>
            {
                bool aActive = xrayQueryIds.Contains(a.colliderId);
                bool bActive = xrayQueryIds.Contains(b.colliderId);
                if (aActive != bActive)
                    return aActive ? -1 : 1;
                return a.bounds.SqrDistance(dronePosition).CompareTo(b.bounds.SqrDistance(dronePosition));
            });
            xraySeedInstant = false;
            xrayStructureRevision++;
            InvalidateXrayMapCache();
        }

        private void EnsureXrayMapMesh()
        {
            if (xrayMapMesh != null)
                return;

            xrayMapMesh = new Mesh();
            xrayMapMesh.name = "GhostLines Xray Map";
            xrayMapMesh.hideFlags = HideFlags.HideAndDontSave;
            xrayMapMesh.MarkDynamic();
        }

        private void InvalidateXrayMapCache()
        {
            xrayMapCacheValid = false;
            xrayMapCachedStructureRevision = -1;
        }

        private bool IsXrayMapCacheCurrent(Vector3 dronePosition)
        {
            return xrayMapCacheValid
                && xrayMapCachedStructureRevision == xrayStructureRevision
                && xrayMapCachedDronePosition == dronePosition;
        }

        private XrayStructure BuildXrayStructure(Collider collider)
        {
            if (collider is BoxCollider box)
            {
                Vector3 half = box.size * 0.5f;
                Vector3 c = box.center;
                Vector3[] p = new Vector3[8];
                p[0] = collider.transform.TransformPoint(c + new Vector3(-half.x, -half.y, -half.z));
                p[1] = collider.transform.TransformPoint(c + new Vector3(half.x, -half.y, -half.z));
                p[2] = collider.transform.TransformPoint(c + new Vector3(half.x, half.y, -half.z));
                p[3] = collider.transform.TransformPoint(c + new Vector3(-half.x, half.y, -half.z));
                p[4] = collider.transform.TransformPoint(c + new Vector3(-half.x, -half.y, half.z));
                p[5] = collider.transform.TransformPoint(c + new Vector3(half.x, -half.y, half.z));
                p[6] = collider.transform.TransformPoint(c + new Vector3(half.x, half.y, half.z));
                p[7] = collider.transform.TransformPoint(c + new Vector3(-half.x, half.y, half.z));
                return MakeXrayStructure(p, collider.bounds);
            }

            Bounds b = collider.bounds;
            if (b.size.sqrMagnitude < 0.0001f)
                return null;
            Vector3 min = b.min;
            Vector3 max = b.max;
            Vector3[] boundsPoints =
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, max.y, min.z), new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z)
            };
            return MakeXrayStructure(boundsPoints, b);
        }

        private XrayStructure MakeXrayStructure(Vector3[] p, Bounds bounds)
        {
            int[,] edgePairs =
            {
                { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
                { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
                { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
            };
            List<XraySegment> segments = new List<XraySegment>(12);
            for (int i = 0; i < 12; i++)
                segments.Add(new XraySegment(p[edgePairs[i, 0]], p[edgePairs[i, 1]]));
            return new XrayStructure { bounds = bounds, segments = segments };
        }

        private bool TryClipXraySegmentToSphere(
            Vector3 worldA,
            Vector3 worldB,
            Vector3 center,
            float radius,
            out Vector3 drawA,
            out Vector3 drawB,
            out float alphaA,
            out float alphaB)
        {
            drawA = worldA;
            drawB = worldB;
            alphaA = 0f;
            alphaB = 0f;
            Vector3 direction = worldB - worldA;
            float directionLengthSqr = direction.sqrMagnitude;
            float radiusSqr = radius * radius;
            if (directionLengthSqr < 0.0001f)
            {
                if ((worldA - center).sqrMagnitude > radiusSqr)
                    return false;
                alphaA = GetXrayRangeFade(worldA, center, radius);
                alphaB = alphaA;
                return true;
            }

            Vector3 offset = worldA - center;
            float qa = directionLengthSqr;
            float qb = 2f * Vector3.Dot(offset, direction);
            float qc = offset.sqrMagnitude - radiusSqr;
            float discriminant = qb * qb - 4f * qa * qc;
            if (discriminant < 0f)
                return false;

            float root = Mathf.Sqrt(discriminant);
            float t0 = (-qb - root) / (2f * qa);
            float t1 = (-qb + root) / (2f * qa);
            if (t0 > t1)
            {
                float swap = t0;
                t0 = t1;
                t1 = swap;
            }

            float enter = Mathf.Max(0f, t0);
            float exit = Mathf.Min(1f, t1);
            if (enter > exit)
                return false;

            drawA = Vector3.LerpUnclamped(worldA, worldB, enter);
            drawB = Vector3.LerpUnclamped(worldA, worldB, exit);
            alphaA = GetXrayRangeFade(drawA, center, radius);
            alphaB = GetXrayRangeFade(drawB, center, radius);
            return true;
        }

        private float GetXrayRangeFade(Vector3 position, Vector3 center, float radius)
        {
            float fadeStart = radius * 0.6f;
            float distance = Vector3.Distance(position, center);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(radius, fadeStart, distance));
        }

        private void SetXrayEnabled(bool enabled)
        {
            xrayEnabled = enabled;
            if (!enabled)
                return;

            xraySeedInstant = true;
            xrayStructuresBuilt = false;
            xrayStructureCenterValid = false;
            xrayStructures.Clear();
            xrayVisibleStructures.Clear();
            xrayFadingVisible.Clear();
        }

        private void AddXrayMapSegment(Color mapColor, Vector3 drawA, Vector3 drawB, float alphaA, float alphaB)
        {
            int firstIndex = xrayMapVertices.Count;
            xrayMapVertices.Add(drawA);
            xrayMapVertices.Add(drawB);
            xrayMapColors.Add(new Color(mapColor.r, mapColor.g, mapColor.b, mapColor.a * alphaA));
            xrayMapColors.Add(new Color(mapColor.r, mapColor.g, mapColor.b, mapColor.a * alphaB));
            xrayMapIndices.Add(firstIndex);
            xrayMapIndices.Add(firstIndex + 1);
        }

        private void RebuildXrayMapMesh(Vector3 dronePosition)
        {
            Color mapColor = new Color(0.15f, 0.75f, 1f, 0.42f);
            int lineBudget = XRAY_MAX_LINES;
            float clipRadius = XRAY_RENDER_RADIUS + XRAY_FADE_BUFFER;
            xrayVisibleStructures.Clear();
            xrayFadingVisible.Clear();
            float clipRadiusSqr = clipRadius * clipRadius;
            foreach (var structure in xrayStructures)
            {
                bool inClipRange = structure.bounds.SqrDistance(dronePosition) <= clipRadiusSqr;
                bool stillFading = structure.currentAlpha > 0.01f && !inClipRange;
                if (stillFading)
                    xrayFadingVisible.Add(structure);
                else if (inClipRange)
                    xrayVisibleStructures.Add(structure);
            }
            xrayMapVertices.Clear();
            xrayMapColors.Clear();
            xrayMapIndices.Clear();
            foreach (var structure in xrayStructures)
            {
                bool visible = structure.bounds.SqrDistance(dronePosition) <= clipRadiusSqr;
                if (visible)
                    structure.currentAlpha = 1f;
                else
                    structure.currentAlpha = Mathf.MoveTowards(
                        structure.currentAlpha, 0f, XRAY_FADE_SPEED * Time.deltaTime);
            }
            foreach (var structure in xrayFadingVisible)
            {
                float reveal = structure.currentAlpha;
                if (reveal < 0.01f) continue;
                foreach (var segment in structure.segments)
                {
                    if (lineBudget < 1) break;
                    Vector3 rangeA, rangeB;
                    float rangeAlphaA, rangeAlphaB;
                    if (!TryClipXraySegmentToSphere(
                        segment.a,
                        segment.b,
                        dronePosition,
                        clipRadius,
                        out rangeA,
                        out rangeB,
                        out rangeAlphaA,
                        out rangeAlphaB))
                        continue;
                    if (rangeAlphaA < 0.01f && rangeAlphaB < 0.01f)
                    {
                        Vector3 midpoint = Vector3.Lerp(rangeA, rangeB, 0.5f);
                        float midpointAlpha = GetXrayRangeFade(midpoint, dronePosition, clipRadius);
                        if (midpointAlpha > 0.01f)
                        {
                            if (lineBudget < 2) break;
                            lineBudget -= 2;
                            AddXrayMapSegment(mapColor, rangeA, midpoint, rangeAlphaA * reveal, midpointAlpha * reveal);
                            AddXrayMapSegment(mapColor, midpoint, rangeB, midpointAlpha * reveal, rangeAlphaB * reveal);
                        }
                    }
                    else
                    {
                        lineBudget -= 1;
                        AddXrayMapSegment(mapColor, rangeA, rangeB, rangeAlphaA * reveal, rangeAlphaB * reveal);
                    }
                }
                if (lineBudget < 1) break;
            }
            foreach (var structure in xrayVisibleStructures)
            {
                foreach (var segment in structure.segments)
                {
                    if (lineBudget < 1) break;
                    Vector3 rangeA, rangeB;
                    float rangeAlphaA, rangeAlphaB;
                    if (!TryClipXraySegmentToSphere(
                        segment.a,
                        segment.b,
                        dronePosition,
                        clipRadius,
                        out rangeA,
                        out rangeB,
                        out rangeAlphaA,
                        out rangeAlphaB))
                        continue;
                    if (rangeAlphaA < 0.01f && rangeAlphaB < 0.01f)
                    {
                        Vector3 midpoint = Vector3.Lerp(rangeA, rangeB, 0.5f);
                        float midpointAlpha = GetXrayRangeFade(midpoint, dronePosition, clipRadius);
                        if (midpointAlpha > 0.01f)
                        {
                            if (lineBudget < 2) break;
                            lineBudget -= 2;
                            AddXrayMapSegment(mapColor, rangeA, midpoint, rangeAlphaA, midpointAlpha);
                            AddXrayMapSegment(mapColor, midpoint, rangeB, midpointAlpha, rangeAlphaB);
                        }
                    }
                    else
                    {
                        lineBudget -= 1;
                        AddXrayMapSegment(mapColor, rangeA, rangeB, rangeAlphaA, rangeAlphaB);
                    }
                }
                if (lineBudget < 1) break;
            }

            xrayMapMesh.Clear();
            xrayMapMesh.SetVertices(xrayMapVertices);
            xrayMapMesh.SetColors(xrayMapColors);
            if (xrayMapIndices.Count > 0)
                xrayMapMesh.SetIndices(xrayMapIndices, MeshTopology.Lines, 0, false);
            xrayMapMesh.RecalculateBounds();

            xrayMapCachedDronePosition = dronePosition;
            xrayMapCachedStructureRevision = xrayStructureRevision;
            xrayMapCacheValid = true;
        }

        private void DrawXrayMap()
        {
            Vector3 dronePosition;
            if (!TryGetXrayDronePosition(out dronePosition))
                return;

            RefreshXrayStructures(dronePosition);
            if (xrayWireMaterial == null)
                return;
            EnsureXrayMapMesh();
            if (xrayMapMesh == null)
                return;

            if (!IsXrayMapCacheCurrent(dronePosition))
                RebuildXrayMapMesh(dronePosition);
            if (xrayMapMesh.vertexCount == 0)
                return;

            xrayWireMaterial.SetPass(0);
            Graphics.DrawMeshNow(xrayMapMesh, Matrix4x4.identity);
        }

        private void DrawXrayDrone(Camera cam)
        {
            if (spectatedEntry == null || xrayWireMaterial == null)
                return;
            Transform root;
            if (!ghostDroneMap.TryGetValue(spectatedEntry, out root) || root == null)
                return;

            if (xrayDroneRoot != root)
            {
                xrayDroneRoot = root;
                xrayDroneFilters.Clear();
                List<MeshFilter> preferredBodies = new List<MeshFilter>();
                List<MeshFilter> bodyCandidates = new List<MeshFilter>();
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter == null || filter.sharedMesh == null)
                        continue;
                    string name = filter.transform.name.ToLowerInvariant();
                    if (name.Contains("prop") || name.Contains("blade") || name.Contains("motor")
                        || name.Contains("camera") || name.Contains("nano") || name.Contains("antenna")
                        || name.Contains("solder") || name.Contains("soupape") || name.Contains("led"))
                        continue;
                    Renderer renderer = filter.GetComponent<Renderer>();
                    if (renderer != null && renderer.bounds.size.sqrMagnitude > 0.04f)
                    {
                        bodyCandidates.Add(filter);
                        if (name.Contains("frame") || name.Contains("body") || name.Contains("chassis")
                            || name.Contains("airframe") || name.Contains("fixationframe"))
                            preferredBodies.Add(filter);
                    }
                }
                List<MeshFilter> selectedBodies = preferredBodies.Count > 0 ? preferredBodies : bodyCandidates;
                selectedBodies.Sort((a, b) =>
                {
                    float av = a.GetComponent<Renderer>() != null ? a.GetComponent<Renderer>().bounds.size.sqrMagnitude : 0f;
                    float bv = b.GetComponent<Renderer>() != null ? b.GetComponent<Renderer>().bounds.size.sqrMagnitude : 0f;
                    return bv.CompareTo(av);
                });
                for (int i = 0; i < selectedBodies.Count && i < 3; i++)
                    xrayDroneFilters.Add(selectedBodies[i]);
            }

            DrawXrayDroneBounds(cam, root.position, spectatedEntry.color);
        }

        private void DrawXrayDroneBounds(Camera cam, Vector3 dronePosition, Color color)
        {
            if (xrayDroneFilters.Count == 0 || xrayWireMaterial == null)
                return;
            Renderer firstRenderer = xrayDroneFilters[0].GetComponent<Renderer>();
            if (firstRenderer == null)
                return;
            Bounds bounds = firstRenderer.bounds;
            for (int i = 1; i < xrayDroneFilters.Count; i++)
            {
                Renderer renderer = xrayDroneFilters[i].GetComponent<Renderer>();
                if (renderer != null)
                    bounds.Encapsulate(renderer.bounds);
            }

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            xrayDroneBoundsCorners[0] = new Vector3(min.x, min.y, min.z);
            xrayDroneBoundsCorners[1] = new Vector3(max.x, min.y, min.z);
            xrayDroneBoundsCorners[2] = new Vector3(max.x, max.y, min.z);
            xrayDroneBoundsCorners[3] = new Vector3(min.x, max.y, min.z);
            xrayDroneBoundsCorners[4] = new Vector3(min.x, min.y, max.z);
            xrayDroneBoundsCorners[5] = new Vector3(max.x, min.y, max.z);
            xrayDroneBoundsCorners[6] = new Vector3(max.x, max.y, max.z);
            xrayDroneBoundsCorners[7] = new Vector3(min.x, max.y, max.z);

            xrayWireMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.LoadIdentity();
            GL.Begin(GL.LINES);
            for (int i = 0; i < xrayBoxEdgePairs.Length; i += 2)
            {
                Vector3 drawA, drawB;
                float alphaA, alphaB;
                if (!TryClipXraySegmentToSphere(
                    xrayDroneBoundsCorners[xrayBoxEdgePairs[i]],
                    xrayDroneBoundsCorners[xrayBoxEdgePairs[i + 1]],
                    dronePosition,
                    XRAY_RENDER_RADIUS,
                    out drawA,
                    out drawB,
                    out alphaA,
                    out alphaB))
                    continue;
                GL.Color(new Color(1f, 1f, 1f, 0.95f * alphaA));
                GL.Vertex(cam.worldToCameraMatrix.MultiplyPoint3x4(drawA));
                GL.Color(new Color(1f, 1f, 1f, 0.95f * alphaB));
                GL.Vertex(cam.worldToCameraMatrix.MultiplyPoint3x4(drawB));
            }
            GL.End();
            GL.PopMatrix();
        }

        private void OnCameraPostRender(Camera cam)
        {
            if (!replayReady || !xrayEnabled || cam != Camera.main)
                return;
            EnsureXrayMaterials();
            if (xrayWireMaterial == null)
                return;
            DrawXrayMap();
            DrawXrayDrone(cam);
        }

        private void OnCameraPreRender(Camera cam)
        {
            if (!replayReady || cam != Camera.main) return;

            float rt = stableReplayTime;

            Vector3 nativeCameraPosition = cam.transform.position;
            Quaternion nativeCameraRotation = cam.transform.rotation;
            Transform nativeRoot;
            Transform nativeCameraMount;
            TryGetNativeTrackedTransforms(out nativeRoot, out nativeCameraMount);
            Vector3 nativeRootPosition = nativeRoot != null
                ? nativeRoot.position
                : orbitTarget != null ? orbitTarget.position : nativeCameraPosition;

            ApplyGhostDroneTransforms(displayReplayTime);
            if (freeCamActive)
            {
                cam.transform.position = freeCamPosition;
                cam.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                if (holdZoomFovCaptured)
                    cam.fieldOfView = holdZoomCurrentFov;

                GhostLineEntry freeCamTarget = spectatedEntry;
                if (freeCamTarget == null && nativeRoot != null)
                    freeCamTarget = FindEntryForNativeRoot(nativeRoot);

                Vector3 freeCamDronePosition;
                if (freeCamTarget != null)
                {
                    if (!ghostSmoothedPos.TryGetValue(freeCamTarget, out freeCamDronePosition))
                    {
                        Quaternion freeCamDroneRotation;
                        TryGetDroneTransform(freeCamTarget, displayReplayTime, out freeCamDronePosition, out freeCamDroneRotation);
                    }
                    spectatedEntry = freeCamTarget;
                    ghostDronePos = freeCamDronePosition;
                    ghostDronePosValid = true;
                    if (holdZoomActive)
                    {
                        UpdateFreeCamFocus(freeCamDronePosition);
                        cam.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                    }
                }
                UpdateDroneMarkers();
                return;
            }
            if (orbitTarget == null) return;

            GhostLineEntry target = spectatedEntry;
            if (target == null)
            {
                target = FindEntryForNativeRoot(nativeRoot);
                if (target == null)
                {
                    Vector3 tgtPos = nativeRoot != null ? nativeRoot.position : orbitTarget.position;
                    float bestD = float.MaxValue;
                    foreach (var e in GhostLinesPlugin.entries)
                    {
                        Vector3 ep; Quaternion er;
                        if (TryGetDroneTransformUnshifted(e, rt, out ep, out er))
                        {
                            float d = Vector3.Distance(ep, tgtPos);
                            if (d < bestD) { bestD = d; target = e; }
                        }
                    }
                }
            }
            if (target == null)
            {
                if (!noTargetLogged)
                {
                    noTargetLogged = true;
                    Debug.Log("[GhostLines] OnCameraPreRender: no target found");
                }
                return;
            }
            noTargetLogged = false;
            if (spectatedEntry == null)
                spectatedEntry = target;
            InitializeDefaultReferenceEntry();

            if (nativeRoot != null)
            {
                Transform mappedRoot;
                if (!ghostDroneMap.TryGetValue(target, out mappedRoot) || mappedRoot != nativeRoot)
                {
                    ghostDroneMap[target] = nativeRoot;
                    CacheGhostTrails(target, nativeRoot);
                    SetGhostTrailsSuppressed(target, true);
                }
                if (nativeCameraMount != null)
                    ghostCamMountMap[target] = nativeCameraMount;
            }

            Vector3 posS; Quaternion rotS;
            if (!TryGetDroneTransform(target, rt, out posS, out rotS)) { Debug.Log("[GhostLines] OnCameraPreRender: TryGetDroneTransform failed"); return; }

            Vector3 smoothedPosS;
            if (ghostSmoothedPos.TryGetValue(target, out smoothedPosS))
                smoothedPosS = Vector3.Lerp(smoothedPosS, posS, ghostPosBlend);
            else
                smoothedPosS = posS;
            ghostSmoothedPos[target] = smoothedPosS;
            Quaternion smoothedRotS;
            if (ghostSmoothedRot.TryGetValue(target, out smoothedRotS))
                smoothedRotS = Quaternion.Slerp(smoothedRotS, rotS, ghostPosBlend);
            else
                smoothedRotS = rotS;
            ghostSmoothedRot[target] = smoothedRotS;

            if (nativeRoot != null)
                nativeRoot.SetPositionAndRotation(smoothedPosS, smoothedRotS);

            ghostDronePos = smoothedPosS;
            ghostDronePosValid = true;

            if (customCameraActive)
            {
                Quaternion rot = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
                Vector3 orbitPos = smoothedPosS + rot * (Vector3.back * orbitDist);
                cam.transform.position = orbitPos;
                Vector3 lookDirection = smoothedPosS - cam.transform.position;
                if (lookDirection.sqrMagnitude > 0.0001f)
                    cam.transform.rotation = Quaternion.LookRotation(lookDirection, Vector3.up);
                UpdateDroneMarkers();
                return;
            }

            bool isThirdP = true;
            if (flightCamCtrl != null && inThirdPersonProp != null)
                try { isThirdP = (bool)inThirdPersonProp.GetValue(flightCamCtrl, null); } catch { }

            if (isThirdP)
            {
                Vector3 shift = smoothedPosS - nativeRootPosition;
                cam.transform.SetPositionAndRotation(
                    nativeCameraPosition + shift,
                    nativeCameraRotation);
            }
            else
            {
                if (ghostCamMountMap.TryGetValue(target, out Transform camMount) && camMount != null)
                {
                    cam.transform.SetPositionAndRotation(camMount.position, camMount.rotation);
                }
                else
                {
                    Vector3 shift = posS - nativeRootPosition;
                    cam.transform.SetPositionAndRotation(
                        nativeCameraPosition + shift,
                        nativeCameraRotation);
                }

            }

            UpdateDroneMarkers();
        }

        private void CacheReplayController()
        {
            if (replayControlsObj == null) return;
            if (replayController != null && timeProp == null)
            {
                var existingType = replayController.GetType();
                togglePlayPauseMethod = existingType.GetMethod("TogglePlayPause", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                timeProp = existingType.GetProperty("CurrentTime", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                Debug.Log($"[GhostLines] Cached direct ReplayControls, TogglePlayPause={(togglePlayPauseMethod != null)}, CurrentTime={timeProp?.CanRead},{timeProp?.CanWrite}");
            }
            if (replayController == null)
            {
                var comps = replayControlsObj.GetComponents<Component>();
                foreach (var c in comps)
                {
                    var t = c.GetType();
                    if (t.Name == "ReplayControls")
                    {
                        replayController = c;
                        togglePlayPauseMethod = t.GetMethod("TogglePlayPause", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        timeProp = t.GetProperty("CurrentTime", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        Debug.Log($"[GhostLines] Cached ReplayControls controller, TogglePlayPause={(togglePlayPauseMethod != null)}, CurrentTime={timeProp?.CanRead},{timeProp?.CanWrite}");
                        break;
                    }
                }

                Transform playBtnT = replayControlsObj.transform.Find("VLayout/Controls/btnPlay/ImgPause");
                if (playBtnT != null)
                {
                    isPlaying = playBtnT.gameObject.activeSelf;
                    Debug.Log($"[GhostLines] Initial isPlaying={isPlaying}");
                }
            }

            if (sliderComponent == null)
                FindSliderReflection();

            if (!cameraDumped)
            {
                cameraDumped = true;
                DumpCameraInfo();
            }
        }

        private bool IsNativeReplayPlaying()
        {
            Transform pauseT = replayControlsObj != null
                ? replayControlsObj.transform.Find("VLayout/Controls/btnPlay/ImgPause")
                : null;
            return pauseT != null && pauseT.gameObject.activeSelf;
        }

        private void SetNativeReplayPlaying(bool playing)
        {
            CacheReplayController();
            if (togglePlayPauseMethod != null && IsNativeReplayPlaying() != playing)
                togglePlayPauseMethod.Invoke(replayController, null);
        }

        private void RequestPlayButton()
        {
            frameStepPaused = false;
            if (IsReplayFinished())
            {
                restartRequested = true;
                return;
            }

            if (overridePlayback)
                pendingPlayToggle = true;
            else
                ClickGameButton("btnPlay");
        }

        private void ClickGameButton(string name)
        {
            CacheReplayController();
            if (name == "btnPlay")
            {
                frameStepPaused = false;
                if (overridePlayback)
                {
                    isPlaying = !isPlaying;
                    Debug.Log($"[GhostLines] Override play/pause, now isPlaying={isPlaying}");
                }
                else if (togglePlayPauseMethod != null)
                {
                    togglePlayPauseMethod.Invoke(replayController, null);
                    isPlaying = !isPlaying;
                    Debug.Log($"[GhostLines] Toggled play/pause, now isPlaying={isPlaying}");
                }
            }
            else if (name == "btnRewind")
            {
                frameStepPaused = false;
                float next = StepPlaybackSpeed(-1);
                SetPlaybackSpeed(next);
                Debug.Log($"[GhostLines] Rewind speed set to {playbackSpeed}x");
            }
            else if (name == "btnFastForward")
            {
                frameStepPaused = false;
                float next = StepPlaybackSpeed(1);
                SetPlaybackSpeed(next);
                Debug.Log($"[GhostLines] FF speed set to {playbackSpeed}x");
            }
        }

        private static readonly float[] PLAYBACK_SPEED_LADDER = new float[]
        {
            -4f, -2f, -1f, -0.5f, -0.25f, -0.1f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f
        };

        private float StepPlaybackSpeed(int direction)
        {
            int idx = -1;
            for (int i = 0; i < PLAYBACK_SPEED_LADDER.Length; i++)
                if (Mathf.Approximately(playbackSpeed, PLAYBACK_SPEED_LADDER[i])) { idx = i; break; }
            if (idx < 0)
                idx = System.Array.IndexOf(PLAYBACK_SPEED_LADDER, 1f);
            int next = idx + direction;
            if (next < 0 || next >= PLAYBACK_SPEED_LADDER.Length)
                next = System.Array.IndexOf(PLAYBACK_SPEED_LADDER, 1f);
            return PLAYBACK_SPEED_LADDER[next];
        }

        private void DumpCameraInfo()
        {
            try
            {
                Debug.Log("[GhostLines] === Camera Dump ===");
                var cams = GameObject.FindObjectsOfType<Camera>(true);
                Debug.Log($"[GhostLines] Found {cams.Length} cameras");
                foreach (var cam in cams)
                {
                    if (cam == null) continue;
                    Debug.Log($"[GhostLines]   Camera: {cam.name} en={cam.enabled} ms={cam.allowMSAA} ortho={cam.orthographic}");
                    Transform p = cam.transform;
                    if (p == null) { Debug.Log("[GhostLines]     Path: <null transform>"); continue; }
                    string path = p.name;
                    while (p.parent != null) { p = p.parent; if (p == null) break; path = p.name + "/" + path; }
                    Debug.Log($"[GhostLines]     Path: {path}");
                    foreach (var comp in cam.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var t = comp.GetType();
                        string cn = t.Name;
                        if (cn.IndexOf("MouseOrbit", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Orbit", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("ThirdPerson", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Playback", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Flight", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Zoom", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Follow", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("LineOfSight", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Debug.Log($"[GhostLines]     COMP: {cn}");
                            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                object v = null;
                                try { v = f.GetValue(comp); } catch { v = "?"; }
                                Debug.Log($"[GhostLines]       FIELD: {f.Name} ({f.FieldType.Name}) = {v}");
                            }
                            foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                object v = null;
                                if (prop.CanRead) try { v = prop.GetValue(comp, null); } catch { v = "?"; }
                                Debug.Log($"[GhostLines]       PROP: {prop.Name} ({prop.PropertyType.Name}) r={prop.CanRead} w={prop.CanWrite} = {v}");
                            }
                        }
                    }
                }

                Debug.Log("[GhostLines] === End Camera Dump ===");
            }
            catch (System.Exception e)
            {
                Debug.Log($"[GhostLines][DIAG] DumpCameraInfo exception: {e}");
            }
        }

        private void CacheFlightCamModeAccessors()
        {
            const System.Reflection.BindingFlags propertyFlags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            try
            {
                if (flightCamCtrl == null) return;
                object impl = GetFlightCamImpl();
                if (impl == null) return;
                foreach (var m in impl.GetType().GetMethods(propertyFlags))
                {
                    if (m.ReturnType != typeof(void)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType.Name == "CameraMode" && ps[1].ParameterType == typeof(bool))
                    {
                        cameraModeSetMethod = m;
                        cameraModeEnumType = ps[0].ParameterType;
                        break;
                    }
                }
            }
            catch { }
        }

        private object GetFlightCamImpl()
        {
            try
            {
                if (flightCamCtrl == null) return null;
                if (flightCamImplFieldInfo == null)
                {
                    flightCamImplFieldInfo = flightCamCtrl.GetType().GetField("activeImplementation",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                }
                if (flightCamImplFieldInfo == null) return null;
                flightCamImpl = flightCamImplFieldInfo.GetValue(flightCamCtrl);
                return flightCamImpl;
            }
            catch { return null; }
        }

        private int GetCurrentNativeCameraMode()
        {
            try
            {
                object impl = GetFlightCamImpl();
                if (impl == null) return 0;
                if (currentCameraModePropInfo == null)
                {
                    currentCameraModePropInfo = impl.GetType().GetProperty("CurrentCameraMode",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                if (currentCameraModePropInfo == null) return 0;
                object v = currentCameraModePropInfo.GetValue(impl, null);
                return v == null ? 0 : System.Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        private void CaptureReplayEntryCameraMode()
        {
            if (replayEntryCameraMode != 0)
                return;

            TryInitCamera();
            replayEntryCameraMode = GetCurrentNativeCameraMode();
        }

        private void SetNativeCameraMode(int mode, bool instant)
        {
            try
            {
                if (cameraModeSetMethod == null || cameraModeEnumType == null) return;
                object impl = GetFlightCamImpl();
                if (impl == null) return;
                object modeVal = System.Enum.ToObject(cameraModeEnumType, mode);
                cameraModeSetMethod.Invoke(impl, new object[] { modeVal, instant });
            }
            catch { }
        }

        private void TryInitCamera()
        {
            if (orbitTarget == null && Camera.main != null)
            {
                var ctrl = Camera.main.GetComponent("FlightCameraController");
                if (ctrl != null)
                {
                    flightCamCtrl = ctrl;
                    CacheFlightCamModeAccessors();
                    const System.Reflection.BindingFlags propertyFlags =
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
                    var tProp = ctrl.GetType().GetProperty("StuntCameraReferenceTransform", propertyFlags);
                    stuntCameraRefProp = tProp;
                    inThirdPersonProp = ctrl.GetType().GetProperty("InThirdPerson", propertyFlags);
                    trackingDroneDataProp = ctrl.GetType().GetProperty("TrackingDroneData", propertyFlags);
                    if (tProp != null)
                    {
                        orbitTarget = tProp.GetValue(ctrl, null) as Transform;
                        if (orbitTarget != null)
                        {
                            Vector3 diff = Camera.main.transform.position - orbitTarget.position;
                            orbitDist = Mathf.Max(diff.magnitude, 3f);
                            if (diff.sqrMagnitude < 0.0001f)
                                diff = Vector3.back * orbitDist;
                            orbitYaw = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;
                            orbitPitch = -Mathf.Asin(diff.y / orbitDist) * Mathf.Rad2Deg;
                            Debug.Log($"[GhostLines] Orbit target={orbitTarget.name} dist={orbitDist:F1} yaw={orbitYaw:F1} pitch={orbitPitch:F1}");
                        }
                    }
                }
            }
        }

        private void BeginCameraLookDrag()
        {
            cameraLookDragActive = true;
            cameraLookSampleValid = true;
            lastCameraLookMouse = Input.mousePosition;
            cameraLookScreenWidth = Screen.width;
            cameraLookScreenHeight = Screen.height;
        }

        private void CancelCameraLook()
        {
            cameraLookDragActive = false;
            cameraLookSampleValid = false;
            cameraLookScreenWidth = -1;
            cameraLookScreenHeight = -1;
        }

        private Vector2 ReadCameraLookDelta()
        {
            if (!cameraLookDragActive || !cameraLookSampleValid || !Application.isFocused)
            {
                CancelCameraLook();
                return Vector2.zero;
            }

            if (cameraLookScreenWidth != Screen.width || cameraLookScreenHeight != Screen.height)
            {
                CancelCameraLook();
                return Vector2.zero;
            }

            Vector2 current = Input.mousePosition;
            Vector2 delta = current - lastCameraLookMouse;
            lastCameraLookMouse = current;

            Vector2 rawDelta;
            if (TryGetRawCameraLookDelta(out rawDelta))
            {
                if (Mathf.Abs(delta.x) > 0.5f && Mathf.Abs(rawDelta.x) > 0.0001f)
                {
                    float measuredScale = Mathf.Clamp(Mathf.Abs(delta.x / rawDelta.x), 0.05f, 200f);
                    cameraRawMouseScaleX = Mathf.Lerp(cameraRawMouseScaleX, measuredScale, 0.2f);
                }
                if (Mathf.Abs(delta.y) > 0.5f && Mathf.Abs(rawDelta.y) > 0.0001f)
                {
                    float measuredScale = Mathf.Clamp(Mathf.Abs(delta.y / rawDelta.y), 0.05f, 200f);
                    cameraRawMouseScaleY = Mathf.Lerp(cameraRawMouseScaleY, measuredScale, 0.2f);
                }

                // At the desktop edge absolute coordinates stop changing, so use only
                // the physical mouse-axis movement for that frozen component.
                if (Mathf.Abs(delta.x) < 0.01f && Mathf.Abs(rawDelta.x) > 0.0001f)
                    delta.x = rawDelta.x * cameraRawMouseScaleX;
                if (Mathf.Abs(delta.y) < 0.01f && Mathf.Abs(rawDelta.y) > 0.0001f)
                    delta.y = rawDelta.y * cameraRawMouseScaleY;
            }
            return delta;
        }

        private bool TryGetRawCameraLookDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (cameraRawMouseAxesState < 0)
                return false;

            try
            {
                delta.x = Input.GetAxisRaw("Mouse X");
                delta.y = Input.GetAxisRaw("Mouse Y");
                cameraRawMouseAxesState = 1;
                return true;
            }
            catch
            {
                // Liftoff can use a custom Input Manager; do not retry missing axes every frame.
                cameraRawMouseAxesState = -1;
                return false;
            }
        }

        private void EnterFreeCam()
        {
            if (Camera.main == null) return;
            Transform cam = Camera.main.transform;
            freeCamActive = true;
            customCameraActive = false;
            isOrbiting = false;
            CancelCameraLook();
            freeCamPosition = cam.position;
            freeCamCurrentSpeed = FREE_CAM_SPEED;

            Vector3 euler = cam.rotation.eulerAngles;
            freeCamYaw = euler.y;
            freeCamPitch = euler.x;
            if (freeCamPitch > 180f) freeCamPitch -= 360f;

            if (lastNativeCameraMode == 0)
                lastNativeCameraMode = GetCurrentNativeCameraMode();
            SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
            Debug.Log($"[GhostLines] Free cam enabled at pos={freeCamPosition} yaw={freeCamYaw:F1} pitch={freeCamPitch:F1}");
        }

        private void ResetToDroneCamera()
        {
            if (pendingCameraModeRestore == 0 && lastNativeCameraMode != 0)
                pendingCameraModeRestore = lastNativeCameraMode;
            freeCamActive = false;
            customCameraActive = false;
            isOrbiting = false;
            CancelCameraLook();
            startedFromFpv = false;
            lastNativeCameraMode = 0;
            Debug.Log("[GhostLines] Camera reset to drone");
        }
        private void HandleFreeCamInput()
        {
            if (Camera.main == null)
                return;

            if (Input.GetMouseButtonDown(2))
            {
                ResetToDroneCamera();
                return;
            }

            bool focusHeld = holdZoomActive && spectatedEntry != null;

            if (focusHeld)
            {
                CancelCameraLook();
            }
            else if (Input.GetMouseButtonDown(1))
                BeginCameraLookDrag();

            if (Input.GetMouseButton(1) && !focusHeld)
            {
                Vector2 delta = ReadCameraLookDelta();

                freeCamYaw += delta.x * FREE_CAM_MOUSE_SENSITIVITY;
                freeCamPitch -= delta.y * FREE_CAM_MOUSE_SENSITIVITY;
                freeCamPitch = Mathf.Clamp(freeCamPitch, -85f, 85f);
            }

            if (Input.GetMouseButtonUp(1))
                CancelCameraLook();

            if (focusHeld)
                return;

            Vector3 move = Vector3.zero;
            if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame)
            {
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamforward"))) move += Vector3.forward;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamback"))) move += Vector3.back;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamleft"))) move += Vector3.left;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamright"))) move += Vector3.right;
            }

            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float targetSpeed = shiftHeld ? FREE_CAM_SHIFT_MAX_SPEED : FREE_CAM_SPEED;
            freeCamCurrentSpeed = Mathf.MoveTowards(
                freeCamCurrentSpeed,
                targetSpeed,
                (shiftHeld ? FREE_CAM_SHIFT_ACCELERATION : FREE_CAM_SHIFT_DECELERATION) * Time.deltaTime);

            if (move.sqrMagnitude > 0.0001f)
            {
                Quaternion rot = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                freeCamPosition += rot * move.normalized * freeCamCurrentSpeed * Time.deltaTime;
            }
        }

        private void UpdateDroneMarkers()
        {
            droneLabelHitboxes.Clear();
            if (!replayReady || Camera.main == null)
                return;

            float replayTime = displayReplayTime;
            UpdateReferenceDeltaDisplay(replayTime);

            Vector3 camPos = Camera.main.transform.position;
            foreach (var entry in GhostLinesPlugin.entries)
            {
                if (entry.markerObject == null || entry.markerTether == null || entry.markerText == null)
                    continue;

                Vector3 dronePos;
                if (!TryGetSmoothDronePos(entry, replayTime, out dronePos))
                    continue;

                entry.markerObject.SetActive(true);
                entry.markerTether.enabled = GhostLinesPlugin.showNameTags || GhostLinesPlugin.showDeltaDisplay;
                MeshRenderer nameRenderer = entry.markerText.GetComponent<MeshRenderer>();
                if (nameRenderer != null)
                    nameRenderer.enabled = GhostLinesPlugin.showNameTags;

                float markerHeight = entry.markerBaseHeight > 0f ? entry.markerBaseHeight : DRONE_MARKER_HEIGHT;
                Vector3 labelPos = dronePos + Vector3.up * markerHeight;

                entry.markerObject.transform.position = dronePos;
                entry.markerTether.SetPosition(0, Vector3.zero);

                Transform labelT = entry.markerText.transform;
                labelT.position = labelPos;
                labelT.rotation = Quaternion.LookRotation(camPos - labelPos, Vector3.up);
                labelT.Rotate(0f, 180f, 0f, Space.Self);

                float dist = Vector3.Distance(camPos, labelPos);
                float fovScale = Camera.main.fieldOfView / 60f;
                float scale = Mathf.Clamp(dist * 0.08f * fovScale, 0.35f, 1.8f);
                labelT.localScale = Vector3.one * scale;

                if (entry.markerText.text != entry.pilotName)
                    entry.markerText.text = entry.pilotName;
                ApplyDroneLabelColor(entry);
                entry.markerTether.startWidth = Mathf.Clamp(dist * 0.0025f, 0.04f, 0.16f);
                entry.markerTether.endWidth = Mathf.Clamp(dist * 0.0025f, 0.04f, 0.16f);

                float labelHalfHeight = nameRenderer != null ? nameRenderer.bounds.size.y * 0.5f : 0.35f;
                float tetherTop = markerHeight - labelHalfHeight - 0.05f;
                entry.markerTether.SetPosition(1, Vector3.up * Mathf.Max(tetherTop, 0.3f));

                // Rebuild the click target after enabling and refreshing the text mesh.
                if (GhostLinesPlugin.showNameTags && nameRenderer != null)
                    AddDroneLabelHitbox(entry, nameRenderer.bounds, Camera.main);
            }
        }

        private bool TryGetSmoothDronePos(GhostLineEntry entry, float replayTime, out Vector3 pos)
        {
            Vector3 smoothed;
            bool hasPrev = ghostSmoothedPos.TryGetValue(entry, out smoothed);
            if (hasPrev && ghostDroneMap.ContainsKey(entry))
            {
                pos = smoothed;
                return true;
            }
            Vector3 raw;
            if (!TryGetDronePosition(entry, replayTime, out raw))
            {
                pos = Vector3.zero;
                return false;
            }
            if (ghostDroneMap.ContainsKey(entry))
                pos = hasPrev ? Vector3.Lerp(smoothed, raw, ghostPosBlend) : raw;
            else
                pos = Vector3.Lerp(hasPrev ? smoothed : raw, raw, ghostPosBlend);
            ghostSmoothedPos[entry] = pos;
            return true;
        }

        private void ApplyDroneLabelColor(GhostLineEntry entry)
        {
            if (entry == null)
                return;

            Color color = entry == hoveredDroneLabelEntry
                ? Color.Lerp(entry.color, Color.white, 0.72f)
                : entry.color;
            if (entry.markerText != null)
                entry.markerText.color = color;
            if (entry.markerTether != null)
            {
                entry.markerTether.startColor = color;
                entry.markerTether.endColor = color;
            }
        }

        private void AddDroneLabelHitbox(GhostLineEntry entry, Bounds bounds, Camera camera)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = scratchLabelCorners;
            corners[0] = new Vector3(min.x, min.y, min.z);
            corners[1] = new Vector3(max.x, min.y, min.z);
            corners[2] = new Vector3(min.x, max.y, min.z);
            corners[3] = new Vector3(max.x, max.y, min.z);
            corners[4] = new Vector3(min.x, min.y, max.z);
            corners[5] = new Vector3(max.x, min.y, max.z);
            corners[6] = new Vector3(min.x, max.y, max.z);
            corners[7] = new Vector3(max.x, max.y, max.z);

            float left = float.MaxValue;
            float right = float.MinValue;
            float top = float.MaxValue;
            float bottom = float.MinValue;
            float screenMinY = float.MaxValue;
            float screenMaxY = float.MinValue;
            float nearestDepth = float.MaxValue;
            foreach (Vector3 corner in corners)
            {
                Vector3 screen = camera.WorldToScreenPoint(corner);
                if (screen.z <= camera.nearClipPlane)
                    return;
                left = Mathf.Min(left, screen.x);
                right = Mathf.Max(right, screen.x);
                top = Mathf.Min(top, Screen.height - screen.y);
                bottom = Mathf.Max(bottom, Screen.height - screen.y);
                screenMinY = Mathf.Min(screenMinY, screen.y);
                screenMaxY = Mathf.Max(screenMaxY, screen.y);
                nearestDepth = Mathf.Min(nearestDepth, screen.z);
            }

            if (right < camera.pixelRect.xMin || left > camera.pixelRect.xMax ||
                screenMaxY < camera.pixelRect.yMin || screenMinY > camera.pixelRect.yMax)
                return;

            Rect rect = new Rect(left, top, right - left, bottom - top);
            rect.xMin -= 8f;
            rect.xMax += 8f;
            rect.yMin -= 6f;
            rect.yMax += 6f;
            droneLabelHitboxes.Add(new DroneLabelHitbox
            {
                entry = entry,
                rect = rect,
                depth = nearestDepth
            });
        }

        private bool TryGetDroneInput(GhostLineEntry entry, float replayTime, out Vector4 input)
        {
            input = Vector4.zero;
            if (entry == null || entry.stateTimes == null || entry.originalInputs == null
                || entry.stateTimes.Length == 0 || entry.stateTimes.Length != entry.originalInputs.Length)
                return false;

            float queryT = GetGateQueryTimestamp(entry, replayTime);
            if (entry.stateTimes.Length == 1)
            {
                input = entry.originalInputs[0];
                return true;
            }

            int hi = FindFirstStateAtOrAfter(entry.stateTimes, queryT);
            if (hi <= 0)
            {
                input = entry.originalInputs[0];
                return true;
            }
            if (hi >= entry.stateTimes.Length)
            {
                input = entry.originalInputs[entry.originalInputs.Length - 1];
                return true;
            }

            int lo = hi - 1;
            float t0 = entry.stateTimes[lo];
            float t1 = entry.stateTimes[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, queryT);
            input = Vector4.LerpUnclamped(entry.originalInputs[lo], entry.originalInputs[hi], u);
            return true;
        }

        private bool IsFirstPersonView()
        {
            if (freeCamActive || customCameraActive)
                return false;
            if (flightCamCtrl == null || inThirdPersonProp == null)
                return !lateIsThirdPerson;
            try { return !(bool)inThirdPersonProp.GetValue(flightCamCtrl, null); }
            catch { return !lateIsThirdPerson; }
        }

        private void DrawStickPanel(Rect rect, Vector2 value)
        {
            rect = new Rect(
                Mathf.Round(rect.x),
                Mathf.Round(rect.y),
                Mathf.Round(rect.width),
                Mathf.Round(rect.height));
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.75f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 2f, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), whiteTex);

            float cx = rect.x + rect.width * 0.5f;
            float cy = rect.y + rect.height * 0.5f;
            GUI.color = new Color(1f, 1f, 1f, 0.3f);
            DrawDashedHLine(rect.x + 6f, rect.xMax - 6f, cy, 5f, 3f, 2f);
            DrawDashedVLine(rect.y + 6f, rect.yMax - 6f, cx, 5f, 3f, 2f);

            Vector2 clamped = new Vector2(Mathf.Clamp(value.x, -1f, 1f), Mathf.Clamp(value.y, -1f, 1f));
            float ringSize = Mathf.Clamp(rect.width * 0.17f, 8f, 14f) * 1.15f;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 1f;
            float cursorX = cx + clamped.x * radius;
            float cursorY = cy - clamped.y * radius;
            GUI.color = new Color(1f, 1f, 1f, 0.95f);
            GUI.DrawTexture(new Rect(cursorX - ringSize * 0.5f, cursorY - ringSize * 0.5f, ringSize, ringSize), stickRingTex);

            GUI.color = old;
        }

        private void DrawDashedHLine(float x0, float x1, float y, float dash, float gap, float thickness)
        {
            float t = x0;
            while (t < x1)
            {
                float end = Mathf.Min(t + dash, x1);
                GUI.DrawTexture(new Rect(t, y - thickness * 0.5f, end - t, thickness), whiteTex);
                t = end + gap;
            }
        }

        private void DrawDashedVLine(float y0, float y1, float x, float dash, float gap, float thickness)
        {
            float t = y0;
            while (t < y1)
            {
                float end = Mathf.Min(t + dash, y1);
                GUI.DrawTexture(new Rect(x - thickness * 0.5f, t, thickness, end - t), whiteTex);
                t = end + gap;
            }
        }

        private void EnsureStickTextures()
        {
            if (stickRingTex != null)
                return;
            int s = 48;
            stickRingTex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            stickRingTex.filterMode = FilterMode.Bilinear;
            stickRingTex.wrapMode = TextureWrapMode.Clamp;
            float c = (s - 1) * 0.5f;
            float inner = c * 0.48f;
            float outer = c;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01((d - (inner - 1.5f)) * 0.5f) * Mathf.Clamp01(((outer + 1.5f) - d) * 0.5f);
                    stickRingTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            stickRingTex.Apply();
        }

        private void DrawStickCam()
        {
            if (!replayReady || !stickCamEnabled || spectatedEntry == null)
                return;

            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;

            Vector4 input;
            if (!TryGetDroneInput(spectatedEntry, replayTime, out input))
                return;

            float throttle = input.x * 2f - 1f;
            Vector2 leftStick = new Vector2(input.y, throttle);
            Vector2 rightStick = new Vector2(-input.w, input.z);

            float panelSize = Mathf.Clamp(Screen.height * 0.11f, 68f, 96f) * stickCamSize;
            float gap = 14f * stickCamSize;
            float groupW = panelSize * 2f + gap;
            float groupH = panelSize;

            if (stickCamX < 0f)
                stickCamX = Screen.width * 0.5f;
            if (stickCamY < 0f)
                stickCamY = Screen.height - 56f - panelSize * 0.5f - 10f;

            stickCamX = Mathf.Clamp(stickCamX, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
            stickCamY = Mathf.Clamp(stickCamY, groupH * 0.5f + 2f, Screen.height - groupH * 0.5f - 2f);

            Rect leftPanel = new Rect(stickCamX - groupW * 0.5f, stickCamY - groupH * 0.5f, panelSize, panelSize);
            Rect rightPanel = new Rect(leftPanel.xMax + gap, leftPanel.y, panelSize, panelSize);
            Rect totalRect = new Rect(stickCamX - groupW * 0.5f - 5f, stickCamY - groupH * 0.5f - 5f, groupW + 10f, groupH + 10f);
            currentStickCamOverlayRect = totalRect;
            currentStickCamOverlayRectValid = true;

            var ev = Event.current;
            Vector2 mp = ev.mousePosition;
            if (ev.type == EventType.MouseDown && ev.button == 0 && totalRect.Contains(mp))
            {
                if (Time.realtimeSinceStartup - lastStickCamClickTime < 0.35f &&
                    (mp - lastStickCamClickPos).sqrMagnitude < 36f)
                {
                    lastStickCamClickTime = -1f;
                    draggingStickCam = false;
                    OpenStickGraph();
                }
                else
                {
                    lastStickCamClickTime = Time.realtimeSinceStartup;
                    lastStickCamClickPos = mp;
                    draggingStickCam = true;
                    stickCamPositionCustomized = true;
                    dragStartMouse = mp;
                    dragStartStickCamPos = new Vector2(stickCamX, stickCamY);
                }
                ev.Use();
            }
            if (draggingStickCam)
            {
                if (ev.type == EventType.MouseUp && ev.button == 0)
                {
                    draggingStickCam = false;
                    SaveCurrentOverlayPositions();
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && ev.button == 0)
                {
                    Vector2 offset = mp - dragStartMouse;
                    float draggedX = SnapOverlayToHorizontalCenter(dragStartStickCamPos.x + offset.x);
                    stickCamX = Mathf.Clamp(draggedX, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
                    stickCamY = Mathf.Clamp(dragStartStickCamPos.y + offset.y, groupH * 0.5f + 2f, Screen.height - groupH * 0.5f - 2f);
                    ev.Use();
                }
            }

            EnsureStickTextures();
            DrawStickPanel(leftPanel, leftStick);
            DrawStickPanel(rightPanel, rightStick);
        }

        private void OpenStickGraph()
        {
            OpenStickGraphFor(spectatedEntry);
        }

        private void OpenStickGraphFor(GhostLineEntry pilot)
        {
            if (pilot == null || pilot.originalInputs == null ||
                pilot.stateTimes == null || pilot.stateTimes.Length < 2 ||
                !GhostLinesPlugin.entries.Contains(pilot))
                return;

            for (int i = stickGraphs.Count - 1; i >= 0; i--)
            {
                if (stickGraphs[i].pilot == pilot)
                {
                    StickGraphState existing = stickGraphs[i];
                    stickGraphs.RemoveAt(i);
                    stickGraphs.Add(existing);
                    existing.fadeAmount = 1f;
                    return;
                }
            }

            StickGraphState state = new StickGraphState();
            state.pilot = pilot;
            state.fadeAmount = 1f;
            int count = stickGraphs.Count;
            state.rect = new Rect(
                Screen.width * 0.5f - state.rect.width * 0.5f + count * STICK_GRAPH_OPEN_OFFSET,
                Screen.height * 0.5f - state.rect.height * 0.5f + count * STICK_GRAPH_OPEN_OFFSET,
                state.rect.width,
                state.rect.height);
            stickGraphs.Add(state);
        }

        private void EnsureStickGraphRect(StickGraphState g, bool initializePosition = true)
        {
            if (initializePosition && g.rect.x < 0f)
            {
                g.rect = new Rect(
                    Screen.width - g.rect.width - 24f,
                    120f,
                    g.rect.width,
                    g.rect.height);
            }
            g.rect.x = Mathf.Clamp(g.rect.x, 12f, Mathf.Max(12f, Screen.width - g.rect.width - 12f));
            g.rect.y = Mathf.Clamp(g.rect.y, 12f, Mathf.Max(12f, Screen.height - g.rect.height - 12f));
        }

        private static Rect GetStickGraphOptionRect(Rect ddBtn, int index)
        {
            return new Rect(ddBtn.x, ddBtn.yMax + 2f + 20f * index, ddBtn.width, 20f);
        }

        private float SampleStickChannel(GhostLineEntry entry, float stateTime, int channel)
        {
            float[] times = entry.stateTimes;
            Vector4[] inputs = entry.originalInputs;
            int count = Mathf.Min(times.Length, inputs.Length);
            if (count == 0)
                return 0f;

            int lo = 0;
            int hi = count - 1;
            if (stateTime <= times[0])
            {
                lo = 0;
                hi = 0;
            }
            else if (stateTime >= times[hi])
            {
                lo = hi;
            }
            else
            {
                int low = 0;
                int high = count - 1;
                while (high - low > 1)
                {
                    int mid = (low + high) / 2;
                    if (times[mid] <= stateTime) low = mid;
                    else high = mid;
                }
                lo = low;
                hi = high;
            }

            Vector4 input;
            if (lo == hi)
                input = inputs[lo];
            else
            {
                float t0 = times[lo];
                float t1 = times[hi];
                float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, stateTime);
                input = Vector4.LerpUnclamped(inputs[lo], inputs[hi], u);
            }

            switch (channel)
            {
                case 0: return Mathf.Clamp(input.x * 2f - 1f, -1f, 1f);
                case 1: return Mathf.Clamp(input.y, -1f, 1f);
                case 2: return Mathf.Clamp(-input.w, -1f, 1f);
                default: return Mathf.Clamp(input.z, -1f, 1f);
            }
        }

        private void DrawStickGraph()
        {
            if (stickGraphs.Count == 0)
                return;

            EnsureSettingsStyles();
            EnsureSettingsTextures();
            EnsureIcons();

            Event e = Event.current;
            stickGraphPilotOptions.Clear();
            foreach (GhostLineEntry entry in GhostLinesPlugin.entries)
            {
                if (entry.originalInputs != null && entry.stateTimes != null && entry.stateTimes.Length >= 2)
                    stickGraphPilotOptions.Add(entry);
            }
            if (stickGraphActivated != null && stickGraphs.Contains(stickGraphActivated))
            {
                stickGraphs.Remove(stickGraphActivated);
                stickGraphs.Add(stickGraphActivated);
                stickGraphActivated = null;
            }
            for (int gi = 0; gi < stickGraphs.Count; gi++)
            {
                StickGraphState g = stickGraphs[gi];
                if (g.pilot == null || g.pilot.originalInputs == null ||
                    g.pilot.stateTimes == null || g.pilot.stateTimes.Length < 2 ||
                    !GhostLinesPlugin.entries.Contains(g.pilot))
                {
                    if (g.traceTex != null)
                    {
                        Destroy(g.traceTex);
                        g.traceTex = null;
                    }
                    stickGraphs.RemoveAt(gi);
                    gi--;
                    continue;
                }

                EnsureStickGraphRect(g);
                Rect panel = g.rect;
                float alpha = g.fadeAmount;
                if (alpha < 0.01f)
                    continue;

                bool covered = false;
                for (int j = gi + 1; j < stickGraphs.Count; j++)
                {
                    if (stickGraphs[j].fadeAmount > 0.01f && stickGraphs[j].rect.Contains(e.mousePosition))
                    {
                        covered = true;
                        break;
                    }
                }

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
                Rect close = new Rect(panel.xMax - 30f, panel.y + 5f, 22f, 20f);
                Rect pin = new Rect(panel.xMax - 60f, panel.y + 5f, 22f, 20f);
                float ddW = 96f;
                Rect heatBtn = new Rect(panel.xMax - 88f, panel.y + 6f, 22f, 18f);
                Rect plusBtn = new Rect(heatBtn.x - 28f, panel.y + 6f, 22f, 18f);
                Rect ddBtn = new Rect(plusBtn.x - ddW - 6f, panel.y + 6f, ddW, 18f);
                Rect pilotBtn = new Rect(panel.x + 12f, panel.y + 6f, Mathf.Max(20f, ddBtn.x - panel.x - 20f), 18f);
                int pilotOptCount = Mathf.Min(stickGraphPilotOptions.Count, 8);

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
                    ddBtn.Contains(e.mousePosition) || plusBtn.Contains(e.mousePosition) ||
                    heatBtn.Contains(e.mousePosition) || inPilotList;

                float edgeT = STICK_GRAPH_EDGE_THICK;
                float cornerT = STICK_GRAPH_CORNER_GRAB;
                Rect edgeTop = new Rect(panel.x, panel.y, panel.width, edgeT);
                Rect edgeBottom = new Rect(panel.x, panel.yMax - edgeT, panel.width, edgeT);
                Rect edgeLeft = new Rect(panel.x, panel.y, edgeT, panel.height);
                Rect edgeRight = new Rect(panel.xMax - edgeT, panel.y, edgeT, panel.height);
                Rect cornerTopLeft = new Rect(panel.x, panel.y, cornerT, cornerT);
                Rect cornerTopRight = new Rect(panel.xMax - cornerT, panel.y, cornerT, cornerT);
                Rect cornerBottomLeft = new Rect(panel.x, panel.yMax - cornerT, cornerT, cornerT);
                Rect cornerBottomRight = new Rect(panel.xMax - cornerT, panel.yMax - cornerT, cornerT, cornerT);

                int hoverEdge = 0;
                if (!inTitleControls && !g.channelDropdown && !g.pilotDropdown)
                {
                    if (cornerTopLeft.Contains(e.mousePosition))
                        hoverEdge = STICK_GRAPH_EDGE_LEFT | STICK_GRAPH_EDGE_TOP;
                    else if (cornerTopRight.Contains(e.mousePosition))
                        hoverEdge = STICK_GRAPH_EDGE_RIGHT | STICK_GRAPH_EDGE_TOP;
                    else if (cornerBottomLeft.Contains(e.mousePosition))
                        hoverEdge = STICK_GRAPH_EDGE_LEFT | STICK_GRAPH_EDGE_BOTTOM;
                    else if (cornerBottomRight.Contains(e.mousePosition))
                        hoverEdge = STICK_GRAPH_EDGE_RIGHT | STICK_GRAPH_EDGE_BOTTOM;
                    else
                    {
                        if (edgeRight.Contains(e.mousePosition)) hoverEdge |= STICK_GRAPH_EDGE_RIGHT;
                        if (edgeBottom.Contains(e.mousePosition)) hoverEdge |= STICK_GRAPH_EDGE_BOTTOM;
                        if (edgeLeft.Contains(e.mousePosition)) hoverEdge |= STICK_GRAPH_EDGE_LEFT;
                        if (edgeTop.Contains(e.mousePosition)) hoverEdge |= STICK_GRAPH_EDGE_TOP;
                    }
                }
                g.edgeHover = hoverEdge;

                int shownEdges = g.resizing ? g.resizeEdge : g.edgeHover;
                if (shownEdges != 0)
                {
                    GUI.color = new Color(0.25f, 0.62f, 0.95f, 0.55f * alpha);
                    if ((shownEdges & STICK_GRAPH_EDGE_LEFT) != 0)
                        GUI.DrawTexture(edgeLeft, whiteTex);
                    if ((shownEdges & STICK_GRAPH_EDGE_RIGHT) != 0)
                        GUI.DrawTexture(edgeRight, whiteTex);
                    if ((shownEdges & STICK_GRAPH_EDGE_TOP) != 0)
                    {
                        float leftTrim = (shownEdges & STICK_GRAPH_EDGE_LEFT) != 0 ? edgeT : 0f;
                        float rightTrim = (shownEdges & STICK_GRAPH_EDGE_RIGHT) != 0 ? edgeT : 0f;
                        GUI.DrawTexture(new Rect(panel.x + leftTrim, panel.y, panel.width - leftTrim - rightTrim, edgeT), whiteTex);
                    }
                    if ((shownEdges & STICK_GRAPH_EDGE_BOTTOM) != 0)
                    {
                        float leftTrim = (shownEdges & STICK_GRAPH_EDGE_LEFT) != 0 ? edgeT : 0f;
                        float rightTrim = (shownEdges & STICK_GRAPH_EDGE_RIGHT) != 0 ? edgeT : 0f;
                        GUI.DrawTexture(new Rect(panel.x + leftTrim, panel.yMax - edgeT, panel.width - leftTrim - rightTrim, edgeT), whiteTex);
                    }
                }

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
                    stickGraphActivated = g;
                    e.Use();
                }
                if (g.resizing && e.type == EventType.MouseDrag && e.button == 0)
                {
                    float dx = e.mousePosition.x - g.resizeStartMouse.x;
                    float dy = e.mousePosition.y - g.resizeStartMouse.y;
                    float newX = g.resizeStartRect.x;
                    float newY = g.resizeStartRect.y;
                    float newW = g.resizeStartRect.width;
                    float newH = g.resizeStartRect.height;
                    float maxW = Mathf.Max(STICK_GRAPH_MIN_WIDTH, Screen.width - 24f);
                    float maxH = Mathf.Max(STICK_GRAPH_MIN_HEIGHT, Screen.height - 24f);
                    if ((g.resizeEdge & STICK_GRAPH_EDGE_RIGHT) != 0)
                        newW = Mathf.Clamp(g.resizeStartRect.width + dx, STICK_GRAPH_MIN_WIDTH, maxW);
                    if ((g.resizeEdge & STICK_GRAPH_EDGE_BOTTOM) != 0)
                        newH = Mathf.Clamp(g.resizeStartRect.height + dy, STICK_GRAPH_MIN_HEIGHT, maxH);
                    if ((g.resizeEdge & STICK_GRAPH_EDGE_LEFT) != 0)
                    {
                        newW = Mathf.Clamp(g.resizeStartRect.width - dx, STICK_GRAPH_MIN_WIDTH, maxW);
                        newX = g.resizeStartRect.x + (g.resizeStartRect.width - newW);
                    }
                    if ((g.resizeEdge & STICK_GRAPH_EDGE_TOP) != 0)
                    {
                        newH = Mathf.Clamp(g.resizeStartRect.height - dy, STICK_GRAPH_MIN_HEIGHT, maxH);
                        newY = g.resizeStartRect.y + (g.resizeStartRect.height - newH);
                    }
                    g.rect = new Rect(newX, newY, newW, newH);
                    EnsureStickGraphRect(g, false);
                    e.Use();
                }
                if (g.resizing && e.type == EventType.MouseUp && e.button == 0)
                {
                    g.resizing = false;
                    g.resizeEdge = 0;
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
                if (!covered && e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) &&
                    !close.Contains(e.mousePosition) && !pin.Contains(e.mousePosition) && !ddBtn.Contains(e.mousePosition) &&
                    !plusBtn.Contains(e.mousePosition) && !heatBtn.Contains(e.mousePosition) &&
                    !inDropdownList && !inPilotList && g.edgeHover == 0)
                {
                    g.dragCandidate = true;
                    g.dragOffset = e.mousePosition - new Vector2(panel.x, panel.y);
                    stickGraphActivated = g;
                }
                if (g.dragging && e.type == EventType.MouseDrag && e.button == 0)
                {
                    g.rect.x = e.mousePosition.x - g.dragOffset.x;
                    g.rect.y = e.mousePosition.y - g.dragOffset.y;
                    EnsureStickGraphRect(g, false);
                    e.Use();
                }
                if (g.dragging && e.type == EventType.MouseUp && e.button == 0)
                {
                    g.dragging = false;
                    e.Use();
                }

                string pilotLabel = string.IsNullOrEmpty(g.pilot.pilotName) ? "Pilot" : g.pilot.pilotName;
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
                    g.dragCandidate = false;
                    stickGraphActivated = g;
                    settingsControlPressed = true;
                    e.Use();
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
                    StickGraphState dup = new StickGraphState();
                    dup.pilot = g.pilot;
                    dup.channel = g.channel;
                    dup.heatmap = g.heatmap;
                    dup.fadeAmount = 1f;
                    dup.rect = new Rect(g.rect.x + STICK_GRAPH_OPEN_OFFSET, g.rect.y + STICK_GRAPH_OPEN_OFFSET, g.rect.width, g.rect.height);
                    stickGraphs.Add(dup);
                    g.pilotDropdown = false;
                    g.channelDropdown = false;
                    e.Use();
                }

                bool heatHover = heatBtn.Contains(e.mousePosition);
                GUI.color = g.heatmap
                    ? new Color(0.25f, 0.62f, 0.95f, 0.95f * alpha)
                    : heatHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
                GUI.DrawTexture(heatBtn, whiteTex);
                GUI.color = ThemeButtonBorder(alpha);
                GUI.DrawTexture(new Rect(heatBtn.x, heatBtn.y, heatBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(heatBtn.x, heatBtn.yMax - 1f, heatBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(heatBtn.x, heatBtn.y, 1f, heatBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(heatBtn.xMax - 1f, heatBtn.y, 1f, heatBtn.height), whiteTex);
                float stripW = 3f;
                float stripStartX = heatBtn.x + (heatBtn.width - 4f * stripW) * 0.5f;
                float stripY = heatBtn.y + 3f;
                float stripH = heatBtn.height - 6f;
                GUI.color = new Color(0.05f, 1f, 0.12f, alpha);
                GUI.DrawTexture(new Rect(stripStartX, stripY, stripW, stripH), whiteTex);
                GUI.color = new Color(1f, 0.95f, 0.02f, alpha);
                GUI.DrawTexture(new Rect(stripStartX + stripW, stripY, stripW, stripH), whiteTex);
                GUI.color = new Color(1f, 0.35f, 0.02f, alpha);
                GUI.DrawTexture(new Rect(stripStartX + 2f * stripW, stripY, stripW, stripH), whiteTex);
                GUI.color = new Color(1f, 0.02f, 0.06f, alpha);
                GUI.DrawTexture(new Rect(stripStartX + 3f * stripW, stripY, stripW, stripH), whiteTex);
                if (!covered && e.type == EventType.MouseDown && e.button == 0 && heatBtn.Contains(e.mousePosition))
                {
                    g.heatmap = !g.heatmap;
                    g.dragCandidate = false;
                    stickGraphActivated = g;
                    settingsControlPressed = true;
                    e.Use();
                }

                if (DrawStyledToggleButton(pin, iconPin, g.pinned, alpha, !covered))
                {
                    g.pinned = !g.pinned;
                    stickGraphActivated = g;
                }
                GUI.color = ThemeCloseLabel(alpha);
                GUI.Label(close, "x", settingsCloseStyle);
                if (!covered && e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
                {
                    if (g.traceTex != null)
                    {
                        Destroy(g.traceTex);
                        g.traceTex = null;
                    }
                    stickGraphs.RemoveAt(gi);
                    gi--;
                    e.Use();
                    GUI.color = old;
                    continue;
                }

                float[] times = g.pilot.stateTimes;
                float lastStateTime = times[times.Length - 1];
                float lapStart = Mathf.Clamp(GetGateTimestamp(g.pilot), times[0], lastStateTime);
                float lapEnd = lastStateTime;
                float lapDuration = Mathf.Max(lapEnd - lapStart, 0.001f);

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

                Color signalColor = darkMode
                    ? Color.Lerp(g.pilot.color, new Color(1f, 1f, 1f), 0.25f)
                    : Color.Lerp(g.pilot.color, new Color(0f, 0f, 0f), 0.4f);
                int tw = Mathf.Max(1, (int)plot.width);
                int th = Mathf.Max(1, (int)plot.height);
                if (g.traceCachePilot != g.pilot || g.traceCacheChannel != g.channel ||
                    g.traceCachePlotW != tw || g.traceCachePlotH != th || g.traceCacheHeatmap != g.heatmap ||
                    g.traceCacheInputs != g.pilot.originalInputs ||
                    g.traceCacheLapStart != lapStart || g.traceCacheLapDuration != lapDuration)
                {
                    if (g.traceTex != null)
                    {
                        Destroy(g.traceTex);
                        g.traceTex = null;
                    }
                    Color[] pixels = new Color[tw * th];
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = Color.clear;
                    float dt = Mathf.Max(lapDuration / tw, 0.0001f);
                    float heatRef = 8f;
                    float[] rates = null;
                    if (g.heatmap)
                    {
                        rates = new float[tw];
                        float prevVal = SampleStickChannel(g.pilot, lapStart, g.channel);
                        for (int i = 0; i < tw; i++)
                        {
                            float f = (i + 1) / (float)tw;
                            float value = SampleStickChannel(g.pilot, lapStart + f * lapDuration, g.channel);
                            rates[i] = Mathf.Abs(value - prevVal) / dt;
                            prevVal = value;
                        }
                        float[] sorted = new float[tw];
                        System.Array.Copy(rates, sorted, tw);
                        System.Array.Sort(sorted);
                        heatRef = sorted[Mathf.Min(tw - 1, (int)(tw * 0.85f))];
                        heatRef = Mathf.Max(heatRef, 0.0001f);
                    }
                    float prevValue = SampleStickChannel(g.pilot, lapStart, g.channel);
                    int prevPy = Mathf.Clamp(
                        (int)((1f - (prevValue + 1f) * 0.5f) * th), 0, th - 1);
                    for (int i = 0; i < tw; i++)
                    {
                        float f = (i + 1) / (float)tw;
                        float stateTime = lapStart + f * lapDuration;
                        float value = SampleStickChannel(g.pilot, stateTime, g.channel);
                        int py = Mathf.Clamp((int)((1f - (value + 1f) * 0.5f) * th), 0, th - 1);
                        Color col = signalColor;
                        if (g.heatmap)
                        {
                            float t = Mathf.Clamp01(rates[i] / heatRef);
                            col = GhostLinesPlugin.SpeedHeatmapColor(t);
                        }
                        int yMin = Mathf.Max(0, Mathf.Min(prevPy, py) - 3);
                        int yMax = Mathf.Min(th - 1, Mathf.Max(prevPy, py) + 3);
                        for (int ry = yMin; ry <= yMax; ry++)
                            pixels[(th - 1 - ry) * tw + i] = col;
                        prevPy = py;
                        prevValue = value;
                    }
                    g.traceTex = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                    g.traceTex.filterMode = FilterMode.Point;
                    g.traceTex.SetPixels(pixels);
                    g.traceTex.Apply();
                    g.traceCachePilot = g.pilot;
                    g.traceCacheChannel = g.channel;
                    g.traceCachePlotW = tw;
                    g.traceCachePlotH = th;
                    g.traceCacheHeatmap = g.heatmap;
                    g.traceCacheInputs = g.pilot.originalInputs;
                    g.traceCacheLapStart = lapStart;
                    g.traceCacheLapDuration = lapDuration;
                }
                GUI.color = new Color(1f, 1f, 1f, alpha);
                Rect traceDraw = new Rect(Mathf.Round(plot.x), Mathf.Round(plot.y), tw, th);
                GUI.DrawTexture(traceDraw, g.traceTex);

                float replayTime;
                if (TryGetReplayTime(out replayTime))
                {
                    float queryT = GetGateQueryTimestamp(g.pilot, replayTime);
                    if (queryT >= lapStart && queryT <= lapEnd)
                    {
                        float playheadX = plot.x + (queryT - lapStart) / lapDuration * plot.width;
                        Color playheadColor = darkMode
                            ? new Color(1f, 1f, 1f, 0.85f * alpha)
                            : new Color(0.12f, 0.12f, 0.12f, 0.85f * alpha);
                        GUI.color = playheadColor;
                        GUI.DrawTexture(new Rect(playheadX, plot.y, 1f, plot.height), whiteTex);
                        GUI.color = new Color(playheadColor.r, playheadColor.g, playheadColor.b, 0.4f * alpha);
                        GUI.DrawTexture(new Rect(playheadX + 1f, plot.y, 1f, plot.height), whiteTex);
                    }
                }

                GUI.color = ThemeDimLabel(alpha);
                GUI.Label(new Rect(timeLblRow.x, timeLblRow.y, 60f, timeLblRow.height), "0.0s", settingsSmallStyle);
                GUI.Label(new Rect(timeLblRow.x + timeLblRow.width * 0.5f - 30f, timeLblRow.y, 60f, timeLblRow.height),
                    (lapDuration * 0.5f).ToString("0.0") + "s", settingsSmallCenterStyle);
                GUI.Label(new Rect(timeLblRow.xMax - 60f, timeLblRow.y, 60f, timeLblRow.height),
                    lapDuration.ToString("0.0") + "s", settingsSmallStyle);

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
                    g.dragCandidate = false;
                    stickGraphActivated = g;
                    settingsControlPressed = true;
                    e.Use();
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
                        GhostLineEntry optPilot = stickGraphPilotOptions[i];
                        string optName = string.IsNullOrEmpty(optPilot.pilotName) ? "Pilot" : optPilot.pilotName;
                        bool optHover = optRect.Contains(e.mousePosition);
                        bool optSelected = optPilot == g.pilot;
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
                            g.fadeAmount = 1f;
                            g.pilotDropdown = false;
                            settingsControlPressed = false;
                            e.Use();
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

            float refDist = GetDistanceAlongPath(refEntry, replayTime);
            float refLapDist = Mathf.Max(0f, refDist - refEntry.lapStartDistance);

            foreach (var entry in GhostLinesPlugin.entries)
            {
                if (entry.deltaText == null) continue;

                if (entry == refEntry)
{
                    float shown = Mathf.Max(0f, replayTime);
                    if (entry.finishTime > 0f && shown > entry.finishTime)
                        shown = entry.finishTime;
                    if (Mathf.Abs(shown - entry.lastDeltaShownValue) >= 0.0005f || entry.lastShownDeltaText == "")
                    {
                        string timeText = shown.ToString("F3");
                        entry.lastDeltaShownValue = shown;
                        entry.lastShownDeltaText = timeText;
                        SetDeltaText(entry, timeText, Color.white, GhostLinesPlugin.showDeltaDisplay);
                    }
                    continue;
                }

                if (entry.pathDistances == null || entry.pathDistances.Length == 0 || entry.lapTotalDistance <= 0f)
                {
                    if (entry.guiDeltaText != "")
                        SetDeltaText(entry, "", Color.white, false);
                    continue;
                }

                float entryDist = GetDistanceAlongPath(entry, replayTime);
                float entryLapDist = Mathf.Max(0f, entryDist - entry.lapStartDistance);

                float delta;
                if (refLapDist <= 0f || entryLapDist <= 0f)
                {
                    delta = 0f;
                }
                else
                {
                    float avgProgress = (refLapDist / refEntry.lapTotalDistance + entryLapDist / entry.lapTotalDistance) * 0.5f;
                    float refTime = GetTimeAtDistance(refEntry, avgProgress * refEntry.lapTotalDistance + refEntry.lapStartDistance) - refEntry.lapStartOffset;
                    float entryTime = GetTimeAtDistance(entry, avgProgress * entry.lapTotalDistance + entry.lapStartDistance) - entry.lapStartOffset;
                    delta = entryTime - refTime;
                }

                if (Mathf.Abs(delta - entry.lastDeltaShownValue) < 0.0005f && entry.lastShownDeltaText != null)
                    continue;
                entry.lastDeltaShownValue = delta;
                string sign = delta > 0f ? "+" : "";
                string text = sign + delta.ToString("F3");
                entry.lastShownDeltaText = text;
                if (entry.guiDeltaText == text)
                    continue;
                Color deltaColor = delta > 0f
                    ? new Color(1f, 0.15f, 0.15f, 1f)
                    : new Color(0.2f, 1f, 0.25f, 1f);
                SetDeltaText(entry, text, deltaColor, GhostLinesPlugin.showDeltaDisplay);
            }
        }

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

        private bool TryGetReplayTime(out float replayTime)
        {
            replayTime = 0f;

            if (replayController != null && timeProp != null)
            {
                try
                {
                    replayTime = (float)timeProp.GetValue(replayController, null);
                    // The plugin clock is elapsed time from each pilot's first gate.
                    return true;
                }
                catch { }
            }

            if (sliderComponent != null && sliderValueProp != null)
            {
                try
                {
                    replayTime = (float)sliderValueProp.GetValue(sliderComponent, null);
                    return true;
                }
                catch { }
            }

            return false;
        }

        private float GetEffectiveReplayEnd(float nativeMaximum)
        {
            if (nativeMaximum <= 0f)
                return nativeMaximum;

            float latestFinish = 0f;
            bool hasValidFinish = false;
            foreach (var entry in GhostLinesPlugin.entries)
            {
                if (entry == null)
                    continue;
                if (!entry.built)
                    return nativeMaximum;
                if (entry.finishTime > 0f && !float.IsNaN(entry.finishTime) && !float.IsInfinity(entry.finishTime))
                {
                    latestFinish = Mathf.Max(latestFinish, entry.finishTime);
                    hasValidFinish = true;
                }
            }

            return hasValidFinish ? Mathf.Min(nativeMaximum, latestFinish) : nativeMaximum;
        }

        private float GetLoopReplayEnd(float nativeMaximum)
        {
            if (spectatedEntry != null && spectatedEntry.built && spectatedEntry.finishTime > 0f &&
                !float.IsNaN(spectatedEntry.finishTime) && !float.IsInfinity(spectatedEntry.finishTime))
                return Mathf.Min(nativeMaximum, spectatedEntry.finishTime);
            return GetEffectiveReplayEnd(nativeMaximum);
        }

        private void RequestLoopedReplayAtEnd()
        {
            if (!loopReplayEnabled || !isPlaying || restartRequested || restartPlaybackPending)
                return;

            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;

            float loopEnd = GetLoopReplayEnd(GetNativeReplayMaximum(replayTime));
            if (loopEnd > 0f && replayTime >= loopEnd - 0.01f)
            {
                restartRequested = true;
                Debug.Log("[GhostLines] Loop replay restart requested");
            }
        }

        private float GetNativeReplayMaximum(float fallback)
        {
            if (sliderComponent != null && sliderValueProp != null)
            {
                try
                {
                    var maxProp = GetSliderMaxValueProp();
                    if (maxProp != null)
                        return (float)maxProp.GetValue(sliderComponent, null);
                }
                catch { }
            }

            if (replayController != null)
            {
                try
                {
                    var maxProp = GetControllerMaxTimeProp();
                    if (maxProp != null)
                        return (float)maxProp.GetValue(replayController, null);
                }
                catch { }
            }

            return fallback;
        }

        private System.Reflection.PropertyInfo GetSliderMinValueProp()
        {
            if (sliderMinValuePropInfo == null && sliderComponent != null)
                sliderMinValuePropInfo = sliderComponent.GetType().GetProperty("minValue", typeof(float));
            return sliderMinValuePropInfo;
        }

        private System.Reflection.PropertyInfo GetSliderMaxValueProp()
        {
            if (sliderMaxValuePropInfo == null && sliderComponent != null)
                sliderMaxValuePropInfo = sliderComponent.GetType().GetProperty("maxValue", typeof(float));
            return sliderMaxValuePropInfo;
        }

        private System.Reflection.PropertyInfo GetControllerMaxTimeProp()
        {
            if (controllerMaxTimePropInfo == null && replayController != null)
                controllerMaxTimePropInfo = replayController.GetType().GetProperty("MaximumTimelineTime", typeof(float));
            return controllerMaxTimePropInfo;
        }

        private int GetPilotNumberKey()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) return 1;
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) return 2;
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) return 3;
            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) return 4;
            if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5)) return 5;
            return 0;
        }

        private void StepReplayFrame(int direction)
        {
            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;
            SetReplayTime(Mathf.Max(0f, replayTime + direction * FRAME_STEP_SECONDS));
            frameStepPaused = true;
        }

        private void HandleFrameStepInput()
        {
            bool stepBack = Input.GetKey(KeyCode.LeftArrow);
            bool stepForward = Input.GetKey(KeyCode.RightArrow);
            int direction = stepBack ? -1 : stepForward ? 1 : 0;
            if (direction == 0)
            {
                frameStepHeldDirection = 0;
                return;
            }

            bool newlyPressed = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow);
            if (newlyPressed || direction != frameStepHeldDirection)
            {
                StepReplayFrame(direction);
                frameStepHeldDirection = direction;
                frameStepNextRepeat = Time.unscaledTime + FRAME_STEP_INITIAL_DELAY;
                return;
            }

            if (Time.unscaledTime >= frameStepNextRepeat)
            {
                StepReplayFrame(direction);
                frameStepNextRepeat = Time.unscaledTime + FRAME_STEP_REPEAT_INTERVAL;
            }
        }

        private void SetReplayTime(float replayTime, bool preservePlaybackSpeed = false)
        {
            float nativeMaximum = GetNativeReplayMaximum(replayTime);
            replayTime = Mathf.Min(replayTime, GetEffectiveReplayEnd(nativeMaximum));
            overridePlayback = false;
            if (!preservePlaybackSpeed)
                playbackSpeed = 1f;
            isPlaying = false;
            SetNativeReplayPlaying(false);

            if (sliderComponent != null && sliderValueProp != null && sliderValueProp.CanWrite)
            {
                try
                {
                    var minProp = GetSliderMinValueProp();
                    var maxProp = GetSliderMaxValueProp();
                    float min = minProp != null ? (float)minProp.GetValue(sliderComponent, null) : 0f;
                    float max = maxProp != null ? (float)maxProp.GetValue(sliderComponent, null) : replayTime;
                    sliderValueProp.SetValue(sliderComponent, Mathf.Clamp(replayTime, min, max), null);
                    return;
                }
                catch { }
            }

            if (replayController != null && timeProp != null && timeProp.CanWrite)
            {
                try { timeProp.SetValue(replayController, replayTime, null); } catch { }
            }
        }

        private void SeekReplayTimeKeepingPlayback(float replayTime)
        {
            if (sliderComponent != null && sliderValueProp != null && sliderValueProp.CanWrite)
            {
                try
                {
                    var minProp = GetSliderMinValueProp();
                    var maxProp = GetSliderMaxValueProp();
                    float min = minProp != null ? (float)minProp.GetValue(sliderComponent, null) : 0f;
                    float max = maxProp != null ? (float)maxProp.GetValue(sliderComponent, null) : replayTime;
                    sliderValueProp.SetValue(sliderComponent, Mathf.Clamp(replayTime, min, max), null);
                }
                catch { }
            }
            else if (replayController != null && timeProp != null && timeProp.CanWrite)
            {
                try { timeProp.SetValue(replayController, replayTime, null); } catch { }
            }

            if (!resumePlaybackAfterSeek)
                return;

            if (!overridePlayback)
                SetNativeReplayPlaying(true);
            isPlaying = true;
        }

        private void CacheAttachToDroneMethod()
        {
            if (flightCamCtrl == null || attachToDroneMethod != null)
                return;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            foreach (var method in flightCamCtrl.GetType().GetMethods(flags))
            {
                if (method.Name != "AttachToDrone")
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1)
                {
                    attachToDroneMethod = method;
                    attachToDroneParameterType = parameters[0].ParameterType;
                    return;
                }
            }
        }

        private void TrySpectatePilot(int index, bool preserveCameraView = false)
        {
            if (index < 0 || index >= GhostLinesPlugin.entries.Count)
                return;

            GhostLineEntry entry = GhostLinesPlugin.entries[index];
            Transform root;
            if (!ghostDroneMap.TryGetValue(entry, out root) || root == null)
            {
                MatchGhostDrones();
                if (!ghostDroneMap.TryGetValue(entry, out root) || root == null)
                {
                    Debug.Log($"[GhostLines] Pilot {index + 1} drone is not ready");
                    return;
                }
            }

            if (flightCamCtrl == null)
                TryInitCamera();
            CacheAttachToDroneMethod();
            if (attachToDroneMethod == null || attachToDroneParameterType == null)
            {
                Debug.Log("[GhostLines] Native drone attach method not found");
                return;
            }

            object drone = null;
            for (Transform current = root; current != null && drone == null; current = current.parent)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    drone = FindNativeDroneData(component);
                    if (drone != null) break;
                }
            }
            if (drone == null)
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    drone = FindNativeDroneData(component);
                    if (drone != null) break;
                }
            }
            if (drone == null)
            {
                Debug.Log($"[GhostLines] Pilot {index + 1} native drone component not found");
                return;
            }

            bool preserveFreeCamView = preserveCameraView && freeCamActive;
            bool preserveOrbitView = preserveCameraView && customCameraActive && !freeCamActive;
            bool zoomGlideFreeCam = preserveFreeCamView && holdZoomFovCaptured;
            Vector3 savedFreePosition = freeCamPosition;
            float savedFreeYaw = freeCamYaw;
            float savedFreePitch = freeCamPitch;
            float savedOrbitDist = orbitDist;
            float savedOrbitYaw = orbitYaw;
            float savedOrbitPitch = orbitPitch;
            bool savedOrbiting = isOrbiting;

            try
            {
                attachToDroneMethod.Invoke(flightCamCtrl, new object[] { drone });
                spectatedEntry = entry;
                if (holdZoomFovCaptured && freeCamActive)
                {
                    zoomTransitionUntil = Time.time + ZOOM_TRANSITION_DURATION;
                    zoomTransitionStartYaw = freeCamYaw;
                    zoomTransitionStartPitch = freeCamPitch;
                }
                if (!zoomGlideFreeCam && (preserveFreeCamView || preserveOrbitView))
                {
                    pendingCameraViewRestore = true;
                    pendingFreeCamViewRestore = preserveFreeCamView;
                    pendingOrbitViewRestore = preserveOrbitView;
                    pendingFreeCamPosition = savedFreePosition;
                    pendingFreeCamYaw = savedFreeYaw;
                    pendingFreeCamPitch = savedFreePitch;
                    pendingOrbitDist = savedOrbitDist;
                    pendingOrbitYaw = savedOrbitYaw;
                    pendingOrbitPitch = savedOrbitPitch;
                    pendingOrbiting = savedOrbiting;
                }
                Debug.Log($"[GhostLines] Spectating pilot {index + 1}: {entry.pilotName}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GhostLines] Could not spectate pilot {index + 1}: {ex.Message}");
            }
        }

        private object FindNativeDroneData(Component component)
        {
            if (component == null || attachToDroneParameterType == null)
                return null;
            if (attachToDroneParameterType.IsInstanceOfType(component))
                return component;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            System.Type type = component.GetType();

            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType != attachToDroneParameterType)
                    continue;
                try
                {
                    object value = field.GetValue(component);
                    if (value != null) return value;
                }
                catch { }
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (property.PropertyType != attachToDroneParameterType || !property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                try
                {
                    object value = property.GetValue(component, null);
                    if (value != null) return value;
                }
                catch { }
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.ReturnType != attachToDroneParameterType || method.GetParameters().Length != 0)
                    continue;
                if (!method.Name.StartsWith("get_", System.StringComparison.Ordinal)
                    && method.Name.IndexOf("Drone", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    object value = method.Invoke(component, null);
                    if (value != null) return value;
                }
                catch { }
            }
            return null;
        }

        private bool IsReplayFinished()
        {
            if (sliderComponent != null && sliderValueProp != null)
            {
                try
                {
                    float cur = (float)sliderValueProp.GetValue(sliderComponent, null);
                    var maxProp = GetSliderMaxValueProp();
                    float maxV = maxProp != null ? (float)maxProp.GetValue(sliderComponent, null) : 1f;
                    maxV = GetEffectiveReplayEnd(maxV);
                    return cur >= maxV - 0.01f;
                }
                catch { }
            }
            if (replayController != null && timeProp != null)
            {
                try
                {
                    float cur = (float)timeProp.GetValue(replayController, null);
                    var maxProp = GetControllerMaxTimeProp();
                    float maxV = maxProp != null ? (float)maxProp.GetValue(replayController, null) : 1f;
                    maxV = GetEffectiveReplayEnd(maxV);
                    return cur >= maxV - 0.01f;
                }
                catch { }
            }
            return false;
        }

        private void EnforceEffectiveReplayEnd()
        {
            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;

            float nativeMaximum = GetNativeReplayMaximum(replayTime);
            float replayEnd = GetEffectiveReplayEnd(nativeMaximum);
            if (replayEnd <= 0f || replayEnd >= nativeMaximum - 0.01f || replayTime < replayEnd - 0.01f)
                return;

            SetReplayTime(replayEnd, true);
        }

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

            if (entry.stateTimes == null || entry.originalPositions == null || entry.stateTimes.Length == 0 ||
                entry.stateTimes.Length != entry.originalPositions.Length)
                return false;

            float queryT = GetGateQueryTimestamp(entry, replayTime);

            if (entry.stateTimes.Length == 1)
            {
                position = entry.originalPositions[0];
                return true;
            }

            int hi = FindFirstStateAtOrAfter(entry.stateTimes, queryT);

            if (hi <= 0)
            {
                position = entry.originalPositions[0];
                return true;
            }

            if (hi >= entry.stateTimes.Length)
            {
                position = entry.originalPositions[entry.originalPositions.Length - 1];
                return true;
            }

            int lo = hi - 1;
            float t0 = entry.stateTimes[lo];
            float t1 = entry.stateTimes[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, queryT);
            position = Vector3.Lerp(entry.originalPositions[lo], entry.originalPositions[hi], u);
            return true;
        }

        private bool TryGetDroneTransform(GhostLineEntry entry, float replayTime, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (entry.stateTimes == null || entry.originalPositions == null || entry.originalRotations == null ||
                entry.stateTimes.Length == 0 || entry.stateTimes.Length != entry.originalPositions.Length ||
                entry.stateTimes.Length != entry.originalRotations.Length)
                return false;
            float queryT = GetGateQueryTimestamp(entry, replayTime);
            if (entry.stateTimes.Length == 1)
            {
                pos = entry.originalPositions[0]; rot = entry.originalRotations[0];
                return true;
            }
            int hi = FindFirstStateAtOrAfter(entry.stateTimes, queryT);
            if (hi <= 0) { pos = entry.originalPositions[0]; rot = entry.originalRotations[0]; return true; }
            if (hi >= entry.stateTimes.Length)
            { pos = entry.originalPositions[entry.originalPositions.Length - 1]; rot = entry.originalRotations[entry.originalRotations.Length - 1]; return true; }
            int lo = hi - 1;
            float t0 = entry.stateTimes[lo], t1 = entry.stateTimes[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, queryT);
            pos = Vector3.LerpUnclamped(entry.originalPositions[lo], entry.originalPositions[hi], u);
            rot = Quaternion.SlerpUnclamped(entry.originalRotations[lo], entry.originalRotations[hi], u);
            return true;
        }

        private bool TryGetDroneTransformUnshifted(GhostLineEntry entry, float replayTime, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (entry.stateTimes == null || entry.originalPositions == null || entry.originalRotations == null ||
                entry.stateTimes.Length == 0 || entry.stateTimes.Length != entry.originalPositions.Length ||
                entry.stateTimes.Length != entry.originalRotations.Length)
                return false;
            float firstT = entry.stateTimes[0];
            float lastT = entry.stateTimes[entry.stateTimes.Length - 1];
            float queryT = Mathf.Clamp(replayTime + firstT, firstT, lastT);
            if (entry.stateTimes.Length == 1)
            {
                pos = entry.originalPositions[0]; rot = entry.originalRotations[0];
                return true;
            }
            int hi = FindFirstStateAtOrAfter(entry.stateTimes, queryT);
            if (hi <= 0) { pos = entry.originalPositions[0]; rot = entry.originalRotations[0]; return true; }
            if (hi >= entry.stateTimes.Length)
            { pos = entry.originalPositions[entry.originalPositions.Length - 1]; rot = entry.originalRotations[entry.originalRotations.Length - 1]; return true; }
            int lo = hi - 1;
            float t0 = entry.stateTimes[lo], t1 = entry.stateTimes[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, queryT);
            pos = Vector3.LerpUnclamped(entry.originalPositions[lo], entry.originalPositions[hi], u);
            rot = Quaternion.SlerpUnclamped(entry.originalRotations[lo], entry.originalRotations[hi], u);
            return true;
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

        private void MatchGhostDrones()
        {
            bool firstPass = ghostMatchAttempts == 0;
            var drones = FindObjectsOfType<GameObject>(true);
            System.Collections.Generic.List<GameObject> ghostList = new System.Collections.Generic.List<GameObject>();
            foreach (var go in drones)
            {
                if (!IsGhostDroneRoot(go.transform))
                    continue;
                if (go.GetComponentsInChildren<Renderer>(true).Length > 0)
                    ghostList.Add(go);
            }

            if (ghostList.Count == 0)
                return;

            if (firstPass)
            {
                Debug.Log($"[GhostLines] Found {ghostList.Count} candidate ghost drones");
                foreach (var go in ghostList)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.Append("[GhostLines] Ghost " + go.name + " pos=" + go.transform.position + " comps:");
                    foreach (var c in go.GetComponents<Component>())
                        sb.Append(" " + c.GetType().Name);
                    Debug.Log(sb.ToString());
                }
            }

            float rt;
            TryGetReplayTime(out rt);
            var matchedEntries = new System.Collections.Generic.HashSet<GhostLineEntry>(ghostDroneMap.Keys);

            foreach (var go in ghostList)
            {
                if (ghostDroneMap.ContainsValue(go.transform))
                    continue;

                Vector3 dronePos = go.transform.position;
                GhostLineEntry bestEntry = null;
                float bestDist = float.MaxValue;
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    if (matchedEntries.Contains(entry))
                        continue;
                    Vector3 ePos; Quaternion eRot;
                    if (TryGetDroneTransformUnshifted(entry, rt, out ePos, out eRot))
                    {
                        float d = Vector3.Distance(dronePos, ePos);
                        if (d < bestDist) { bestDist = d; bestEntry = entry; }
                    }
                }
                if (bestEntry != null && bestDist < 10f)
                {
                    matchedEntries.Add(bestEntry);
                    ghostDroneMap[bestEntry] = go.transform;
                    ghostCamMountMap[bestEntry] = FindCameraMountTransform(go.transform);
                    CacheGhostTrails(bestEntry, go.transform);
                    SetGhostTrailsSuppressed(bestEntry, true);
                    Debug.Log($"[GhostLines] Matched {go.name} -> {bestEntry.pilotName} d={bestDist:F3} camMount={(ghostCamMountMap[bestEntry] != null ? ghostCamMountMap[bestEntry].name : "NONE")}");
                }
                else if (bestEntry != null && firstPass)
                {
                    Debug.Log($"[GhostLines] Ghost drone {go.name} best dist {bestDist:F3} > 10, no match");
                }
            }
        }

        private bool IsGhostDroneRoot(Transform candidate)
        {
            if (candidate == null)
                return false;

            string name = candidate.name;
            bool namedDrone = name.IndexOf("Fixation", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Frame01", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!namedDrone)
                return false;

            for (Transform parent = candidate.parent; parent != null; parent = parent.parent)
            {
                string parentName = parent.name;
                if (parentName.IndexOf("Fixation", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || parentName.IndexOf("Frame01", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }
            return true;
        }

        private Transform FindCameraMountTransform(Transform droneRoot)
        {
            if (droneRoot == null) return null;
            var children = droneRoot.GetComponentsInChildren<Transform>(true);
            System.Text.StringBuilder dump = new System.Text.StringBuilder();
            dump.Append("[GhostLines] Child transforms under " + droneRoot.name + ":");
            Transform slotWithRot = null;
            Transform parentPoint = null;
            Transform cameraNano = null;
            foreach (var t in children)
            {
                if (t == droneRoot) continue;
                dump.Append(" " + t.name + ";");
                string lname = t.name.ToLowerInvariant();
                if (lname.Contains("redot") || lname.Contains("nano"))
                {
                    if (cameraNano == null) cameraNano = t;
                }
                if (lname.Contains("cameraparentpoint") || lname.Contains("parentpoint"))
                {
                    if (parentPoint == null) parentPoint = t;
                }
                if (lname.Contains("fpvcam") || lname.Contains("fpv_cam") || lname.Contains("camera") || lname.Contains("cammount") || lname.Contains("cam_mount") || lname.Contains("gopro") || lname.Contains("lens") || lname.Contains("fpv"))
                {
                    if (slotWithRot == null) slotWithRot = t;
                }
            }
            Transform best = parentPoint ?? cameraNano ?? slotWithRot;
            if (cameraMountLoggedRoots.Add(droneRoot))
            {
                Debug.Log(dump.ToString());
                if (best != null)
                    Debug.Log($"[GhostLines] Selected camera mount candidate: {best.name} (local rot={best.localRotation.eulerAngles})");
                else
                    Debug.Log($"[GhostLines] No camera mount transform found by keyword under {droneRoot.name}");
            }
            return best;
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

        private void FindSliderReflection()
        {
            Transform sliderT = null;
            Transform vlayout = replayControlsObj.transform.Find("VLayout");
            if (vlayout != null)
            {
                Transform timeline = vlayout.Find("Timeline");
                if (timeline != null)
                {
                    Transform slider = timeline.Find("Slider");
                    if (slider != null)
                        sliderT = slider.Find("TimelineSlider");
                }
            }

            if (sliderT == null)
            {
                var all = replayControlsObj.GetComponentsInChildren<Transform>(true);
                foreach (var tr in all)
                    if (tr.name == "TimelineSlider") { sliderT = tr; break; }
            }

            if (sliderT != null)
            {
                foreach (var comp in sliderT.GetComponents<Component>())
                {
                    var t = comp.GetType();
                    var prop = t.GetProperty("value", typeof(float));
                    if (prop != null && prop.CanRead && prop.CanWrite)
                    {
                        sliderComponent = comp;
                        sliderValueProp = prop;
                        Debug.Log($"[GhostLines] Found slider on {t.Name}");
                        return;
                    }
                }
                Debug.Log("[GhostLines] TimelineSlider found but no float 'value' property");
            }

            Debug.Log("[GhostLines] Named slider search failed, sweeping all components...");
            var allComps = replayControlsObj.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComps)
            {
                var t = comp.GetType();
                if (t == typeof(Transform) || t == typeof(RectTransform)) continue;
                var prop = t.GetProperty("value", typeof(float));
                if (prop != null && prop.CanRead && prop.CanWrite)
                {
                    sliderComponent = comp;
                    sliderValueProp = prop;
                    Debug.Log($"[GhostLines] Found slider via sweep: {t.Name} on {comp.gameObject.name}");
                    return;
                }
            }
            Debug.Log("[GhostLines] No component with float 'value' found in ReplayControlsUI");
        }

        private void OnGUI()
        {
            if (!replayReady)
                return;

            if (whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1);
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
            }

            UpdateOverlayResolution();
            currentPlaybackOverlayRectValid = false;
            currentStickCamOverlayRectValid = false;
            DrawStickCam();
            DrawStickGraph();
            DrawBottomScrubber();
            DrawSettingsPanel();
            DrawBindingsWindow();
            HandleDroneLabelClick();
            GUI.color = Color.white;
        }

        private void HandleDroneLabelClick()
        {
            if (!GhostLinesPlugin.showNameTags || (!freeCamActive && !customCameraActive) || droneLabelHitboxes.Count == 0)
            {
                droneLabelClickCandidate = null;
                SetHoveredDroneLabel(null);
                return;
            }

            Event e = Event.current;
            if (e == null)
                return;

            bool blocked = IsDroneLabelInteractionBlocked(e.mousePosition);
            SetHoveredDroneLabel(blocked ? null : FindHoveredDroneLabel(e.mousePosition));
            if (e.button != 0)
                return;

            if (e.type == EventType.MouseDown)
            {
                droneLabelClickCandidate = null;
                if (blocked || hoveredDroneLabelEntry == null)
                    return;

                droneLabelClickCandidate = hoveredDroneLabelEntry;
                droneLabelClickStart = e.mousePosition;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && droneLabelClickCandidate != null)
            {
                if ((e.mousePosition - droneLabelClickStart).sqrMagnitude > 64f)
                    droneLabelClickCandidate = null;
            }
            else if (e.type == EventType.MouseUp && droneLabelClickCandidate != null)
            {
                GhostLineEntry target = droneLabelClickCandidate;
                droneLabelClickCandidate = null;
                if ((e.mousePosition - droneLabelClickStart).sqrMagnitude <= 64f)
                {
                    int index = GhostLinesPlugin.entries.IndexOf(target);
                    if (index >= 0)
                        TrySpectatePilot(index, true);
                    e.Use();
                }
            }
        }

        private bool IsDroneLabelInteractionBlocked(Vector2 mousePosition)
        {
            bool stickGraphBusy = false;
            bool stickGraphHovered = false;
            for (int i = 0; i < stickGraphs.Count; i++)
            {
                StickGraphState g = stickGraphs[i];
                if (g.dragging || g.dragCandidate || g.resizing)
                    stickGraphBusy = true;
                if (g.rect.Contains(mousePosition))
                    stickGraphHovered = true;
            }
            return playbackMouseCaptured || draggingScrubber || isDraggingHandle || draggingStickCam ||
                settingsDragging || stickSizeDragging || uiSizeDragging ||
                stickGraphBusy ||
                (currentPlaybackOverlayRectValid && currentPlaybackOverlayRect.Contains(mousePosition)) ||
                (currentStickCamOverlayRectValid && currentStickCamOverlayRect.Contains(mousePosition)) ||
                (settingsOpen && GetSettingsPanelRect().Contains(mousePosition)) ||
                (bindingsOpen && GetBindingsWindowRect().Contains(mousePosition)) ||
                stickGraphHovered;
        }

        private GhostLineEntry FindHoveredDroneLabel(Vector2 mousePosition)
        {
            DroneLabelHitbox best = default(DroneLabelHitbox);
            bool found = false;
            foreach (DroneLabelHitbox hitbox in droneLabelHitboxes)
            {
                if (!hitbox.rect.Contains(mousePosition))
                    continue;
                if (!found || hitbox.depth < best.depth)
                {
                    best = hitbox;
                    found = true;
                }
            }
            return found ? best.entry : null;
        }

        private void SetHoveredDroneLabel(GhostLineEntry entry)
        {
            if (hoveredDroneLabelEntry == entry)
                return;

            GhostLineEntry previous = hoveredDroneLabelEntry;
            hoveredDroneLabelEntry = entry;
            ApplyDroneLabelColor(previous);
            ApplyDroneLabelColor(hoveredDroneLabelEntry);
        }

        private void SetNameTagsVisible(bool visible)
        {
            GhostLinesPlugin.showNameTags = visible;
            if (!visible)
            {
                droneLabelHitboxes.Clear();
                droneLabelClickCandidate = null;
                SetHoveredDroneLabel(null);
            }

            // Force the enabled renderers and projected click targets to agree immediately.
            UpdateDroneMarkers();
        }

        private void UpdateOverlayResolution()
        {
            int width = Screen.width;
            int height = Screen.height;
            if (overlayScreenWidth <= 0 || overlayScreenHeight <= 0)
            {
                if (stickCamPositionCustomized)
                {
                    stickCamX = savedStickCamPositionX * width;
                    stickCamY = savedStickCamPositionY * height;
                }
                if (scrubberPositionCustomized)
                {
                    scrubberX = savedPlaybackUiPositionX * width;
                    scrubberY = savedPlaybackUiPositionY * height;
                }
            }
            if (overlayScreenWidth > 0 && overlayScreenHeight > 0 &&
                (overlayScreenWidth != width || overlayScreenHeight != height))
            {
                float widthScale = width / (float)overlayScreenWidth;
                float heightScale = height / (float)overlayScreenHeight;
                if (scrubberPositionCustomized)
                {
                    if (scrubberX >= 0f) scrubberX *= widthScale;
                    if (scrubberY >= 0f) scrubberY *= heightScale;
                }
                else
                {
                    scrubberX = width * 0.5f;
                    scrubberY = -1f;
                }

                if (stickCamPositionCustomized)
                {
                    if (stickCamX >= 0f) stickCamX *= widthScale;
                    if (stickCamY >= 0f) stickCamY *= heightScale;
                }
                else
                {
                    stickCamX = width * 0.5f;
                    stickCamY = -1f;
                }

                if (settingsPanelPositionCustomized && settingsRect.x >= 0f && settingsRect.y >= 0f)
                {
                    settingsRect.x *= widthScale;
                    settingsRect.y *= heightScale;
                }
            }

            overlayScreenWidth = width;
            overlayScreenHeight = height;
        }

        private string FormatTime(float t)
        {
            int mins = (int)(t / 60);
            float secs = t % 60;
            return $"{mins}:{secs:F3}";
        }

        private void EnsurePlaybackTimeStyles(float scale)
        {
            if (playbackTimeLeftStyle == null)
            {
                playbackTimeLeftStyle = new GUIStyle(GUI.skin.label);
                playbackTimeRightStyle = new GUIStyle(GUI.skin.label);
                playbackSpeedStyle = new GUIStyle(GUI.skin.label);
                playbackTimeLeftStyle.alignment = TextAnchor.MiddleLeft;
                playbackTimeRightStyle.alignment = TextAnchor.MiddleRight;
                playbackSpeedStyle.alignment = TextAnchor.MiddleCenter;
                playbackTimeLeftStyle.wordWrap = false;
                playbackTimeRightStyle.wordWrap = false;
                playbackSpeedStyle.wordWrap = false;
                playbackTimeLeftStyle.padding = new RectOffset(0, 0, 0, 0);
                playbackTimeRightStyle.padding = new RectOffset(0, 0, 0, 0);
                playbackSpeedStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            int fontSize = Mathf.Max(10, Mathf.RoundToInt(12f * scale));
            playbackTimeLeftStyle.fontSize = fontSize;
            playbackTimeRightStyle.fontSize = fontSize;
            playbackSpeedStyle.fontSize = fontSize;
        }

        private Texture2D CreateTriangleTex(int w, int h, bool right)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color wc = Color.white;
            const int samples = 4;
            const float left = 0.16f;
            const float top = 0.14f;
            const float bottom = 0.86f;
            const float tip = 0.88f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        float ny = (y + (sy + 0.5f) / samples) / h;
                        if (ny < top || ny > bottom)
                            continue;

                        float vertical = Mathf.Abs(ny - 0.5f) / ((bottom - top) * 0.5f);
                        float edge = left + (tip - left) * (1f - vertical);
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float nx = (x + (sx + 0.5f) / samples) / w;
                            bool inside = right
                                ? nx <= 1f - left && nx >= 1f - edge
                                : nx >= left && nx <= edge;
                            if (inside)
                                covered++;
                        }
                    }

                    float alpha = covered / (float)(samples * samples);
                    tex.SetPixel(x, y, new Color(wc.r, wc.g, wc.b, alpha));
                }
            }
            tex.Apply();
            return tex;
        }

        private Texture2D CreatePauseTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h);
            Color wc = Color.white;
            Color t = Color.clear;
            int bw = Mathf.RoundToInt(w * 0.28f);
            int gap = w - 2 * bw - 2 * Mathf.RoundToInt(w * 0.1f);
            int m = Mathf.RoundToInt(w * 0.1f);
            int l1 = m;
            int r1 = l1 + bw - 1;
            int l2 = w - m - bw;
            int r2 = w - m - 1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, (x >= l1 && x <= r1) || (x >= l2 && x <= r2) ? wc : t);
            tex.Apply();
            return tex;
        }

        private Texture2D CreateDoubleTriangleTex(int w, int h, bool right)
        {
            Texture2D tex = new Texture2D(w, h);
            Color wc = Color.white;
            Color t = Color.clear;
            int gap = 2;
            int half = (w - gap) / 2;
            float h1 = h - 1;
            float half1 = half - 1;
            for (int y = 0; y < h; y++)
            {
                float ny = y / h1;
                float dist = Mathf.Abs(ny - 0.5f) * 2f;
                for (int x = 0; x < w; x++)
                {
                    if (x < half)
                    {
                        float nx = x / half1;
                        tex.SetPixel(x, y, (right ? nx >= dist : nx <= 1f - dist) ? wc : t);
                    }
                    else if (x >= half + gap)
                    {
                        float nx = (x - half - gap) / half1;
                        tex.SetPixel(x, y, (right ? nx >= dist : nx <= 1f - dist) ? wc : t);
                    }
                    else
                        tex.SetPixel(x, y, t);
                }
            }
            tex.Apply();
            return tex;
        }

        private Texture2D CreateLoopTex(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size;
                    float ny = (y + 0.5f) / size;
                    bool top = nx > 0.20f && nx < 0.72f && ny > 0.20f && ny < 0.32f;
                    bool right = nx > 0.66f && nx < 0.78f && ny > 0.26f && ny < 0.66f;
                    bool bottom = nx > 0.28f && nx < 0.80f && ny > 0.68f && ny < 0.80f;
                    bool left = nx > 0.22f && nx < 0.34f && ny > 0.36f && ny < 0.74f;
                    bool topArrow = nx >= 0.68f && nx <= 0.92f && Mathf.Abs(ny - 0.26f) <= (0.92f - nx) * 0.65f;
                    bool bottomArrow = nx >= 0.08f && nx <= 0.32f && Mathf.Abs(ny - 0.74f) <= (nx - 0.08f) * 0.65f;
                    tex.SetPixel(x, y, top || right || bottom || left || topArrow || bottomArrow ? Color.white : Color.clear);
                }
            }
            tex.Apply(false);
            return tex;
        }

        private Texture2D CreateGearTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color wc = Color.white;
            Color t = Color.clear;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (x + 0.5f) / w - 0.5f;
                    float ny = (y + 0.5f) / h - 0.5f;
                    float radius = Mathf.Sqrt(nx * nx + ny * ny);
                    float angle = Mathf.Atan2(ny, nx);
                    bool ring = radius > 0.22f && radius < 0.42f;
                    bool tooth = radius >= 0.38f && radius < 0.53f && Mathf.Abs(Mathf.Cos(angle * 4f)) > 0.72f;
                    bool hole = radius < 0.19f;
                    tex.SetPixel(x, y, (ring || tooth) && !hole ? wc : t);
                }
            }
            tex.Apply(true);
            return tex;
        }

private Texture2D CreateKeyboardTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color wc = Color.white;
            Color t = Color.clear;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (x + 0.5f) / w;
                    float ny = (y + 0.5f) / h;
                    bool cap1 = nx > 0.18f && nx < 0.34f && ny > 0.34f && ny < 0.5f;
                    bool cap2 = nx > 0.38f && nx < 0.54f && ny > 0.34f && ny < 0.5f;
                    bool cap3 = nx > 0.58f && nx < 0.74f && ny > 0.34f && ny < 0.5f;
                    bool cap4 = nx > 0.24f && nx < 0.4f && ny > 0.56f && ny < 0.72f;
                    bool cap5 = nx > 0.44f && nx < 0.6f && ny > 0.56f && ny < 0.72f;
                    bool cap6 = nx > 0.64f && nx < 0.76f && ny > 0.56f && ny < 0.72f;
                    tex.SetPixel(x, y, (cap1 || cap2 || cap3 || cap4 || cap5 || cap6) ? wc : t);
                }
            }
            tex.Apply();
            return tex;
        }

        private Texture2D CreatePinTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (x + 0.5f) / w - 0.5f;
                    float ny = (y + 0.5f) / h - 0.5f;
                    float localX = (nx + ny) * 0.7071f;
                    float localY = (nx - ny) * 0.7071f;
                    bool head = Mathf.Abs(localX) < 0.28f && localY > -0.42f && localY < -0.1f;
                    bool cap = Mathf.Abs(localX) < 0.43f && localY >= -0.1f && localY < 0.06f;
                    bool stem = Mathf.Abs(localX) < 0.12f && localY >= 0.06f && localY < 0.34f;
                    bool tip = Mathf.Abs(localX) < 0.06f && localY >= 0.34f && localY < 0.48f;
                    tex.SetPixel(x, y, head || cap || stem || tip ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            return tex;
        }

        private void EnsureIcons()
        {
            if (iconsInitialized) return;
            iconsInitialized = true;
            iconPlay = CreateTriangleTex(64, 64, false);
            iconPause = CreatePauseTex(14, 14);
            iconRewind = CreateDoubleTriangleTex(22, 14, true);
            iconFF = CreateDoubleTriangleTex(22, 14, false);
            iconLoop = CreateLoopTex(64);
            iconSettings = CreateGearTex(64, 64);
            iconPin = CreatePinTex(14, 14);
            iconBindings = CreateKeyboardTex(64, 64);
        }

        private bool DrawStyledButton(Rect rect, Texture2D icon, float alpha)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            Color old = GUI.color;

            float shadowOff = 2f;
            GUI.color = new Color(0f, 0f, 0f, 0.22f * alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y + shadowOff, rect.width, rect.height), whiteTex);

            GUI.color = hover
                ? new Color(1f, 1f, 1f, 0.92f * alpha)
                : ThemeButtonBg(alpha);
            GUI.DrawTexture(rect, whiteTex);

            float db = 1;
            GUI.color = ThemeButtonBorder(alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - db, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, db, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - db, rect.y, db, rect.height), whiteTex);

            if (icon != null)
            {
                float iconS = Mathf.Min(rect.width - 10, rect.height - 8, 18);
                if (icon == iconPlay)
                    iconS *= 1.15f;
                if (icon == iconBindings)
                    iconS *= 1.35f;
                Rect ir = new Rect(rect.x + (rect.width - iconS) * 0.5f, rect.y + (rect.height - iconS) * 0.5f, iconS, iconS);
                GUI.color = ThemeButtonIcon(alpha);
                GUI.DrawTexture(ir, icon);
            }

            GUI.color = old;

            if (e.type == EventType.MouseUp && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private bool DrawStyledToggleButton(Rect rect, Texture2D icon, bool selected, float alpha, bool allowHover = true)
        {
            Event e = Event.current;
            bool hover = allowHover && rect.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
                settingsControlPressed = true;

            Color old = GUI.color;
            float shadowOff = 2f;
            GUI.color = new Color(0f, 0f, 0f, 0.22f * alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y + shadowOff, rect.width, rect.height), whiteTex);

            GUI.color = selected
                ? new Color(0.25f, 0.62f, 0.95f, 0.95f * alpha)
                : hover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
            GUI.DrawTexture(rect, whiteTex);

            float db = 1f;
            GUI.color = ThemeButtonBorder(alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - db, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, db, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - db, rect.y, db, rect.height), whiteTex);

            if (icon != null)
            {
                float iconS = icon == iconLoop ? 18f : 14f;
                Rect iconRect = new Rect(rect.x + (rect.width - iconS) * 0.5f, rect.y + (rect.height - iconS) * 0.5f, iconS, iconS);
                GUI.color = selected ? new Color(1f, 1f, 1f, alpha) : ThemeButtonIcon(alpha);
                GUI.DrawTexture(iconRect, icon);
            }
            GUI.color = old;

            if (e.type == EventType.MouseUp && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private void SetPlaybackSpeed(float speed)
        {
            if (Mathf.Approximately(speed, 1f))
            {
                if (overridePlayback)
                {
                    overridePlayback = false;
                    playbackSpeed = 1f;
                    SetNativeReplayPlaying(isPlaying);
                }
                return;
            }
            bool wasPlaying = isPlaying;
            if (!overridePlayback)
            {
                overridePlayback = true;
                SetNativeReplayPlaying(false);
            }
            playbackSpeed = speed;
            isPlaying = wasPlaying;
        }
        private void DrawBottomScrubber()
        {
            float s = playbackUiSize;
            float barH = 56f * s;
            float barW = (Screen.width / 3f) * s;
            if (scrubberX < 0) scrubberX = Screen.width * 0.5f;
            if (scrubberY < 0) scrubberY = Screen.height - barH * 0.5f;
            scrubberX = Mathf.Clamp(scrubberX, barW * 0.5f, Screen.width - barW * 0.5f);
            scrubberY = Mathf.Clamp(scrubberY, barH * 0.5f + 2f, Screen.height - barH * 0.5f);
            float barX = scrubberX - barW * 0.5f;
            float barY = scrubberY - barH * 0.5f;

            float val = 0, min = 0, max = 1;
            if (replayController != null && timeProp != null)
            {
                val = (float)timeProp.GetValue(replayController, null);
                var maxProp = GetControllerMaxTimeProp();
                if (maxProp != null)
                    max = (float)maxProp.GetValue(replayController, null);
            }
            else if (sliderComponent != null && sliderValueProp != null)
            {
                val = (float)sliderValueProp.GetValue(sliderComponent, null);
                var minProp = GetSliderMinValueProp();
                var maxProp = GetSliderMaxValueProp();
                if (minProp != null) min = (float)minProp.GetValue(sliderComponent, null);
                if (maxProp != null) max = (float)maxProp.GetValue(sliderComponent, null);
            }

            max = GetEffectiveReplayEnd(max);
            val = Mathf.Clamp(val, min, max);

            Rect totalRect = new Rect(barX - 5f * s, barY - 5f * s, barW + 10f * s, barH + 10f * s);
            currentPlaybackOverlayRect = totalRect;
            currentPlaybackOverlayRectValid = true;
            Vector2 mp = Event.current.mousePosition;
            if ((mp - lastMousePos).sqrMagnitude > 1f)
            {
                mouseStillTimer = 0f;
                lastMousePos = mp;
            }
            mouseStillTimer += Time.deltaTime;
            bool hoveringSettings = settingsOpen && GetSettingsPanelRect().Contains(mp);
            bool hoveringStickCam = currentStickCamOverlayRectValid && currentStickCamOverlayRect.Contains(mp);
            bool hoveringAnyGraph = false;
            for (int i = 0; i < stickGraphs.Count; i++)
            {
                if (stickGraphs[i].rect.Contains(mp))
                {
                    hoveringAnyGraph = true;
                    break;
                }
            }
            bool hoveringOverlay = totalRect.Contains(mp) || hoveringSettings || hoveringStickCam || hoveringAnyGraph;
            float playbackTarget = mouseStillTimer < 0.8f ? 1f : 0f;
            if (hoveringOverlay || Time.time < replayEnterTime + 3f || playbackPinned) playbackTarget = 1f;
            fadeAmount = Mathf.Lerp(fadeAmount, playbackTarget, Time.deltaTime * 6f);

            if (settingsOpen)
            {
                float settingsTarget = mouseStillTimer < 0.8f || hoveringOverlay || settingsPinned ? 1f : 0f;
                settingsFadeAmount = Mathf.Lerp(settingsFadeAmount, settingsTarget, Time.deltaTime * 6f);
            }
            else
            {
                settingsFadeAmount = 0f;
            }

            bool hoveringBindings = bindingsOpen && GetBindingsWindowRect().Contains(mp);
            if (bindingsOpen)
            {
                float bindingsTarget = mouseStillTimer < 0.8f || hoveringOverlay || hoveringBindings || bindingsPinned ? 1f : 0f;
                bindingsFadeAmount = Mathf.Lerp(bindingsFadeAmount, bindingsTarget, Time.deltaTime * 6f);
            }
            else
            {
                bindingsFadeAmount = 0f;
            }

            for (int i = 0; i < stickGraphs.Count; i++)
            {
                StickGraphState g = stickGraphs[i];
                if (g.pinned)
                {
                    g.fadeAmount = 1f;
                    continue;
                }
                float stickGraphTarget = mouseStillTimer < 0.8f || hoveringOverlay || hoveringAnyGraph ? 1f : 0f;
                g.fadeAmount = Mathf.Lerp(g.fadeAmount, stickGraphTarget, Time.deltaTime * 6f);
            }

            if (fadeAmount < 0.01f) return;

            EnsurePlaybackTimeStyles(s);
            float labelW = Mathf.Max(55f * s, 54f);
            float labelInset = Mathf.Max(6f * s, 6f);
            float labelH = Mathf.Max(18f * s, 18f);
            float btnW = 34f * s;
            float btnH = 24f * s;
            float row1Y = barY + Mathf.Max(2f * s, 2f);
            float row2Y = barY + 28f * s;

            Rect sliderRect = new Rect(
                barX + labelW + labelInset,
                row1Y,
                barW - labelW * 2f - labelInset * 2f,
                Mathf.Max(18f * s, 18f));
            Rect btnPlayRect = new Rect(barX + 5f * s, row2Y, btnW, btnH);
            Rect btnRewindRect = new Rect(barX + 5f * s + btnW + 4f * s, row2Y, btnW, btnH);
            Rect btnFFRect = new Rect(barX + 5f * s + (btnW + 4f * s) * 2f, row2Y, btnW, btnH);
            float speedW = Mathf.Max(34f * s, 32f);
            Rect speedRect = new Rect(btnFFRect.xMax + 4f * s, row2Y, speedW, btnH);
            Rect btnLoopRect = new Rect(speedRect.xMax + 4f * s, row2Y, btnW, btnH);
            float pinW = btnW;

            Color oldColor = GUI.color;

            GUI.color = ThemeBarBg(fadeAmount);
            GUI.DrawTexture(totalRect, whiteTex);
            float bd = 1;
            GUI.color = ThemeBarBorder(fadeAmount);
            GUI.DrawTexture(new Rect(totalRect.x, totalRect.y, totalRect.width, bd), whiteTex);
            GUI.DrawTexture(new Rect(totalRect.x, totalRect.yMax - bd, totalRect.width, bd), whiteTex);
            GUI.DrawTexture(new Rect(totalRect.x, totalRect.y, bd, totalRect.height), whiteTex);
            GUI.DrawTexture(new Rect(totalRect.xMax - bd, totalRect.y, bd, totalRect.height), whiteTex);

            EnsureIcons();
            Texture2D playTex = isPlaying ? iconPause : iconPlay;
            if (DrawStyledButton(btnPlayRect, playTex, fadeAmount))
            {
                Debug.Log("[GhostLines] GUI: play pressed");
                RequestPlayButton();
            }
            if (DrawStyledButton(btnRewindRect, iconRewind, fadeAmount))
            {
                Debug.Log("[GhostLines] GUI: rewind pressed");
                ClickGameButton("btnRewind");
            }
            if (DrawStyledButton(btnFFRect, iconFF, fadeAmount))
            {
                Debug.Log("[GhostLines] GUI: ff pressed");
                ClickGameButton("btnFastForward");
            }
            if (DrawStyledToggleButton(btnLoopRect, iconLoop, loopReplayEnabled, fadeAmount))
                loopReplayEnabled = !loopReplayEnabled;

            GUI.color = ThemeLabel(fadeAmount);
            GUI.Label(speedRect, playbackSpeed.ToString("0.##") + "x", playbackSpeedStyle);

            Rect btnSettingsRect = new Rect(btnLoopRect.xMax + 4f * s, row2Y, btnW, btnH);
            if (DrawStyledButton(btnSettingsRect, iconSettings, fadeAmount))
            {
                settingsOpen = !settingsOpen;
                SaveSettingsPanelOpen(settingsOpen);
                if (settingsOpen)
                {
                    EnsureSettingsPanelRect();
                    settingsFadeAmount = 1f;
                }
            }
            Rect btnBindingsRect = new Rect(btnSettingsRect.xMax + 4f * s, row2Y, btnW, btnH);
            if (DrawStyledButton(btnBindingsRect, iconBindings, fadeAmount))
            {
                bindingsOpen = !bindingsOpen;
                if (bindingsOpen)
                {
                    EnsureBindingsWindowRect();
                    bindingsFadeAmount = 1f;
                }
            }
            Rect btnPinRect = new Rect(btnBindingsRect.xMax + 4f * s, row2Y, pinW, btnH);
            if (DrawStyledToggleButton(btnPinRect, iconPin, playbackPinned, fadeAmount))
                playbackPinned = !playbackPinned;

            bool onControl = sliderRect.Contains(mp) || btnPlayRect.Contains(mp)
                          || btnRewindRect.Contains(mp) || btnFFRect.Contains(mp) || btnLoopRect.Contains(mp)
                          || btnSettingsRect.Contains(mp) || btnBindingsRect.Contains(mp) || btnPinRect.Contains(mp);
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0 && totalRect.Contains(mp) && !onControl)
            {
                draggingScrubber = true;
                playbackMouseCaptured = true;
                isOrbiting = false;
                CancelCameraLook();
                scrubberPositionCustomized = true;
                dragStartMouse = mp;
                dragStartBarPos = new Vector2(scrubberX, scrubberY);
                ev.Use();
            }
            if (draggingScrubber)
            {
                if (ev.type == EventType.MouseUp && ev.button == 0)
                {
                    draggingScrubber = false;
                    playbackMouseCaptured = false;
                    SaveCurrentOverlayPositions();
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && ev.button == 0)
                {
                    Vector2 offset = mp - dragStartMouse;
                    float draggedX = SnapOverlayToHorizontalCenter(dragStartBarPos.x + offset.x);
                    scrubberX = Mathf.Clamp(draggedX, barW * 0.5f, Screen.width - barW * 0.5f);
                    scrubberY = Mathf.Clamp(dragStartBarPos.y + offset.y, barH * 0.5f + 2f, Screen.height - barH * 0.5f);
                    ev.Use();
                }
            }

            float currentTime = Mathf.Max(0f, val);
            float totalTime = Mathf.Max(0f, max);

            GUI.color = ThemeLabel(fadeAmount);
            GUI.Label(new Rect(barX + labelInset, row1Y, labelW, labelH), FormatTime(currentTime), playbackTimeLeftStyle);
            GUI.color = oldColor;

            float trackH = 10f * s;
            float handleW = 12f * s;
            float handleH = 20f * s;
            float pad = handleW / 2f;
            float tX = sliderRect.x + pad;
            float tW = sliderRect.width - handleW;
            float tY = sliderRect.y + (sliderRect.height - trackH) / 2f;
            float range = Mathf.Max(max - min, 0.0001f);
            float normV = Mathf.Clamp01((val - min) / range);

            GUI.color = ThemeTrack(fadeAmount);
            GUI.DrawTexture(new Rect(tX, tY, tW, trackH), whiteTex);

            if (normV > 0.001f)
            {
                GUI.color = new Color(0.25f, 0.62f, 0.95f, fadeAmount);
                GUI.DrawTexture(new Rect(tX, tY, normV * tW, trackH), whiteTex);
            }

            float handleX = tX + normV * tW;
            Rect handleRect = new Rect(handleX - handleW / 2f, sliderRect.y + (sliderRect.height - handleH) / 2f, handleW, handleH);
            Event ce = Event.current;
            bool hHit = new Rect(handleRect.x - 4, handleRect.y - 4, handleRect.width + 8, handleRect.height + 8).Contains(ce.mousePosition);
            bool tHit = new Rect(tX, sliderRect.y, tW, sliderRect.height).Contains(ce.mousePosition);
            GUI.color = hHit ? new Color(1f, 1f, 1f, fadeAmount) : ThemeHandle(fadeAmount);
            GUI.DrawTexture(handleRect, whiteTex);

            float hb = 1;
            GUI.color = new Color(0.35f, 0.35f, 0.35f, 0.55f * fadeAmount);
            GUI.DrawTexture(new Rect(handleRect.x, handleRect.y, handleRect.width, hb), whiteTex);
            GUI.DrawTexture(new Rect(handleRect.x, handleRect.yMax - hb, handleRect.width, hb), whiteTex);
            GUI.DrawTexture(new Rect(handleRect.x, handleRect.y, hb, handleRect.height), whiteTex);
            GUI.DrawTexture(new Rect(handleRect.xMax - hb, handleRect.y, hb, handleRect.height), whiteTex);

            if (ce.type == EventType.MouseDown && ce.button == 0 && (hHit || tHit) && !isDraggingHandle)
            {
                isDraggingHandle = true;
                playbackMouseCaptured = true;
                isOrbiting = false;
                CancelCameraLook();
                resumePlaybackAfterSeek = overridePlayback ? isPlaying : IsNativeReplayPlaying();
                float nn = Mathf.Clamp01((ce.mousePosition.x - tX) / tW);
                float nv = min + nn * range;
                SeekReplayTimeKeepingPlayback(nv);
                ce.Use();
            }
            if (isDraggingHandle)
            {
                if (ce.type == EventType.MouseUp && ce.button == 0)
                {
                    isDraggingHandle = false;
                    playbackMouseCaptured = false;
                    if (resumePlaybackAfterSeek && !overridePlayback)
                        SetNativeReplayPlaying(true);
                    resumePlaybackAfterSeek = false;
                    ce.Use();
                }
                else if (ce.type == EventType.MouseDrag && ce.button == 0)
                {
                    float nn = Mathf.Clamp01((ce.mousePosition.x - tX) / tW);
                    float nv = min + nn * range;
                    SeekReplayTimeKeepingPlayback(nv);
                    ce.Use();
                }
            }
            GUI.color = ThemeLabel(fadeAmount);
            GUI.Label(new Rect(barX + barW - labelInset - labelW, row1Y, labelW, labelH), FormatTime(totalTime), playbackTimeRightStyle);

            GUI.color = oldColor;
        }

        private float GetSettingsPanelHeight()
        {
            int pilotCount = GhostLinesPlugin.entries.Count == 0 ? 1 : GhostLinesPlugin.entries.Count;
            float height = 42f;
            height += 34f * 4f;
            height += 30f;
            height += 20f;
            height += 28f * pilotCount;
            height += 34f + 30f + 30f;
            height += 34f + 34f + 34f;
            return height + 12f;
        }

        private void EnsureSettingsPanelRect(bool initializePosition = true)
        {
            float panelH = GetSettingsPanelHeight();
            float panelW = 304f;
            if (initializePosition && settingsRect.x < 0f)
            {
                float defaultX = Screen.width - panelW - 24f;
                float defaultY = Screen.height - panelH - 24f;
                if (savedSettingsPanelPositionCustomized)
                {
                    defaultX = savedSettingsPanelPositionX * Screen.width;
                    defaultY = savedSettingsPanelPositionY * Screen.height;
                }
                settingsRect = new Rect(defaultX, defaultY, panelW, panelH);
            }
            else
            {
                float bottom = settingsRect.y + settingsRect.height;
                settingsRect.width = panelW;
                settingsRect.height = panelH;
                settingsRect.y = bottom - panelH;
            }

            settingsRect.x = Mathf.Clamp(settingsRect.x, 12f, Mathf.Max(12f, Screen.width - settingsRect.width - 12f));
            settingsRect.y = Mathf.Clamp(settingsRect.y, 12f, Mathf.Max(12f, Screen.height - settingsRect.height - 12f));
        }

        private Rect GetSettingsPanelRect()
        {
            EnsureSettingsPanelRect();
            return settingsRect;
        }

        private void EnsureSettingsStyles()
        {
            if (settingsLabelStyle == null)
            {
                settingsLabelStyle = new GUIStyle(GUI.skin.label);
                settingsLabelStyle.fontSize = 12;
                settingsLabelStyle.alignment = TextAnchor.MiddleLeft;
                settingsSmallStyle = new GUIStyle(settingsLabelStyle);
                settingsSmallStyle.fontSize = 10;
                settingsSmallCenterStyle = new GUIStyle(settingsSmallStyle);
                settingsSmallCenterStyle.alignment = TextAnchor.MiddleCenter;
                settingsAxisLabelStyle = new GUIStyle(settingsSmallCenterStyle);
                settingsAxisLabelStyle.clipping = TextClipping.Overflow;
                settingsHeaderStyle = new GUIStyle(settingsLabelStyle);
                settingsHeaderStyle.fontSize = 13;
                settingsHeaderStyle.fontStyle = FontStyle.Bold;
                settingsHeaderStyle.wordWrap = false;
                settingsHeaderStyle.clipping = TextClipping.Clip;
                settingsPlusStyle = new GUIStyle(settingsHeaderStyle);
                settingsPlusStyle.fontSize = 14;
                settingsPlusStyle.alignment = TextAnchor.MiddleCenter;
                settingsCloseStyle = new GUIStyle(settingsHeaderStyle);
                settingsCloseStyle.alignment = TextAnchor.MiddleCenter;
                settingsSectionStyle = new GUIStyle(settingsLabelStyle);
                settingsSectionStyle.fontSize = 10;
                settingsSectionStyle.fontStyle = FontStyle.Bold;
            }
            if (darkMode)
            {
                settingsLabelStyle.normal.textColor = new Color(0.88f, 0.88f, 0.88f);
                settingsSmallStyle.normal.textColor = new Color(0.74f, 0.74f, 0.76f);
                settingsHeaderStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
                settingsSectionStyle.normal.textColor = new Color(0.6f, 0.6f, 0.62f);
                if (settingsAxisLabelStyle != null)
                    settingsAxisLabelStyle.normal.textColor = new Color(0.74f, 0.74f, 0.76f);
            }
            else
            {
                settingsLabelStyle.normal.textColor = new Color(0.12f, 0.12f, 0.12f);
                settingsSmallStyle.normal.textColor = new Color(0.38f, 0.38f, 0.38f);
                settingsHeaderStyle.normal.textColor = new Color(0.12f, 0.12f, 0.12f);
                settingsSectionStyle.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
                if (settingsAxisLabelStyle != null)
                    settingsAxisLabelStyle.normal.textColor = new Color(0.38f, 0.38f, 0.38f);
            }
        }

        private Texture2D CreateTargetTexture(int size, int kind)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size - 0.5f;
                    float ny = (y + 0.5f) / size - 0.5f;
                    float radius = Mathf.Sqrt(nx * nx + ny * ny);
                    bool filled = kind == 0
                        ? radius > 0.32f && radius < 0.47f
                        : kind == 1 ? radius < 0.25f : radius < 0.5f;
                    tex.SetPixel(x, y, filled ? Color.white : Color.clear);
                }
            }
            tex.Apply(true);
            return tex;
        }

        private void EnsureSettingsTextures()
        {
            if (targetRingTex != null) return;
            targetRingTex = CreateTargetTexture(72, 0);
            targetDotTex = CreateTargetTexture(72, 1);
            targetFillTex = CreateTargetTexture(72, 2);
        }

        private Color ThemePanelBg(float alpha) { return darkMode ? new Color(0.12f, 0.12f, 0.13f, 0.96f * alpha) : new Color(0.97f, 0.97f, 0.97f, 0.96f * alpha); }
        private Color ThemePanelBorder(float alpha) { return darkMode ? new Color(0.48f, 0.48f, 0.5f, 0.6f * alpha) : new Color(0.32f, 0.32f, 0.32f, 0.65f * alpha); }
        private Color ThemeLabel(float alpha) { return darkMode ? new Color(1f, 1f, 1f, alpha) : new Color(0.13f, 0.13f, 0.13f, alpha); }
        private Color ThemeSectionLabel(float alpha) { return darkMode ? new Color(1f, 1f, 1f, alpha) : new Color(0.32f, 0.32f, 0.32f, alpha); }
        private Color ThemeDimLabel(float alpha) { return darkMode ? new Color(0.8f, 0.8f, 0.8f, alpha) : new Color(0.42f, 0.42f, 0.42f, alpha); }
        private Color ThemeCloseLabel(float alpha) { return darkMode ? new Color(0.85f, 0.85f, 0.85f, alpha) : new Color(0.35f, 0.35f, 0.35f, alpha); }
        private Color ThemeOptionBg(float alpha) { return darkMode ? new Color(0.24f, 0.24f, 0.26f, alpha) : new Color(0.93f, 0.93f, 0.93f, alpha); }
        private Color ThemeOptionBorder(float alpha) { return darkMode ? new Color(0.42f, 0.42f, 0.45f, 0.6f * alpha) : new Color(0.55f, 0.55f, 0.55f, 0.65f * alpha); }
        private Color ThemeOptionText(float alpha) { return darkMode ? new Color(0.9f, 0.9f, 0.9f, alpha) : new Color(0.15f, 0.15f, 0.15f, alpha); }
        private Color ThemeToggleOff(float alpha) { return darkMode ? new Color(0.4f, 0.4f, 0.42f, alpha) : new Color(0.68f, 0.68f, 0.68f, alpha); }
        private Color ThemeTrack(float alpha) { return darkMode ? new Color(0.45f, 0.45f, 0.48f, 0.45f * alpha) : new Color(0.3f, 0.3f, 0.3f, 0.4f * alpha); }
        private Color ThemeHandle(float alpha) { return darkMode ? new Color(0.7f, 0.7f, 0.72f, alpha) : new Color(0.85f, 0.85f, 0.85f, alpha); }
        private Color ThemeBarBg(float alpha) { return darkMode ? new Color(0.13f, 0.13f, 0.14f, 0.9f * alpha) : new Color(1f, 1f, 1f, 0.9f * alpha); }
        private Color ThemeBarBorder(float alpha) { return darkMode ? new Color(0.45f, 0.45f, 0.47f, alpha) : new Color(0.6f, 0.6f, 0.6f, alpha); }
        private Color ThemeButtonBg(float alpha) { return darkMode ? new Color(0.27f, 0.27f, 0.29f, 0.92f * alpha) : new Color(0.9f, 0.9f, 0.9f, 0.92f * alpha); }
        private Color ThemeButtonBorder(float alpha) { return darkMode ? new Color(0.5f, 0.5f, 0.52f, 0.55f * alpha) : new Color(0.35f, 0.35f, 0.35f, 0.5f * alpha); }
        private Color ThemeButtonIcon(float alpha) { return darkMode ? new Color(0.88f, 0.88f, 0.88f, alpha) : new Color(0.2f, 0.2f, 0.2f, alpha); }

        private bool DrawSettingsOption(Rect rect, string label, bool selected, float alpha, TextAnchor align = TextAnchor.MiddleLeft)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
                settingsControlPressed = true;
            Color old = GUI.color;
            GUI.color = selected
                ? new Color(0.25f, 0.62f, 0.95f, alpha)
                : ThemeOptionBg(alpha);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = hover
                ? new Color(0.2f, 0.2f, 0.2f, alpha)
                : ThemeOptionBorder(alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), whiteTex);
            GUI.color = selected ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
            GUIStyle labelStyle = align == TextAnchor.MiddleCenter ? settingsSmallCenterStyle : settingsSmallStyle;
            GUI.Label(rect, label, labelStyle);
            GUI.color = old;
            if (e.type == EventType.MouseUp && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private bool DrawSettingsToggle(Rect rect, bool enabled, float alpha)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
                settingsControlPressed = true;
            Color old = GUI.color;
            GUI.color = enabled
                ? new Color(0.25f, 0.62f, 0.95f, alpha)
                : ThemeToggleOff(alpha);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            float knob = rect.height - 4f;
            float knobX = enabled ? rect.xMax - knob - 2f : rect.x + 2f;
            GUI.DrawTexture(new Rect(knobX, rect.y + 2f, knob, knob), whiteTex);
            GUI.color = old;
            if (e.type == EventType.MouseUp && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private bool DrawSettingsTarget(Rect rect, bool selected, float alpha)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
                settingsControlPressed = true;
            Color old = GUI.color;
            if (selected)
            {
                GUI.color = new Color(0.15f, 0.65f, 1f, 0.95f * alpha);
                GUI.DrawTexture(rect, targetFillTex);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(rect, targetDotTex);
            }
            else
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f, alpha * (hover ? 1f : 0.7f));
                GUI.DrawTexture(rect, targetRingTex);
            }
            GUI.color = old;
            if (e.type == EventType.MouseUp && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private void DrawSettingsSlider(Rect trackRect, float min, float max, float alpha, ref bool dragging, ref float value)
        {
            float trackH = 6f;
            float handleW = 12f;
            float handleH = 16f;
            float range = Mathf.Max(max - min, 0.0001f);
            float normV = Mathf.Clamp01((value - min) / range);
            float tX = trackRect.x;
            float tW = trackRect.width - handleW;
            float tY = trackRect.y + (trackRect.height - trackH) / 2f;
            float handleX = tX + normV * tW;
            Color old = GUI.color;

            GUI.color = ThemeTrack(alpha);
            GUI.DrawTexture(new Rect(tX, tY, tW, trackH), whiteTex);
            GUI.color = new Color(0.25f, 0.62f, 0.95f, alpha);
            GUI.DrawTexture(new Rect(tX, tY, normV * tW, trackH), whiteTex);

            Rect handleRect = new Rect(handleX - handleW / 2f, trackRect.y + (trackRect.height - handleH) / 2f, handleW, handleH);
            Event ce = Event.current;
            bool hHit = new Rect(handleRect.x - 4f, handleRect.y - 4f, handleRect.width + 8f, handleRect.height + 8f).Contains(ce.mousePosition);
            bool tHit = trackRect.Contains(ce.mousePosition);
            if (ce.type == EventType.MouseDown && ce.button == 0 && (hHit || tHit))
                settingsControlPressed = true;
            GUI.color = hHit ? new Color(1f, 1f, 1f, alpha) : ThemeHandle(alpha);
            GUI.DrawTexture(handleRect, whiteTex);

            if (ce.type == EventType.MouseDown && (hHit || tHit) && !dragging)
            {
                dragging = true;
                value = min + Mathf.Clamp01((ce.mousePosition.x - tX) / tW) * range;
                ce.Use();
            }
            else if (dragging)
            {
                if (ce.type == EventType.MouseUp)
                {
                    dragging = false;
                    ce.Use();
                }
                else if (ce.type == EventType.MouseDrag)
                {
                    value = min + Mathf.Clamp01((ce.mousePosition.x - tX) / tW) * range;
                    ce.Use();
                }
            }
            GUI.color = old;
        }

        private void DrawBindingsWindow()
        {
            if (!bindingsOpen || bindingsFadeAmount < 0.01f) return;
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            Rect panel = GetBindingsWindowRect();
            float alpha = bindingsFadeAmount;
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

            Event e = Event.current;
            Rect close = new Rect(panel.xMax - 30f, panel.y + 7f, 22f, 20f);
            Rect pin = new Rect(panel.xMax - 60f, panel.y + 7f, 22f, 20f);
            if (e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) && !close.Contains(e.mousePosition))
            {
                bindingsDragCandidate = true;
                bindingsDragOffset = e.mousePosition - new Vector2(panel.x, panel.y);
            }
            if (bindingsDragging && e.type == EventType.MouseDrag && e.button == 0)
            {
                bindingsWindowRect.x = e.mousePosition.x - bindingsDragOffset.x;
                bindingsWindowRect.y = e.mousePosition.y - bindingsDragOffset.y;
                EnsureBindingsWindowRect(false);
                e.Use();
            }
            if (bindingsDragging && e.type == EventType.MouseUp && e.button == 0)
            {
                bindingsDragging = false;
                e.Use();
            }

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 7f, 220f, 20f), "Control Bindings", settingsHeaderStyle);
            EnsureIcons();
            if (DrawStyledToggleButton(pin, iconPin, bindingsPinned, alpha))
                bindingsPinned = !bindingsPinned;
            GUI.color = ThemeCloseLabel(alpha);
            GUI.Label(close, "x", settingsCloseStyle);
            if (e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
            {
                bindingsOpen = false;
                bindingListeningFor = null;
                bindingsDragCandidate = false;
                bindingsDragging = false;
                e.Use();
                GUI.color = old;
                return;
            }

            float x = panel.x + 14f;
            float w = panel.width - 28f;
            float rowY = panel.y + 42f;
            float rowH = 26f;
            float btnW = 76f;
            float btnH = 20f;
            float btnX = panel.xMax - 92f;

            for (int i = 0; i < GhostLinesPlugin.BindingIds.Length; i++)
            {
                string id = GhostLinesPlugin.BindingIds[i];
                float ry = rowY + rowH * i;
                bool listening = bindingListeningFor == id;

                GUI.color = new Color(1f, 1f, 1f, 0.05f * alpha);
                if (i % 2 == 1)
                    GUI.DrawTexture(new Rect(x, ry, w, rowH), whiteTex);

                string labelText = GhostLinesPlugin.BindingLabel(id);
                float rowMidY = ry + rowH * 0.5f;
                float textWidth = settingsSmallStyle.CalcSize(new GUIContent(labelText)).x;
                float lineStart = x + Mathf.Min(textWidth + 6f, w - 92f);
                float lineEnd = btnX - 3f;
                GUI.color = new Color(0.55f, 0.55f, 0.6f, 0.35f * alpha);
                if (lineEnd > lineStart)
                    GUI.DrawTexture(new Rect(lineStart, rowMidY - 0.5f, lineEnd - lineStart, 1f), whiteTex);

                GUI.color = listening ? new Color(0.85f, 0.62f, 0.16f, 0.95f * alpha) : ThemeLabel(alpha);
                GUI.Label(new Rect(x, ry, w - 92f, rowH), labelText, settingsSmallStyle);

                Rect keyRect = new Rect(btnX, ry + (rowH - btnH) / 2f, btnW, btnH);
                string keyText = listening ? "Press key..." : GhostLinesPlugin.BindingKeyName(id);
                if (DrawSettingsOption(keyRect, keyText, listening, alpha, TextAnchor.MiddleCenter))
                {
                    bindingListeningFor = listening ? null : id;
                    bindingListeningJustStarted = true;
                }
            }

            Rect resetRect = new Rect(panel.x + (panel.width - 104f) / 2f, rowY + GhostLinesPlugin.BindingIds.Length * rowH + 6f, 104f, 20f);
            if (DrawSettingsOption(resetRect, "Reset Defaults", false, alpha, TextAnchor.MiddleCenter))
            {
                bindingListeningFor = null;
                GhostLinesPlugin.ResetAllBindings();
            }

            if (e.type == EventType.MouseDown && e.button == 0 && bindingsDragCandidate)
            {
                if (!settingsControlPressed)
                    bindingsDragging = true;
                bindingsDragCandidate = false;
            }
            if (e.type == EventType.MouseUp && e.button == 0)
                settingsControlPressed = false;

            GUI.color = old;
        }

        private float GetBindingsWindowHeight()
        {
            float rows = GhostLinesPlugin.BindingIds.Length;
            return 42f + rows * 26f + 44f;
        }

        private void EnsureBindingsWindowRect(bool initializePosition = true)
        {
            float panelH = GetBindingsWindowHeight();
            float panelW = 320f;
            if (initializePosition && (bindingsWindowRect.width < 1f || bindingsWindowRect.height < 1f))
            {
                float defaultX = (Screen.width - panelW) / 2f;
                float defaultY = (Screen.height - panelH) / 2f;
                if (bindingsPinned && bindingsWindowRect.x > 0f)
                {
                    defaultX = bindingsWindowRect.x;
                    defaultY = bindingsWindowRect.y > 0f ? bindingsWindowRect.y : defaultY;
                }
                bindingsWindowRect = new Rect(defaultX, defaultY, panelW, panelH);
            }
            else
            {
                float bottom = bindingsWindowRect.y + bindingsWindowRect.height;
                bindingsWindowRect.width = panelW;
                bindingsWindowRect.height = panelH;
                bindingsWindowRect.y = bottom - panelH;
            }

            bindingsWindowRect.x = Mathf.Clamp(bindingsWindowRect.x, 12f, Mathf.Max(12f, Screen.width - bindingsWindowRect.width - 12f));
            bindingsWindowRect.y = Mathf.Clamp(bindingsWindowRect.y, 12f, Mathf.Max(12f, Screen.height - bindingsWindowRect.height - 12f));
        }

        private Rect GetBindingsWindowRect()
        {
            EnsureBindingsWindowRect();
            return bindingsWindowRect;
        }

        private void DrawSettingsPanel()
        {
            if (!settingsOpen || settingsFadeAmount < 0.01f) return;
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            Rect panel = GetSettingsPanelRect();
            float alpha = settingsFadeAmount;
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

            Event e = Event.current;
            Rect close = new Rect(panel.xMax - 30f, panel.y + 7f, 22f, 20f);
            Rect pin = new Rect(panel.xMax - 60f, panel.y + 7f, 22f, 20f);
            if (e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) && !close.Contains(e.mousePosition))
            {
                settingsDragCandidate = true;
                settingsControlPressed = false;
                settingsDragOffset = e.mousePosition - new Vector2(panel.x, panel.y);
            }
            if (settingsDragging && e.type == EventType.MouseDrag && e.button == 0)
            {
                settingsRect.x = e.mousePosition.x - settingsDragOffset.x;
                settingsRect.y = e.mousePosition.y - settingsDragOffset.y;
                EnsureSettingsPanelRect(false);
                e.Use();
            }
            if (settingsDragging && e.type == EventType.MouseUp && e.button == 0)
            {
                settingsDragging = false;
                SaveCurrentOverlayPositions();
                e.Use();
            }

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 7f, 220f, 20f), "Settings", settingsHeaderStyle);
            EnsureIcons();
            if (DrawStyledToggleButton(pin, iconPin, settingsPinned, alpha))
                settingsPinned = !settingsPinned;
            GUI.color = ThemeCloseLabel(alpha);
            GUI.Label(close, "x", settingsCloseStyle);
            if (e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
            {
                settingsOpen = false;
                SaveSettingsPanelOpen(false);
                settingsControlPressed = false;
                settingsDragCandidate = false;
                settingsDragging = false;
                e.Use();
                GUI.color = old;
                return;
            }

            float x = panel.x + 14f;
            float w = panel.width - 28f;
            float y = panel.y + 42f;
            bool anyVisible = false;
            foreach (var entry in GhostLinesPlugin.entries)
                if (entry.visible) { anyVisible = true; break; }

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(x, y, 190f, 24f), "Race Lines", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), anyVisible, alpha))
                SetAllRaceLinesVisible(!anyVisible);
            y += 34f;

            GUI.Label(new Rect(x, y, 190f, 24f), "Show Lines Through Objects", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), GhostLinesPlugin.linesThroughObjects, alpha))
                GhostLinesPlugin.SetLinesThroughObjects(!GhostLinesPlugin.linesThroughObjects);
            y += 34f;

            GUI.Label(new Rect(x, y, 190f, 24f), "Delta Display", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), GhostLinesPlugin.showDeltaDisplay, alpha))
                GhostLinesPlugin.showDeltaDisplay = !GhostLinesPlugin.showDeltaDisplay;
            y += 34f;

            GUI.Label(new Rect(x, y, 190f, 24f), "Name Tags", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), GhostLinesPlugin.showNameTags, alpha))
                SetNameTagsVisible(!GhostLinesPlugin.showNameTags);
            y += 34f;

            GUI.color = ThemeLabel(alpha);
            float optionGap = 6f;
            bool smoothSelected = GhostLinesPlugin.smoothingEnabled;
            float buttonPad = 16f;
            float smoothW = settingsSmallStyle.CalcSize(new GUIContent("Smooth")).x + buttonPad;
            float originalW = settingsSmallStyle.CalcSize(new GUIContent("Original")).x + buttonPad;
            float visualOptionsW = smoothW + optionGap + originalW;
            float visualOptionsX = panel.xMax - 14f - visualOptionsW;
            GUI.Label(new Rect(x, y, visualOptionsX - x - 8f, 22f), "Race Line Visual", settingsLabelStyle);
            if (DrawSettingsOption(new Rect(visualOptionsX, y - 2f, smoothW, 22f), "Smooth", smoothSelected, alpha, TextAnchor.MiddleCenter) && !smoothSelected)
                GhostLinesPlugin.ToggleSmoothing();
            if (DrawSettingsOption(new Rect(visualOptionsX + smoothW + optionGap, y - 2f, originalW, 22f), "Original", !smoothSelected, alpha, TextAnchor.MiddleCenter) && smoothSelected)
                GhostLinesPlugin.ToggleSmoothing();
            y += 30f;

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(x, y, 120f, 18f), "Line", settingsSectionStyle);
            GUI.Label(new Rect(panel.xMax - 116f, y, 78f, 18f), "Delta", settingsSectionStyle);
            y += 20f;
            if (GhostLinesPlugin.entries.Count == 0)
            {
                GUI.color = ThemeDimLabel(alpha);
                GUI.Label(new Rect(x, y, w, 24f), "Waiting for pilots...", settingsSmallStyle);
                y += 28f;
            }
            else
            {
                for (int i = 0; i < GhostLinesPlugin.entries.Count; i++)
                {
                    GhostLineEntry entry = GhostLinesPlugin.entries[i];
                    Color entryColor = entry.color;
                    GUI.color = new Color(entryColor.r, entryColor.g, entryColor.b, alpha);
                    GUI.DrawTexture(new Rect(x, y + 2f, 18f, 18f), whiteTex);
                    if (DrawSettingsToggle(new Rect(x + 28f, y + 2f, 30f, 18f), entry.visible, alpha))
                        SetRaceLineVisible(entry, !entry.visible);

                    string name = string.IsNullOrEmpty(entry.pilotName) ? "Pilot " + (i + 1) : entry.pilotName;
                    string delta = string.IsNullOrEmpty(entry.guiDeltaText) ? "" : entry.guiDeltaText;
                    GUI.color = ThemeLabel(alpha);
                    GUI.Label(new Rect(x + 68f, y, panel.width - 198f, 22f), name, settingsSmallStyle);
                    GUI.Label(new Rect(panel.xMax - 116f, y, 78f, 22f), delta, settingsSmallStyle);
                    bool isReference = entry == referenceEntry;
                    if (DrawSettingsTarget(new Rect(panel.xMax - 38f, y + 3f, 18f, 18f), isReference, alpha))
                        referenceEntry = isReference ? null : entry;
                    y += 28f;
                }
            }

            GUI.Label(new Rect(x, y, 190f, 24f), "Stick Cam", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), stickCamEnabled, alpha))
            {
                stickCamEnabled = !stickCamEnabled;
                SaveDisplayPreferences();
            }
            y += 34f;

            GUI.Label(new Rect(x, y, 110f, 20f), "Stick Cam Size", settingsLabelStyle);
            float previousStickCamSize = stickCamSize;
            DrawSettingsSlider(new Rect(x + 116f, y + 4f, panel.xMax - 14f - (x + 116f), 16f), 0.5f, 1.8f, alpha, ref stickSizeDragging, ref stickCamSize);
            if (!Mathf.Approximately(previousStickCamSize, stickCamSize))
                SaveDisplayPreferences();
            y += 30f;

            GUI.Label(new Rect(x, y, 110f, 20f), "Playback UI Size", settingsLabelStyle);
            float previousPlaybackUiSize = playbackUiSize;
            DrawSettingsSlider(new Rect(x + 116f, y + 4f, panel.xMax - 14f - (x + 116f), 16f), 0.6f, 1.6f, alpha, ref uiSizeDragging, ref playbackUiSize);
            if (!Mathf.Approximately(previousPlaybackUiSize, playbackUiSize))
                SaveDisplayPreferences();
            y += 30f;

            GUI.Label(new Rect(x, y, 190f, 24f), "Xray", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), xrayEnabled, alpha))
                SetXrayEnabled(!xrayEnabled);
            y += 34f;

            GUI.Label(new Rect(x, y, 190f, 24f), "Dark Mode", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), darkMode, alpha))
            {
                darkMode = !darkMode;
                SaveDisplayPreferences();
            }
            y += 34f;

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(x, y, 190f, 24f), "Speed Heatmap", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(panel.xMax - 48f, y + 3f, 30f, 18f), GhostLinesPlugin.speedHeatmapEnabled, alpha))
                GhostLinesPlugin.ToggleSpeedHeatmap();
            y += 34f;

            if (e.type == EventType.MouseDown && e.button == 0 && settingsDragCandidate)
            {
                if (!settingsControlPressed)
                {
                    settingsDragging = true;
                    settingsPanelPositionCustomized = true;
                    e.Use();
                }
                settingsDragCandidate = false;
            }

            if (e.type == EventType.MouseUp && e.button == 0)
                settingsControlPressed = false;

            GUI.color = old;
        }
        } // GhostLinePoller
    } // GhostLinesPlugin
    internal static class ModLog
    {
        private static ManualLogSource source;
        private static ConfigEntry<bool> debugLogging;

        public static void Initialize(ManualLogSource logger, ConfigEntry<bool> enableDebugLogging)
        {
            source = logger;
            debugLogging = enableDebugLogging;
        }

        public static void Reset()
        {
            source = null;
            debugLogging = null;
        }

        public static void Log(object message)
        {
            if (source != null && debugLogging != null && debugLogging.Value)
                source.LogDebug(message);
        }

        public static void LogWarning(object message)
        {
            if (source != null)
                source.LogWarning(message);
            else
                UnityEngine.Debug.LogWarning(message);
        }

        public static void LogError(object message)
        {
            if (source != null)
                source.LogError(message);
            else
                UnityEngine.Debug.LogError(message);
        }
    }
} // namespace
