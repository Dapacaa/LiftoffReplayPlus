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
        private void UpdateHoldZoom()
        {
            Camera camera = Camera.main;
            if (camera == null)
                return;

            bool zoomKeyDown = bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("holdzoom"));
            if (zoomKeyDown)
                holdZoomActive = !holdZoomActive;
            bool zoomHeld = holdZoomActive;
            if (!zoomHeld && !holdZoomFovCaptured)
                return;

            bool fpvView = !freeCamActive && GetCurrentNativeCameraMode() == CAMERA_MODE_FIRST_PERSON;
            if (fpvView)
            {
                if (holdZoomFovCaptured)
                {
                    camera.fieldOfView = holdZoomNormalFov;
                    holdZoomFovCaptured = false;
                }
                return;
            }

            if (zoomHeld && !holdZoomFovCaptured)
            {
                holdZoomNormalFov = camera.fieldOfView;
                holdZoomFovBias = 0f;
                holdZoomSmoothFov = camera.fieldOfView;
                holdZoomFovCaptured = true;
            }
            if (!holdZoomFovCaptured)
                return;

            float baseFov;
            if (zoomHeld && freeCamActive && spectatedEntry != null)
            {
                float droneDist = 0f;
                Vector3 dronePos;
                if (!ghostSmoothedPos.TryGetValue(spectatedEntry, out dronePos))
                    TryGetDronePosition(spectatedEntry, displayReplayTime + predictionLead, out dronePos);
                droneDist = Vector3.Distance(freeCamPosition, dronePos);
                float farFactor = 1f - Mathf.Exp(-droneDist / 8f);
                baseFov = Mathf.Lerp(holdZoomNormalFov, HOLD_ZOOM_MIN_FOV, farFactor);
            }
            else
            {
                baseFov = zoomHeld ? holdZoomNormalFov * HOLD_ZOOM_FOV_SCALE : holdZoomNormalFov;
            }

            if (zoomHeld)
            {
                float scroll = Input.mouseScrollDelta.y;
                if (scroll != 0f)
                {
                    float logRatio = Mathf.Log(HOLD_ZOOM_SCROLL_RATIO);
                    float biasFloor = Mathf.Log(HOLD_ZOOM_EXTRA_MIN_FOV / baseFov) / logRatio * HOLD_ZOOM_SCROLL_STEP;
                    float biasCeil = Mathf.Log(holdZoomNormalFov / baseFov) / logRatio * HOLD_ZOOM_SCROLL_STEP;
                    holdZoomFovBias = Mathf.Clamp(
                        holdZoomFovBias - scroll * HOLD_ZOOM_SCROLL_STEP,
                        Mathf.Min(biasFloor, biasCeil),
                        Mathf.Max(biasFloor, biasCeil));
                }
            }

            float scrollFactor = Mathf.Pow(HOLD_ZOOM_SCROLL_RATIO, holdZoomFovBias / HOLD_ZOOM_SCROLL_STEP);
            float targetFov;
            if (zoomHeld && freeCamActive && spectatedEntry != null)
            {
                targetFov = Mathf.Clamp(baseFov * scrollFactor, HOLD_ZOOM_EXTRA_MIN_FOV, holdZoomNormalFov);
            }
            else
            {
                targetFov = zoomHeld
                    ? Mathf.Clamp(baseFov * scrollFactor, HOLD_ZOOM_EXTRA_MIN_FOV, holdZoomNormalFov)
                    : holdZoomNormalFov;
            }
            float smoothK = 1f - Mathf.Exp(-Time.unscaledDeltaTime / HOLD_ZOOM_SMOOTH_TAU);
            holdZoomSmoothFov = Mathf.Lerp(holdZoomSmoothFov, targetFov, smoothK);
            camera.fieldOfView = holdZoomSmoothFov;
            holdZoomCurrentFov = holdZoomSmoothFov;

            if (!zoomHeld && Mathf.Abs(camera.fieldOfView - holdZoomNormalFov) < 0.5f)
            {
                camera.fieldOfView = holdZoomNormalFov;
                holdZoomFovCaptured = false;
            }
        }

        private void UpdateFreeCamFocus(Vector3 pilotPos)
        {
            Vector3 toPilot = pilotPos - freeCamPosition;
            float dist = toPilot.magnitude;
            if (dist < 0.0001f)
                return;
            float targetYaw = Mathf.Atan2(toPilot.x, toPilot.z) * Mathf.Rad2Deg;
            float targetPitch = -Mathf.Asin(Mathf.Clamp(toPilot.y / dist, -1f, 1f)) * Mathf.Rad2Deg;
            if (Time.unscaledTime < zoomTransitionUntil)
            {
                float remaining = Mathf.Clamp01((zoomTransitionUntil - Time.unscaledTime) / ZOOM_TRANSITION_DURATION);
                float eased = 1f - remaining * remaining;
                freeCamYaw = Mathf.LerpAngle(zoomTransitionStartYaw, targetYaw, eased);
                freeCamPitch = Mathf.Lerp(zoomTransitionStartPitch, targetPitch, eased);
            }
            else
            {
                freeCamYaw = targetYaw;
                freeCamPitch = targetPitch;
            }
        }

        private void RestoreHoldZoom()
        {
            holdZoomActive = false;
            if (!holdZoomFovCaptured)
                return;

            if (Camera.main != null)
                Camera.main.fieldOfView = holdZoomNormalFov;
            holdZoomFovCaptured = false;
        }

        private void DumpCameraInfo()
        {
            try
            {
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] === Camera Dump ===");
                var cams = GameObject.FindObjectsOfType<Camera>(true);
                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Found {cams.Length} cameras");
                foreach (var cam in cams)
                {
                    if (cam == null) continue;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines]   Camera: {cam.name} en={cam.enabled} ms={cam.allowMSAA} ortho={cam.orthographic}");
                    Transform p = cam.transform;
                    if (p == null) { Debug.Log("[GhostLines]     Path: <null transform>"); continue; }
                    string path = p.name;
                    while (p.parent != null) { p = p.parent; if (p == null) break; path = p.name + "/" + path; }
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines]     Path: {path}");
                    foreach (var comp in cam.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var t = comp.GetType();
                        string cn = t.Name;
                        if (cn.IndexOf("MouseOrbit", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Orbit", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("ThirdPerson", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Playback", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Flight", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Zoom", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("Follow", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || cn.IndexOf("LineOfSight", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (ModLog.DebugEnabled) Debug.Log($"[GhostLines]     COMP: {cn}");
                            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                object v = null;
                                try { v = f.GetValue(comp); } catch { v = "?"; }
                                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines]       FIELD: {f.Name} ({f.FieldType.Name}) = {v}");
                            }
                            foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                object v = null;
                                if (prop.CanRead) try { v = prop.GetValue(comp, null); } catch { v = "?"; }
                                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines]       PROP: {prop.Name} ({prop.PropertyType.Name}) r={prop.CanRead} w={prop.CanWrite} = {v}");
                            }
                        }
                    }
                }

                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] === End Camera Dump ===");
            }
            catch (System.Exception e)
            {
                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines][DIAG] DumpCameraInfo exception: {e}");
            }
        }

        private void CacheFlightCamModeAccessors()
        {
            const System.Reflection.BindingFlags propertyFlags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            try
            {
                if (flightCamCtrl == null) return;
                object impl = GetFlightCamImpl();
                if (impl == null) return;
                foreach (var m in impl.GetType().GetMethods(propertyFlags))
                {
                    if (m.ReturnType != typeof(void)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType.Name == "CameraMode" && ps[1].ParameterType == typeof(bool))
                    {
                        cameraModeSetMethod = m;
                        cameraModeEnumType = ps[0].ParameterType;
                        break;
                    }
                }
            }
            catch { }
        }

        private object GetFlightCamImpl()
        {
            try
            {
                if (flightCamCtrl == null) return null;
                if (flightCamImplFieldInfo == null)
                {
                    flightCamImplFieldInfo = flightCamCtrl.GetType().GetField("activeImplementation",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                }
                if (flightCamImplFieldInfo == null) return null;
                flightCamImpl = flightCamImplFieldInfo.GetValue(flightCamCtrl);
                return flightCamImpl;
            }
            catch { return null; }
        }

        private int GetCurrentNativeCameraMode()
        {
            try
            {
                object impl = GetFlightCamImpl();
                if (impl == null) return 0;
                if (currentCameraModePropInfo == null)
                {
                    currentCameraModePropInfo = impl.GetType().GetProperty("CurrentCameraMode",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                if (currentCameraModePropInfo == null) return 0;
                object v = currentCameraModePropInfo.GetValue(impl, null);
                return v == null ? 0 : System.Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        private void CaptureReplayEntryCameraMode()
        {
            if (replayEntryCameraMode != 0)
                return;

            TryInitCamera();
            replayEntryCameraMode = GetCurrentNativeCameraMode();
        }

        private void SetNativeCameraMode(int mode, bool instant)
        {
            try
            {
                if (cameraModeSetMethod == null || cameraModeEnumType == null) return;
                object impl = GetFlightCamImpl();
                if (impl == null) return;
                object modeVal = System.Enum.ToObject(cameraModeEnumType, mode);
                cameraModeSetMethod.Invoke(impl, new object[] { modeVal, instant });
            }
            catch { }
        }

        private void TryInitCamera()
        {
            if (orbitTarget == null && Camera.main != null)
            {
                var ctrl = Camera.main.GetComponent<FlightCameraController>();
                if (ctrl != null)
                {
                    flightCamCtrl = ctrl;
                    CacheFlightCamModeAccessors();
                    orbitTarget = ctrl.StuntCameraReferenceTransform;
                    if (orbitTarget != null)
                    {
                        Vector3 diff = Camera.main.transform.position - orbitTarget.position;
                        orbitDist = Mathf.Max(diff.magnitude, 3f);
                        if (diff.sqrMagnitude < 0.0001f)
                            diff = Vector3.back * orbitDist;
                        orbitYaw = Mathf.Atan2(diff.x, diff.z) * Mathf.Rad2Deg;
                        orbitPitch = -Mathf.Asin(diff.y / orbitDist) * Mathf.Rad2Deg;
                        if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Orbit target={orbitTarget.name} dist={orbitDist:F1} yaw={orbitYaw:F1} pitch={orbitPitch:F1}");
                    }
                }
            }
        }

        private void BeginCameraLookDrag()
        {
            cameraLookDragActive = true;
            cameraLookSampleValid = true;
            lastCameraLookMouse = Input.mousePosition;
            cameraLookScreenWidth = Screen.width;
            cameraLookScreenHeight = Screen.height;
        }

        private void CancelCameraLook()
        {
            cameraLookDragActive = false;
            cameraLookSampleValid = false;
            cameraLookScreenWidth = -1;
            cameraLookScreenHeight = -1;
        }

        private Vector2 ReadCameraLookDelta()
        {
            if (!cameraLookDragActive || !cameraLookSampleValid || !Application.isFocused)
            {
                CancelCameraLook();
                return Vector2.zero;
            }

            if (cameraLookScreenWidth != Screen.width || cameraLookScreenHeight != Screen.height)
            {
                CancelCameraLook();
                return Vector2.zero;
            }

            Vector2 current = Input.mousePosition;
            Vector2 delta = current - lastCameraLookMouse;
            lastCameraLookMouse = current;

            Vector2 rawDelta;
            if (TryGetRawCameraLookDelta(out rawDelta))
            {
                if (Mathf.Abs(delta.x) > 0.5f && Mathf.Abs(rawDelta.x) > 0.0001f)
                {
                    float measuredScale = Mathf.Clamp(Mathf.Abs(delta.x / rawDelta.x), 0.05f, 200f);
                    cameraRawMouseScaleX = Mathf.Lerp(cameraRawMouseScaleX, measuredScale, 0.2f);
                }
                if (Mathf.Abs(delta.y) > 0.5f && Mathf.Abs(rawDelta.y) > 0.0001f)
                {
                    float measuredScale = Mathf.Clamp(Mathf.Abs(delta.y / rawDelta.y), 0.05f, 200f);
                    cameraRawMouseScaleY = Mathf.Lerp(cameraRawMouseScaleY, measuredScale, 0.2f);
                }

                // At the desktop edge absolute coordinates stop changing, so use only
                // the physical mouse-axis movement for that frozen component.
                if (Mathf.Abs(delta.x) < 0.01f && Mathf.Abs(rawDelta.x) > 0.0001f)
                    delta.x = rawDelta.x * cameraRawMouseScaleX;
                if (Mathf.Abs(delta.y) < 0.01f && Mathf.Abs(rawDelta.y) > 0.0001f)
                    delta.y = rawDelta.y * cameraRawMouseScaleY;
            }
            return delta;
        }

        private bool TryGetRawCameraLookDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (cameraRawMouseAxesState < 0)
                return false;

            try
            {
                delta.x = Input.GetAxisRaw("Mouse X");
                delta.y = Input.GetAxisRaw("Mouse Y");
                cameraRawMouseAxesState = 1;
                return true;
            }
            catch
            {
                // Liftoff can use a custom Input Manager; do not retry missing axes every frame.
                cameraRawMouseAxesState = -1;
                return false;
            }
        }

        private void EnterFreeCam()
        {
            if (Camera.main == null) return;
            Transform cam = Camera.main.transform;
            freeCamActive = true;
            // The hold timer isn't updated while freecam is active; clear it so
            // leaving freecam with a movement key still held doesn't re-enter at once.
            freeCamEntryHoldStart = -1f;
            customCameraActive = false;
            isOrbiting = false;
            CancelCameraLook();
            freeCamPosition = cam.position;
            freeCamCurrentSpeed = FREE_CAM_SPEED;

            Vector3 euler = cam.rotation.eulerAngles;
            freeCamYaw = euler.y;
            freeCamPitch = euler.x;
            if (freeCamPitch > 180f) freeCamPitch -= 360f;

            if (lastNativeCameraMode == 0)
                lastNativeCameraMode = GetCurrentNativeCameraMode();
            SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
            if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Free cam enabled at pos={freeCamPosition} yaw={freeCamYaw:F1} pitch={freeCamPitch:F1}");
        }

        // Whether the camera key (V) belongs to the mod this frame, so the game's own
        // camera cycle stays out of it (see CameraViewToggle).
        internal bool ModWantsCameraKey()
        {
            if (bindingListeningFor != null || Time.frameCount == bindingCaptureSuppressFrame)
                return false;
            if (!Input.GetKeyDown(GhostLinesPlugin.GetBinding("camreset")))
                return false;
            if (replayReady)
                return freeCamActive || customCameraActive;
            return mpFreeCamActive || mpZoomActive;
        }

        private void ResetToDroneCamera()
        {
            RestoreHoldZoom();
            if (pendingCameraModeRestore == 0 && lastNativeCameraMode != 0)
                pendingCameraModeRestore = lastNativeCameraMode;
            freeCamActive = false;
            customCameraActive = false;
            isOrbiting = false;
            CancelCameraLook();
            startedFromFpv = false;
            lastNativeCameraMode = 0;
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Camera reset to drone");
        }

        private void HandlePlayheadDragCamera()
        {
            Vector3 keyMove = FreeCamMove();
            bool keys = keyMove != Vector3.zero;
            playheadDragCameraActive = (isDraggingHandle || draggingScrubber) && keys &&
                bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame;
            if (!playheadDragCameraActive)
                return;
            if (!freeCamActive)
                EnterFreeCam();
            if (Camera.main == null)
                return;

            Vector3 move = keyMove;

            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float targetSpeed = shiftHeld ? FREE_CAM_SHIFT_MAX_SPEED : FREE_CAM_SPEED;
            freeCamCurrentSpeed = Mathf.MoveTowards(
                freeCamCurrentSpeed,
                targetSpeed,
                (shiftHeld ? FREE_CAM_SHIFT_ACCELERATION : FREE_CAM_SHIFT_DECELERATION) * Time.unscaledDeltaTime);

            if (move.sqrMagnitude > 0.0001f)
            {
                Quaternion rot = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                freeCamPosition += rot * move.normalized * freeCamCurrentSpeed * Time.unscaledDeltaTime;
            }
        }
        private void HandleFreeCamInput()
        {
            if (Camera.main == null)
                return;

            if (Input.GetMouseButtonDown(2))
            {
                ResetToDroneCamera();
                return;
            }

            bool focusHeld = holdZoomActive && spectatedEntry != null;

            if (focusHeld && Input.GetMouseButtonDown(1))
            {
                RestoreHoldZoom();
                focusHeld = false;
            }

            if (focusHeld)
            {
                CancelCameraLook();
            }
            else
            {
                if (Input.GetMouseButtonDown(1) || (Input.GetMouseButton(1) && !cameraLookDragActive))
                    BeginCameraLookDrag();
                if (Input.GetMouseButton(1))
                {
                    Vector2 delta = ReadCameraLookDelta();

                    freeCamYaw += delta.x * FREE_CAM_MOUSE_SENSITIVITY;
                    freeCamPitch -= delta.y * FREE_CAM_MOUSE_SENSITIVITY;
                    freeCamPitch = Mathf.Clamp(freeCamPitch, -85f, 85f);
                }
            }

            if (Input.GetMouseButtonUp(1))
                CancelCameraLook();

            Vector3 move = Vector3.zero;
            if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && !playheadDragCameraActive && !IsChatTyping())
            {
            move = FreeCamMove();
            }

            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float targetSpeed = shiftHeld ? FREE_CAM_SHIFT_MAX_SPEED : FREE_CAM_SPEED;
            freeCamCurrentSpeed = Mathf.MoveTowards(
                freeCamCurrentSpeed,
                targetSpeed,
                (shiftHeld ? FREE_CAM_SHIFT_ACCELERATION : FREE_CAM_SHIFT_DECELERATION) * Time.unscaledDeltaTime);

            if (move.sqrMagnitude > 0.0001f)
            {
                Quaternion rot = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                freeCamPosition += rot * move.normalized * freeCamCurrentSpeed * Time.unscaledDeltaTime;
            }
        }

        public bool CursorNeededByPlugin()
        {
            if (!Application.isFocused)
                return true;

            bool searchActive = leaderboardSearchInput != null && leaderboardSearchInput.gameObject.activeInHierarchy &&
                (leaderboardSearchInput.isFocused || leaderboardSearchFocusLock);
            if (bindingListeningFor != null || searchActive ||
                IsDroneLabelInteractionBlocked(Input.mousePosition))
                return true;
            if (multiplayerPipMenuOpen || mpZoomActive || mpMenuDrag.Dragging)
                return true;
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w != null && (w.dragging || w.resizing || w.dragCandidate || w.dropdownOpen))
                    return true;
            }
            return false;
        }

        private int droneMarkersRequestedFrame = -1;
        private int droneMarkersWorkFrame = -1;
    }
}
