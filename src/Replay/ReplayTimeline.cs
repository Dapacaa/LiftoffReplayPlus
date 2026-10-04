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
        private void CacheReplayController()
        {
            if (replayControlsObj == null) return;
            if (replayController == null)
            {
                replayController = replayControlsObj.GetComponent<ReplayControls>();
                if (replayController != null && ModLog.DebugEnabled)
                    Debug.Log("[GhostLines] Cached ReplayControls controller");

                Transform playBtnT = replayControlsObj.transform.Find("VLayout/Controls/btnPlay/ImgPause");
                if (playBtnT != null)
                {
                    isPlaying = playBtnT.gameObject.activeSelf;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Initial isPlaying={isPlaying}");
                }
            }

            if (sliderComponent == null)
                FindSliderReflection();

            if (!cameraDumped)
            {
                cameraDumped = true;
                DumpCameraInfo();
            }

            if (nativeSpeedPropInfo == null && nativeSpeedFieldInfo == null && !nativeSpeedResolveLogged)
            {
                nativeSpeedResolveLogged = true;
                object race = GetReplayRace();
                if (race != null)
                {
                    var raceType = race.GetType();
                    const System.Reflection.BindingFlags flags =
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
                    nativeSpeedPropInfo = raceType.GetProperty(NATIVE_SPEED_MEMBER, flags);
                    if (nativeSpeedPropInfo == null || !nativeSpeedPropInfo.CanRead)
                    {
                        nativeSpeedPropInfo = null;
                        nativeSpeedFieldInfo = raceType.GetField(NATIVE_SPEED_MEMBER, flags);
                    }
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Native playback speed member: prop={nativeSpeedPropInfo != null}, field={nativeSpeedFieldInfo != null}");
                }
            }
        }

        private bool IsNativeReplayPlaying()
        {
            if (nativePlayPauseIcon == null)
            {
                nativePlayPauseIcon = replayControlsObj != null
                    ? replayControlsObj.transform.Find("VLayout/Controls/btnPlay/ImgPause")
                    : null;
            }
            return nativePlayPauseIcon != null && nativePlayPauseIcon.gameObject.activeSelf;
        }

        private void SetNativeReplayPlaying(bool playing)
        {
            CacheReplayController();
            if (replayController != null && IsNativeReplayPlaying() != playing)
                replayController.TogglePlayPause();
        }

        private void RequestPlayButton()
        {
            frameStepPaused = false;
            if (trimEnabled)
            {
                float cur;
                if (TryGetReplayTime(out cur) && cur < GetPlaybackStart() - 0.01f)
                {
                    restartRequested = true;
                    return;
                }
            }
            if (IsReplayFinished())
            {
                restartRequested = true;
                return;
            }

            if (overridePlayback)
                pendingPlayToggle = true;
            else
                ClickGameButton("btnPlay");
        }

        private void ClickGameButton(string name)
        {
            CacheReplayController();
            if (name == "btnPlay")
            {
                frameStepPaused = false;
                if (overridePlayback)
                {
                    isPlaying = !isPlaying;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Override play/pause, now isPlaying={isPlaying}");
                }
                else if (replayController != null)
                {
                    replayController.TogglePlayPause();
                    isPlaying = !isPlaying;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Toggled play/pause, now isPlaying={isPlaying}");
                }
            }
            else if (name == "btnRewind")
            {
                frameStepPaused = false;
                float next = StepPlaybackSpeed(-1);
                SetPlaybackSpeed(next);
                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Rewind speed set to {playbackSpeed}x");
            }
            else if (name == "btnFastForward")
            {
                frameStepPaused = false;
                float next = StepPlaybackSpeed(1);
                SetPlaybackSpeed(next);
                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] FF speed set to {playbackSpeed}x");
            }
        }

        private static readonly float[] PLAYBACK_SPEED_LADDER = new float[]
        {
            -4f, -2f, -1f, -0.5f, -0.25f, -0.1f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f
        };

        private float StepPlaybackSpeed(int direction)
        {
            int idx = -1;
            for (int i = 0; i < PLAYBACK_SPEED_LADDER.Length; i++)
                if (Mathf.Approximately(playbackSpeed, PLAYBACK_SPEED_LADDER[i])) { idx = i; break; }
            if (idx < 0)
                idx = System.Array.IndexOf(PLAYBACK_SPEED_LADDER, 1f);
            int next = idx + direction;
            if (next < 0 || next >= PLAYBACK_SPEED_LADDER.Length)
                next = System.Array.IndexOf(PLAYBACK_SPEED_LADDER, 1f);
            return PLAYBACK_SPEED_LADDER[next];
        }

        private bool TryGetReplayTime(out float replayTime)
        {
            replayTime = 0f;

            if (replayController != null)
            {
                try
                {
                    replayTime = replayController.CurrentTime;
                    // The plugin clock is elapsed time from each pilot's first gate.
                    return true;
                }
                catch { }
            }

            if (sliderComponent != null)
            {
                try
                {
                    replayTime = sliderComponent.value;
                    return true;
                }
                catch { }
            }

            return false;
        }

        private float GetEffectiveReplayEnd(float nativeMaximum)
        {
            if (nativeMaximum <= 0f)
                return nativeMaximum;

            float latestFinish = 0f;
            bool hasValidFinish = false;
            foreach (var entry in GhostLinesPlugin.entries)
            {
                if (entry == null)
                    continue;
                if (!entry.built)
                    return nativeMaximum;
                if (entry.finishTime > 0f && !float.IsNaN(entry.finishTime) && !float.IsInfinity(entry.finishTime))
                {
                    latestFinish = Mathf.Max(latestFinish, entry.finishTime);
                    hasValidFinish = true;
                }
            }

            return hasValidFinish ? Mathf.Min(nativeMaximum, latestFinish) : nativeMaximum;
        }

        private float GetLoopReplayEnd(float nativeMaximum)
        {
            if (trimEnabled && trimEnd > 0f)
                return Mathf.Min(nativeMaximum, trimEnd);
            if (spectatedEntry != null && spectatedEntry.built && spectatedEntry.finishTime > 0f &&
                !float.IsNaN(spectatedEntry.finishTime) && !float.IsInfinity(spectatedEntry.finishTime))
                return Mathf.Min(nativeMaximum, spectatedEntry.finishTime);
            return GetEffectiveReplayEnd(nativeMaximum);
        }

        private float GetTrimEnd(float nativeMaximum)
        {
            if (trimEnabled && trimEnd > 0f)
                return Mathf.Min(nativeMaximum, trimEnd);
            return GetEffectiveReplayEnd(nativeMaximum);
        }

        private float GetPlaybackStart()
        {
            return trimEnabled ? trimStart : 0f;
        }

        private void RequestLoopedReplayAtEnd()
        {
            if (!loopReplayEnabled || !isPlaying || restartRequested || restartPlaybackPending)
                return;

            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;

            float loopEnd = GetLoopReplayEnd(GetNativeReplayMaximum(replayTime));
            if (loopEnd > 0f && replayTime >= loopEnd - 0.01f)
            {
                restartRequested = true;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Loop replay restart requested");
            }
        }

        private float GetNativeReplayMaximum(float fallback)
        {
            if (sliderComponent != null)
            {
                try
                {
                    return sliderComponent.maxValue;
                }
                catch { }
            }

            if (replayController != null)
            {
                try
                {
                    return replayController.MaximumTimelineTime;
                }
                catch { }
            }

            return fallback;
        }

        private int GetPilotNumberKey()
        {
            for (int i = 1; i <= 5; i++)
            {
                KeyCode bound = GhostLinesPlugin.GetBinding("specpilot" + i);
                if (Input.GetKeyDown(bound) || Input.GetKeyDown(KeyCode.Keypad1 + i - 1))
                    return i;
            }
            return 0;
        }

        private void StepReplayFrame(int direction)
        {
            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;
            float target = Mathf.Max(0f, replayTime + direction * FRAME_STEP_SECONDS);
            float nativeMaximum = GetNativeReplayMaximum(target);
            target = Mathf.Max(Mathf.Min(target, GetTrimEnd(nativeMaximum)), GetPlaybackStart());
            SetReplayTime(target);
            frameStepPaused = true;
            pipStepTargetTime = target;
            pipForceRenderFrame = Time.frameCount + 12;
            for (int i = 0; i < pipWindows.Count; i++)
                pipWindows[i].rtDirty = true;
        }

        private void HandleFrameStepInput()
        {
            bool stepBack = Input.GetKey(KeyCode.LeftArrow);
            bool stepForward = Input.GetKey(KeyCode.RightArrow);
            int direction = stepBack ? -1 : stepForward ? 1 : 0;
            if (direction == 0)
            {
                frameStepHeldDirection = 0;
                return;
            }

            bool newlyPressed = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow);
            if (newlyPressed || direction != frameStepHeldDirection)
            {
                StepReplayFrame(direction);
                frameStepHeldDirection = direction;
                frameStepNextRepeat = Time.unscaledTime + FRAME_STEP_INITIAL_DELAY;
                return;
            }

            if (Time.unscaledTime >= frameStepNextRepeat)
            {
                StepReplayFrame(direction);
                frameStepNextRepeat = Time.unscaledTime + FRAME_STEP_REPEAT_INTERVAL;
            }
        }

        private void SetReplayTime(float replayTime, bool preservePlaybackSpeed = false)
        {
            float nativeMaximum = GetNativeReplayMaximum(replayTime);
            replayTime = Mathf.Min(replayTime, GetTrimEnd(nativeMaximum));
            replayTime = Mathf.Max(replayTime, GetPlaybackStart());
            overridePlayback = false;
            if (!preservePlaybackSpeed)
                playbackSpeed = 1f;
            isPlaying = false;
            SetNativeReplayPlaying(false);

            if (sliderComponent != null)
            {
                try
                {
                    float min = sliderComponent.minValue;
                    float max = sliderComponent.maxValue;
                    sliderComponent.value = Mathf.Clamp(replayTime, min, max);
                    return;
                }
                catch { }
            }
        }

        private void SeekReplayTimeKeepingPlayback(float replayTime)
        {
            if (sliderComponent != null)
            {
                try
                {
                    float min = sliderComponent.minValue;
                    float max = sliderComponent.maxValue;
                    sliderComponent.value = Mathf.Clamp(replayTime, min, max);
                }
                catch { }
            }

            if (!resumePlaybackAfterSeek)
                return;

            if (!overridePlayback)
                SetNativeReplayPlaying(true);
            isPlaying = true;
        }

        private void CacheAttachToDroneMethod()
        {
            if (flightCamCtrl == null || attachToDroneMethod != null)
                return;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            foreach (var method in flightCamCtrl.GetType().GetMethods(flags))
            {
                if (method.Name != "AttachToDrone")
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1)
                {
                    attachToDroneMethod = method;
                    attachToDroneParameterType = parameters[0].ParameterType;
                    return;
                }
            }
        }

        private void TrySpectatePilot(int index, bool preserveCameraView = false)
        {
            if (index < 0 || index >= GhostLinesPlugin.entries.Count)
                return;

            GhostLineEntry entry = GhostLinesPlugin.entries[index];
            Transform root;
            if (!ghostDroneMap.TryGetValue(entry, out root) || root == null)
            {
                MatchGhostDrones();
                if (!ghostDroneMap.TryGetValue(entry, out root) || root == null)
                {
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Pilot {index + 1} drone is not ready");
                    return;
                }
            }

            if (flightCamCtrl == null)
                TryInitCamera();
            CacheAttachToDroneMethod();
            if (attachToDroneMethod == null || attachToDroneParameterType == null)
            {
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Native drone attach method not found");
                return;
            }

            object drone = null;
            for (Transform current = root; current != null && drone == null; current = current.parent)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    drone = FindNativeDroneData(component);
                    if (drone != null) break;
                }
            }
            if (drone == null)
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    drone = FindNativeDroneData(component);
                    if (drone != null) break;
                }
            }
            if (drone == null)
            {
                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Pilot {index + 1} native drone component not found");
                return;
            }

            bool preserveFreeCamView = preserveCameraView && freeCamActive;
            bool preserveOrbitView = preserveCameraView && customCameraActive && !freeCamActive;
            bool zoomGlideFreeCam = preserveFreeCamView && holdZoomFovCaptured;
            Vector3 savedFreePosition = freeCamPosition;
            float savedFreeYaw = freeCamYaw;
            float savedFreePitch = freeCamPitch;
            float savedOrbitDist = orbitDist;
            float savedOrbitYaw = orbitYaw;
            float savedOrbitPitch = orbitPitch;
            bool savedOrbiting = isOrbiting;

            try
            {
                attachToDroneMethod.Invoke(flightCamCtrl, new object[] { drone });
                spectatedEntry = entry;
                if (holdZoomFovCaptured && freeCamActive)
                {
                    zoomTransitionUntil = Time.unscaledTime + ZOOM_TRANSITION_DURATION;
                    zoomTransitionStartYaw = freeCamYaw;
                    zoomTransitionStartPitch = freeCamPitch;
                }
                if (!zoomGlideFreeCam && (preserveFreeCamView || preserveOrbitView))
                {
                    pendingCameraViewRestore = true;
                    pendingFreeCamViewRestore = preserveFreeCamView;
                    pendingOrbitViewRestore = preserveOrbitView;
                    pendingFreeCamPosition = savedFreePosition;
                    pendingFreeCamYaw = savedFreeYaw;
                    pendingFreeCamPitch = savedFreePitch;
                    pendingOrbitDist = savedOrbitDist;
                    pendingOrbitYaw = savedOrbitYaw;
                    pendingOrbitPitch = savedOrbitPitch;
                    pendingOrbiting = savedOrbiting;
                }
                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Spectating pilot {index + 1}: {entry.pilotName}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GhostLines] Could not spectate pilot {index + 1}: {ex.Message}");
            }
        }

        private object FindNativeDroneData(Component component)
        {
            if (component == null || attachToDroneParameterType == null)
                return null;
            if (attachToDroneParameterType.IsInstanceOfType(component))
                return component;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            System.Type type = component.GetType();

            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType != attachToDroneParameterType)
                    continue;
                try
                {
                    object value = field.GetValue(component);
                    if (value != null) return value;
                }
                catch { }
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (property.PropertyType != attachToDroneParameterType || !property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                try
                {
                    object value = property.GetValue(component, null);
                    if (value != null) return value;
                }
                catch { }
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.ReturnType != attachToDroneParameterType || method.GetParameters().Length != 0)
                    continue;
                if (!method.Name.StartsWith("get_", System.StringComparison.Ordinal)
                    && method.Name.IndexOf("Drone", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    object value = method.Invoke(component, null);
                    if (value != null) return value;
                }
                catch { }
            }
            return null;
        }

        private bool IsReplayFinished()
        {
            if (sliderComponent != null)
            {
                try
                {
                    float cur = sliderComponent.value;
                    float maxV = sliderComponent.maxValue;
                    maxV = GetTrimEnd(maxV);
                    return cur >= maxV - 0.01f;
                }
                catch { }
            }
            if (replayController != null)
            {
                try
                {
                    float cur = replayController.CurrentTime;
                    float maxV = replayController.MaximumTimelineTime;
                    maxV = GetTrimEnd(maxV);
                    return cur >= maxV - 0.01f;
                }
                catch { }
            }
            return false;
        }

        private void EnforceEffectiveReplayEnd()
        {
            float replayTime;
            if (!TryGetReplayTime(out replayTime))
                return;

            float nativeMaximum = GetNativeReplayMaximum(replayTime);
            float replayEnd = GetTrimEnd(nativeMaximum);
            if (replayEnd <= 0f || replayEnd >= nativeMaximum - 0.01f || replayTime < replayEnd - 0.01f)
                return;

            SetReplayTime(replayEnd, true);
        }

        private void FindSliderReflection()
        {
            Transform sliderT = null;
            Transform vlayout = replayControlsObj.transform.Find("VLayout");
            if (vlayout != null)
            {
                Transform timeline = vlayout.Find("Timeline");
                if (timeline != null)
                {
                    Transform slider = timeline.Find("Slider");
                    if (slider != null)
                        sliderT = slider.Find("TimelineSlider");
                }
            }

            if (sliderT == null)
            {
                var all = replayControlsObj.GetComponentsInChildren<Transform>(true);
                foreach (var tr in all)
                    if (tr.name == "TimelineSlider") { sliderT = tr; break; }
            }

            if (sliderT != null)
            {
                sliderComponent = sliderT.GetComponent<Slider>();
                if (sliderComponent != null)
                {
                    if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Found timeline slider");
                    return;
                }
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] TimelineSlider found but it has no Slider");
            }

            // Fallback when the UI path moved: the first Slider under the replay UI.
            sliderComponent = replayControlsObj.GetComponentInChildren<Slider>(true);
            if (ModLog.DebugEnabled)
                Debug.Log(sliderComponent != null
                    ? "[GhostLines] Found slider via sweep on " + sliderComponent.gameObject.name
                    : "[GhostLines] No Slider found in ReplayControlsUI");
        }

        private readonly System.Diagnostics.Stopwatch perfGuiAllSw = new System.Diagnostics.Stopwatch();
        private float perfGuiAllMs;
    }
}
