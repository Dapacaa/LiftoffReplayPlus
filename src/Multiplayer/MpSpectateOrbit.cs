using UnityEngine;
using Debug = LiftoffGhostLines.ModLog;
using static LiftoffGhostLines.GhostLinesPlugin;

namespace LiftoffGhostLines
{
    // Orbit camera for spectating in multiplayer: in the game's third-person view
    // of another pilot, right-drag orbits around their drone and the wheel zooms,
    // like the replay orbit. Middle-click hands the camera back to the game's
    // chase view. The angle and distance are kept across pilot switches and V
    // toggles (world-fixed, so "where you left it"). Never while flying your own
    // drone, in MP freecam or in MP zoom.
    public partial class GhostLinePoller
    {
        private bool mpOrbitActive;
        private bool mpOrbitDragging;
        private float mpOrbitYaw;
        private float mpOrbitPitch;
        private float mpOrbitDist = 4f;
        private Transform mpOrbitPivot;          // spectated drone root this frame
        private FlightCameraController mpOrbitCamCtrl;
        private Camera mpOrbitCamCtrlCam;
        private Transform mpOrbitMineRoot;       // cache for the ownership check
        private bool mpOrbitMineIsLocal;

        private const float MP_ORBIT_MIN_DIST = 0.8f;
        private const float MP_ORBIT_MAX_DIST = 60f;

        // The spectated drone's root when spectating another pilot in third
        // person, else null.
        private Transform GetMpSpectatePivot()
        {
            Camera cam = MainCameraCached();
            if (cam == null)
                return null;
            if (mpOrbitCamCtrlCam != cam)
            {
                mpOrbitCamCtrlCam = cam;
                mpOrbitCamCtrl = cam.GetComponent<FlightCameraController>();
            }
            if (mpOrbitCamCtrl == null)
                return null;
            FlightCameraController.AttachedDroneData data = null;
            bool thirdPerson = false;
            try
            {
                thirdPerson = mpOrbitCamCtrl.InThirdPerson;
                data = mpOrbitCamCtrl.TrackingDroneData;
            }
            catch { return null; }
            if (!thirdPerson || data == null || data.root == null)
                return null;
            if (mpOrbitMineRoot != data.root)
            {
                mpOrbitMineRoot = data.root;
                Photon.Pun.PhotonView view = data.root.GetComponentInParent<Photon.Pun.PhotonView>();
                mpOrbitMineIsLocal = view == null || view.IsMine;
            }
            return mpOrbitMineIsLocal ? null : data.root;
        }

        // Multiplayer camera not in first person on a drone: MP freecam, the game's
        // third person, or the spectate orbit. Used to hide the HUD and to skip the
        // game's black flash on spectated resets.
        internal bool CameraAwayFromMpDrone()
        {
            if (replayReady || !PhotonInRoom())
                return false;
            if (mpFreeCamActive || (mpOrbitActive && mpOrbitPivot != null))
                return true;
            try { return mpOrbitCamCtrl != null && mpOrbitCamCtrl.InThirdPerson; }
            catch { return false; }
        }

        private bool MouseOverAnyModUi()
        {
            Vector2 p = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            for (int l = 0; l <= UI_LAYER_MPPIP; l++)
                if (IsUiLayerAt(l, p))
                    return true;
            return IsUiBlockedFor(-1, p);
        }

        // Update: input. Called from UpdateMultiplayerFreeCam's frame.
        private void UpdateMpSpectateOrbit(bool keysOk)
        {
            mpOrbitPivot = null;
            if (replayReady || mpFreeCamActive || mpZoomActive)
            {
                EndMpOrbitDrag();
                return;
            }
            Transform pivot = GetMpSpectatePivot();
            if (pivot == null)
            {
                EndMpOrbitDrag();
                return;
            }
            mpOrbitPivot = pivot;
            Camera cam = MainCameraCached();
            if (cam == null || !keysOk || IsChatTyping())
                return;

            bool overUi = MouseOverAnyModUi();
            if (Input.GetMouseButtonDown(1) && !overUi)
            {
                if (!mpOrbitActive)
                    SeedMpOrbitFromCamera(cam, pivot.position);
                mpOrbitActive = true;
                mpOrbitDragging = true;
                BeginCameraLookDrag();
            }
            if (mpOrbitDragging && Input.GetMouseButton(1))
            {
                if (!cameraLookDragActive)
                    BeginCameraLookDrag();
                Vector2 delta = ReadCameraLookDelta();
                mpOrbitYaw += delta.x * FREE_CAM_MOUSE_SENSITIVITY;
                mpOrbitPitch = Mathf.Clamp(mpOrbitPitch - delta.y * FREE_CAM_MOUSE_SENSITIVITY, -85f, 85f);
            }
            if (Input.GetMouseButtonUp(1))
                EndMpOrbitDrag();

            float scroll = overUi ? 0f : Input.mouseScrollDelta.y;
            if (scroll != 0f)
            {
                if (!mpOrbitActive)
                {
                    SeedMpOrbitFromCamera(cam, pivot.position);
                    mpOrbitActive = true;
                }
                mpOrbitDist = Mathf.Clamp(mpOrbitDist - scroll * 0.6f, MP_ORBIT_MIN_DIST, MP_ORBIT_MAX_DIST);
            }

            if (Input.GetMouseButtonDown(2) && mpOrbitActive && !overUi)
            {
                mpOrbitActive = false;
                EndMpOrbitDrag();
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] MP spectate orbit off (middle click)");
            }
        }

        private void SeedMpOrbitFromCamera(Camera cam, Vector3 pivot)
        {
            Vector3 diff = cam.transform.position - pivot;
            if (diff.sqrMagnitude < 0.0001f)
                diff = Vector3.back * 3f;
            mpOrbitDist = Mathf.Clamp(diff.magnitude, MP_ORBIT_MIN_DIST, MP_ORBIT_MAX_DIST);
            Vector3 euler = Quaternion.LookRotation(-diff.normalized).eulerAngles;
            mpOrbitYaw = euler.y;
            mpOrbitPitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        private void EndMpOrbitDrag()
        {
            if (!mpOrbitDragging)
                return;
            mpOrbitDragging = false;
            CancelCameraLook();
        }

        // Pre-render: place the camera after the game's own camera update.
        private void ApplyMpSpectateOrbit(Camera cam)
        {
            if (!mpOrbitActive || mpOrbitPivot == null || mpFreeCamActive)
                return;
            Vector3 pivot = mpOrbitPivot.position;
            Quaternion rot = Quaternion.Euler(mpOrbitPitch, mpOrbitYaw, 0f);
            Vector3 pos = pivot + rot * (Vector3.back * mpOrbitDist);
            Vector3 look = pivot - pos;
            cam.transform.SetPositionAndRotation(pos,
                look.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(look, Vector3.up) : rot);
        }
    }
}
