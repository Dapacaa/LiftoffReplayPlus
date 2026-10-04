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
        // Places every mapped native drone at this frame's snapshot pose (cached by
        // BuildFramePoses; nothing is resampled or re-smoothed). Called around the
        // PiP renders and from the main camera's pre-cull.
        private void ApplyGhostDroneTransforms()
        {
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Key == null || pair.Value == null)
                    continue;

                SetGhostTrailsSuppressed(pair.Key, true);
                Vector3 smoothed;
                Quaternion smoothedRot;
                if (SampleFramePose(pair.Key) &&
                    ghostSmoothedPos.TryGetValue(pair.Key, out smoothed) &&
                    ghostSmoothedRot.TryGetValue(pair.Key, out smoothedRot))
                    pair.Value.SetPositionAndRotation(smoothed, smoothedRot);
            }
        }

        private void EnsureSmoothDroneTrailMaterial()
        {
            if (smoothDroneTrailMaterial != null)
                return;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                return;
            smoothDroneTrailMaterial = new Material(shader);
            smoothDroneTrailMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        private SmoothDroneTrailState GetSmoothDroneTrailState(GhostLineEntry entry)
        {
            SmoothDroneTrailState state;
            if (smoothDroneTrailStates.TryGetValue(entry, out state) && state != null && state.line != null)
                return state;
            EnsureSmoothDroneTrailMaterial();
            if (smoothDroneTrailMaterial == null)
                return null;
            GameObject obj = new GameObject("GhostLinesSmoothTrail");
            obj.hideFlags = HideFlags.HideAndDontSave;
            LineRenderer line = obj.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.sharedMaterial = smoothDroneTrailMaterial;
            line.widthMultiplier = SMOOTH_TRAIL_WIDTH;
            line.widthCurve = new AnimationCurve(new Keyframe(0f, 0.2f), new Keyframe(1f, 1f));
            line.numCapVertices = 3;
            line.numCornerVertices = 3;
            line.textureMode = LineTextureMode.Stretch;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            state = new SmoothDroneTrailState { objectRoot = obj, line = line };
            smoothDroneTrailStates[entry] = state;
            return state;
        }

        private void UpdateSmoothDroneTrails(float replayTime)
        {
            if (!smoothDroneTrails)
                return;
            foreach (var pair in ghostDroneMap)
            {
                GhostLineEntry entry = pair.Key;
                Transform drone = pair.Value;
                if (entry == null || drone == null)
                    continue;
                SmoothDroneTrailState state = GetSmoothDroneTrailState(entry);
                if (state == null)
                    continue;
                Vector3 position;
                if (!ghostSmoothedPos.TryGetValue(entry, out position))
                    position = drone.position;
                if (state.lastTime >= 0f && replayTime < state.lastTime - 0.02f ||
                    state.lastTime >= 0f && Mathf.Abs(replayTime - state.lastTime) > 0.3f)
                {
                    state.points.Clear();
                    state.times.Clear();
                }
                bool addPoint = state.points.Count == 0 ||
                    (position - state.lastPosition).sqrMagnitude >= SMOOTH_TRAIL_SAMPLE_DISTANCE * SMOOTH_TRAIL_SAMPLE_DISTANCE;
                if (addPoint)
                {
                    state.points.Add(position);
                    state.times.Add(replayTime);
                    state.lastPosition = position;
                    while (state.points.Count > SMOOTH_TRAIL_MAX_POINTS ||
                        (state.times.Count > 1 && replayTime - state.times[0] > SMOOTH_TRAIL_SECONDS))
                    {
                        state.points.RemoveAt(0);
                        state.times.RemoveAt(0);
                    }
                }
                state.lastTime = replayTime;
                if (state.points.Count < 2)
                {
                    state.line.enabled = false;
                    continue;
                }
                Color color = entry.color;
                color.a = 0.9f;
                state.line.startColor = new Color(color.r, color.g, color.b, 0.05f);
                state.line.endColor = color;
                if (addPoint || state.renderedCount != state.points.Count)
                {
                    state.line.positionCount = state.points.Count;
                    for (int i = 0; i < state.points.Count; i++)
                        state.line.SetPosition(i, state.points[i]);
                    state.renderedCount = state.points.Count;
                }
                state.line.enabled = true;
            }
        }

        private void ClearSmoothDroneTrails()
        {
            foreach (var pair in smoothDroneTrailStates)
            {
                if (pair.Value != null && pair.Value.objectRoot != null)
                    UnityEngine.Object.Destroy(pair.Value.objectRoot);
            }
            smoothDroneTrailStates.Clear();
        }

        private GhostLineEntry FindEntryForNativeRoot(Transform root)
        {
            if (root == null)
                return null;
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Value == root)
                    return pair.Key;
            }
            return null;
        }

        private bool TryGetNativeTrackedTransforms(out Transform root, out Transform cameraMount)
        {
            root = null;
            cameraMount = null;
            if (flightCamCtrl == null)
                return false;

            FlightCameraController.AttachedDroneData data = null;
            try { data = flightCamCtrl.TrackingDroneData; } catch { }
            if (data == null)
                return false;
            root = data.root;
            cameraMount = data.cameraParentPoint;
            return root != null || cameraMount != null;
        }

        // Cheap request: multiple paths (Update, LateUpdate, camera PreRender) used to
        // run the full marker refresh 2x+ per frame. Actual work happens once per
        // frame in RunDroneMarkersIfRequested(), invoked from the camera PreRender
        // phase so markers are positioned after the ghost roots move.
        private void UpdateDroneMarkers()
        {
            droneMarkersRequestedFrame = Time.frameCount;
        }

        private void RunDroneMarkersIfRequested()
        {
            if (droneMarkersWorkFrame == Time.frameCount || droneMarkersRequestedFrame != Time.frameCount)
                return;
            droneMarkersWorkFrame = Time.frameCount;
            UpdateDroneMarkersInternal();
        }

        private void UpdateDroneMarkersInternal()
        {
            droneLabelHitboxes.Clear();
            if (!replayReady || Camera.main == null)
                return;

            float replayTime = displayReplayTime;
            UpdateReferenceDeltaDisplay(replayTime);

            bool fpvHideSpectated = IsFirstPersonView() && spectatedEntry != null;
            Vector3 camPos = Camera.main.transform.position;
            foreach (var entry in GhostLinesPlugin.entries)
            {
                if (entry.markerObject == null || entry.markerTether == null || entry.markerText == null)
                    continue;

                if (fpvHideSpectated && entry == spectatedEntry)
                {
                    entry.markerObject.SetActive(false);
                    continue;
                }

                Vector3 dronePos;
                if (!TryGetSmoothDronePos(entry, replayTime, out dronePos))
                    continue;

                entry.markerObject.SetActive(true);
                entry.markerTether.enabled = (GhostLinesPlugin.showNameTags && entry.nameTagVisible) || (GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible);
                if (entry.markerRendererCache == null)
                    entry.markerRendererCache = entry.markerText.GetComponent<MeshRenderer>();
                MeshRenderer nameRenderer = entry.markerRendererCache;
                if (nameRenderer != null)
                    nameRenderer.enabled = GhostLinesPlugin.showNameTags && entry.nameTagVisible;

                float markerHeight = entry.markerBaseHeight > 0f ? entry.markerBaseHeight : DRONE_MARKER_HEIGHT;
                Vector3 labelPos = dronePos + Vector3.up * markerHeight;

                entry.markerObject.transform.position = dronePos;
                entry.markerTether.SetPosition(0, Vector3.up * DRONE_TETHER_START_OFFSET);

                Transform labelT = entry.markerText.transform;
                labelT.position = labelPos;
                labelT.rotation = Quaternion.LookRotation(camPos - labelPos, Vector3.up);
                labelT.Rotate(0f, 180f, 0f, Space.Self);

                float dist = Vector3.Distance(camPos, labelPos);
                float fovScale = Camera.main.fieldOfView / 60f;
                float scale = Mathf.Clamp(dist * 0.08f * fovScale, 0.35f, 1.8f);
                labelT.localScale = Vector3.one * scale;

                if (entry.markerText.text != entry.pilotName)
                    entry.markerText.text = entry.pilotName;
                ApplyDroneLabelColor(entry);
                entry.markerTether.startWidth = Mathf.Clamp(dist * 0.0025f, 0.04f, 0.16f);
                entry.markerTether.endWidth = Mathf.Clamp(dist * 0.0025f, 0.04f, 0.16f);

                float labelHalfHeight = nameRenderer != null ? nameRenderer.bounds.size.y * 0.5f : 0.35f;
                float tetherTop = markerHeight - labelHalfHeight - 0.05f;
                entry.markerTether.SetPosition(1, Vector3.up * Mathf.Max(tetherTop, 0.3f));

                // Rebuild the click target after enabling and refreshing the text mesh.
                if (GhostLinesPlugin.showNameTags && entry.nameTagVisible && nameRenderer != null)
                    AddDroneLabelHitbox(entry, nameRenderer.bounds, Camera.main);
            }
        }

        private bool TryGetSmoothDronePos(GhostLineEntry entry, float replayTime, out Vector3 pos)
        {
            // Normal path: the frame snapshot (sampled + smoothed once per frame).
            Quaternion snapRot;
            if (TryGetFramePose(entry, out pos, out snapRot))
                return true;
            Vector3 smoothed;
            bool hasPrev = ghostSmoothedPos.TryGetValue(entry, out smoothed);
            // Before this frame's snapshot (markers refreshed from Update): show the
            // last smoothed pose without advancing the smoothing a second time.
            if (hasPrev && frameSnapshotFrame >= 0)
            {
                pos = smoothed;
                return true;
            }
            if (hasPrev && ghostDroneMap.ContainsKey(entry))
            {
                pos = smoothed;
                return true;
            }
            Vector3 raw;
            if (!TryGetDronePosition(entry, replayTime, out raw))
            {
                pos = Vector3.zero;
                return false;
            }
            if (ghostDroneMap.ContainsKey(entry))
                pos = hasPrev ? Vector3.Lerp(smoothed, raw, ghostPosBlend) : raw;
            else
                pos = Vector3.Lerp(hasPrev ? smoothed : raw, raw, ghostPosBlend);
            ghostSmoothedPos[entry] = pos;
            return true;
        }

        private void ApplyDroneLabelColor(GhostLineEntry entry)
        {
            if (entry == null)
                return;

            Color color = entry == hoveredDroneLabelEntry
                ? Color.Lerp(entry.color, Color.white, 0.72f)
                : entry.color;
            if (color == entry.lastMarkerColor)
                return;
            entry.lastMarkerColor = color;
            if (entry.markerText != null)
                entry.markerText.color = color;
            if (entry.markerTether != null)
            {
                entry.markerTether.startColor = color;
                entry.markerTether.endColor = color;
            }
        }

        private void AddDroneLabelHitbox(GhostLineEntry entry, Bounds bounds, Camera camera)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = scratchLabelCorners;
            corners[0] = new Vector3(min.x, min.y, min.z);
            corners[1] = new Vector3(max.x, min.y, min.z);
            corners[2] = new Vector3(min.x, max.y, min.z);
            corners[3] = new Vector3(max.x, max.y, min.z);
            corners[4] = new Vector3(min.x, min.y, max.z);
            corners[5] = new Vector3(max.x, min.y, max.z);
            corners[6] = new Vector3(min.x, max.y, max.z);
            corners[7] = new Vector3(max.x, max.y, max.z);

            float left = float.MaxValue;
            float right = float.MinValue;
            float top = float.MaxValue;
            float bottom = float.MinValue;
            float screenMinY = float.MaxValue;
            float screenMaxY = float.MinValue;
            float nearestDepth = float.MaxValue;
            foreach (Vector3 corner in corners)
            {
                Vector3 screen = camera.WorldToScreenPoint(corner);
                if (screen.z <= camera.nearClipPlane)
                    return;
                left = Mathf.Min(left, screen.x);
                right = Mathf.Max(right, screen.x);
                top = Mathf.Min(top, Screen.height - screen.y);
                bottom = Mathf.Max(bottom, Screen.height - screen.y);
                screenMinY = Mathf.Min(screenMinY, screen.y);
                screenMaxY = Mathf.Max(screenMaxY, screen.y);
                nearestDepth = Mathf.Min(nearestDepth, screen.z);
            }

            if (right < camera.pixelRect.xMin || left > camera.pixelRect.xMax ||
                screenMaxY < camera.pixelRect.yMin || screenMinY > camera.pixelRect.yMax)
                return;

            Rect rect = new Rect(left, top, right - left, bottom - top);
            rect.xMin -= 8f;
            rect.xMax += 8f;
            rect.yMin -= 6f;
            rect.yMax += 6f;
            droneLabelHitboxes.Add(new DroneLabelHitbox
            {
                entry = entry,
                rect = rect,
                depth = nearestDepth
            });
        }

        private bool TryGetDroneInput(GhostLineEntry entry, float replayTime, out Vector4 input)
        {
            input = Vector4.zero;
            if (entry == null || entry.stateTimes == null || entry.originalInputs == null
                || entry.stateTimes.Length == 0 || entry.stateTimes.Length != entry.originalInputs.Length)
                return false;

            float queryT = GetGateQueryTimestamp(entry, replayTime);
            if (entry.stateTimes.Length == 1)
            {
                input = entry.originalInputs[0];
                return true;
            }

            int hi = FindFirstStateAtOrAfter(entry.stateTimes, queryT);
            if (hi <= 0)
            {
                input = entry.originalInputs[0];
                return true;
            }
            if (hi >= entry.stateTimes.Length)
            {
                input = entry.originalInputs[entry.originalInputs.Length - 1];
                return true;
            }

            int lo = hi - 1;
            float t0 = entry.stateTimes[lo];
            float t1 = entry.stateTimes[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, queryT);
            input = Vector4.LerpUnclamped(entry.originalInputs[lo], entry.originalInputs[hi], u);
            return true;
        }

        private bool IsFirstPersonView()
        {
            if (freeCamActive || customCameraActive)
                return false;
            if (flightCamCtrl == null)
                return !lateIsThirdPerson;
            try { return !flightCamCtrl.InThirdPerson; }
            catch { return !lateIsThirdPerson; }
        }

        private void MatchGhostDrones()
        {
            System.Collections.Generic.List<GameObject> ghostList = new System.Collections.Generic.List<GameObject>();
            for (int si = 0; si < SceneManager.sceneCount; si++)
            {
                UnityEngine.SceneManagement.Scene scene = SceneManager.GetSceneAt(si);
                if (!scene.isLoaded)
                    continue;
                GameObject[] sceneRoots = scene.GetRootGameObjects();
                for (int ri = 0; ri < sceneRoots.Length; ri++)
                    CollectGhostDroneCandidates(sceneRoots[ri].transform, ghostList);
            }

            if (ghostList.Count == 0)
                return;

            float rt;
            TryGetReplayTime(out rt);
            var matchedEntries = new System.Collections.Generic.HashSet<GhostLineEntry>(ghostDroneMap.Keys);
            var availableDrones = new System.Collections.Generic.List<GameObject>();
            // Swap each heuristic candidate for the transform the game itself moves
            // (what the camera's AttachedDroneData.root reports, as used for the
            // spectated drone). Driving a parent/sibling instead fights the
            // game's per-physics-tick writes and makes the drone skip.
            for (int gi = 0; gi < ghostList.Count; gi++)
            {
                Transform nativeRoot, nativeMount;
                if (TryResolveNativeDroneTransforms(ghostList[gi].transform, out nativeRoot, out nativeMount) &&
                    nativeRoot != null && nativeRoot != ghostList[gi].transform)
                {
                    if (!ghostRootRemapLogged)
                    {
                        ghostRootRemapLogged = true;
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] ghost drone root remapped: '" + ghostList[gi].name +
                            "' -> '" + nativeRoot.name + "' (" + (nativeRoot.IsChildOf(ghostList[gi].transform) ? "child" : "not child") + ")");
                    }
                    ghostList[gi] = nativeRoot.gameObject;
                }
            }
            foreach (var go in ghostList)
            {
                if (!ghostDroneMap.ContainsValue(go.transform))
                {
                    // Suppress the native effect before a recording match exists.
                    // A late or failed match must not leave a second, trailing
                    // drone underneath the independently drawn marker.
                    SuppressUnmatchedGhostTrails(go.transform);
                    availableDrones.Add(go);
                }
            }

            // Choose the globally closest entry/root pair each pass.  Matching
            // roots in scene-enumeration order can steal a nearby pilot's drone
            // and leave the final (often slowest) pilot with an un-suppressed
            // native drone and trail.
            while (availableDrones.Count > 0)
            {
                GameObject bestDrone = null;
                GhostLineEntry bestEntry = null;
                float bestDist = float.MaxValue;
                for (int di = 0; di < availableDrones.Count; di++)
                {
                    GameObject go = availableDrones[di];
                    Vector3 dronePos = go.transform.position;
                    foreach (var entry in GhostLinesPlugin.entries)
                    {
                        if (matchedEntries.Contains(entry))
                            continue;
                        Vector3 ePos; Quaternion eRot;
                        if (!TryGetDroneTransformUnshifted(entry, rt, out ePos, out eRot))
                            continue;
                        float d = Vector3.Distance(dronePos, ePos);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestDrone = go;
                            bestEntry = entry;
                        }
                    }
                }

                if (bestDrone == null || bestEntry == null || bestDist >= 20f)
                {
                    break;
                }

                matchedEntries.Add(bestEntry);
                ghostDroneMap[bestEntry] = bestDrone.transform;
                Transform resolvedRoot, resolvedMount;
                ghostCamMountMap[bestEntry] =
                    TryResolveNativeDroneTransforms(bestDrone.transform, out resolvedRoot, out resolvedMount) && resolvedMount != null
                        ? resolvedMount
                        : FindCameraMountTransform(bestDrone.transform);
                CacheGhostTrails(bestEntry, bestDrone.transform);
                SetGhostTrailsSuppressed(bestEntry, true);
                availableDrones.Remove(bestDrone);
            }
        }

        private System.Type nativeDroneType;
        private bool ghostRootRemapLogged;
        private bool nativeDroneTypeResolved;

        // Resolves the game's drone component under/above a candidate and reads
        // root + cameraParentPoint through FlightCameraController.AttachedDroneData,
        // exactly as the flight camera does. The drone type name is obfuscated, so
        // it is taken from that constructor's single parameter.
        private bool TryResolveNativeDroneTransforms(Transform candidate, out Transform root, out Transform mount)
        {
            root = null;
            mount = null;
            if (candidate == null)
                return false;
            if (!nativeDroneTypeResolved)
            {
                nativeDroneTypeResolved = true;
                try
                {
                    foreach (var ctor in typeof(FlightCameraController.AttachedDroneData).GetConstructors())
                    {
                        var ps = ctor.GetParameters();
                        if (ps.Length == 1 && typeof(Component).IsAssignableFrom(ps[0].ParameterType))
                        {
                            nativeDroneType = ps[0].ParameterType;
                            break;
                        }
                    }
                }
                catch { }
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] native drone type resolved=" + (nativeDroneType != null));
            }
            if (nativeDroneType == null)
                return false;
            try
            {
                Component drone = candidate.GetComponentInChildren(nativeDroneType, true);
                if (drone == null)
                    drone = candidate.GetComponentInParent(nativeDroneType);
                if (drone == null)
                    return false;
                var data = System.Activator.CreateInstance(typeof(FlightCameraController.AttachedDroneData), drone)
                    as FlightCameraController.AttachedDroneData;
                if (data == null)
                    return false;
                root = data.root;
                mount = data.cameraParentPoint;
                return root != null;
            }
            catch { return false; }
        }

        private static bool IsExcludedDronePathName(string name)
        {
            return name.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("gym", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("building", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("wall", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ceiling", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("gate", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("structure", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("environment", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("track", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("scenery", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CollectGhostDroneCandidates(Transform t, List<GameObject> outList)
        {
            if (t == null || IsExcludedDronePathName(t.name))
                return;
            if (IsGhostDroneRoot(t))
            {
                if (t.GetComponentsInChildren<Renderer>(true).Length > 0)
                    outList.Add(t.gameObject);
                return;
            }
            for (int i = 0; i < t.childCount; i++)
                CollectGhostDroneCandidates(t.GetChild(i), outList);
        }

        private bool IsGhostDroneRoot(Transform candidate)
        {
            if (candidate == null)
                return false;

            if (IsExcludedDronePathName(candidate.name))
                return false;
            // Drone frames vary widely (DquadFixationFrame01, FrameTalon...,
            // custom frames, etc.).  Identify the shared hierarchy instead of
            // relying on one frame name.  Environment props can also contain
            // "Frame01", but do not have a Trail plus motor/battery/slot layout.
            if (candidate.GetComponentInChildren<TrailRenderer>(true) == null)
                return false;

            int droneParts = 0;
            bool hasCameraSlot = false;
            bool hasMotor = false;
            bool hasBattery = false;
            bool hasLaunchBase = false;
            bool hasColliders = false;
            foreach (Transform child in candidate.GetComponentsInChildren<Transform>(true))
            {
                if (child == candidate) continue;
                string childName = child.name;
                if (childName.IndexOf("cameraslot", StringComparison.OrdinalIgnoreCase) >= 0
                    || childName.IndexOf("cameraparentpoint", StringComparison.OrdinalIgnoreCase) >= 0
                    || childName.IndexOf("fpvcam", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasCameraSlot = true;
                if (childName.IndexOf("motor", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasMotor = true;
                if (childName.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasBattery = true;
                if (childName.IndexOf("launchbase", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasLaunchBase = true;
                if (childName.IndexOf("collider", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasColliders = true;
            }
            if (hasCameraSlot) droneParts++;
            if (hasMotor) droneParts++;
            if (hasBattery) droneParts++;
            if (hasLaunchBase) droneParts++;
            if (hasColliders) droneParts++;
            if (droneParts < 2)
                return false;

            for (Transform parent = candidate.parent; parent != null; parent = parent.parent)
            {
                string parentName = parent.name;
                if (parentName.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0
                    || parentName.IndexOf("gym", StringComparison.OrdinalIgnoreCase) >= 0
                    || parentName.IndexOf("building", StringComparison.OrdinalIgnoreCase) >= 0
                    || parentName.IndexOf("environment", StringComparison.OrdinalIgnoreCase) >= 0
                    || parentName.IndexOf("scenery", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }
            return true;
        }

        private Transform FindCameraMountTransform(Transform droneRoot)
        {
            if (droneRoot == null) return null;
            var children = droneRoot.GetComponentsInChildren<Transform>(true);
            Transform slotWithRot = null;
            Transform parentPoint = null;
            Transform cameraNano = null;
            foreach (var t in children)
            {
                if (t == droneRoot) continue;
                string lname = t.name.ToLowerInvariant();
                if (lname.Contains("redot") || lname.Contains("nano"))
                {
                    if (cameraNano == null) cameraNano = t;
                }
                if (lname.Contains("cameraparentpoint") || lname.Contains("parentpoint"))
                {
                    if (parentPoint == null) parentPoint = t;
                }
                if (lname.Contains("fpvcam") || lname.Contains("fpv_cam") || lname.Contains("camera") || lname.Contains("cammount") || lname.Contains("cam_mount") || lname.Contains("gopro") || lname.Contains("lens") || lname.Contains("fpv"))
                {
                    if (slotWithRot == null) slotWithRot = t;
                }
            }
            Transform best = parentPoint ?? cameraNano ?? slotWithRot;
            return best;
        }
    }
}
