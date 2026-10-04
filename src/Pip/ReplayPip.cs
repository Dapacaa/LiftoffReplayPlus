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
        private void SetPipEnabled(bool enabled)
        {
            if (pipEnabled == enabled)
                return;
            pipEnabled = enabled;
            if (pipEnabledConfig != null)
            {
                pipEnabledConfig.Value = enabled;
                Prefs.RequestSave();
            }
            if (!pipEnabled)
                CloseAllPiPWindows();
        }

        private int GetEffectivePipRefreshHz(int windowCount)
        {
            if (pipRefreshAuto)
            {
                if (windowCount >= 3)
                    return 15;
                return 30;
            }
            return pipRefreshHz > 0 ? pipRefreshHz : 30;
        }

        private void SetPipRefreshHz(int hz)
        {
            pipRefreshHz = hz;
            pipRefreshAuto = false;
            if (pipRefreshHzConfig != null)
            {
                pipRefreshHzConfig.Value = hz;
                Prefs.RequestSave();
            }
            if (pipRefreshAutoConfig != null)
            {
                pipRefreshAutoConfig.Value = false;
                Prefs.RequestSave();
            }
        }

        private void SetPipRefreshAuto(bool enabled)
        {
            pipRefreshAuto = enabled;
            if (pipRefreshAutoConfig != null)
            {
                pipRefreshAutoConfig.Value = enabled;
                Prefs.RequestSave();
            }
        }

        internal void MarkPiPWindowsDirty()
        {
            for (int i = 0; i < pipWindows.Count; i++)
                pipWindows[i].rtDirty = true;
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
                multiplayerPipWindows[i].nextRenderAt = 0f;
        }

        private void SetPipShowNamesDelta(bool enabled)
        {
            pipShowNamesDelta = enabled;
            MarkPiPWindowsDirty();
            if (pipShowNamesDeltaConfig != null)
            {
                pipShowNamesDeltaConfig.Value = enabled;
                Prefs.RequestSave();
            }
        }

        private void ApplyPipFarClipFromSliderX(float mouseX, Rect track)
        {
            float t = Mathf.Clamp01((mouseX - track.x) / track.width);
            float meters = Mathf.Clamp(Mathf.Round((25f + t * 475f) / 5f) * 5f, 25f, 500f);
            if (pipFarClip == meters)
                return;
            pipFarClip = meters;
            for (int i = 0; i < pipWindows.Count; i++)
            {
                if (pipWindows[i].cam != null)
                    pipWindows[i].cam.farClipPlane = meters;
                pipWindows[i].rtDirty = true;
            }
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                if (multiplayerPipWindows[i].cam != null)
                    multiplayerPipWindows[i].cam.farClipPlane = meters;
                multiplayerPipWindows[i].nextRenderAt = 0f;
            }
        }

        private void SavePipFarClip()
        {
            if (pipFarClipConfig != null)
            {
                pipFarClipConfig.Value = pipFarClip;
                Prefs.RequestSave();
            }
        }

        private void SetPipResolutionScale(float scale)
        {
            pipResolutionScale = scale;
            if (pipResolutionScaleConfig != null)
            {
                pipResolutionScaleConfig.Value = scale;
                Prefs.RequestSave();
            }
            for (int i = 0; i < pipWindows.Count; i++)
                pipWindows[i].rtDirty = true;
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
                multiplayerPipWindows[i].nextRenderAt = 0f;
        }

        private void TogglePiPWindow(GhostLineEntry entry)
        {
            for (int i = 0; i < pipWindows.Count; i++)
            {
                if (pipWindows[i].entry == entry)
                {
                    ClosePiPWindow(pipWindows[i]);
                    return;
                }
            }
            OpenPiPWindow(entry);
        }

        private void OpenPiPWindow(GhostLineEntry entry)
        {
            if (!pipEnabled)
                SetPipEnabled(true);
            int cascade = pipWindows.Count % 5;
            OpenPiPWindow(entry, new Rect(
                Screen.width * 0.5f - 160f + cascade * PIP_OPEN_OFFSET,
                Screen.height * 0.5f - 100f + cascade * PIP_OPEN_OFFSET,
                320f, 200f));
        }

        private PiPWindow OpenPiPWindow(GhostLineEntry entry, Rect rect)
        {
            if (entry == null)
                return null;
            for (int i = 0; i < pipWindows.Count; i++)
            {
                if (pipWindows[i].entry == entry)
                    return null;
            }
            PiPWindow w = new PiPWindow();
            w.entry = entry;
            w.rect = rect;
            w.fadeAmount = 1f;
            ClampPiPWindowRect(w);
            SyncPiPCameraSettings();
            CreatePiPCamera(w);
            EnsurePiPRenderTexture(w);
            RenderPiPWindow(w);
            float pipInterval = 1f / GetEffectivePipRefreshHz(pipWindows.Count + 1);
            w.nextRenderAt = Time.unscaledTime + pipInterval + pipInterval * (float)(pipWindows.Count % 4) / 4f;
            if (pipExternalMonitor)
                pipWindows.Insert(0, w);
            else
                pipWindows.Add(w);
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] PIP window opened for " + GetPilotLabel(entry, 0));
            return w;
        }

        private void SyncPiPCameraSettings()
        {
            Camera main = Camera.main;
            if (main == null)
                return;
            if (holdZoomFovCaptured)
                pipFov = holdZoomNormalFov;
            else
                pipFov = main.fieldOfView;
            pipNearClip = main.nearClipPlane;
        }

        private void CreatePiPCamera(PiPWindow w)
        {
            if (w.camObject != null)
                return;
            w.camObject = new GameObject("GhostLinesPiPCamera");
            w.camObject.transform.position = Vector3.zero;
            w.cam = w.camObject.AddComponent<Camera>();
            w.cam.enabled = false;
            w.cam.targetTexture = null;
            Camera main = Camera.main;
            w.cam.clearFlags = main != null ? main.clearFlags : CameraClearFlags.SolidColor;
            w.cam.backgroundColor = main != null ? main.backgroundColor : new Color(0.02f, 0.02f, 0.03f);
            w.cam.fieldOfView = pipFov;
            w.cam.nearClipPlane = pipNearClip;
            w.cam.farClipPlane = pipFarClip;
            w.cam.cullingMask = GetPiPCullingMask();
            w.cam.renderingPath = RenderingPath.Forward;
            w.cam.allowHDR = false;
            w.cam.allowMSAA = false;
            w.cam.depth = -1000f;
        }

        private int GetPiPCullingMask()
        {
            Camera main = Camera.main;
            int mask = main != null ? main.cullingMask : -1;
            mask &= ~(1 << 5);
            if (!pipCullingMaskLogged)
            {
                pipCullingMaskLogged = true;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] PIP culling mask 0x" + mask.ToString("X8") + " (UI layer excluded)");
            }
            return mask;
        }

        private void EnsurePiPRenderTexture(PiPWindow w)
        {
            Rect content = GetPiPContentRect(w.rect);
            float targetW = content.width * pipResolutionScale;
            float targetH = content.height * pipResolutionScale;
            if (pipExternalMonitor && w.nativePip != null && w.nativePip.IsOpen)
            {
                RECT cl;
                if (GetClientRect(w.nativePip.hwnd, out cl) && cl.right > 0 && cl.bottom > 0)
                {
                    targetW = cl.right * pipResolutionScale;
                    targetH = cl.bottom * pipResolutionScale;
                }
            }
            float fit = Mathf.Min(1f, Mathf.Min(PIP_RT_MAX_WIDTH / targetW, PIP_RT_MAX_HEIGHT / targetH));
            int newW = Mathf.Clamp(Mathf.RoundToInt(targetW * fit), 48, (int)PIP_RT_MAX_WIDTH);
            int newH = Mathf.Clamp(Mathf.RoundToInt(targetH * fit), 48, (int)PIP_RT_MAX_HEIGHT);
            if (w.rt != null && Mathf.Abs(w.rtW - newW) <= PIP_RT_RECREATE_TOLERANCE && Mathf.Abs(w.rtH - newH) <= PIP_RT_RECREATE_TOLERANCE)
                return;
            if (w.rt != null)
            {
                w.rt.Release();
                Destroy(w.rt);
                w.rt = null;
            }
            w.rtW = newW;
            w.rtH = newH;
            w.rt = new RenderTexture(newW, newH, 16, RenderTextureFormat.Default);
            w.rt.name = "GhostLinesPiPRT";
            if (w.cam != null)
                w.cam.targetTexture = w.rt;
        }

        private void ClosePiPWindow(PiPWindow w)
        {
            if (w.nativePip != null)
            {
                CloseNativePipWindow(w.nativePip);
                w.nativePip = null;
            }
            if (w.camObject != null)
            {
                Destroy(w.camObject);
                w.camObject = null;
            }
            w.cam = null;
            if (w.rt != null)
            {
                w.rt.Release();
                Destroy(w.rt);
                w.rt = null;
            }
            pipWindows.Remove(w);
        }

        private void CloseAllPiPWindows()
        {
            for (int i = pipWindows.Count - 1; i >= 0; i--)
                ClosePiPWindow(pipWindows[i]);
        }

        private void TickPiPWindows()
        {
            float now = Time.unscaledTime;
            float interval = 1f / GetEffectivePipRefreshHz(pipWindows.Count);
            if (pipStepTargetTime >= 0f)
            {
                float applied;
                bool settled = TryGetReplayTime(out applied) && Mathf.Abs(applied - pipStepTargetTime) <= 0.002f;
                if (settled || Time.frameCount >= pipForceRenderFrame)
                {
                    pipStepTargetTime = -1f;
                    pipForceRenderFrame = -1;
                }
            }
            bool pausedUi = !isPlaying && !draggingScrubber && !isDraggingHandle && !draggingTrimStart && !draggingTrimEnd;
            if (pausedUi && pipStepTargetTime < 0f)
            {
                if (pinnedPiPReplayTime < 0f)
                {
                    float pt;
                    if (TryGetReplayTime(out pt))
                        pinnedPiPReplayTime = pt;
                }
            }
            else
                pinnedPiPReplayTime = -1f;
            for (int i = pipWindows.Count - 1; i >= 0; i--)
            {
                PiPWindow w = pipWindows[i];
                if (w.entry == null || !GhostLinesPlugin.entries.Contains(w.entry))
                {
                    ClosePiPWindow(w);
                    continue;
                }
                if (w.rtDirty && pipForceRenderFrame < 0)
                {
                    EnsurePiPRenderTexture(w);
                    w.rtDirty = false;
                    RenderPiPWindow(w);
                    w.nextRenderAt = now + interval;
                }
                if (!isPlaying && !draggingScrubber && !isDraggingHandle && !draggingTrimStart && !draggingTrimEnd && w.renderedOnce)
                    continue;
                if (now < w.nextRenderAt)
                    continue;
                EnsurePiPRenderTexture(w);
                RenderPiPWindow(w);
                w.nextRenderAt = now + interval;
            }
        }

        private bool IsPlaybackTrimHit(Vector2 mouse)
        {
            if (!trimEnabled || !currentPlaybackOverlayRectValid)
                return false;
            float s = playbackUiSize;
            ComputePlaybackBar(out float barX, out float barY, out float barW, out float barH, out Rect sliderRect, out float _, out float _, out float _);
            float min = 0f;
            float max = 1f;
            if (replayController != null)
            {
                max = replayController.MaximumTimelineTime;
            }
            else if (sliderComponent != null)
            {
                min = sliderComponent.minValue;
                max = sliderComponent.maxValue;
            }
            max = GetEffectiveReplayEnd(max);
            float thandleW = 12f * s;
            float ttX = sliderRect.x + thandleW * 0.5f;
            float ttW = sliderRect.width - thandleW;
            float trange = Mathf.Max(max - min, 0.0001f);
            float tsN = Mathf.Clamp01((trimStart - min) / trange);
            float teN = Mathf.Clamp01((trimEnd - min) / trange);
            float tsX = ttX + tsN * ttW;
            float teX = ttX + teN * ttW;
            float poleH = 20f * s;
            float poleTop = sliderRect.y + (sliderRect.height - poleH) * 0.5f;
            float flagW = 14f * s;
            float flagOverlap = 4f * s;
            float flagTop = barY - 13f * s;
            float hitPad = 2f * s;
            float hitY = flagTop - hitPad;
            float hitH = (poleTop + poleH) - flagTop + hitPad * 2f;
            float sMinX = tsX - flagW - hitPad;
            if (new Rect(sMinX, hitY, tsX + hitPad - sMinX, hitH).Contains(mouse))
                return true;
            float eMinX = teX - hitPad;
            if (new Rect(eMinX, hitY, teX + flagW + hitPad - eMinX, hitH).Contains(mouse))
                return true;
            return false;
        }

        private void HandleOverlayResizeMouse()
        {
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool leftDown = Input.GetMouseButtonDown(0);
            bool leftHeld = Input.GetMouseButton(0);
            bool leftUp = Input.GetMouseButtonUp(0);

            if (currentStickCamOverlayRectValid)
            {
            if (!stickCamResizing)
            {
                stickCamEdgeHover = GetOverlayHoverEdge(currentStickCamOverlayRect, mouse);
                if ((stickCamEdgeHover & PIP_EDGE_RIGHT) == 0 || (stickCamEdgeHover & (PIP_EDGE_TOP | PIP_EDGE_BOTTOM)) == 0)
                    stickCamEdgeHover = 0;
                if (currentStickCamCloseRectValid && currentStickCamCloseRect.Contains(mouse))
                    stickCamEdgeHover = 0;
            }
                bool stickBlocked = IsUiBlockedFor(UI_LAYER_STICKCAM, mouse);
                if (stickCamResizing && !leftHeld && !leftDown && !leftUp)
                {
                    stickCamResizing = false;
                    stickCamResizeEdge = 0;
                }
                if (leftDown && !stickCamResizing && stickCamEdgeHover != 0 && !stickBlocked)
                {
                    uiFrontLayer = UI_LAYER_STICKCAM;
                    draggingStickCam = false;
                    stickCamResizing = true;
                    stickCamResizeEdge = stickCamEdgeHover;
                    stickCamResizeStartMouse = mouse;
                    // currentStickCamInstance can outlive a closed instance; a stale one
                    // would anchor the resize to where that instance used to be.
                    stickCamResizeTarget = currentStickCamInstance != null && stickCamInstances.Contains(currentStickCamInstance)
                        ? currentStickCamInstance
                        : null;
                    stickCamResizeStartScale = stickCamResizeTarget != null ? stickCamResizeTarget.size : stickCamSize;

                    // Pin the corner opposite the grabbed one (always the left side,
                    // since only the right-hand corners are grabbable).
                    float startBase = Mathf.Clamp(Screen.height * 0.11f, 68f, 96f);
                    float startW = stickCamResizeStartScale * (2f * startBase + 14f) + 10f;
                    float startH = stickCamResizeStartScale * startBase + 10f;
                    float centerX = stickCamResizeTarget != null ? stickCamResizeTarget.x : stickCamX;
                    float centerY = stickCamResizeTarget != null ? stickCamResizeTarget.y : stickCamY;
                    bool startTopGrab = (stickCamResizeEdge & PIP_EDGE_TOP) != 0;
                    stickCamResizeAnchor = new Vector2(
                        centerX - startW * 0.5f,
                        startTopGrab ? centerY + startH * 0.5f : centerY - startH * 0.5f);
                }
                if (stickCamResizing)
                {
                    if (leftUp)
                    {
                        stickCamResizing = false;
                        stickCamResizeEdge = 0;
                        stickCamResizeTarget = null;
                        SaveCurrentOverlayPositions();
                        SaveDisplayPreferences();
                    }
                    else if (leftHeld)
                    {
                        float b0 = Mathf.Clamp(Screen.height * 0.11f, 68f, 96f);
                        float ax = stickCamResizeAnchor.x;
                        float ay = stickCamResizeAnchor.y;
                        bool topGrab = (stickCamResizeEdge & PIP_EDGE_TOP) != 0;
                        float fx = (Mathf.Abs(mouse.x - ax) - 10f) / (2f * b0 + 14f);
                        float fy = (Mathf.Abs(mouse.y - ay) - 10f) / b0;
                        // Only the grabbed stick cam changes size; the single overlay (no
                        // instances) uses the saved global size.
                        float newSize = Mathf.Clamp(Mathf.Max(fx, fy), STICK_CAM_MIN_SIZE, STICK_CAM_MAX_SIZE);
                        if (stickCamResizeTarget != null)
                            stickCamResizeTarget.size = newSize;
                        else
                            stickCamSize = newSize;

                        // Keep the opposite corner pinned so the grabbed corner follows
                        // the mouse; resizing about the center made it lag at half speed.
                        float s1w = newSize * (2f * b0 + 14f) + 10f;
                        float s1h = newSize * b0 + 10f;
                        float newX = ax + s1w * 0.5f;
                        float newY = topGrab ? ay - s1h * 0.5f : ay + s1h * 0.5f;
                        if (stickCamResizeTarget != null)
                        {
                            stickCamResizeTarget.x = newX;
                            stickCamResizeTarget.y = newY;
                        }
                        else
                        {
                            stickCamX = newX;
                            stickCamY = newY;
                            stickCamPositionCustomized = true;
                        }
                    }
                }
            }

            if (currentPlaybackOverlayRectValid)
            {
                if (!playbackResizing)
                {
                    playbackEdgeHover = GetOverlayHoverEdge(currentPlaybackOverlayRect, mouse);
                    if (playbackEdgeHover != 0 && IsPlaybackTrimHit(mouse))
                        playbackEdgeHover = 0;
                }
                bool playbackBlocked = IsUiBlockedFor(UI_LAYER_PLAYBAR, mouse);
                if (playbackResizing && !leftHeld && !leftDown && !leftUp)
                {
                    playbackResizing = false;
                    playbackResizeEdge = 0;
                }
                if (leftDown && !playbackResizing && playbackEdgeHover != 0 && !playbackBlocked)
                {
                    uiFrontLayer = UI_LAYER_PLAYBAR;
                    draggingScrubber = false;
                    playbackResizing = true;
                    playbackResizeEdge = playbackEdgeHover;
                    playbackResizeStartScale = playbackUiSize;
                    playbackResizeStartMouse = mouse;
                }
                if (playbackResizing)
                {
                    if (leftUp)
                    {
                        playbackResizing = false;
                        playbackResizeEdge = 0;
                        SaveCurrentOverlayPositions();
                        SaveDisplayPreferences();
                    }
                    else if (leftHeld)
                    {
                        float start = playbackResizeStartScale;
                        float s0w = start * (Screen.width / 3f + 10f);
                        float s0h = start * 66f;
                        bool rightGrab = (playbackResizeEdge & PIP_EDGE_RIGHT) != 0;
                        bool topGrab = (playbackResizeEdge & PIP_EDGE_TOP) != 0;
                        float ax = rightGrab ? scrubberX - s0w * 0.5f : scrubberX + s0w * 0.5f;
                        float ay = topGrab ? scrubberY + s0h * 0.5f : scrubberY - s0h * 0.5f;
                        float fx = Mathf.Abs(mouse.x - ax) / (Screen.width / 3f + 10f);
                        float fy = Mathf.Abs(mouse.y - ay) / 66f;
                        playbackUiSize = Mathf.Clamp(Mathf.Max(fx, fy), 0.6f, 1.6f);
                    }
                }
            }
        }

        private void HandlePiPWindowMouse()
        {
            if (pipWindows.Count == 0 || pipExternalMonitor)
                return;
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool leftDown = Input.GetMouseButtonDown(0);
            bool leftHeld = Input.GetMouseButton(0);
            bool leftUp = Input.GetMouseButtonUp(0);
            bool pipDropdownAnyOpen = false;
            for (int j = 0; j < pipWindows.Count; j++)
            {
                if (pipWindows[j].dropdownOpen)
                {
                    pipDropdownAnyOpen = true;
                    break;
                }
            }
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w.entry == null)
                    continue;
                if (w.fadeAmount < 0.01f)
                    continue;
                Rect panel = w.rect;
                PipHeaderRects(panel, out Rect pilotBtn, out Rect pinBtn, out Rect plusBtn, out Rect closeBtn);
                bool covered = false;
                for (int j = i + 1; j < pipWindows.Count; j++)
                {
                    if (pipWindows[j].entry != null && pipWindows[j].fadeAmount > 0.01f && pipWindows[j].rect.Contains(mouse))
                    {
                        covered = true;
                        break;
                    }
                }
                w.edgeHover = GetPiPHoverEdge(w, mouse, closeBtn, covered);

                if (leftDown && pilotBtn.Contains(mouse) && !covered)
                {
                    uiFrontLayer = UI_LAYER_PIP;
                    pipActivated = w;
                    w.dragCandidate = false;
                    w.dragging = false;
                    w.dropdownOpen = !w.dropdownOpen;
                    if (w.dropdownOpen)
                    {
                        for (int j = 0; j < pipWindows.Count; j++)
                        {
                            if (pipWindows[j] != w)
                                pipWindows[j].dropdownOpen = false;
                        }
                    }
                    continue;
                }

                if (leftDown && !IsUiBlockedFor(UI_LAYER_PIP, mouse))
                {
                    uiFrontLayer = UI_LAYER_PIP;
                    if (!covered && closeBtn.Contains(mouse))
                    {
                        ClosePiPWindow(w);
                        i--;
                        continue;
                    }
                    if (pipDropdownAnyOpen)
                    {
                        if (w.dropdownOpen)
                        {
                            if (GetPiPDropdownRect(w).Contains(mouse))
                                continue;
                            w.dropdownOpen = false;
                        }
                        continue;
                    }
                    if (!covered && plusBtn.Contains(mouse))
                    {
                        w.dropdownOpen = false;
                        GhostLineEntry next = GetPiPNextUnshownPilot();
                        if (next != null)
                        {
                            PiPWindow spawned = OpenPiPWindow(next, new Rect(w.rect.x + PIP_OPEN_OFFSET, w.rect.y + PIP_OPEN_OFFSET, w.rect.width, w.rect.height));
                            if (spawned != null)
                                pipActivated = spawned;
                        }
                        continue;
                    }
                    if (w.dropdownOpen && GetPiPDropdownRect(w).Contains(mouse))
                        continue;
                    if (w.edgeHover != 0)
                    {
                        w.resizing = true;
                        w.resizeEdge = w.edgeHover;
                        w.resizeStartMouse = mouse;
                        w.resizeStartRect = w.rect;
                        w.dragCandidate = false;
                        w.dragging = false;
                        pipActivated = w;
                        continue;
                    }
                    w.dragCandidate = !w.resizing && !covered && panel.Contains(mouse) && !pinBtn.Contains(mouse) && !plusBtn.Contains(mouse) && !pilotBtn.Contains(mouse) && !closeBtn.Contains(mouse);
                    w.dragStartMouse = mouse;
                    w.dragging = false;
                    if (w.dragCandidate)
                        pipActivated = w;
                }
                else if (leftHeld)
                {
                    if (w.dragCandidate && !w.dragging && (mouse - w.dragStartMouse).sqrMagnitude > 36f)
                    {
                        w.dragging = true;
                        w.dragOffset = mouse - new Vector2(panel.x, panel.y);
                    }
                    if (w.dragging)
                    {
                        w.rect.x = mouse.x - w.dragOffset.x;
                        w.rect.y = mouse.y - w.dragOffset.y;
                        ClampPiPWindowRect(w);
                    }
                    if (w.resizing)
                    {
                        w.rect = ApplyPipAspectResize(w.resizeEdge, w.resizeStartRect,
                            mouse.x - w.resizeStartMouse.x, mouse.y - w.resizeStartMouse.y);
                        ClampPiPWindowRect(w);
                        w.rtDirty = true;
                    }
                }
                else if (leftUp)
                {
                    w.dragCandidate = false;
                    w.dragging = false;
                    if (w.resizing)
                    {
                        w.resizing = false;
                        w.resizeEdge = 0;
                        w.rtDirty = true;
                    }
                }
            }
        }

        private int GetOverlayHoverEdge(Rect panel, Vector2 mouse)
        {
            float cornerT = PIP_RESIZE_GRAB;
            Rect cornerTL = new Rect(panel.x, panel.y, cornerT, cornerT);
            Rect cornerTR = new Rect(panel.xMax - cornerT, panel.y, cornerT, cornerT);
            Rect cornerBL = new Rect(panel.x, panel.yMax - cornerT, cornerT, cornerT);
            Rect cornerBR = new Rect(panel.xMax - cornerT, panel.yMax - cornerT, cornerT, cornerT);
            int hover = 0;
            if (cornerTL.Contains(mouse))
                hover = PIP_EDGE_LEFT | PIP_EDGE_TOP;
            else if (cornerTR.Contains(mouse))
                hover = PIP_EDGE_RIGHT | PIP_EDGE_TOP;
            else if (cornerBL.Contains(mouse))
                hover = PIP_EDGE_LEFT | PIP_EDGE_BOTTOM;
            else if (cornerBR.Contains(mouse))
                hover = PIP_EDGE_RIGHT | PIP_EDGE_BOTTOM;
            else
            {
                float edgeT = PIP_EDGE_THICK;
                float midX = panel.x + panel.width * 0.5f;
                float midY = panel.y + panel.height * 0.5f;
                if (new Rect(panel.x, panel.yMax - edgeT, panel.width, edgeT).Contains(mouse))
                    hover = mouse.x < midX ? PIP_EDGE_LEFT | PIP_EDGE_BOTTOM : PIP_EDGE_RIGHT | PIP_EDGE_BOTTOM;
                else if (new Rect(panel.x, panel.y, panel.width, edgeT).Contains(mouse))
                    hover = mouse.x < midX ? PIP_EDGE_LEFT | PIP_EDGE_TOP : PIP_EDGE_RIGHT | PIP_EDGE_TOP;
                else if (new Rect(panel.xMax - edgeT, panel.y, edgeT, panel.height).Contains(mouse))
                    hover = mouse.y < midY ? PIP_EDGE_RIGHT | PIP_EDGE_TOP : PIP_EDGE_RIGHT | PIP_EDGE_BOTTOM;
                else if (new Rect(panel.x, panel.y, edgeT, panel.height).Contains(mouse))
                    hover = mouse.y < midY ? PIP_EDGE_LEFT | PIP_EDGE_TOP : PIP_EDGE_LEFT | PIP_EDGE_BOTTOM;
            }
            return hover;
        }

        private void DrawOverlayEdgeHighlights(Rect panel, int shownEdges, float alpha)
        {
            if (shownEdges == 0)
                return;
            Color old = GUI.color;
            float edgeT = PIP_EDGE_THICK;
            GUI.color = new Color(0.25f, 0.62f, 0.95f, 0.55f * alpha);
            if ((shownEdges & PIP_EDGE_TOP) != 0)
                GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, edgeT), whiteTex);
            if ((shownEdges & PIP_EDGE_BOTTOM) != 0)
                GUI.DrawTexture(new Rect(panel.x, panel.yMax - edgeT, panel.width, edgeT), whiteTex);
            float insetTop = (shownEdges & PIP_EDGE_TOP) != 0 ? edgeT : 0f;
            float insetBottom = (shownEdges & PIP_EDGE_BOTTOM) != 0 ? edgeT : 0f;
            if ((shownEdges & PIP_EDGE_LEFT) != 0)
                GUI.DrawTexture(new Rect(panel.x, panel.y + insetTop, edgeT, panel.height - insetTop - insetBottom), whiteTex);
            if ((shownEdges & PIP_EDGE_RIGHT) != 0)
                GUI.DrawTexture(new Rect(panel.xMax - edgeT, panel.y + insetTop, edgeT, panel.height - insetTop - insetBottom), whiteTex);
            GUI.color = old;
        }

        private void DrawStickCamCornerHighlights(Rect panel, int shownEdges)
        {
            if (shownEdges == 0)
                return;
            Color old = GUI.color;
            float c = 20f;
            float t = 4f;
            GUI.color = new Color(0.25f, 0.62f, 0.95f, 0.55f);
            if ((shownEdges & (PIP_EDGE_RIGHT | PIP_EDGE_TOP)) == (PIP_EDGE_RIGHT | PIP_EDGE_TOP))
            {
                GUI.DrawTexture(new Rect(panel.xMax - c, panel.y, c - t, t), whiteTex);
                GUI.DrawTexture(new Rect(panel.xMax - t, panel.y, t, c), whiteTex);
            }
            if ((shownEdges & (PIP_EDGE_RIGHT | PIP_EDGE_BOTTOM)) == (PIP_EDGE_RIGHT | PIP_EDGE_BOTTOM))
            {
                GUI.DrawTexture(new Rect(panel.xMax - c, panel.yMax - t, c - t, t), whiteTex);
                GUI.DrawTexture(new Rect(panel.xMax - t, panel.yMax - c, t, c), whiteTex);
            }
            GUI.color = old;
        }

        private int GetPiPResizeEdge(Rect panel, Vector2 mouse)
        {
            if (!panel.Contains(mouse))
                return 0;
            float cornerT = PIP_RESIZE_GRAB;
            float edgeT = PIP_EDGE_THICK;
            if (mouse.x <= panel.x + cornerT && mouse.y <= panel.y + cornerT)
                return PIP_EDGE_LEFT | PIP_EDGE_TOP;
            if (mouse.x >= panel.xMax - cornerT && mouse.y <= panel.y + cornerT)
                return PIP_EDGE_RIGHT | PIP_EDGE_TOP;
            if (mouse.x <= panel.x + cornerT && mouse.y >= panel.yMax - cornerT)
                return PIP_EDGE_LEFT | PIP_EDGE_BOTTOM;
            if (mouse.x >= panel.xMax - cornerT && mouse.y >= panel.yMax - cornerT)
                return PIP_EDGE_RIGHT | PIP_EDGE_BOTTOM;
            if (mouse.y <= panel.y + edgeT)
                return PIP_EDGE_TOP;
            if (mouse.y >= panel.yMax - edgeT)
                return PIP_EDGE_BOTTOM;
            if (mouse.x <= panel.x + edgeT)
                return PIP_EDGE_LEFT;
            if (mouse.x >= panel.xMax - edgeT)
                return PIP_EDGE_RIGHT;
            return 0;
        }

        private int GetPiPHoverEdge(PiPWindow w, Vector2 mouse, Rect closeBtn, bool covered)
        {
            if (w.resizing || w.dragging || covered || closeBtn.Contains(mouse))
                return 0;
            if (w.dropdownOpen && GetPiPDropdownRect(w).Contains(mouse))
                return 0;
            Rect panel = w.rect;
            if (GetPiPPilotBtnRect(panel).Contains(mouse))
                return 0;
            if (new Rect(panel.xMax - 58f, panel.y + 3f, 18f, 18f).Contains(mouse))
                return 0;
            if (new Rect(panel.xMax - 82f, panel.y + 3f, 18f, 18f).Contains(mouse))
                return 0;
            return GetPiPResizeEdge(panel, mouse);
        }

        private void RenderPiPWindow(PiPWindow w)
        {
            if (w.cam == null || w.rt == null)
                return;
            Rect screenRect = new Rect(0f, 0f, Screen.width, Screen.height);
            if (w.rect.width < 60f || w.rect.height < 40f || !w.rect.Overlaps(screenRect))
                return;

            Transform mount = null;
            ghostCamMountMap.TryGetValue(w.entry, out mount);
            if (mount == null)
                ghostDroneMap.TryGetValue(w.entry, out mount);

            Transform rootT;
            float appliedT;
            Vector3 rawPos = Vector3.zero;
            Quaternion rawRot = Quaternion.identity;
            bool haveAppliedT = TryGetReplayTime(out appliedT);
            if (pinnedPiPReplayTime >= 0f)
            {
                appliedT = w.lastRenderTime >= 0f ? w.lastRenderTime : pinnedPiPReplayTime;
                haveAppliedT = true;
            }
            else if (!haveAppliedT)
            {
                appliedT = 0f;
                haveAppliedT = true;
            }
            if (haveAppliedT)
                w.lastRenderTime = appliedT;
            // While playing, place the camera from this frame's snapshot pose, the
            // same one its drone root was just moved to; paused PiPs keep sampling
            // at their pinned time.
            bool havePose = pinnedPiPReplayTime < 0f && TryGetFramePose(w.entry, out rawPos, out rawRot);
            if (!havePose && haveAppliedT)
                havePose = TryGetDroneTransform(w.entry, appliedT, out rawPos, out rawRot);
            if (ghostDroneMap.TryGetValue(w.entry, out rootT) && rootT != null && havePose)
            {
                Vector3 localOffset = Quaternion.Inverse(rootT.rotation) * (mount.position - rootT.position);
                Quaternion localRot = Quaternion.Inverse(rootT.rotation) * mount.rotation;
                Vector3 mountPos = rawPos + rawRot * localOffset;
                Quaternion mountRot = rawRot * localRot;
                Vector3 camPos = mountPos
                    + mountRot * (Vector3.forward * PIP_CAMERA_FORWARD_OFFSET)
                    + mountRot * (Vector3.up * PIP_CAMERA_UP_OFFSET);
                w.cam.transform.SetPositionAndRotation(camPos, mountRot);
            }
            else if (mount == null && havePose)
            {
                Vector3 camPos = rawPos
                    + rawRot * (Vector3.forward * PIP_CAMERA_FORWARD_OFFSET)
                    + rawRot * (Vector3.up * PIP_CAMERA_UP_OFFSET);
                w.cam.transform.SetPositionAndRotation(camPos, rawRot);
            }
            else if (mount != null)
            {
                Vector3 camPos = mount.position
                    + mount.rotation * (Vector3.forward * PIP_CAMERA_FORWARD_OFFSET)
                    + mount.rotation * (Vector3.up * PIP_CAMERA_UP_OFFSET);
                w.cam.transform.SetPositionAndRotation(camPos, mount.rotation);
            }
            else
            {
                return;
            }
            if (!w.renderDiagLogged)
            {
                w.renderDiagLogged = true;
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] PIP first render " + GetPilotLabel(w.entry, 0)
                    + " mountMap=" + (ghostCamMountMap.ContainsKey(w.entry) ? "1" : "0")
                    + " droneMap=" + (ghostDroneMap.ContainsKey(w.entry) ? "1" : "0")
                    + " t=" + appliedT.ToString("F2")
                    + " pos=" + w.cam.transform.position.ToString("F1")
                    + " rot=" + w.cam.transform.rotation.eulerAngles.ToString("F0"));
            }
            List<GhostLineEntry> pipHiddenEntries = null;
            if (!pipShowNamesDelta && GhostLinesPlugin.entries != null)
            {
                foreach (var pe in GhostLinesPlugin.entries)
                {
                    if (pe == null || pe.markerText == null)
                        continue;
                    GameObject labelParent = pe.markerText.transform.parent != null ? pe.markerText.transform.parent.gameObject : null;
                    if (labelParent == null || !labelParent.activeSelf)
                        continue;
                    if (pipHiddenEntries == null)
                        pipHiddenEntries = pipRenderHiddenEntries ?? (pipRenderHiddenEntries = new List<GhostLineEntry>());
                    pipHiddenEntries.Add(pe);
                    labelParent.SetActive(false);
                    if (pe.markerTether != null)
                        pe.markerTether.enabled = false;
                }
            }
            w.cam.Render();
            w.renderedOnce = true;
            if (pipHiddenEntries != null)
            {
                foreach (var pe in pipHiddenEntries)
                {
                    if (pe == null || pe.markerText == null)
                        continue;
                    GameObject labelParent = pe.markerText.transform.parent != null ? pe.markerText.transform.parent.gameObject : null;
                    if (labelParent != null)
                        labelParent.SetActive(true);
                    if (pe.markerTether != null)
                        pe.markerTether.enabled = (GhostLinesPlugin.showNameTags && pe.nameTagVisible) || (GhostLinesPlugin.showDeltaDisplay && pe.deltaDisplayVisible);
                }
                pipHiddenEntries.Clear();
            }
            if (w.nativePip != null)
                PushNativePipFrame(w.nativePip, w.rt);
        }

        private Rect GetPiPContentRect(Rect panel)
        {
            return new Rect(panel.x + 1f, panel.y + PIP_TITLE_HEIGHT, panel.width - 2f, panel.height - PIP_TITLE_HEIGHT - 1f);
        }

        private void ClampPiPWindowRect(PiPWindow w)
        {
            w.rect.x = Mathf.Clamp(w.rect.x, 0f, Mathf.Max(0f, Screen.width - w.rect.width));
            w.rect.y = Mathf.Clamp(w.rect.y, 0f, Mathf.Max(0f, Screen.height - w.rect.height));
        }

        private readonly Dictionary<string, string> fitNameCache = new Dictionary<string, string>();
        private bool fitNameCacheDarkMode = false;

        private void DrawPiPWindows()
        {
            if (pipWindows.Count == 0)
                return;
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            Event e = Event.current;
            if (pipExternalMonitor)
                return;
            if (pipActivated != null && pipWindows.Contains(pipActivated))
            {
                pipWindows.Remove(pipActivated);
                pipWindows.Add(pipActivated);
                pipActivated = null;
            }
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w.entry == null)
                    continue;
                float alpha = w.fadeAmount;
                if (alpha < 0.01f)
                    continue;
                Rect panel = w.rect;

                bool chromeVisible = w.chromeAlpha > 0.01f;

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

                Rect content = chromeVisible ? GetPiPContentRect(panel) : panel;

                if (w.rt != null)
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.DrawTexture(content, w.rt, ScaleMode.StretchToFill, false);
                }
                else
                {
                    GUI.color = new Color(0.1f, 0.1f, 0.12f, alpha);
                    GUI.DrawTexture(content, whiteTex);
                }

                for (int de2 = w.docked.Count - 1; de2 >= 0; de2--)
                {
                    PipDockEntry de = w.docked[de2];
                    if ((de.stick != null && !stickCamInstances.Contains(de.stick)) ||
                        (de.graph != null && !liveInputGraphs.Contains(de.graph)) ||
                        (de.stickGraph != null && !stickGraphs.Contains(de.stickGraph)))
                        w.docked.RemoveAt(de2);
                }

                LiveGraphState replayDragGraph = null;
                for (int gi2 = 0; gi2 < liveInputGraphs.Count; gi2++)
                {
                    LiveGraphState cg = liveInputGraphs[gi2];
                    if (cg.dragging && cg.mpTarget == null)
                    {
                        replayDragGraph = cg;
                        break;
                    }
                }
                StickCamInstance replayDragStick = null;
                for (int si2 = 0; si2 < stickCamInstances.Count; si2++)
                {
                    if (stickCamInstances[si2].dragging)
                    {
                        replayDragStick = stickCamInstances[si2];
                        break;
                    }
                }
                StickGraphState replayDragStickGraph = null;
                for (int sg2 = 0; sg2 < stickGraphs.Count; sg2++)
                {
                    if (stickGraphs[sg2].dragging)
                    {
                        replayDragStickGraph = stickGraphs[sg2];
                        break;
                    }
                }
                PiPWindow replayPreviewWindow = null;
                int replayPreviewSlot = -1;
                if (replayDragGraph != null || replayDragStick != null || replayDragStickGraph != null || draggingStickCam)
                {
                    replayPreviewWindow = FindReplayPipWindowForDock(e.mousePosition);
                    if (replayPreviewWindow != null)
                        replayPreviewSlot = GetPipDockSlotAt(replayPreviewWindow.rect, e.mousePosition);
                }
                if (w == replayPreviewWindow && replayPreviewSlot >= 0)
                {
                    Rect dockGridContent = GetPiPContentRect(panel);
                    for (int s = 0; s < 9; s++)
                    {
                        Rect cell = GetPipDockSlotRect(dockGridContent, s);
                        cell = new Rect(cell.x + 3f, cell.y + 3f, cell.width - 6f, cell.height - 6f);
                        bool slotHovered = s == replayPreviewSlot;
                        bool slotOccupied = PipSlotOccupied(w.docked, s);
                        GUI.color = slotHovered
                            ? new Color(1f, 0.55f, 0.1f, 0.32f * alpha)
                            : slotOccupied ? new Color(1f, 0.55f, 0.1f, 0.07f * alpha) : new Color(1f, 0.55f, 0.1f, 0.13f * alpha);
                        GUI.DrawTexture(cell, whiteTex);
                        GUI.color = new Color(1f, 0.6f, 0.1f, (slotHovered ? 0.9f : 0.45f) * alpha);
                        GUI.DrawTexture(new Rect(cell.x, cell.y, cell.width, 1f), whiteTex);
                        GUI.DrawTexture(new Rect(cell.x, cell.yMax - 1f, cell.width, 1f), whiteTex);
                        GUI.DrawTexture(new Rect(cell.x, cell.y, 1f, cell.height), whiteTex);
                        GUI.DrawTexture(new Rect(cell.xMax - 1f, cell.y, 1f, cell.height), whiteTex);
                    }
                }

                for (int de3 = 0; de3 < w.docked.Count; de3++)
                {
                    PipDockEntry de = w.docked[de3];
                    Rect dockContent = GetPiPContentRect(panel);
                    Rect cell = GetPipDockSlotRect(dockContent, de.slot);
                    cell = new Rect(cell.x + 3f, cell.y + 3f, cell.width - 6f, cell.height - 6f);
                    if (de.stickGraph != null)
                    {
                        StickGraphState sg = de.stickGraph;
                        if (sg.pilot == null || sg.pilot.stateTimes == null || sg.pilot.stateTimes.Length < 2)
                            continue;
                        int stw = Mathf.Max(1, (int)cell.width);
                        int sth = Mathf.Max(1, (int)cell.height);
                        float sgLastT = sg.pilot.stateTimes[sg.pilot.stateTimes.Length - 1];
                        float sgFirstT = sg.pilot.stateTimes[0];
                        float sgLapStart = Mathf.Clamp(GetGateTimestamp(sg.pilot), sgFirstT, sgLastT);
                        float sgLapDuration = Mathf.Max(sgLastT - sgLapStart, 0.001f);
                        bool sgRebuild = sg.traceTex == null || sg.traceCachePlotW != stw || sg.traceCachePlotH != sth ||
                            IsStickGraphTraceStale(sg, sgLapStart, sgLapDuration);
                        if (sgRebuild)
                            BuildStickGraphTraceTexture(sg, stw, sth, sgLapStart, sgLapDuration);
                        if (sg.traceTex != null)
                        {
                            GUI.color = new Color(1f, 1f, 1f, alpha);
                            GUI.DrawTexture(cell, sg.traceTex, ScaleMode.StretchToFill, true);
                        }
                        float sgQueryT = sgFirstT;
                        if (replayReady && TryGetFrameReplayTime(out float sgRt))
                            sgQueryT = GetGateQueryTimestamp(sg.pilot, sgRt);
                        if (sgQueryT >= sgLapStart && sgQueryT <= sgLastT)
                        {
                            float sgPlayX = cell.x + (sgQueryT - sgLapStart) / sgLapDuration * cell.width;
                            Color sgPlayColor = darkMode
                                ? new Color(1f, 1f, 1f, 0.85f * alpha)
                                : new Color(0.12f, 0.12f, 0.12f, 0.85f * alpha);
                            GUI.color = sgPlayColor;
                            GUI.DrawTexture(new Rect(sgPlayX, cell.y, 1f, cell.height), whiteTex);
                            GUI.color = new Color(sgPlayColor.r, sgPlayColor.g, sgPlayColor.b, 0.4f * alpha);
                            GUI.DrawTexture(new Rect(sgPlayX + 1f, cell.y, 1f, cell.height), whiteTex);
                        }
                        string sgAxis = AxisLetters[Mathf.Clamp(sg.channel, 0, 3)];
                        GUI.color = new Color(0f, 0f, 0f, alpha);
                        GUI.Label(new Rect(cell.x + 3f, cell.y + 3f, 20f, 14f), sgAxis, settingsSmallStyle);
                        GUI.color = new Color(0.863f, 0.863f, 0.863f, alpha);
                        GUI.Label(new Rect(cell.x + 2f, cell.y + 2f, 20f, 14f), sgAxis, settingsSmallStyle);
                        if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                        {
                            if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                                (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                            {
                                lastPipDockClickTime = -1f;
                                sg.rect = new Rect(e.mousePosition.x - sg.rect.width * 0.5f,
                                    e.mousePosition.y - sg.rect.height * 0.5f, sg.rect.width, sg.rect.height);
                                EnsureStickGraphRect(sg);
                                w.docked.RemoveAt(de3);
                                de3--;
                                w.dragCandidate = false;
                                w.dragging = false;
                                stickGraphActivated = sg;
                                uiFrontLayer = UI_LAYER_STICKGRAPH;
                                e.Use();
                            }
                            else
                            {
                                lastPipDockClickTime = Time.realtimeSinceStartup;
                                lastPipDockClickPos = e.mousePosition;
                            }
                        }
                        continue;
                    }
                    if (de.stick != null)
                    {
                        StickCamInstance di = de.stick;
                        Vector4 input = Vector4.zero;
                        bool hasInput = false;
                        if (di.entry != null && replayReady)
                        {
                            float dockRt;
                            if (TryGetFrameReplayTime(out dockRt) && TryGetDroneInput(di.entry, dockRt, out input))
                                hasInput = true;
                        }
                        else if (di.mpTarget != null && di.mpTarget.root != null)
                        {
                            if (di.mpTarget.root != di.mpTarget.syncRoot || di.mpTarget.photonView != di.mpTarget.syncView)
                            {
                                di.mpTarget.syncComponent = null;
                                di.mpTarget.lastSyncAttempt = 0f;
                            }
                            if (di.mpTarget.syncComponent == null)
                                EnsureMpTargetSyncAccess(di.mpTarget);
                            if (di.mpTarget.syncComponent != null && TryGetMpInputCached(di.mpTarget, out input))
                                hasInput = true;
                        }
                        if (hasInput)
                        {
                            float gapd = cell.height * 0.12f;
                            float panelSize = Mathf.Min(cell.height, (cell.width - gapd) * 0.5f);
                            float groupW = panelSize * 2f + gapd;
                            Rect lp = new Rect(cell.center.x - groupW * 0.5f, cell.center.y - panelSize * 0.5f, panelSize, panelSize);
                            Rect rp = new Rect(lp.xMax + gapd, lp.y, panelSize, panelSize);
                            EnsureStickTextures();
                            DrawStickPanel(lp, new Vector2(input.y, input.x * 2f - 1f));
                            DrawStickPanel(rp, new Vector2(-input.w, input.z));
                        }
                        if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                        {
                            if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                                (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                            {
                                float gapu = cell.height * 0.12f;
                                float panelSizeU = Mathf.Min(cell.height, (cell.width - gapu) * 0.5f);
                                float groupWU = panelSizeU * 2f + gapu;
                                lastPipDockClickTime = -1f;
                                di.x = Mathf.Clamp(e.mousePosition.x, groupWU * 0.5f + 2f, Screen.width - groupWU * 0.5f - 2f);
                                di.y = Mathf.Clamp(e.mousePosition.y, panelSizeU * 0.5f + 2f, Screen.height - panelSizeU * 0.5f - 2f);
                                w.docked.RemoveAt(de3);
                                de3--;
                                w.dragCandidate = false;
                                w.dragging = false;
                                uiFrontLayer = UI_LAYER_STICKCAM;
                                e.Use();
                            }
                            else
                            {
                                lastPipDockClickTime = Time.realtimeSinceStartup;
                                lastPipDockClickPos = e.mousePosition;
                            }
                        }
                        continue;
                    }
                    if (de.graph == null)
                        continue;
                    LiveGraphState dg = de.graph;
                    bool dgLapMode = dg.mpTarget == null && dg.pilot != null && replayReady &&
                        dg.pilot.originalInputs != null && dg.pilot.stateTimes != null && dg.pilot.stateTimes.Length >= 2;
                    if (dgLapMode)
                    {
                        int ltw = Mathf.Max(1, (int)cell.width);
                        int lth = Mathf.Max(1, (int)cell.height);
                        float dgLapEnd = dg.pilot.stateTimes[dg.pilot.stateTimes.Length - 1];
                        float dgLapStart = Mathf.Clamp(GetGateTimestamp(dg.pilot), dg.pilot.stateTimes[0], dgLapEnd);
                        float dgLapDur = Mathf.Max(dgLapEnd - dgLapStart, 0.001f);
                        bool dgLapRebuild = dg.traceTex == null || dg.traceCachePilot != dg.pilot ||
                            dg.traceCacheChannel != dg.channel || dg.traceCachePlotW != ltw || dg.traceCachePlotH != lth;
                        if (dgLapRebuild)
                        {
                            BuildLapTraceTexture(dg.pilot, dg.channel, ltw, lth, dgLapStart, dgLapDur,
                                ref dg.traceTex, ref dg.tracePixels);
                            dg.traceLastBuildTime = Time.unscaledTime;
                            dg.traceCachePilot = dg.pilot;
                            dg.traceCacheMpTarget = null;
                            dg.traceCacheChannel = dg.channel;
                            dg.traceCachePlotW = ltw;
                            dg.traceCachePlotH = lth;
                        }
                        if (dg.traceTex != null)
                        {
                            GUI.color = new Color(1f, 1f, 1f, alpha);
                            GUI.DrawTexture(cell, dg.traceTex, ScaleMode.StretchToFill, true);
                        }
                        float dgQuery = dgLapStart;
                        if (TryGetFrameReplayTime(out float dgRt2))
                            dgQuery = GetGateQueryTimestamp(dg.pilot, dgRt2);
                        if (dgQuery >= dgLapStart && dgQuery <= dgLapEnd)
                        {
                            float dgPlayX = cell.x + (dgQuery - dgLapStart) / dgLapDur * cell.width;
                            Color dgPlayColor = darkMode
                                ? new Color(1f, 1f, 1f, 0.85f * alpha)
                                : new Color(0.12f, 0.12f, 0.12f, 0.85f * alpha);
                            GUI.color = dgPlayColor;
                            GUI.DrawTexture(new Rect(dgPlayX, cell.y, 1f, cell.height), whiteTex);
                            GUI.color = new Color(dgPlayColor.r, dgPlayColor.g, dgPlayColor.b, 0.4f * alpha);
                            GUI.DrawTexture(new Rect(dgPlayX + 1f, cell.y, 1f, cell.height), whiteTex);
                        }
                        string dgAxisChar = AxisLetters[Mathf.Clamp(dg.channel, 0, 3)];
                        GUI.color = new Color(0f, 0f, 0f, alpha);
                        GUI.Label(new Rect(cell.x + 3f, cell.y + 3f, 20f, 14f), dgAxisChar, settingsSmallStyle);
                        GUI.color = new Color(0.863f, 0.863f, 0.863f, alpha);
                        GUI.Label(new Rect(cell.x + 2f, cell.y + 2f, 20f, 14f), dgAxisChar, settingsSmallStyle);
                        if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                        {
                            if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                                (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                            {
                                lastPipDockClickTime = -1f;
                                dg.rect = new Rect(e.mousePosition.x - dg.rect.width * 0.5f,
                                    e.mousePosition.y - dg.rect.height * 0.5f, dg.rect.width, dg.rect.height);
                                dg.rect = ClampGraphRect(dg.rect);
                                w.docked.RemoveAt(de3);
                                de3--;
                                w.dragCandidate = false;
                                w.dragging = false;
                                liveGraphActivated = dg;
                                uiFrontLayer = UI_LAYER_STICKGRAPH;
                                e.Use();
                            }
                            else
                            {
                                lastPipDockClickTime = Time.realtimeSinceStartup;
                                lastPipDockClickPos = e.mousePosition;
                            }
                        }
                        continue;
                    }
                    bool hasGraphData = dg.mpSampleCount >= 2 || (dg.mpTarget == null && dg.pilot != null);
                    int tw = Mathf.Max(1, (int)cell.width);
                    int th = Mathf.Max(1, (int)cell.height);
                    int history = LIVE_HISTORY_OPTIONS[Mathf.Clamp(dg.historyIndex, 0, LIVE_HISTORY_OPTIONS.Length - 1)];
                    bool rebuild = dg.traceTex == null || dg.traceCachePlotW != tw || dg.traceCachePlotH != th ||
                        dg.traceCacheChannel != dg.channel || dg.traceCacheHistory != history ||
                        dg.traceCacheMpTarget != dg.mpTarget || dg.traceCachePilot != dg.pilot ||
                        Time.unscaledTime - dg.traceLastBuildTime > 0.1f;
                    if (rebuild)
                    {
                        BuildLiveTraceTexture(dg, tw, th, history);
                    }
                    if (dg.traceTex != null)
                    {
                        GUI.color = new Color(1f, 1f, 1f, alpha);
                        GUI.DrawTexture(cell, dg.traceTex, ScaleMode.StretchToFill, true);
                    }
                    string axisChar = AxisLetters[Mathf.Clamp(dg.channel, 0, 3)];
                    GUI.color = new Color(0f, 0f, 0f, alpha);
                    GUI.Label(new Rect(cell.x + 3f, cell.y + 3f, 20f, 14f), axisChar, settingsSmallStyle);
                    GUI.color = new Color(0.863f, 0.863f, 0.863f, alpha);
                    GUI.Label(new Rect(cell.x + 2f, cell.y + 2f, 20f, 14f), axisChar, settingsSmallStyle);
                    if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                    {
                        if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                            (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                        {
                            lastPipDockClickTime = -1f;
                            dg.rect = new Rect(e.mousePosition.x - dg.rect.width * 0.5f,
                                e.mousePosition.y - dg.rect.height * 0.5f, dg.rect.width, dg.rect.height);
                            dg.rect = ClampGraphRect(dg.rect);
                            w.docked.RemoveAt(de3);
                            de3--;
                            w.dragCandidate = false;
                            w.dragging = false;
                            liveGraphActivated = dg;
                            uiFrontLayer = UI_LAYER_STICKGRAPH;
                            e.Use();
                        }
                        else
                        {
                            lastPipDockClickTime = Time.realtimeSinceStartup;
                            lastPipDockClickPos = e.mousePosition;
                        }
                    }
                }

                if (w == dockedStickPip && dockedStickSlot >= 0)
                {
                    Rect dcell = GetPipDockSlotRect(GetPiPContentRect(panel), dockedStickSlot);
                    dcell = new Rect(dcell.x + 3f, dcell.y + 3f, dcell.width - 6f, dcell.height - 6f);
                    Vector4 dInput = Vector4.zero;
                    bool dHas = false;
                    float dGap = dcell.height * 0.12f;
                    float dPanel = Mathf.Min(dcell.height, (dcell.width - dGap) * 0.5f);
                    float dGroupW = dPanel * 2f + dGap;
                    if (replayReady && spectatedEntry != null && TryGetFrameReplayTime(out float dRt) &&
                        TryGetDroneInput(spectatedEntry, dRt, out dInput))
                        dHas = true;
                    if (dHas)
                    {
                        Rect dlp = new Rect(dcell.center.x - dGroupW * 0.5f, dcell.center.y - dPanel * 0.5f, dPanel, dPanel);
                        Rect drp = new Rect(dlp.xMax + dGap, dlp.y, dPanel, dPanel);
                        EnsureStickTextures();
                        DrawStickPanel(dlp, new Vector2(dInput.y, dInput.x * 2f - 1f));
                        DrawStickPanel(drp, new Vector2(-dInput.w, dInput.z));
                    }
                    if (e.type == EventType.MouseDown && e.button == 0 && dcell.Contains(e.mousePosition))
                    {
                        if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                            (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                        {
                            lastPipDockClickTime = -1f;
                            stickCamX = SnapOverlayToHorizontalCenter(Mathf.Clamp(
                                e.mousePosition.x, dGroupW * 0.5f + 2f, Screen.width - dGroupW * 0.5f - 2f));
                            stickCamY = Mathf.Clamp(e.mousePosition.y, dPanel * 0.5f + 2f, Screen.height - dPanel * 0.5f - 2f);
                            dockedStickPip = null;
                            dockedStickSlot = -1;
                            uiFrontLayer = UI_LAYER_STICKCAM;
                            e.Use();
                        }
                        else
                        {
                            lastPipDockClickTime = Time.realtimeSinceStartup;
                            lastPipDockClickPos = e.mousePosition;
                        }
                    }
                }

                if (chromeVisible)
                {
                PipHeaderRects(panel, out Rect pilotBtn, out Rect pinBtn, out Rect plusBtn, out Rect closeBtn);

                bool pilotHover = pilotBtn.Contains(e.mousePosition);
                GUI.color = pilotHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
                GUI.DrawTexture(pilotBtn, whiteTex);
                GUI.color = ThemeButtonBorder(alpha);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.y, pilotBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.yMax - 1f, pilotBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.y, 1f, pilotBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.xMax - 1f, pilotBtn.y, 1f, pilotBtn.height), whiteTex);
                GUI.color = ThemeLabel(alpha);
                GUI.Label(new Rect(pilotBtn.x + 6f, pilotBtn.y, pilotBtn.width - 12f, pilotBtn.height),
                    GetPilotLabel(w.entry, GhostLinesPlugin.entries.IndexOf(w.entry)), settingsSmallStyle);

                if (DrawStyledToggleButton(pinBtn, iconPin, w.pinned, alpha, true, UI_LAYER_PIP))
                {
                    w.pinned = !w.pinned;
                    pipActivated = w;
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

                GUI.color = ThemeCloseLabel(alpha);
                GUI.Label(new Rect(closeBtn.x, closeBtn.y - 3f, closeBtn.width, closeBtn.height),
                    "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);

                int shownEdges = w.resizing ? w.resizeEdge : w.edgeHover;
                if (shownEdges != 0)
                {
                    float edgeT = PIP_EDGE_THICK;
                    GUI.color = new Color(0.25f, 0.62f, 0.95f, 0.55f * alpha);
                    if ((shownEdges & PIP_EDGE_LEFT) != 0)
                        GUI.DrawTexture(new Rect(panel.x, panel.y, edgeT, panel.height), whiteTex);
                    if ((shownEdges & PIP_EDGE_RIGHT) != 0)
                        GUI.DrawTexture(new Rect(panel.xMax - edgeT, panel.y, edgeT, panel.height), whiteTex);
                    if ((shownEdges & PIP_EDGE_TOP) != 0)
                    {
                        float leftTrim = (shownEdges & PIP_EDGE_LEFT) != 0 ? edgeT : 0f;
                        float rightTrim = (shownEdges & PIP_EDGE_RIGHT) != 0 ? edgeT : 0f;
                        GUI.DrawTexture(new Rect(panel.x + leftTrim, panel.y, panel.width - leftTrim - rightTrim, edgeT), whiteTex);
                    }
                    if ((shownEdges & PIP_EDGE_BOTTOM) != 0)
                    {
                        float leftTrim = (shownEdges & PIP_EDGE_LEFT) != 0 ? edgeT : 0f;
                        float rightTrim = (shownEdges & PIP_EDGE_RIGHT) != 0 ? edgeT : 0f;
                        GUI.DrawTexture(new Rect(panel.x + leftTrim, panel.yMax - edgeT, panel.width - leftTrim - rightTrim, edgeT), whiteTex);
                    }
                }
                }
                GUI.color = old;
            }
            DrawPiPDropdowns();
        }

        private void DrawPiPDropdowns()
        {
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            Event e = Event.current;
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w.entry == null || !w.dropdownOpen)
                    continue;
                float alpha = w.fadeAmount;
                if (alpha < 0.01f)
                    continue;
                Rect dd = GetPiPDropdownRect(w);
                Color ddOld = GUI.color;
                float rowH = 20f;
                int optCount = GhostLinesPlugin.entries.Count;
                if (optCount == 0)
                {
                    GUI.color = ThemeOptionBg(alpha);
                    GUI.DrawTexture(dd, whiteTex);
                    GUI.color = ThemeOptionBorder(alpha);
                    GUI.DrawTexture(new Rect(dd.x, dd.y, dd.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(dd.x, dd.yMax - 1f, dd.width, 1f), whiteTex);
                    GUI.color = ThemeDimLabel(alpha);
                    GUI.Label(new Rect(dd.x + 8f, dd.y, dd.width - 16f, rowH), "No pilots", settingsSmallStyle);
                    GUI.color = ddOld;
                    continue;
                }
                for (int pi = 0; pi < optCount; pi++)
                {
                    GhostLineEntry opt = GhostLinesPlugin.entries[pi];
                    Rect optRect = new Rect(dd.x, dd.y + rowH * pi, dd.width, rowH);
                    bool optHover = optRect.Contains(e.mousePosition);
                    bool optSelected = opt == w.entry;
                    GUI.color = optSelected
                        ? new Color(0.25f, 0.62f, 0.95f, alpha)
                        : optHover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeOptionBg(alpha);
                    GUI.DrawTexture(optRect, whiteTex);
                    GUI.color = ThemeOptionBorder(alpha);
                    GUI.DrawTexture(new Rect(optRect.x, optRect.y, optRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(optRect.x, optRect.yMax - 1f, optRect.width, 1f), whiteTex);
                    GUI.color = optSelected ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
                    GUI.Label(optRect, GetPilotLabel(opt, pi), settingsSmallStyle);
                    if (e.type == EventType.MouseUp && e.button == 0 && optRect.Contains(e.mousePosition))
                    {
                        AssignPiPPilot(w, opt);
                        w.dropdownOpen = false;
                        pipActivated = w;
                        e.Use();
                    }
                }
                GUI.color = ddOld;
            }
        }

        private void AssignPiPPilot(PiPWindow w, GhostLineEntry entry)
        {
            if (entry == null || w.entry == entry)
                return;
            for (int i = 0; i < pipWindows.Count; i++)
            {
                if (pipWindows[i] != w && pipWindows[i].entry == entry)
                    return;
            }
            w.entry = entry;
            w.rtDirty = true;
        }

        // Shared PiP header layout: pilot field, pin, plus, close with even 6px gaps
        // and the big 28x22 X centred on the pin row.
        private static void PipHeaderRects(Rect panel, out Rect pilot, out Rect pin, out Rect plus, out Rect close)
        {
            // Pin sits next to the X, matching the stick/live graph header order.
            pilot = new Rect(panel.x + 6f, panel.y + 3f, Mathf.Max(70f, panel.width - 94f), 18f);
            pin = new Rect(panel.xMax - 58f, panel.y + 3f, 18f, 18f);
            plus = new Rect(panel.xMax - 82f, panel.y + 3f, 18f, 18f);
            close = new Rect(panel.xMax - 34f, panel.y + 1f, 28f, 22f);
        }

        private Rect GetPiPPilotBtnRect(Rect panel)
        {
            return new Rect(panel.x + 6f, panel.y + 3f, Mathf.Max(70f, panel.xMax - 88f - (panel.x + 6f)), 18f);
        }

        private GhostLineEntry GetPiPNextUnshownPilot()
        {
            for (int i = 0; i < GhostLinesPlugin.entries.Count; i++)
            {
                GhostLineEntry entry = GhostLinesPlugin.entries[i];
                bool shown = false;
                for (int j = 0; j < pipWindows.Count; j++)
                {
                    if (pipWindows[j].entry == entry)
                    {
                        shown = true;
                        break;
                    }
                }
                if (!shown)
                    return entry;
            }
            return null;
        }

        private Rect GetPiPDropdownRect(PiPWindow w)
        {
            Rect pilotBtn = GetPiPPilotBtnRect(w.rect);
            float rowH = 20f;
            float ddW = pilotBtn.width;
            float ddH = rowH * Mathf.Max(GhostLinesPlugin.entries.Count, 1);
            float ddY = pilotBtn.yMax;
            if (ddY + ddH > Screen.height - 4f)
                ddY = Mathf.Max(4f, pilotBtn.y - ddH);
            return new Rect(pilotBtn.x, ddY, ddW, ddH);
        }

        private bool IsPiPMouseBlocked(Vector2 mousePosition)
        {
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w.rect.Contains(mousePosition) && w.fadeAmount > 0.01f)
                    return true;
                if (w.dropdownOpen && GetPiPDropdownRect(w).Contains(mousePosition))
                    return true;
            }
            return false;
        }

        private const int UI_LAYER_STICKGRAPH = 2;
        private const int UI_LAYER_SETTINGS = 3;
        private const int UI_LAYER_BINDINGS = 4;
        private const int UI_LAYER_PIP = 5;
        private const int UI_LAYER_STICKCAM = 6;
        private const int UI_LAYER_PLAYBAR = 7;
        private const int UI_LAYER_MPPIP = 8;
        private int uiFrontLayer = UI_LAYER_PIP;
    }
}
