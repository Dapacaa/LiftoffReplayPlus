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
        private void TickLobbyWatcher()
        {
            TickLobbyJoinPending();
            TickLobbyReturnPending();
            MaybePatchLoadFast();
            // Replay mode must not run the lobby watcher: watching a replay while
            // still in a Photon room would otherwise reconnect the watcher every
            // frame (the stop below sets no backoff on its own).
            if (replayReady && watcherClient != null)
                StopLobbyWatcher("replay active");
            bool needWatcher = Photon.Pun.PhotonNetwork.InRoom && !replayReady;
            if (needWatcher)
            {
                if (watcherClient == null && Time.unscaledTime >= watcherNextConnectAt)
                    StartLobbyWatcher();
                if (watcherClient != null)
                {
                    // Service() is the Photon pump; per-frame pumping allocates enough
                    // to trigger a GC every couple of seconds. The watcher is a passive
                    // lobby observer — 10Hz is plenty.
                    if (Time.unscaledTime >= watcherServiceAt)
                    {
                        watcherServiceAt = Time.unscaledTime + 0.1f;
                        try { watcherClient.Service(); }
                        catch { }
                    }
                }
            }
            else if (watcherClient != null)
            {
                StopLobbyWatcher("main connection is in the lobby again");
            }
        }

        private void StartLobbyWatcher()
        {
            try
            {
                Photon.Realtime.AppSettings src = Photon.Pun.PhotonNetwork.PhotonServerSettings.AppSettings;
                Photon.Realtime.AppSettings settings = new Photon.Realtime.AppSettings();
                Photon.Realtime.LoadBalancingClient mainClient = Photon.Pun.PhotonNetwork.NetworkingClient;
                string mainAppId = null;
                string mainAppVersion = null;
                try
                {
                    if (mainClient != null)
                    {
                        mainAppId = mainClient.AppId;
                        mainAppVersion = mainClient.AppVersion;
                    }
                }
                catch { }
                settings.AppIdRealtime = !string.IsNullOrEmpty(mainAppId) ? mainAppId : src.AppIdRealtime;
                settings.AppVersion = mainAppVersion ?? src.AppVersion;
                settings.UseNameServer = src.UseNameServer;
                settings.FixedRegion = string.IsNullOrEmpty(Photon.Pun.PhotonNetwork.CloudRegion) ? src.FixedRegion : Photon.Pun.PhotonNetwork.CloudRegion;
                settings.Server = src.Server;
                settings.Port = src.Port;
                settings.Protocol = src.Protocol;
                settings.EnableProtocolFallback = src.EnableProtocolFallback;
                settings.AuthMode = src.AuthMode;
                watcherClient = new Photon.Realtime.LoadBalancingClient();
                watcherCallbacks = new LobbyWatcherCallbacks(this);
                watcherClient.AddCallbackTarget(watcherCallbacks);
                watcherClient.AuthValues = Photon.Pun.PhotonNetwork.AuthValues;
                ProbeLog("watcher connecting: appVersion='" + (settings.AppVersion ?? "") + "' region=" + (settings.FixedRegion ?? "auto"));
                if (!watcherClient.ConnectUsingSettings(settings))
                {
                    ProbeLog("watcher ConnectUsingSettings returned false");
                    TeardownWatcherClient();
                    watcherNextConnectAt = Time.unscaledTime + 10f;
                }
            }
            catch (Exception ex)
            {
                ProbeLog("watcher connect error: " + ex.GetType().Name + ": " + ex.Message);
                TeardownWatcherClient();
                watcherNextConnectAt = Time.unscaledTime + 10f;
            }
        }

        private void StopLobbyWatcher(string reason)
        {
            if (watcherClient == null)
                return;
            ProbeLog("watcher disconnecting: " + reason);
            TeardownWatcherClient();
            // Back off so an immediate re-arm (TickLobbyWatcher runs every frame)
            // can't turn a stop into a connect/teardown storm.
            watcherNextConnectAt = Time.unscaledTime + 10f;
        }

        private void TeardownWatcherClient()
        {
            if (watcherCallbacks != null && watcherClient != null)
            {
                try { watcherClient.RemoveCallbackTarget(watcherCallbacks); }
                catch { }
            }
            if (watcherClient != null)
            {
                try { watcherClient.Disconnect(); }
                catch { }
            }
            watcherClient = null;
            watcherCallbacks = null;
        }

        private void OnWatcherDisconnected(Photon.Realtime.DisconnectCause cause)
        {
            ProbeLog("watcher disconnected: " + cause);
            watcherClient = null;
            watcherCallbacks = null;
            watcherNextConnectAt = Time.unscaledTime + 10f;
        }

        private class LobbyWatcherCallbacks : Photon.Realtime.IConnectionCallbacks, Photon.Realtime.ILobbyCallbacks
        {
            private GhostLinePoller owner;

            public LobbyWatcherCallbacks(GhostLinePoller owner)
            {
                this.owner = owner;
            }

            public void OnConnected() { }

            public void OnConnectedToMaster()
            {
                try
                {
                    Photon.Realtime.TypedLobby lobby = Photon.Pun.PhotonNetwork.CurrentLobby ?? Photon.Realtime.TypedLobby.Default;
                    owner.watcherClient.OpJoinLobby(lobby);
                }
                catch (Exception ex)
                {
                    owner.ProbeLog("watcher join lobby error: " + ex.Message);
                }
            }

            public void OnDisconnected(Photon.Realtime.DisconnectCause cause)
            {
                owner.OnWatcherDisconnected(cause);
            }

            public void OnRegionListReceived(Photon.Realtime.RegionHandler regionHandler) { }

            public void OnCustomAuthenticationResponse(System.Collections.Generic.Dictionary<string, object> data) { }

            public void OnCustomAuthenticationFailed(string debugMessage)
            {
                owner.ProbeLog("watcher CUSTOM AUTH FAILED: " + debugMessage);
            }

            public void OnJoinedLobby()
            {
                owner.ProbeLog("watcher joined lobby - watching rooms");
            }

            public void OnLeftLobby() { }

            public void OnRoomListUpdate(System.Collections.Generic.List<Photon.Realtime.RoomInfo> roomList)
            {
                owner.ApplyLobbyRoomDelta(roomList);
            }

            public void OnLobbyStatisticsUpdate(System.Collections.Generic.List<Photon.Realtime.TypedLobbyInfo> lobbyStatistics) { }
        }

        private Vector2 LobbyBrowserSize()
        {
            float w = lobbyBrowserW > 0f ? Mathf.Clamp(lobbyBrowserW, 460f, Mathf.Max(460f, Screen.width - 40f)) : Mathf.Min(1000f, Screen.width - 60f);
            float h = lobbyBrowserH > 0f ? Mathf.Clamp(lobbyBrowserH, 320f, Mathf.Max(320f, Screen.height - 40f)) : Mathf.Min(780f, Screen.height - 60f);
            return new Vector2(w, h);
        }

        private Rect GetLobbyBrowserRect()
        {
            Vector2 sz = LobbyBrowserSize();
            return new Rect((Screen.width - sz.x) * 0.5f + lobbyBrowserDragOffset.x, (Screen.height - sz.y) * 0.5f + lobbyBrowserDragOffset.y, sz.x, sz.y);
        }

        private void DrawLobbyBrowser()
        {
            lobbyBrowserSelfDraw = true;
            float a = lobbyBrowserIdleAlpha;
            EnsureSettingsStyles();
            if (lobbyTitleStyle == null)
            {
                lobbyTitleStyle = new GUIStyle(GUI.skin.label);
                lobbyTitleStyle.alignment = TextAnchor.MiddleLeft;
                lobbyTitleStyle.fontSize = 24;
                lobbyTitleStyle.fontStyle = FontStyle.Bold;
                lobbyTitleStyle.normal.textColor = Color.white;
            }
            if (lobbyNameStyle == null)
            {
                lobbyNameStyle = new GUIStyle(GUI.skin.label);
                lobbyNameStyle.alignment = TextAnchor.MiddleLeft;
                lobbyNameStyle.fontSize = 20;
                lobbyNameStyle.fontStyle = FontStyle.Bold;
                lobbyNameStyle.normal.textColor = Color.white;
                // Single-line, clipped: word-wrap made titles collide with the info
                // line at narrow window widths.
                lobbyNameStyle.wordWrap = false;
                lobbyNameStyle.clipping = TextClipping.Clip;
            }
            if (lobbyInfoStyle == null)
            {
                lobbyInfoStyle = new GUIStyle(GUI.skin.label);
                lobbyInfoStyle.alignment = TextAnchor.MiddleLeft;
                lobbyInfoStyle.fontSize = 15;
                lobbyInfoStyle.normal.textColor = new Color(0.82f, 0.84f, 0.88f, 1f);
                lobbyInfoStyle.wordWrap = false;
                lobbyInfoStyle.clipping = TextClipping.Clip;
            }
            // Shrink the row fonts when the window is narrow so more of each name
            // survives truncation instead of collapsing to "...".
            bool lobbyNarrow = LobbyBrowserSize().x < 620f;
            int lobbyNameSize = lobbyNarrow ? 16 : 20;
            int lobbyInfoSize = lobbyNarrow ? 12 : 15;
            if (lobbyNameStyle.fontSize != lobbyNameSize)
            {
                lobbyNameStyle.fontSize = lobbyNameSize;
                lobbyInfoStyle.fontSize = lobbyInfoSize;
                fitNameCache.Clear();
                labelWidthCache.Clear();
                lobbyRowTextCache.Clear();
            }
            if (lobbyCountStyle == null)
            {
                lobbyCountStyle = new GUIStyle(GUI.skin.label);
                lobbyCountStyle.alignment = TextAnchor.MiddleRight;
                lobbyCountStyle.fontSize = 18;
                lobbyCountStyle.fontStyle = FontStyle.Bold;
                lobbyCountStyle.normal.textColor = Color.white;
            }
            if (lobbyJoinStyle == null)
            {
                lobbyJoinStyle = new GUIStyle(GUI.skin.label);
                lobbyJoinStyle.alignment = TextAnchor.MiddleCenter;
                lobbyJoinStyle.fontSize = 16;
                lobbyJoinStyle.fontStyle = FontStyle.Bold;
                lobbyJoinStyle.normal.textColor = Color.white;
            }
            if (lobbyPrioStyle == null)
            {
                lobbyPrioStyle = new GUIStyle(GUI.skin.label);
                lobbyPrioStyle.alignment = TextAnchor.MiddleCenter;
                lobbyPrioStyle.fontSize = 19;
                lobbyPrioStyle.fontStyle = FontStyle.Bold;
                lobbyPrioStyle.normal.textColor = Color.white;
            }
            if (lobbyCloseStyle == null)
            {
                lobbyCloseStyle = new GUIStyle(GUI.skin.label);
                lobbyCloseStyle.alignment = TextAnchor.MiddleCenter;
                lobbyCloseStyle.fontSize = 30;
                lobbyCloseStyle.fontStyle = FontStyle.Bold;
                lobbyCloseStyle.normal.textColor = Color.white;
            }
            if (lobbyCircleOnTex == null)
                lobbyCircleOnTex = MakeSelectionCircleTexture(true);
            if (lobbyCircleOffTex == null)
                lobbyCircleOffTex = MakeSelectionCircleTexture(false);
            Event e = Event.current;
            Vector2 lbSize = LobbyBrowserSize();
            {
                float baseX = (Screen.width - lbSize.x) * 0.5f;
                float baseY = (Screen.height - lbSize.y) * 0.5f;
                lobbyBrowserDragOffset.x = Mathf.Clamp(lobbyBrowserDragOffset.x, 8f - baseX, Screen.width - 8f - baseX - lbSize.x);
                lobbyBrowserDragOffset.y = Mathf.Clamp(lobbyBrowserDragOffset.y, 8f - baseY, Screen.height - 8f - baseY - lbSize.y);
            }
            Rect rect = GetLobbyBrowserRect();
            lobbyBrowserRect = rect;
            lobbyBrowserScreenRect = rect;
            if (a < 0.01f)
            {
                // Idle-faded: no draw, no input. Mouse movement fades it back in.
                lobbyBrowserSelfDraw = false;
                return;
            }
            // Resizable (blue edge/corner bars on hover, StickGraph style).
            lobbyBrowserResizeEdgeHover = ComputeWindowResizeEdge(rect, e.mousePosition, false);
            if (e.type == EventType.MouseDown && e.button == 0 && lobbyBrowserResizeEdgeHover != 0)
            {
                lobbyBrowserResizing = true;
                lobbyBrowserResizeEdge = lobbyBrowserResizeEdgeHover;
                lobbyBrowserResizeStartMouse = e.mousePosition;
                lobbyBrowserResizeStartRect = rect;
                e.Use();
            }
            else if (lobbyBrowserResizing && e.type == EventType.MouseDrag && e.button == 0)
            {
                Rect nr = ApplyWindowResizeDrag(lobbyBrowserResizeEdge, lobbyBrowserResizeStartMouse, lobbyBrowserResizeStartRect, e.mousePosition, 460f, 320f);
                lobbyBrowserW = nr.width;
                lobbyBrowserH = nr.height;
                lobbyBrowserDragOffset.x = nr.x - (Screen.width - nr.width) * 0.5f;
                lobbyBrowserDragOffset.y = nr.y - (Screen.height - nr.height) * 0.5f;
                lobbyBrowserRect = nr;
                e.Use();
            }
            else if (lobbyBrowserResizing && e.type == EventType.MouseUp && e.button == 0)
            {
                lobbyBrowserResizing = false;
                lobbyBrowserResizeEdge = 0;
                e.Use();
            }
            GUI.color = new Color(0f, 0f, 0f, 0.2f * a);
            GUI.DrawTexture(new Rect(rect.x + 3f, rect.y + 3f, rect.width, rect.height), whiteTex);
            GUI.color = ThemePanelBg(a);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = ThemePanelBorder(a);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), whiteTex);
            EnsureIcons();
            GUI.color = ThemeLabel(a);
            GUI.Label(new Rect(rect.x + 20f, rect.y + 12f, 420f, 32f), "Lobby Browser", lobbyTitleStyle);
            Rect closeBtn = new Rect(rect.xMax - 54f, rect.y + 2f, 44f, 44f);
            // Pin centred on the same line as the X, with an even gap to its left.
            Rect pinBtn = new Rect(rect.xMax - 88f, rect.y + 11f, 26f, 26f);
            Rect dragRect = new Rect(rect.x, rect.y, rect.width - 96f, 48f);
            bool closeHover = closeBtn.Contains(e.mousePosition);
            bool pinHover = pinBtn.Contains(e.mousePosition);

            // Footer geometry precomputed so the drag zone can exclude the switches
            // (the footer and its empty space are draggable like the top bar).
            float footerH = 52f;
            float footerTop = rect.yMax - footerH;
            string alertLabel = "Alert on free slot";
            string autoJoinLabel = "Auto-join selected";
            float alertLabelW = CalcLabelWidth(lobbyInfoStyle, alertLabel);
            float autoJoinLabelW = CalcLabelWidth(lobbyInfoStyle, autoJoinLabel);
            float alertToggleX = rect.x + 22f + alertLabelW + 8f;
            float autoLabelX = alertToggleX + 38f + 40f;
            float autoToggleX = autoLabelX + autoJoinLabelW + 8f;
            Rect alertToggleRect = new Rect(alertToggleX, footerTop + 9f, 38f, 22f);
            Rect autoToggleRect = new Rect(autoToggleX, footerTop + 9f, 38f, 22f);
            Rect footerDragRect = new Rect(rect.x, footerTop, rect.width, footerH);

            bool overFooterToggle = alertToggleRect.Contains(e.mousePosition) || autoToggleRect.Contains(e.mousePosition);
            bool dragZone = lobbyBrowserResizeEdgeHover == 0 && !pinHover && !closeHover &&
                (dragRect.Contains(e.mousePosition) || (footerDragRect.Contains(e.mousePosition) && !overFooterToggle));
            if (e.type == EventType.MouseDown && e.button == 0 && dragZone)
                lobbyBrowserDrag.Arm(e.mousePosition, lobbyBrowserDragOffset);
            lobbyBrowserDrag.Drag(e, false, ref lobbyBrowserDragOffset);
            if (e.type == EventType.MouseUp && e.button == 0)
                lobbyBrowserDrag.End();
            if (DrawStyledToggleButton(pinBtn, iconPin, lobbyBrowserPinned, a, true, -1))
            {
                lobbyBrowserPinned = !lobbyBrowserPinned;
                overlayMouseMovedAt = Time.unscaledTime;
            }
            GUI.color = closeHover ? ThemeLabel(a) : ThemeDimLabel(a);
            // Nudged up a few px so the X's glyph centres on the pin beside it.
            GUI.Label(new Rect(closeBtn.x, closeBtn.y - 3f, closeBtn.width, closeBtn.height), "x", lobbyCloseStyle);
            if (e.type == EventType.MouseDown && e.button == 0 && closeHover)
            {
                lobbyBrowserOpen = false;
                lobbyBrowserSelfDraw = false;
                e.Use();
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                lobbyBrowserOpen = false;
                lobbyBrowserSelfDraw = false;
                e.Use();
                return;
            }

            if (Time.unscaledTime - lobbyBrowserRowsAt >= 0.5f)
            {
                lobbyBrowserRowsAt = Time.unscaledTime;
                lobbyBrowserRows.Clear();
                foreach (var kv in lobbyRoomStates)
                {
                    if (kv.Value != null)
                        lobbyBrowserRows.Add(kv.Value);
                }
                lobbyBrowserRows.Sort((ra, rb) =>
                {
                    int ai = selectedLobbies.IndexOf(ra.Name);
                    int bi = selectedLobbies.IndexOf(rb.Name);
                    if (ai >= 0 || bi >= 0)
                    {
                        if (ai < 0)
                            return 1;
                        if (bi < 0)
                            return -1;
                        return ai.CompareTo(bi);
                    }
                    if (ra.Listed != rb.Listed)
                        return ra.Listed ? -1 : 1;
                    return string.Compare(ra.Name, rb.Name, StringComparison.OrdinalIgnoreCase);
                });
                float nameWRebuild = (rect.width - 43) - 56f - 10f - 90f - 10f - 110f - 10f;
                lobbyRowTextCache.Clear();
                foreach (var roomT in lobbyBrowserRows)
                {
                    LobbyRowText rt = new LobbyRowText();
                    string titleT = !string.IsNullOrEmpty(roomT.TrackName) ? roomT.TrackName
                        : !string.IsNullOrEmpty(roomT.EnvName) ? roomT.EnvName
                        : roomT.Name;
                    rt.Title = FitTextCached(titleT, nameWRebuild, lobbyNameStyle, "ln");
                    string infoT = "";
                    if (!string.IsNullOrEmpty(roomT.HostName))
                        infoT += "Host: " + roomT.HostName;
                    if (!string.IsNullOrEmpty(roomT.EnvName) && roomT.EnvName != titleT)
                        infoT += (infoT.Length > 0 ? "   |   " : "") + roomT.EnvName;
                    string modeT = LobbyGameModeName(roomT.GameMode);
                    if (!string.IsNullOrEmpty(modeT))
                        infoT += (infoT.Length > 0 ? "   |   " : "") + modeT;
                    rt.Info = FitTextCached(infoT, nameWRebuild, lobbyInfoStyle, "li");
                    rt.Count = !roomT.Listed ? "Full" : roomT.PlayerCount + "/" + roomT.MaxPlayers;
                    lobbyRowTextCache[roomT.Name] = rt;
                }
            }

            // Scrollbar sits flush against the window's right edge but stays clear of
            // the 6px resize-edge strip (its click zone would eat the track).
            float trackW = 7f;
            Rect listRect = new Rect(rect.x + 16f, rect.y + 48f, rect.width - 16f - 12f - trackW - 4f, rect.height - 48f - footerH - 12f);
            float rowH = 84f;
            float scrollZone = 4f;
            float rowW = listRect.width - scrollZone;
            float contentH = Mathf.Max(lobbyBrowserRows.Count * rowH, 1f);
            float maxScroll = Mathf.Max(0f, contentH - listRect.height);
            if (lobbyBrowserScrollY > maxScroll)
                lobbyBrowserScrollY = maxScroll;
            if (lobbyBrowserScrollY < 0f)
                lobbyBrowserScrollY = 0f;

            Rect wheelZone = new Rect(listRect.x, listRect.y, listRect.width + trackW + 6f, listRect.height);
            if (e.type == EventType.ScrollWheel && wheelZone.Contains(e.mousePosition) && maxScroll > 0f)
            {
                lobbyBrowserScrollY = Mathf.Clamp(lobbyBrowserScrollY + e.delta.y * 64f, 0f, maxScroll);
                e.Use();
            }

            float trackX = rect.xMax - trackW - 12f;
            float trackY = listRect.y + 2f;
            float trackH = listRect.height - 4f;
            if (maxScroll > 0f)
            {
                GUI.color = ThemeOptionBorder(0.8f * a);
                GUI.DrawTexture(new Rect(trackX, trackY, trackW, trackH), whiteTex);
                float thumbH = Mathf.Max(32f, trackH * (listRect.height / contentH));
                float thumbY = trackY + (lobbyBrowserScrollY / maxScroll) * (trackH - thumbH);
                Rect thumbRect = new Rect(trackX - 2f, thumbY, trackW + 4f, thumbH);
                bool thumbHover = thumbRect.Contains(e.mousePosition);
                GUI.color = lobbyBrowserThumbDrag || thumbHover
                    ? new Color(0.25f, 0.62f, 0.95f, 0.95f * a)
                    : new Color(0.55f, 0.55f, 0.6f, 0.8f * a);
                GUI.DrawTexture(thumbRect, whiteTex);
                if (e.type == EventType.MouseDown && e.button == 0 && thumbRect.Contains(e.mousePosition))
                {
                    lobbyBrowserThumbDrag = true;
                    lobbyBrowserThumbDragStartY = e.mousePosition.y;
                    lobbyBrowserThumbDragStartScroll = lobbyBrowserScrollY;
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag && e.button == 0 && lobbyBrowserThumbDrag)
                {
                    lobbyBrowserScrollY = Mathf.Clamp(lobbyBrowserThumbDragStartScroll +
                        (e.mousePosition.y - lobbyBrowserThumbDragStartY) * (maxScroll / Mathf.Max(1f, trackH - thumbH)), 0f, maxScroll);
                    e.Use();
                }
                else if (lobbyBrowserThumbDrag && e.type == EventType.MouseUp && e.button == 0)
                {
                    lobbyBrowserThumbDrag = false;
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && e.button == 0 &&
                    new Rect(trackX - 4f, trackY, trackW + 8f, trackH).Contains(e.mousePosition))
                {
                    lobbyBrowserScrollY = Mathf.Clamp((e.mousePosition.y - trackY) / trackH * contentH - listRect.height * 0.5f, 0f, maxScroll);
                    e.Use();
                }
            }

            // Draw the list group starting at the window's inner edge (1px border) and
            // inset the row content back to its old place, so the hover highlight can
            // span the full card width with pixel-exact edges (drawing it outside the
            // group produced a visible 1px seam at the group boundary).
            float rowInset = listRect.x - (rect.x + 1f);
            Rect listDrawRect = new Rect(rect.x + 1f, listRect.y, listRect.width + rowInset, listRect.height);
            GUI.BeginGroup(listDrawRect);
            try
            {
            float nameW = rowW - 56f - 10f - 90f - 10f - 110f - 10f;
            for (int ri = 0; ri < lobbyBrowserRows.Count; ri++)
            {
                LobbyWatchRoom room = lobbyBrowserRows[ri];
                float ry = ri * rowH - lobbyBrowserScrollY;
                if (ry + rowH < 0f || ry > listRect.height)
                    continue;
                bool selected = selectedLobbies.Contains(room.Name);
                int selectedPriority = selected ? selectedLobbies.IndexOf(room.Name) + 1 : 0;
                bool joinable = room.Listed && room.IsOpen && room.PlayerCount < room.MaxPlayers;
                Rect cardRect = new Rect(rowInset, ry, rowW, rowH - 1f);
                bool cardHover = cardRect.Contains(e.mousePosition);
                if (cardHover)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.09f * a);
                    GUI.DrawTexture(new Rect(0f, ry, rowInset + rowW + 4f, rowH - 1f), whiteTex);
                    GUI.color = new Color(1f, 1f, 1f, a);
                }
                Rect circleRect = new Rect(rowInset + 4f, ry + (rowH - 44f) * 0.5f, 44f, 44f);
                bool circleHover = circleRect.Contains(e.mousePosition);
                GUI.color = selected
                    ? new Color(0.35f, 0.7f, 1f, a)
                    : circleHover ? new Color(0.85f, 0.87f, 0.9f, a) : new Color(0.55f, 0.57f, 0.62f, a);
                GUI.DrawTexture(circleRect, selected ? lobbyCircleOnTex : lobbyCircleOffTex);
                if (selected)
                {
                    GUI.color = new Color(1f, 1f, 1f, a);
                    GUI.Label(new Rect(circleRect.x + 1f, circleRect.y, circleRect.width, circleRect.height), selectedPriority.ToString(), lobbyPrioStyle);
                }
                if (e.type == EventType.MouseUp && e.button == 0 && circleRect.Contains(e.mousePosition))
                {
                    ToggleLobbySelection(room.Name);
                    e.Use();
                }
                string title = !string.IsNullOrEmpty(room.TrackName) ? room.TrackName
                    : !string.IsNullOrEmpty(room.EnvName) ? room.EnvName
                    : room.Name;
                LobbyRowText rowText;
                if (!lobbyRowTextCache.TryGetValue(room.Name, out rowText))
                {
                    string infoFallback = "";
                    if (!string.IsNullOrEmpty(room.HostName))
                        infoFallback += "Host: " + room.HostName;
                    if (!string.IsNullOrEmpty(room.EnvName) && room.EnvName != title)
                        infoFallback += (infoFallback.Length > 0 ? "   |   " : "") + room.EnvName;
                    string modeFallback = LobbyGameModeName(room.GameMode);
                    if (!string.IsNullOrEmpty(modeFallback))
                        infoFallback += (infoFallback.Length > 0 ? "   |   " : "") + modeFallback;
                    rowText = new LobbyRowText
                    {
                        Title = FitSettingsName(title, nameW),
                        Info = FitSettingsName(infoFallback, nameW),
                        Count = !room.Listed ? "Full" : room.PlayerCount + "/" + room.MaxPlayers
                    };
                    lobbyRowTextCache[room.Name] = rowText;
                }
                GUI.color = ThemeLabel(a);
                GUI.Label(new Rect(rowInset + 60f, ry + 10f, nameW, 26f), rowText.Title, lobbyNameStyle);
                GUI.color = new Color(0.82f, 0.84f, 0.88f, a);
                GUI.Label(new Rect(rowInset + 60f, ry + 42f, nameW, 22f), rowText.Info, lobbyInfoStyle);
                GUI.color = !room.Listed ? new Color(0.9f, 0.55f, 0.45f, a) : ThemeLabel(a);
                GUI.Label(new Rect(rowInset + 60f + nameW + 10f, ry + 30f, 90f, 26f), rowText.Count, lobbyCountStyle);
                Rect joinRect = new Rect(rowInset + rowW - 110f, ry + 25f, 110f, 34f);
                float joinAlpha = (cardHover ? 0.82f : 1f) * a;
                bool joinHover = joinable && joinRect.Contains(e.mousePosition);
                Color joinOld = GUI.color;
                GUI.color = !joinable
                    ? new Color(0.36f, 0.36f, 0.4f, 0.6f * joinAlpha)
                    : ThemeOptionBg(joinAlpha);
                GUI.DrawTexture(joinRect, whiteTex);
                GUI.color = !joinable
                    ? new Color(0.36f, 0.36f, 0.4f, 0.6f * joinAlpha)
                    : ThemeOptionBorder(joinAlpha);
                GUI.DrawTexture(new Rect(joinRect.x, joinRect.y, joinRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(joinRect.x, joinRect.yMax - 1f, joinRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(joinRect.x, joinRect.y, 1f, joinRect.height), whiteTex);
                GUI.DrawTexture(new Rect(joinRect.xMax - 1f, joinRect.y, 1f, joinRect.height), whiteTex);
                GUI.color = !joinable
                    ? new Color(0.75f, 0.75f, 0.78f, 0.5f * joinAlpha)
                    : joinHover ? new Color(1f, 1f, 1f, joinAlpha) : new Color(0.7f, 0.72f, 0.76f, joinAlpha);
                GUI.Label(joinRect, "Join", lobbyJoinStyle);
                GUI.color = joinOld;
                if (e.type == EventType.MouseDown && e.button == 0 && joinHover)
                    settingsControlPressed = true;
                if (e.type == EventType.MouseUp && e.button == 0 && joinHover && !settingsDrag.Dragging)
                {
                    e.Use();
                    JoinLobbyRoom(room.Name);
                }
                // Clicking anywhere on the card (except Join) toggles the selection.
                if (e.type == EventType.MouseUp && e.button == 0 && cardHover && !joinRect.Contains(e.mousePosition))
                {
                    ToggleLobbySelection(room.Name);
                    e.Use();
                }
                GUI.color = ThemeOptionBorder(0.3f * a);
                GUI.DrawTexture(new Rect(0f, ry + rowH - 1f, rowInset + rowW, 1f), whiteTex);
            }
            }
            finally { GUI.EndGroup(); }

            float fy = footerTop;
            GUI.color = ThemeOptionBorder(a);
            GUI.DrawTexture(new Rect(rect.x + 16f, fy - 6f, rect.width - 32f, 1f), whiteTex);
            bool autoJoin = Prefs.autoJoinLobbyConfig != null && Prefs.autoJoinLobbyConfig.Value;
            bool alertOn = Prefs.autoAlertLobbyConfig == null || Prefs.autoAlertLobbyConfig.Value;
            float footerY = fy + 8f;
            // Both switches stay left-anchored, laid out after each other (positions
            // were precomputed above so the footer drag zone can exclude them).
            GUI.color = ThemeLabel(a);
            GUI.Label(new Rect(rect.x + 22f, footerY, alertLabelW, 24f), alertLabel, lobbyInfoStyle);
            if (DrawSettingsToggle(alertToggleRect, alertOn, a, true))
            {
                if (Prefs.autoAlertLobbyConfig != null)
                {
                    Prefs.autoAlertLobbyConfig.Value = !alertOn;
                    if (Prefs.preferencesConfig != null)
                        Prefs.RequestSave();
                }
            }
            GUI.color = ThemeLabel(a);
            GUI.Label(new Rect(autoLabelX, footerY, autoJoinLabelW, 24f), autoJoinLabel, lobbyInfoStyle);
            if (DrawSettingsToggle(autoToggleRect, autoJoin, a, true))
            {
                if (Prefs.autoJoinLobbyConfig != null)
                {
                    Prefs.autoJoinLobbyConfig.Value = !autoJoin;
                    if (Prefs.preferencesConfig != null)
                        Prefs.RequestSave();
                }
            }
            DrawWindowResizeBars(rect, lobbyBrowserResizing ? lobbyBrowserResizeEdge : lobbyBrowserResizeEdgeHover);
            GUI.color = Color.white;
            lobbyBrowserSelfDraw = false;
        }

        private void DrawLobbyAlert()
        {
            if (string.IsNullOrEmpty(lobbyAlertRoom) || Time.unscaledTime >= lobbyAlertUntil)
                return;
            float w = 320f;
            float h = 108f;
            Rect rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f - 60f, w, h);
            lobbyAlertRect = rect;
            GUI.color = new Color(0f, 0f, 0f, 0.2f);
            GUI.DrawTexture(new Rect(rect.x + 3f, rect.y + 3f, w, h), whiteTex);
            GUI.color = ThemePanelBg(1f);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = ThemePanelBorder(1f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), whiteTex);
            GUI.color = ThemeLabel(1f);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 8f, w - 28f, 20f), "Lobby Watcher", settingsHeaderStyle);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 32f, w - 28f, 20f), FitSettingsName("Free slot in " + lobbyAlertRoom, w - 28f), settingsSmallStyle);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 52f, w - 28f, 18f), "Players: " + lobbyAlertCount, settingsSmallStyle);
            float bw = 90f;
            Rect joinRect = new Rect(rect.x + w - bw * 2f - 22f, rect.yMax - 32f, bw, 22f);
            Rect dismissRect = new Rect(rect.x + w - bw - 12f, rect.yMax - 32f, bw, 22f);
            if (DrawSettingsOption(joinRect, "Join", false, 1f, TextAnchor.MiddleCenter, -1, true))
            {
                lobbyAlertUntil = 0f;
                JoinLobbyRoom(lobbyAlertRoom);
            }
            if (DrawSettingsOption(dismissRect, "Dismiss", false, 1f, TextAnchor.MiddleCenter, -1, true))
                lobbyAlertUntil = 0f;
            GUI.color = Color.white;
        }
    }
}
