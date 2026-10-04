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
        private bool PhotonInRoom()
        {
            // Direct access — the csproj references PhotonUnityNetworking and other
            // call sites use this type directly. The old reflection path boxed a bool
            // every call, which added steady GC pressure while flying.
            return Photon.Pun.PhotonNetwork.InRoom;
        }

        private int multiplayerPipEmptyScans = 0;
        private float nextMultiplayerPipFullScanAt = 0f;
        private float watcherServiceAt = 0f;
        private int wsNoRaceReads = 0;

        private int OthersInRoomCount()
        {
            try { return Photon.Pun.PhotonNetwork.PlayerListOthers != null ? Photon.Pun.PhotonNetwork.PlayerListOthers.Length : 0; }
            catch { return 1; } // fail-open: keep the old scan behavior
        }

        private float MultiplayerPipScanInterval()
        {
            // Fresh list every 1s only when there is something to keep fresh; with no
            // targets the empty backoff applies even with the menu open (the scan is a
            // scene-wide FindObjectsOfType = a felt hitch on big maps).
            if (multiplayerPipMenuOpen && multiplayerPipTargets.Count > 0)
                return 1f;
            if (multiplayerPipTargets.Count == 0)
            {
                if (multiplayerPipEmptyScans <= 1) return 2f;
                if (multiplayerPipEmptyScans == 2) return 4f;
                if (multiplayerPipEmptyScans == 3) return 6f;
                return 12f;
            }
            // Targets already validated; the scene-wide scan is a hitch source on big
            // maps (~24ms on Marina Bay). Only refresh quickly when something actually
            // consumes the list: open PiP windows, name tags, or live input graphs.
            if (multiplayerPipWindows.Count > 0 || mpShowDroneNames || liveInputGraphs.Count > 0)
                return 6f;
            return 25f;
        }

        private void FastValidateMultiplayerPipTargets()
        {
            bool deadFound = false;
            float retainCutoff = Time.unscaledTime - MP_TARGET_RETAIN_SECONDS;
            for (int i = multiplayerPipTargets.Count - 1; i >= 0; i--)
            {
                MultiplayerPipTarget t = multiplayerPipTargets[i];
                if (t == null)
                {
                    multiplayerPipTargets.RemoveAt(i);
                    continue;
                }
                if (t.root == null || !t.root.gameObject.activeInHierarchy)
                {
                    deadFound = true;
                    if (t.lastSeenAt < retainCutoff)
                        multiplayerPipTargets.RemoveAt(i);
                }
            }
            if (deadFound)
            {
                nextMultiplayerPipScan = 0f;
                nextMultiplayerPipFullScanAt = 0f;
            }
        }

        // Only the player's own open/close (menu key, close button) is saved.
        private static void SaveMpMenuOpen(bool open)
        {
            if (Prefs.mpMenuOpenConfig != null && Prefs.mpMenuOpenConfig.Value != open)
                Prefs.mpMenuOpenConfig.Value = open;
        }

        private void UpdateMultiplayerPip()
        {
            if (replayReady)
            {
                CloseMultiplayerPipWindows();
                if (multiplayerPipTargets.Count > 0)
                    RestoreSavedNameTags();
                multiplayerPipTargets.Clear();
                multiplayerPipMenuOpen = false;
                multiplayerPipMenuFade = 0f;
                lobbyBrowserOpen = false;
                lobbyAlertUntil = 0f;
                // Kill every MP mode flag so nothing MP-side can act during replay
                // even if the scene-load teardown is bypassed (in-scene replay entry).
                mpInSession = false;
                if (mpFreeCamActive)
                    ExitMultiplayerFreeCam();
                mpZoomActive = false;
                mpZoomFovCaptured = false;
                // Chat is intentionally kept alive during replay: the Photon room
                // stays joined, so incoming messages keep flowing and we keep
                // rendering/tracking them (see UpdateChatTracking).
                StopLobbyWatcher("replay started");
                return;
            }
            bool mpRoomNow = PhotonInRoom();
            if (mpRoomNow && !replayReady)
            {
                // Capture the room name while genuinely flying — mid-replay the room
                // state is unreliable. Guard the format: transit states have been
                // observed reporting garbage (e.g. "o").
                try
                {
                    if (Photon.Pun.PhotonNetwork.CurrentRoom != null &&
                        IsPlausibleRoomName(Photon.Pun.PhotonNetwork.CurrentRoom.Name) &&
                        lastMpRoomName != Photon.Pun.PhotonNetwork.CurrentRoom.Name)
                    {
                        lastMpRoomName = Photon.Pun.PhotonNetwork.CurrentRoom.Name;
                        pluginLog.LogInfo("[GhostLines] leaderboards: cached room " + lastMpRoomName);
                    }
                }
                catch { }
            }
            if (mpRoomNow != wasMpInRoom)
            {
                wasMpInRoom = mpRoomNow;
                if (mpRoomNow)
                {
                    nextMultiplayerPipScan = 0f;
                    nextMultiplayerPipFullScanAt = 0f;
                    multiplayerPipEmptyScans = 0;
                }
            }
            if (mpRoomNow)
            {
                CaptureLiveMpLevel();
                // The menu is for flying: never in the waiting room (closed
                // without touching the saved choice).
                if (!IsContentSceneActive())
                {
                    multiplayerPipMenuOpen = false;
                    multiplayerPipMenuFade = 0f;
                }
                // Reopen the menu the player left open: leaving the room or a
                // replay closes it without touching the saved choice.
                else if (!multiplayerPipMenuOpen && Prefs.mpMenuOpenConfig != null && Prefs.mpMenuOpenConfig.Value)
                {
                    multiplayerPipMenuOpen = true;
                    multiplayerPipLastMouseMove = Time.unscaledTime;
                    mpMenuIdleAlpha = 1f;
                }
            }
            if (!mpRoomNow)
            {
                lbLiveLevel = null;
                lbLiveDrone = null;
                mpInSession = false;
                multiplayerPipMenuOpen = false;
                multiplayerPipMenuFade = 0f;
                lobbyBrowserOpen = false;
                lobbyAlertUntil = 0f;
                lobbyJoinBusyUntil = 0f;
                if (multiplayerPipTargets.Count > 0)
                {
                    RestoreSavedNameTags();
                    multiplayerPipTargets.Clear();
                }
                for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
                    CloseMultiplayerPipWindow(multiplayerPipWindows[i]);
                for (int i = stickCamInstances.Count - 1; i >= 0; i--)
                {
                    if (stickCamInstances[i].mpTarget != null)
                        stickCamInstances.RemoveAt(i);
                }
                mpShowDroneNames = false;
                mpZoomActive = false;
                mpZoomPilotIndex = 0;
                mpZoomSorted.Clear();
                mpZoomSortedVersion = -1;
                mpRadioDragSelecting = false;
                mpRadioDragMoved = false;
                mpRadioLastIndex = -1;
                mpRadioLastColumn = -1;
                mpMenuDrag.Cancel();
                droneSwitchDropdownOpen = false;
                liveGraphSwitchOpen = false;
                multiplayerPipScanVersion++;
                for (int gi = liveInputGraphs.Count - 1; gi >= 0; gi--)
                {
                    if (liveInputGraphs[gi].mpTarget != null)
                    {
                        if (liveInputGraphs[gi].traceTex != null)
                            Destroy(liveInputGraphs[gi].traceTex);
                        liveInputGraphs.RemoveAt(gi);
                    }
                }
                mpCurrentTrackName = null;
                mpCurrentRaceName = null;
                wsNoRaceReads = 0;
                wsTrackItemId = null;
                wsRaceItemId = null;
                wsLogCaptured = false;
                wsQueryAdvancePending = false;
                wsOfficialContent = false;
                wsPendingItemStatusOfficial = false;
                wsEnvironmentName = null;
                wsAutoAppliedTrackName = null;
                wsAutoAppliedRaceName = null;
                wsPendingNames.Clear();
                wsCurrentTargetIndex = 0;
                if (!wsQueryBusy)
                    wsStatus = "";
            }
            if (multiplayerPipTargets.Count == 0 && multiplayerPipWindows.Count == 0)
            {
                // The scene-wide PhotonView scan is the most expensive periodic call in
                // the mod. When nobody else is in the room there is nothing to find —
                // joining players bump the others count and the scan resumes instantly.
                if (OthersInRoomCount() > 0 && PhotonInRoom() && Time.unscaledTime >= nextMultiplayerPipScan)
                {
                    nextMultiplayerPipScan = Time.unscaledTime + MultiplayerPipScanInterval();
                    perfSw.Restart();
                    ScanMultiplayerPipTargets();
                    perfScanMs += (float)perfSw.Elapsed.TotalMilliseconds;
                }
                float emptyFadeTarget = multiplayerPipMenuOpen ? 1f : 0f;
                multiplayerPipMenuFade = Mathf.MoveTowards(multiplayerPipMenuFade, emptyFadeTarget, Time.unscaledDeltaTime * 5f);
                return;
            }
            if (Time.unscaledTime >= nextMultiplayerPipScan)
            {
                nextMultiplayerPipScan = Time.unscaledTime + MultiplayerPipScanInterval();
                if (multiplayerPipTargets.Count == 0 || multiplayerPipMenuOpen || Time.unscaledTime >= nextMultiplayerPipFullScanAt)
                {
                    perfSw.Restart();
                    ScanMultiplayerPipTargets();
                    perfScanMs += (float)perfSw.Elapsed.TotalMilliseconds;
                    nextMultiplayerPipFullScanAt = Time.unscaledTime + 30f;
                }
                else
                {
                    perfSw.Restart();
                    FastValidateMultiplayerPipTargets();
                    perfScanMs += (float)perfSw.Elapsed.TotalMilliseconds;
                }
            }
            HandleMultiplayerPipDirectKeys();
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if ((mouse - multiplayerPipLastMouse).sqrMagnitude > 1f)
            {
                multiplayerPipLastMouse = mouse;
                multiplayerPipLastMouseMove = Time.unscaledTime;
            }
            bool show = multiplayerPipMenuOpen;
            float targetFade = show ? 1f : 0f;
            multiplayerPipMenuFade = Mathf.MoveTowards(multiplayerPipMenuFade, targetFade, Time.unscaledDeltaTime * 5f);
            perfSw.Restart();
            TickMultiplayerPipWindows();
            perfPipMs += (float)perfSw.Elapsed.TotalMilliseconds;
            mpPipTickFrame = Time.frameCount;
        }

        // Frame the MP PiP windows were last ticked (validated) in Update; their
        // rendering happens later the same frame in LateUpdate, after the game's
        // own Update has moved the drones.
        private int mpPipTickFrame = -1;

        private void RenderDueMultiplayerPipWindows()
        {
            if (mpPipTickFrame != Time.frameCount)
                return;
            float now = Time.unscaledTime;
            float interval = 1f / GetEffectivePipRefreshHz(multiplayerPipWindows.Count);
            for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w.target == null || w.target.root == null)
                    continue;
                if (now >= w.nextRenderAt)
                {
                    EnsureMultiplayerPipRenderTexture(w);
                    RenderMultiplayerPipWindow(w);
                    w.nextRenderAt = now + interval;
                }
            }
        }

        private void HandleMultiplayerPipDirectKeys()
        {
            if (bindingListeningFor != null || Time.frameCount == bindingCaptureSuppressFrame ||
                IsChatTyping() || dronesSearchFocused)
                return;
            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!control)
                return;
            int pressed = -1;
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) pressed = 0;
            else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) pressed = 1;
            else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) pressed = 2;
            else if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) pressed = 3;
            else if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5)) pressed = 4;
            if (pressed < 0)
                return;
            if (Time.unscaledTime >= nextMultiplayerPipScan)
            {
                perfSw.Restart();
                ScanMultiplayerPipTargets();
                perfScanMs += (float)perfSw.Elapsed.TotalMilliseconds;
            }
            if (pressed < multiplayerPipTargets.Count)
            {
                multiplayerPipMenuOpen = true;
                multiplayerPipLastMouseMove = Time.unscaledTime;
                multiplayerPipMenuFade = 1f;
                ToggleMultiplayerPip(multiplayerPipTargets[pressed]);
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Multiplayer PIP direct toggle index=" + (pressed + 1));
            }
        }

        private readonly List<Photon.Pun.PhotonView> photonViewScratch = new List<Photon.Pun.PhotonView>();

        // Every networked object, from PUN's own registry. A scene-wide
        // FindObjectsOfType<PhotonView> took ~22ms on Marina Bay, and the pilot
        // scan ran it once a second while the MP menu was open. Falls back to
        // the scene scan only if the registry can't be read.
        private List<Photon.Pun.PhotonView> CollectPhotonViews()
        {
            photonViewScratch.Clear();
            try
            {
                foreach (Photon.Pun.PhotonView view in Photon.Pun.PhotonNetwork.PhotonViewCollection)
                    photonViewScratch.Add(view);
            }
            catch
            {
                photonViewScratch.Clear();
                photonViewScratch.AddRange(FindObjectsOfType<Photon.Pun.PhotonView>());
            }
            return photonViewScratch;
        }

        private void ScanMultiplayerPipTargets()
        {
            photonScanRan = true;
            var found = new List<MultiplayerPipTarget>();
            mpInSession = false;
            List<Photon.Pun.PhotonView> views = CollectPhotonViews();
            foreach (Photon.Pun.PhotonView view in views)
            {
                if (view == null || view.gameObject == null || !view.gameObject.activeInHierarchy)
                    continue;
                Transform root = view.transform;
                if (!root.name.StartsWith("Drone_", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isMine = false;
                try { isMine = view.IsMine; } catch { }
                if (isMine)
                    continue;
                Photon.Realtime.Player owner = null;
                try { owner = view.Owner; } catch { }
                int ownerActor = 0;
                string name = root.name;
                if (owner != null)
                {
                    ownerActor = owner.ActorNumber;
                    name = owner.NickName ?? name;
                }
                int viewId = view.ViewID;
                MultiplayerPipTarget prior = null;
                for (int i = 0; i < multiplayerPipTargets.Count; i++)
                {
                    MultiplayerPipTarget pt = multiplayerPipTargets[i];
                    if (pt != null && pt.viewId == viewId) { prior = pt; break; }
                }
                Transform mount;
                if (prior != null && prior.lastMountRoot == root && prior.mount != null)
                    mount = prior.mount;
                else
                {
                    mount = FindMultiplayerCameraMount(root);
                    if (mount == null)
                        continue;
                    if (prior != null)
                        prior.lastMountRoot = root;
                }
                if (prior == null)
                {
                    prior = new MultiplayerPipTarget { viewId = viewId, ownerActor = ownerActor, tagVisible = GhostLinesPlugin.showNameTags };
                    prior.firstSeenTime = Time.unscaledTime;
                    prior.lastMountRoot = root;
                }
                prior.ownerActor = ownerActor;
                prior.root = root;
                prior.photonView = view;
                prior.mount = mount;
                prior.playerName = name;
                prior.lastSeenAt = Time.unscaledTime;
                bool needResolve = !prior.pilotColorResolved || prior.pilotColorRoot != root;
                if (!needResolve && prior.pilotColorSource != 1 && Time.unscaledTime - prior.lastColorAttemptAt > 5f)
                    needResolve = true;
                if (needResolve)
                {
                    prior.lastColorAttemptAt = Time.unscaledTime;
                    Color uiC;
                    if (TryResolveMpPilotColorUi(owner, name, out uiC))
                    {
                        prior.pilotColor = uiC;
                        prior.pilotColorSource = 1;
                    }
                    else
                    {
                        prior.pilotColor = MP_PILOT_PALETTE[Mathf.Abs(viewId) % MP_PILOT_PALETTE.Length];
                        prior.pilotColorSource = 0;
                    }
                    prior.pilotColorRoot = root;
                    prior.pilotColorResolved = true;
                }
                found.Add(prior);
            }
            mpInSession = found.Count > 0;
            float retainCutoff = Time.unscaledTime - MP_TARGET_RETAIN_SECONDS;
            for (int ri = 0; ri < multiplayerPipTargets.Count; ri++)
            {
                MultiplayerPipTarget t = multiplayerPipTargets[ri];
                if (t == null || t.root != null)
                    continue;
                if (t.lastSeenAt < retainCutoff)
                    continue;
                bool replaced = false;
                for (int fi = 0; fi < found.Count; fi++)
                {
                    if (found[fi] != null && MpTargetMatchesIdentity(found[fi], t))
                    {
                        replaced = true;
                        break;
                    }
                }
                if (!replaced)
                    found.Add(t);
            }
            bool hadTargets = multiplayerPipTargets.Count > 0;
            multiplayerPipTargets = found;
            multiplayerPipTargets.Sort((a, b) =>
            {
                int actorA = a != null ? a.ownerActor : 0;
                int actorB = b != null ? b.ownerActor : 0;
                if (actorA != actorB)
                    return actorA.CompareTo(actorB);
                int nameCmp = string.Compare(
                    a != null ? a.playerName ?? string.Empty : string.Empty,
                    b != null ? b.playerName ?? string.Empty : string.Empty,
                    System.StringComparison.OrdinalIgnoreCase);
                if (nameCmp != 0)
                    return nameCmp;
                return (a != null ? a.viewId : 0).CompareTo(b != null ? b.viewId : 0);
            });
            multiplayerPipScanVersion++;
            multiplayerPipEmptyScans = found.Count > 0 ? 0 : multiplayerPipEmptyScans + 1;
            if (multiplayerPipLastLoggedScanCount != found.Count)
            {
                multiplayerPipLastLoggedScanCount = found.Count;
                string scanNames = "";
                for (int ni = 0; ni < found.Count; ni++)
                {
                    if (ni > 0) scanNames += ", ";
                    scanNames += found[ni].playerName;
                }
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP scan n=" + found.Count + " [" + scanNames + "]");
            }
            if (found.Count == 0 && multiplayerPipAutoShownForTargets)
                multiplayerPipAutoShownForTargets = false;
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                if (multiplayerPipWindows[i].target != null && found.Contains(multiplayerPipWindows[i].target))
                    multiplayerPipWindows[i].lastSeenAt = Time.unscaledTime;
            }
            for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
            {
                MultiplayerPipWindow scanWindow = multiplayerPipWindows[i];
                if (scanWindow.target == null || !multiplayerPipTargets.Contains(scanWindow.target))
                {
                    if (TryRebindMultiplayerPipWindow(scanWindow))
                        continue;
                    if (scanWindow.orphanedSince < 0f)
                        scanWindow.orphanedSince = Time.unscaledTime;
                    if (scanWindow.pinned)
                        continue;
                    if (Time.unscaledTime - scanWindow.orphanedSince < MULTIPLAYER_PIP_TARGET_GRACE)
                        continue;
                    CloseMultiplayerPipWindow(scanWindow);
                }
                else if (scanWindow.orphanedSince >= 0f)
                    scanWindow.orphanedSince = -1f;
            }
        }

        private Transform FindMultiplayerCameraMount(Transform root)
        {
            Transform fallback = null;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                string name = child.name.ToLowerInvariant();
                if (name == "cameraparentpoint")
                    return child;
                if (fallback == null && (name.Contains("camera") || name.Contains("actioncam")))
                    fallback = child;
            }
            return fallback;
        }

        private bool IsMultiplayerPipMouseBlocked(Vector2 mouse)
        {
            if (lobbyBrowserOpen && lobbyBrowserRect.width > 1f && lobbyBrowserRect.Contains(mouse))
                return true;
            if (Time.unscaledTime < lobbyAlertUntil && lobbyAlertRect.width > 1f && lobbyAlertRect.Contains(mouse))
                return true;
            if (pipExternalMonitor)
                return false;
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w == null || w.target == null)
                    continue;
                if (w.rect.Contains(mouse))
                    return true;
                if (w.dropdownOpen && GetMultiplayerPipDropdownRect(w).Contains(mouse))
                    return true;
            }
            return false;
        }

        private void HandleMultiplayerPipInput()
        {
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            float screenDX = 0f;
            float screenDY = 0f;
            if (lastFrameMousePosValid)
            {
                screenDX = mouse.x - lastFrameMousePos.x;
                screenDY = mouse.y - lastFrameMousePos.y;
            }
            lastFrameMousePos = mouse;
            lastFrameMousePosValid = true;
            float axisDX = Input.GetAxisRaw("Mouse X");
            float axisDY = Input.GetAxisRaw("Mouse Y");
            if (Mathf.Abs(screenDX) > 0.5f && Mathf.Abs(axisDX) > 0.001f)
                mouseAxisScaleX = Mathf.Lerp(mouseAxisScaleX, screenDX / axisDX, 0.25f);
            if (Mathf.Abs(screenDY) > 0.5f && Mathf.Abs(axisDY) > 0.001f)
                mouseAxisScaleY = Mathf.Lerp(mouseAxisScaleY, screenDY / axisDY, 0.25f);
            float deltaX = Mathf.Abs(screenDX) > 0.5f ? screenDX : axisDX * mouseAxisScaleX;
            float deltaY = Mathf.Abs(screenDY) > 0.5f ? screenDY : axisDY * mouseAxisScaleY;
            if (multiplayerPipWindows.Count == 0 || pipExternalMonitor)
                return;
            bool leftDown = Input.GetMouseButtonDown(0);
            bool leftHeld = Input.GetMouseButton(0);
            bool leftUp = Input.GetMouseButtonUp(0);
            if (leftDown)
            {
                string clickHit = "none";
                for (int ci = 0; ci < multiplayerPipWindows.Count; ci++)
                {
                    MultiplayerPipWindow cw = multiplayerPipWindows[ci];
                    if (cw == null || cw.target == null)
                        continue;
                    Rect cpanel = cw.rect;
                    if (!cpanel.Contains(mouse))
                        continue;
                    PipHeaderRects(cpanel, out Rect cpilot, out Rect cpin, out Rect cplus, out Rect cclose);
                    if (cpilot.Contains(mouse)) clickHit = "pilot";
                    else if (cclose.Contains(mouse)) clickHit = "close";
                    else if (cplus.Contains(mouse)) clickHit = "plus";
                    else if (cpin.Contains(mouse)) clickHit = "pin";
                    else if (GetPiPResizeEdge(cpanel, mouse) != 0) clickHit = "edge";
                    else clickHit = "video";
                    if (ModLog.DebugEnabled)
                        ModLog.LogFile("[GhostLines] MPPIP click i=" + ci + " hit=" + clickHit +
                            " mouse=" + mouse.x.ToString("F0") + "," + mouse.y.ToString("F0") +
                            " win=" + cpanel.x.ToString("F0") + "," + cpanel.y.ToString("F0") + "," + cpanel.width.ToString("F0") + "x" + cpanel.height.ToString("F0"));
                    break;
                }
            }
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w.target == null)
                    continue;
                if (w.dragCandidate || w.dragging || w.resizing)
                {
                    if (leftHeld)
                    {
                        if (w.dragCandidate || w.dragging)
                        {
                            w.dragAccumX += deltaX;
                            w.dragAccumY += deltaY;
                        }
                        if (w.dragCandidate && !w.dragging &&
                            w.dragAccumX * w.dragAccumX + w.dragAccumY * w.dragAccumY > 36f)
                        {
                            w.dragging = true;
                            w.dragStartRect = w.rect;
                        }
                    }
                    else if (leftUp)
                    {
                        if (ModLog.DebugEnabled)
                            ModLog.LogFile("[GhostLines] MPPIP up dragging=" + w.dragging + " candidate=" + w.dragCandidate);
                        w.dragCandidate = false;
                        w.dragging = false;
                        w.resizing = false;
                        w.resizeEdge = 0;
                        w.dragAccumX = 0f;
                        w.dragAccumY = 0f;
                        w.resizeAccumX = 0f;
                        w.resizeAccumY = 0f;
                    }
                    else if (w.dragCandidate && !w.dragging)
                    {
                        if (ModLog.DebugEnabled)
                            ModLog.LogFile("[GhostLines] MPPIP candidate no-hold");
                        w.dragCandidate = false;
                        w.dragAccumX = 0f;
                        w.dragAccumY = 0f;
                    }
                    break;
                }
                if (IsUiBlockedFor(UI_LAYER_MPPIP, mouse))
                    continue;
                Rect panel = w.rect;
                PipHeaderRects(panel, out Rect pilotBtn, out Rect pinBtn, out Rect plusBtn, out Rect closeBtn);
                if (leftDown)
                {
                    uiFrontLayer = UI_LAYER_MPPIP;
                    if (pilotBtn.Contains(mouse))
                    {
                        PromoteMultiplayerPipWindow(w);
                        w.dropdownOpen = !w.dropdownOpen;
                        break;
                    }
                    if (closeBtn.Contains(mouse))
                    {
                        CloseMultiplayerPipWindow(w);
                        break;
                    }
                    if (plusBtn.Contains(mouse))
                    {
                        PromoteMultiplayerPipWindow(w);
                        MultiplayerPipTarget nextTarget = GetMultiplayerPipNextUnshownTarget();
                        if (nextTarget != null)
                            ToggleMultiplayerPip(nextTarget);
                        break;
                    }
                    if (pinBtn.Contains(mouse))
                    {
                        PromoteMultiplayerPipWindow(w);
                        break;
                    }
                }
                if (w.dropdownOpen)
                {
                    Rect dd = GetMultiplayerPipDropdownRect(w);
                    if (leftDown)
                    {
                        if (dd.Contains(mouse))
                            break;
                        w.dropdownOpen = false;
                    }
                    else if (leftUp)
                    {
                        if (dd.Contains(mouse))
                        {
                            int optIndex = (int)((mouse.y - dd.y) / 20f);
                            if (optIndex >= 0 && optIndex < multiplayerPipTargets.Count && multiplayerPipTargets[optIndex] != null)
                                AssignMultiplayerPiPPilot(w, multiplayerPipTargets[optIndex]);
                            w.dropdownOpen = false;
                            break;
                        }
                        w.dropdownOpen = false;
                    }
                    else
                        break;
                    continue;
                }
                if (!leftDown)
                    continue;
                int edge = GetPiPResizeEdge(panel, mouse);
                if (edge != 0)
                {
                    w.resizing = true;
                    w.resizeEdge = edge;
                    w.resizeAccumX = 0f;
                    w.resizeAccumY = 0f;
                    w.resizeStartRect = panel;
                    break;
                }
                if (panel.Contains(mouse) && !pilotBtn.Contains(mouse) && !pinBtn.Contains(mouse) && !plusBtn.Contains(mouse) && !closeBtn.Contains(mouse))
                {
                    PromoteMultiplayerPipWindow(w);
                    w.dragCandidate = true;
                    w.dragAccumX = 0f;
                    w.dragAccumY = 0f;
                    break;
                }
            }
        }

        private void PromoteMultiplayerPipWindow(MultiplayerPipWindow w)
        {
            if (multiplayerPipWindows.Count < 2 || multiplayerPipWindows[0] == w)
                return;
            multiplayerPipWindows.Remove(w);
            multiplayerPipWindows.Insert(0, w);
        }

        private MultiplayerPipTarget GetMultiplayerPipNextUnshownTarget()
        {
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                MultiplayerPipTarget target = multiplayerPipTargets[i];
                if (target == null || target.root == null)
                    continue;
                bool shown = false;
                for (int j = 0; j < multiplayerPipWindows.Count; j++)
                {
                    if (multiplayerPipWindows[j].target == target)
                    {
                        shown = true;
                        break;
                    }
                }
                if (!shown)
                    return target;
            }
            return null;
        }

        private void UpdateMultiplayerPipResize(MultiplayerPipWindow w, float dx, float dy)
        {
            w.rect = ApplyPipAspectResize(w.resizeEdge, w.resizeStartRect, dx, dy);
            ClampMultiplayerPipWindowRect(w);
            EnsureMultiplayerPipRenderTexture(w);
        }

        private void AssignMultiplayerPiPPilot(MultiplayerPipWindow w, MultiplayerPipTarget target)
        {
            if (target == null || w.target == target)
                return;
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                if (multiplayerPipWindows[i] != w && multiplayerPipWindows[i].target == target)
                    return;
            }
            if (w.target != null)
                w.target.enabled = false;
            w.target = target;
            w.target.enabled = true;
        }

        private Rect GetMultiplayerPipDropdownRect(MultiplayerPipWindow w)
        {
            Rect pilotBtn = new Rect(w.rect.x + 6f, w.rect.y + 3f, Mathf.Max(70f, w.rect.width - 84f), 18f);
            float rowH = 20f;
            float ddH = rowH * Mathf.Max(multiplayerPipTargets.Count, 1);
            float ddW = Mathf.Max(pilotBtn.width, w.rect.width - 84f);
            float ddY = pilotBtn.yMax;
            if (ddY + ddH > Screen.height - 4f)
                ddY = Mathf.Max(4f, pilotBtn.y - ddH);
            return new Rect(pilotBtn.x, ddY, ddW, ddH);
        }

        private void ClampMultiplayerPipWindowRect(MultiplayerPipWindow w)
        {
            w.rect.x = Mathf.Clamp(w.rect.x, 0f, Mathf.Max(0f, Screen.width - w.rect.width));
            w.rect.y = Mathf.Clamp(w.rect.y, 0f, Mathf.Max(0f, Screen.height - w.rect.height));
        }

        private bool MpTargetMatchesIdentity(MultiplayerPipTarget a, MultiplayerPipTarget b)
        {
            if (a == null || b == null)
                return false;
            if (a.ownerActor != 0 && b.ownerActor != 0)
                return a.ownerActor == b.ownerActor;
            return !string.IsNullOrEmpty(a.playerName) && a.playerName == b.playerName;
        }

        private MultiplayerPipTarget FindMpRebindTarget(MultiplayerPipTarget old)
        {
            if (old == null)
                return null;
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                MultiplayerPipTarget t = multiplayerPipTargets[i];
                if (t == null || t == old || t.root == null)
                    continue;
                if (MpTargetMatchesIdentity(t, old))
                    return t;
            }
            return null;
        }

        private bool TryRebindMultiplayerPipWindow(MultiplayerPipWindow w)
        {
            MultiplayerPipTarget newTarget = FindMpRebindTarget(w.target);
            if (newTarget == null)
                return false;
            MultiplayerPipTarget old = w.target;
            newTarget.enabled = true;
            if (old != null)
                newTarget.tagVisible = old.tagVisible;
            w.target = newTarget;
            w.orphanedSince = -1f;
            w.lastSeenAt = Time.unscaledTime;
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP rebind -> " + newTarget.playerName +
                " actor=" + newTarget.ownerActor + " viewId=" + newTarget.viewId);
            return true;
        }

        private void TickMultiplayerPipWindows()
        {
            for (int i = stickCamInstances.Count - 1; i >= 0; i--)
            {
                StickCamInstance si = stickCamInstances[i];
                if (si.mpTarget == null)
                    continue;
                if (si.mpTarget.root != null && multiplayerPipTargets.Contains(si.mpTarget))
                    continue;
                MultiplayerPipTarget stickRebind = FindMpRebindTarget(si.mpTarget);
                if (stickRebind != null)
                {
                    si.mpTarget = stickRebind;
                    continue;
                }
                if (si.mpTarget.root == null && !multiplayerPipTargets.Contains(si.mpTarget))
                {
                    if (stickCamResizeTarget == si)
                    {
                        stickCamResizeTarget = null;
                        stickCamResizing = false;
                    }
                    stickCamInstances.RemoveAt(i);
                    continue;
                }
                if (Time.unscaledTime >= nextOrphanRescanAt)
                {
                    nextOrphanRescanAt = Time.unscaledTime + 2f;
                    nextMultiplayerPipScan = 0f;
                }
            }
            for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w.target == null)
                {
                    CloseMultiplayerPipWindow(w);
                    continue;
                }
                if (w.target.root == null)
                {
                    if (w.orphanedSince < 0f)
                        w.orphanedSince = Time.unscaledTime;
                    if (Time.unscaledTime >= nextOrphanRescanAt)
                    {
                        nextOrphanRescanAt = Time.unscaledTime + 2f;
                        nextMultiplayerPipScan = 0f;
                    }
                    if (!TryRebindMultiplayerPipWindow(w))
                    {
                        if (!w.pinned && Time.unscaledTime - w.orphanedSince >= MULTIPLAYER_PIP_TARGET_GRACE)
                            CloseMultiplayerPipWindow(w);
                        continue;
                    }
                }
                else if (w.orphanedSince >= 0f)
                    w.orphanedSince = -1f;
            }
        }

        private void ToggleMultiplayerPip(MultiplayerPipTarget target)
        {
            if (target == null)
                return;
            target.enabled = !target.enabled;
            if (target.enabled)
            {
                AddMultiplayerPipWindow(target);
            }
            else
            {
                for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
                    if (multiplayerPipWindows[i].target == target) CloseMultiplayerPipWindow(multiplayerPipWindows[i]);
            }
        }

        private void AddMultiplayerPipWindow(MultiplayerPipTarget target)
        {
            if (target == null)
                return;
            int index = multiplayerPipWindows.Count;
            MultiplayerPipWindow w = new MultiplayerPipWindow { target = target, rect = new Rect((Screen.width - 320f) * 0.5f + index * 28f, (Screen.height - 200f) * 0.5f + index * 28f, 320f, 200f) };
            w.lastSeenAt = Time.unscaledTime;
            ClampMultiplayerPipWindowRect(w);
            CreateMultiplayerPipCamera(w);
            if (pipExternalMonitor)
                multiplayerPipWindows.Insert(0, w);
            else
                multiplayerPipWindows.Add(w);
        }

        private void CreateMultiplayerPipCamera(MultiplayerPipWindow w)
        {
            w.camObject = new GameObject("GhostLinesMultiplayerPipCamera");
            w.camObject.hideFlags = HideFlags.HideAndDontSave;
            w.cam = w.camObject.AddComponent<Camera>();
            Camera main = Camera.main;
            w.cam.enabled = false;
            w.cam.clearFlags = main != null ? main.clearFlags : CameraClearFlags.SolidColor;
            w.cam.backgroundColor = main != null ? main.backgroundColor : Color.black;
            float camFov = main != null ? main.fieldOfView : 60f;
            if (mpZoomFovCaptured)
                camFov = mpZoomNormalFov;
            w.cam.fieldOfView = camFov;
            w.cam.nearClipPlane = main != null ? main.nearClipPlane : 0.05f;
            w.cam.farClipPlane = pipFarClip;
            w.cam.cullingMask = GetPiPCullingMask();
            w.cam.allowHDR = false;
            w.cam.allowMSAA = false;
            w.cam.useOcclusionCulling = false;
            w.cam.renderingPath = RenderingPath.Forward;
            EnsureMultiplayerPipRenderTexture(w);
        }

        private void EnsureMultiplayerPipRenderTexture(MultiplayerPipWindow w)
        {
            if (w.cam == null)
                return;
            if (w.resizing)
                return;
            Rect content = GetPiPContentRect(w.rect);
            float targetW = content.width * pipResolutionScale;
            float targetH = content.height * pipResolutionScale;
            if (pipExternalMonitor && w.nativePip != null && w.nativePip.IsOpen)
            {
                RECT cl;
                if (GetClientRect(w.nativePip.hwnd, out cl) && cl.right > 0 && cl.bottom > 0)
                {
                    targetW = cl.right * pipResolutionScale;
                    targetH = cl.bottom * pipResolutionScale;
                }
            }
            float fit = Mathf.Min(1f, Mathf.Min(PIP_RT_MAX_WIDTH / targetW, PIP_RT_MAX_HEIGHT / targetH));
            int newW = Mathf.Clamp(Mathf.RoundToInt(targetW * fit), 48, (int)PIP_RT_MAX_WIDTH);
            int newH = Mathf.Clamp(Mathf.RoundToInt(targetH * fit), 48, (int)PIP_RT_MAX_HEIGHT);
            if (w.rt != null && Mathf.Abs(w.rtW - newW) <= PIP_RT_RECREATE_TOLERANCE && Mathf.Abs(w.rtH - newH) <= PIP_RT_RECREATE_TOLERANCE)
                return;
            if (w.rt != null)
            {
                w.rt.Release();
                Destroy(w.rt);
            }
            w.rtW = newW;
            w.rtH = newH;
            w.rt = new RenderTexture(newW, newH, 16, RenderTextureFormat.Default);
            w.cam.targetTexture = w.rt;
        }

        private void PruneMultiplayerPipTagCache()
        {
            if (multiplayerPipTagObjects.Count == 0)
                return;
            List<Transform> dead = null;
            foreach (var kv in multiplayerPipTagObjects)
            {
                if (kv.Key == null)
                {
                    if (dead == null)
                        dead = new List<Transform>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null)
                foreach (Transform t in dead)
                    multiplayerPipTagObjects.Remove(t);
        }

        private List<GameObject> GetMultiplayerPipTagObjects(Transform root)
        {
            List<GameObject> cached;
            if (multiplayerPipTagObjects.TryGetValue(root, out cached))
                return cached;
            cached = new List<GameObject>();
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                string n = child.name.ToLowerInvariant();
                if (n.IndexOf("name", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("tag", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("label", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("nick", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (child.GetComponent<TextMesh>() == null &&
                    child.GetComponent<CanvasRenderer>() == null &&
                    child.GetComponent<Renderer>() == null)
                    continue;
                cached.Add(child.gameObject);
            }
            multiplayerPipTagObjects[root] = cached;
            return cached;
        }

        private void RenderMultiplayerPipWindow(MultiplayerPipWindow w)
        {
            if (w.cam == null || w.rt == null || w.target == null || w.target.mount == null)
                return;
            Transform mount = w.target.mount;
            Quaternion rotation = mount.rotation;
            Vector3 position = mount.position + rotation * (Vector3.forward * PIP_CAMERA_FORWARD_OFFSET) + rotation * (Vector3.up * PIP_CAMERA_UP_OFFSET);
            w.cam.transform.SetPositionAndRotation(position, rotation);
            List<GameObject> pipTagHidden = null;
            if (!pipShowNamesDelta)
            {
                if (Time.unscaledTime >= multiplayerPipTagPruneAt)
                {
                    multiplayerPipTagPruneAt = Time.unscaledTime + 2f;
                    PruneMultiplayerPipTagCache();
                }
                for (int ti = 0; ti < multiplayerPipTargets.Count; ti++)
                {
                    MultiplayerPipTarget tg = multiplayerPipTargets[ti];
                    if (tg == null || tg.root == null)
                        continue;
                    List<GameObject> objs = GetMultiplayerPipTagObjects(tg.root);
                    for (int oi = 0; oi < objs.Count; oi++)
                    {
                        if (objs[oi] == null || !objs[oi].activeSelf)
                            continue;
                        if (pipTagHidden == null)
                            pipTagHidden = multiplayerPipRenderHiddenTags ?? (multiplayerPipRenderHiddenTags = new List<GameObject>());
                        pipTagHidden.Add(objs[oi]);
                        objs[oi].SetActive(false);
                    }
                }
            }
            w.cam.Render();
            if (pipTagHidden != null)
            {
                for (int i = 0; i < pipTagHidden.Count; i++)
                {
                    if (pipTagHidden[i] != null)
                        pipTagHidden[i].SetActive(true);
                }
                pipTagHidden.Clear();
            }
            if (w.nativePip != null)
                PushNativePipFrame(w.nativePip, w.rt);
        }

        private void CloseMultiplayerPipWindow(MultiplayerPipWindow w)
        {
            if (w == null) return;
            if (w.nativePip != null)
            {
                CloseNativePipWindow(w.nativePip);
                w.nativePip = null;
            }
            if (w.camObject != null) Destroy(w.camObject);
            w.camObject = null;
            w.cam = null;
            if (w.rt != null) { w.rt.Release(); Destroy(w.rt); }
            w.rt = null;
            w.rtW = -1;
            w.rtH = -1;
            for (int i = 0; i < w.docked.Count; i++)
            {
                PipDockEntry d = w.docked[i];
                if (d == null || d.graph == null)
                    continue;
                if (d.graph.rect.x <= -99999f)
                    d.graph.rect = new Rect(Screen.width * 0.5f - 160f, Screen.height * 0.35f, d.graph.rect.width, d.graph.rect.height);
            }
            w.docked.Clear();
            multiplayerPipWindows.Remove(w);
            if (w.target != null) w.target.enabled = false;
        }

        private void CloseMultiplayerPipWindows()
        {
            for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
                CloseMultiplayerPipWindow(multiplayerPipWindows[i]);
        }

        // Replay-style idle fade for the MP settings menu and lobby browser: while
        // the mouse moves or hovers them (or they're pinned) they are fully visible;
        // after 0.8s of stillness they fade away and return on the next movement.
        private void DrawMultiplayerPip(bool frontOnly = false)
        {
            // Pin-enabled idle fade: when the mouse is still and the menu isn't
            // pinned, it fades out like the replay windows (mouse movement brings it
            // back). Windows are drawn in DrawMultiplayerPipWindows, unaffected here.
            if (multiplayerPipMenuFade < 0.01f)
                return;
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            EnsureIcons();
            Color old = GUI.color;
            float a = multiplayerPipMenuFade * mpMenuIdleAlpha;
            Event e = Event.current;
            // Reset each frame; DrawDroneSwitchSection re-arms it while open.
            droneDropdownPending = false;
            // Don't carry an open drone picker across menu close.
            if (!multiplayerPipMenuOpen)
                droneSwitchDropdownOpen = false;

            float menuW = 300f;
            float droneSwitchSectionH = 26f;
            float wsStatusH = string.IsNullOrEmpty(wsStatus) ? 0f : 20f;
            float workshopSectionH = 18f + 22f + 26f + wsStatusH + 4f + 26f + 12f;
            float menuH = 28f + 20f + Mathf.Max(1, multiplayerPipTargets.Count) * 28f
                + droneSwitchSectionH + 22f + 30f * 3f + 28f + 22f + 28f * 2f + workshopSectionH;
            Rect menu = new Rect(Screen.width - menuW - 18f, Screen.height - menuH - 18f, menuW, menuH);
            menu.x += mpMenuDragOffset.x;
            menu.y += mpMenuDragOffset.y;
            mpMenuScreenRect = menu;
            if (a < 0.01f)
                return;
            Rect mpCloseBtn = new Rect(menu.xMax - 34f, menu.y + 3f, 28f, 22f);
            Rect mpPinBtn = new Rect(menu.xMax - 64f, menu.y + 4f, 24f, 20f);
            Rect mpKeysBtn = new Rect(menu.xMax - 92f, menu.y + 4f, 24f, 20f);
            bool mpCloseHover = mpCloseBtn.Contains(e.mousePosition);
            bool mpPinHover = mpPinBtn.Contains(e.mousePosition) || mpKeysBtn.Contains(e.mousePosition);
            // Drag the menu from anywhere in it (like the replay settings panel):
            // arm a candidate on MouseDown, promote to a real drag on MouseDrag, and
            // leave control drags (pilot radio sweep, drone picker/search) alone.
            if (e.type == EventType.MouseDown && e.button == 0 && menu.Contains(e.mousePosition))
            {
                uiFrontLayer = UI_LAYER_MPPIP;
                if (!mpCloseHover && !mpPinHover)
                    mpMenuDrag.Arm(e.mousePosition, mpMenuDragOffset);
            }
            bool mpMenuControlGesture = mpRadioDragSelecting || droneSwitchDropdownOpen || droneDropdownPending || dronesSearchFocused ||
                pipFarSliderDragging;
            if (mpMenuDrag.Drag(e, mpMenuControlGesture, ref mpMenuDragOffset))
            {
                float minOX = -(Screen.width - menuW - 18f);
                float maxOX = Mathf.Max(minOX, 18f);
                float minOY = -(Screen.height - menuH - 18f);
                float maxOY = Mathf.Max(minOY, 18f);
                mpMenuDragOffset.x = Mathf.Clamp(mpMenuDragOffset.x, minOX, maxOX);
                mpMenuDragOffset.y = Mathf.Clamp(mpMenuDragOffset.y, minOY, maxOY);
            }
            if (e.type == EventType.MouseUp && e.button == 0)
                mpMenuDrag.End();
            GUI.color = ThemePanelBg(a); GUI.DrawTexture(menu, whiteTex);
            GUI.color = ThemePanelBorder(a);
            GUI.DrawTexture(new Rect(menu.x, menu.y, menu.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(menu.x, menu.yMax - 1f, menu.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(menu.x, menu.y, 1f, menu.height), whiteTex);
            GUI.DrawTexture(new Rect(menu.xMax - 1f, menu.y, 1f, menu.height), whiteTex);
            GUI.color = ThemeLabel(a);
            GUI.Label(new Rect(menu.x + 12f, menu.y + 6f, 200f, 20f), "MP Settings", settingsHeaderStyle);
            if (DrawStyledToggleButton(mpPinBtn, iconPin, mpMenuPinned, a, true, UI_LAYER_MPPIP))
            {
                mpMenuPinned = !mpMenuPinned;
                overlayMouseMovedAt = Time.unscaledTime;
            }
            // Same Control Bindings window as the replay playback bar's keyboard button.
            if (DrawStyledButton(mpKeysBtn, iconBindings, a, UI_LAYER_MPPIP))
                ToggleBindingsWindow();
            GUI.color = mpCloseHover ? ThemeLabel(a) : ThemeDimLabel(a);
            // IMGUI text sits low; nudge up so the glyph centres on the pin button.
            GUI.Label(new Rect(mpCloseBtn.x, mpCloseBtn.y - 4f, mpCloseBtn.width, mpCloseBtn.height),
                "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);
            if (e.type == EventType.MouseDown && e.button == 0 && mpCloseHover)
            {
                multiplayerPipMenuOpen = false;
                SaveMpMenuOpen(false);
                e.Use();
                return;
            }
            float y = menu.y + 28f;
            bool pipBlock = IsMultiplayerPipMouseBlocked(e.mousePosition);

            float pilotsTop = y;
            GUI.color = ThemeLabel(a);
            GUI.Label(new Rect(menu.xMax - 159f, y, 36f, 18f), "Tags", settingsSectionStyle);
            GUI.Label(new Rect(menu.xMax - 131f, y, 36f, 18f), "Stick", settingsSectionStyle);
            GUI.Label(new Rect(menu.xMax - 103f, y, 36f, 18f), "Graph", settingsSectionStyle);
            GUI.Label(new Rect(menu.xMax - 75f, y, 36f, 18f), "PIP", settingsSectionStyle);
            GUI.Label(new Rect(menu.x + 12f, y, 100f, 18f), "Pilots", settingsSectionLeftStyle);
            y += 20f;
            if (multiplayerPipTargets.Count == 0)
            {
                GUI.color = ThemeDimLabel(a);
                GUI.Label(new Rect(menu.x + 12f, y, menu.width - 24f, 24f), "Waiting for pilots...", settingsSmallStyle);
                y += 28f;
            }
            else
            {
                for (int i = 0; i < multiplayerPipTargets.Count; i++)
                {
                    MultiplayerPipTarget target = multiplayerPipTargets[i];
                    if (target == null)
                        continue;
                    Color entryColor = target.pilotColor;
                    GUI.color = new Color(entryColor.r, entryColor.g, entryColor.b, a);
                    GUI.DrawTexture(new Rect(menu.x + 12f, y + 2f, 18f, 18f), whiteTex);
                    GUI.color = ThemeLabel(a);
                    GUI.Label(new Rect(menu.x + 40f, y, menu.width - 210f, 22f),
                        FitSettingsName(target.playerName, menu.width - 210f), settingsNameStyle);
                    bool pipOpen = false;
                    for (int j = 0; j < multiplayerPipWindows.Count; j++)
                    {
                        if (multiplayerPipWindows[j].target == target)
                        {
                            pipOpen = true;
                            break;
                        }
                    }
                    Rect tagRadio = new Rect(menu.xMax - 150f, y + 3f, 18f, 18f);
                    Rect stickRadio = new Rect(menu.xMax - 122f, y + 3f, 18f, 18f);
                    Rect graphRadio = new Rect(menu.xMax - 94f, y + 3f, 18f, 18f);
                    Rect pipRadio = new Rect(menu.xMax - 66f, y + 3f, 18f, 18f);
                    bool graphOpen = IsMpLiveGraphOpen(target);
                    bool stickPinned = false;
                    for (int sc = 0; sc < stickCamInstances.Count; sc++)
                    {
                        if (stickCamInstances[sc].mpTarget == target)
                        {
                            stickPinned = true;
                            break;
                        }
                    }
                    if (DrawMultiplayerTarget(tagRadio, target.tagVisible, a, pipBlock, 0, i))
                    {
                        target.tagVisible = !target.tagVisible;
                        SyncShowNameTagsFromTargets();
                    }
                    HandleMultiplayerRadioDrag(i, 0, tagRadio);
                    if (DrawMultiplayerTarget(stickRadio, stickPinned, a, pipBlock, 1, i))
                        ToggleStickCamFor(target);
                    HandleMultiplayerRadioDrag(i, 1, stickRadio);
                    if (DrawMultiplayerTarget(graphRadio, graphOpen, a, pipBlock, 2, i))
                        SetMpLiveGraphOpen(target, !graphOpen);
                    HandleMultiplayerRadioDrag(i, 2, graphRadio);
                    if (DrawMultiplayerTarget(pipRadio, pipOpen, a, pipBlock, 3, i))
                        ToggleMultiplayerPip(target);
                    HandleMultiplayerRadioDrag(i, 3, pipRadio);
                    y += 28f;
                }
            }
            GUI.color = ThemeOptionBorder(a);
            GUI.DrawTexture(new Rect(menu.xMax - 155f, pilotsTop + 18f, 1f, y - pilotsTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(menu.xMax - 127f, pilotsTop + 18f, 1f, y - pilotsTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(menu.xMax - 99f, pilotsTop + 18f, 1f, y - pilotsTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(menu.xMax - 71f, pilotsTop + 18f, 1f, y - pilotsTop - 22f), whiteTex);
            GUI.color = ThemeLabel(a);
            if (e.type == EventType.MouseUp && e.button == 0)
                mpRadioDragSelecting = false;

            DrawDroneSwitchSection(menu, ref y, a, e, pipBlock);

            GUI.color = ThemeLabel(a);
            y = DrawSettingsSectionDivider(menu.x + 12f, menu.width - 24f, y, "PiP Settings", a) + 4f;
            float pipGap = 6f;
            float pipPad = 16f;
            GUI.Label(new Rect(menu.x + 12f, y, 110f, 20f), "Refresh Rate", settingsSmallStyle);
            float pipAutoW = CalcLabelWidth(settingsSmallStyle, "Auto") + pipPad;
            float pip15W = CalcLabelWidth(settingsSmallStyle, "15 Hz") + pipPad;
            float pip30W = CalcLabelWidth(settingsSmallStyle, "30 Hz") + pipPad;
            float pip60W = CalcLabelWidth(settingsSmallStyle, "60 Hz") + pipPad;
            float pipBtnsX = menu.xMax - 14f - (pipAutoW + pipGap + pip15W + pipGap + pip30W + pipGap + pip60W);
            if (DrawSettingsOption(new Rect(pipBtnsX, y - 2f, pipAutoW, 22f), "Auto", pipRefreshAuto, a, TextAnchor.MiddleCenter, -1, !pipBlock) && !pipRefreshAuto)
                SetPipRefreshAuto(true);
            if (DrawSettingsOption(new Rect(pipBtnsX + pipAutoW + pipGap, y - 2f, pip15W, 22f), "15 Hz", !pipRefreshAuto && pipRefreshHz == 15, a, TextAnchor.MiddleCenter, -1, !pipBlock))
                SetPipRefreshHz(15);
            if (DrawSettingsOption(new Rect(pipBtnsX + pipAutoW + pipGap + pip15W + pipGap, y - 2f, pip30W, 22f), "30 Hz", !pipRefreshAuto && pipRefreshHz == 30, a, TextAnchor.MiddleCenter, -1, !pipBlock))
                SetPipRefreshHz(30);
            if (DrawSettingsOption(new Rect(pipBtnsX + pipAutoW + pipGap + pip15W + pipGap + pip30W + pipGap, y - 2f, pip60W, 22f), "60 Hz", !pipRefreshAuto && pipRefreshHz == 60, a, TextAnchor.MiddleCenter, -1, !pipBlock))
                SetPipRefreshHz(60);
            y += 30f;

            GUI.Label(new Rect(menu.x + 12f, y, 110f, 20f), "Resolution", settingsSmallStyle);
            float pip25W = CalcLabelWidth(settingsSmallStyle, "25%") + pipPad;
            float pip50W = CalcLabelWidth(settingsSmallStyle, "50%") + pipPad;
            float pip75W = CalcLabelWidth(settingsSmallStyle, "75%") + pipPad;
            float pip100W = CalcLabelWidth(settingsSmallStyle, "100%") + pipPad;
            float pipResBtnsX = menu.xMax - 14f - (pip25W + pipGap + pip50W + pipGap + pip75W + pipGap + pip100W);
            if (DrawSettingsOption(new Rect(pipResBtnsX, y - 2f, pip25W, 22f), "25%", pipResolutionScale == 0.25f, a, TextAnchor.MiddleCenter, -1, !pipBlock) && pipResolutionScale != 0.25f)
                SetPipResolutionScale(0.25f);
            if (DrawSettingsOption(new Rect(pipResBtnsX + pip25W + pipGap, y - 2f, pip50W, 22f), "50%", pipResolutionScale == 0.5f, a, TextAnchor.MiddleCenter, -1, !pipBlock) && pipResolutionScale != 0.5f)
                SetPipResolutionScale(0.5f);
            if (DrawSettingsOption(new Rect(pipResBtnsX + pip25W + pipGap + pip50W + pipGap, y - 2f, pip75W, 22f), "75%", pipResolutionScale == 0.75f, a, TextAnchor.MiddleCenter, -1, !pipBlock) && pipResolutionScale != 0.75f)
                SetPipResolutionScale(0.75f);
            if (DrawSettingsOption(new Rect(pipResBtnsX + pip25W + pipGap + pip50W + pipGap + pip75W + pipGap, y - 2f, pip100W, 22f), "100%", pipResolutionScale == 1f, a, TextAnchor.MiddleCenter, -1, !pipBlock) && pipResolutionScale != 1f)
                SetPipResolutionScale(1f);
            y += 30f;

            GUI.Label(new Rect(menu.x + 12f, y, 110f, 20f), "Draw Distance", settingsSmallStyle);
            float distTrackW = 100f;
            float distValW = 46f;
            float distTrackX = menu.xMax - 14f - distValW - 6f - distTrackW;
            Rect distTrack = new Rect(distTrackX, y + 8f, distTrackW, 6f);
            Rect distValRect = new Rect(menu.xMax - 14f - distValW, y, distValW, 20f);
            Rect distHandle = new Rect(distTrack.x + Mathf.Clamp01((pipFarClip - 25f) / 475f) * (distTrack.width - 10f), distTrack.y - 4f, 10f, 14f);
            bool distHit = !pipBlock && (distHandle.Contains(e.mousePosition) || distTrack.Contains(e.mousePosition));
            if (e.type == EventType.MouseDown && e.button == 0 && distHit)
            {
                pipFarSliderDragging = true;
                ApplyPipFarClipFromSliderX(e.mousePosition.x, distTrack);
                e.Use();
            }
            if (pipFarSliderDragging && e.type == EventType.MouseDrag && e.button == 0)
            {
                ApplyPipFarClipFromSliderX(e.mousePosition.x, distTrack);
                e.Use();
            }
            if (pipFarSliderDragging && e.type == EventType.MouseUp && e.button == 0)
            {
                pipFarSliderDragging = false;
                SavePipFarClip();
            }
            GUI.color = darkMode ? new Color(0.2f, 0.22f, 0.26f, a) : new Color(0.6f, 0.6f, 0.62f, a);
            GUI.DrawTexture(distTrack, whiteTex);
            GUI.color = pipFarSliderDragging || distHit ? new Color(0.25f, 0.62f, 0.95f, a) : darkMode ? new Color(0.7f, 0.72f, 0.76f, a) : new Color(0.42f, 0.42f, 0.45f, a);
            GUI.DrawTexture(distHandle, whiteTex);
            GUI.color = ThemeLabel(a);
            GUI.Label(distValRect, Mathf.RoundToInt(pipFarClip) + "m", settingsSmallRightStyle);
            y += 30f;

            float mpColLX = menu.x + 12f;
            float mpColRX = menu.x + menu.width * 0.5f + 4f;
            float ndW = CalcLabelWidth(settingsSmallStyle, "Names/Delta in PiP");
            GUI.Label(new Rect(mpColLX, y, ndW, 24f), "Names/Delta in PiP", settingsSmallStyle);
            if (DrawSettingsToggle(new Rect(mpColLX + ndW + 8f, y + 3f, 30f, 18f), pipShowNamesDelta, a, !pipBlock))
                SetPipShowNamesDelta(!pipShowNamesDelta);
            float extW = CalcLabelWidth(settingsSmallStyle, "External Monitor PiP");
            GUI.Label(new Rect(mpColRX, y, extW, 24f), "External Monitor PiP", settingsSmallStyle);
            if (DrawSettingsToggle(new Rect(mpColRX + extW + 8f, y + 3f, 30f, 18f), pipExternalMonitor, a, !pipBlock))
                SetPipExternalMonitor(!pipExternalMonitor);
            y += 28f;

            GUI.color = ThemeLabel(a);
            y = DrawSettingsSectionDivider(menu.x + 12f, menu.width - 24f, y, "Display", a) + 4f;
            float lgW = CalcLabelWidth(settingsSmallStyle, "Live Input Graph");
            GUI.Label(new Rect(mpColLX, y, lgW, 24f), "Live Input Graph", settingsSmallStyle);
            // The switch mirrors reality: any open graph (opened here, via a
            // pilot's Graph radio, or the hotkey) shows it on. Turning it off is
            // still the global close-all.
            liveGraphSwitchOpen = liveInputGraphs.Count > 0;
            if (DrawSettingsToggle(new Rect(mpColLX + lgW + 8f, y + 3f, 30f, 18f), liveGraphSwitchOpen, a, !pipBlock))
            {
                liveGraphSwitchOpen = !liveGraphSwitchOpen;
                SetLiveGraphOpen(liveGraphSwitchOpen);
            }
            float dmW = CalcLabelWidth(settingsSmallStyle, "Dark Mode");
            GUI.Label(new Rect(mpColRX, y, dmW, 24f), "Dark Mode", settingsSmallStyle);
            if (DrawSettingsToggle(new Rect(mpColRX + dmW + 8f, y + 3f, 30f, 18f), darkMode, a, !pipBlock))
            {
                darkMode = !darkMode;
                SaveDisplayPreferences();
            }
            y += 28f;

            float nadW = CalcLabelWidth(settingsSmallStyle, "Names above Drones");
            GUI.Label(new Rect(mpColLX, y, nadW, 24f), "Names above Drones", settingsSmallStyle);
            if (DrawSettingsToggle(new Rect(mpColLX + nadW + 8f, y + 3f, 30f, 18f), mpShowDroneNames, a, !pipBlock))
                mpShowDroneNames = !mpShowDroneNames;
            float gsW = CalcLabelWidth(settingsSmallStyle, "Gate Splits");
            GUI.Label(new Rect(mpColRX, y, gsW, 24f), "Gate Splits", settingsSmallStyle);
            if (DrawSettingsToggle(new Rect(mpColRX + gsW + 8f, y + 3f, 30f, 18f),
                Prefs.gateSplitsConfig != null && Prefs.gateSplitsConfig.Value, a, !pipBlock) &&
                Prefs.gateSplitsConfig != null)
                Prefs.gateSplitsConfig.Value = !Prefs.gateSplitsConfig.Value;
            y += 28f;

            GUI.color = ThemeLabel(a);
            y = DrawSettingsSectionDivider(menu.x + 12f, menu.width - 24f, y, "Workshop", a);
            if (ModLog.DebugEnabled && Time.unscaledTime >= wsSectionLogAt)
            {
                wsSectionLogAt = Time.unscaledTime + 2f;
                pluginLog.LogInfo("[GhostLines][WS] mp menu: mpInSession=" + mpInSession +
                    " track='" + mpCurrentTrackName + "' race='" + mpCurrentRaceName +
                    "' trackType=" + (wsTrackNameType != null ? wsTrackNameType.Name : "null") +
                    " raceType=" + (wsRaceNameType != null ? wsRaceNameType.Name : "null") +
                    " busy=" + wsQueryBusy + " status='" + wsStatus + "'");
            }
            string wsTargetName = !string.IsNullOrEmpty(mpCurrentRaceName) ? mpCurrentRaceName : mpCurrentTrackName;
            string wsNameText;
            if (!string.IsNullOrEmpty(wsTargetName) && !string.IsNullOrEmpty(wsEnvironmentName))
                wsNameText = wsEnvironmentName + " - " + wsTargetName;
            else if (!string.IsNullOrEmpty(wsTargetName))
                wsNameText = wsTargetName;
            else if (!string.IsNullOrEmpty(wsEnvironmentName))
                wsNameText = wsEnvironmentName;
            else
                wsNameText = "No track detected yet";
            float wsNameMax = menu.width - 24f;
            GUI.color = ThemeDimLabel(a);
            GUI.Label(new Rect(menu.x + 12f, y, wsNameMax, 18f), FitSettingsName(wsNameText, wsNameMax), settingsSmallStyle);
            y += 22f;
            string dlLabel = "Download";
            if (wsQueryBusy)
                dlLabel = !string.IsNullOrEmpty(wsStatus) && wsStatus.StartsWith("Downloading") ? "Downloading…" : "Searching…";
            else if (wsDownloadingId != 0)
                dlLabel = "Downloading…";
            float wsDlW = CalcLabelWidth(settingsSmallStyle, dlLabel) + 16f;
            float wsCopyW = CalcLabelWidth(settingsSmallStyle, "Copy Link") + 16f;
            float wsBtnRight = menu.xMax - 12f;
            // Measured with the style actually used to draw it (settingsLabelStyle,
            // 12pt) — measuring with settingsSmallStyle (10pt) clipped the label.
            float wsAutoW = CalcLabelWidth(settingsLabelStyle, "Auto Download") + 4f;
            if (menu.x + 12f + wsAutoW + 6f + 30f + 6f + wsDlW + 6f + wsCopyW + 12f > menu.xMax)
            {
                wsDlW = CalcLabelWidth(settingsSmallStyle, dlLabel) + 8f;
                wsCopyW = CalcLabelWidth(settingsSmallStyle, "Copy Link") + 8f;
            }
            GUI.color = ThemeLabel(a);
            GUI.Label(new Rect(menu.x + 12f, y, wsAutoW, 24f), "Auto Download", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(menu.x + 12f + wsAutoW + 6f, y + 3f, 30f, 18f), autoDownloadWorkshop, a))
            {
                autoDownloadWorkshop = !autoDownloadWorkshop;
                SaveAutoDownloadWorkshopPref(autoDownloadWorkshop);
            }
            Rect wsCopyRect = new Rect(wsBtnRight - wsCopyW, y - 2f, wsCopyW, 22f);
            Rect wsDlRect = new Rect(wsCopyRect.x - 6f - wsDlW, y - 2f, wsDlW, 22f);
            if (DrawSettingsOption(wsDlRect, dlLabel, wsQueryBusy || wsDownloadingId != 0, a, TextAnchor.MiddleCenter, -1, !pipBlock && !wsOfficialContent, wsOfficialContent) && !wsQueryBusy)
                StartWorkshopDownloadAll();
            if (DrawSettingsOption(wsCopyRect, "Copy Link", false, a, TextAnchor.MiddleCenter, -1, !pipBlock && !wsOfficialContent, wsOfficialContent))
                CopyWorkshopLink();
            y += 26f;
            if (!string.IsNullOrEmpty(wsStatus))
            {
                GUI.color = ThemeDimLabel(a);
                GUI.Label(new Rect(menu.x + 12f, y, wsNameMax, 18f), FitSettingsName(wsStatus, wsNameMax), settingsSmallStyle);
                y += 20f;
            }
            y += 4f;
            if (DrawSettingsOption(new Rect(menu.x + 12f, y, menu.width - 24f, 26f), "Lobby Browser", lobbyBrowserOpen, a, TextAnchor.MiddleCenter, -1, !pipBlock))
            {
                lobbyBrowserOpen = !lobbyBrowserOpen;
                if (lobbyBrowserOpen)
                {
                    lobbyBrowserIdleAlpha = 1f;
                    overlayMouseMovedAt = Time.unscaledTime;
                }
            }
            y += 26f + 12f;

            GUI.color = old;
        }

        // MP PiP windows and their pilot dropdowns. Drawn in a separate pass so their
        // z-order relative to the live input graphs can follow uiFrontLayer (the last
        // clicked window ends up on top).
        private void DrawMultiplayerPipWindows()
        {
            if (multiplayerPipMenuFade < 0.01f && multiplayerPipWindows.Count == 0)
                return;
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            EnsureIcons();
            Color old = GUI.color;
            float a = multiplayerPipMenuFade;
            Event e = Event.current;

            StickCamInstance pipDragStick = null;
            for (int i = 0; i < stickCamInstances.Count; i++)
            {
                if (stickCamInstances[i].dragging)
                {
                    pipDragStick = stickCamInstances[i];
                    break;
                }
            }
            LiveGraphState pipDragGraph = null;
            for (int i = 0; i < liveInputGraphs.Count; i++)
            {
                if (liveInputGraphs[i].dragging)
                {
                    pipDragGraph = liveInputGraphs[i];
                    break;
                }
            }
            MultiplayerPipWindow pipDockPreviewWindow = null;
            int pipDockPreviewSlot = -1;
            if (pipDragStick != null || pipDragGraph != null)
            {
                pipDockPreviewWindow = FindMpPipWindowForDock(e.mousePosition);
                if (pipDockPreviewWindow != null)
                    pipDockPreviewSlot = GetPipDockSlotAt(pipDockPreviewWindow.rect, e.mousePosition);
            }

            if (!pipExternalMonitor)
            {
                for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
                {
                    MultiplayerPipWindow window = multiplayerPipWindows[i];
                    if (window == null || window.target == null)
                        continue;
                    Rect panel = window.rect;
                    if (window.dragging && e.type == EventType.MouseDrag && e.button == 0)
                    {
                        window.rect.x += e.delta.x;
                        window.rect.y += e.delta.y;
                        ClampMultiplayerPipWindowRect(window);
                        e.Use();
                    }
                    else if (window.resizing && e.type == EventType.MouseDrag && e.button == 0)
                    {
                        window.resizeAccumX += e.delta.x;
                        window.resizeAccumY += e.delta.y;
                        UpdateMultiplayerPipResize(window, window.resizeAccumX, window.resizeAccumY);
                        e.Use();
                    }
                bool chromeVisible = window.chromeAlpha > 0.01f;
                GUI.color = new Color(0f, 0f, 0f, 0.18f * a); GUI.DrawTexture(new Rect(panel.x + 3f, panel.y + 3f, panel.width, panel.height), whiteTex);
                GUI.color = ThemePanelBg(a); GUI.DrawTexture(panel, whiteTex);
                if (window.rt != null)
                {
                    GUI.color = new Color(1f, 1f, 1f, a);
                    GUI.DrawTexture(chromeVisible ? GetPiPContentRect(panel) : panel, window.rt, ScaleMode.StretchToFill, false);
                }
                GUI.color = ThemePanelBorder(a);
                GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), whiteTex);
                GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), whiteTex);

                for (int de2 = window.docked.Count - 1; de2 >= 0; de2--)
                {
                    PipDockEntry de = window.docked[de2];
                    if ((de.stick != null && !stickCamInstances.Contains(de.stick)) ||
                        (de.graph != null && !liveInputGraphs.Contains(de.graph)))
                        window.docked.RemoveAt(de2);
                }

                if (window == pipDockPreviewWindow && pipDockPreviewSlot >= 0)
                {
                    Rect dockGridContent = GetPiPContentRect(panel);
                    for (int s = 0; s < 9; s++)
                    {
                        Rect cell = GetPipDockSlotRect(dockGridContent, s);
                        cell = new Rect(cell.x + 3f, cell.y + 3f, cell.width - 6f, cell.height - 6f);
                        bool slotHovered = s == pipDockPreviewSlot;
                        bool slotOccupied = PipSlotOccupied(window.docked, s);
                        GUI.color = slotHovered
                            ? new Color(1f, 0.55f, 0.1f, 0.32f * a)
                            : slotOccupied ? new Color(1f, 0.55f, 0.1f, 0.07f * a) : new Color(1f, 0.55f, 0.1f, 0.13f * a);
                        GUI.DrawTexture(cell, whiteTex);
                        GUI.color = new Color(1f, 0.6f, 0.1f, (slotHovered ? 0.9f : 0.45f) * a);
                        GUI.DrawTexture(new Rect(cell.x, cell.y, cell.width, 1f), whiteTex);
                        GUI.DrawTexture(new Rect(cell.x, cell.yMax - 1f, cell.width, 1f), whiteTex);
                        GUI.DrawTexture(new Rect(cell.x, cell.y, 1f, cell.height), whiteTex);
                        GUI.DrawTexture(new Rect(cell.xMax - 1f, cell.y, 1f, cell.height), whiteTex);
                    }
                }

                for (int de3 = 0; de3 < window.docked.Count; de3++)
                {
                    PipDockEntry de = window.docked[de3];
                    Rect dockContent = GetPiPContentRect(panel);
                    Rect cell = GetPipDockSlotRect(dockContent, de.slot);
                    cell = new Rect(cell.x + 3f, cell.y + 3f, cell.width - 6f, cell.height - 6f);
                    if (de.stick != null)
                    {
                        StickCamInstance di = de.stick;
                        Vector4 input = Vector4.zero; bool hasInput = false;
                        if (di.entry != null && replayReady)
                        {
                            float dockRt;
                            if (TryGetFrameReplayTime(out dockRt) && TryGetDroneInput(di.entry, dockRt, out input))
                                hasInput = true;
                        }
                        else if (di.mpTarget != null && di.mpTarget.root != null)
                        {
                            if (di.mpTarget.root != di.mpTarget.syncRoot || di.mpTarget.photonView != di.mpTarget.syncView)
                            {
                                di.mpTarget.syncComponent = null;
                                di.mpTarget.lastSyncAttempt = 0f;
                            }
                            if (di.mpTarget.syncComponent == null)
                                EnsureMpTargetSyncAccess(di.mpTarget);
                            if (di.mpTarget.syncComponent != null && TryGetMpInputCached(di.mpTarget, out input))
                                hasInput = true;
                        }
                        if (hasInput)
                        {
                            float gapd = cell.height * 0.12f;
                            float panelSize = Mathf.Min(cell.height, (cell.width - gapd) * 0.5f);
                            float groupW = panelSize * 2f + gapd;
                            Rect lp = new Rect(cell.center.x - groupW * 0.5f, cell.center.y - panelSize * 0.5f, panelSize, panelSize);
                            Rect rp = new Rect(lp.xMax + gapd, lp.y, panelSize, panelSize);
                            EnsureStickTextures();
                            DrawStickPanel(lp, new Vector2(input.y, input.x * 2f - 1f));
                            DrawStickPanel(rp, new Vector2(-input.w, input.z));
                            if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                            {
                                if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                                    (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                                {
                                lastPipDockClickTime = -1f;
                                di.x = Mathf.Clamp(e.mousePosition.x, groupW * 0.5f + 2f, Screen.width - groupW * 0.5f - 2f);
                                di.y = Mathf.Clamp(e.mousePosition.y, panelSize * 0.5f + 2f, Screen.height - panelSize * 0.5f - 2f);
                                window.docked.RemoveAt(de3);
                                de3--;
                                window.dragCandidate = false;
                                window.dragAccumX = 0f;
                                window.dragAccumY = 0f;
                                uiFrontLayer = UI_LAYER_STICKCAM;
                                e.Use();
                                }
                                else
                                {
                                    lastPipDockClickTime = Time.realtimeSinceStartup;
                                    lastPipDockClickPos = e.mousePosition;
                                }
                            }
                        }
                    }

                else if (de.graph != null && de.graph.mpSampleCount >= 2)
                {
                    LiveGraphState dg = de.graph;
                    int tw = Mathf.Max(1, (int)cell.width);
                    int th = Mathf.Max(1, (int)cell.height);
                    int history = LIVE_HISTORY_OPTIONS[Mathf.Clamp(dg.historyIndex, 0, LIVE_HISTORY_OPTIONS.Length - 1)];
                    bool rebuild = dg.traceTex == null || dg.traceCachePlotW != tw || dg.traceCachePlotH != th ||
                        dg.traceCacheChannel != dg.channel || dg.traceCacheHistory != history ||
                        dg.traceCacheMpTarget != dg.mpTarget || dg.traceCachePilot != dg.pilot ||
                        Time.unscaledTime - dg.traceLastBuildTime > 0.1f;
                    if (rebuild)
                    {
                        BuildLiveTraceTexture(dg, tw, th, history);
                    }
                        if (dg.traceTex != null)
                        {
                            GUI.color = new Color(1f, 1f, 1f, a);
                            GUI.DrawTexture(cell, dg.traceTex, ScaleMode.StretchToFill, true);
                        }
                        string axisChar = AxisLetters[Mathf.Clamp(dg.channel, 0, 3)];
                        GUI.color = new Color(0f, 0f, 0f, a);
                        GUI.Label(new Rect(cell.x + 3f, cell.y + 3f, 20f, 14f), axisChar, settingsSmallStyle);
                        GUI.color = new Color(0.863f, 0.863f, 0.863f, a);
                        GUI.Label(new Rect(cell.x + 2f, cell.y + 2f, 20f, 14f), axisChar, settingsSmallStyle);
                    if (e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                    {
                        if (Time.realtimeSinceStartup - lastPipDockClickTime < 0.35f &&
                            (e.mousePosition - lastPipDockClickPos).sqrMagnitude < 36f)
                        {
                                lastPipDockClickTime = -1f;
                                dg.rect = new Rect(e.mousePosition.x - dg.rect.width * 0.5f,
                                    e.mousePosition.y - dg.rect.height * 0.5f, dg.rect.width, dg.rect.height);
                                dg.rect = ClampGraphRect(dg.rect);
                                window.docked.RemoveAt(de3);
                                de3--;
                                window.dragCandidate = false;
                                window.dragAccumX = 0f;
                                window.dragAccumY = 0f;
                                uiFrontLayer = UI_LAYER_STICKGRAPH;
                                e.Use();
                        }
                            else
                            {
                                lastPipDockClickTime = Time.realtimeSinceStartup;
                                lastPipDockClickPos = e.mousePosition;
                            }
                        }
                    }
                }

                string pipWindowName = window.target.playerName;
                if (!string.IsNullOrEmpty(pipWindowName) && !chromeVisible)
                {
                    GUI.color = new Color(0f, 0f, 0f, a);
                    GUI.Label(new Rect(panel.x + 7f, panel.y + 5f, panel.width - 4f, 14f), pipWindowName, settingsSmallStyle);
                    GUI.color = new Color(0.863f, 0.863f, 0.863f, a);
                    GUI.Label(new Rect(panel.x + 6f, panel.y + 4f, panel.width - 4f, 14f), pipWindowName, settingsSmallStyle);
                }

                if (chromeVisible)
                {
                PipHeaderRects(panel, out Rect pilotBtn, out Rect pinBtn, out Rect plusBtn, out Rect closeBtn);
                bool pilotHover = pilotBtn.Contains(e.mousePosition);
                GUI.color = pilotHover ? new Color(1f, 1f, 1f, 0.92f * a) : ThemeButtonBg(a);
                GUI.DrawTexture(pilotBtn, whiteTex);
                GUI.color = ThemeButtonBorder(a);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.y, pilotBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.yMax - 1f, pilotBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.x, pilotBtn.y, 1f, pilotBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(pilotBtn.xMax - 1f, pilotBtn.y, 1f, pilotBtn.height), whiteTex);
                GUI.color = ThemeLabel(a);
                GUI.Label(new Rect(pilotBtn.x + 6f, pilotBtn.y, pilotBtn.width - 12f, pilotBtn.height), window.target.playerName, settingsSmallStyle);
                if (DrawStyledToggleButton(pinBtn, iconPin, window.pinned, a, true, -1))
                    window.pinned = !window.pinned;
                bool plusHover = plusBtn.Contains(e.mousePosition);
                GUI.color = plusHover ? new Color(1f, 1f, 1f, 0.92f * a) : ThemeButtonBg(a);
                GUI.DrawTexture(plusBtn, whiteTex);
                GUI.color = ThemeButtonBorder(a);
                GUI.DrawTexture(new Rect(plusBtn.x, plusBtn.y, plusBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(plusBtn.x, plusBtn.yMax - 1f, plusBtn.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(plusBtn.x, plusBtn.y, 1f, plusBtn.height), whiteTex);
                GUI.DrawTexture(new Rect(plusBtn.xMax - 1f, plusBtn.y, 1f, plusBtn.height), whiteTex);
                GUI.color = ThemeLabel(a);
                GUI.Label(plusBtn, "+", settingsPlusStyle);
                GUI.color = ThemeCloseLabel(a);
                GUI.Label(new Rect(closeBtn.x, closeBtn.y - 3f, closeBtn.width, closeBtn.height),
                    "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);

                if (!window.dragging && !window.resizing && !window.dropdownOpen)
                    window.edgeHover = GetPiPResizeEdge(panel, e.mousePosition);
                int shownEdges = window.resizing ? window.resizeEdge : window.edgeHover;
                if (shownEdges != 0)
                {
                    float edgeT = PIP_EDGE_THICK;
                    GUI.color = new Color(0.25f, 0.62f, 0.95f, 0.55f * a);
                    if ((shownEdges & PIP_EDGE_LEFT) != 0)
                        GUI.DrawTexture(new Rect(panel.x, panel.y, edgeT, panel.height), whiteTex);
                    if ((shownEdges & PIP_EDGE_RIGHT) != 0)
                        GUI.DrawTexture(new Rect(panel.xMax - edgeT, panel.y, edgeT, panel.height), whiteTex);
                    if ((shownEdges & PIP_EDGE_TOP) != 0)
                    {
                        float leftTrim = (shownEdges & PIP_EDGE_LEFT) != 0 ? edgeT : 0f;
                        float rightTrim = (shownEdges & PIP_EDGE_RIGHT) != 0 ? edgeT : 0f;
                        GUI.DrawTexture(new Rect(panel.x + leftTrim, panel.y, panel.width - leftTrim - rightTrim, edgeT), whiteTex);
                    }
                    if ((shownEdges & PIP_EDGE_BOTTOM) != 0)
                    {
                        float leftTrim = (shownEdges & PIP_EDGE_LEFT) != 0 ? edgeT : 0f;
                        float rightTrim = (shownEdges & PIP_EDGE_RIGHT) != 0 ? edgeT : 0f;
                        GUI.DrawTexture(new Rect(panel.x + leftTrim, panel.yMax - edgeT, panel.width - leftTrim - rightTrim, edgeT), whiteTex);
                    }
                }
                }
            }
            }

            if (!pipExternalMonitor)
            {
                for (int i = multiplayerPipWindows.Count - 1; i >= 0; i--)
                {
                    MultiplayerPipWindow window = multiplayerPipWindows[i];
                    if (window == null || !window.dropdownOpen || window.target == null)
                        continue;
                    Rect dd = GetMultiplayerPipDropdownRect(window);
                    GUI.color = ThemePanelBg(a);
                    GUI.DrawTexture(dd, whiteTex);
                    GUI.color = ThemeOptionBorder(a);
                    GUI.DrawTexture(new Rect(dd.x, dd.y, dd.width, 1f), whiteTex);
                    int optCount = multiplayerPipTargets.Count;
                    if (optCount == 0)
                    {
                        GUI.color = ThemeDimLabel(a);
                        GUI.Label(new Rect(dd.x + 8f, dd.y, dd.width - 16f, 20f), "No pilots", settingsSmallStyle);
                    }
                    for (int pi = 0; pi < optCount; pi++)
                    {
                        MultiplayerPipTarget opt = multiplayerPipTargets[pi];
                        if (opt == null)
                            continue;
                        Rect optRect = new Rect(dd.x, dd.y + 20f * pi, dd.width, 20f);
                        bool optHover = optRect.Contains(e.mousePosition);
                        bool optSelected = opt == window.target;
                        GUI.color = optSelected
                            ? new Color(0.25f, 0.62f, 0.95f, a)
                            : optHover ? new Color(1f, 1f, 1f, 0.92f * a) : ThemeOptionBg(a);
                        GUI.DrawTexture(optRect, whiteTex);
                        GUI.color = optSelected ? new Color(1f, 1f, 1f, a) : ThemeOptionText(a);
                        GUI.Label(optRect, opt.playerName, settingsSmallStyle);
                    }
                }
            }
            // Drone picker floats on top of everything so opening it never resizes
            // the settings panel.
            if (droneDropdownPending)
                DrawDroneDropdownOverlay(e);
            GUI.color = old;
        }
    }
}
