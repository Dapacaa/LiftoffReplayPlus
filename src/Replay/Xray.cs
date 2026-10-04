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
        private static readonly Color32 XrayMapColor = new Color32(38, 191, 255, 107);
        private static readonly Comparison<XrayStructure> XrayStructureByDistance =
            (a, b) => a.sortKey.CompareTo(b.sortKey);
        // Case-insensitive substrings of hierarchy names that mark drone parts and
        // other clutter that shouldn't be wireframed.
        private static readonly string[] XrayExcludedNameParts =
        {
            "fixation", "dquad", "cameraredot", "camera nano", "soupape",
            "soldering", "propeller", "prop_", "motor"
        };
        private readonly float[] xrayCornerAlphas = new float[8];
        private int xrayMapVertexCount;

        private void EnsureXrayMaterials()
        {
            if (xrayWireMaterial != null)
                return;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                return;

            xrayWireMaterial = new Material(shader);
            xrayWireMaterial.hideFlags = HideFlags.HideAndDontSave;
            xrayWireMaterial.SetInt("_SrcBlend", 5);
            xrayWireMaterial.SetInt("_DstBlend", 10);
            xrayWireMaterial.SetInt("_Cull", 0);
            xrayWireMaterial.SetInt("_ZWrite", 0);
            xrayWireMaterial.SetInt("_ZTest", 8);
            xrayWireMaterial.SetColor("_Color", Color.white);
        }

        private static bool NameContainsAny(string name, string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (name.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // Filters that depend only on the collider's own hierarchy and size, so the
        // result is cached per collider (see xrayRejectedById).
        private bool IsXrayRejected(Collider collider)
        {
            if (collider.attachedRigidbody != null)
                return true;
            Transform transform = collider.transform;
            if (transform.name.StartsWith("GhostLine_", StringComparison.Ordinal)
                || transform.name.StartsWith("GhostMarker_", StringComparison.Ordinal))
                return true;
            if (transform.GetComponent<LineRenderer>() != null || transform.GetComponent<TrailRenderer>() != null)
                return true;
            if (transform.GetComponentInParent<Canvas>() != null || transform.GetComponentInParent<Rigidbody>() != null)
                return true;
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (NameContainsAny(current.name, XrayExcludedNameParts))
                    return true;
            }

            Vector3 size = collider.bounds.size;
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 0.25f;
        }

        // Checked live rather than cached: drone roots get mapped during playback.
        private bool IsUnderGhostDrone(Transform transform)
        {
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Value != null && transform.IsChildOf(pair.Value))
                    return true;
            }
            return false;
        }

        private bool TryGetXrayDronePosition(out Vector3 position)
        {
            if (ghostDronePosValid)
            {
                position = ghostDronePos;
                return true;
            }

            Transform root;
            if (spectatedEntry != null && ghostDroneMap.TryGetValue(spectatedEntry, out root) && root != null)
            {
                position = root.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private void RefreshXrayStructures(Vector3 dronePosition)
        {
            if (xrayStructureCenterValid &&
                (dronePosition - xrayStructureCenter).sqrMagnitude < XRAY_REBUILD_DISTANCE * XRAY_REBUILD_DISTANCE)
                return;

            xrayStructureCenter = dronePosition;
            xrayStructureCenterValid = true;

            Dictionary<int, XrayStructure> next = xrayNextStructureById;
            next.Clear();
            // Triggers are never drawn, so let physics drop them instead of filtering.
            int colliderCount = Physics.OverlapSphereNonAlloc(
                dronePosition,
                XRAY_QUERY_RADIUS,
                xrayColliderBuffer,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < colliderCount; i++)
            {
                Collider collider = xrayColliderBuffer[i];
                xrayColliderBuffer[i] = null;
                if (collider == null)
                    continue;
                int id = collider.GetInstanceID();
                if (next.ContainsKey(id))
                    continue;

                // Structures still in range are reused as-is; only newcomers are
                // filtered and built.
                XrayStructure structure;
                if (!xrayStructureById.TryGetValue(id, out structure))
                {
                    bool rejected;
                    if (!xrayRejectedById.TryGetValue(id, out rejected))
                    {
                        rejected = IsXrayRejected(collider);
                        xrayRejectedById[id] = rejected;
                    }
                    if (rejected)
                        continue;
                    structure = BuildXrayStructure(collider);
                    if (structure == null)
                    {
                        xrayRejectedById[id] = true;
                        continue;
                    }
                }
                if (IsUnderGhostDrone(collider.transform))
                    continue;

                structure.sortKey = structure.bounds.SqrDistance(dronePosition);
                next[id] = structure;
            }

            xrayNextStructureById = xrayStructureById;
            xrayStructureById = next;

            // Nearest first, so the line budget is spent close to the drone.
            xrayStructures.Clear();
            foreach (XrayStructure structure in next.Values)
                xrayStructures.Add(structure);
            xrayStructures.Sort(XrayStructureByDistance);
            xrayStructureRevision++;
            InvalidateXrayMapCache();
        }

        private void EnsureXrayMapMesh()
        {
            if (xrayMapMesh != null)
                return;

            xrayMapMesh = new Mesh();
            xrayMapMesh.name = "GhostLines Xray Map";
            xrayMapMesh.hideFlags = HideFlags.HideAndDontSave;
            xrayMapMesh.MarkDynamic();
            InvalidateXrayMapCache();

            // Every segment is its own vertex pair, so the index buffer is always
            // 0..n-1; fill it once and upload a prefix of it.
            if (xrayMapIndices == null)
            {
                xrayMapIndices = new int[XRAY_MAX_LINES * 2];
                for (int i = 0; i < xrayMapIndices.Length; i++)
                    xrayMapIndices[i] = i;
            }
        }

        private void InvalidateXrayMapCache()
        {
            xrayMapCacheValid = false;
            xrayMapCachedStructureRevision = -1;
        }

        // Quantized to a half-meter grid: an exact-equality key never hit while the
        // drone was moving, forcing a full mesh rebuild + upload every frame.
        private static Vector3 QuantizeXrayMapKey(Vector3 dronePosition)
        {
            return new Vector3(
                Mathf.Round(dronePosition.x * 2f) * 0.5f,
                Mathf.Round(dronePosition.y * 2f) * 0.5f,
                Mathf.Round(dronePosition.z * 2f) * 0.5f);
        }

        private bool IsXrayMapCacheCurrent(Vector3 dronePosition)
        {
            return xrayMapCacheValid
                && xrayMapCachedStructureRevision == xrayStructureRevision
                && xrayMapCachedDronePosition == QuantizeXrayMapKey(dronePosition);
        }

        private XrayStructure BuildXrayStructure(Collider collider)
        {
            Vector3[] p = new Vector3[8];
            Bounds b = collider.bounds;
            if (collider is BoxCollider box)
            {
                Transform t = collider.transform;
                Vector3 half = box.size * 0.5f;
                Vector3 c = box.center;
                p[0] = t.TransformPoint(c + new Vector3(-half.x, -half.y, -half.z));
                p[1] = t.TransformPoint(c + new Vector3(half.x, -half.y, -half.z));
                p[2] = t.TransformPoint(c + new Vector3(half.x, half.y, -half.z));
                p[3] = t.TransformPoint(c + new Vector3(-half.x, half.y, -half.z));
                p[4] = t.TransformPoint(c + new Vector3(-half.x, -half.y, half.z));
                p[5] = t.TransformPoint(c + new Vector3(half.x, -half.y, half.z));
                p[6] = t.TransformPoint(c + new Vector3(half.x, half.y, half.z));
                p[7] = t.TransformPoint(c + new Vector3(-half.x, half.y, half.z));
                return new XrayStructure { bounds = b, corners = p };
            }

            if (b.size.sqrMagnitude < 0.0001f)
                return null;
            SetBoxCorners(p, b);
            return new XrayStructure { bounds = b, corners = p };
        }

        // Same corner order as BuildXrayStructure, matching xrayBoxEdgePairs.
        private static void SetBoxCorners(Vector3[] p, Bounds b)
        {
            Vector3 min = b.min;
            Vector3 max = b.max;
            p[0] = new Vector3(min.x, min.y, min.z);
            p[1] = new Vector3(max.x, min.y, min.z);
            p[2] = new Vector3(max.x, max.y, min.z);
            p[3] = new Vector3(min.x, max.y, min.z);
            p[4] = new Vector3(min.x, min.y, max.z);
            p[5] = new Vector3(max.x, min.y, max.z);
            p[6] = new Vector3(max.x, max.y, max.z);
            p[7] = new Vector3(min.x, max.y, max.z);
        }

        private static float FarthestSqrDistance(Bounds bounds, Vector3 point)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float dx = Mathf.Max(Mathf.Abs(point.x - min.x), Mathf.Abs(max.x - point.x));
            float dy = Mathf.Max(Mathf.Abs(point.y - min.y), Mathf.Abs(max.y - point.y));
            float dz = Mathf.Max(Mathf.Abs(point.z - min.z), Mathf.Abs(max.z - point.z));
            return dx * dx + dy * dy + dz * dz;
        }

        private bool TryClipXraySegmentToSphere(
            Vector3 worldA,
            Vector3 worldB,
            Vector3 center,
            float radius,
            out Vector3 drawA,
            out Vector3 drawB,
            out float alphaA,
            out float alphaB)
        {
            drawA = worldA;
            drawB = worldB;
            alphaA = 0f;
            alphaB = 0f;
            float radiusSqr = radius * radius;
            Vector3 offset = worldA - center;
            float offsetSqr = offset.sqrMagnitude;
            float endSqr = (worldB - center).sqrMagnitude;

            // Both ends inside: the sphere is convex, so nothing to clip.
            if (offsetSqr <= radiusSqr && endSqr <= radiusSqr)
            {
                alphaA = XrayRangeFade(offsetSqr, radius);
                alphaB = XrayRangeFade(endSqr, radius);
                return true;
            }

            Vector3 direction = worldB - worldA;
            float qa = direction.sqrMagnitude;
            if (qa < 0.0001f)
                return false;

            // Half-b form of the ray/sphere quadratic; qa > 0 keeps t0 <= t1.
            float halfB = Vector3.Dot(offset, direction);
            float discriminant = halfB * halfB - qa * (offsetSqr - radiusSqr);
            if (discriminant < 0f)
                return false;

            float root = Mathf.Sqrt(discriminant);
            float enter = Mathf.Max(0f, (-halfB - root) / qa);
            float exit = Mathf.Min(1f, (-halfB + root) / qa);
            if (enter > exit)
                return false;

            drawA = Vector3.LerpUnclamped(worldA, worldB, enter);
            drawB = Vector3.LerpUnclamped(worldA, worldB, exit);
            alphaA = XrayRangeFade((drawA - center).sqrMagnitude, radius);
            alphaB = XrayRangeFade((drawB - center).sqrMagnitude, radius);
            return true;
        }

        // SmoothStep from 0 at the radius to 1 at 60% of it, taking a squared
        // distance so the solid core and outside cases skip the sqrt.
        private static float XrayRangeFade(float distanceSqr, float radius)
        {
            float fadeStart = radius * 0.6f;
            if (distanceSqr <= fadeStart * fadeStart)
                return 1f;
            if (distanceSqr >= radius * radius)
                return 0f;
            float t = (radius - Mathf.Sqrt(distanceSqr)) / (radius - fadeStart);
            return t * t * (3f - 2f * t);
        }

        private void SetXrayEnabled(bool enabled)
        {
            xrayEnabled = enabled;
            GhostLinesPlugin.SaveGlobalDisplayPreferences();
            MarkPiPWindowsDirty();
            if (!enabled)
                return;

            // The map may have changed while disabled; re-query on the next draw.
            // The rejection cache stays valid, since it is keyed by collider.
            xrayStructureCenterValid = false;
            xrayStructures.Clear();
            xrayStructureById.Clear();
            InvalidateXrayMapCache();
        }

        private void AddXrayMapVertex(Vector3 position, float alpha)
        {
            Color32 color = XrayMapColor;
            color.a = (byte)(XrayMapColor.a * alpha + 0.5f);
            xrayMapVertices[xrayMapVertexCount] = position;
            xrayMapColors[xrayMapVertexCount] = color;
            xrayMapVertexCount++;
        }

        // Returns false once the line budget is spent.
        private bool AddXrayMapEdge(Vector3 a, Vector3 b, float alphaA, float alphaB, Vector3 center, float radius)
        {
            if (alphaA < 0.01f && alphaB < 0.01f)
            {
                // A chord with both ends on the rim: without a midpoint the
                // interpolated alpha would hide it entirely.
                Vector3 midpoint = (a + b) * 0.5f;
                float midpointAlpha = XrayRangeFade((midpoint - center).sqrMagnitude, radius);
                if (midpointAlpha <= 0.01f)
                    return true;
                if (xrayMapVertexCount + 4 > xrayMapVertices.Length)
                    return false;
                AddXrayMapVertex(a, alphaA);
                AddXrayMapVertex(midpoint, midpointAlpha);
                AddXrayMapVertex(midpoint, midpointAlpha);
                AddXrayMapVertex(b, alphaB);
                return true;
            }

            if (xrayMapVertexCount + 2 > xrayMapVertices.Length)
                return false;
            AddXrayMapVertex(a, alphaA);
            AddXrayMapVertex(b, alphaB);
            return true;
        }

        private void RebuildXrayMapMesh(Vector3 dronePosition)
        {
            float clipRadius = XRAY_RENDER_RADIUS + XRAY_FADE_BUFFER;
            float clipRadiusSqr = clipRadius * clipRadius;
            float fadeStart = clipRadius * 0.6f;
            float fadeStartSqr = fadeStart * fadeStart;
            int[] edges = xrayBoxEdgePairs;
            float[] cornerAlphas = xrayCornerAlphas;
            xrayMapVertexCount = 0;
            bool budgetLeft = true;

            for (int s = 0; s < xrayStructures.Count && budgetLeft; s++)
            {
                XrayStructure structure = xrayStructures[s];
                Bounds bounds = structure.bounds;
                if (bounds.SqrDistance(dronePosition) > clipRadiusSqr)
                    continue;
                Vector3[] corners = structure.corners;
                float farthestSqr = FarthestSqrDistance(bounds, dronePosition);

                if (farthestSqr <= fadeStartSqr)
                {
                    // Entirely in the solid core: every edge at full alpha.
                    if (xrayMapVertexCount + edges.Length > xrayMapVertices.Length)
                        break;
                    for (int e = 0; e < edges.Length; e++)
                        AddXrayMapVertex(corners[edges[e]], 1f);
                }
                else if (farthestSqr <= clipRadiusSqr)
                {
                    // Entirely inside the sphere: no clipping, and each corner's
                    // fade is shared by three edges.
                    for (int c = 0; c < 8; c++)
                        cornerAlphas[c] = XrayRangeFade((corners[c] - dronePosition).sqrMagnitude, clipRadius);
                    for (int e = 0; e < edges.Length && budgetLeft; e += 2)
                    {
                        int ia = edges[e];
                        int ib = edges[e + 1];
                        budgetLeft = AddXrayMapEdge(
                            corners[ia], corners[ib], cornerAlphas[ia], cornerAlphas[ib], dronePosition, clipRadius);
                    }
                }
                else
                {
                    for (int e = 0; e < edges.Length && budgetLeft; e += 2)
                    {
                        Vector3 rangeA, rangeB;
                        float rangeAlphaA, rangeAlphaB;
                        if (!TryClipXraySegmentToSphere(
                            corners[edges[e]],
                            corners[edges[e + 1]],
                            dronePosition,
                            clipRadius,
                            out rangeA,
                            out rangeB,
                            out rangeAlphaA,
                            out rangeAlphaB))
                            continue;
                        budgetLeft = AddXrayMapEdge(
                            rangeA, rangeB, rangeAlphaA, rangeAlphaB, dronePosition, clipRadius);
                    }
                }
            }

            int count = xrayMapVertexCount;
            xrayMapMesh.Clear();
            if (count > 0)
            {
                xrayMapMesh.SetVertices(xrayMapVertices, 0, count);
                xrayMapMesh.SetColors(xrayMapColors, 0, count);
                xrayMapMesh.SetIndices(xrayMapIndices, 0, count, MeshTopology.Lines, 0, false);
                // Everything drawn lies inside the clip sphere; skips a pass over the vertices.
                xrayMapMesh.bounds = new Bounds(dronePosition, Vector3.one * (clipRadius * 2f));
            }

            xrayMapCachedDronePosition = QuantizeXrayMapKey(dronePosition);
            xrayMapCachedStructureRevision = xrayStructureRevision;
            xrayMapCacheValid = true;
        }

        private void DrawXrayMap()
        {
            Vector3 dronePosition;
            if (!TryGetXrayDronePosition(out dronePosition))
                return;

            RefreshXrayStructures(dronePosition);
            if (xrayWireMaterial == null)
                return;
            EnsureXrayMapMesh();
            if (xrayMapMesh == null)
                return;

            if (!IsXrayMapCacheCurrent(dronePosition))
                RebuildXrayMapMesh(dronePosition);
            if (xrayMapVertexCount == 0)
                return;

            xrayWireMaterial.SetPass(0);
            Graphics.DrawMeshNow(xrayMapMesh, Matrix4x4.identity);
        }

        private void DrawXrayDrones(Camera cam)
        {
            if (xrayWireMaterial == null)
                return;
            Transform root;
            if (spectatedEntry == null || !ghostDroneMap.TryGetValue(spectatedEntry, out root) || root == null)
                return;
            if (xrayDroneRoot != root)
            {
                xrayDroneRoot = root;
                CollectXrayDroneRenderers(root);
            }
            DrawXrayDroneBounds(cam, root.position);
        }

        // Picks up to three of the largest body meshes (frame/body preferred) so the
        // box hugs the airframe rather than props, antennas or the camera.
        private void CollectXrayDroneRenderers(Transform root)
        {
            xrayDroneRenderers.Clear();
            List<Renderer> preferredBodies = new List<Renderer>();
            List<Renderer> bodyCandidates = new List<Renderer>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || filter.sharedMesh == null)
                    continue;
                string name = filter.transform.name.ToLowerInvariant();
                if (name.Contains("prop") || name.Contains("blade") || name.Contains("motor")
                    || name.Contains("camera") || name.Contains("nano") || name.Contains("antenna")
                    || name.Contains("solder") || name.Contains("soupape") || name.Contains("led"))
                    continue;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer != null && renderer.bounds.size.sqrMagnitude > 0.04f)
                {
                    bodyCandidates.Add(renderer);
                    if (name.Contains("frame") || name.Contains("body") || name.Contains("chassis")
                        || name.Contains("airframe") || name.Contains("fixationframe"))
                        preferredBodies.Add(renderer);
                }
            }
            List<Renderer> selectedBodies = preferredBodies.Count > 0 ? preferredBodies : bodyCandidates;
            selectedBodies.Sort((a, b) => b.bounds.size.sqrMagnitude.CompareTo(a.bounds.size.sqrMagnitude));
            for (int i = 0; i < selectedBodies.Count && i < 3; i++)
                xrayDroneRenderers.Add(selectedBodies[i]);
        }

        private void DrawXrayDroneBounds(Camera cam, Vector3 dronePosition)
        {
            if (xrayDroneRenderers.Count == 0 || xrayWireMaterial == null)
                return;
            Renderer firstRenderer = xrayDroneRenderers[0];
            if (firstRenderer == null)
                return;
            Bounds bounds = firstRenderer.bounds;
            for (int i = 1; i < xrayDroneRenderers.Count; i++)
            {
                Renderer renderer = xrayDroneRenderers[i];
                if (renderer != null)
                    bounds.Encapsulate(renderer.bounds);
            }
            SetBoxCorners(xrayDroneBoundsCorners, bounds);

            Matrix4x4 worldToCamera = cam.worldToCameraMatrix;
            xrayWireMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.LoadIdentity();
            GL.Begin(GL.LINES);
            for (int i = 0; i < xrayBoxEdgePairs.Length; i += 2)
            {
                Vector3 drawA, drawB;
                float alphaA, alphaB;
                if (!TryClipXraySegmentToSphere(
                    xrayDroneBoundsCorners[xrayBoxEdgePairs[i]],
                    xrayDroneBoundsCorners[xrayBoxEdgePairs[i + 1]],
                    dronePosition,
                    XRAY_RENDER_RADIUS,
                    out drawA,
                    out drawB,
                    out alphaA,
                    out alphaB))
                    continue;
                GL.Color(new Color(1f, 1f, 1f, 0.95f * alphaA));
                GL.Vertex(worldToCamera.MultiplyPoint3x4(drawA));
                GL.Color(new Color(1f, 1f, 1f, 0.95f * alphaB));
                GL.Vertex(worldToCamera.MultiplyPoint3x4(drawB));
            }
            GL.End();
            GL.PopMatrix();
        }

        private int mainCamFrame = -1;
        private Camera mainCamCache = null;
    }
}
