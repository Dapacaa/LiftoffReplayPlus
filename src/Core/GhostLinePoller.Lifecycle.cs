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
        private void Start()
        {
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] GhostLinePoller started");
            // The mod never uses GUILayout, so skip the Layout event: it only ran
            // every OnGUI draw one extra time per frame.
            useGUILayout = false;
            Camera.onPreCull += OnCameraPreRender;
            Camera.onPostRender += OnCameraPostRender;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreRender;
            Camera.onPostRender -= OnCameraPostRender;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            CleanupLeaderboardSearch();
            CloseAllNativePipWindows();
            if (nativePipReadTex != null) Destroy(nativePipReadTex);
            nativePipReadTex = null;
            if (xrayWireMaterial != null) Destroy(xrayWireMaterial);
            xrayWireMaterial = null;
            if (xrayMapMesh != null) Destroy(xrayMapMesh);
            xrayMapMesh = null;
            DestroyTexture(ref whiteTex);
            DestroyTexture(ref roundedBtnTex);
            DestroyTexture(ref stickRingTex);
            DestroyTexture(ref stickCloseTex);
            raceGraphRaster.Release();
            DestroyTexture(ref iconPlay);
            DestroyTexture(ref iconPause);
            DestroyTexture(ref iconRewind);
            DestroyTexture(ref iconFF);
            DestroyTexture(ref iconLoop);
            DestroyTexture(ref iconTrim);
            DestroyTexture(ref iconSettings);
            DestroyTexture(ref iconPin);
            DestroyTexture(ref iconBindings);
            DestroyTexture(ref lobbyCircleOnTex);
            DestroyTexture(ref lobbyCircleOffTex);
            DestroyTexture(ref targetRingTex);
            DestroyTexture(ref targetDotTex);
            DestroyTexture(ref targetFillTex);
            DestroyTexture(ref mpChatWheelTex);
            DestroyTexture(ref mpChatRampTex);
            DestroyTexture(ref mpChatWheelMarkerTex);
            DestroyTexture(ref mpChatRoundBtnTex);
            DestroyTexture(ref raceHistoryFieldTex);
            DestroyTexture(ref raceHistoryFieldFocusTex);
            DestroyTexture(ref raceScrollTrackTex);
            DestroyTexture(ref raceScrollThumbTex);
            DestroyTexture(ref raceScrollThumbHoverTex);
            iconsInitialized = false;
            DisposeInputGraphs();
            ResetXrayState();
            RestoreReplayControlsUI();
            RestoreNativeHudVisibility();
            RestoreNativeStandingsTextStyles();
            RestoreHoldZoom();
            CancelCameraLook();
            ClearActiveReplayEntries();
            CloseAllPiPWindows();
            CloseMultiplayerPipWindows();
            if (multiplayerPipTargets.Count > 0)
                RestoreSavedNameTags();
            multiplayerPipTargets.Clear();
            StopLobbyWatcher("poller destroyed");
            lbProvisioning = false;
            // Unhook the Unity log pump and free persistent materials so a plugin
            // reload does not stack handlers or leak HideAndDontSave materials.
            if (wsLogHooked)
            {
                Application.logMessageReceived -= OnUnityLogMessage;
                wsLogHooked = false;
            }
            // Photon keeps callback targets for the whole session; a stale target
            // would keep calling into this destroyed poller and a recreated poller
            // would register a second one.
            if (raceEventProbe != null)
            {
                try { Photon.Pun.PhotonNetwork.RemoveCallbackTarget(raceEventProbe); } catch { }
                raceEventProbe = null;
            }
            raceEventProbeHooked = false;
            if (lobbyReturnCallbacks != null)
            {
                try { Photon.Pun.PhotonNetwork.RemoveCallbackTarget(lobbyReturnCallbacks); } catch { }
                lobbyReturnCallbacks = null;
            }
            matchmakingHooked = false;
            if (throughWallTetherMaterial != null) Destroy(throughWallTetherMaterial);
            throughWallTetherMaterial = null;
            if (smoothDroneTrailMaterial != null) Destroy(smoothDroneTrailMaterial);
            smoothDroneTrailMaterial = null;
            GhostLinesPlugin.RestoreGcMode();
            Prefs.FlushSave();
            ModLog.FlushLogFile();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            SaveCurrentOverlayPositions();
            SaveDisplayPreferences();
            Prefs.FlushSave();
            ModLog.CloseLogFile();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                isOrbiting = false;
                CancelCameraLook();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                isOrbiting = false;
                CancelCameraLook();
            }
        }

        private void OnDisable()
        {
            isOrbiting = false;
            RestoreHoldZoom();
            CancelCameraLook();
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture != null)
                Destroy(texture);
            texture = null;
        }

        // Any load (including additive ones) re-arms the replay-controls search,
        // but does not reset: the in-flight leaderboards provisioning loads a scene
        // additively, and resetting then wiped MP PiPs, chat, stick cams and splits.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            replaySearchSceneChangeAt = Time.unscaledTime;
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            replaySearchSceneChangeAt = Time.unscaledTime;
            ResetState();
            // Reset state also for same-name scene reloads: TickRaceProbe only
            // resets on a scene *name* change, so reloading the same track used to
            // keep the previous run's laps/route. Record the *active* scene's name,
            // which is what TickRaceProbe compares against.
            ResetRaceSplitsForScene();
            GateSplitsBinding.DetachIfStale();
            raceProbeLastScene = next.name;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            replaySearchSceneChangeAt = Time.unscaledTime;
            bool wasReplay = replayReady;
            if (wasReplay)
                DeactivateReplay();
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Scene unloaded" + (wasReplay ? "; replay deactivated, recording candidates cleared" : ""));
        }
    }
}
