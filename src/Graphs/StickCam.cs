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
            if (stickCloseTex == null)
                stickCloseTex = BuildStickCloseTexture((int)STICKCAM_CLOSE_GLYPH);
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

        private const float STICKCAM_CLOSE_GLYPH = 12f;
        private const float STICKCAM_CLOSE_HIT_PAD = 4f;

        // The cross sits the same distance from the right panel's top and right
        // borders, and that distance puts its right edge on the resize corner's
        // left edge (PIP_RESIZE_GRAB): overlapping the corner turned a corner
        // grab into a close click.
        private static Rect GetStickCamCloseGlyphRect(Rect totalRect)
        {
            // The panel sits 5px inside totalRect, and its border is 2px thick.
            float innerRight = totalRect.xMax - 7f;
            float innerTop = totalRect.y + 7f;
            float gap = PIP_RESIZE_GRAB - 7f;
            return new Rect(
                Mathf.Round(innerRight - gap - STICKCAM_CLOSE_GLYPH),
                Mathf.Round(innerTop + gap),
                STICKCAM_CLOSE_GLYPH,
                STICKCAM_CLOSE_GLYPH);
        }

        // Padded for an easier click, except on the right, which would reach
        // into the resize corner.
        private static Rect GetStickCamCloseRect(Rect totalRect)
        {
            Rect glyph = GetStickCamCloseGlyphRect(totalRect);
            float pad = STICKCAM_CLOSE_HIT_PAD;
            return new Rect(glyph.x - pad, glyph.y - pad, glyph.width + pad, glyph.height + pad * 2f);
        }

        private void DrawStickCamCloseGlyph(Rect totalRect)
        {
            Color old = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(GetStickCamCloseGlyphRect(totalRect), stickCloseTex);
            GUI.color = old;
        }

        // Anti-aliased cross drawn 1:1 on screen, so it stays centered where a
        // font "x" sat low in its line box.
        private static Texture2D BuildStickCloseTexture(int s)
        {
            Texture2D tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            const float inset = 1.5f;
            const float halfStroke = 1.1f;
            float lo = inset;
            float hi = s - inset;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float d = Mathf.Min(
                        DistanceToSegment(p, new Vector2(lo, lo), new Vector2(hi, hi)),
                        DistanceToSegment(p, new Vector2(lo, hi), new Vector2(hi, lo)));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(halfStroke + 0.5f - d)));
                }
            }
            tex.Apply();
            return tex;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        private void ComputeStickCamOverlayRect()
        {
            float panelSize = Mathf.Clamp(Screen.height * 0.11f, 68f, 96f) * stickCamSize;
            float gap = 14f * stickCamSize;
            float groupW = panelSize * 2f + gap;
            float groupH = panelSize;
            float cx = stickCamX < 0f ? Screen.width * 0.5f : stickCamX;
            float cy = stickCamY < 0f ? Screen.height - 56f - panelSize * 0.5f - 10f : stickCamY;
            cx = Mathf.Clamp(cx, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
            cy = Mathf.Clamp(cy, groupH * 0.5f + 2f, Screen.height - groupH * 0.5f - 2f);
            currentStickCamOverlayRect = new Rect(cx - groupW * 0.5f - 5f, cy - groupH * 0.5f - 5f, groupW + 10f, groupH + 10f);
            currentStickCamOverlayRectValid = true;
        }

        private void ComputePlaybackOverlayRect()
        {
            ComputePlaybackBar(out float barX, out float barY, out float barW, out float barH, out Rect _, out float _, out float _, out float _);
            float s = playbackUiSize;
            currentPlaybackOverlayRect = new Rect(barX - 5f * s, barY - 5f * s, barW + 10f * s, barH + 10f * s);
            currentPlaybackOverlayRectValid = true;
        }

        private void ComputePlaybackBar(out float barX, out float barY, out float barW, out float barH, out Rect sliderRect,
            out float labelW, out float labelInset, out float row1Y)
        {
            float s = playbackUiSize;
            barH = 56f * s;
            barW = (Screen.width / 3f) * s;
            float cx = scrubberX < 0 ? Screen.width * 0.5f : scrubberX;
            float cy = scrubberY < 0 ? Screen.height - barH * 0.5f : scrubberY;
            cx = Mathf.Clamp(cx, barW * 0.5f, Screen.width - barW * 0.5f);
            cy = Mathf.Clamp(cy, barH * 0.5f + 2f, Screen.height - barH * 0.5f);
            barX = cx - barW * 0.5f;
            barY = cy - barH * 0.5f;
            labelW = Mathf.Max(55f * s, 54f);
            labelInset = Mathf.Max(6f * s, 6f);
            row1Y = barY + Mathf.Max(2f * s, 2f);
            sliderRect = new Rect(
                barX + labelW + labelInset,
                row1Y,
                barW - labelW * 2f - labelInset * 2f,
                Mathf.Max(18f * s, 18f));
        }

        private class StickCamInstance
        {
            public GhostLineEntry entry;
            public MultiplayerPipTarget mpTarget;
            public float x;
            public float y;
            public bool dragging;
            public Vector2 dragStartMouse;
            public Vector2 dragStartPos;
            public Vector4 lastInput;
            public bool hasLastInput;
            // Each stick cam sizes independently; new ones start at the saved size.
            public float size = 1f;
        }

        private const float STICK_CAM_MIN_SIZE = 0.5f;
        private const float STICK_CAM_MAX_SIZE = 3f;

        private void DrawStickCam()
        {
            if (!stickCamEnabled)
                return;

            if (replayReady && !ReferenceEquals(spectatedEntry, lastStickCamSpectateEntry))
            {
                GhostLineEntry prevSpectated = lastStickCamSpectateEntry;
                lastStickCamSpectateEntry = spectatedEntry;
                if (spectatedEntry != null && prevSpectated != null && stickCamInstances.Count > 0)
                {
                    int prevIdx = -1;
                    for (int i = 0; i < stickCamInstances.Count; i++)
                    {
                        if (stickCamInstances[i].entry == prevSpectated)
                        {
                            prevIdx = i;
                            break;
                        }
                    }
                    if (prevIdx >= 0)
                    {
                        bool newHasInstance = false;
                        for (int i = 0; i < stickCamInstances.Count; i++)
                        {
                            if (stickCamInstances[i].entry == spectatedEntry)
                            {
                                newHasInstance = true;
                                break;
                            }
                        }
                        if (!newHasInstance)
                            stickCamInstances[prevIdx].entry = spectatedEntry;
                    }
                }
            }

            if (stickCamInstances.Count > 0)
            {
                DrawStickCamInstances();
                return;
            }

            if (dockedStickPip != null)
            {
                if (dockedStickPip.entry == null || !pipWindows.Contains(dockedStickPip))
                {
                    dockedStickPip = null;
                    dockedStickSlot = -1;
                }
                else
                {
                    return;
                }
            }

            Vector4 input;
            GhostLineEntry camEntry = null;
            if (replayReady)
            {
                camEntry = spectatedEntry;
                if (camEntry == null)
                    return;
                float replayTime;
                if (!TryGetFrameReplayTime(out replayTime))
                    return;
                if (!TryGetDroneInput(camEntry, replayTime, out input))
                    return;
            }
            else
            {
                return;
            }

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

            EnsureSettingsStyles();
            Rect scCloseRect = GetStickCamCloseRect(totalRect);
            currentStickCamInstance = null;
            currentStickCamCloseRect = scCloseRect;
            currentStickCamCloseRectValid = true;

            var ev = Event.current;
            Vector2 mp = ev.mousePosition;
            if (ev.type == EventType.MouseDown && ev.button == 0 && !stickCamResizing && totalRect.Contains(mp) &&
                scCloseRect.Contains(mp) && !IsUiBlockedFor(UI_LAYER_STICKCAM, mp))
            {
                uiFrontLayer = UI_LAYER_STICKCAM;
                stickCamEnabled = false;
                dockedStickPip = null;
                dockedStickSlot = -1;
                currentStickCamOverlayRectValid = false;
                currentStickCamCloseRectValid = false;
                SaveDisplayPreferences();
                ev.Use();
                return;
            }
            if (ev.type == EventType.MouseDown && ev.button == 0 && totalRect.Contains(mp) && !IsUiBlockedFor(UI_LAYER_STICKCAM, mp))
            {
                uiFrontLayer = UI_LAYER_STICKCAM;
                if (!stickCamResizing && stickCamEdgeHover == 0)
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
                    PiPWindow stickDrop = FindReplayPipWindowForDock(ev.mousePosition);
                    if (stickDrop != null)
                    {
                        int stickSlot = GetPipDockSlotAt(stickDrop.rect, ev.mousePosition);
                        if (stickSlot >= 0)
                        {
                            dockedStickPip = stickDrop;
                            dockedStickSlot = stickSlot;
                            ev.Use();
                            return;
                        }
                    }
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
            else if (stickCamResizing && ev.type == EventType.MouseUp && ev.button == 0)
            {
                stickCamResizing = false;
                stickCamResizeEdge = 0;
                SaveCurrentOverlayPositions();
                SaveDisplayPreferences();
                ev.Use();
            }

            DrawStickCamCornerHighlights(totalRect, stickCamResizing ? stickCamResizeEdge : stickCamEdgeHover);

            EnsureStickTextures();
            DrawStickPanel(leftPanel, leftStick);
            DrawStickPanel(rightPanel, rightStick);
            if (totalRect.Contains(mp))
                DrawStickCamCloseGlyph(totalRect);
        }

        private void DrawStickCamInstances()
        {
            EnsureStickTextures();
            EnsureSettingsStyles();
            if (whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1);
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
            }
            float replayTime = 0f;
            bool hasReplayTime = replayReady && TryGetFrameReplayTime(out replayTime);
            var ev = Event.current;
            Vector2 mp = ev.mousePosition;
            StickCamInstance mouseInstance = null;
            Rect mouseRect = Rect.zero;
            Rect hoveredCloseRect = Rect.zero;
            Rect lastTotal = Rect.zero;
            bool anyDrawn = false;

            for (int i = 0; i < stickCamInstances.Count; i++)
            {
                StickCamInstance inst = stickCamInstances[i];
                if (IsStickCamDocked(inst))
                    continue;
                Vector4 input = Vector4.zero;
                bool hasInput = false;
                if (replayReady)
                {
                    if (inst.entry != null && hasReplayTime)
                        hasInput = TryGetDroneInput(inst.entry, replayTime, out input);
                }
                else
                {
                    if (inst.mpTarget != null && inst.mpTarget.root != null)
                    {
                        if (inst.mpTarget.root != inst.mpTarget.syncRoot || inst.mpTarget.photonView != inst.mpTarget.syncView)
                        {
                            inst.mpTarget.syncComponent = null;
                            inst.mpTarget.lastSyncAttempt = 0f;
                        }
                        if (inst.mpTarget.syncComponent == null)
                            EnsureMpTargetSyncAccess(inst.mpTarget);
                        else
                            hasInput = TryGetMpInputCached(inst.mpTarget, out input);
                    }
                }
                if (!hasInput)
                {
                    if (!inst.hasLastInput)
                        continue;
                    input = inst.lastInput;
                }
                else
                {
                    inst.lastInput = input;
                    inst.hasLastInput = true;
                }

                float panelSize = Mathf.Clamp(Screen.height * 0.11f, 68f, 96f) * inst.size;
                float gap = 14f * inst.size;
                float groupW = panelSize * 2f + gap;
                float groupH = panelSize;
                inst.x = Mathf.Clamp(inst.x, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
                inst.y = Mathf.Clamp(inst.y, groupH * 0.5f + 2f, Screen.height - groupH * 0.5f - 2f);
                Rect leftPanel = new Rect(inst.x - groupW * 0.5f, inst.y - groupH * 0.5f, panelSize, panelSize);
                Rect rightPanel = new Rect(leftPanel.xMax + gap, leftPanel.y, panelSize, panelSize);
                Rect totalRect = new Rect(inst.x - groupW * 0.5f - 5f, inst.y - groupH * 0.5f - 5f, groupW + 10f, groupH + 10f);
                lastTotal = totalRect;
                anyDrawn = true;
                bool hovered = totalRect.Contains(mp);
                Rect scCloseRect = GetStickCamCloseRect(totalRect);
                if (ev.type == EventType.MouseDown && ev.button == 0 && hovered && !stickCamResizing &&
                    scCloseRect.Contains(mp) && !IsUiBlockedFor(UI_LAYER_STICKCAM, mp))
                {
                    uiFrontLayer = UI_LAYER_STICKCAM;
                    if (stickCamResizeTarget == inst)
                    {
                        stickCamResizeTarget = null;
                        stickCamResizing = false;
                    }
                    stickCamInstances.RemoveAt(i);
                    ev.Use();
                    i--;
                    continue;
                }
                if (hovered)
                {
                    mouseInstance = inst;
                    mouseRect = totalRect;
                    hoveredCloseRect = scCloseRect;
                }

                if (ev.type == EventType.MouseDown && ev.button == 0 && hovered && !IsUiBlockedFor(UI_LAYER_STICKCAM, mp))
                {
                    uiFrontLayer = UI_LAYER_STICKCAM;
                    if (!stickCamResizing && stickCamEdgeHover == 0)
                    {
                        lastStickCamClickTime = Time.realtimeSinceStartup;
                        lastStickCamClickPos = mp;
                        inst.dragging = true;
                        inst.dragStartMouse = mp;
                        inst.dragStartPos = new Vector2(inst.x, inst.y);
                    }
                    ev.Use();
                }
                if (inst.dragging)
                {
                    if (ev.type == EventType.MouseUp && ev.button == 0)
                    {
                        inst.dragging = false;
                        MultiplayerPipWindow dropTarget = FindMpPipWindowForDock(ev.mousePosition);
                        if (dropTarget != null)
                        {
                            int dropSlot = GetPipDockSlotAt(dropTarget.rect, ev.mousePosition);
                            if (dropSlot >= 0)
                                DockStickCamInto(dropTarget.docked, inst, dropSlot);
                        }
                        PiPWindow replayDrop = FindReplayPipWindowForDock(ev.mousePosition);
                        if (replayDrop != null)
                        {
                            int replaySlot = GetPipDockSlotAt(replayDrop.rect, ev.mousePosition);
                            if (replaySlot >= 0)
                                DockStickCamInto(replayDrop.docked, inst, replaySlot);
                        }
                        ev.Use();
                    }
                    else if (ev.type == EventType.MouseDrag && ev.button == 0)
                    {
                        Vector2 offset = mp - inst.dragStartMouse;
                        float draggedX = SnapOverlayToHorizontalCenter(inst.dragStartPos.x + offset.x);
                        inst.x = Mathf.Clamp(draggedX, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
                        inst.y = Mathf.Clamp(inst.dragStartPos.y + offset.y, groupH * 0.5f + 2f, Screen.height - groupH * 0.5f - 2f);
                        ev.Use();
                    }
                }

                int shownEdge = stickCamResizing && stickCamResizeTarget == inst
                    ? stickCamResizeEdge
                    : hovered ? stickCamEdgeHover : 0;
                DrawStickCamCornerHighlights(totalRect, shownEdge);
                DrawStickPanel(leftPanel, new Vector2(input.y, input.x * 2f - 1f));
                DrawStickPanel(rightPanel, new Vector2(-input.w, input.z));
                if (hovered)
                    DrawStickCamCloseGlyph(totalRect);
            }

            currentStickCamInstance = mouseInstance;
            currentStickCamCloseRect = hoveredCloseRect;
            currentStickCamCloseRectValid = mouseInstance != null;
            if (anyDrawn)
            {
                currentStickCamOverlayRect = mouseInstance != null ? mouseRect : lastTotal;
                currentStickCamOverlayRectValid = true;
            }
            else
            {
                currentStickCamOverlayRectValid = false;
            }
        }

        private void GetStickCamSpawnPos(int index, out float spawnX, out float spawnY)
        {
            float panelSize = Mathf.Clamp(Screen.height * 0.11f, 68f, 96f) * stickCamSize;
            float gap = 14f * stickCamSize;
            float groupW = panelSize * 2f + gap;
            float groupH = panelSize;
            float bx = stickCamX < 0f ? Screen.width * 0.5f : stickCamX;
            float by = stickCamY < 0f ? Screen.height - 56f - panelSize * 0.5f - 10f : stickCamY;
            spawnX = Mathf.Clamp(bx + index * STICK_GRAPH_OPEN_OFFSET, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
            spawnY = Mathf.Clamp(by + index * STICK_GRAPH_OPEN_OFFSET, groupH * 0.5f + 2f, Screen.height - groupH * 0.5f - 2f);
        }

        private void AddStickCamInstance(GhostLineEntry entry, MultiplayerPipTarget mpTarget)
        {
            GetStickCamSpawnPos(stickCamInstances.Count, out float spawnX, out float spawnY);
            stickCamInstances.Add(new StickCamInstance
            {
                entry = entry,
                mpTarget = mpTarget,
                x = spawnX,
                y = spawnY,
                size = stickCamSize
            });
        }

        private void RemoveStickCamInstance(GhostLineEntry entry, MultiplayerPipTarget mpTarget)
        {
            for (int i = stickCamInstances.Count - 1; i >= 0; i--)
            {
                bool match = entry != null
                    ? stickCamInstances[i].entry == entry
                    : stickCamInstances[i].mpTarget == mpTarget;
                if (match)
                {
                    if (stickCamResizeTarget == stickCamInstances[i])
                    {
                        stickCamResizeTarget = null;
                        stickCamResizing = false;
                    }
                    if (currentStickCamInstance == stickCamInstances[i])
                        currentStickCamInstance = null;
                    stickCamInstances.RemoveAt(i);
                    return;
                }
            }
        }

        private void ToggleStickCamFor(GhostLineEntry entry)
        {
            for (int i = 0; i < stickCamInstances.Count; i++)
            {
                if (stickCamInstances[i].entry == entry)
                {
                    RemoveStickCamInstance(entry, null);
                    return;
                }
            }
            AddStickCamInstance(entry, null);
        }

        private void ToggleStickCamFor(MultiplayerPipTarget target)
        {
            for (int i = 0; i < stickCamInstances.Count; i++)
            {
                if (stickCamInstances[i].mpTarget == target)
                {
                    RemoveStickCamInstance(null, target);
                    return;
                }
            }
            AddStickCamInstance(null, target);
        }
    }
}
