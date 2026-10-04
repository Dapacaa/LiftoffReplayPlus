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
        private bool IsUiLayerAt(int layer, Vector2 p)
        {
            switch (layer)
            {
                case UI_LAYER_STICKGRAPH:
                    for (int i = 0; i < stickGraphs.Count; i++)
                        if (stickGraphs[i].fadeAmount > 0.01f && stickGraphs[i].rect.Contains(p))
                            return true;
                    for (int i = 0; i < liveInputGraphs.Count; i++)
                        if (liveInputGraphs[i].fadeAmount > 0.01f && liveInputGraphs[i].rect.Contains(p))
                            return true;
                    return false;
                case UI_LAYER_SETTINGS:
                    return settingsOpen && GetSettingsPanelRect().Contains(p);
                case UI_LAYER_BINDINGS:
                    return bindingsOpen && GetBindingsWindowRect().Contains(p);
                case UI_LAYER_PIP:
                    return IsPiPMouseBlocked(p);
                case UI_LAYER_STICKCAM:
                    return currentStickCamOverlayRectValid && currentStickCamOverlayRect.Contains(p);
                case UI_LAYER_MPPIP:
                    for (int i = 0; i < multiplayerPipWindows.Count; i++)
                    {
                        MultiplayerPipWindow mw = multiplayerPipWindows[i];
                        if (mw != null && mw.rect.Contains(p))
                            return true;
                    }
                    return false;
                case UI_LAYER_PLAYBAR:
                    return currentPlaybackOverlayRectValid && currentPlaybackOverlayRect.Contains(p);
                default:
                    return false;
            }
        }

        private int UiLayerOrder(int layer)
        {
            if (layer == uiFrontLayer)
                return UI_LAYER_PLAYBAR;
            if (layer == UI_LAYER_MPPIP)
                return 1;
            return layer < uiFrontLayer ? layer : layer - 1;
        }

        private bool IsUiBlockedFor(int layer, Vector2 p)
        {
            if (lobbyBrowserOpen && !lobbyBrowserSelfDraw && lobbyBrowserRect.width > 1f && lobbyBrowserRect.Contains(p))
                return true;
            if (Time.unscaledTime < lobbyAlertUntil && lobbyAlertRect.width > 1f && lobbyAlertRect.Contains(p))
                return true;
            int order = UiLayerOrder(layer);
            for (int l = 0; l <= UI_LAYER_MPPIP; l++)
            {
                if (l == layer)
                    continue;
                if (UiLayerOrder(l) > order && IsUiLayerAt(l, p))
                    return true;
            }
            return false;
        }

        private bool IsDroneLabelInteractionBlocked(Vector2 mousePosition)
        {
            bool stickGraphBusy = false;
            for (int i = 0; i < stickGraphs.Count; i++)
            {
                StickGraphState g = stickGraphs[i];
                if (g.dragging || g.dragCandidate || g.resizing)
                    stickGraphBusy = true;
            }
            for (int i = 0; i < liveInputGraphs.Count; i++)
            {
                LiveGraphState lg = liveInputGraphs[i];
                if (lg.dragging || lg.dragCandidate || lg.resizing)
                    stickGraphBusy = true;
            }
            return playbackMouseCaptured || draggingScrubber || isDraggingHandle || draggingStickCam ||
                settingsDrag.Dragging || stickCamResizing || playbackResizing ||
                stickGraphBusy ||
                (currentPlaybackOverlayRectValid && currentPlaybackOverlayRect.Contains(mousePosition)) ||
                (currentStickCamOverlayRectValid && currentStickCamOverlayRect.Contains(mousePosition)) ||
                IsUiBlockedFor(0, mousePosition);
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
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                if (multiplayerPipTargets[i] != null)
                    multiplayerPipTargets[i].tagVisible = visible;
            }
            GhostLinesPlugin.SaveNameTagsPreference();
            GhostLinesPlugin.SaveGlobalDisplayPreferences();
            MarkPiPWindowsDirty();
            if (!visible)
            {
                droneLabelHitboxes.Clear();
                droneLabelClickCandidate = null;
                SetHoveredDroneLabel(null);
            }

            // Force the enabled renderers and projected click targets to agree immediately.
            UpdateDroneMarkers();
        }
    }
}
