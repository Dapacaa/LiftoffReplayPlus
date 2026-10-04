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
        private void EnterMultiplayerFreeCam()
        {
            if (Camera.main == null || mpFreeCamActive)
                return;
            mpFreeCamActive = true;
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] FC enter poller=" + GetInstanceID() + " freecam=" + mpFreeCamActive);
            Transform cam = Camera.main.transform;
            mpFreeCamPosition = cam.position;
            mpFreeCamCurrentSpeed = FREE_CAM_SPEED;
            Vector3 euler = cam.rotation.eulerAngles;
            mpFreeCamYaw = euler.y;
            mpFreeCamPitch = euler.x;
            if (mpFreeCamPitch > 180f) mpFreeCamPitch -= 360f;
            // First person hides parts of the tracked drone (frame, props) from
            // its own camera; seen from the freecam only the battery etc. was
            // left. Switch the game to third person while freecam is on, like
            // replay freecam does, and put first person back on exit.
            mpFreeCamRestoreMode = 0;
            if (EnsureMpFlightCamAccessors() && GetCurrentNativeCameraMode() == CAMERA_MODE_FIRST_PERSON)
            {
                mpFreeCamRestoreMode = CAMERA_MODE_FIRST_PERSON;
                SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
            }
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP free cam enabled");
        }

        // Camera mode to restore when MP freecam ends (0 = leave it alone).
        private int mpFreeCamRestoreMode = 0;

        // The camera-mode helpers go through flightCamCtrl, which only the replay
        // sets up; in a room take it from the main camera.
        private bool EnsureMpFlightCamAccessors()
        {
            if (flightCamCtrl == null)
            {
                Camera cam = MainCameraCached();
                FlightCameraController ctrl = cam != null ? cam.GetComponent<FlightCameraController>() : null;
                if (ctrl == null)
                    return false;
                flightCamCtrl = ctrl;
            }
            if (cameraModeSetMethod == null)
                CacheFlightCamModeAccessors();
            return cameraModeSetMethod != null;
        }

        private void ExitMultiplayerFreeCam()
        {
            if (!mpFreeCamActive)
                return;
            mpFreeCamActive = false;
            mpZoomActive = false;
            if (mpZoomFovCaptured && Camera.main != null)
                Camera.main.fieldOfView = mpZoomNormalFov;
            mpZoomFovCaptured = false;
            if (mpFreeCamRestoreMode != 0)
            {
                if (EnsureMpFlightCamAccessors())
                    SetNativeCameraMode(mpFreeCamRestoreMode, true);
                mpFreeCamRestoreMode = 0;
            }
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP free cam disabled");
        }

        private MultiplayerPipTarget GetMpZoomSortedAt(int index)
        {
            if (multiplayerPipTargets.Count == 0)
                return null;
            if (mpZoomSortedVersion != multiplayerPipScanVersion || mpZoomSorted.Count != multiplayerPipTargets.Count)
            {
                mpZoomSorted.Clear();
                mpZoomSorted.AddRange(multiplayerPipTargets);
                mpZoomSorted.Sort((a, b) => string.Compare(a.playerName, b.playerName, System.StringComparison.OrdinalIgnoreCase));
                mpZoomSortedVersion = multiplayerPipScanVersion;
            }
            if (index < 0 || index >= mpZoomSorted.Count)
                return null;
            return mpZoomSorted[index];
        }

        // Zoom starts on the pilot the game camera is spectating (it keeps tracking
        // them under MP freecam). Leaves the current choice when spectating yourself
        // or when that drone isn't among the targets.
        private void SelectSpectatedPilotForZoom()
        {
            try
            {
                Camera cam = MainCameraCached();
                FlightCameraController ctrl = cam != null ? cam.GetComponent<FlightCameraController>() : null;
                FlightCameraController.AttachedDroneData data = ctrl != null ? ctrl.TrackingDroneData : null;
                Transform tracked = data != null ? data.root : null;
                if (tracked == null)
                    return;
                for (int i = 0; ; i++)
                {
                    MultiplayerPipTarget t = GetMpZoomSortedAt(i);
                    if (t == null)
                        break;
                    if (t.root != null && (tracked == t.root || tracked.IsChildOf(t.root) || t.root.IsChildOf(tracked)))
                    {
                        mpZoomPilotIndex = i;
                        return;
                    }
                }
            }
            catch { }
        }

        private MultiplayerPipTarget GetMpZoomTarget()
        {
            if (multiplayerPipTargets.Count == 0)
                return null;
            if (mpZoomPilotIndex < 0)
                mpZoomPilotIndex = 0;
            if (mpZoomPilotIndex >= multiplayerPipTargets.Count)
                mpZoomPilotIndex = multiplayerPipTargets.Count - 1;
            return GetMpZoomSortedAt(mpZoomPilotIndex);
        }

        private void UpdateMpHoldZoom()
        {
            if (!mpZoomActive && !mpZoomFovCaptured)
                return;
            if (!mpZoomActive)
                mpZoomTrackValid = false;
            Camera camera = Camera.main;
            if (camera == null)
                return;
            if (mpZoomActive && !mpZoomFovCaptured)
            {
                mpZoomNormalFov = camera.fieldOfView;
                mpZoomFovBias = 0f;
                mpZoomSmoothFov = camera.fieldOfView;
                mpZoomFovCaptured = true;
            }
            if (!mpZoomFovCaptured)
                return;
            float baseFov;
            MultiplayerPipTarget spec = GetMpZoomTarget();
            if (mpZoomActive && spec != null)
            {
                Vector3 dronePos = Vector3.zero;
                if (spec.mount != null)
                    dronePos = spec.mount.position;
                else if (spec.root != null)
                    dronePos = spec.root.position;
                if (!mpZoomTrackValid || mpZoomTrackTarget != spec)
                {
                    mpZoomTrackPos = dronePos;
                    mpZoomTrackValid = true;
                    mpZoomTrackTarget = spec;
                }
                else
                {
                    float tk = 1f - Mathf.Exp(-Time.unscaledDeltaTime / MP_ZOOM_TRACK_TAU);
                    mpZoomTrackPos = Vector3.Lerp(mpZoomTrackPos, dronePos, tk);
                }
                float droneDist = Vector3.Distance(mpFreeCamPosition, mpZoomTrackPos);
                float farFactor = 1f - Mathf.Exp(-droneDist / 8f);
                baseFov = Mathf.Lerp(mpZoomNormalFov, HOLD_ZOOM_MIN_FOV, farFactor);
                Vector3 toPilot = mpZoomTrackPos - mpFreeCamPosition;
                float trackDist = toPilot.magnitude;
                if (trackDist > 0.0001f)
                {
                    float targetYaw = Mathf.Atan2(toPilot.x, toPilot.z) * Mathf.Rad2Deg;
                    float targetPitch = -Mathf.Asin(Mathf.Clamp(toPilot.y / trackDist, -1f, 1f)) * Mathf.Rad2Deg;
                    if (Time.unscaledTime < mpZoomTransitionUntil)
                    {
                        float remaining = Mathf.Clamp01((mpZoomTransitionUntil - Time.unscaledTime) / ZOOM_TRANSITION_DURATION);
                        float eased = 1f - remaining * remaining;
                        mpFreeCamYaw = Mathf.LerpAngle(mpZoomTransitionStartYaw, targetYaw, eased);
                        mpFreeCamPitch = Mathf.LerpAngle(mpZoomTransitionStartPitch, targetPitch, eased);
                    }
                    else
                    {
                        mpFreeCamYaw = targetYaw;
                        mpFreeCamPitch = targetPitch;
                    }
                }
            }
            else
            {
                baseFov = mpZoomActive ? mpZoomNormalFov * HOLD_ZOOM_FOV_SCALE : mpZoomNormalFov;
            }
            if (mpZoomActive)
            {
                float scroll = Input.mouseScrollDelta.y;
                if (scroll != 0f)
                {
                    float logRatio = Mathf.Log(HOLD_ZOOM_SCROLL_RATIO);
                    float biasFloor = Mathf.Log(HOLD_ZOOM_EXTRA_MIN_FOV / baseFov) / logRatio * HOLD_ZOOM_SCROLL_STEP;
                    float biasCeil = Mathf.Log(mpZoomNormalFov / baseFov) / logRatio * HOLD_ZOOM_SCROLL_STEP;
                    mpZoomFovBias = Mathf.Clamp(
                        mpZoomFovBias - scroll * HOLD_ZOOM_SCROLL_STEP,
                        Mathf.Min(biasFloor, biasCeil),
                        Mathf.Max(biasFloor, biasCeil));
                }
            }
            float scrollFactor = Mathf.Pow(HOLD_ZOOM_SCROLL_RATIO, mpZoomFovBias / HOLD_ZOOM_SCROLL_STEP);
            float targetFov = mpZoomActive
                ? Mathf.Clamp(baseFov * scrollFactor, HOLD_ZOOM_EXTRA_MIN_FOV, mpZoomNormalFov)
                : mpZoomNormalFov;
            float smoothK = 1f - Mathf.Exp(-Time.unscaledDeltaTime / HOLD_ZOOM_SMOOTH_TAU);
            mpZoomSmoothFov = Mathf.Lerp(mpZoomSmoothFov, targetFov, smoothK);
            camera.fieldOfView = mpZoomSmoothFov;
            mpZoomCurrentFov = mpZoomSmoothFov;
            if (!mpZoomActive && Mathf.Abs(camera.fieldOfView - mpZoomNormalFov) < 0.5f)
            {
                camera.fieldOfView = mpZoomNormalFov;
                mpZoomFovCaptured = false;
            }
        }

        private void UpdateMultiplayerFreeCam()
        {
            // Gate on actually being in a Photon room, not on mpInSession: that flag
            // is driven by finding *other pilots'* drones, so it is false in a solo
            // room — which used to block freecam for players flying alone.
            if (replayReady || !PhotonInRoom())
            {
                if (mpFreeCamActive)
                    ExitMultiplayerFreeCam();
                mpZoomActive = false;
                return;
            }
            bool keysOk = bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame;
            UpdateMpSpectateOrbit(keysOk);
            perfSw.Restart();
            // MP freecam is an explicit toggle: while it is active the input gate cuts
            // the live drone's controls, so it must never start from a movement-key hold.
            KeyCode mpFreeCamKey = GhostLinesPlugin.GetBinding("mpfreecam");
            bool toggleRequested = keysOk && !IsChatTyping() && mpFreeCamKey != KeyCode.None &&
                Input.GetKeyDown(mpFreeCamKey);
            perfFcKeysMs += (float)perfSw.Elapsed.TotalMilliseconds;
            perfSw.Restart();
            UpdateMpHoldZoom();
            perfFcZoomMs += (float)perfSw.Elapsed.TotalMilliseconds;
            if (toggleRequested)
            {
                if (mpFreeCamActive)
                {
                    ExitMultiplayerFreeCam();
                    return;
                }
                EnterMultiplayerFreeCam();
            }
            if ((keysOk && Input.GetKeyDown(GhostLinesPlugin.GetBinding("camreset"))) || Input.GetMouseButtonDown(2))
            {
                if (mpZoomActive || mpFreeCamActive)
                    CameraViewToggle.modUsedKeyFrame = Time.frameCount;
                if (mpZoomActive)
                {
                    mpZoomActive = false;
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP zoom off");
                }
                if (mpFreeCamActive)
                {
                    ExitMultiplayerFreeCam();
                    return;
                }
                if (!mpFreeCamActive)
                    return;
            }
            if (mpFreeCamActive && keysOk && Input.GetKeyDown(GhostLinesPlugin.GetBinding("holdzoom")))
            {
                mpZoomActive = !mpZoomActive;
                if (mpZoomActive)
                {
                    SelectSpectatedPilotForZoom();
                    mpZoomTransitionUntil = Time.unscaledTime + ZOOM_TRANSITION_DURATION;
                    mpZoomTransitionStartYaw = mpFreeCamYaw;
                    mpZoomTransitionStartPitch = mpFreeCamPitch;
                    MultiplayerPipTarget spec = GetMpZoomTarget();
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP zoom on target=" + (spec != null ? spec.playerName : "?"));
                }
                else
                    ModLog.LogFile("[GhostLines] MPPIP zoom off");
            }
            if (mpZoomActive)
            {
                for (int sel = 1; sel <= 8; sel++)
                {
                    if (!keysOk)
                        break;
                    if (Input.GetKeyDown((KeyCode)(KeyCode.Alpha0 + sel)) ||
                        Input.GetKeyDown((KeyCode)(KeyCode.Keypad0 + sel)))
                    {
                        if (GetMpZoomSortedAt(sel - 1) != null)
                        {
                            mpZoomPilotIndex = sel - 1;
                            MultiplayerPipTarget spec = GetMpZoomTarget();
                            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP spec pilot=" + sel + " name=" + (spec != null ? spec.playerName : "?"));
                        }
                        break;
                    }
                }
            }
            if (!mpFreeCamActive)
                return;
            if (Camera.main == null)
                return;
            if (!mpZoomActive && (Input.GetMouseButtonDown(1) || (Input.GetMouseButton(1) && !cameraLookDragActive)))
                BeginCameraLookDrag();
            if (!mpZoomActive && Input.GetMouseButton(1))
            {
                Vector2 delta = ReadCameraLookDelta();
                mpFreeCamYaw += delta.x * FREE_CAM_MOUSE_SENSITIVITY;
                mpFreeCamPitch -= delta.y * FREE_CAM_MOUSE_SENSITIVITY;
                mpFreeCamPitch = Mathf.Clamp(mpFreeCamPitch, -85f, 85f);
            }
            if (Input.GetMouseButtonUp(1))
                CancelCameraLook();
            Vector3 move = Vector3.zero;
            if (keysOk && !IsChatTyping())
            {
                move += FreeCamMove();
            }
            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float targetSpeed = shiftHeld ? FREE_CAM_SHIFT_MAX_SPEED : FREE_CAM_SPEED;
            mpFreeCamCurrentSpeed = Mathf.MoveTowards(
                mpFreeCamCurrentSpeed,
                targetSpeed,
                (shiftHeld ? FREE_CAM_SHIFT_ACCELERATION : FREE_CAM_SHIFT_DECELERATION) * Time.unscaledDeltaTime);
            if (move.sqrMagnitude > 0.0001f)
            {
                Quaternion rot = Quaternion.Euler(mpFreeCamPitch, mpFreeCamYaw, 0f);
                mpFreeCamPosition += rot * move.normalized * mpFreeCamCurrentSpeed * Time.unscaledDeltaTime;
            }
        }

        private InGamePlayerActivityIndicator[] mpUiIndicatorCache = null;
        private float mpUiIndicatorCacheAt = 0f;
    }
}
