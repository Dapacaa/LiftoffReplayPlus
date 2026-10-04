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
        private void OnGUI()
        {
            ourInputScope++;
            try
            {
            // If the game's chat field has focus, make sure no IMGUI control of
            // ours holds keyboard focus — otherwise Unity routes keystrokes to
            // IMGUI and the uGUI chat field's caret blinks but nothing types.
            try
            {
                if (mpChatInputField != null && mpChatInputField.isFocused && GUIUtility.keyboardControl != 0)
                    GUIUtility.keyboardControl = 0;
            }
            catch { }
            perfGuiAllSw.Restart();
            Prof.Span ps = Prof.Begin();
            OnGUIImpl();
            Prof.End(Prof.OnGui, ps);
            perfGuiAllMs += (float)perfGuiAllSw.Elapsed.TotalMilliseconds;
            }
            finally { ourInputScope--; }
        }

        private void OnGUIImpl()
        {
            Event guiEvent = Event.current;
            if (!replayReady && bindingListeningFor == null && IsContentSceneActive() && guiEvent != null && guiEvent.type == EventType.KeyDown &&
                guiEvent.keyCode != KeyCode.None && guiEvent.keyCode == GhostLinesPlugin.GetBinding("mpmenu") && Time.frameCount != mpF10ToggleFrame)
            {
                mpF10ToggleFrame = Time.frameCount;
                multiplayerPipMenuOpen = !multiplayerPipMenuOpen;
                SaveMpMenuOpen(multiplayerPipMenuOpen);
                multiplayerPipLastMouseMove = Time.unscaledTime;
                multiplayerPipMenuFade = 1f;
                overlayMouseMovedAt = Time.unscaledTime;
                if (multiplayerPipMenuOpen)
                    mpMenuIdleAlpha = 1f;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Multiplayer menu key toggled (GUI): " + multiplayerPipMenuOpen);
                guiEvent.Use();
            }
            // Gate Splits / Lap History and the lobby alert are drawn only in the
            // non-replay branch below, so they must keep OnGUIImpl alive even when
            // no replay/MP/chat UI is otherwise active (e.g. a solo race).
            bool splitsPanelWanted = Prefs.gateSplitsConfig != null && Prefs.gateSplitsConfig.Value;
            bool lobbyAlertWanted = Time.unscaledTime < lobbyAlertUntil;
            bool popupDateTooltipWanted = !string.IsNullOrEmpty(lbPopupDateHoverText);
            if (!replayReady && !mpShowDroneNames && mpChatStates.Count == 0 &&
                multiplayerPipMenuFade < 0.01f && multiplayerPipWindows.Count == 0 && liveInputGraphs.Count == 0 &&
                !lobbyBrowserOpen && !lbReplayWindowOpen && !splitsPanelWanted && !raceHistoryOpen &&
                !lobbyAlertWanted && !popupDateTooltipWanted && !bindingsOpen)
                return;

            if (whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1);
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
            }

            // IMGUI always paints over the game's uGUI, so the only way to keep
            // the pause/ESC menu on top is to not draw the mod UI while it is
            // open. The date tooltip stays: it belongs to the menu's leaderboard.
            if (IsGameMenuOpenCached())
            {
                DrawLbPopupDateTooltip();
                GUI.color = Color.white;
                return;
            }

            UpdateOverlayResolution();
            currentPlaybackOverlayRectValid = false;
            currentStickCamOverlayRectValid = false;
            if (replayReady)
                ComputeStickCamOverlayRect();
            if (replayReady)
                ComputePlaybackOverlayRect();
            bool frontGraph = uiFrontLayer == UI_LAYER_STICKGRAPH;
            bool frontSettings = uiFrontLayer == UI_LAYER_SETTINGS;
            bool frontBindings = uiFrontLayer == UI_LAYER_BINDINGS;
            bool frontPip = uiFrontLayer == UI_LAYER_PIP;
            bool frontStickCam = uiFrontLayer == UI_LAYER_STICKCAM;
            bool frontPlaybar = uiFrontLayer == UI_LAYER_PLAYBAR;
            bool frontMpPip = uiFrontLayer == UI_LAYER_MPPIP;
            try { Prof.Span gs = Prof.Begin(); DrawMultiplayerPip(); Prof.End(Prof.GuiMpMenu, gs); }
            catch (System.Exception multiplayerPipDrawEx0) { ModLog.LogFile("[GhostLines] MPPIP Draw EX " + multiplayerPipDrawEx0); }
            // MP PiP windows sit behind the live graphs unless the MP layer is the
            // active/front one (clicked last); then they are drawn after the graphs
            // below so the clicked window goes on top, like every other overlay.
            if (!frontMpPip)
            {
                try { Prof.Span gs = Prof.Begin(); DrawMultiplayerPipWindows(); Prof.End(Prof.GuiMpWindows, gs); }
                catch (System.Exception multiplayerPipDrawEx1) { ModLog.LogFile("[GhostLines] MPPIP Draw EX " + multiplayerPipDrawEx1); }
            }
            if (!frontGraph)
                { Prof.Span gs = Prof.Begin(); DrawStickGraph(); Prof.End(Prof.GuiStickGraph, gs); }
            if (replayReady && !frontSettings)
                { Prof.Span gs = Prof.Begin(); DrawSettingsPanel(); Prof.End(Prof.GuiSettings, gs); }
            if (!frontBindings)
                { Prof.Span gs = Prof.Begin(); DrawBindingsWindow(); Prof.End(Prof.GuiBindings, gs); }
            if (!frontPip && pipEnabled)
                { Prof.Span gs = Prof.Begin(); DrawPiPWindows(); Prof.End(Prof.GuiPip, gs); }
            if (!frontStickCam)
                { Prof.Span gs = Prof.Begin(); DrawStickCam(); Prof.End(Prof.GuiStickCam, gs); }
            if (replayReady && !frontPlaybar)
                { Prof.Span gs = Prof.Begin(); DrawBottomScrubber(); Prof.End(Prof.GuiScrubber, gs); }
            if (frontGraph)
                { Prof.Span gs = Prof.Begin(); DrawStickGraph(); Prof.End(Prof.GuiStickGraph, gs); }
            if (!frontGraph)
                { Prof.Span gs = Prof.Begin(); DrawLiveGraph(false); Prof.End(Prof.GuiLiveGraph, gs); }
            if (replayReady && frontSettings)
                { Prof.Span gs = Prof.Begin(); DrawSettingsPanel(); Prof.End(Prof.GuiSettings, gs); }
            if (frontBindings)
                { Prof.Span gs = Prof.Begin(); DrawBindingsWindow(); Prof.End(Prof.GuiBindings, gs); }
            if (frontPip && pipEnabled)
                { Prof.Span gs = Prof.Begin(); DrawPiPWindows(); Prof.End(Prof.GuiPip, gs); }
            if (mpShowDroneNames)
                { Prof.Span gs = Prof.Begin(); DrawMultiplayerNames(); Prof.End(Prof.GuiNames, gs); }
            perfSw.Restart();
            { Prof.Span gs = Prof.Begin(); DrawChatOverlays(); Prof.End(Prof.GuiChat, gs); }
            perfChatMs += (float)perfSw.Elapsed.TotalMilliseconds;
            if (frontGraph)
                { Prof.Span gs = Prof.Begin(); DrawLiveGraph(true); Prof.End(Prof.GuiLiveGraph, gs); }
            if (frontMpPip)
            {
                try { Prof.Span gs = Prof.Begin(); DrawMultiplayerPipWindows(); Prof.End(Prof.GuiMpWindows, gs); }
                catch (System.Exception multiplayerPipDrawEx2) { ModLog.LogFile("[GhostLines] MPPIP Draw EX " + multiplayerPipDrawEx2); }
            }
            if (frontStickCam)
                { Prof.Span gs = Prof.Begin(); DrawStickCam(); Prof.End(Prof.GuiStickCam, gs); }
            if (replayReady && frontPlaybar)
                { Prof.Span gs = Prof.Begin(); DrawBottomScrubber(); Prof.End(Prof.GuiScrubber, gs); }
            { Prof.Span gs = Prof.Begin(); HandleDroneLabelClick(); Prof.End(Prof.GuiLabelClick, gs); }
            if (!replayReady && lobbyBrowserOpen)
            {
                try { Prof.Span gs = Prof.Begin(); DrawLobbyBrowser(); Prof.End(Prof.GuiLobby, gs); }
                catch (System.Exception lobbyBrowserEx) { ModLog.LogFile("[GhostLines] lobby browser EX " + lobbyBrowserEx); }
            }
            if (!replayReady)
            {
                try { Prof.Span gs = Prof.Begin(); DrawLobbyAlert(); Prof.End(Prof.GuiLobby, gs); }
                catch (System.Exception lobbyAlertEx) { ModLog.LogFile("[GhostLines] lobby alert EX " + lobbyAlertEx); }
                try { Prof.Span gs = Prof.Begin(); DrawGateSplitsPanel(); Prof.End(Prof.GuiSplits, gs); }
                catch (System.Exception splitsEx) { ModLog.LogFile("[GhostLines] gate splits EX " + splitsEx); }
            }
            if (replayReady && PhotonInRoom())
                { Prof.Span gs = Prof.Begin(); DrawBackToLobbyButton(); Prof.End(Prof.GuiMisc, gs); }
            DrawLbPopupDateTooltip();
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

        private class PiPWindow
        {
            public GhostLineEntry entry;
            public Rect rect;
            public GameObject camObject;
            public Camera cam;
            public RenderTexture rt;
            public int rtW = -1;
            public int rtH = -1;
            public bool rtDirty = false;
            public bool dragging = false;
            public bool dragCandidate = false;
            public Vector2 dragStartMouse;
            public Vector2 dragOffset;
            public bool dropdownOpen = false;
            public bool resizing = false;
            public int resizeEdge = 0;
            public int edgeHover = 0;
            public float fadeAmount = 1f;
            public bool pinned = true;
            public Vector2 resizeStartMouse;
            public Rect resizeStartRect;
            public float nextRenderAt = 0f;
            public float lastRenderTime = -1f;
            public bool renderedOnce = false;
            public bool renderDiagLogged = false;
            public float chromeAlpha = 1f;
            public NativePipExternal nativePip;
            public List<PipDockEntry> docked = new List<PipDockEntry>();
        }

        private string GetPilotLabel(GhostLineEntry entry, int fallbackIndex)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.pilotName))
                return entry.pilotName;
            if (fallbackIndex >= 0)
                return "Pilot " + (fallbackIndex + 1);
            return "Pilot";
        }
    }
}
