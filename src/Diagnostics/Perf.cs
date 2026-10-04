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
        public void InstrumentedEnforceOverrides()
        {
            // Called from UpdateImpl only (the plugin's willRenderCanvases path was
            // dead and has been removed). The frame guard keeps it idempotent.
            if (enforceOverridesFrame == Time.frameCount)
                return;
            enforceOverridesFrame = Time.frameCount;
            perfSw.Restart();
            EnforceNativeHudVisibility();
            EnforceNativeReplayOverrides();
            EnforceNativeSpectatedTextStyle();
            perfEnforceMs += (float)perfSw.Elapsed.TotalMilliseconds;
        }

        public void InstrumentedTickLeaderboardSearch()
        {
            perfSw.Restart();
            TickLeaderboardSearch();
            perfLbMs += (float)perfSw.Elapsed.TotalMilliseconds;
        }

        private readonly System.Diagnostics.Stopwatch perfUpdSw = new System.Diagnostics.Stopwatch();
        private float perfUpdMs;
        // Diagnostic: flags any individual unmeasured tick that spikes, so a hidden
        // periodic cost can't hide behind the aggregate Update timer.
        private readonly System.Diagnostics.Stopwatch slowTickSw = new System.Diagnostics.Stopwatch();
        private float slowTickLogAt = 0f;
        private void LogSlowTick(string name)
        {
            float ms = (float)slowTickSw.Elapsed.TotalMilliseconds;
            if (ms > 4f && ModLog.DebugEnabled && Time.unscaledTime >= slowTickLogAt)
            {
                slowTickLogAt = Time.unscaledTime + 5f;
                pluginLog.LogInfo("[GhostLines] slow tick " + name + " " + ms.ToString("F1") + "ms");
            }
        }
        private readonly System.Diagnostics.Stopwatch perfLateSw = new System.Diagnostics.Stopwatch();
        private float perfLateMs;

        private void PerfWindowTick()
        {
            if (Time.unscaledDeltaTime > perfMaxFrame)
                perfMaxFrame = Time.unscaledDeltaTime;
            if (!perfWindowActive)
            {
                perfWindowActive = true;
                perfWindowStart = Time.unscaledTime;
                perfGc0 = System.GC.CollectionCount(0);
                perfGc1 = System.GC.CollectionCount(1);
                return;
            }
            if (Time.unscaledTime - perfWindowStart < 1f)
                return;
            // Pushes buffered debug-file lines out even when logging goes quiet.
            if (ModLog.DebugEnabled)
                ModLog.FlushLogFile();
            Prof.Flush(perfMaxFrame * 1000f, Time.unscaledTime - perfWindowStart);
            int g0 = System.GC.CollectionCount(0) - perfGc0;
            int g1 = System.GC.CollectionCount(1) - perfGc1;
            if (ModLog.DebugEnabled)
                ModLog.LogFile("[GhostLines] perf 1s: maxFrame=" + (perfMaxFrame * 1000f).ToString("F1")
                + "ms gc0=" + g0 + " gc1=" + g1
                + " lb=" + perfLbMs.ToString("F1")
                + " match=" + perfMatchMs.ToString("F1")
                + " scan=" + perfScanMs.ToString("F1")
                + " pip=" + perfPipMs.ToString("F1")
                + " chatTrack=" + perfChatTrackMs.ToString("F1")
                + " guiChat=" + perfChatMs.ToString("F1")
                + " chatN=" + perfChatN
                + " chatE=" + perfChatEntriesN
                + " chatGate=" + perfChatGateMs.ToString("F1")
                + " chatBuild=" + perfChatBuildMs.ToString("F1")
                + " chatPick=" + perfChatPickerMs.ToString("F1")
                + " chatSel=" + (perfChatSel ? 1 : 0)
                + " chatHov=" + (perfChatHover ? 1 : 0)
                + " preCull=" + perfPreCullMs.ToString("F1")
                + " postCull=" + perfPostCullMs.ToString("F1")
                + " guiAll=" + perfGuiAllMs.ToString("F1")
                + " upd=" + perfUpdMs.ToString("F1")
                + " late=" + perfLateMs.ToString("F1")
                + " inGate=" + perfInGateMs.ToString("F1")
                + " bind=" + perfBindMs.ToString("F1")
                + " pwMouse=" + perfPwMouseMs.ToString("F1")
                + " mpIn=" + perfMpInMs.ToString("F1")
                + " ovMouse=" + perfOvMouseMs.ToString("F1")
                + " drone=" + perfDroneMs.ToString("F1")
                + " diag=" + perfDiagMs.ToString("F1")
                + " lbUi=" + perfLbUiMs.ToString("F1")
                + " cam=" + perfCamMs.ToString("F1")
                + " mpPip=" + perfMpPipMs.ToString("F1")
                + " freeCam=" + perfFreeCamMs.ToString("F1")
                + " fcKeys=" + perfFcKeysMs.ToString("F1")
                + " fcZoom=" + perfFcZoomMs.ToString("F1")
                + " nativePip=" + perfNativePipMs.ToString("F1")
                + " enforce=" + perfEnforceMs.ToString("F1"));
            perfWindowStart = Time.unscaledTime;
            perfMaxFrame = 0f;
            perfGc0 = System.GC.CollectionCount(0);
            perfGc1 = System.GC.CollectionCount(1);
            perfLbMs = 0f;
            perfMatchMs = 0f;
            perfScanMs = 0f;
            perfLobbyMs = 0f;
            perfPipMs = 0f;
            perfChatTrackMs = 0f;
            perfChatMs = 0f;
            perfChatGateMs = 0f;
            perfChatBuildMs = 0f;
            perfChatPickerMs = 0f;
            perfChatN = 0;
            perfChatEntriesN = 0;
            perfChatSel = false;
            perfChatHover = false;
            perfPreCullMs = 0f;
            perfPostCullMs = 0f;
            perfGuiAllMs = 0f;
            perfUpdMs = 0f;
            perfLateMs = 0f;
            perfInGateMs = 0f;
            perfLbUiMs = 0f;
            perfMpPipMs = 0f;
            perfFreeCamMs = 0f;
            perfNativePipMs = 0f;
            perfCamMs = 0f;
            perfFcKeysMs = 0f;
            perfFcZoomMs = 0f;
            perfBindMs = 0f;
            perfPwMouseMs = 0f;
            perfMpInMs = 0f;
            perfOvMouseMs = 0f;
            perfDroneMs = 0f;
            perfDiagMs = 0f;
            perfEnforceMs = 0f;
        }
    }
}
