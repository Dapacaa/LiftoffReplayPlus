using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace LiftoffGhostLines
{
    // Persisted settings. Bound in GhostLinesPlugin.Awake; read by every feature.
    internal static class Prefs
    {
        internal static ConfigFile preferencesConfig;
        internal static ConfigEntry<bool> stickCamEnabledConfig;
        internal static ConfigEntry<float> stickCamSizeConfig;
        internal static ConfigEntry<float> playbackUiSizeConfig;
        internal static ConfigEntry<bool> darkModeConfig;
        internal static ConfigEntry<bool> smoothDroneTrailsConfig;
        internal static ConfigEntry<bool> autoDownloadWorkshopConfig;
        internal static ConfigEntry<bool> autoJoinLobbyConfig;
        internal static ConfigEntry<bool> gateSplitsConfig;
        internal static ConfigEntry<bool> mpMenuOpenConfig;
        internal static ConfigEntry<bool> gateSplitsSegmentsConfig;
        internal static ConfigEntry<bool> lapHistoryVsBestConfig;
        internal static ConfigEntry<bool> autoAlertLobbyConfig;
        internal static ConfigEntry<bool> smoothingEnabledConfig;
        internal static ConfigEntry<bool> showDeltaDisplayConfig;
        internal static ConfigEntry<bool> showNameTagsConfig;
        internal static ConfigEntry<bool> linesThroughObjectsConfig;
        internal static ConfigEntry<bool> xrayEnabledConfig;
        internal static ConfigEntry<bool> settingsPanelOpenConfig;
        internal static ConfigEntry<bool> stickCamPositionCustomizedConfig;
        internal static ConfigEntry<float> stickCamPositionXConfig;
        internal static ConfigEntry<float> stickCamPositionYConfig;
        internal static ConfigEntry<bool> playbackUiPositionCustomizedConfig;
        internal static ConfigEntry<float> playbackUiPositionXConfig;
        internal static ConfigEntry<float> playbackUiPositionYConfig;
        internal static ConfigEntry<bool> settingsPanelPositionCustomizedConfig;
        internal static ConfigEntry<float> settingsPanelPositionXConfig;
        internal static ConfigEntry<float> settingsPanelPositionYConfig;
        internal static ConfigEntry<bool> pipEnabledConfig;
        internal static ConfigEntry<bool> pipRefreshAutoConfig;
        internal static ConfigEntry<int> pipRefreshHzConfig;
        internal static ConfigEntry<float> pipResolutionScaleConfig;
        internal static ConfigEntry<float> pipFarClipConfig;
        internal static ConfigEntry<bool> pipShowNamesDeltaConfig;
        internal static ConfigEntry<bool> pipExternalMonitorConfig;
        internal static ConfigEntry<string> chatColorPreset1Config;
        internal static ConfigEntry<string> chatColorPreset2Config;
        internal static ConfigEntry<string> chatColorPreset3Config;
        internal static ConfigEntry<string> chatColorPreset4Config;
        internal static ConfigEntry<bool> gcIncrementalConfig;
        internal static ConfigEntry<float> gcCollectIntervalConfig;
        internal static Dictionary<string, ConfigEntry<KeyCode>> bindingConfigs = new Dictionary<string, ConfigEntry<KeyCode>>();
        internal static Dictionary<string, ConfigEntry<KeyCode>> droneBindingConfigs = new Dictionary<string, ConfigEntry<KeyCode>>();

        // ---- Debounced saving ------------------------------------------------
        // SaveOnConfigSet is off (see Attach): a drag release or toggle sets
        // several entries, and each set used to rewrite the whole file. Any change
        // now marks the file dirty and the poller writes it once, SaveDelay after
        // the last change. FlushSave writes immediately (quit / teardown).
        private const float SaveDelay = 1f;
        private static bool saveDirty;
        private static float saveDueAt;

        internal static void Attach(ConfigFile config)
        {
            preferencesConfig = config;
            config.SaveOnConfigSet = false;
            config.SettingChanged -= OnSettingChanged;
            config.SettingChanged += OnSettingChanged;
        }

        private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            RequestSave();
        }

        internal static void RequestSave()
        {
            saveDirty = true;
            saveDueAt = Time.unscaledTime + SaveDelay;
        }

        internal static void Tick()
        {
            if (saveDirty && Time.unscaledTime >= saveDueAt)
                FlushSave();
        }

        internal static void FlushSave()
        {
            if (!saveDirty || preferencesConfig == null)
                return;
            saveDirty = false;
            Prof.Span ps = Prof.Begin();
            try { preferencesConfig.Save(); }
            catch (System.Exception ex) { ModLog.LogWarning("[GhostLines] config save failed: " + ex.Message); }
            Prof.End(Prof.ConfigSave, ps);
            if (ModLog.DebugEnabled)
                ModLog.LogFile("[GhostLines] config saved");
        }

        // ---- Key migration ---------------------------------------------------
        // Moves a value saved under an old key to its new key before the new key is
        // bound. Unbound keys sit in ConfigFile's private OrphanedEntries (the raw
        // strings read from disk), so this renames the raw entry: the next Bind
        // parses it, and the old key disappears on the next save. No-op when the
        // old key is absent or the new key already has a saved value.
        private static System.Reflection.PropertyInfo orphanedEntriesProperty;

        internal static void MigrateKey(ConfigFile config, string section, string oldKey, string newKey)
        {
            if (config == null || oldKey == newKey)
                return;
            try
            {
                if (orphanedEntriesProperty == null)
                    orphanedEntriesProperty = typeof(ConfigFile).GetProperty("OrphanedEntries",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var orphans = orphanedEntriesProperty != null
                    ? orphanedEntriesProperty.GetValue(config, null) as Dictionary<ConfigDefinition, string>
                    : null;
                if (orphans == null)
                    return;
                var oldDef = new ConfigDefinition(section, oldKey);
                var newDef = new ConfigDefinition(section, newKey);
                string raw;
                if (!orphans.TryGetValue(oldDef, out raw))
                    return;
                orphans.Remove(oldDef);
                if (!orphans.ContainsKey(newDef) && !config.ContainsKey(newDef))
                    orphans[newDef] = raw;
                RequestSave();
            }
            catch (System.Exception ex)
            {
                ModLog.LogWarning("[GhostLines] config key migration failed for " + oldKey + ": " + ex.Message);
            }
        }
    }
}
