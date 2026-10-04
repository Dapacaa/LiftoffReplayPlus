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
        private void RestoreGhostTrails()
        {
            foreach (var pair in ghostTrailStates)
            {
                if (pair.Value == null) continue;
                foreach (var state in pair.Value)
                {
                    if (state == null || state.trail == null) continue;
                    state.trail.enabled = state.enabled;
                    state.trail.emitting = state.emitting;
                }
            }
            ghostTrailStates.Clear();
            foreach (var pair in unmatchedGhostTrailStates)
            {
                if (pair.Value == null) continue;
                foreach (var state in pair.Value)
                {
                    if (state == null || state.trail == null) continue;
                    state.trail.enabled = state.enabled;
                    state.trail.emitting = state.emitting;
                }
            }
            unmatchedGhostTrailStates.Clear();
        }

        private void CacheGhostTrails(GhostLineEntry entry, Transform drone)
        {
            if (entry == null || drone == null)
                return;

            if (ghostTrailStates.ContainsKey(entry))
            {
                RestoreGhostTrailsForEntry(entry);
                ghostTrailStates.Remove(entry);
                ghostTrailsSuppressedSet.Remove(entry);
            }

            List<GhostTrailState> states;
            if (unmatchedGhostTrailStates.TryGetValue(drone, out states))
            {
                unmatchedGhostTrailStates.Remove(drone);
                ghostTrailStates[entry] = states;
                SetGhostTrailsSuppressed(entry, true);
                return;
            }

            states = new List<GhostTrailState>();
            foreach (var trail in drone.GetComponentsInChildren<TrailRenderer>(true))
            {
                if (trail == null) continue;
                states.Add(new GhostTrailState
                {
                    trail = trail,
                    enabled = trail.enabled,
                    emitting = trail.emitting
                });
                trail.Clear();
                trail.emitting = false;
                trail.enabled = false;
            }
            ghostTrailStates[entry] = states;
            SetGhostTrailsSuppressed(entry, true);
        }

        private void SuppressUnmatchedGhostTrails(Transform drone)
        {
            if (drone == null || unmatchedGhostTrailStates.ContainsKey(drone))
                return;

            // Do not re-capture a root that is already assigned to an entry.
            foreach (var pair in ghostDroneMap)
            {
                if (pair.Value == drone)
                    return;
            }

            var states = new List<GhostTrailState>();
            foreach (var trail in drone.GetComponentsInChildren<TrailRenderer>(true))
            {
                if (trail == null) continue;
                states.Add(new GhostTrailState
                {
                    trail = trail,
                    enabled = trail.enabled,
                    emitting = trail.emitting
                });
                trail.Clear();
                trail.emitting = false;
                trail.enabled = false;
            }
            unmatchedGhostTrailStates[drone] = states;
        }

        private void RestoreGhostTrailsForEntry(GhostLineEntry entry)
        {
            ghostTrailsSuppressedSet.Remove(entry);
            if (!ghostTrailStates.TryGetValue(entry, out var states) || states == null)
                return;
            foreach (var state in states)
            {
                if (state == null || state.trail == null) continue;
                state.trail.enabled = state.enabled;
                state.trail.emitting = state.emitting;
            }
        }

        private void SetGhostTrailsSuppressed(GhostLineEntry entry, bool suppressed)
        {
            if (suppressed)
            {
                if (ghostTrailsSuppressedSet.Contains(entry))
                    return;
                ghostTrailsSuppressedSet.Add(entry);
            }
            if (!ghostTrailStates.TryGetValue(entry, out var states) || states == null)
                return;

            foreach (var state in states)
            {
                if (state == null || state.trail == null) continue;
                if (suppressed)
                {
                    state.trail.Clear();
                    state.trail.emitting = false;
                    state.trail.enabled = false;
                }
                else
                {
                    state.trail.enabled = state.enabled;
                    state.trail.emitting = state.emitting;
                }
            }
        }

        private void SetAllRaceLinesVisible(bool visible)
        {
            foreach (var entry in GhostLinesPlugin.entries)
                SetRaceLineVisible(entry, visible);
        }

        private void SetRaceLineVisible(GhostLineEntry entry, bool visible)
        {
            if (entry == null) return;
            entry.visible = visible;
            if (entry.lineObject != null)
                entry.lineObject.SetActive(visible);
            if (entry.markerObject != null)
                entry.markerObject.SetActive(true);
            if (entry.markerTether != null)
                entry.markerTether.enabled = (GhostLinesPlugin.showNameTags && entry.nameTagVisible) || (GhostLinesPlugin.showDeltaDisplay && entry.deltaDisplayVisible);
            SetGhostTrailsSuppressed(entry, true);
            MarkPiPWindowsDirty();
        }

        private void DestroyActiveEntry(GhostLineEntry entry)
        {
            if (entry == null) return;
            Transform mappedRoot;
            if (ghostDroneMap.TryGetValue(entry, out mappedRoot))
            {
                if (xrayDroneRoot == mappedRoot)
                {
                    xrayDroneRoot = null;
                    xrayDroneRenderers.Clear();
                    InvalidateXrayMapCache();
                }
                // Remove unconditionally: a destroyed Transform compares == null,
                // so the old `if (mappedRoot != null)` guard leaked the key and
                // left ApplyGhostDroneTransforms iterating a dead entry.
                ghostDroneMap.Remove(entry);
            }
            ghostCamMountMap.Remove(entry);
            ghostSmoothedPos.Remove(entry);
            ghostSmoothedRot.Remove(entry);
            framePoses.Remove(entry);
            RestoreGhostTrailsForEntry(entry);
            ghostTrailStates.Remove(entry);
            SmoothDroneTrailState smoothTrail;
            if (smoothDroneTrailStates.TryGetValue(entry, out smoothTrail))
            {
                if (smoothTrail != null && smoothTrail.objectRoot != null)
                    UnityEngine.Object.Destroy(smoothTrail.objectRoot);
                smoothDroneTrailStates.Remove(entry);
            }
            GhostLinesPlugin.DestroyLineResources(entry);
            if (entry.markerObject != null) UnityEngine.Object.Destroy(entry.markerObject);
            entry.markerObject = null;
            entry.markerTether = null;
            entry.markerText = null;
            entry.deltaText = null;
            entry.guiDeltaText = "";
            entry.visible = false;
            entry.built = false;
            if (spectatedEntry == entry)
                spectatedEntry = null;
            if (referenceEntry == entry)
                referenceEntry = null;
            if (hoveredDroneLabelEntry == entry)
                SetHoveredDroneLabel(null);
            if (droneLabelClickCandidate == entry)
                droneLabelClickCandidate = null;
        }

        private void ClearActiveReplayEntries()
        {
            RestoreGhostTrails();
            foreach (var entry in GhostLinesPlugin.entries)
                DestroyActiveEntry(entry);
            GhostLinesPlugin.entries.Clear();
            GhostLineEntry.entriesEpoch++;
            ghostDroneMap.Clear();
            ghostSmoothedPos.Clear();
            ghostSmoothedRot.Clear();
            ClearFrameSnapshot();
            ghostCamMountMap.Clear();
            ClearSmoothDroneTrails();
            spectatedEntry = null;
            referenceEntry = null;
            referenceAutoInitialized = false;
            ghostDroneSearchTimer = 0f;
            ghostMatchAttempts = 0;
            ghostDronePosValid = false;
        }

        private object GetReplayRace()
        {
            if (replayController == null)
                return null;
            try
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                System.Type type = replayController.GetType();
                if (replayRaceFieldInfo == null && replayRacePropInfo == null)
                {
                    replayRaceFieldInfo = type.GetField("replayRace", flags);
                    if (replayRaceFieldInfo == null)
                        replayRacePropInfo = type.GetProperty("replayRace", flags);
                }
                if (replayRaceFieldInfo != null)
                    return replayRaceFieldInfo.GetValue(replayController);
                if (replayRacePropInfo != null && replayRacePropInfo.CanRead)
                    return replayRacePropInfo.GetValue(replayController, null);
                return null;
            }
            catch { return null; }
        }

        private bool ContainsReplayRecording(object obj, object recording, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || recording == null || depth > 6)
                return false;
            if (object.ReferenceEquals(obj, recording))
                return true;
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return false;
            if (obj is System.Array && obj.GetType().GetElementType() == typeof(byte))
                return false;
            if (obj is UnityEngine.Object && depth > 0)
                return false;
            if (!visited.Add(obj))
                return false;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                try
                {
                    foreach (var item in enumerable)
                    {
                        if (walked++ >= 256)
                            break;
                        if (ContainsReplayRecording(item, recording, depth + 1, visited))
                            return true;
                    }
                }
                catch { }
            }

            var fields = obj.GetType().GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                if (ContainsReplayRecording(child, recording, depth + 1, visited))
                    return true;
            }
            return false;
        }

        private void CollectReplayRecordingCandidates(object obj, int depth, System.Collections.Generic.HashSet<object> visited)
        {
            if (obj == null || depth > 6)
                return;
            if (obj is LocalGhostStatesRecording recording)
            {
                GhostLinesPlugin.AddRecordingCandidate(recording);
                return;
            }
            if (obj is string || obj is System.Type || obj is System.Reflection.MemberInfo || obj is System.Collections.IDictionary)
                return;
            if (obj is System.Array && obj.GetType().GetElementType() == typeof(byte))
                return;
            if (obj is UnityEngine.Object && depth > 0)
                return;
            if (!visited.Add(obj))
                return;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                int walked = 0;
                try
                {
                    foreach (var item in enumerable)
                    {
                        if (walked++ >= 256)
                            break;
                        CollectReplayRecordingCandidates(item, depth + 1, visited);
                    }
                }
                catch { }
            }

            var fields = obj.GetType().GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType == typeof(System.Type))
                    continue;
                object child = null;
                try { child = field.GetValue(obj); } catch { }
                CollectReplayRecordingCandidates(child, depth + 1, visited);
            }
        }

        private bool IsCandidateReadyForReplay(GhostLineEntry candidate, object replayRace)
        {
            if (candidate == null || candidate.GetRecording() == null || replayRace == null)
                return false;
            var recording = candidate.GetRecording();
            if (recording.states == null || recording.states.Count < 2)
                return false;
            if (object.ReferenceEquals(replayRace, replayController))
            {
                try
                {
                    return (recording.GameMode & LevelFlags.Replay) != 0;
                }
                catch { return false; }
            }
            // A recording found in this replay race stays in it, so only positive
            // results are cached; misses are re-checked (the race may still be
            // filling in).
            if (!object.ReferenceEquals(replayRecordingHitsRace, replayRace))
            {
                replayRecordingHitsRace = replayRace;
                replayRecordingHits.Clear();
            }
            if (replayRecordingHits.Contains(recording))
                return true;
            if (!ContainsReplayRecording(replayRace, recording, 0, new System.Collections.Generic.HashSet<object>()))
                return false;
            replayRecordingHits.Add(recording);
            return true;
        }

        private object replayRecordingHitsRace;
        private readonly System.Collections.Generic.HashSet<object> replayRecordingHits = new System.Collections.Generic.HashSet<object>();

        private void SyncActiveReplayEntries()
        {
            if (!replayReady || activeReplayRace == null)
                return;

            bool raceChanged = !object.ReferenceEquals(lastSyncedReplayRace, activeReplayRace);
            if (raceChanged)
                lastSyncedReplayRace = activeReplayRace;

            if (raceChanged || replayEntriesDirty)
            {
                replayEntriesDirty = false;
                for (int i = GhostLinesPlugin.entries.Count - 1; i >= 0; i--)
                {
                    var entry = GhostLinesPlugin.entries[i];
                    if (!IsCandidateReadyForReplay(entry, activeReplayRace))
                    {
                        DestroyActiveEntry(entry);
                        GhostLinesPlugin.entries.RemoveAt(i);
                        GhostLineEntry.entriesEpoch++;
                    }
                }

                foreach (var candidate in recordingCandidates)
                {
                    if (!IsCandidateReadyForReplay(candidate, activeReplayRace))
                        continue;
                    if (!GhostLinesPlugin.entries.Contains(candidate))
                    {
                        GhostLinesPlugin.entries.Add(candidate);
                        GhostLineEntry.entriesEpoch++;
                    }
                    GhostLinesPlugin.TryBuildLine(candidate);
                }
            }
            else
            {
                for (int i = GhostLinesPlugin.entries.Count - 1; i >= 0; i--)
                {
                    var entry = GhostLinesPlugin.entries[i];
                    if (entry.GetRecording() == null)
                    {
                        DestroyActiveEntry(entry);
                        GhostLinesPlugin.entries.RemoveAt(i);
                        GhostLineEntry.entriesEpoch++;
                    }
                }

                foreach (var candidate in recordingCandidates)
                {
                    if (candidate.GetRecording() == null)
                        continue;
                    if (!GhostLinesPlugin.entries.Contains(candidate))
                    {
                        GhostLinesPlugin.entries.Add(candidate);
                        GhostLineEntry.entriesEpoch++;
                    }
                    GhostLinesPlugin.TryBuildLine(candidate);
                }
            }
        }

        private bool TryGetFullyLoadedReplay(out object replayRace)
        {
            replayRace = null;
            FindReplayControls();
            if (replayControlsObj == null || !replayControlsObj.activeInHierarchy)
                return false;
            CacheReplayController();
            if (replayController == null)
                return false;

            replayRace = GetReplayRace();
            if (replayRace == null)
            {
                try
                {
                    if (replayController.MaximumTimelineTime <= 0.01f)
                        return false;
                }
                catch { return false; }
                replayRace = replayController;
            }
            return true;
        }

        private float replaySearchSceneChangeAt = -999f;

        // The replay-controls search is a scene-wide scan; only run it while there is
        // a plausible reason to: the native replay UI heartbeat, a recent scene change
        // (replay entry loads a scene), a recent leaderboard appearance ("watch replay"
        // entry point), or an active replay (rebinding refresh).
        private bool ReplaySearchArmed()
        {
            if (replayReady)
                return true;
            if (Time.unscaledTime - GhostLinesPlugin.replayUiSeenAt < 5f)
                return true;
            // With the Update hook in place the heartbeat above already fires the
            // moment replay controls exist. The time windows below re-ran the scan
            // for 15s after every scene change and 60s after any leaderboard: a
            // ~30ms hitch every few seconds on big maps (Marina Bay) with nothing
            // to find.
            if (GhostLinesPlugin.replayUiHeartbeatPatched)
                return false;
            if (Time.unscaledTime - replaySearchSceneChangeAt < 15f)
                return true;
            if (Time.unscaledTime - GhostLinesPlugin.leaderboardUiSeenAt < 60f)
                return true;
            return false;
        }

        private void FindReplayControls()
        {
            if (replayControlsObj != null && replayControlsObj.activeInHierarchy)
                return;
            // ReplayControls only ever exist in a flight/content scene; the armed
            // windows can stay open for a minute after UI was seen, which used to
            // run this scene-wide scan in menus.
            if (!IsContentSceneActive())
            {
                nextReplayControlsSearch = Time.unscaledTime + 0.5f;
                return;
            }
            if (!ReplaySearchArmed())
            {
                nextReplayControlsSearch = Time.unscaledTime + 0.5f;
                return;
            }
            if (Time.unscaledTime < nextReplayControlsSearch)
                return;
            if (mpInSession)
            {
                nextReplayControlsSearch = Time.unscaledTime + 5f;
                return;
            }
            ReplayControls[] controls = UnityEngine.Object.FindObjectsOfType<ReplayControls>(true);
            bool found = false;
            foreach (var control in controls)
            {
                if (control == null || !control.gameObject.scene.IsValid())
                    continue;

                GameObject root = control.gameObject;
                Transform current = control.transform;
                while (current != null)
                {
                    if (current.name.IndexOf("ReplayControlsUI", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        root = current.gameObject;
                        break;
                    }
                    current = current.parent;
                }

                bool changed = replayController != control || replayControlsObj != root;
                replayController = control;
                BindReplayControls(root);
                if (changed)
                    Debug.Log($"[GhostLines] Found ReplayControls component on {control.gameObject.name}");
                found = true;
                break;
            }
            if (found)
            {
                replayControlsSearchMisses = 0;
                nextReplayControlsSearch = Time.unscaledTime + 1f;
            }
            else
            {
                replayControlsSearchMisses++;
                nextReplayControlsSearch = Time.unscaledTime +
                    (replayControlsSearchMisses >= 4 ? 4f : replayControlsSearchMisses >= 2 ? 2f : 1f);
            }
        }

        private void BindReplayControls(GameObject controlsRoot)
        {
            if (controlsRoot == null)
                return;

            bool shouldHide = controlsHidden || replayReady;
            if (replayControlsObj != controlsRoot)
            {
                RestoreReplayControlsUI();
                controlsCG = null;
                controlsHidden = shouldHide;
            }

            replayControlsObj = controlsRoot;
            if (controlsCG == null)
            {
                controlsCG = replayControlsObj.GetComponent<CanvasGroup>();
                controlsCanvasGroupAdded = false;
            }

            if (controlsHidden)
                SetReplayControlsHidden(true);
        }

        private void CaptureReplayControlsState()
        {
            if (controlsCG == null || controlsStateCaptured)
                return;

            originalControlsAlpha = controlsCG.alpha;
            originalControlsInteractable = controlsCG.interactable;
            originalControlsBlocksRaycasts = controlsCG.blocksRaycasts;
            controlsStateCaptured = true;
        }

        private void SetReplayControlsHidden(bool hidden)
        {
            if (controlsCG == null && replayControlsObj != null)
            {
                controlsCG = replayControlsObj.AddComponent<CanvasGroup>();
                controlsCanvasGroupAdded = true;
            }
            if (controlsCG == null)
                return;

            CaptureReplayControlsState();
            controlsHidden = hidden;
            controlsCG.alpha = hidden ? 0f : 1f;
            controlsCG.interactable = !hidden;
            controlsCG.blocksRaycasts = !hidden;
        }

        public void EnforceNativeHudVisibility()
        {
            if (!replayReady)
            {
                EnforceMpHudVisibility();
                return;
            }

            if (nativeHudModuleParent == null)
            {
                nativeHudModuleParentStateCaptured = false;
                if (Time.unscaledTime < nextNativeHudResolve)
                    return;

                nextNativeHudResolve = Time.unscaledTime + 0.5f;
                if (nativeDroneHud == null)
                    nativeDroneHud = DroneHUD.use;
                if (nativeDroneHud == null || droneHudModuleParentField == null)
                    return;

                nativeHudModuleParent = droneHudModuleParentField.GetValue(nativeDroneHud) as Canvas;
                if (nativeHudModuleParent == null)
                    return;

                nativeHudModuleParentOriginalEnabled = nativeHudModuleParent.enabled;
                nativeHudModuleParentStateCaptured = true;
            }

            // Change-gated: this ran on every Canvas.willRenderCanvases event (2-4x/frame).
            if (nativeHudModuleParent.enabled)
                nativeHudModuleParent.enabled = false;
        }

        // Multiplayer: the drone HUD is drawn at the drone's camera point, so once the
        // camera leaves first person (MP freecam, spectate orbit, third person) it
        // floats in the world. Hide it for those views and put it back after.
        private Canvas mpHudCanvas;
        private bool mpHudHiddenByUs;
        private float mpHudResolveAt;

        private void EnforceMpHudVisibility()
        {
            bool inRoom = PhotonInRoom();
            bool wantHidden = inRoom && CameraAwayFromMpDrone();
            if (!wantHidden)
            {
                if (mpHudHiddenByUs)
                {
                    if (mpHudCanvas != null)
                        mpHudCanvas.enabled = true;
                    mpHudHiddenByUs = false;
                }
                if (!inRoom)
                    mpHudCanvas = null;
                return;
            }
            if (mpHudCanvas == null)
            {
                mpHudHiddenByUs = false;
                if (Time.unscaledTime < mpHudResolveAt || droneHudModuleParentField == null)
                    return;
                mpHudResolveAt = Time.unscaledTime + 0.5f;
                DroneHUD hud = DroneHUD.use;
                mpHudCanvas = hud != null ? droneHudModuleParentField.GetValue(hud) as Canvas : null;
                if (mpHudCanvas == null)
                    return;
            }
            if (mpHudCanvas.enabled)
            {
                mpHudCanvas.enabled = false;
                mpHudHiddenByUs = true;
            }
        }

        public void EnforceNativeReplayOverrides()
        {
            bool lockWanted = replayReady || mpFreeCamActive;
            if (GhostLinesPlugin.blockDroneReset != lockWanted)
                ModLog.LogFile("[GhostLines] FLAG " + lockWanted + " poller=" + GetInstanceID() +
                    " replay=" + replayReady + " freecam=" + mpFreeCamActive);
            GhostLinesPlugin.blockDroneReset = lockWanted;
            if (!lockWanted)
            {
                RestoreNativeReplayOverrides();
                return;
            }
            if (replayReady)
            {
                if (nativeWarningsRoot == null)
                {
                    if (Time.unscaledTime >= nextNativeWarningsResolve)
                    {
                        // Back off after a few misses: the subtree traversal is not
                        // free and the warning may simply not exist on this map.
                        nextNativeWarningsResolve = Time.unscaledTime +
                            (nativeWarningsResolveMisses >= 3 ? 5f : 0.5f);
                        if (nativeDroneHud == null)
                            nativeDroneHud = DroneHUD.use;
                        if (nativeDroneHud != null)
                        {
                            GameObject warnings = FindDescendantGameObject(nativeDroneHud.transform, "FlyableZoneWarningTest");
                            if (warnings != null)
                            {
                                nativeWarningsOriginalActive = warnings.activeSelf;
                                nativeWarningsRoot = warnings;
                                nativeWarningsResolveMisses = 0;
                                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] RPLY warnings resolved go=" + warnings.name);
                            }
                            else
                            {
                                nativeWarningsResolveMisses++;
                            }
                        }
                    }
                }
                if (nativeWarningsRoot != null && nativeWarningsRoot.activeSelf)
                    nativeWarningsRoot.SetActive(false);
            }

            if (!nativeDroneResetLocked)
            {
                if (flightManagerInstance == null)
                {
                    if (flightManagerUseGetter != null)
                    {
                        try
                        {
                            flightManagerInstance = flightManagerUseGetter.Invoke(null, null) as FlightManager;
                        }
                        catch
                        {
                        }
                    }
                    if (flightManagerInstance == null && Time.unscaledTime >= nextFlightManagerScanAt)
                    {
                        // Resources.FindObjectsOfTypeAll is a full loaded-object
                        // scan; this used to run every frame while unresolved.
                        nextFlightManagerScanAt = Time.unscaledTime + 0.5f;
                        FlightManager[] fms = Resources.FindObjectsOfTypeAll<FlightManager>();
                        if (fms != null && fms.Length > 0)
                            flightManagerInstance = fms[0];
                    }
                }
                if (flightManagerInstance != null)
                {
                    try
                    {
                        flightManagerInstance.LockDroneReset(nativeDroneResetLockToken);
                        nativeDroneResetLocked = true;
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] drone reset locked poller=" + GetInstanceID() +
                            " replay=" + replayReady + " freecam=" + mpFreeCamActive);
                    }
                    catch (System.Exception lockEx)
                    {
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] RPLY lock EX " + lockEx);
                    }
                }
            }
            else
            {
                // Unity-null once the scene destroyed it.
                if (flightManagerInstance == null)
                    nativeDroneResetLocked = false;
            }

            if (replayReady || mpFreeCamActive)
            {
                if (flyableZoneCheckerType == null)
                    flyableZoneCheckerType = ResolveNativeType("FlyableZoneChecker");
                if (flyableZoneCheckerType != null && Time.unscaledTime >= nextNativeZoneCheckersResolve)
                {
                    nextNativeZoneCheckersResolve = Time.unscaledTime + 2f;
                    for (int ci = nativeZoneCheckers.Count - 1; ci >= 0; ci--)
                    {
                        if (nativeZoneCheckers[ci] == null)
                        {
                            nativeZoneCheckers.RemoveAt(ci);
                            continue;
                        }
                        nativeZoneCheckers[ci].enabled = false;
                    }
                    // Once every scene checker is captured and disabled, the expensive
                    // Resources.FindObjectsOfTypeAll sweep is pure waste — it only runs
                    // again if the cache empties (scene change restores it via ResetState).
                    if (nativeZoneCheckers.Count > 0)
                        nextNativeZoneCheckersResolve = float.MaxValue;
                    else
                    {
                        UnityEngine.Object[] checkers = Resources.FindObjectsOfTypeAll(flyableZoneCheckerType);
                        if (checkers != null)
                        {
                            foreach (UnityEngine.Object obj in checkers)
                            {
                                MonoBehaviour mb = obj as MonoBehaviour;
                                if (mb == null || mb.gameObject == null)
                                    continue;
                                if (!mb.gameObject.activeInHierarchy)
                                    continue;
                                try
                                {
                                    if (!mb.gameObject.scene.IsValid())
                                        continue;
                                }
                                catch
                                {
                                    continue;
                                }
                                if (!nativeZoneCheckers.Contains(mb))
                                    nativeZoneCheckers.Add(mb);
                                mb.enabled = false;
                            }
                            if (nativeZoneCheckers.Count > 0)
                                nextNativeZoneCheckersResolve = float.MaxValue;
                        }
                    }
                }
            }
        }

        private static System.Type ResolveNativeType(string typeName)
        {
            return GameAccess.FindGameTypeByFullName(typeName);
        }

        public void RestoreNativeReplayOverrides()
        {
            bool didAnything = nativeWarningsRoot != null || nativeDroneResetLocked || nativeZoneCheckers.Count > 0;
            if (nativeWarningsRoot != null)
            {
                if (nativeWarningsRoot.activeSelf != nativeWarningsOriginalActive)
                    nativeWarningsRoot.SetActive(nativeWarningsOriginalActive);
                nativeWarningsRoot = null;
            }
            nextNativeWarningsResolve = 0f;
            nativeWarningsResolveMisses = 0;
            nextFlightManagerScanAt = 0f;
            if (nativeDroneResetLocked && flightManagerInstance != null)
            {
                try
                {
                    flightManagerInstance.UnlockDroneReset(nativeDroneResetLockToken);
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] drone reset unlocked");
                }
                catch (System.Exception unlockEx)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] RPLY unlock EX " + unlockEx);
                }
            }
            nativeDroneResetLocked = false;
            flightManagerInstance = null;
            for (int ci = nativeZoneCheckers.Count - 1; ci >= 0; ci--)
            {
                if (nativeZoneCheckers[ci] != null)
                    nativeZoneCheckers[ci].enabled = true;
            }
            nativeZoneCheckers.Clear();
            nextNativeZoneCheckersResolve = 0f;
            GhostLinesPlugin.blockDroneReset = false;
            if (didAnything)
                ModLog.LogFile("[GhostLines] RPLY overrides restored");
        }

        private void DiagGateState()
        {
            if (Time.unscaledTime < nextGateDiag)
                return;
            nextGateDiag = Time.unscaledTime + 1f;
            if (!ModLog.DebugEnabled)
                return;
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] DIAG tick poller=" + GetInstanceID() +
                " replay=" + replayReady + " freecam=" + mpFreeCamActive +
                " flag=" + GhostLinesPlugin.blockDroneReset);
            if (Time.unscaledTime < nextGateDiagHeavy)
                return;
            nextGateDiagHeavy = Time.unscaledTime + 5f;
            if (!replayReady && !mpFreeCamActive)
                return;
            try
            {
                string line = "[GhostLines] GATE lock=" + (replayReady || mpFreeCamActive) +
                    " replay=" + replayReady + " freecam=" + mpFreeCamActive +
                    " flag=" + GhostLinesPlugin.blockDroneReset +
                    " fmInst=" + (flightManagerInstance != null ? "1" : "0") +
                    " fmLocked=" + nativeDroneResetLocked +
                    " checkers=" + nativeZoneCheckers.Count;

                if (flyableZoneCheckerType != null)
                {
                    if (gateDiagInZone == null)
                    {
                        System.Reflection.BindingFlags flags =
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance;
                        gateDiagInZone = flyableZoneCheckerType.GetField("inZone", flags);
                        gateDiagAllowedHeight = flyableZoneCheckerType.GetField("allowedHeight", flags);
                        gateDiagOutOfZoneTimer = flyableZoneCheckerType.GetField("outOfZoneTimer", flags);
                    }
                    // The cached checkers, not a Resources.FindObjectsOfTypeAll sweep:
                    // that cost ~35ms every 5s in MP freecam on big maps.
                    List<MonoBehaviour> chk = nativeZoneCheckers;
                    if (chk.Count > 0)
                    {
                        MonoBehaviour first = chk[0];
                        if (first != null)
                        {
                            line += " chkEnabled=" + first.enabled + " chkCount=" + chk.Count;
                            if (gateDiagInZone != null)
                                line += " inZone=" + gateDiagInZone.GetValue(first);
                            if (gateDiagAllowedHeight != null)
                                line += " h=" + gateDiagAllowedHeight.GetValue(first);
                            if (gateDiagOutOfZoneTimer != null)
                                line += " t=" + gateDiagOutOfZoneTimer.GetValue(first);
                        }
                        else
                        {
                            line += " chkCount=" + chk.Count;
                        }
                    }
                }

                if (gateDiagFade == null)
                {
                    if (Time.unscaledTime >= nextGateDiagFadeResolve)
                    {
                        nextGateDiagFadeResolve = Time.unscaledTime + 2f;
                        gateDiagFade = GameObject.Find("JESUS/ScreenFader");
                    }
                }
                if (gateDiagFade != null)
                {
                    CanvasGroup cg = gateDiagFade.GetComponent<CanvasGroup>();
                    UnityEngine.UI.Image img = gateDiagFade.GetComponentInChildren<UnityEngine.UI.Image>(true);
                    line += " fade=" + (cg != null ? cg.alpha.ToString("F2") : "n/a") +
                        (img != null ? "/" + img.color.a.ToString("F2") : "");
                }

                if (gateDiagLocalDrone == null)
                {
                    if (Time.unscaledTime >= nextGateDiagLocalDroneResolve)
                    {
                        nextGateDiagLocalDroneResolve = Time.unscaledTime + 3f;
                        gateDiagLocalDrone = FindLocalDroneRoot();
                        gateDiagLastDronePosValid = false;
                    }
                }
                if (gateDiagLocalDrone != null)
                {
                    Vector3 p = gateDiagLocalDrone.position;
                    if (gateDiagLastDronePosValid)
                    {
                        float d = Vector3.Distance(p, gateDiagLastDronePos);
                        if (d > 10f)
                            ModLog.LogFile("[GhostLines] GATE drone JUMP " + d.ToString("F1") + "m " +
                                gateDiagLastDronePos + " -> " + p);
                    }
                    gateDiagLastDronePos = p;
                    gateDiagLastDronePosValid = true;
                    line += " drone=" + p;
                }

                if (ModLog.DebugEnabled) ModLog.LogFile(line);
            }
            catch (System.Exception gateEx)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] GATE diag EX " + gateEx);
            }
        }

        private Transform FindLocalDroneRoot()
        {
            // Only once the MP scan has run (as before): skips a full object scan
            // in sessions that never went multiplayer.
            if (!photonScanRan)
                return null;
            try
            {
                List<Photon.Pun.PhotonView> views = CollectPhotonViews();
                if (views != null)
                {
                    foreach (Photon.Pun.PhotonView view in views)
                    {
                        if (view == null || view.gameObject == null || !view.gameObject.activeInHierarchy)
                            continue;
                        bool isMine = false;
                        try { isMine = view.IsMine; } catch { }
                        if (!isMine)
                            continue;
                        Transform root = view.transform.root;
                        if (root != null && root.name.StartsWith("Drone_", System.StringComparison.OrdinalIgnoreCase))
                            return root;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static GameObject FindDescendantGameObject(Transform root, string name)
        {
            if (root == null)
                return null;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == null)
                    continue;
                if (child.name == name)
                    return child.gameObject;
            }
            return null;
        }

        public void EnforceNativeSpectatedTextStyle()
        {
            if (!replayReady)
            {
                RestoreNativeStandingsTextStyles();
                return;
            }

            if (nativeStandingsEntriesEpochSeen != GhostLineEntry.entriesEpoch)
            {
                nativeStandingsEntriesEpochSeen = GhostLineEntry.entriesEpoch;
                nativeStandingsTextLastContent.Clear();
                nativeStandingsEntryCache.Clear();
            }

            if (Time.unscaledTime >= nextNativeStandingsResolve)
            {
                nextNativeStandingsResolve = Time.unscaledTime + 0.5f;
                UnityEngine.SceneManagement.Scene activeScene =
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                bool sceneChanged = !nativeStandingsSceneValid ||
                    activeScene.handle != nativeStandingsSceneHandle;
                bool hasDeadTexts = false;
                for (int i = 0; i < nativeStandingsTexts.Count; i++)
                {
                    if (nativeStandingsTexts[i] == null)
                    {
                        hasDeadTexts = true;
                        break;
                    }
                }
                if (sceneChanged)
                {
                    nativeStandingsSceneHandle = activeScene.handle;
                    nativeStandingsSceneValid = true;
                    nativeStandingsEmptyScans = 0;
                    nativeStandingsBaseStyles.Clear();
                    ResolveNativeStandingsTexts();
                }
                else if (hasDeadTexts)
                {
                    nativeStandingsBaseStyles.Clear();
                    ResolveNativeStandingsTexts();
                }
                else if (nativeStandingsTexts.Count == 0 && nativeStandingsEmptyScans < 3 &&
                    Time.unscaledTime >= nextNativeStandingsEmptyScan)
                {
                    nativeStandingsEmptyScans++;
                    nextNativeStandingsEmptyScan = Time.unscaledTime + 5f;
                    nativeStandingsBaseStyles.Clear();
                    ResolveNativeStandingsTexts();
                }
            }

            foreach (Text text in nativeStandingsTexts)
            {
                if (text == null)
                    continue;

                string content = text.text;
                string lastContent;
                GhostLineEntry entry;
                if (!nativeStandingsTextLastContent.TryGetValue(text, out lastContent) || lastContent != content)
                {
                    entry = FindEntryForNativeStandingsText(content);
                    nativeStandingsTextLastContent[text] = content;
                    nativeStandingsEntryCache[text] = entry;
                }
                else
                {
                    nativeStandingsEntryCache.TryGetValue(text, out entry);
                }

                FontStyle baseStyle;
                if (!nativeStandingsBaseStyles.TryGetValue(text, out baseStyle))
                    baseStyle = NormalizeNativeStandingsStyle(text.fontStyle);
                FontStyle desiredStyle = entry != null && entry == spectatedEntry
                    ? MakeBoldNativeStandingsStyle(baseStyle)
                    : baseStyle;
                if (text.fontStyle != desiredStyle)
                {
                    text.fontStyle = desiredStyle;
                    text.SetVerticesDirty();
                }
            }
        }

        private void ResolveNativeStandingsTexts()
        {
            foreach (Text staleText in nativeStandingsBaseStyles.Keys)
            {
                if (staleText == null)
                    continue;
                nativeStandingsTextLastContent.Remove(staleText);
                nativeStandingsEntryCache.Remove(staleText);
            }
            nativeStandingsBaseStyles.Clear();
            Text[] allTexts = UnityEngine.Object.FindObjectsOfType<Text>(true);
            List<Text> resolved = new List<Text>();
            foreach (Text text in allTexts)
            {
                if (text == null || text.gameObject.name != "TextPrimaryInfo")
                    continue;

                bool inPlayerActivity = false;
                string path = text.gameObject.name;
                for (Transform current = text.transform.parent; current != null; current = current.parent)
                {
                    path = current.name + "/" + path;
                    if (current.name == "PlayerActivityBar" || current.name.StartsWith("PlayerActivity", System.StringComparison.Ordinal))
                    {
                        inPlayerActivity = true;
                        break;
                    }
                }
                if (!inPlayerActivity && path.IndexOf("PlayerActivityBar/", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                resolved.Add(text);
                if (!nativeStandingsBaseStyles.ContainsKey(text))
                    nativeStandingsBaseStyles[text] = NormalizeNativeStandingsStyle(text.fontStyle);
            }

            nativeStandingsTexts = resolved;
        }

        private GhostLineEntry FindEntryForNativeStandingsText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string displayedName = NormalizeNativeStandingsName(text);

            foreach (GhostLineEntry entry in GhostLinesPlugin.entries)
            {
                string pilotName = NormalizeNativeStandingsName(GhostLinesPlugin.CleanPilotName(entry.pilotName));
                if (!string.IsNullOrWhiteSpace(pilotName) &&
                    (string.Equals(displayedName, pilotName, System.StringComparison.OrdinalIgnoreCase) ||
                     displayedName.StartsWith(pilotName + " - ", System.StringComparison.OrdinalIgnoreCase)))
                    return entry;
            }
            return null;
        }

        private static string NormalizeNativeStandingsName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string result = System.Text.RegularExpressions.Regex.Replace(value, "<.*?>", "");
            result = result.Replace('\u00A0', ' ').Trim();
            int suffixIndex = result.IndexOf(" - ", System.StringComparison.Ordinal);
            if (suffixIndex >= 0)
                result = result.Substring(0, suffixIndex).Trim();
            return result;
        }

        private static FontStyle NormalizeNativeStandingsStyle(FontStyle style)
        {
            return (style & FontStyle.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;
        }

        private static FontStyle MakeBoldNativeStandingsStyle(FontStyle style)
        {
            return (style & FontStyle.Italic) != 0 ? FontStyle.BoldAndItalic : FontStyle.Bold;
        }

        private void RestoreNativeStandingsTextStyles()
        {
            foreach (KeyValuePair<Text, FontStyle> pair in nativeStandingsBaseStyles)
            {
                if (pair.Key != null)
                {
                    if (pair.Key.fontStyle != pair.Value)
                    {
                        pair.Key.fontStyle = pair.Value;
                        pair.Key.SetVerticesDirty();
                    }
                }
            }
            nativeStandingsTexts.Clear();
            nativeStandingsBaseStyles.Clear();
            nextNativeStandingsResolve = 0f;
            // Force a fresh scan on the next replay entry, even in the same scene.
            nativeStandingsSceneValid = false;
            nativeStandingsEmptyScans = 0;
        }

        private void RestoreNativeHudVisibility()
        {
            if (nativeHudModuleParentStateCaptured && nativeHudModuleParent != null)
                nativeHudModuleParent.enabled = nativeHudModuleParentOriginalEnabled;
            nativeHudModuleParentStateCaptured = false;
            nativeHudModuleParent = null;
            nativeDroneHud = null;
            nextNativeHudResolve = 0f;
        }

        private void RestoreReplayControlsUI()
        {
            controlsHidden = false;
            if (controlsCG != null && controlsStateCaptured)
            {
                controlsCG.alpha = originalControlsAlpha;
                controlsCG.interactable = originalControlsInteractable;
                controlsCG.blocksRaycasts = originalControlsBlocksRaycasts;
            }
            if (controlsCG != null && controlsCanvasGroupAdded)
            {
                Destroy(controlsCG);
                controlsCG = null;
            }

            controlsStateCaptured = false;
            controlsCanvasGroupAdded = false;
        }

        // Reset contract: DeactivateReplay (replay exit) and ResetState (active scene
        // change) share ResetReplayScope, so the replay-scoped list exists once.
        // Each feature helper below both clears its state and destroys the
        // UnityEngine.Objects it created; OnDestroy reuses the same helpers.
        private void DeactivateReplay()
        {
            RestoreHoldZoom();
            CloseAllPiPWindows();
            RestoreNativeStandingsTextStyles();
            int cameraModeToRestore = replayEntryCameraMode != 0
                ? replayEntryCameraMode
                : pendingCameraModeRestore != 0 ? pendingCameraModeRestore : lastNativeCameraMode;
            if (cameraModeToRestore != 0)
                SetNativeCameraMode(cameraModeToRestore, false);
            RestoreReplayControlsUI();
            RestoreNativeHudVisibility();
            RestoreNativeReplayOverrides();
            ClearActiveReplayEntries();
            recordingCandidates.Clear();
            recordingCandidateSerial = 0;
            // Drop references into the finished replay's state.
            recordedOwners.Clear();
            recordedOwnersRace = null;
            replayRecordingHits.Clear();
            replayRecordingHitsRace = null;
            ResetReplayScope();
        }

        // State that only lives while a replay is active.
        private void ResetReplayScope()
        {
            replayReady = false;
            activeReplayRace = null;
            settingsOpen = false;
            playbackMouseCaptured = false;
            playbackPinned = false;
            settingsPinned = false;
            settingsFadeAmount = 0f;
            DisposeInputGraphs();
            ResetXrayState();
            ClearFrameSnapshot();
            overridePlayback = false;
            playbackSpeed = 1f;
            isPlaying = false;
            frameStepPaused = false;
            restartRequested = false;
            restartPlaybackPending = false;
            loopReplayEnabled = false;
            trimEnabled = false;
            trimStart = 0f;
            trimEnd = 0f;
            draggingTrimStart = false;
            draggingTrimEnd = false;
            trimResumePlayback = false;
            pendingCameraViewRestore = false;
            pendingFreeCamViewRestore = false;
            pendingOrbitViewRestore = false;
            pendingPlayToggle = false;
            fpvOnReplayPending = false;
            customCameraActive = false;
            startedFromFpv = false;
            pendingThirdPersonScroll = 0f;
            freeCamActive = false;
            isOrbiting = false;
            CancelCameraLook();
            lastNativeCameraMode = 0;
            pendingCameraModeRestore = 0;
            replayEntryCameraMode = 0;
            draggingStickCam = false;
            // Stale overlay rects would stay "valid" and act as phantom interactive
            // areas after leaving replay.
            currentStickCamOverlayRectValid = false;
            currentPlaybackOverlayRectValid = false;
        }

        // Stick graphs and live graphs each own a trace Texture2D.
        private void DisposeInputGraphs()
        {
            for (int si = 0; si < stickGraphs.Count; si++)
            {
                if (stickGraphs[si].traceTex != null)
                    Destroy(stickGraphs[si].traceTex);
            }
            stickGraphs.Clear();
            for (int li = 0; li < liveInputGraphs.Count; li++)
            {
                if (liveInputGraphs[li].traceTex != null)
                    Destroy(liveInputGraphs[li].traceTex);
            }
            liveInputGraphs.Clear();
        }

        private void ResetXrayState()
        {
            xrayStructures.Clear();
            xrayStructureById.Clear();
            xrayNextStructureById.Clear();
            xrayRejectedById.Clear();
            xrayStructureCenterValid = false;
            InvalidateXrayMapCache();
            xrayDroneRenderers.Clear();
            xrayDroneRoot = null;
        }

        private void UpdateReplayLifecycle()
        {
            object replayRace;
            if (!TryGetFullyLoadedReplay(out replayRace))
            {
                if (replayReady)
                    DeactivateReplay();
                return;
            }

            if (!replayReady || !object.ReferenceEquals(activeReplayRace, replayRace))
            {
                if (replayReady)
                    DeactivateReplay();
                activeReplayRace = replayRace;
                replayReady = true;
                settingsOpen = savedSettingsPanelOpen;
                settingsFadeAmount = settingsOpen ? 1f : 0f;
                replayEnterTime = Time.unscaledTime;
                CaptureReplayEntryCameraMode();
                fpvOnReplayPending = true;
                fpvForceTimeout = Time.unscaledTime + 3f;
                SetReplayControlsHidden(true);
                CollectReplayRecordingCandidates(replayRace, 0, new System.Collections.Generic.HashSet<object>());
                SyncActiveReplayEntries();
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Replay fully loaded; activating Ghost Lines");
            }

            if (Time.unscaledTime >= nextReplaySync)
            {
                nextReplaySync = Time.unscaledTime + 0.5f;
                SyncActiveReplayEntries();
            }
        }

        private void InitializeDefaultReferenceEntry()
        {
            if (referenceAutoInitialized) return;
            if (referenceEntry != null)
            {
                referenceAutoInitialized = true;
                return;
            }
            if (spectatedEntry == null) return;
            referenceEntry = spectatedEntry;
            referenceAutoInitialized = true;
            if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Default delta comparison: {referenceEntry.pilotName}");
        }

        // Scene-scoped reset: runs when the active scene changes (not for additive
        // loads). Clears the replay scope plus everything tied to the old scene.
        public void ResetState()
        {
            RestoreHoldZoom();
            RestoreNativeStandingsTextStyles();
            CleanupLeaderboardSearch();
            if (replayReady)
                DeactivateReplay();
            else
            {
                RestoreNativeHudVisibility();
                RestoreReplayControlsUI();
                RestoreNativeReplayOverrides();
                ClearActiveReplayEntries();
                ResetReplayScope();
            }
            nextReplaySync = 0f;
            replayControlsObj = null;
            nextReplayControlsSearch = 0f;
            replayControlsSearchMisses = 0;
            controlsCG = null;
            controlsHidden = false;
            liveGraphSwitchOpen = false;
            droneSwitchDropdownOpen = false;
            stickCamInstances.Clear();
            currentStickCamInstance = null;
            stickCamResizeTarget = null;
            lastStickCamSpectateEntry = null;
            // Stale chat state keeps OnGUI off its fast path.
            mpChatStates.Clear();
            mpChatLastChildCount = -1;
            mpChatMessageParent = null;
            CloseMultiplayerPipWindows();
            // The exit paths only restore while targets exist, so restore here
            // before dropping them, or the MP session mirror sticks.
            if (multiplayerPipTargets.Count > 0)
                RestoreSavedNameTags();
            multiplayerPipTargets.Clear();
            mpInputCache.Clear();
            mpInputCacheValid.Clear();
            StopLobbyWatcher("state reset");
            settingsDrag.Cancel();
            settingsRect = new Rect(-1f, -1f, 400f, 360f);
            // Cached handles into the old scene's native replay UI / camera.
            sliderComponent = null;
            replayController = null;
            attachToDroneMethod = null;
            attachToDroneParameterType = null;
            flightCamCtrl = null;
            flightCamImpl = null;
            cameraModeSetMethod = null;
            cameraModeEnumType = null;
            fpvForceLogged = false;
            referenceAutoInitialized = false;
            orbitTarget = null;
            cameraDumped = false;
            noTargetLogged = false;
            spectatedEntry = null;
            referenceEntry = null;
            ClearSmoothDroneTrails();
            ghostDroneMap.Clear();
            ghostSmoothedPos.Clear();
            ghostSmoothedRot.Clear();
            ClearFrameSnapshot();
            ghostCamMountMap.Clear();
            ghostDroneSearchTimer = 0f;
            ghostMatchAttempts = 0;
            lateIsThirdPerson = true;
            ghostDronePosValid = false;
            ghostDronePos = Vector3.zero;
            mpFreeCamActive = false;
            mpZoomActive = false;
            mpZoomFovCaptured = false;
            mpInSession = false;
        }
    }
}
