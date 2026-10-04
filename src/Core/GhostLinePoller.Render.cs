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
        private Camera MainCameraCached()
        {
            if (Time.frameCount != mainCamFrame)
            {
                mainCamFrame = Time.frameCount;
                mainCamCache = Camera.main;
            }
            return mainCamCache;
        }

        private void OnCameraPostRender(Camera cam)
        {
            cameraCbSw.Restart();
            Prof.Span ps = Prof.Begin();
            try { OnCameraPostRenderImpl(cam); }
            finally { perfPostCullMs += (float)cameraCbSw.Elapsed.TotalMilliseconds; Prof.End(Prof.PostRender, ps); }
        }

        private void OnCameraPostRenderImpl(Camera cam)
        {
            if (!replayReady || !xrayEnabled || cam != MainCameraCached())
                return;
            EnsureXrayMaterials();
            if (xrayWireMaterial == null)
                return;
            DrawXrayMap();
            DrawXrayDrones(cam);
        }

        private void OnCameraPreRender(Camera cam)
        {
            cameraCbSw.Restart();
            Prof.Span ps = Prof.Begin();
            try { OnCameraPreRenderImpl(cam); }
            finally { perfPreCullMs += (float)cameraCbSw.Elapsed.TotalMilliseconds; Prof.End(Prof.PreCull, ps); }
        }

        private void OnCameraPreRenderImpl(Camera cam)
        {
            if (cam != MainCameraCached()) return;
            if (!replayReady)
            {
                if (mpFreeCamActive)
                {
                    cam.transform.position = mpFreeCamPosition;
                    cam.transform.rotation = Quaternion.Euler(mpFreeCamPitch, mpFreeCamYaw, 0f);
                    if (mpZoomFovCaptured)
                        cam.fieldOfView = mpZoomCurrentFov;
                }
                else
                    ApplyMpSpectateOrbit(cam);
                return;
            }

            // Native clock: only used to match an unmapped entry to the native
            // root below (TryGetDroneTransformUnshifted). Poses use the snapshot.
            float rt = stableReplayTime;

            Vector3 nativeCameraPosition = cam.transform.position;
            Quaternion nativeCameraRotation = cam.transform.rotation;
            Transform nativeRoot;
            Transform nativeCameraMount;
            TryGetNativeTrackedTransforms(out nativeRoot, out nativeCameraMount);
            Vector3 nativeRootPosition = nativeRoot != null
                ? nativeRoot.position
                : orbitTarget != null ? orbitTarget.position : nativeCameraPosition;

            ApplyGhostDroneTransforms();
            if (freeCamActive)
            {
                cam.transform.position = freeCamPosition;
                cam.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                if (holdZoomFovCaptured)
                    cam.fieldOfView = holdZoomCurrentFov;

                GhostLineEntry freeCamTarget = spectatedEntry;
                if (freeCamTarget == null && nativeRoot != null)
                    freeCamTarget = FindEntryForNativeRoot(nativeRoot);

                Vector3 freeCamDronePosition;
                if (freeCamTarget != null)
                {
                    if (!SampleFramePose(freeCamTarget) ||
                        !ghostSmoothedPos.TryGetValue(freeCamTarget, out freeCamDronePosition))
                    {
                        Quaternion freeCamDroneRotation;
                        TryGetDroneTransform(freeCamTarget, displayReplayTime, out freeCamDronePosition, out freeCamDroneRotation);
                    }
                    spectatedEntry = freeCamTarget;
                    ghostDronePos = freeCamDronePosition;
                    ghostDronePosValid = true;
                    if (holdZoomActive)
                    {
                        UpdateFreeCamFocus(freeCamDronePosition);
                        cam.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                    }
                }
                RunDroneMarkersIfRequested();
                return;
            }
            if (orbitTarget == null) return;

            GhostLineEntry target = spectatedEntry;
            if (target == null)
            {
                target = FindEntryForNativeRoot(nativeRoot);
                if (target == null)
                {
                    Vector3 tgtPos = nativeRoot != null ? nativeRoot.position : orbitTarget.position;
                    float bestD = float.MaxValue;
                    foreach (var e in GhostLinesPlugin.entries)
                    {
                        Vector3 ep; Quaternion er;
                        if (TryGetDroneTransformUnshifted(e, rt, out ep, out er))
                        {
                            float d = Vector3.Distance(ep, tgtPos);
                            if (d < bestD) { bestD = d; target = e; }
                        }
                    }
                }
            }
            if (target == null)
            {
                if (!noTargetLogged)
                {
                    noTargetLogged = true;
                    if (ModLog.DebugEnabled) Debug.Log("[GhostLines] OnCameraPreRender: no target found");
                }
                return;
            }
            noTargetLogged = false;
            if (spectatedEntry == null)
                spectatedEntry = target;
            InitializeDefaultReferenceEntry();

            if (nativeRoot != null)
            {
                Transform mappedRoot;
                if (!ghostDroneMap.TryGetValue(target, out mappedRoot) || mappedRoot != nativeRoot)
                {
                    ghostDroneMap[target] = nativeRoot;
                    CacheGhostTrails(target, nativeRoot);
                    SetGhostTrailsSuppressed(target, true);
                }
                if (nativeCameraMount != null)
                    ghostCamMountMap[target] = nativeCameraMount;
            }

            // Same clock and the same single smoothing pass as every other ghost
            // (it was resampled on stableReplayTime and smoothed a second time).
            Vector3 smoothedPosS;
            Quaternion smoothedRotS;
            if (!SampleFramePose(target) ||
                !ghostSmoothedPos.TryGetValue(target, out smoothedPosS) ||
                !ghostSmoothedRot.TryGetValue(target, out smoothedRotS))
            { Debug.Log("[GhostLines] OnCameraPreRender: TryGetDroneTransform failed"); return; }

            if (nativeRoot != null)
                nativeRoot.SetPositionAndRotation(smoothedPosS, smoothedRotS);

            ghostDronePos = smoothedPosS;
            ghostDronePosValid = true;

            if (customCameraActive)
            {
                Quaternion rot = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
                Vector3 orbitPos = smoothedPosS + rot * (Vector3.back * orbitDist);
                cam.transform.position = orbitPos;
                Vector3 lookDirection = smoothedPosS - cam.transform.position;
                if (lookDirection.sqrMagnitude > 0.0001f)
                    cam.transform.rotation = Quaternion.LookRotation(lookDirection, Vector3.up);
                RunDroneMarkersIfRequested();
                return;
            }

            bool isThirdP = true;
            if (flightCamCtrl != null)
                try { isThirdP = flightCamCtrl.InThirdPerson; } catch { }

            if (isThirdP)
            {
                Vector3 shift = smoothedPosS - nativeRootPosition;
                cam.transform.SetPositionAndRotation(
                    nativeCameraPosition + shift,
                    nativeCameraRotation);
            }
            else
            {
                if (ghostCamMountMap.TryGetValue(target, out Transform camMount) && camMount != null)
                {
                    cam.transform.SetPositionAndRotation(camMount.position, camMount.rotation);
                }
                else
                {
                    // Same smoothed pose the drone body is drawn at (the mount
                    // path above follows it too), so the two can't drift apart.
                    Vector3 shift = smoothedPosS - nativeRootPosition;
                    cam.transform.SetPositionAndRotation(
                        nativeCameraPosition + shift,
                        nativeCameraRotation);
                }

            }

            RunDroneMarkersIfRequested();
        }
    }
}
