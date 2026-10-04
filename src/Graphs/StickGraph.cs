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
        private void BuildStickGraphTraceTexture(StickGraphState sg, int tw, int th, float lapStart, float lapDuration)
        {
            GhostLineEntry pilot = sg.pilot;
            int channel = sg.channel;
            bool heatmap = sg.heatmap;
            Color32 signal = PilotTraceColor(pilot);
            TraceTexture.Build(ref sg.traceTex, ref sg.tracePixels, tw, th, 0f,
                (float f, out Color32 color) =>
                {
                    float stateTime = lapStart + f * lapDuration;
                    color = heatmap ? (Color32)StickGraphHeatColor(pilot, stateTime) : signal;
                    return SampleStickChannel(pilot, stateTime, channel);
                });
            sg.traceLastBuildTime = Time.unscaledTime;
            sg.traceCachePilot = sg.pilot;
            sg.traceCacheChannel = sg.channel;
            sg.traceCachePlotW = tw;
            sg.traceCachePlotH = th;
            sg.traceCacheHeatmap = sg.heatmap;
            sg.traceCacheHeatRange = GhostLinesPlugin.speedHeatmapRangeRevision;
            sg.traceCacheInputs = sg.pilot.originalInputs;
            sg.traceCacheLapStart = lapStart;
            sg.traceCacheLapDuration = lapDuration;
        }

        // A pilot's trace colour, nudged toward the theme's text colour for contrast.
        private Color PilotTraceColor(GhostLineEntry pilot)
        {
            return darkMode
                ? Color.Lerp(pilot.color, new Color(1f, 1f, 1f), 0.25f)
                : Color.Lerp(pilot.color, new Color(0f, 0f, 0f), 0.4f);
        }

        // Heatmap colour by drone speed, on the same shared range and colours as
        // the line heatmap, so a colour means the same speed in both views.
        private static Color StickGraphHeatColor(GhostLineEntry pilot, float stateTime)
        {
            float speed = GhostLinesPlugin.SampleDroneSpeedAtTime(pilot, stateTime);
            return GhostLinesPlugin.IsFinite(speed)
                ? GhostLinesPlugin.SpeedHeatmapColor(GhostLinesPlugin.NormalizeHeatmapSpeed(speed))
                : new Color(0.5f, 0.5f, 0.5f, 1f);
        }

        private bool IsStickGraphTraceStale(StickGraphState sg, float lapStart, float lapDuration)
        {
            return sg.traceCachePilot != sg.pilot || sg.traceCacheChannel != sg.channel ||
                sg.traceCacheHeatmap != sg.heatmap || sg.traceCacheInputs != sg.pilot.originalInputs ||
                sg.traceCacheLapStart != lapStart || sg.traceCacheLapDuration != lapDuration ||
                (sg.heatmap && sg.traceCacheHeatRange != GhostLinesPlugin.speedHeatmapRangeRevision);
        }

        private bool IsStickGraphOpenFor(GhostLineEntry pilot)
        {
            for (int i = 0; i < stickGraphs.Count; i++)
            {
                if (stickGraphs[i].pilot == pilot)
                    return true;
            }
            return false;
        }

        private void ToggleStickGraphFor(GhostLineEntry pilot)
        {
            bool closed = false;
            for (int i = stickGraphs.Count - 1; i >= 0; i--)
            {
                StickGraphState g = stickGraphs[i];
                if (g.pilot != pilot)
                    continue;
                // Also drop it from any PiP it is docked in.
                for (int w = 0; w < pipWindows.Count; w++)
                {
                    if (pipWindows[w] != null)
                        pipWindows[w].docked.RemoveAll(d => d.stickGraph == g);
                }
                if (g.traceTex != null)
                {
                    Destroy(g.traceTex);
                    g.traceTex = null;
                }
                stickGraphs.RemoveAt(i);
                closed = true;
            }
            if (!closed)
                OpenStickGraphFor(pilot);
        }

        private void BuildLapTraceTexture(GhostLineEntry pilot, int channel, int tw, int th, float lapStart, float lapDuration,
            ref Texture2D tex, ref Color32[] pixels)
        {
            Color32 signal = PilotTraceColor(pilot);
            TraceTexture.Build(ref tex, ref pixels, tw, th, 0f,
                (float f, out Color32 color) =>
                {
                    color = signal;
                    return SampleStickChannel(pilot, lapStart + f * lapDuration, channel);
                });
        }

        private Color GetLiveGraphSignalColor(LiveGraphState g)
        {
            if (g.mpTarget != null || g.mpColor.a > 0f)
                return g.mpColor.a > 0f ? g.mpColor : new Color(0.25f, 0.62f, 0.95f);
            if (g.pilot == null)
                return new Color(0.25f, 0.62f, 0.95f);
            return PilotTraceColor(g.pilot);
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

        private static Rect ClampGraphRect(Rect rect)
        {
            rect.x = Mathf.Clamp(rect.x, 12f, Mathf.Max(12f, Screen.width - rect.width - 12f));
            rect.y = Mathf.Clamp(rect.y, 12f, Mathf.Max(12f, Screen.height - rect.height - 12f));
            return rect;
        }

        // Pilot dropdown rows (20px each, below the button): every pilot, limited
        // only by the room left on screen.
        private static int PilotDropdownRows(Rect pilotBtn, int count)
        {
            int fit = Mathf.FloorToInt((Screen.height - (pilotBtn.yMax + 2f)) / 20f);
            return Mathf.Min(count, Mathf.Max(1, fit));
        }

        private static Rect GetStickGraphOptionRect(Rect ddBtn, int index)
        {
            return new Rect(ddBtn.x, ddBtn.yMax + 2f + 20f * index, ddBtn.width, 20f);
        }

        private float SampleStickChannel(GhostLineEntry entry, float stateTime, int channel)
        {
            if (entry.stateTimes.Length == 0 || entry.originalInputs.Length == 0)
                return 0f;
            return InputSampler.Channel(
                InputSampler.SampleTimeline(entry.stateTimes, entry.originalInputs, stateTime), channel);
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
                if (IsStickGraphDocked(g))
                    continue;
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
                if (!covered && IsUiBlockedFor(UI_LAYER_STICKGRAPH, e.mousePosition))
                    covered = true;

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
                Rect close = new Rect(panel.xMax - 36f, panel.y + 4f, 28f, 22f);
                Rect pin = new Rect(panel.xMax - 64f, panel.y + 5f, 22f, 20f);
                float ddW = 96f;
                Rect heatBtn = new Rect(panel.xMax - 92f, panel.y + 6f, 22f, 18f);
                Rect plusBtn = new Rect(heatBtn.x - 28f, panel.y + 6f, 22f, 18f);
                Rect ddBtn = new Rect(plusBtn.x - ddW - 6f, panel.y + 6f, ddW, 18f);
                Rect pilotBtn = new Rect(panel.x + 12f, panel.y + 6f,
                    Mathf.Max(20f, ddBtn.x - 6f - (panel.x + 12f)), 18f);
                int pilotOptCount = PilotDropdownRows(pilotBtn, stickGraphPilotOptions.Count);

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

                g.edgeHover = inTitleControls || g.channelDropdown || g.pilotDropdown
                    ? 0
                    : ComputeWindowResizeEdge(panel, e.mousePosition, false);
                DrawWindowResizeBars(panel, g.resizing ? g.resizeEdge : g.edgeHover, alpha);

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
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                    e.Use();
                }
                if (g.resizing && e.type == EventType.MouseDrag && e.button == 0)
                {
                    g.rect = ApplyWindowResizeDrag(g.resizeEdge, g.resizeStartMouse, g.resizeStartRect,
                        e.mousePosition, STICK_GRAPH_MIN_WIDTH, STICK_GRAPH_MIN_HEIGHT);
                    EnsureStickGraphRect(g, false);
                    e.Use();
                }
                if (g.resizing && e.type == EventType.MouseUp && e.button == 0)
                {
                    g.resizing = false;
                    g.resizeEdge = 0;
                    g.traceCachePlotW = -1;
                    g.traceCachePlotH = -1;
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
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
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
                    PiPWindow sgDrop = FindReplayPipWindowForDock(e.mousePosition);
                    if (sgDrop != null)
                    {
                        int sgSlot = GetPipDockSlotAt(sgDrop.rect, e.mousePosition);
                        if (sgSlot >= 0)
                            DockStickGraphInto(sgDrop.docked, g, sgSlot);
                    }
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
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
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
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                    settingsControlPressed = true;
                    e.Use();
                }

                if (DrawStyledToggleButton(pin, iconPin, g.pinned, alpha, !covered, UI_LAYER_STICKGRAPH))
                {
                    g.pinned = !g.pinned;
                    stickGraphActivated = g;
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
                }
                GUI.color = ThemeCloseLabel(alpha);
                GUI.Label(new Rect(close.x, close.y - 3f, close.width, close.height),
                    "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);
                if (!covered && e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
                {
                    uiFrontLayer = UI_LAYER_STICKGRAPH;
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

                int tw = Mathf.Max(1, (int)plot.width);
                int th = Mathf.Max(1, (int)plot.height);
                bool traceContentChanged = IsStickGraphTraceStale(g, lapStart, lapDuration);
                bool traceSizeChanged = g.traceCachePlotW != tw || g.traceCachePlotH != th;
                bool traceStale = Time.unscaledTime - g.traceLastBuildTime > 0.06f;
                if (traceContentChanged || (traceSizeChanged && traceStale))
                    BuildStickGraphTraceTexture(g, tw, th, lapStart, lapDuration);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                Rect traceDraw = new Rect(Mathf.Round(plot.x), Mathf.Round(plot.y), tw, th);
                GUI.DrawTexture(traceDraw, g.traceTex);

                if (trimEnabled)
                {
                    float trimQueryStart = GetGateQueryTimestamp(g.pilot, trimStart);
                    float trimQueryEnd = GetGateQueryTimestamp(g.pilot, trimEnd);
                    float trimX1 = plot.x + (Mathf.Max(trimQueryStart, lapStart) - lapStart) / lapDuration * plot.width;
                    float trimX2 = plot.x + (Mathf.Min(trimQueryEnd, lapEnd) - lapStart) / lapDuration * plot.width;
                    if (trimX2 - trimX1 > 0.5f)
                    {
                        GUI.color = darkMode
                            ? new Color(1f, 0.6f, 0f, 0.18f * alpha)
                            : new Color(1f, 0.45f, 0f, 0.30f * alpha);
                        GUI.DrawTexture(new Rect(trimX1, plot.y, trimX2 - trimX1, plot.height), whiteTex);
                    }
                }

                float replayTime;
                if (TryGetFrameReplayTime(out replayTime))
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
                GUI.Label(new Rect(timeLblRow.xMax - 60f, timeLblRow.y, 60f, timeLblRow.height),
                    lapDuration.ToString("0.0") + "s", settingsSmallStyle);
                // Share of the lap the channel spent at full deflection. The lap
                // trace doesn't change while the lap/channel stay the same, so
                // sample it once instead of on every OnGUI event.
                if (g.fullStatPilot != g.pilot || g.fullStatTimes != times || g.fullStatChannel != g.channel ||
                    g.fullStatStart != lapStart || g.fullStatEnd != lapEnd)
                {
                    int fullCount = 0;
                    const int statSamples = 400;
                    for (int i = 0; i < statSamples; i++)
                    {
                        float sf = (i + 1) / (float)statSamples;
                        float sv = SampleStickChannel(g.pilot, lapStart + sf * (lapEnd - lapStart), g.channel);
                        if (Mathf.Abs(sv) >= 0.999f)
                            fullCount++;
                    }
                    g.fullStatPilot = g.pilot;
                    g.fullStatTimes = times;
                    g.fullStatChannel = g.channel;
                    g.fullStatStart = lapStart;
                    g.fullStatEnd = lapEnd;
                    g.fullStatLabel = "100%: " + (fullCount * 100f / statSamples).ToString("0") + "%";
                }
                GUI.color = ThemeLabel(alpha);
                GUI.Label(new Rect(timeLblRow.x + timeLblRow.width * 0.5f - 60f, timeLblRow.y, 120f, timeLblRow.height),
                    g.fullStatLabel, settingsSmallCenterStyle);

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

        private GhostLineEntry lastDeltaRefEntry;
    }
}
