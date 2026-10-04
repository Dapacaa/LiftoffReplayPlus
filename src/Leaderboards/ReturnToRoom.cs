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
        // Liftoff rooms are always "Liftoff-XXXX" (public and private alike).
        // Photon transit states can briefly report garbage names — never trust those.
        private bool IsPlausibleRoomName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Length >= 9 && name.StartsWith("Liftoff-");
        }

        // Remembers the live multiplayer Level while flying so we can return to it
        // without leaving the room.
        private float lbLiveLevelLogAt = 0f;
        private void CaptureLiveMpLevel()
        {
            if (replayReady)
                return;
            if (Time.unscaledTime < lbLiveLevelCaptureAt)
                return;
            lbLiveLevelCaptureAt = Time.unscaledTime + 0.5f;
            try
            {
                CurrentContentContainer ccc = CurrentContentContainer.use;
                if (ccc == null)
                    return;
                // Ground truth: never treat a replay level as the live level.
                try { if (ccc.InReplay) return; } catch { }
                // Only capture a fully-initialised multiplayer level, so a transient
                // loading Level (or an unrelated solo/test level) can't become the
                // direct-return target. The room's live Level is a fresh instance
                // after every scene load, so keep the reference current instead of
                // holding the first one forever — a stale Level's asset references
                // can be released while the replay runs, making the direct reload
                // silently no-op the longer you stay in the replay.
                try { if (!ccc.InMultiplayer) return; } catch { }
                try { if (ccc.IsUpdatingLevel) return; } catch { }
                Level lvl = ccc.Level;
                if (lvl != null)
                {
                    bool changed = !object.ReferenceEquals(lbLiveLevel, lvl);
                    lbLiveLevel = lvl;
                    if (changed)
                        pluginLog.LogInfo("[GhostLines] leaderboards: captured live mp level (env='" +
                            (lvl.Environment != null ? lvl.Environment.name : "null") + "')");
                }
                DroneConfiguration drone = ccc.DroneConfig;
                if (drone != null)
                    lbLiveDrone = drone;
            }
            catch (System.Exception ex)
            {
                if (Time.unscaledTime >= lbLiveLevelLogAt)
                {
                    lbLiveLevelLogAt = Time.unscaledTime + 5f;
                    pluginLog.LogInfo("[GhostLines] leaderboards: live level capture EX " + ex.Message);
                }
            }
        }

        // Compares a level with the room's current content (Photon room
        // props: CN = track or race name, E = environment). Unknown/missing props
        // count as a match so the direct return stays the default. requireNames:
        // a level with no track/race names doesn't match (a rebuilt level whose
        // workshop content isn't available locally).
        private bool LevelMatchesRoom(Level lvl, string what, bool requireNames)
        {
            try
            {
                var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                if (lvl == null || room == null || room.CustomProperties == null)
                    return true;
                string roomEnv = room.CustomProperties.ContainsKey("E") ? room.CustomProperties["E"] as string : null;
                string roomContent = room.CustomProperties.ContainsKey("CN") ? room.CustomProperties["CN"] as string : null;
                string lvlEnv = lvl.Environment != null ? lvl.Environment.name : null;
                string lvlTrack = lvl.trackInfo != null ? lvl.trackInfo.Name : null;
                string lvlRace = lvl.raceInfo != null ? lvl.raceInfo.Name : null;
                bool envMatches = string.IsNullOrEmpty(roomEnv) || string.IsNullOrEmpty(lvlEnv) ||
                    string.Equals(roomEnv.Trim(), lvlEnv.Trim(), System.StringComparison.OrdinalIgnoreCase);
                bool contentMatches = string.IsNullOrEmpty(roomContent) ||
                    (!requireNames && string.IsNullOrEmpty(lvlTrack) && string.IsNullOrEmpty(lvlRace)) ||
                    SameContentName(roomContent, lvlTrack) || SameContentName(roomContent, lvlRace);
                if (envMatches && contentMatches)
                    return true;
                pluginLog.LogInfo("[GhostLines] leaderboards: " + what + " level differs from the room (room CN='" + roomContent +
                    "' E='" + roomEnv + "', level track='" + lvlTrack + "' race='" + lvlRace + "' env='" + lvlEnv + "')");
                return false;
            }
            catch
            {
                return true;
            }
        }

        // Builds a fresh Level for the room's current track the way the game does
        // when the host changes track: the multiplayer game-mode settings base
        // class has a static factory that picks the current mode from the shared
        // session settings, and an abstract CreateLevel(). Both read static session
        // state, so this works from the replay scene where the MP manager is gone.
        // Names are obfuscated, so the class is found by shape: abstract, has an
        // abstract GameMode property, a static parameterless factory returning
        // itself, and an abstract parameterless method returning a Level subclass.
        private static System.Reflection.MethodInfo mpModeFactory;
        private static System.Reflection.MethodInfo mpModeCreateLevel;
        private static bool mpModeResolved;

        private static bool ResolveMpModeLevelBuilder()
        {
            if (mpModeResolved)
                return mpModeFactory != null && mpModeCreateLevel != null;
            mpModeResolved = true;
            const System.Reflection.BindingFlags pubInst =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            const System.Reflection.BindingFlags pubStatic =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (System.Type t in GameAccess.GameTypes())
            {
                if (t == null || !t.IsAbstract || !t.IsClass || t.IsSealed || typeof(UnityEngine.Object).IsAssignableFrom(t))
                    continue;
                bool hasModeProp = false;
                foreach (var prop in t.GetProperties(pubInst))
                {
                    if (prop.PropertyType == typeof(Liftoff.Multiplayer.GameMode))
                    {
                        hasModeProp = true;
                        break;
                    }
                }
                if (!hasModeProp)
                    continue;
                System.Reflection.MethodInfo factory = null, createLevel = null;
                foreach (var m in t.GetMethods(pubStatic))
                    if (m.ReturnType == t && m.GetParameters().Length == 0)
                        factory = m;
                foreach (var m in t.GetMethods(pubInst))
                    if (m.IsAbstract && typeof(Level).IsAssignableFrom(m.ReturnType) && m.GetParameters().Length == 0)
                        createLevel = m;
                if (factory != null && createLevel != null)
                {
                    mpModeFactory = factory;
                    mpModeCreateLevel = createLevel;
                    return true;
                }
            }
            pluginLog.LogInfo("[GhostLines] leaderboards: MP level builder not found");
            return false;
        }

        private Level BuildRoomLevel()
        {
            if (!ResolveMpModeLevelBuilder())
                return null;
            try
            {
                object mode = mpModeFactory.Invoke(null, null);
                return mode != null ? mpModeCreateLevel.Invoke(mode, null) as Level : null;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: building the room level failed: " +
                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
        }

        private static bool SameContentName(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                string.Equals(a.Trim(), b.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }

        // Reloads the captured live level via the game's SceneLoader, staying in
        // the room. Returns false if the machinery isn't available (caller then
        // falls back to leave+rejoin).
        private System.Type lbLevelRequestType = null;
        private System.Reflection.ConstructorInfo lbLevelRequestCtor = null;
        private System.Reflection.MethodInfo lbSceneLoadMethod = null;

        private bool TryDirectReturnToMpLevel(Level level)
        {
            if (level == null)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: direct return skipped — no captured live level");
                return false;
            }
            try
            {
                if (lbSceneLoadMethod == null)
                    lbSceneLoadMethod = AccessTools.Method(typeof(SceneLoader), "Load");
                if (lbSceneLoadMethod == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: direct return skipped — SceneLoader.Load not found");
                    return false;
                }
                if (lbLevelRequestCtor == null)
                {
                    // The Load parameter is an interface; the concrete request type
                    // is a public class implementing it with a (Level) ctor. Find it
                    // by shape so a re-obfuscation can't break the name match.
                    System.Type reqIface = lbSceneLoadMethod.GetParameters()[0].ParameterType;
                    foreach (System.Type t in GameAccess.TypesOf(typeof(SceneLoader).Assembly))
                    {
                        if (t == null || t.IsInterface || t.IsAbstract || !reqIface.IsAssignableFrom(t))
                            continue;
                        var ctor = t.GetConstructor(new System.Type[] { typeof(Level) });
                        if (ctor != null)
                        {
                            lbLevelRequestType = t;
                            lbLevelRequestCtor = ctor;
                            break;
                        }
                    }
                }
                if (lbLevelRequestCtor == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: direct return skipped — no Level request type implementing " +
                        lbSceneLoadMethod.GetParameters()[0].ParameterType.Name);
                    return false;
                }
                object req = lbLevelRequestCtor.Invoke(new object[] { level });
                SceneLoader sl = SceneLoader.use;
                if (sl == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: direct return skipped — SceneLoader singleton null");
                    return false;
                }
                string lvlInfo = "?";
                try
                {
                    Level lv = level;
                    if (lv != null)
                        lvlInfo = "flags=" + lv.LevelFlags + " env='" +
                            (lv.Environment != null ? lv.Environment.name : "null") + "'";
                }
                catch { }
                lbDirectLastInvokeAt = Time.unscaledTime;
                lbDirectSceneAt = SceneManager.GetActiveScene().name;
                lbDirectSawLoading = false;
                if (ModLog.DebugEnabled) ModLog.LogFile("[RET] direct invoke scene='" + lbDirectSceneAt + "' level{" + lvlInfo + "}");
                lbSceneLoadMethod.Invoke(sl, new object[] { req });
                pluginLog.LogInfo("[GhostLines] leaderboards: direct level reload via SceneLoader (staying in room) type=" +
                    lbLevelRequestType.Name);
                if (ModLog.DebugEnabled) ModLog.LogFile("[RET] direct invoke returned, awaiting game load");
                lbAwaitCursorRestore = true;
                // The reloaded flight scene can add a second InGameUI; run the
                // dedup scan often for a while until the new scene settles.
                chatDedupUrgentUntil = Time.unscaledTime + 20f;
                nextChatDedupAt = 0f;
                return true;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: direct level reload EX " + ex.Message);
                return false;
            }
        }

        // During an in-room replay the game's return-to-lobby manager is gone (its
        // callback NREs and the confirm popup is dead), raw LoadScene can't load
        // workshop tracks (AssetBundle-only) and re-applies the Replay flag, the
        // room-update handler is a silent no-op (manager singleton missing in the
        // replay scene), and ReconnectAndRejoin refuses while still connected. So
        // return via leave + same-room rejoin through the proven room-switch
        // machinery: the game's own join entry reloads the flight scene from room
        // props (bundles handled, clean MP flags). Public rooms always survive;
        // solo private rooms survive when the room has rejoin TTL, otherwise the
        // watchdog below falls back to the multiplayer menu.
        private void TryReturnToMpLobby()
        {
            lbReturnDirectTried = false;
            lbDirectSawLoading = false;
            lbDirectLastInvokeAt = 0f;
            lbDirectSceneAt = null;
            string sceneAtEntry = null;
            try { sceneAtEntry = SceneManager.GetActiveScene().name; } catch { }
            if (ModLog.DebugEnabled) ModLog.LogFile("[RET] TryReturnToMpLobby enter scene='" + sceneAtEntry + "' replayReady=" + replayReady +
                " liveLevel=" + (lbLiveLevel != null));
            try
            {
                if (replayReady)
                    DeactivateReplay();
            }
            catch { }
            string roomName = null;
            bool inRoom = false;
            try
            {
                if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.CurrentRoom != null)
                {
                    var cr = Photon.Pun.PhotonNetwork.CurrentRoom;
                    if (IsPlausibleRoomName(cr.Name))
                    {
                        inRoom = true;
                        roomName = cr.Name;
                    }
                    else if (!string.IsNullOrEmpty(cr.Name))
                        pluginLog.LogInfo("[GhostLines] leaderboards: ignoring implausible live room name '" + cr.Name + "'");
                }
            }
            catch { }
            if (string.IsNullOrEmpty(roomName))
                roomName = lastMpRoomName;
            if (!IsPlausibleRoomName(roomName))
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: no valid room (live=" +
                    (inRoom ? roomName : "none") + " cached=" + (lastMpRoomName ?? "null") + ") — loading multiplayer menu");
                UnityEngine.SceneManagement.SceneManager.LoadScene("MultiplayerMenu");
                return;
            }
            pendingLobbyReturn = roomName;
            pendingLobbyReturnAt = Time.unscaledTime;
            lobbySwitchActive = true;
            // Preferred: reload a level in place, staying in the Photon room (the
            // player keeps their slot; rooms fill up). Normally that's the captured
            // live level; if the host changed track during the replay, it's a fresh
            // level built for the room's current track. Only when neither fits the
            // room does it fall back to leave + rejoin.
            Level returnLevel = lbLiveLevel as Level;
            if (returnLevel == null || !LevelMatchesRoom(returnLevel, "captured", false))
            {
                Level built = BuildRoomLevel();
                if (built != null && LevelMatchesRoom(built, "rebuilt", true))
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: room changed track during replay — loading its current level in place");
                    returnLevel = built;
                }
                else
                    returnLevel = null;
            }
            if (returnLevel != null && TryDirectReturnToMpLevel(returnLevel))
            {
                lbReturnDirectTried = true;
                return;
            }
            lbReturnDirectTried = false;
            if (inRoom)
            {
                // Same-room rejoin: JoinLobbyRoom early-returns when already in the
                // target room, so drive the leave branch directly.
                pluginLog.LogInfo("[GhostLines] leaderboards: leaving replay room '" + roomName + "' for same-room rejoin");
                try
                {
                    pendingLobbyJoin = roomName;
                    pendingLobbyJoinAt = Time.unscaledTime;
                    Photon.Pun.PhotonNetwork.LeaveRoom(false);
                    pluginLog.LogInfo("[GhostLines] leaderboards: left room, loading menu for rejoin");
                    SceneManager.LoadSceneAsync("MultiplayerMenu");
                }
                catch (System.Exception ex)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: same-room rejoin EX " + ex.Message);
                    pendingLobbyJoin = null;
                    pendingLobbyReturn = null;
                    lobbySwitchActive = false;
                    try { UnityEngine.SceneManagement.SceneManager.LoadScene("MultiplayerMenu"); } catch { }
                }
                return;
            }
            pluginLog.LogInfo("[GhostLines] leaderboards: not in room — rejoining " + roomName);
            JoinLobbyRoom(roomName);
        }

        // Watchdog for the lobby return: success is the game's next level setup
        // (cleared in OnUnityLogMessage); timeout falls back to the menu so a
        // dead room (e.g. solo private without rejoin TTL) can't strand us.
        private void TickLobbyReturnPending()
        {
            if (pendingLobbyReturn == null)
                return;
            float elapsed = Time.unscaledTime - pendingLobbyReturnAt;
            string sceneNow = null;
            try { sceneNow = SceneManager.GetActiveScene().name; } catch { }
            if (lbReturnDirectTried)
            {
                // Success is registered by the game's own "Level setup:" log
                // (OnUnityLogMessage clears pendingLobbyReturn), so this watchdog only
                // decides when to give up. Never interrupt a load that is actually
                // running: the old fixed 6s timeout fired during slow content/workshop
                // loads and forced a needless leave+rejoin. Re-invoking the load is
                // also wrong — the game is mid-reload and a second request (plus the
                // leave+rejoin) leaves it stuck on the loading screen.
                bool loadingNow = sceneNow == "LoadingScreen";
                if (loadingNow)
                {
                    lbDirectSawLoading = true;
                    if (elapsed > 60f)
                    {
                        if (ModLog.DebugEnabled) ModLog.LogFile("[RET] direct load still on LoadingScreen after " +
                            elapsed.ToString("0.0") + "s — falling back");
                        lbReturnDirectTried = false;
                        string roomSlow = pendingLobbyReturn;
                        pendingLobbyReturn = null;
                        lobbySwitchActive = false;
                        TryReturnToMpLobbyFallback(roomSlow);
                    }
                    return;
                }
                if (lbDirectSawLoading)
                {
                    // A load we saw start has left the loading screen; the level is
                    // initialising. Hand success over to the "Level setup" handler.
                    ModLog.LogFile("[RET] game left LoadingScreen after " + elapsed.ToString("0.0") +
                        "s scene='" + sceneNow + "' — treating direct return as successful");
                    pendingLobbyReturn = null;
                    lobbySwitchActive = false;
                    return;
                }
                // Never saw a load: the request was swallowed (e.g. unavailable level
                // request). Fall back promptly.
                if (elapsed > 8f)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[RET] direct return never started a load for '" + pendingLobbyReturn +
                        "' scene='" + sceneNow + "' elapsed=" + elapsed.ToString("0.0") +
                        "s replayReady=" + replayReady + " — falling back to leave + rejoin");
                    lbReturnDirectTried = false;
                    string room = pendingLobbyReturn;
                    pendingLobbyReturn = null;
                    lobbySwitchActive = false;
                    TryReturnToMpLobbyFallback(room);
                    return;
                }
            }
            if (elapsed > 40f)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[RET] return to '" + pendingLobbyReturn + "' timed out (scene='" + sceneNow +
                    "') — loading multiplayer menu");
                pendingLobbyReturn = null;
                lobbySwitchActive = false;
                try { UnityEngine.SceneManagement.SceneManager.LoadScene("MultiplayerMenu"); } catch { }
            }
        }

        // Leave + same-room rejoin path (the original return mechanism), used when
        // the direct in-room reload isn't available or fails.
        private void TryReturnToMpLobbyFallback(string roomName)
        {
            if (ModLog.DebugEnabled) ModLog.LogFile("[RET] fallback leave+rejoin for room='" + roomName + "'");
            chatDedupUrgentUntil = Time.unscaledTime + 20f;
            nextChatDedupAt = 0f;
            if (!IsPlausibleRoomName(roomName))
            {
                try { UnityEngine.SceneManagement.SceneManager.LoadScene("MultiplayerMenu"); } catch { }
                return;
            }
            bool inRoom = false;
            try { inRoom = Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.CurrentRoom != null; } catch { }
            pendingLobbyReturn = roomName;
            pendingLobbyReturnAt = Time.unscaledTime;
            lobbySwitchActive = true;
            if (inRoom)
            {
                try
                {
                    pendingLobbyJoin = roomName;
                    pendingLobbyJoinAt = Time.unscaledTime;
                    Photon.Pun.PhotonNetwork.LeaveRoom(false);
                    SceneManager.LoadSceneAsync("MultiplayerMenu");
                }
                catch
                {
                    pendingLobbyJoin = null;
                    pendingLobbyReturn = null;
                    lobbySwitchActive = false;
                    try { UnityEngine.SceneManagement.SceneManager.LoadScene("MultiplayerMenu"); } catch { }
                }
                return;
            }
            JoinLobbyRoom(roomName);
        }
    }
}
