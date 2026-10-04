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
        // The graph's lines and gate dots are rasterized into one texture (see
        // LineRaster); drawing them as one 2x2 IMGUI quad per pixel cost 45-190 ms
        // of OnGUI per second in flight.
        private readonly LineRaster raceGraphRaster = new LineRaster();
        private readonly List<Vector2> raceGraphSmoothBuf = new List<Vector2>();

        // The old per-pixel 2px quads overlapped 2-3 deep, so a lap line read far
        // more opaque than its alpha; compounding keeps the dimmer laps as visible.
        private static float RaceGraphLineAlpha(float alpha)
        {
            float clear = 1f - Mathf.Clamp01(alpha);
            return 1f - clear * clear;
        }

        private sealed class RaceGraphDot
        {
            public Vector2 pos;
            public int rowIdx;
            public int lapNumber;
            public double delta;
            public Color col;
        }
        private readonly List<RaceGraphDot> raceGraphDots = new List<RaceGraphDot>();
        private readonly List<RaceGraphDot> raceGraphDotPool = new List<RaceGraphDot>();

        // Recycle the per-gate dot objects: the graph rebuilds every Repaint and
        // used to allocate one RaceGraphDot per gate per lap per frame.
        private void ResetRaceGraphDots()
        {
            for (int i = 0; i < raceGraphDots.Count; i++)
                raceGraphDotPool.Add(raceGraphDots[i]);
            raceGraphDots.Clear();
        }

        private RaceGraphDot RentRaceGraphDot()
        {
            int n = raceGraphDotPool.Count;
            if (n > 0)
            {
                RaceGraphDot d = raceGraphDotPool[n - 1];
                raceGraphDotPool.RemoveAt(n - 1);
                return d;
            }
            return new RaceGraphDot();
        }
        private float raceGraphYLo = 0f;
        private float raceGraphYHi = 0f;
        private bool raceGraphScaleInit = false;
        private readonly List<Vector2> raceGraphPtBuf = new List<Vector2>();
        private readonly List<double> raceGraphAllD = new List<double>();
        private readonly List<StoredLap> raceGraphLaps = new List<StoredLap>();
        private readonly List<int> raceGraphDefIdxsCache = new List<int>();
        private readonly HashSet<int> raceGraphDefSet = new HashSet<int>();
        private int raceGraphDefIdxsCount = -1;
        // Bumped on any history mutation (add/remove/edit/trim). The default
        // "five fastest" cache must key on this, not .Count — once history
        // saturates at 50 the count stops changing and the cache would freeze.
        private int raceGraphHistoryEpoch = 0;
        private int raceGraphDefIdxsEpoch = -1;

        private static double RaceHistoryParse(string s)
        {
            if (string.IsNullOrEmpty(s))
                return double.NaN;
            s = s.Trim().Replace(',', '.');
            double v;
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                return double.NaN;
            return v;
        }

        // Lap-history list button strip, anchored to the list's right edge so the
        // group stays put when the window is resized. Content-local x positions.
        private static void RaceHistoryListButtons(float contentW, out float bestX, out float editX,
            out float showX, out float delX)
        {
            const float gap = 4f;
            const float smallW = 44f;
            delX = contentW - 6f - 54f;
            showX = delX - gap - smallW;
            editX = showX - gap - smallW;
            bestX = editX - gap - smallW;
        }

        private void DrawRaceHistoryWindow(Event e)
        {
            float savedFade = uiFadeAlpha;
            uiFadeAlpha = raceHistoryIdleAlpha;
            try { DrawRaceHistoryWindowInner(e); }
            finally { uiFadeAlpha = savedFade; }
        }

        private void DrawRaceHistoryWindowInner(Event e)
        {
            bool editing = raceHistoryEditIdx >= 0 && raceHistoryEditIdx < raceLapHistory.Count;
            if (raceHistoryFieldStyle == null)
            {
                // Flat field to match the panel chrome instead of the rounded,
                // glossy default skin text field.
                raceHistoryFieldStyle = new GUIStyle(GUI.skin.textField);
                raceHistoryFieldStyle.fontSize = 12;
                raceHistoryFieldStyle.alignment = TextAnchor.MiddleLeft;
                raceHistoryFieldStyle.padding = new RectOffset(6, 6, 2, 2);
                raceHistoryFieldStyle.border = new RectOffset(0, 0, 0, 0);
                raceHistoryFieldTex = MakeSolidTexture(new Color(0f, 0f, 0f, 0.45f * uiFadeAlpha));
                raceHistoryFieldFocusTex = MakeSolidTexture(new Color(0f, 0f, 0f, 0.6f * uiFadeAlpha));
                raceHistoryFieldStyle.normal.background = raceHistoryFieldTex;
                raceHistoryFieldStyle.hover.background = raceHistoryFieldTex;
                raceHistoryFieldStyle.active.background = raceHistoryFieldFocusTex;
                raceHistoryFieldStyle.focused.background = raceHistoryFieldFocusTex;
                raceHistoryFieldStyle.normal.textColor = Color.white;
                raceHistoryFieldStyle.hover.textColor = Color.white;
                raceHistoryFieldStyle.active.textColor = Color.white;
                raceHistoryFieldStyle.focused.textColor = Color.white;
            }
            float wh = Mathf.Clamp(raceHistoryW, 380f, Mathf.Max(380f, Screen.width - 40f));
            raceHistoryW = wh;
            float hh = 32f;
            float minH = hh + 4f + 148f + 24f + 24f + 8f;
            if (raceHistoryH <= 0f)
            {
                raceHistoryH = editing
                    ? hh + 4f + 148f + 24f + Mathf.Min(raceVisibleIdx.Count, 10) * 22f + 40f + 8f
                    : hh + 4f + 148f + 24f + Mathf.Min(raceLapHistory.Count, 10) * 24f + 16f + 8f;
            }
            float hgt = Mathf.Clamp(raceHistoryH, minH, Mathf.Max(minH, Screen.height - 40f));
            raceHistoryH = hgt;
            Rect win = new Rect(raceHistoryX, raceHistoryY, wh, hgt);
            raceHistoryScreenRect = win;
            Rect headRect = new Rect(win.x, win.y, win.width, hh);
            // Same X box as the gate-splits panel.
            Rect closeRect = new Rect(win.xMax - 36f, win.y + 4f, 28f, 22f);
            // Header: pin (next to the X) then the graph mode toggle, 6px gaps.
            Rect histPinRect = new Rect(closeRect.x - 6f - 24f, win.y + 5f, 24f, 20f);
            Rect vsBestRect = new Rect(histPinRect.x - 6f - 84f, win.y + 5f, 84f, 20f);
            if (e.type == EventType.MouseDown && e.button == 0 && histPinRect.Contains(e.mousePosition))
            {
                e.Use();
                raceHistoryPinned = !raceHistoryPinned;
                overlayMouseMovedAt = Time.unscaledTime;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && vsBestRect.Contains(e.mousePosition))
            {
                e.Use();
                if (Prefs.lapHistoryVsBestConfig != null)
                {
                    Prefs.lapHistoryVsBestConfig.Value = !Prefs.lapHistoryVsBestConfig.Value;
                    if (Prefs.preferencesConfig != null)
                        Prefs.RequestSave();
                    raceGraphScaleInit = false;
                }
            }
            // Resizable (blue edge/corner bars on hover, StickGraph style).
            raceHistoryResizeEdgeHover = ComputeWindowResizeEdge(win, e.mousePosition, false);
            if (e.type == EventType.MouseDown && e.button == 0 && raceHistoryResizeEdgeHover != 0)
            {
                raceHistoryResizing = true;
                raceHistoryResizeEdge = raceHistoryResizeEdgeHover;
                raceHistoryResizeStartMouse = e.mousePosition;
                raceHistoryResizeStartRect = win;
                raceHistoryDrag.Cancel();
                e.Use();
            }
            else if (raceHistoryResizing && e.type == EventType.MouseDrag && e.button == 0)
            {
                Rect nr = ApplyWindowResizeDrag(raceHistoryResizeEdge, raceHistoryResizeStartMouse, raceHistoryResizeStartRect, e.mousePosition, 380f, minH);
                raceHistoryX = nr.x;
                raceHistoryY = nr.y;
                raceHistoryW = nr.width;
                raceHistoryH = nr.height;
                e.Use();
            }
            else if (raceHistoryResizing && e.type == EventType.MouseUp && e.button == 0)
            {
                raceHistoryResizing = false;
                raceHistoryResizeEdge = 0;
                e.Use();
            }
            // Drag anywhere on the window (like the other windows), except the
            // controls: close/resize, the graph (scroll-zoom/pan when zoomed), the
            // scrollbar, and the list's Best/Edit/Show/Del buttons. The header
            // still drags immediately; empty space arms a candidate.
            bool histBlocked = false;
            if (raceLapHistory.Count > 0)
            {
                if (editing)
                {
                    histBlocked = e.mousePosition.y > win.y + hh;
                }
                else
                {
                    Rect gArea = new Rect(win.x + 10f, win.y + hh + 4f, win.width - 20f, 148f);
                    bool graphZoomed = raceGraphSpan > 0f && raceGraphSpan < raceVisibleIdx.Count - 0.5f;
                    histBlocked = (gArea.Contains(e.mousePosition) && graphZoomed) ||
                        new Rect(win.x + win.width - 26f, gArea.yMax + 24f, 20f,
                            Mathf.Max(24f, win.yMax - gArea.yMax - 32f)).Contains(e.mousePosition);
                    if (!histBlocked)
                    {
                        float listTop = gArea.yMax + 24f;
                        RaceHistoryListButtons(win.width - 36f,
                            out float blX, out float beX, out float bsX, out float bdX);
                        float groupL = win.x + 8f + blX;
                        float groupR = win.x + 8f + bdX + 54f;
                        for (int li = 0; li < raceLapHistory.Count; li++)
                        {
                            float sy = listTop + (raceLapHistory.Count - 1 - li) * 24f - raceHistoryScroll.y;
                            if (sy > win.yMax || sy + 20f < listTop)
                                continue;
                            if (new Rect(groupL, sy, groupR - groupL, 20f).Contains(e.mousePosition))
                            {
                                histBlocked = true;
                                break;
                            }
                        }
                    }
                }
            }
            if (e.type == EventType.MouseDown && e.button == 0 && win.Contains(e.mousePosition) &&
                !closeRect.Contains(e.mousePosition) && !vsBestRect.Contains(e.mousePosition) &&
                !histPinRect.Contains(e.mousePosition) && raceHistoryResizeEdgeHover == 0)
            {
                Vector2 histStart = new Vector2(raceHistoryX, raceHistoryY);
                if (headRect.Contains(e.mousePosition))
                {
                    raceHistoryDrag.Begin(e.mousePosition, histStart);
                    e.Use();
                }
                else if (!histBlocked)
                    raceHistoryDrag.Arm(e.mousePosition, histStart);
            }
            Vector2 histPos = new Vector2(raceHistoryX, raceHistoryY);
            if (raceHistoryDrag.Drag(e, histBlocked, ref histPos))
            {
                raceHistoryX = Mathf.Clamp(histPos.x, 4f, Mathf.Max(4f, Screen.width - wh - 4f));
                raceHistoryY = Mathf.Clamp(histPos.y, 4f, Mathf.Max(4f, Screen.height - 40f));
            }
            if (e.type == EventType.MouseUp && e.button == 0)
                raceHistoryDrag.End();
            if (e.type == EventType.MouseDown && e.button == 0 && closeRect.Contains(e.mousePosition))
            {
                e.Use();
                raceHistoryOpen = false;
                raceHistoryEditIdx = -1;
                return;
            }
            // Window chrome is repaint-only, but the editor (TextFields) and the
            // graph (scroll-zoom, pan, and the Show/Hide/Best/Del list buttons)
            // must process every event or their input never fires.
            if (e.type == EventType.Repaint)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.55f * uiFadeAlpha);
                GUI.DrawTexture(win, whiteTex);
                GUI.color = ThemePanelBorder(1f);
                GUI.DrawTexture(new Rect(win.x, win.y, win.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(win.x, win.yMax - 1f, win.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(win.x, win.y, 1f, win.height), whiteTex);
                GUI.DrawTexture(new Rect(win.xMax - 1f, win.y, 1f, win.height), whiteTex);
                GUI.color = ThemeLabel(1f);
                GUI.Label(new Rect(win.x + 8f, win.y + 4f, 240f, 22f),
                    editing ? "Edit Lap " + raceLapHistory[raceHistoryEditIdx].number : "Lap History", raceSplitsStyle);
                EnsureIcons();
                DrawStyledToggleButton(histPinRect, iconPin, raceHistoryPinned, raceHistoryIdleAlpha, false, -1);
                bool vsBestMode = Prefs.lapHistoryVsBestConfig == null ||
                    Prefs.lapHistoryVsBestConfig.Value;
                bool vsBestHover = vsBestRect.Contains(e.mousePosition);
                GUI.color = vsBestHover ? new Color(1f, 1f, 1f, 0.12f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.3f * uiFadeAlpha);
                GUI.DrawTexture(vsBestRect, whiteTex);
                GUI.color = vsBestHover ? ThemeLabel(1f) : ThemeDimLabel(1f);
                GUI.Label(vsBestRect, vsBestMode ? "vs Best" : "All Laps", raceSplitsTrimStyle);
                GUI.color = ThemeLabel(1f);
                GUI.Label(closeRect, "x", settingsCloseBigStyle != null
                    ? settingsCloseBigStyle
                    : (settingsCloseStyle != null ? settingsCloseStyle : raceSplitsTrimStyle));
            }
            if (raceLapHistory.Count == 0)
            {
                if (e.type == EventType.Repaint)
                {
                    GUI.color = ThemeDimLabel(1f);
                    GUI.Label(new Rect(win.x + 8f, win.y + hh + 6f, wh - 16f, 20f), "no laps recorded yet", raceSplitsStyle);
                }
                DrawWindowResizeBars(win, raceHistoryResizing ? raceHistoryResizeEdge : raceHistoryResizeEdgeHover);
                GUI.color = Color.white;
                return;
            }
            if (editing)
                DrawRaceHistoryEditor(win, hh, e);
            else
                DrawRaceHistoryGraph(win, hh, e);
            DrawWindowResizeBars(win, raceHistoryResizing ? raceHistoryResizeEdge : raceHistoryResizeEdgeHover);
            GUI.color = Color.white;
        }

        private Texture2D MakeSolidTexture(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply(false, true);
            return t;
        }

        // Skins the IMGUI scroll view's bar to match the lobby browser's custom
        // track/thumb (flat 7px bar, blue on hover/drag).
        private void EnsureRaceHistoryScrollSkin()
        {
            if (raceScrollbarStyle != null && raceScrollSkinDark == darkMode)
                return;
            raceScrollSkinDark = darkMode;
            if (raceScrollTrackTex != null) { Destroy(raceScrollTrackTex); raceScrollTrackTex = null; }
            if (raceScrollThumbTex != null) { Destroy(raceScrollThumbTex); raceScrollThumbTex = null; }
            if (raceScrollThumbHoverTex != null) { Destroy(raceScrollThumbHoverTex); raceScrollThumbHoverTex = null; }
            raceScrollTrackTex = MakeSolidTexture(ThemeOptionBorder(0.8f));
            raceScrollThumbTex = MakeSolidTexture(new Color(0.55f, 0.55f, 0.6f, 0.8f));
            raceScrollThumbHoverTex = MakeSolidTexture(new Color(0.25f, 0.62f, 0.95f, 0.95f));
            raceScrollbarStyle = new GUIStyle(GUI.skin.verticalScrollbar);
            raceScrollbarStyle.fixedWidth = 7f;
            raceScrollbarStyle.normal.background = raceScrollTrackTex;
            raceScrollbarThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            raceScrollbarThumbStyle.fixedWidth = 7f;
            raceScrollbarThumbStyle.normal.background = raceScrollThumbTex;
            raceScrollbarThumbStyle.hover.background = raceScrollThumbHoverTex;
            raceScrollbarThumbStyle.active.background = raceScrollThumbHoverTex;
        }

        private void DrawRaceHistoryEditor(Rect win, float hh, Event e)
        {
            var stored = raceLapHistory[raceHistoryEditIdx];
            float listH = Mathf.Clamp(win.yMax - (win.y + hh) - 46f, 44f, raceVisibleIdx.Count * 22f);
            EnsureRaceHistoryScrollSkin();
            GUIStyle oldScrollbar = GUI.skin.verticalScrollbar;
            GUIStyle oldThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = raceScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = raceScrollbarThumbStyle;
            raceHistoryEditScroll = GUI.BeginScrollView(
                new Rect(win.x + 8f, win.y + hh + 4f, win.width - 16f, listH),
                raceHistoryEditScroll,
                new Rect(0f, 0f, win.width - 27f, raceVisibleIdx.Count * 22f));
            try
            {
                for (int vi = 0; vi < raceVisibleIdx.Count && vi < raceHistoryEditVals.Count; vi++)
                {
                    float y = vi * 22f;
                    int rowIdx = raceVisibleIdx[vi];
                    string label = rowIdx < raceGateLabels.Count ? raceGateLabels[rowIdx] : RaceGateLabel(rowIdx);
                    GUI.color = ThemeDimLabel(1f);
                    GUI.Label(new Rect(0f, y, 70f, 20f), label, raceSplitsStyle);
                    GUI.color = Color.white;
                    GUI.SetNextControlName("GhoLinesRaceEdit" + vi);
                    raceHistoryEditVals[vi] = GUI.TextField(new Rect(76f, y, 84f, 20f), raceHistoryEditVals[vi], raceHistoryFieldStyle);
                }
            }
            finally
            {
                GUI.EndScrollView();
                GUI.skin.verticalScrollbar = oldScrollbar;
                GUI.skin.verticalScrollbarThumb = oldThumb;
            }
            Rect saveRect = new Rect(win.x + 8f, win.y + hh + listH + 10f, 80f, 24f);
            Rect cancelRect = new Rect(win.x + 96f, win.y + hh + listH + 10f, 80f, 24f);
            GUI.color = new Color(0.25f, 0.55f, 0.3f, 0.85f * uiFadeAlpha);
            GUI.DrawTexture(saveRect, whiteTex);
            GUI.color = ThemeLabel(1f);
            GUI.Label(saveRect, "Save", raceSplitsTrimStyle);
            GUI.color = new Color(0f, 0f, 0f, 0.35f * uiFadeAlpha);
            GUI.DrawTexture(cancelRect, whiteTex);
            GUI.color = ThemeDimLabel(1f);
            GUI.Label(cancelRect, "Cancel", raceSplitsTrimStyle);
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (cancelRect.Contains(e.mousePosition))
                {
                    e.Use();
                    raceHistoryEditIdx = -1;
                }
                else if (saveRect.Contains(e.mousePosition))
                {
                    e.Use();
                    // Rebuild the attempt from edited cumulative splits (visible row order).
                    double t0 = stored.attempt.times.Count > 0 ? stored.attempt.times[0] : 0.0;
                    var att = new RaceLapAttempt();
                    double lastVal = 0.0;
                    double prevVal = 0.0;
                    bool ok = true;
                    for (int vi = 0; vi < raceVisibleIdx.Count && vi < raceHistoryEditVals.Count; vi++)
                    {
                        int rowIdx = raceVisibleIdx[vi];
                        string rowKey = rowIdx < raceRowKeys.Count ? raceRowKeys[rowIdx] : null;
                        double v = RaceHistoryParse(raceHistoryEditVals[vi]);
                        if (rowKey == null || double.IsNaN(v) || v < 0.0)
                        {
                            ok = false;
                            break;
                        }
                        // Enforce non-decreasing cumulative splits so a stray edit
                        // cannot produce a lap with time running backwards.
                        if (v < prevVal)
                            v = prevVal;
                        prevVal = v;
                        bool skip = raceMergedFinish && rowIdx == raceRowKeys.Count - 1;
                        if (!skip)
                        {
                            att.keys.Add(rowKey);
                            att.times.Add(t0 + v);
                        }
                        if (v > lastVal)
                            lastVal = v;
                    }
                    if (ok && att.times.Count > 0)
                    {
                        stored.attempt = att;
                        if (lastVal > 0.0)
                            stored.lapTime = lastVal;
                        if (raceBestLapHistoryIdx == raceHistoryEditIdx)
                        {
                            raceBestLap = new RaceLapAttempt();
                            raceBestLap.keys.AddRange(att.keys);
                            raceBestLap.times.AddRange(att.times);
                            raceBestLapTime = stored.lapTime;
                        }
                        ProbeLog("gate splits: lap " + stored.number + " splits edited");
                        raceGraphHistoryEpoch++;
                        PersistSplitLap();
                    }
                    raceHistoryEditIdx = -1;
                }
            }
        }

        private List<int> DefaultGraphIdxs()
        {
            if (raceGraphDefIdxsEpoch == raceGraphHistoryEpoch)
                return raceGraphDefIdxsCache;
            raceGraphDefIdxsCache.Clear();
            for (int i = 0; i < raceLapHistory.Count; i++)
                raceGraphDefIdxsCache.Add(i);
            if (raceGraphDefIdxsCache.Count > 5)
            {
                raceGraphDefIdxsCache.Sort((a, b) => raceLapHistory[a].lapTime.CompareTo(raceLapHistory[b].lapTime));
                raceGraphDefIdxsCache.RemoveRange(5, raceGraphDefIdxsCache.Count - 5);
                raceGraphDefIdxsCache.Sort();
            }
            raceGraphDefIdxsCount = raceLapHistory.Count;
            raceGraphDefIdxsEpoch = raceGraphHistoryEpoch;
            raceGraphDefSet.Clear();
            foreach (var di in raceGraphDefIdxsCache)
                raceGraphDefSet.Add(di);
            return raceGraphDefIdxsCache;
        }

        private void DrawRaceHistoryGraph(Rect win, float hh, Event e)
        {
            bool repaint = e.type == EventType.Repaint;
            // vsBest: lines are deltas against the best lap. Off: absolute lap times
            // so all laps can be read together without picking a reference.
            bool vsBest = Prefs.lapHistoryVsBestConfig == null ||
                Prefs.lapHistoryVsBestConfig.Value;
            Rect g = new Rect(win.x + 10f, win.y + hh + 4f, win.width - 20f, 148f);
            if (repaint)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.28f * uiFadeAlpha);
                GUI.DrawTexture(g, whiteTex);
            }
            float rowsF = raceVisibleIdx.Count;
            if (raceGraphRowsAt != raceVisibleIdx.Count || raceGraphSpan <= 0f)
            {
                raceGraphRowsAt = raceVisibleIdx.Count;
                raceGraphSpan = rowsF;
                raceGraphFrom = 0f;
                raceGraphScaleInit = false;
            }
            if (raceGraphSpan > rowsF)
            {
                raceGraphSpan = rowsF;
                raceGraphFrom = 0f;
            }
            bool zoomed = raceGraphSpan < rowsF - 0.5f;
            Rect rzRect = new Rect(g.xMax - 48f, g.y + 4f, 44f, 18f);
            // Reset handled FIRST so the pan handler can't eat the click.
            if (e.type == EventType.MouseDown && e.button == 0 && zoomed && rzRect.Contains(e.mousePosition))
            {
                e.Use();
                raceGraphFrom = 0f;
                raceGraphSpan = rowsF;
                raceGraphScaleInit = false;
            }
            else if (e.type == EventType.ScrollWheel && g.Contains(e.mousePosition) && rowsF > 2f)
            {
                float frac = Mathf.Clamp01((e.mousePosition.x - g.x) / g.width);
                float rowAt = raceGraphFrom + frac * raceGraphSpan;
                float factor = e.delta.y > 0f ? 1.25f : 1f / 1.25f;
                raceGraphSpan = Mathf.Clamp(raceGraphSpan * factor, 2f, rowsF);
                raceGraphFrom = Mathf.Clamp(rowAt - frac * raceGraphSpan, 0f, rowsF - raceGraphSpan);
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && g.Contains(e.mousePosition) && zoomed &&
                !rzRect.Contains(e.mousePosition))
            {
                raceGraphPanDrag = true;
                raceGraphPanStartX = e.mousePosition.x;
                raceGraphPanStartFrom = raceGraphFrom;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && raceGraphPanDrag)
            {
                raceGraphFrom = Mathf.Clamp(raceGraphPanStartFrom - (e.mousePosition.x - raceGraphPanStartX) / g.width * raceGraphSpan, 0f, rowsF - raceGraphSpan);
                e.Use();
            }
            else if (raceGraphPanDrag && e.type == EventType.MouseUp && e.button == 0)
            {
                raceGraphPanDrag = false;
                e.Use();
            }
            if (zoomed && repaint)
            {
                bool rzHover = rzRect.Contains(e.mousePosition);
                GUI.color = rzHover ? new Color(1f, 1f, 1f, 0.15f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.4f * uiFadeAlpha);
                GUI.DrawTexture(rzRect, whiteTex);
                GUI.color = ThemeDimLabel(1f);
                GUI.Label(rzRect, "Reset", raceSplitsTrimStyle);
            }
            // Reference = session best, else the oldest stored lap.
            RaceLapAttempt refAtt = raceBestLap;
            if (refAtt == null && raceLapHistory.Count > 0)
                refAtt = raceLapHistory[0].attempt;
            // Plotted laps: ALL laps when ≤5 exist, else the 5 fastest; per-lap
            // Show/Hide toggles override.
            var laps = raceGraphLaps;
            laps.Clear();
            var defIdxs = DefaultGraphIdxs();
            if (raceGraphCustom)
            {
                for (int i = 0; i < raceLapHistory.Count; i++)
                    if (raceGraphSel.Contains(i))
                        laps.Add(raceLapHistory[i]);
            }
            else
            {
                foreach (var di in defIdxs)
                    laps.Add(raceLapHistory[di]);
            }
            // Data-driven vertical range: 5th–95th percentile of the plotted
            // deltas in the visible window, mapped edge-to-edge across the full
            // height so close laps separate clearly (blowout laps clip at the
            // edge — hide them via Show/Hide if they pollute the scale).
            int vi0 = Mathf.Max(0, (int)raceGraphFrom - 1);
            int vi1 = Mathf.Min(raceVisibleIdx.Count - 1, (int)(raceGraphFrom + raceGraphSpan) + 1);
            if (repaint)
            {
                var allD = raceGraphAllD;
                allD.Clear();
                foreach (var s in laps)
                {
                    if (vsBest && refAtt == null)
                        break;
                    for (int vi = vi0; vi <= vi1; vi++)
                    {
                        int rowIdx = raceVisibleIdx[vi];
                        string rowKey = rowIdx < raceRowKeys.Count ? raceRowKeys[rowIdx] : null;
                        if (rowKey == null)
                            continue;
                        int aIdx = RaceAttemptIndexOf(s.attempt, rowKey);
                        if (aIdx < 0)
                            continue;
                        double v = s.attempt.times[aIdx] - s.attempt.times[0];
                        if (vsBest)
                        {
                            int rIdx = RaceAttemptIndexOf(refAtt, rowKey);
                            if (rIdx < 0)
                                continue;
                            v -= refAtt.times[rIdx] - refAtt.times[0];
                        }
                        allD.Add(v);
                    }
                }
                double dLo = 0.0, dHi = 1.0;
                if (allD.Count > 0)
                {
                    allD.Sort();
                    if (vsBest)
                    {
                        // 5th-95th percentile so close laps separate clearly (blowout
                        // laps clip — hide them via Show/Hide if they pollute the scale).
                        dLo = allD[Mathf.Clamp((int)(allD.Count * 0.05), 0, allD.Count - 1)];
                        dHi = allD[Mathf.Clamp((int)(allD.Count * 0.95), 0, allD.Count - 1)];
                    }
                    else
                    {
                        // Absolute mode keeps the full range so every lap line stays
                        // visible (clipping the fastest/slowest would hide them).
                        dLo = allD[0];
                        dHi = allD[allD.Count - 1];
                        double pad = System.Math.Max(0.5, (dHi - dLo) * 0.05);
                        dLo -= pad;
                        dHi += pad;
                    }
                }
                if (dHi - dLo < 0.3)
                {
                    double mid = (dLo + dHi) * 0.5;
                    dLo = mid - 0.15;
                    dHi = mid + 0.15;
                }
                if (!raceGraphScaleInit)
                {
                    raceGraphYLo = (float)dLo;
                    raceGraphYHi = (float)dHi;
                    raceGraphScaleInit = true;
                }
                else
                {
                    raceGraphYLo = Mathf.Lerp(raceGraphYLo, (float)dLo, 0.3f);
                    raceGraphYHi = Mathf.Lerp(raceGraphYHi, (float)dHi, 0.3f);
                }
            }
            float yLo = raceGraphYLo;
            float yHi = raceGraphYHi;
            float ySpread = Mathf.Max(0.001f, yHi - yLo);
            // Clipped graph area — lines/dots enter and exit through the edges.
            GUI.BeginGroup(g);
            try
            {
            if (repaint)
            {
                if (vsBest)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.5f * uiFadeAlpha);
                    float tZero = Mathf.Clamp01((float)((0.0 - yLo) / ySpread));
                    float yZero = (g.height - 6f) - tZero * (g.height - 12f);
                    GUI.DrawTexture(new Rect(0f, yZero - 0.5f, g.width, 1f), whiteTex);
                }
                GUI.color = ThemeDimLabel(1f);
                GUI.Label(new Rect(4f, 2f, 90f, 16f),
                    vsBest ? (yHi >= 0 ? "+" : "") + yHi.ToString("F2") + "s" : FormatRaceTime(yHi),
                    raceSplitsTrimStyle);
                GUI.Label(new Rect(4f, g.height - 18f, 90f, 16f),
                    vsBest ? (yLo >= 0 ? "+" : "") + yLo.ToString("F2") + "s" : FormatRaceTime(yLo),
                    raceSplitsTrimStyle);
            }
            // Delta lines per lap (older → dimmer, newest brightest), smoothed
            // through the gate vertices, with a dot at every gate; hovering a dot
            // shows gate number + delta. Repaint only (the IMGUI quads are the
            // expensive part once many laps exist).
            ResetRaceGraphDots();
            if (repaint)
            {
                // Built at full opacity; the window fade is applied when the texture
                // is drawn, so fading alone never forces a rebuild.
                raceGraphRaster.Begin(Mathf.CeilToInt(g.width), Mathf.CeilToInt(g.height));
                for (int si = 0; si < laps.Count; si++)
                {
                    var s = laps[si];
                    Color c = RaceHistoryColors[si % RaceHistoryColors.Length];
                    float fade = 0.45f + 0.55f * (float)(si + 1) / laps.Count;
                    c.a = fade;
                    bool isBestLap = raceLapHistory.IndexOf(s) == raceBestLapHistoryIdx;
                    if (isBestLap)
                        c.a = 1f;
                    var lapPts = raceGraphPtBuf;
                    lapPts.Clear();
                    for (int vi = vi0; vi <= vi1; vi++)
                    {
                        int rowIdx = raceVisibleIdx[vi];
                        string rowKey = rowIdx < raceRowKeys.Count ? raceRowKeys[rowIdx] : null;
                        if (rowKey == null || (vsBest && refAtt == null))
                        {
                            RaceGraphEmitSmooth(lapPts, c);
                            continue;
                        }
                        int aIdx = RaceAttemptIndexOf(s.attempt, rowKey);
                        int rIdx = vsBest ? RaceAttemptIndexOf(refAtt, rowKey) : -1;
                        if (aIdx < 0 || (vsBest && rIdx < 0))
                        {
                            RaceGraphEmitSmooth(lapPts, c);
                            continue;
                        }
                        double dReal = s.attempt.times[aIdx] - s.attempt.times[0];
                        if (vsBest)
                            dReal -= refAtt.times[rIdx] - refAtt.times[0];
                        float dc = Mathf.Clamp((float)dReal, yLo, yHi);
                        float ty = (dc - yLo) / ySpread;
                        float x = 6f + (g.width - 12f) * (raceGraphSpan <= 1f ? 0f : (vi - raceGraphFrom) / (raceGraphSpan - 1f));
                        float y = (g.height - 6f) - ty * (g.height - 12f);
                        var p = new Vector2(x, y);
                        lapPts.Add(p);
                        raceGraphRaster.Box(new Rect(p.x - 2f, p.y - 2f, 4f, 4f),
                            new Color(c.r, c.g, c.b, Mathf.Min(1f, c.a + 0.25f)));
                        RaceGraphDot dot = RentRaceGraphDot();
                        dot.pos = p;
                        dot.rowIdx = rowIdx;
                        dot.lapNumber = s.number;
                        dot.delta = dReal;
                        dot.col = new Color(c.r, c.g, c.b, 1f * uiFadeAlpha);
                        raceGraphDots.Add(dot);
                    }
                    RaceGraphEmitSmooth(lapPts, c);
                }
                raceGraphRaster.End();
                GUI.color = new Color(1f, 1f, 1f, uiFadeAlpha);
                GUI.DrawTexture(new Rect(0f, 0f, raceGraphRaster.Texture.width, raceGraphRaster.Texture.height),
                    raceGraphRaster.Texture);
            // Hover tooltip: nearest gate dot under the cursor (local coords).
            Vector2 hm = e.mousePosition;
            if (raceGraphDots.Count > 0)
            {
                RaceGraphDot best = null;
                float bestDist = 10f;
                foreach (var dot in raceGraphDots)
                {
                    float dd = Vector2.Distance(dot.pos, hm);
                    if (dd < bestDist)
                    {
                        bestDist = dd;
                        best = dot;
                    }
                }
                if (best != null)
                {
                    string gLabel = best.rowIdx < raceGateLabels.Count ? raceGateLabels[best.rowIdx] : RaceGateLabel(best.rowIdx);
                    string text = gLabel + "  " + (vsBest
                        ? (best.delta >= 0 ? "+" : "") + best.delta.ToString("F2")
                        : FormatRaceTime(best.delta)) + "  L" + best.lapNumber;
                    Vector2 size = new Vector2(text.Length * 6.6f + 12f, 20f);
                    Vector2 tp = new Vector2(best.pos.x + 8f, best.pos.y - 26f);
                    if (tp.x + size.x > g.width - 2f)
                        tp.x = best.pos.x - size.x - 8f;
                    if (tp.y < 2f)
                        tp.y = best.pos.y + 10f;
                    if (tp.x < 2f)
                        tp.x = 2f;
                    GUI.color = new Color(0f, 0f, 0f, 0.85f * uiFadeAlpha);
                    GUI.DrawTexture(new Rect(tp, size), whiteTex);
                    GUI.color = ThemePanelBorder(1f);
                    GUI.DrawTexture(new Rect(tp.x, tp.y, size.x, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(tp.x, tp.y + size.y - 1f, size.x, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(tp.x, tp.y, 1f, size.y), whiteTex);
                    GUI.DrawTexture(new Rect(tp.x + size.x - 1f, tp.y, 1f, size.y), whiteTex);
                    GUI.color = best.col;
                    GUI.Label(new Rect(tp.x + 2f, tp.y, size.x - 4f, size.y), text, raceSplitsTrimStyle);
                }
            }
            }
            }
            finally { GUI.EndGroup(); }
            // Legend row.
            if (repaint)
            {
                float lx = g.x + 2f;
                for (int si = 0; si < laps.Count; si++)
                {
                    var s = laps[si];
                    GUI.color = RaceHistoryColors[si % RaceHistoryColors.Length];
                    GUI.DrawTexture(new Rect(lx, g.yMax + 3f, 10f, 10f), whiteTex);
                    GUI.color = ThemeDimLabel(1f);
                    string tag = "L" + s.number + " " + FormatRaceTime(s.lapTime) +
                        (raceLapHistory.IndexOf(s) == raceBestLapHistoryIdx ? "*" : "");
                    GUI.Label(new Rect(lx + 12f, g.yMax - 1f, 64f, 18f), tag, raceSplitsTrimStyle);
                    lx += 78f;
                    if (lx > g.xMax - 70f)
                        break;
                }
            }
            // Lap list (newest first; height follows the resizable window).
            float listH = Mathf.Max(24f, win.yMax - (g.yMax + 24f) - 8f);
            Rect listRect = new Rect(win.x + 8f, g.yMax + 24f, win.width - 16f, listH);
            var defSet = raceGraphDefSet;
            // Same lobby-style scrollbar skin as the editor list.
            EnsureRaceHistoryScrollSkin();
            GUIStyle oldListScrollbar = GUI.skin.verticalScrollbar;
            GUIStyle oldListThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = raceScrollbarStyle;
            GUI.skin.verticalScrollbarThumb = raceScrollbarThumbStyle;
            try
            {
            raceHistoryScroll = GUI.BeginScrollView(listRect, raceHistoryScroll,
                new Rect(0f, 0f, win.width - 36f, raceLapHistory.Count * 24f));
            RaceHistoryListButtons(win.width - 36f,
                out float lbX, out float leX, out float lsX, out float ldX);
            for (int li = raceLapHistory.Count - 1; li >= 0; li--)
            {
                var s = raceLapHistory[li];
                float y = (raceLapHistory.Count - 1 - li) * 24f;
                // Only the visible slice is drawn/hit-tested: with a long session the
                // list used to emit IMGUI calls for every stored lap on every repaint.
                if (y + 24f < raceHistoryScroll.y - 1f || y > raceHistoryScroll.y + listH + 1f)
                    continue;
                Rect bestRect = new Rect(lbX, y, 44f, 20f);
                Rect editRect = new Rect(leX, y, 44f, 20f);
                Rect showRect = new Rect(lsX, y, 44f, 20f);
                Rect delRect = new Rect(ldX, y, 54f, 20f);
                bool onGraph = raceGraphCustom ? raceGraphSel.Contains(li) : defSet.Contains(li);
                if (repaint)
                {
                    GUI.color = li == raceBestLapHistoryIdx ? ThemeLabel(1f) : ThemeDimLabel(1f);
                    GUI.Label(new Rect(0f, y, 146f, 20f),
                        "Lap " + s.number + "   " + FormatRaceTime(s.lapTime) + (li == raceBestLapHistoryIdx ? " (best)" : ""),
                        raceSplitsStyle);
                    GUI.color = new Color(0f, 0f, 0f, 0.35f * uiFadeAlpha);
                    GUI.DrawTexture(bestRect, whiteTex);
                    GUI.DrawTexture(editRect, whiteTex);
                    GUI.color = onGraph ? new Color(0.2f, 0.45f, 0.6f, 0.85f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.35f * uiFadeAlpha);
                    GUI.DrawTexture(showRect, whiteTex);
                    GUI.color = new Color(0.55f, 0.25f, 0.2f, 0.7f * uiFadeAlpha);
                    GUI.DrawTexture(delRect, whiteTex);
                    GUI.color = ThemeDimLabel(1f);
                    GUI.Label(bestRect, "Best", raceSplitsTrimStyle);
                    GUI.Label(editRect, "Edit", raceSplitsTrimStyle);
                    GUI.color = onGraph ? ThemeLabel(1f) : ThemeDimLabel(1f);
                    GUI.Label(showRect, onGraph ? "Hide" : "Show", raceSplitsTrimStyle);
                    GUI.color = ThemeDimLabel(1f);
                    GUI.Label(delRect, "Delete", raceSplitsTrimStyle);
                }
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    if (delRect.Contains(e.mousePosition))
                    {
                        e.Use();
                        bool wasBest = raceBestLapHistoryIdx == li;
                        raceLapHistory.RemoveAt(li);
                        raceGraphHistoryEpoch++;
                        if (wasBest)
                            RecomputeBestFromHistory();
                        else if (raceBestLapHistoryIdx > li)
                            raceBestLapHistoryIdx--;
                        var adj = new List<int>(raceGraphSel);
                        raceGraphSel.Clear();
                        foreach (var si2 in adj)
                        {
                            if (si2 == li)
                                continue;
                            raceGraphSel.Add(si2 > li ? si2 - 1 : si2);
                        }
                        ProbeLog("gate splits: lap " + s.number + " deleted from history");
                        PersistSplitLap();
                        break;
                    }
                    else if (bestRect.Contains(e.mousePosition))
                    {
                        e.Use();
                        raceBestLap = new RaceLapAttempt();
                        raceBestLap.keys.AddRange(s.attempt.keys);
                        raceBestLap.times.AddRange(s.attempt.times);
                        raceBestLapTime = s.lapTime;
                        raceBestLapHistoryIdx = li;
                        ProbeLog("gate splits: lap " + s.number + " set as session best");
                    }
                    else if (showRect.Contains(e.mousePosition))
                    {
                        e.Use();
                        raceGraphCustom = true;
                        if (raceGraphSel.Contains(li))
                            raceGraphSel.Remove(li);
                        else
                        {
                            if (raceGraphSel.Count == 0)
                            {
                                foreach (var di in defIdxs)
                                    raceGraphSel.Add(di);
                            }
                            while (raceGraphSel.Count >= 5)
                            {
                                int worst = -1;
                                double worstT = double.MinValue;
                                foreach (var si2 in raceGraphSel)
                                {
                                    if (si2 >= 0 && si2 < raceLapHistory.Count && raceLapHistory[si2].lapTime > worstT)
                                    {
                                        worstT = raceLapHistory[si2].lapTime;
                                        worst = si2;
                                    }
                                }
                                if (worst < 0)
                                    break;
                                raceGraphSel.Remove(worst);
                            }
                            raceGraphSel.Add(li);
                        }
                    }
                    else if (editRect.Contains(e.mousePosition))
                    {
                        e.Use();
                        raceHistoryEditIdx = li;
                        raceHistoryEditVals.Clear();
                        foreach (int rowIdx in raceVisibleIdx)
                        {
                            string rowKey = rowIdx < raceRowKeys.Count ? raceRowKeys[rowIdx] : null;
                            int aIdx = rowKey != null ? RaceAttemptIndexOf(s.attempt, rowKey) : -1;
                            double cum = aIdx >= 0 ? s.attempt.times[aIdx] - s.attempt.times[0] : 0.0;
                            raceHistoryEditVals.Add(cum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
            GUI.EndScrollView();
            }
            finally
            {
                GUI.skin.verticalScrollbar = oldListScrollbar;
                GUI.skin.verticalScrollbarThumb = oldListThumb;
            }
        }

        // Memoised on the displayed precision: the splits panel formats every row
        // each Repaint, and nearly all of those values are unchanged frame to frame.
        private static readonly NumberTextCache raceTimeCache = new NumberTextCache(100.0, FormatRaceTimeUncached);
        private static readonly NumberTextCache raceTimeP3Cache = new NumberTextCache(1000.0, FormatRaceTimeP3Uncached);
        private static readonly NumberTextCache signedF2Cache = new NumberTextCache(100.0, v => (v >= 0 ? "+" : "") + v.ToString("F2"));
        private static readonly NumberTextCache f2Cache = new NumberTextCache(100.0, v => v.ToString("F2"));

        private static string FormatRaceTime(double t)
        {
            return t < 0.0 ? "—" : raceTimeCache.Get(t);
        }

        // "+0.12" / "-0.12".
        private static string FormatSignedF2(double d)
        {
            return signedF2Cache.Get(d);
        }

        private static string FormatF2(double d)
        {
            return f2Cache.Get(d);
        }

        private static string FormatRaceTimeUncached(double t)
        {
            if (t < 60.0)
                return t.ToString("F2");
            int m = (int)(t / 60.0);
            return m + ":" + (t - m * 60.0).ToString("00.00");
        }

        // Lap totals get one more decimal than the split columns so close bests
        // are distinguishable in the footer.
        private static string FormatRaceTimeP3(double t)
        {
            return t < 0.0 ? "—" : raceTimeP3Cache.Get(t);
        }

        private static string FormatRaceTimeP3Uncached(double t)
        {
            if (t < 60.0)
                return t.ToString("F3");
            int m = (int)(t / 60.0);
            return m + ":" + (t - m * 60.0).ToString("00.000");
        }

        // Gate-to-gate duration for attempt entry idx (entry 0 has no segment).
        private static double RaceAttemptSegment(RaceLapAttempt a, int idx)
        {
            if (a == null || idx <= 0 || idx >= a.times.Count)
                return -1.0;
            return a.times[idx] - a.times[idx - 1];
        }

        // Merged finish (Start arch is the line): the closing segment is the lap
        // time minus the last gate's cumulative split.
        private double BestMergedFinishSegment()
        {
            if (raceBestLap == null || raceBestLap.times.Count < 2 || raceBestLapTime <= 0.0)
                return -1.0;
            int last = raceBestLap.times.Count - 1;
            double seg = raceBestLapTime - (raceBestLap.times[last] - raceBestLap.times[0]);
            return seg > 0.0 ? seg : -1.0;
        }

        // Smooths a polyline of gate vertices with a centripetal Catmull-Rom spline
        // so the trace reads as a curve instead of a blocky connect-the-dots, and
        // records it into the graph raster.
        private void RaceGraphEmitSmooth(List<Vector2> pts, Color c)
        {
            int n = pts.Count;
            if (n < 2)
            {
                pts.Clear();
                return;
            }
            List<Vector2> smooth = raceGraphSmoothBuf;
            smooth.Clear();
            smooth.Add(pts[0]);
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 p0 = i == 0 ? 2f * pts[0] - pts[1] : pts[i - 1];
                Vector2 p1 = pts[i];
                Vector2 p2 = pts[i + 1];
                Vector2 p3 = i + 2 < n ? pts[i + 2] : 2f * pts[n - 1] - pts[n - 2];
                float dist = Vector2.Distance(p1, p2);
                int steps = Mathf.Clamp(Mathf.CeilToInt(dist / 12f), 2, 12);
                for (int k = 1; k <= steps; k++)
                    smooth.Add(CatmullRom(p0, p1, p2, p3, (float)k / steps));
            }
            raceGraphRaster.Polyline(smooth, new Color(c.r, c.g, c.b, RaceGraphLineAlpha(c.a)), 2f);
            pts.Clear();
        }

        private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                2f * p1 +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }


        // ---- Phase 0 v2: gate-pass capture probes (logging only) ----
        private readonly HashSet<string> raceTrigDumpedTypes = new HashSet<string>();
        private readonly Dictionary<int, float> raceTrigLastAt = new Dictionary<int, float>();
        private readonly HashSet<string> raceLapDumpedTypes = new HashSet<string>();
        private float raceLapNextLogAt = 0f;
        private bool raceItemDumped = false;

        private float racePatchRetryAt = 0f;
    }
}
