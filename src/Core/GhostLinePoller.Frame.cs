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
        private void Update()
        {
            ourInputScope++;
            try
            {
                perfUpdSw.Restart();
                Prof.FrameBoundary(Time.unscaledDeltaTime);
                Prof.Span ps = Prof.Begin();
                UpdateImpl();
                Prof.End(Prof.Update, ps);
                perfUpdMs += (float)perfUpdSw.Elapsed.TotalMilliseconds;
            }
            finally { ourInputScope--; }
        }

        private int stutterGc0 = -1;
        private int stutterGc1 = -1;
        private float stutterLastLogAt = 0f;
        private bool firstUpdateSeen = false;

        private void UpdateImpl()
        {
            if (!firstUpdateSeen)
            {
                firstUpdateSeen = true;
                if (Instance == null)
                    Instance = this;
                CameraViewToggle.modWantsKey = () => Instance != null && Instance.ModWantsCameraKey();
                ScreenFadeGate.suppress = () => Instance != null && Instance.CameraAwayFromMpDrone();
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] GhostLinePoller first Update");
                ModLog.LogFile("[GhostLines] MPPIP poller first Update");
            }
            Prefs.Tick();
            // A spurious teardown early in startup can flip GC back to automatic;
            // re-apply the configured mode so the session isn't stuck on GC stutters.
            if (!GhostLinesPlugin.gcManualActive && Time.unscaledTime >= GhostLinesPlugin.nextGcReapplyAt)
                GhostLinesPlugin.ApplyGcMode();
            // Manual GC mode: the poller owns collections now — one deliberate collect
            // on a timer instead of Unity's automatic stop-the-world pauses.
            if (GhostLinesPlugin.gcManualActive && Time.unscaledTime >= GhostLinesPlugin.nextManualCollectAt)
            {
                GhostLinesPlugin.nextManualCollectAt = Time.unscaledTime +
                    System.Math.Max(5f, Prefs.gcCollectIntervalConfig != null ? Prefs.gcCollectIntervalConfig.Value : 20f);
                try
                {
                    long before = System.GC.GetTotalMemory(false);
                    System.Diagnostics.Stopwatch gcSw = System.Diagnostics.Stopwatch.StartNew();
                    System.GC.Collect();
                    gcSw.Stop();
                    pluginLog.LogInfo("[GhostLines] manual GC collect freed=" +
                        ((before - System.GC.GetTotalMemory(false)) / 1024.0).ToString("F0") + "KB took=" +
                        gcSw.Elapsed.TotalMilliseconds.ToString("F1") + "ms");
                }
                catch { }
            }
            // Hitch auto-logger: fires only when a frame overran 20ms (throttled to
            // one entry per 3s). GC deltas tell allocation-driven stutters apart
            // from scene/render hitches. Debug logging only.
            if (ModLog.DebugEnabled && Time.unscaledDeltaTime > 0.02f && Time.unscaledTime - stutterLastLogAt > 3f)
            {
                int sg0 = stutterGc0 < 0 ? 0 : System.GC.CollectionCount(0) - stutterGc0;
                int sg1 = stutterGc1 < 0 ? 0 : System.GC.CollectionCount(1) - stutterGc1;
                stutterGc0 = System.GC.CollectionCount(0);
                stutterGc1 = System.GC.CollectionCount(1);
                stutterLastLogAt = Time.unscaledTime;
                string stutterScene;
                try { stutterScene = SceneManager.GetActiveScene().name; } catch { stutterScene = "?"; }
                // pluginLog at Info so it lands in LogOutput.log next to the game's own lines.
                string gcModeStr;
                try { gcModeStr = UnityEngine.Scripting.GarbageCollector.GCMode.ToString(); } catch { gcModeStr = "?"; }
                pluginLog.LogInfo("[GhostLines] stutter " + (Time.unscaledDeltaTime * 1000f).ToString("F0") + "ms scene=" + stutterScene +
                    " gc0=+" + sg0 + " gc1=+" + sg1 + " gcMode=" + gcModeStr +
                    // Current 1s-window segment sums — the segment that spikes is the culprit.
                    " | upd=" + perfUpdMs.ToString("F1") +
                    " mpPip=" + perfMpPipMs.ToString("F1") +
                    " scan=" + perfScanMs.ToString("F1") +
                    " lobby=" + perfLobbyMs.ToString("F1") +
                    " gui=" + perfGuiAllMs.ToString("F1") +
                    " chat=" + perfChatMs.ToString("F1") +
                    " pip=" + perfPipMs.ToString("F1") +
                    " mp=" + mpInSession + " tgt=" + multiplayerPipTargets.Count +
                    " chat=" + mpChatStates.Count + " watcher=" + (watcherClient != null) +
                    " graph=" + raceGraphDumped +
                    " splits=" + (Prefs.gateSplitsConfig != null && Prefs.gateSplitsConfig.Value));
            }
            PerfWindowTick();
            HookUnityLog();
            perfSw.Restart();
            Prof.Span psLobby = Prof.Begin();
            HookPhotonProbe();
            TickLobbyWatcher();
            Prof.End(Prof.UpdLobby, psLobby);
            perfLobbyMs += (float)perfSw.Elapsed.TotalMilliseconds;
            Prof.Span psLbTicks = Prof.Begin();
            TickLbForceTrack();
            TickLbPersistHide();
            slowTickSw.Restart(); TickLbPopupMultiSelect(); LogSlowTick("lbPopup");
            TickLbPopupDateHover();
            Prof.End(Prof.UpdLbTicks, psLbTicks);
            TickOverlayFades();
            Prof.Span psRace = Prof.Begin();
            slowTickSw.Restart(); TickRaceProbe(); LogSlowTick("raceProbe");
            Prof.End(Prof.UpdRaceProbe, psRace);
            Prof.Span psWs = Prof.Begin();
            slowTickSw.Restart(); TickWorkshopDownload(); LogSlowTick("workshop");
            Prof.End(Prof.UpdWorkshop, psWs);
            Prof.Span psDedup = Prof.Begin();
            slowTickSw.Restart(); TickInGameUiSingleton(); LogSlowTick("inGameUiDedup");
            Prof.End(Prof.UpdUiDedup, psDedup);
            TickMpCursorRestore();
            if (Time.unscaledTime >= nextPollerStatusLog)
            {
                nextPollerStatusLog = Time.unscaledTime + 5f;
                if (ModLog.DebugEnabled)
                {
                    string heartbeatScene = "?";
                    try { heartbeatScene = SceneManager.GetActiveScene().name; } catch { }
                    pluginLog.LogInfo("[GhostLines] MPPIP heartbeat scene=" + heartbeatScene +
                        " replayReady=" + replayReady +
                        " mpInSession=" + mpInSession +
                        " targets=" + multiplayerPipTargets.Count +
                        " windows=" + multiplayerPipWindows.Count +
                        " fade=" + multiplayerPipMenuFade.ToString("F2") +
                        " track='" + (mpCurrentTrackName ?? "") + "' race='" + (mpCurrentRaceName ?? "") +
                        "' wsStatus='" + wsStatus + "'");
                }
            }

            perfSw.Restart();
            TickBindingsKeyCapture();
            perfBindMs += (float)perfSw.Elapsed.TotalMilliseconds;
            perfSw.Restart();
            UpdateInputGate();
            perfInGateMs += (float)perfSw.Elapsed.TotalMilliseconds;
            if (!replayReady && bindingListeningFor == null && IsContentSceneActive() && Input.GetKeyDown(GhostLinesPlugin.GetBinding("mpmenu")) && Time.frameCount != mpF10ToggleFrame)
            {
                mpF10ToggleFrame = Time.frameCount;
                multiplayerPipMenuOpen = !multiplayerPipMenuOpen;
                SaveMpMenuOpen(multiplayerPipMenuOpen);
                multiplayerPipLastMouseMove = Time.unscaledTime;
                multiplayerPipMenuFade = 1f;
                overlayMouseMovedAt = Time.unscaledTime;
                if (multiplayerPipMenuOpen)
                    mpMenuIdleAlpha = 1f;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Multiplayer menu key toggled: " + multiplayerPipMenuOpen);
            }
            Prof.Span psLb = Prof.Begin();
            InstrumentedTickLeaderboardSearch();
            perfSw.Restart();
            TickLeaderboardDragSelection();
            RefocusLeaderboardSearchInput();
            MaintainLeaderboardSearchFocus();
            Prof.End(Prof.UpdLbUi, psLb);
            perfLbUiMs += (float)perfSw.Elapsed.TotalMilliseconds;

            GhostLinesPlugin.DrainPendingRecordings();
            for (int i = recordingCandidates.Count - 1; i >= 0; i--)
            {
                if (recordingCandidates[i] == null || recordingCandidates[i].GetRecording() == null)
                {
                    recordingCandidates.RemoveAt(i);
                    replayEntriesDirty = true;
                }
            }            // Cleanup dead entries
            for (int i = GhostLinesPlugin.entries.Count - 1; i >= 0; i--)
            {
                var entry = GhostLinesPlugin.entries[i];
                if (entry.GetRecording() == null)
                {
                    DestroyActiveEntry(entry);
                    GhostLinesPlugin.entries.RemoveAt(i);
                    replayEntriesDirty = true;
                    GhostLineEntry.entriesEpoch++;
                }
            }

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("smooth")))
                GhostLinesPlugin.ToggleSmoothing();

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("restart")))
                restartRequested = true;

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("xray")))
                SetXrayEnabled(!xrayEnabled);

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("playpause")))
                RequestPlayButton();

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("graph")))
                OpenStickGraph();

            if (replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && Input.GetKeyDown(GhostLinesPlugin.GetBinding("pip")))
            {
                if (!pipEnabled)
                    SetPipEnabled(true);
                if (spectatedEntry != null)
                    TogglePiPWindow(spectatedEntry);
            }

            Prof.Span psLife = Prof.Begin();
            if (replayControlsObj == null && !mpInSession && ReplaySearchArmed())
            {
                GameObject btn = Time.unscaledTime < nextReplayControlsSearch ? null : GameObject.Find("btnPlay");
                if (btn != null)
                {
                    Transform t = btn.transform;
                    while (t != null)
                    {
                        if (t.name.Contains("ReplayControlsUI"))
                        {
                            BindReplayControls(t.gameObject);
                            if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Found ReplayControlsUI via btnPlay");

                            Transform pauseT = replayControlsObj.transform.Find("VLayout/Controls/btnPlay/ImgPause");
                            if (pauseT != null)
                            {
                                isPlaying = pauseT.gameObject.activeSelf;
                                if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Initial isPlaying={isPlaying}");
                            }
                            break;
                        }
                        t = t.parent;
                    }
                }
            }

            if (replayControlsObj != null)
            {
                if (sliderComponent == null)
                {
                    sliderSearchTimer += Time.unscaledDeltaTime;
                    if (sliderSearchTimer >= 3f)
                    {
                        sliderSearchTimer = 0f;
                        FindSliderReflection();
                    }
                }
            }

            if (replayControlsObj != null && replayController == null)
                CacheReplayController();
            UpdateReplayLifecycle();
            Prof.End(Prof.UpdReplayLife, psLife);
            Prof.Span psMp = Prof.Begin();
            try { perfMpPipSw.Restart(); UpdateMultiplayerPip(); perfMpPipMs += (float)perfMpPipSw.Elapsed.TotalMilliseconds; }
            catch (System.Exception multiplayerPipEx) { ModLog.LogFile("[GhostLines] MPPIP Update EX " + multiplayerPipEx); }
            try { perfSw.Restart(); UpdateMultiplayerFreeCam(); perfFreeCamMs += (float)perfSw.Elapsed.TotalMilliseconds; }
            catch (System.Exception mpFreeCamEx) { ModLog.LogFile("[GhostLines] MPPIP freecam EX " + mpFreeCamEx); }
            try { perfSw.Restart(); UpdateNativePipExternal(); perfNativePipMs += (float)perfSw.Elapsed.TotalMilliseconds; }
            catch (System.Exception nativePipEx) { ModLog.LogFile("[GhostLines] MPPIP external EX " + nativePipEx); }
            Prof.End(Prof.UpdMpPip, psMp);
            Prof.Span psChat = Prof.Begin();
            try { perfSw.Restart(); slowTickSw.Restart(); UpdateChatTracking(); perfChatTrackMs += (float)perfSw.Elapsed.TotalMilliseconds; LogSlowTick("chatTrack"); }
            catch (System.Exception chatEx) { ModLog.LogFile("[GhostLines] MPPIP chat EX " + chatEx); }
            Prof.End(Prof.UpdChatTrack, psChat);
            // Replay PiPs render in LateUpdate, after BuildFramePoses.
            if (!(pipEnabled && replayReady) && pipWindows.Count > 0)
                CloseAllPiPWindows();
            Prof.Span psWin = Prof.Begin();
            perfSw.Restart();
            HandlePiPWindowMouse();
            perfPwMouseMs += (float)perfSw.Elapsed.TotalMilliseconds;
            try { perfSw.Restart(); HandleMultiplayerPipInput(); perfMpInMs += (float)perfSw.Elapsed.TotalMilliseconds; }
            catch (System.Exception multiplayerPipInputEx) { ModLog.LogFile("[GhostLines] MPPIP Input EX " + multiplayerPipInputEx); }
            Prof.End(Prof.UpdWinMouse, psWin);
            // In a room, not mpInSession: that only turns true once another pilot's
            // drone is seen, so drone hotkeys never worked while alone in a room.
            if (!replayReady && (mpInSession || PhotonInRoom()))
            {
                if (!wasMpInSession)
                {
                    wasMpInSession = true;
                    dronesNeedScan = true;
                }
                perfSw.Restart();
                Prof.Span psDrones = Prof.Begin();
                RefreshDroneSwitchEntriesIfNeeded();
                // The search field only counts as focused while its dropdown is shown.
                if (!droneSwitchDropdownOpen || !multiplayerPipMenuOpen)
                    dronesSearchFocused = false;
                if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && !dronesSearchFocused)
                {
                    try { TryDroneSwitchHotkeys(); }
                    catch (System.Exception droneHotkeyEx) { ModLog.LogFile("[GhostLines] DRONESWITCH hotkey EX " + droneHotkeyEx); }
                    if (!GhostLinesPlugin.textFieldFocused && !IsChatTyping() &&
                        Input.GetKeyDown(GhostLinesPlugin.GetBinding("nametags")))
                        ToggleMultiplayerNames();
                }
                perfDroneMs += (float)perfSw.Elapsed.TotalMilliseconds;
                Prof.End(Prof.UpdDrones, psDrones);
            }
            else if (wasMpInSession)
            {
                wasMpInSession = false;
                dronesNeedScan = true;
            }
            perfSw.Restart();
            HandleOverlayResizeMouse();
            perfOvMouseMs += (float)perfSw.Elapsed.TotalMilliseconds;
            Prof.Span psEnf = Prof.Begin();
            InstrumentedEnforceOverrides();
            Prof.End(Prof.UpdEnforce, psEnf);
            perfSw.Restart();
            Prof.Span psDiag = Prof.Begin();
            DiagGateState();
            Prof.End(Prof.UpdDiag, psDiag);
            perfDiagMs += (float)perfSw.Elapsed.TotalMilliseconds;
            if (!replayReady)
                return;

            Prof.Span psReplay = Prof.Begin();
            try { UpdateReplayFrame(); }
            finally { Prof.End(Prof.UpdReplay, psReplay); }
        }

        private void UpdateReplayFrame()
        {
            HandleFrameStepInput();

            if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame)
            {
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("rewind")))
                ClickGameButton("btnRewind");
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("ff")))
                ClickGameButton("btnFastForward");
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("nametags")))
                SetNameTagsVisible(!GhostLinesPlugin.showNameTags);
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("alllines")))
            {
                bool anyOn = false;
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    if (entry.visible)
                    {
                        anyOn = true;
                        break;
                    }
                }
                SetAllRaceLinesVisible(!anyOn);
            }
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("linesthrough")))
                GhostLinesPlugin.SetLinesThroughObjects(!GhostLinesPlugin.linesThroughObjects);
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("delta")))
                GhostLinesPlugin.SetDeltaDisplay(!GhostLinesPlugin.showDeltaDisplay);
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("heatmap")))
                GhostLinesPlugin.ToggleSpeedHeatmap();
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("loop")))
                loopReplayEnabled = !loopReplayEnabled;
            // In replay this opens the full-lap stick graph; the live graph is
            // multiplayer-only.
            if (Input.GetKeyDown(GhostLinesPlugin.GetBinding("livegraph")) && spectatedEntry != null)
                ToggleStickGraphFor(spectatedEntry);
            }

            int pilotKey = 0;
            if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame)
                pilotKey = GetPilotNumberKey();
            if (pilotKey > 0)
                TrySpectatePilot(pilotKey - 1, true);

            if (replayControlsObj != null && replayController != null && ghostMatchAttempts < GHOST_MATCH_MAX_ATTEMPTS
                && GhostLinesPlugin.entries.Count > 0 && ghostDroneMap.Count < GhostLinesPlugin.entries.Count)
            {
                ghostDroneSearchTimer += Time.unscaledDeltaTime;
                if (ghostDroneSearchTimer >= GHOST_MATCH_RETRY_INTERVAL)
                {
                    ghostDroneSearchTimer = 0f;
                    perfSw.Restart();
                    MatchGhostDrones();
                    perfMatchMs += (float)perfSw.Elapsed.TotalMilliseconds;
                    ghostMatchAttempts++;
                }
            }

            if (playbackMouseCaptured && !Input.GetMouseButton(0) &&
                !draggingScrubber && !isDraggingHandle && !draggingTrimStart && !draggingTrimEnd)
            {
                playbackMouseCaptured = false;
            }
            if ((draggingTrimStart || draggingTrimEnd) && !Input.GetMouseButton(0) && !Application.isFocused)
            {
                draggingTrimStart = false;
                draggingTrimEnd = false;
                playbackMouseCaptured = false;
                if (trimResumePlayback)
                {
                    if (!overridePlayback)
                        SetNativeReplayPlaying(true);
                    isPlaying = true;
                }
                trimResumePlayback = false;
            }
            if (isDraggingHandle && !Input.GetMouseButton(0) && !Application.isFocused)
            {
                isDraggingHandle = false;
                playbackMouseCaptured = false;
                if (resumePlaybackAfterSeek && !overridePlayback)
                    SetNativeReplayPlaying(true);
                resumePlaybackAfterSeek = false;
            }
            if (draggingScrubber || isDraggingHandle || draggingTrimStart || draggingTrimEnd)
                CancelCameraLook();

            // --- Orbit input: must run EVERY frame, not behind the 1s gate ---
            if (!playbackMouseCaptured && !draggingScrubber && !isDraggingHandle)
            {
            if (orbitTarget == null)
                TryInitCamera();

            if (flightCamCtrl != null && orbitTarget != null)
            {
                Transform currentTarget = flightCamCtrl.StuntCameraReferenceTransform;
                if (currentTarget != null && currentTarget != orbitTarget)
                {
                    bool preserveCustomCamera = customCameraActive;
                    bool preserveFpvOrbit = startedFromFpv;
                    bool preserveOrbiting = isOrbiting;
                    bool restoreCameraView = pendingCameraViewRestore;
                    bool restoreFreeCamView = pendingFreeCamViewRestore;
                    bool restoreOrbitView = pendingOrbitViewRestore;
                    Vector3 savedFreePosition = pendingFreeCamPosition;
                    float savedFreeYaw = pendingFreeCamYaw;
                    float savedFreePitch = pendingFreeCamPitch;
                    float savedOrbitDist = pendingOrbitDist;
                    float savedOrbitYaw = pendingOrbitYaw;
                    float savedOrbitPitch = pendingOrbitPitch;
                    bool savedOrbiting = pendingOrbiting;
                    pendingCameraViewRestore = false;
                    pendingFreeCamViewRestore = false;
                    pendingOrbitViewRestore = false;
                    orbitTarget = currentTarget;
                    customCameraActive = preserveCustomCamera;
                    startedFromFpv = preserveCustomCamera && preserveFpvOrbit;
                    isOrbiting = preserveCustomCamera && preserveOrbiting;
                    if (restoreCameraView && restoreFreeCamView)
                    {
                        freeCamPosition = savedFreePosition;
                        freeCamYaw = savedFreeYaw;
                        freeCamPitch = savedFreePitch;
                    }
                    else if (restoreCameraView && restoreOrbitView)
                    {
                        orbitDist = savedOrbitDist;
                        orbitYaw = savedOrbitYaw;
                        orbitPitch = savedOrbitPitch;
                        isOrbiting = savedOrbiting;
                    }
                    else if (Camera.main != null)
                    {
                        Vector3 diff = Camera.main.transform.position - orbitTarget.position;
                        if (diff.sqrMagnitude < 0.0001f)
                            diff = Vector3.back * 3f;
                        orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                        Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                        orbitYaw = rot.eulerAngles.y;
                        orbitPitch = rot.eulerAngles.x;
                        if (orbitPitch > 180f) orbitPitch -= 360f;
                    }
                    if (ModLog.DebugEnabled) Debug.Log(preserveCustomCamera
                        ? "[GhostLines] Camera target changed; preserving custom orbit"
                        : "[GhostLines] Pilot switch detected, camera reset");
                    Transform switchedRoot;
                    Transform switchedMount;
                    TryGetNativeTrackedTransforms(out switchedRoot, out switchedMount);
                    spectatedEntry = FindEntryForNativeRoot(switchedRoot);
                    if (spectatedEntry == null)
                    {
                        float rt;
                        float bestDist = float.MaxValue;
                        Vector3 refPos = switchedRoot != null ? switchedRoot.position : orbitTarget.position;
                        if (TryGetReplayTime(out rt))
                        {
                            foreach (var e in GhostLinesPlugin.entries)
                            {
                                Vector3 dp; Quaternion dr;
                                if (TryGetDroneTransformUnshifted(e, rt, out dp, out dr))
                                {
                                    float d = Vector3.Distance(dp, refPos);
                                    if (d < bestDist) { bestDist = d; spectatedEntry = e; }
                                }
                            }
                        }
                    }
                    InitializeDefaultReferenceEntry();
                    UpdateDroneMarkers();
                }
            }


            bool freeCamEntryHeld = bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && !IsChatTyping() &&
                FreeCamMove() != Vector3.zero;

            bool requestedFreeCam = !freeCamActive && freeCamEntryHeld &&
                freeCamEntryHoldStart >= 0f &&
                Time.unscaledTime - freeCamEntryHoldStart >= FREE_CAM_ENTRY_HOLD_SECONDS;

            if (requestedFreeCam)
                EnterFreeCam();

            if (Time.frameCount != bindingCaptureSuppressFrame && bindingListeningFor == null && Input.GetKeyDown(GhostLinesPlugin.GetBinding("camreset")) && (freeCamActive || customCameraActive))
            {
                // Freecam: back to the drone in the view it came from. Orbit camera
                // (the mod's third person): the key toggles to first person.
                pendingCameraModeRestore = freeCamActive ? lastNativeCameraMode : CAMERA_MODE_FIRST_PERSON;
                ResetToDroneCamera();
                CameraViewToggle.modUsedKeyFrame = Time.frameCount;
            }

            if (freeCamActive)
            {
                HandleFreeCamInput();
            }
            else if (orbitTarget != null && Camera.main != null)
            {
                Vector3 pivot = ghostDronePosValid ? ghostDronePos : orbitTarget.position;

                // Scroll zoom
                bool usingPendingThirdPersonScroll = !Mathf.Approximately(pendingThirdPersonScroll, 0f);
                float scroll = usingPendingThirdPersonScroll ? pendingThirdPersonScroll : Input.mouseScrollDelta.y;
                pendingThirdPersonScroll = 0f;
                if (scroll != 0f)
                {
                    int nativeMode = usingPendingThirdPersonScroll ? CAMERA_MODE_THIRD_PERSON_CLOSE : GetCurrentNativeCameraMode();
                    bool nativeFirstPerson = nativeMode == CAMERA_MODE_FIRST_PERSON;

                    // Native FPV has no usable orbit distance. Hand the wheel input to
                    // the game's third-person camera, then consume it on the next frame.
                    if (!customCameraActive && nativeFirstPerson && scroll < 0f)
                    {
                        if (lastNativeCameraMode == 0)
                            lastNativeCameraMode = nativeMode;
                        startedFromFpv = true;
                        SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
                        pendingThirdPersonScroll = scroll;
                        if (ModLog.DebugEnabled) Debug.Log("[GhostLines] FPV scroll-out: switching to third-person close");
                        scroll = 0f;
                    }
                    else if (!customCameraActive && nativeFirstPerson && scroll > 0f)
                    {
                        // Do not create a custom camera while native FPV is active.
                        scroll = 0f;
                    }

                    if (scroll != 0f)
                    {
                        if (!customCameraActive && !usingPendingThirdPersonScroll)
                            startedFromFpv = false;

                        if (!customCameraActive && Camera.main != null && orbitTarget != null)
                        {
                            Vector3 diff = Camera.main.transform.position - pivot;
                            if (diff.sqrMagnitude < 0.0001f)
                                diff = Vector3.back * 3f;
                            orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                            Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                            orbitYaw = rot.eulerAngles.y;
                            orbitPitch = rot.eulerAngles.x;
                            if (orbitPitch > 180f) orbitPitch -= 360f;
                        }

                        orbitDist -= scroll * 2f;
                        orbitDist = Mathf.Clamp(orbitDist, 0.5f, 200f);
                        if (!customCameraActive && lastNativeCameraMode == 0)
                            lastNativeCameraMode = GetCurrentNativeCameraMode();
                        if (customCameraActive && scroll > 0f && orbitDist <= (startedFromFpv ? 1.5f : 0.9f))
                        {
                            customCameraActive = false;
                            isOrbiting = false;
                            CancelCameraLook();
                            startedFromFpv = false;
                            if (lastNativeCameraMode != 0)
                            {
                                pendingCameraModeRestore = lastNativeCameraMode;
                                lastNativeCameraMode = 0;
                            }
                            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Scrolled in; returned to native camera");
                        }
                        else
                        {
                            customCameraActive = true;
                            if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Scroll zoom: dist={orbitDist:F2}");
                        }
                    }
                }

                // Right-click down -> start orbit
                if (Input.GetMouseButtonDown(1))
                {
                    bool startingFromNative = !customCameraActive;
                    int nativeMode = startingFromNative ? GetCurrentNativeCameraMode() : 0;
                    if (startingFromNative && lastNativeCameraMode == 0)
                        lastNativeCameraMode = nativeMode;
                    isOrbiting = true;
                    customCameraActive = true;
                    if (startingFromNative)
                    {
                        Vector3 diff = Camera.main.transform.position - pivot;
                        if (diff.sqrMagnitude < 0.0001f) diff = Vector3.back * 3f;
                        startedFromFpv = nativeMode == CAMERA_MODE_FIRST_PERSON;
                        if (startedFromFpv)
                        {
                            orbitDist = 3f;
                            Quaternion fpvRot = Quaternion.LookRotation(Camera.main.transform.forward);
                            Vector3 fpvEuler = fpvRot.eulerAngles;
                            orbitYaw = fpvEuler.y;
                            orbitPitch = fpvEuler.x;
                            SetNativeCameraMode(CAMERA_MODE_THIRD_PERSON_CLOSE, true);
                        }
                        else
                        {
                            orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                            Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                            orbitYaw = rot.eulerAngles.y;
                            orbitPitch = rot.eulerAngles.x;
                        }
                        if (orbitPitch > 180f) orbitPitch -= 360f;
                    }

                    BeginCameraLookDrag();
                    if (ModLog.DebugEnabled) Debug.Log(startingFromNative
                        ? $"[GhostLines] Orbit START dist={orbitDist:F2} yaw={orbitYaw:F1} pitch={orbitPitch:F1}"
                        : $"[GhostLines] Orbit DRAG START dist={orbitDist:F2} yaw={orbitYaw:F1} pitch={orbitPitch:F1}");
                }

                // Right-click held -> compute orbit delta
                if (Input.GetMouseButton(1) && isOrbiting)
                {
                    if (!cameraLookDragActive)
                        BeginCameraLookDrag();
                    Vector2 delta = ReadCameraLookDelta();

                    orbitYaw += delta.x * 0.3f;
                    orbitPitch -= delta.y * 0.3f;
                    orbitPitch = Mathf.Clamp(orbitPitch, -85f, 85f);
                }

                // Right-click up -> stop orbit
                if (Input.GetMouseButtonUp(1) && isOrbiting)
                {
                    isOrbiting = false;
                    CancelCameraLook();
                    if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Orbit END, view locked");
                }

                // Middle-click -> reset to default camera
                if (Input.GetMouseButtonDown(2))
                {
                    if (Camera.main != null && orbitTarget != null)
                    {
                        Vector3 diff = Camera.main.transform.position - pivot;
                        if (diff.sqrMagnitude < 0.0001f) diff = Vector3.back * 3f;
                        orbitDist = Mathf.Max(diff.magnitude, 0.01f);
                        Quaternion rot = Quaternion.LookRotation(-diff.normalized);
                        orbitYaw = rot.eulerAngles.y;
                        orbitPitch = rot.eulerAngles.x;
                        if (orbitPitch > 180f) orbitPitch -= 360f;
                    }
                    customCameraActive = false;
                    isOrbiting = false;
                    CancelCameraLook();
                    startedFromFpv = false;
                    if (lastNativeCameraMode != 0)
                    {
                        pendingCameraModeRestore = lastNativeCameraMode;
                        lastNativeCameraMode = 0;
                    }
                    if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Middle click reset");
                }
            }
            }

            perfSw.Restart();
            HandlePlayheadDragCamera();
            perfCamMs += (float)perfSw.Elapsed.TotalMilliseconds;

            // --- Slow polling (1/sec) for non-input tasks ---
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;

            if (sliderComponent != null)
            {
                float cur = sliderComponent.value;
                float maxVal = sliderComponent.maxValue;
                if (cur >= maxVal - 0.001f)
                    isPlaying = false;
            }

            foreach (var entry in GhostLinesPlugin.entries)
                GhostLinesPlugin.TryBuildLine(entry);

            if (replayController != null)
            {
                foreach (var entry in GhostLinesPlugin.entries)
                {
                    bool needsResolve = !entry.replayColorResolved ||
                        (!entry.replayNameResolved && entry.pilotName != null && entry.pilotName.StartsWith("Pilot "));
                    if (needsResolve && entry.replayResolveTries >= 30)
                        continue;
                    if (needsResolve)
                        entry.replayResolveTries++;
                    if (!entry.replayColorResolved)
                    {
                        Color? realColor = ResolvePilotColorFromReplayState(entry.GetRecording());
                        if (realColor.HasValue)
                        {
                            entry.resolvedReplayColor = realColor.Value;
                            entry.replayColorResolved = true;
                        }
                    }
                    bool nameChanged = false;
                    if (!entry.replayNameResolved && entry.pilotName != null && entry.pilotName.StartsWith("Pilot "))
                    {
                        string realName = GhostLinesPlugin.CleanPilotName(ResolvePilotNameFromReplayState(entry.GetRecording()));
                        if (!string.IsNullOrWhiteSpace(realName))
                        {
                            entry.resolvedReplayName = realName;
                            entry.replayNameResolved = true;
                            entry.pilotName = realName;
                            GhostLineEntry.entriesEpoch++;
                            nameChanged = true;
                        }
                    }
                    bool colorChanged = entry.replayColorResolved && entry.resolvedReplayColor.Value != entry.color;
                    if (colorChanged)
                    {
                        entry.color = entry.resolvedReplayColor.Value;
                        GhostLinesPlugin.RebuildLine(entry);
                        if (entry.markerText != null && nameChanged)
                            entry.markerText.text = entry.pilotName;
                    }
                    else if (nameChanged && entry.markerText != null)
                    {
                        entry.markerText.text = entry.pilotName;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            ourInputScope++;
            try
            {
                perfLateSw.Restart();
                Prof.Span ps = Prof.Begin();
                LateUpdateImpl();
                Prof.End(Prof.LateUpdate, ps);
                perfLateMs += (float)perfLateSw.Elapsed.TotalMilliseconds;
            }
            finally { ourInputScope--; }
        }

        private void LateUpdateImpl()
        {
            if (!replayReady)
            {
                try { perfSw.Restart(); RenderDueMultiplayerPipWindows(); perfPipMs += (float)perfSw.Elapsed.TotalMilliseconds; }
                catch (System.Exception mpPipRenderEx) { ModLog.LogFile("[GhostLines] MPPIP render EX " + mpPipRenderEx); }
                return;
            }

            if (TryGetReplayTime(out float stableRt))
                stableReplayTime = stableRt;
            // Per-frame snapshot for OnGUI draws (scrubber etc.): GUI runs 2-4 events
            // per frame — reading the native time/max via reflection on every event
            // was ~10-20 reflection invokes per frame.
            scrubberTimeSnapshot = stableReplayTime;
            scrubberMaxSnapshot = GetNativeReplayMaximum(0f);
            replayTimeDelta = prevReplayTime > 0f
                ? Mathf.Max(0f, stableReplayTime - prevReplayTime)
                : 0f;
            prevReplayTime = stableReplayTime;
            float rawLead = replayTimeDelta <= 0.5f ? replayTimeDelta : 0f;
            predictionLead = Mathf.Lerp(predictionLead, rawLead, 0.3f);
            float clockLead;
            bool clockRunning = TryGetNativeClockLead(out clockLead);
            if (clockRunning)
            {
                // Playing natively: the extrapolated clock is already continuous,
                // so use it directly (the lerp below only half-hid the tick steps
                // and added frame-time-dependent lag).
                float continuousRt = stableReplayTime + clockLead;
                if (scrubberMaxSnapshot > 0f)
                    continuousRt = Mathf.Min(continuousRt, scrubberMaxSnapshot);
                displayReplayTime = continuousRt;
            }
            else
            {
                float ghostBlend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.02f);
                displayReplayTime = Mathf.Abs(stableReplayTime - displayReplayTime) > 0.25f
                    ? stableReplayTime
                    : Mathf.Lerp(displayReplayTime, stableReplayTime, ghostBlend);
            }
            ghostPosBlend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.025f);
            UpdateHoldZoom();
            RequestLoopedReplayAtEnd();
            EnforceEffectiveReplayEnd();

            bool restartScheduledThisFrame = false;
            if (restartRequested)
            {
                restartRequested = false;
                frameStepPaused = false;
                float restartSpeed = playbackSpeed;
                bool restartWithOverride = !Mathf.Approximately(restartSpeed, 1f);
                if (sliderComponent != null)
                {
                    float restartTime = restartSpeed < 0f
                        ? GetLoopReplayEnd(GetNativeReplayMaximum(0f))
                        : GetPlaybackStart();
                    sliderComponent.value = restartTime;
                }
                overridePlayback = restartWithOverride;
                isPlaying = true;
                restartPlaybackPending = !restartWithOverride;
                restartScheduledThisFrame = true;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Replay restart requested");
            }

            if (pendingPlayToggle)
            {
                pendingPlayToggle = false;
                if (overridePlayback)
                {
                    isPlaying = !isPlaying;
                    if (ModLog.DebugEnabled) Debug.Log($"[GhostLines] Override play/pause, now isPlaying={isPlaying}");
                }
            }

            if (overridePlayback)
                SetNativeReplayPlaying(false);

            if (controlsCG != null && controlsHidden)
            {
                controlsCG.alpha = 0f;
                controlsCG.interactable = false;
                controlsCG.blocksRaycasts = false;
            }

            if (flightCamCtrl != null)
            {
                // The game's getter throws while the camera has no active mode.
                try { lateIsThirdPerson = flightCamCtrl.InThirdPerson; } catch { }
                if (!lateIsThirdPerson && !startedFromFpv) customCameraActive = false;
            }

            if (frameStepPaused)
            {
                SetNativeReplayPlaying(false);
                isPlaying = false;
            }
            else if (replayControlsObj != null && !overridePlayback)
            {
                isPlaying = !IsReplayFinished() && IsNativeReplayPlaying();
            }

            if (restartPlaybackPending && !restartScheduledThisFrame)
            {
                restartPlaybackPending = false;
                SetNativeReplayPlaying(true);
                isPlaying = true;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Replay restarted and playing");
            }

            if (pendingCameraModeRestore != 0)
            {
                SetNativeCameraMode(pendingCameraModeRestore, false);
                pendingCameraModeRestore = 0;
            }

            if (fpvOnReplayPending && !freeCamActive && !customCameraActive)
            {
                CaptureReplayEntryCameraMode();
                if (GetCurrentNativeCameraMode() == CAMERA_MODE_FIRST_PERSON || Time.unscaledTime > fpvForceTimeout)
                {
                    fpvOnReplayPending = false;
                }
                else if (cameraModeSetMethod != null)
                {
                    SetNativeCameraMode(CAMERA_MODE_FIRST_PERSON, false);
                    if (!fpvForceLogged)
                    {
                        fpvForceLogged = true;
                        if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Forcing FPV at replay entry");
                    }
                }
            }

            SyncPlaybackSpeedWithNative();

            if (overridePlayback && isPlaying && sliderComponent != null && !isDraggingHandle)
            {
                float cur = sliderComponent.value;
                float maxV = sliderComponent.maxValue;
                float minV = sliderComponent.minValue;
                maxV = GetTrimEnd(maxV);
                minV = Mathf.Max(minV, GetPlaybackStart());
                // Scaled on purpose: the override advances the replay the way the
                // native player does, so it pauses with the game's time scale.
                float next = cur + playbackSpeed * Time.deltaTime;
                float loopEnd = GetLoopReplayEnd(maxV);
                if (loopReplayEnabled && playbackSpeed > 0f && next >= loopEnd)
                {
                    next = loopEnd;
                    restartRequested = true;
                }
                if (next >= maxV || next <= minV)
                {
                    next = Mathf.Clamp(next, minV, maxV);
                    isPlaying = false;
                    SetNativeReplayPlaying(false);
                }
                sliderComponent.value = next;
            }

            // One pose snapshot per frame; PiPs render from it so each PiP shows
            // this frame's ghosts, not last frame's.
            BuildFramePoses();
            if (pipEnabled)
                RenderPiPsAtFramePoses();

            if (freeCamActive && Camera.main != null)
            {
                Camera.main.transform.position = freeCamPosition;
                Camera.main.transform.rotation = Quaternion.Euler(freeCamPitch, freeCamYaw, 0f);
                UpdateDroneMarkers();
                return;
            }

            UpdateDroneMarkers();
        }

        private void SyncPlaybackSpeedWithNative()
        {
            if (overridePlayback)
            {
                SetNativeReplayPlaying(false);
                return;
            }
            if (isDraggingHandle || draggingScrubber)
                return;
            if (nativeSpeedPropInfo == null && nativeSpeedFieldInfo == null)
                return;
            object race = GetReplayRace();
            if (race == null)
                return;
            float native;
            try
            {
                native = nativeSpeedPropInfo != null
                    ? (float)nativeSpeedPropInfo.GetValue(race, null)
                    : (float)nativeSpeedFieldInfo.GetValue(race);
            }
            catch (System.Exception ex)
            {
                if (!nativeSpeedReadFailedLogged)
                {
                    nativeSpeedReadFailedLogged = true;
                    pluginLog.LogWarning("[GhostLines] Native playback speed read failed: " + ex.Message);
                }
                return;
            }
            if (native > 0.001f && Mathf.Abs(native - playbackSpeed) > 0.001f)
                playbackSpeed = native;
        }
    }
}
