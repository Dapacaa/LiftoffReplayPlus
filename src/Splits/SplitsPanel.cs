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
        private void DrawGateSplitsPanel()
        {
            float savedFade = uiFadeAlpha;
            uiFadeAlpha = raceSplitsIdleAlpha;
            try { DrawGateSplitsPanelInner(); }
            finally { uiFadeAlpha = savedFade; }
        }

        private void DrawGateSplitsPanelInner()
        {
            if (Prefs.gateSplitsConfig == null || !Prefs.gateSplitsConfig.Value)
            {
                raceSplitsScreenRect = new Rect(0f, 0f, -1f, -1f);
                return;
            }
            // The panel is a live-flight overlay: only draw in a content scene.
            if (!IsContentSceneActive())
            {
                raceSplitsScreenRect = new Rect(0f, 0f, -1f, -1f);
                return;
            }
            Event e = Event.current;
            bool bySegments = Prefs.gateSplitsSegmentsConfig != null &&
                Prefs.gateSplitsSegmentsConfig.Value;
            EnsureSettingsStyles();
            if (raceSplitsStyle == null)
            {
                raceSplitsStyle = new GUIStyle(GUI.skin.label);
                raceSplitsStyle.fontSize = 15;
                raceSplitsStyle.fontStyle = FontStyle.Bold;
                raceSplitsStyle.normal.textColor = Color.white;
                raceSplitsDeltaStyle = new GUIStyle(raceSplitsStyle);
                raceSplitsDeltaStyle.alignment = TextAnchor.MiddleRight;
                raceSplitsBigStyle = new GUIStyle(raceSplitsStyle);
                raceSplitsBigStyle.fontSize = 28;
                raceSplitsBigStyle.fontStyle = FontStyle.Bold;
                raceSplitsBigStyle.alignment = TextAnchor.LowerRight;
                raceSplitsTrimStyle = new GUIStyle(GUI.skin.label);
                raceSplitsTrimStyle.fontSize = 11;
                raceSplitsTrimStyle.fontStyle = FontStyle.Bold;
                raceSplitsTrimStyle.alignment = TextAnchor.MiddleCenter;
            }
            int curCount = raceCurLap != null ? raceCurLap.keys.Count : 0;
            // While the game's gate event is bound, show only the rows the event path
            // has actually acquired. The scene-scan route is a fallback for builds
            // without the binding; displaying its label-only route here made the full
            // gate list appear and then vanish at the first crossing.
            int rows = GateSplitsBinding.Subscribed
                ? raceRowKeys.Count
                : (raceLapGateTotal > 0 ? raceVisibleIdx.Count : Mathf.Max(raceCycleKeys.Count, curCount));
            float w = Mathf.Clamp(raceSplitsPanelW, 240f, Mathf.Max(240f, Screen.width - 40f));
            raceSplitsPanelW = w;
            float headerH = 48f;
            float rowH = 22f;
            float footerH = 70f;
            float minPanelH = headerH + rowH + footerH + 6f;
            // Until the user manually resizes, the panel hugs its gate count (up to 20
            // rows) instead of reserving a fixed 20-row block and leaving dead space on
            // short tracks.
            if (!raceSplitsManualSize)
                raceSplitsPanelH = headerH + Mathf.Clamp(rows, 1, 20) * rowH + footerH + 6f;
            float h = Mathf.Clamp(raceSplitsPanelH, minPanelH, Mathf.Max(minPanelH, Screen.height - 40f));
            raceSplitsPanelH = h;
            raceSplitsPanelY = Mathf.Clamp(raceSplitsPanelY, 4f, Mathf.Max(4f, Screen.height - h - 4f));
            int visRows = Mathf.Clamp(Mathf.FloorToInt((h - headerH - footerH - 6f) / rowH), 1, Mathf.Max(1, rows));
            Rect panel = new Rect(raceSplitsPanelX, raceSplitsPanelY, w, h);
            raceSplitsScreenRect = panel;
            Rect header = new Rect(panel.x, panel.y, panel.width, headerH);
            // Resizable (blue edge/corner bars on hover, StickGraph style).
            raceSplitsResizeEdgeHover = ComputeWindowResizeEdge(panel, e.mousePosition, false);
            if (e.type == EventType.MouseDown && e.button == 0 && raceSplitsResizeEdgeHover != 0)
            {
                raceSplitsResizing = true;
                raceSplitsManualSize = true;
                raceSplitsResizeEdge = raceSplitsResizeEdgeHover;
                raceSplitsResizeStartMouse = e.mousePosition;
                raceSplitsResizeStartRect = panel;
                raceSplitsDrag.Cancel();
                e.Use();
            }
            else if (raceSplitsResizing && e.type == EventType.MouseDrag && e.button == 0)
            {
                Rect nr = ApplyWindowResizeDrag(raceSplitsResizeEdge, raceSplitsResizeStartMouse, raceSplitsResizeStartRect, e.mousePosition, 240f, minPanelH);
                raceSplitsPanelX = nr.x;
                raceSplitsPanelY = nr.y;
                raceSplitsPanelW = nr.width;
                raceSplitsPanelH = nr.height;
                e.Use();
            }
            else if (raceSplitsResizing && e.type == EventType.MouseUp && e.button == 0)
            {
                raceSplitsResizing = false;
                raceSplitsResizeEdge = 0;
                e.Use();
            }
            // Header close button: turns the panel (and its config) off.
            Rect splitsCloseRect = new Rect(panel.xMax - 36f, panel.y + 4f, 28f, 22f);
            Rect splitsPinRect = new Rect(panel.xMax - 66f, panel.y + 4f, 24f, 20f);
            bool splitsCloseHover = splitsCloseRect.Contains(e.mousePosition);
            // Footer button click handlers run on input events (before the
            // Repaint-only draw gate below).
            float footYPre = panel.y + headerH + visRows * rowH + 4f;
            Rect clearRectPre = new Rect(panel.x + 8f, footYPre + 8f, 92f, 28f);
            Rect histRectPre = new Rect(panel.x + 8f + 96f, footYPre + 8f, 92f, 28f);
            // Lower row: identical 92x28 boxes to the upper row.
            Rect modeRectPre = new Rect(panel.x + 8f, footYPre + 38f, 92f, 28f);
            Rect resetRectPre = new Rect(panel.x + 8f + 96f, footYPre + 38f, 92f, 28f);
            // Drag from anywhere on the panel (like the replay windows): arm a
            // candidate, promote on drag. Buttons/close are excluded.
            if (e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) &&
                raceSplitsResizeEdgeHover == 0)
            {
                // Buttons (and the lap history window on top) own their clicks.
                bool splitsDragBlocked = splitsCloseHover || splitsPinRect.Contains(e.mousePosition) ||
                    clearRectPre.Contains(e.mousePosition) ||
                    histRectPre.Contains(e.mousePosition) || modeRectPre.Contains(e.mousePosition) ||
                    resetRectPre.Contains(e.mousePosition) ||
                    (raceHistoryOpen && raceHistoryW > 1f && raceHistoryH > 1f &&
                     new Rect(raceHistoryX, raceHistoryY, raceHistoryW, raceHistoryH).Contains(e.mousePosition));
                raceSplitsDrag.Arm(e.mousePosition, new Vector2(raceSplitsPanelX, raceSplitsPanelY), splitsDragBlocked);
            }
            Vector2 splitsPos = new Vector2(raceSplitsPanelX, raceSplitsPanelY);
            if (raceSplitsDrag.Drag(e, false, ref splitsPos))
            {
                raceSplitsPanelX = Mathf.Clamp(splitsPos.x, 4f, Mathf.Max(4f, Screen.width - w - 4f));
                raceSplitsPanelY = Mathf.Clamp(splitsPos.y, 4f, Mathf.Max(4f, Screen.height - 40f));
            }
            if (e.type == EventType.MouseUp && e.button == 0)
                raceSplitsDrag.End();
            if (e.type == EventType.MouseDown && e.button == 0 && splitsPinRect.Contains(e.mousePosition))
            {
                e.Use();
                raceSplitsPinned = !raceSplitsPinned;
                overlayMouseMovedAt = Time.unscaledTime;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && splitsCloseHover)
            {
                e.Use();
                if (Prefs.gateSplitsConfig != null)
                {
                    Prefs.gateSplitsConfig.Value = false;
                    if (Prefs.preferencesConfig != null)
                        Prefs.RequestSave();
                }
                return;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && clearRectPre.Contains(e.mousePosition))
            {
                e.Use();
                raceBestLap = null;
                raceBestLapTime = -1;
                raceBestLapHistoryIdx = -1;
                ProbeLog("gate splits: session best cleared");
            }
            if (e.type == EventType.MouseDown && e.button == 0 && histRectPre.Contains(e.mousePosition))
            {
                e.Use();
                raceHistoryOpen = !raceHistoryOpen;
                raceGraphSpan = 0f;
                raceGraphFrom = 0f;
                if (!raceHistoryOpen)
                    raceHistoryEditIdx = -1;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && modeRectPre.Contains(e.mousePosition))
            {
                e.Use();
                if (Prefs.gateSplitsSegmentsConfig != null)
                {
                    Prefs.gateSplitsSegmentsConfig.Value = !Prefs.gateSplitsSegmentsConfig.Value;
                    if (Prefs.preferencesConfig != null)
                        Prefs.RequestSave();
                }
            }
            if (e.type == EventType.MouseDown && e.button == 0 && resetRectPre.Contains(e.mousePosition))
            {
                e.Use();
                ResetRaceRoute();
            }
            // Wheel over the rows: manual scroll for long tracks (50-gate layouts).
            // Any gate crossing or lap start snaps the view back to the live row.
            if (e.type == EventType.ScrollWheel && rows > visRows && panel.Contains(e.mousePosition) &&
                !(raceHistoryOpen && raceHistoryW > 1f && raceHistoryH > 1f &&
                  new Rect(raceHistoryX, raceHistoryY, raceHistoryW, raceHistoryH).Contains(e.mousePosition)))
            {
                if (!raceSplitsScrollManual)
                {
                    raceSplitsScrollManual = true;
                    raceSplitsScroll = raceSplitsLastAutoOffset;
                }
                int maxOff = Mathf.Max(0, rows - visRows);
                // Sign-normalised: one wheel notch advances three rows, down = lower
                // gates (the raw delta sign is inverted from that expectation here).
                raceSplitsScroll = Mathf.Clamp(raceSplitsScroll + Mathf.Sign(e.delta.y) * 3f, 0f, maxOff);
                e.Use();
            }
            // Everything below is pure drawing — only needed on Repaint. The lap
            // history window is the exception: it must process mouse/key events on
            // every event (drag, resize, graph scroll-zoom, list buttons), so run
            // its input pass here and its drawn pass at the very end (on top).
            if (e.type != EventType.Repaint)
            {
                if (raceHistoryOpen)
                    DrawRaceHistoryWindow(e);
                return;
            }
            // Same panel darkness as the lap-history window.
            GUI.color = new Color(0f, 0f, 0f, 0.55f * uiFadeAlpha);
            GUI.DrawTexture(panel, whiteTex);
            GUI.color = ThemePanelBorder(1f);
            GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), whiteTex);
            GUI.color = ThemeLabel(1f);
            GUI.Label(new Rect(panel.x + 8f, panel.y + 3f, 130f, 20f), "Gate Splits", raceSplitsStyle);
            EnsureIcons();
            DrawStyledToggleButton(splitsPinRect, iconPin, raceSplitsPinned, raceSplitsIdleAlpha, false, -1);
            GUI.color = splitsCloseHover ? ThemeLabel(1f) : ThemeDimLabel(1f);
            GUI.Label(splitsCloseRect, "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : (settingsCloseStyle != null ? settingsCloseStyle : raceSplitsStyle));
            GUI.color = ThemeDimLabel(1f);
            GUI.Label(new Rect(panel.x + 8f, panel.y + 26f, panel.width - 16f, 20f),
                rows > 0 ? rows + " gates" : "no gates yet", raceSplitsStyle);
            Color goodC = new Color(0.4f, 0.95f, 0.5f, 1f * uiFadeAlpha);
            Color badC = new Color(0.98f, 0.45f, 0.4f, 1f * uiFadeAlpha);
            Color noC = new Color(0.6f, 0.62f, 0.66f, 1f * uiFadeAlpha);
            float bestColW = 64f;
            float deltaColW = 70f;
            float nameColW = panel.width - 16f - bestColW - deltaColW - 12f;
            double baseT = raceSyncT0 >= 0 ? raceSyncT0 : (raceCurLap != null ? raceCurLap.times[0] : 0.0);
            double liveNow = raceGameLapTime >= 0 ? raceGameLapTime : (raceCurLap != null ? RaceNowTime() - baseT : 0.0);
            // Scroll: keep the last passed / active row visible (LiveSplit style).
            // After a lap closes the new attempt only has Start, so the view
            // scrolls back to the top and deltas appear one by one as gates pass.
            RaceLapAttempt followLap = raceCurLap;
            int followPos = 0;
            if (rows > 0 && followLap != null && followLap.keys.Count > 0)
            {
                for (int v = 0; v < rows; v++)
                {
                    int rowIdx = raceLapGateTotal > 0 ? raceVisibleIdx[v] : v;
                    string rk = raceLapGateTotal > 0 && rowIdx < raceRowKeys.Count ? raceRowKeys[rowIdx] : null;
                    bool isMergedFin = raceMergedFinish && raceLapGateTotal > 0 && rowIdx == raceLapGateTotal - 1;
                    bool isPassed = !isMergedFin && (rk != null
                        ? RaceAttemptIndexOf(followLap, rk) >= 0
                        : rowIdx < followLap.keys.Count);
                    if (isPassed)
                        followPos = v;
                }
            }
            int offset = 0;
            if (rows > visRows)
            {
                offset = Mathf.Max(0, followPos - (visRows - 1));
                offset = Mathf.Min(offset, rows - visRows);
            }
            // Remembered so entering manual scroll starts from the live view, and
            // manual scroll wins until the next gate crossing/lap start re-anchors.
            raceSplitsLastAutoOffset = offset;
            if (raceSplitsScrollManual && rows > visRows)
                offset = Mathf.Clamp(Mathf.RoundToInt(raceSplitsScroll), 0, rows - visRows);
            for (int v = 0; v < visRows; v++)
            {
                int i = offset + v;
                if (i >= rows)
                    break;
                int rowIdx = raceLapGateTotal > 0 ? raceVisibleIdx[i] : i;
                float ry = panel.y + headerH + v * rowH;
                string label = rowIdx < raceGateLabels.Count ? raceGateLabels[rowIdx] : RaceGateLabel(rowIdx);
                string rowKey = raceLapGateTotal > 0 && rowIdx < raceRowKeys.Count ? raceRowKeys[rowIdx] : null;
                bool isMergedFin = raceMergedFinish && raceLapGateTotal > 0 && rowIdx == raceLapGateTotal - 1;
                Rect bestRect = new Rect(panel.xMax - 8f - bestColW, ry, bestColW, rowH);
                Rect deltaRect = new Rect(bestRect.x - 6f - deltaColW, ry, deltaColW, rowH);
                Rect nameRect = new Rect(panel.x + 8f, ry, nameColW, rowH);
                GUI.color = i % 2 == 1 ? new Color(1f, 1f, 1f, 0.05f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.14f * uiFadeAlpha);
                GUI.DrawTexture(new Rect(panel.x + 1f, ry, panel.width - 2f, rowH), whiteTex);
                bool passed = !isMergedFin && raceCurLap != null && (rowKey != null
                    ? RaceAttemptIndexOf(raceCurLap, rowKey) >= 0
                    : rowIdx < raceCurLap.keys.Count);
                bool active = !passed && raceCurLap != null && i == followPos + 1;
                if (active)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.08f * uiFadeAlpha);
                    GUI.DrawTexture(new Rect(panel.x + 2f, ry, panel.width - 4f, rowH), whiteTex);
                }
                int myIdx = rowKey != null
                    ? RaceAttemptIndexOf(raceCurLap, rowKey)
                    : (raceCurLap != null && rowIdx < raceCurLap.keys.Count ? rowIdx : -1);
                string bestText = "—";
                Color bestCol = ThemeDimLabel(1f);
                int bestIdx = -1;
                if (isMergedFin)
                {
                    // Merged Finish row shares the Start key — its split is the full
                    // lap time (cumulative) or the closing segment (segments).
                    if (raceBestLapTime > 0)
                    {
                        double finSeg = bySegments ? BestMergedFinishSegment() : -1.0;
                        if (!bySegments || finSeg >= 0.0)
                        {
                            bestText = FormatRaceTime(bySegments ? finSeg : raceBestLapTime);
                            bestCol = ThemeLabel(1f);
                        }
                    }
                }
                else
                {
                    bestIdx = rowKey != null
                        ? RaceAttemptIndexOf(raceBestLap, rowKey)
                        : (raceBestLap != null && rowIdx < raceBestLap.keys.Count ? rowIdx : -1);
                    if (bestIdx >= 0)
                    {
                        double refVal = bySegments
                            ? RaceAttemptSegment(raceBestLap, bestIdx)
                            : (raceBestLap.times[bestIdx] - raceBestLap.times[0]);
                        if (!bySegments || refVal >= 0.0)
                        {
                            bestText = FormatRaceTime(refVal);
                            bestCol = ThemeLabel(1f);
                        }
                    }
                    else if (passed && raceCurLap != null && myIdx >= 0 && myIdx < raceCurLap.times.Count)
                    {
                        double refVal = bySegments
                            ? RaceAttemptSegment(raceCurLap, myIdx)
                            : (raceCurLap.times[myIdx] - raceCurLap.times[0]);
                        if (!bySegments || refVal >= 0.0)
                            bestText = FormatRaceTime(refVal);
                    }
                }
                string deltaText = "—";
                Color dc = noC;
                double shownDelta = 0.0;
                bool hasShownDelta = false;
                if (passed && bestIdx >= 0 && raceCurLap != null &&
                    myIdx >= 0 && myIdx < raceCurLap.times.Count &&
                    (!bySegments || (myIdx > 0 && bestIdx > 0)))
                {
                    double d = bySegments
                        ? RaceAttemptSegment(raceCurLap, myIdx) - RaceAttemptSegment(raceBestLap, bestIdx)
                        : (raceCurLap.times[myIdx] - raceCurLap.times[0]) - (raceBestLap.times[bestIdx] - raceBestLap.times[0]);
                    if (d < -0.005) { dc = goodC; deltaText = FormatF2(d); shownDelta = d; hasShownDelta = true; }
                    else if (d > 0.005) { dc = badC; deltaText = FormatSignedF2(d); shownDelta = d; hasShownDelta = true; }
                    // Within a hundredth: either the displayed lap IS the reference
                    // (just finished best) or a dead tie — a column of 0.00 is noise.
                    else { dc = noC; deltaText = "—"; }
                }
                if (active)
                {
                    double refT;
                    if (bySegments)
                    {
                        refT = isMergedFin
                            ? BestMergedFinishSegment()
                            : (bestIdx > 0 ? RaceAttemptSegment(raceBestLap, bestIdx) : -1.0);
                    }
                    else
                    {
                        refT = isMergedFin
                            ? raceBestLapTime
                            : (bestIdx >= 0 ? raceBestLap.times[bestIdx] - raceBestLap.times[0] : -1.0);
                    }
                    if (refT >= 0)
                    {
                        // Segment mode compares the segment in progress: time since
                        // the last passed gate vs the best for this same span.
                        double live = liveNow;
                        if (bySegments && raceCurLap != null && raceCurLap.times.Count > 0)
                            live = liveNow - raceCurLap.times[raceCurLap.times.Count - 1];
                        double d = live - refT;
                        dc = d < 0 ? goodC : badC;
                        deltaText = FormatSignedF2(d);
                        shownDelta = d;
                        hasShownDelta = true;
                    }
                }
                // Magnitude bar beside the delta so faster/slower stretches read at a
                // glance in both cumulative and segment modes.
                if (hasShownDelta && shownDelta != 0.0)
                {
                    float barMax = deltaColW * 1.25f;
                    // Square-root scale, asymmetric on purpose: losses use a ~4s
                    // reference so big mistakes stop clipping, while gains use a
                    // ~0.5s reference so the tiny savings on an optimised track
                    // still show a clearly visible green bar.
                    float refS = shownDelta < 0.0 ? 0.5f : 4f;
                    float frac = Mathf.Sqrt(Mathf.Clamp01(Mathf.Abs((float)shownDelta) / refS));
                    float bw = Mathf.Max(2f, frac * barMax);
                    GUI.color = shownDelta < 0 ? goodC : badC;
                    // Bar's right end sits just clear of the right-aligned delta text
                    // (~8px), then grows left into the empty gate-name area.
                    raceBarSizeContent.text = deltaText;
                    float textW = raceSplitsDeltaStyle != null
                        ? raceSplitsDeltaStyle.CalcSize(raceBarSizeContent).x : 40f;
                    float barRight = deltaRect.xMax - textW - 8f;
                    GUI.DrawTexture(new Rect(barRight - bw, ry + rowH - 4f, bw, 3f), whiteTex);
                }
                GUI.color = (active || passed) ? ThemeLabel(1f) : ThemeDimLabel(1f);
                GUI.Label(nameRect, label, raceSplitsStyle);
                GUI.color = dc;
                GUI.Label(deltaRect, deltaText, raceSplitsDeltaStyle);
                GUI.color = bestCol;
                GUI.Label(bestRect, bestText, raceSplitsDeltaStyle);
            }
            // Row position indicator for scrollable long tracks.
            if (rows > visRows)
            {
                float rowsTop = panel.y + headerH;
                float rowsH = visRows * rowH;
                GUI.color = new Color(1f, 1f, 1f, 0.08f * uiFadeAlpha);
                GUI.DrawTexture(new Rect(panel.xMax - 4f, rowsTop, 2f, rowsH), whiteTex);
                float thumbH = Mathf.Max(12f, rowsH * (float)visRows / rows);
                float t = rows > visRows ? (float)offset / (rows - visRows) : 0f;
                GUI.color = new Color(1f, 1f, 1f, 0.35f * uiFadeAlpha);
                GUI.DrawTexture(new Rect(panel.xMax - 4f, rowsTop + t * (rowsH - thumbH), 2f, thumbH), whiteTex);
            }
            // Footer: separator + big time (always visible, LiveSplit style).
            float footY = panel.y + headerH + visRows * rowH + 4f;
            GUI.color = ThemePanelBorder(1f);
            GUI.DrawTexture(new Rect(panel.x + 8f, footY, panel.width - 16f, 1f), whiteTex);
            Color bigCol = ThemeLabel(1f);
            if (raceCurLap != null && raceBestLap != null && raceBestLap.times.Count > 0)
            {
                int refIdx = curCount < raceBestLap.times.Count ? curCount : raceBestLap.times.Count - 1;
                double d = liveNow - (raceBestLap.times[refIdx] - raceBestLap.times[0]);
                bigCol = d < 0 ? goodC : badC;
            }
            GUI.color = bigCol;
            GUI.Label(new Rect(panel.x + 8f, footY + 3f, panel.width - 16f, 32f),
                FormatRaceTime(raceCurLap != null ? liveNow : 0.0), raceSplitsBigStyle);
            // Session best sits under the live lap time at the bottom.
            GUI.color = ThemeDimLabel(1f);
            GUI.Label(new Rect(panel.x + 8f, footY + 37f, panel.width - 16f, 18f),
                raceBestLapTime > 0 ? "Best " + FormatRaceTimeP3(raceBestLapTime) : "Best —",
                raceSplitsDeltaStyle);
            // Four equal 92x28 buttons in two rows: Clear Best / Lap History above,
            // Segments / Reset Route below.
            Rect clearRect = new Rect(panel.x + 8f, footY + 8f, 92f, 28f);
            bool clearHover = clearRect.Contains(e.mousePosition);
            GUI.color = clearHover ? new Color(1f, 1f, 1f, 0.12f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.3f * uiFadeAlpha);
            GUI.DrawTexture(clearRect, whiteTex);
            GUI.color = clearHover ? ThemeLabel(1f) : ThemeDimLabel(1f);
            GUI.Label(clearRect, "Clear Best", raceSplitsTrimStyle);
            Rect histRect = new Rect(panel.x + 8f + 96f, footY + 8f, 92f, 28f);
            bool histHover = histRect.Contains(e.mousePosition);
            GUI.color = histHover ? new Color(1f, 1f, 1f, 0.12f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.3f * uiFadeAlpha);
            GUI.DrawTexture(histRect, whiteTex);
            GUI.color = histHover ? ThemeLabel(1f) : ThemeDimLabel(1f);
            GUI.Label(histRect, raceHistoryOpen ? "Close Laps" : "Lap History", raceSplitsTrimStyle);
            Rect modeRect = new Rect(panel.x + 8f, footY + 38f, 92f, 28f);
            bool modeHover = modeRect.Contains(e.mousePosition);
            GUI.color = modeHover ? new Color(1f, 1f, 1f, 0.12f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.3f * uiFadeAlpha);
            GUI.DrawTexture(modeRect, whiteTex);
            GUI.color = modeHover ? ThemeLabel(1f) : ThemeDimLabel(1f);
            GUI.Label(modeRect, bySegments ? "Segments" : "Cumulative", raceSplitsTrimStyle);
            Rect resetRect = new Rect(panel.x + 8f + 96f, footY + 38f, 92f, 28f);
            bool resetHover = resetRect.Contains(e.mousePosition);
            GUI.color = resetHover ? new Color(1f, 1f, 1f, 0.12f * uiFadeAlpha) : new Color(0f, 0f, 0f, 0.3f * uiFadeAlpha);
            GUI.DrawTexture(resetRect, whiteTex);
            GUI.color = resetHover ? ThemeLabel(1f) : ThemeDimLabel(1f);
            GUI.Label(resetRect, "Reset Route", raceSplitsTrimStyle);
            DrawWindowResizeBars(panel, raceSplitsResizing ? raceSplitsResizeEdge : raceSplitsResizeEdgeHover);
            GUI.color = Color.white;
            if (raceHistoryOpen)
                DrawRaceHistoryWindow(e);
        }

        private GUIStyle raceHistoryFieldStyle = null;
        private Texture2D raceHistoryFieldTex = null;
        private Texture2D raceHistoryFieldFocusTex = null;
    }
}
