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
    public class GhostLineEntry
    {
        public System.WeakReference recordingRef;
        public LocalGhostStatesRecording GetRecording()
        {
            return recordingRef != null && recordingRef.IsAlive ? recordingRef.Target as LocalGhostStatesRecording : null;
        }
        public int hash;
        public string pilotName;
        public Color color;
        public bool visible = true;
        public bool xrayVisible = false;
        public bool nameTagVisible = true;
        public GameObject lineObject;
        public Material lineMaterial;
        public GameObject markerObject;
        public LineRenderer markerTether;
        public TextMesh markerText;
        public MeshRenderer markerRendererCache;
        public Color lastMarkerColor = new Color(-1f, -1f, -1f, -1f);
        public Vector3[] originalPositions;
        public Quaternion[] originalRotations;
        public Vector4[] originalInputs;
        public float[] stateTimes;
        // Pose-only copy of the states on the regular 0.1 s recording grid (see
        // GhostLinesPlugin.BuildPoseSamples). Other features keep the full arrays.
        public float[] poseTimes;
        public Vector3[] posePositions;
        public Quaternion[] poseRotations;
        public bool built;
        public float lapStartOffset;
        public float finishTime = -1f;
        public float markerBaseHeight = 2.8f;
        public float[] pathDistances;
        // Speed (m/s) per pose sample, aligned with poseTimes/posePositions.
        public float[] speedSamples;
        // Per-vertex heatmap colours, sampled along the line (see RebuildLine).
        public Texture2D heatmapTex;
        public float lapStartDistance;
        public float lapTotalDistance;
        public TextMesh deltaText;
        public string guiDeltaText = "";
        public string resolvedReplayName;
        public bool replayNameResolved = false;
        public Color? resolvedReplayColor;
        public bool replayColorResolved = false;
        // Resolve attempts against the replay state (1/sec); capped so an entry
        // that can never resolve stops paying for the reflective walk.
        public int replayResolveTries = 0;
        public string lastShownDeltaText = "";
        public float lastDeltaShownValue = float.NaN;
        public float lastDeltaRawValue = float.NaN;
        public float lastDeltaUpdateTime = -1f;
        public float lastDeltaReplayTime = float.NaN;
        public float lastRefMatchDist = float.NaN;
        public float lastRefMatchReplayTime = float.NaN;
        public float lastRefDistNow = float.NaN;
        public bool deltaDisplayVisible = true;
        public static int entriesEpoch;
    }
}
