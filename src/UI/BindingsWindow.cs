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
        // Opened from the replay playback bar or the MP menu.
        private void ToggleBindingsWindow()
        {
            bindingsOpen = !bindingsOpen;
            bindingListeningFor = null;
            if (bindingsOpen)
            {
                bindingsOpenedInReplay = replayReady;
                EnsureBindingsWindowRect();
                bindingsFadeAmount = 1f;
                uiFrontLayer = UI_LAYER_BINDINGS;
            }
        }

        private void DrawBindingsWindow()
        {
            // Close on entering or leaving a replay, so a window opened from one
            // menu doesn't turn up on its own in the other.
            if (bindingsOpen && bindingsOpenedInReplay != replayReady)
            {
                bindingsOpen = false;
                bindingListeningFor = null;
                bindingsDrag.Cancel();
            }
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
            Rect close = new Rect(panel.xMax - 36f, panel.y + 6f, 28f, 22f);
            Rect pin = new Rect(panel.xMax - 64f, panel.y + 7f, 22f, 20f);
            if (e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) && !close.Contains(e.mousePosition)
                && !IsUiBlockedFor(UI_LAYER_BINDINGS, e.mousePosition))
            {
                uiFrontLayer = UI_LAYER_BINDINGS;
                bindingsDrag.Arm(e.mousePosition, new Vector2(panel.x, panel.y));
            }
            Vector2 bindingsPos = new Vector2(bindingsWindowRect.x, bindingsWindowRect.y);
            if (bindingsDrag.Drag(e, false, ref bindingsPos))
            {
                bindingsWindowRect.x = bindingsPos.x;
                bindingsWindowRect.y = bindingsPos.y;
                EnsureBindingsWindowRect(false);
            }
            if (e.type == EventType.MouseUp && e.button == 0 && bindingsDrag.Dragging)
            {
                bindingsDrag.End();
                e.Use();
            }

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 7f, 220f, 20f), "Control Bindings", settingsHeaderStyle);
            EnsureIcons();
            if (DrawStyledToggleButton(pin, iconPin, bindingsPinned, alpha, true, UI_LAYER_BINDINGS))
                bindingsPinned = !bindingsPinned;
            GUI.color = ThemeCloseLabel(alpha);
            GUI.Label(new Rect(close.x, close.y - 3f, close.width, close.height),
                "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);
            if (e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
            {
                bindingsOpen = false;
                bindingListeningFor = null;
                bindingsDrag.Cancel();
                e.Use();
                GUI.color = old;
                return;
            }

            float x = panel.x + 14f;
            float w = panel.width - 28f;
            float rowH = 26f;
            float btnW = 76f;
            float btnH = 20f;
            float btnX = panel.xMax - 92f;

            int totalRows = GhostLinesPlugin.BindingIds.Length;
            int visibleRows = Mathf.Max(1, Mathf.FloorToInt((panel.height - 42f - 44f) / rowH));
            bool scrollNeeded = totalRows > visibleRows;
            float maxScroll = Mathf.Max(0f, totalRows - visibleRows);
            bindingsScroll = Mathf.Clamp(bindingsScroll, 0f, maxScroll);
            float listTop = panel.y + 42f;
            float listBottom = listTop + visibleRows * rowH;
            float rowY = listTop - bindingsScroll * rowH;

            if (scrollNeeded && e.type == EventType.ScrollWheel &&
                new Rect(panel.x, listTop, panel.width, listBottom - listTop).Contains(e.mousePosition))
            {
                bindingsScroll -= Input.mouseScrollDelta.y * 3f;
                e.Use();
            }

            for (int i = 0; i < totalRows; i++)
            {
                float ry = rowY + rowH * i;
                if (ry + rowH < listTop || ry > listBottom)
                    continue;
                string id = GhostLinesPlugin.BindingIds[i];
                bool listening = bindingListeningFor == id;

                GUI.color = new Color(1f, 1f, 1f, 0.05f * alpha);
                if (i % 2 == 1)
                    GUI.DrawTexture(new Rect(x, ry, w, rowH), whiteTex);

                string labelText = GhostLinesPlugin.BindingLabel(id);
                float rowMidY = ry + rowH * 0.5f;
                float textWidth = CalcLabelWidth(settingsSmallStyle, labelText);
                float lineStart = x + Mathf.Min(textWidth + 6f, w - 92f);
                float lineEnd = btnX - 3f;
                GUI.color = new Color(0.55f, 0.55f, 0.6f, 0.35f * alpha);
                if (lineEnd > lineStart)
                    GUI.DrawTexture(new Rect(lineStart, rowMidY - 0.5f, lineEnd - lineStart, 1f), whiteTex);

                GUI.color = listening ? new Color(0.85f, 0.62f, 0.16f, 0.95f * alpha) : ThemeLabel(alpha);
                GUI.Label(new Rect(x, ry, w - 92f, rowH), labelText, settingsSmallStyle);

                Rect keyRect = new Rect(btnX, ry + (rowH - btnH) / 2f, btnW, btnH);
                string keyText = listening ? "Press key..." : GhostLinesPlugin.BindingKeyName(id);
                if (DrawSettingsOption(keyRect, keyText, listening, alpha, TextAnchor.MiddleCenter, UI_LAYER_BINDINGS))
                    ToggleBindingListen(id);
            }

            Rect resetRect = new Rect(panel.x + (panel.width - 104f) / 2f, listBottom + 6f, 104f, 20f);
            if (DrawSettingsOption(resetRect, "Reset Defaults", false, alpha, TextAnchor.MiddleCenter, UI_LAYER_BINDINGS))
            {
                bindingListeningFor = null;
                GhostLinesPlugin.ResetAllBindings();
            }

            if (scrollNeeded)
            {
                Rect track = new Rect(panel.xMax - 10f, listTop, 5f, listBottom - listTop);
                GUI.color = new Color(1f, 1f, 1f, 0.08f * alpha);
                GUI.DrawTexture(track, whiteTex);
                float thumbH = Mathf.Max(18f, (listBottom - listTop) * visibleRows / totalRows);
                float thumbY = track.y + (track.height - thumbH) * (bindingsScroll / maxScroll);
                Rect thumb = new Rect(track.x, thumbY, 5f, thumbH);
                GUI.color = new Color(0.7f, 0.7f, 0.75f, 0.55f * alpha);
                GUI.DrawTexture(thumb, whiteTex);
                if (e.type == EventType.MouseDown && e.button == 0 && thumb.Contains(e.mousePosition))
                {
                    bindingsScrollDragging = true;
                    bindingsDrag.Cancel();
                }
                if (bindingsScrollDragging && e.type == EventType.MouseDrag && e.button == 0)
                {
                    float frac = Mathf.Clamp01((e.mousePosition.y - track.y) / Mathf.Max(1f, track.height - thumbH));
                    bindingsScroll = frac * maxScroll;
                    e.Use();
                }
                if (bindingsScrollDragging && e.type == EventType.MouseUp && e.button == 0)
                    bindingsScrollDragging = false;
            }

            // A press that no control claimed drags the window straight away.
            if (e.type == EventType.MouseDown && e.button == 0 && bindingsDrag.Armed)
            {
                if (settingsControlPressed)
                    bindingsDrag.Cancel();
                else
                    bindingsDrag.Promote();
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
            float panelH = Mathf.Min(GetBindingsWindowHeight(), Mathf.Max(100f, Screen.height - 24f));
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

        private float wsSectionLogAt = 0f;

        private bool wsLogHooked = false;
        // Set once the level-setup log hook has reported this session's track/race.
        private bool wsLogCaptured = false;
        private bool wsLogCaptureActive = false;
        private int wsPendingItemKind = 0;
        private string wsPendingItemName = null;

        private System.Reflection.MethodBase gameJoinMethod = null;
        private string gameJoinTypeName = null;
        private Photon.Realtime.LoadBalancingClient watcherClient = null;
        private LobbyWatcherCallbacks watcherCallbacks = null;
        private float watcherNextConnectAt = 0f;
        private bool lobbyBrowserOpen = false;
        private Rect lobbyBrowserRect = new Rect(-1f, -1f, 0f, 0f);
        private float lobbyBrowserW = 0f;
        private float lobbyBrowserH = 0f;
        private bool lobbyBrowserResizing = false;
        private int lobbyBrowserResizeEdge = 0;
        private int lobbyBrowserResizeEdgeHover = 0;
        private Vector2 lobbyBrowserResizeStartMouse = Vector2.zero;
        private Rect lobbyBrowserResizeStartRect = new Rect(0f, 0f, 0f, 0f);
        private float lobbyBrowserScrollY = 0f;
        private bool lobbyBrowserThumbDrag = false;
        private float lobbyBrowserThumbDragStartY = 0f;
        private float lobbyBrowserThumbDragStartScroll = 0f;
        private bool lobbyBrowserSelfDraw = false;
        private GUIStyle lobbyTitleStyle = null;
        private GUIStyle lobbyNameStyle = null;
        private GUIStyle lobbyInfoStyle = null;
        private GUIStyle lobbyCountStyle = null;
        private GUIStyle lobbyJoinStyle = null;
        private GUIStyle lobbyPrioStyle = null;
        private GUIStyle lobbyCloseStyle = null;
        private Vector2 lobbyBrowserDragOffset = Vector2.zero;
        private readonly WindowDrag lobbyBrowserDrag = new WindowDrag();
        private Texture2D lobbyCircleOnTex = null;
        private Texture2D lobbyCircleOffTex = null;

        private Texture2D MakeSelectionCircleTexture(bool filled)
        {
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = (size - 1) * 0.5f;
            float outerR = size * 0.5f - 3f;
            float innerR = outerR - 6f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c = Color.clear;
                    if (d <= innerR)
                        c = filled ? new Color(1f, 1f, 1f, 1f) : Color.clear;
                    else if (d <= outerR)
                        c = Color.white;
                    else if (d <= outerR + 1f)
                        c = new Color(1f, 1f, 1f, outerR + 1f - d);
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
        private readonly Dictionary<string, LobbyWatchRoom> lobbyRoomStates = new Dictionary<string, LobbyWatchRoom>(StringComparer.Ordinal);
        private readonly List<string> selectedLobbies = new List<string>();
        private float lobbyJoinBusyUntil = 0f;
        private int lobbyPropsLogCount = 0;
        private readonly Dictionary<string, float> lobbyAlertCooldown = new Dictionary<string, float>(StringComparer.Ordinal);
        private string lobbyAlertRoom = null;
        private string lobbyAlertCount = "";
        private float lobbyAlertUntil = 0f;
        private Rect lobbyAlertRect = new Rect(-1f, -1f, 0f, 0f);
        private string pendingLobbyJoin = null;
        private float pendingLobbyJoinAt = 0f;
        private bool lobbySwitchActive = false;
        private string pendingLobbyReturn = null;
        private float pendingLobbyReturnAt = 0f;
        private readonly List<LobbyWatchRoom> lobbyBrowserRows = new List<LobbyWatchRoom>();
        private float lobbyBrowserRowsAt = -999f;

        // Prebuilt row strings, refreshed with the 0.5s row cache instead of per event.
        private sealed class LobbyRowText
        {
            public string Title;
            public string Info;
            public string Count;
        }
        private readonly Dictionary<string, LobbyRowText> lobbyRowTextCache = new Dictionary<string, LobbyRowText>();
    }
}
