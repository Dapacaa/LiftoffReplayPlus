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
        private int ReadRoomGameMode()
        {
            try
            {
                var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                if (room != null && room.CustomProperties != null && room.CustomProperties.ContainsKey("GM"))
                    return System.Convert.ToInt32(room.CustomProperties["GM"]);
            }
            catch { }
            return -1;
        }

        private void ToggleLobbySelection(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            int idx = selectedLobbies.IndexOf(name);
            if (idx >= 0)
                selectedLobbies.RemoveAt(idx);
            else
                selectedLobbies.Add(name);
            lobbyBrowserRowsAt = -999f;
            lobbyRowTextCache.Clear();
        }

        private void OnSelectedSlotOpened(LobbyWatchRoom room)
        {
            if (room == null || string.IsNullOrEmpty(room.Name) || !selectedLobbies.Contains(room.Name))
                return;
            float cooldownUntil;
            if (lobbyAlertCooldown.TryGetValue(room.Name, out cooldownUntil) && Time.unscaledTime < cooldownUntil)
                return;
            lobbyAlertCooldown[room.Name] = Time.unscaledTime + 60f;
            ProbeLog("free slot in selected room '" + room.Name + "' (" + room.PlayerCount + "/" + room.MaxPlayers + ")");
            bool autoJoin = Prefs.autoJoinLobbyConfig != null && Prefs.autoJoinLobbyConfig.Value;
            bool alertOn = Prefs.autoAlertLobbyConfig == null || Prefs.autoAlertLobbyConfig.Value;
            // Auto-join only arms where it cannot surprise: mid-flight, or with the browser open.
            // Plain menu browsing just gets the alert prompt.
            bool armed = Photon.Pun.PhotonNetwork.InRoom || lobbyBrowserOpen;
            if (autoJoin && armed)
            {
                if (Time.unscaledTime < lobbyJoinBusyUntil)
                {
                    ProbeLog("join already in progress, skipping auto-join for '" + room.Name + "'");
                    return;
                }
                // Priority: the selected room closest to the top of the list wins.
                LobbyWatchRoom target = room;
                for (int i = 0; i < selectedLobbies.Count; i++)
                {
                    LobbyWatchRoom w;
                    if (!lobbyRoomStates.TryGetValue(selectedLobbies[i], out w) || w == null)
                        continue;
                    if (w.Listed && w.IsOpen && w.PlayerCount < w.MaxPlayers)
                    {
                        target = w;
                        break;
                    }
                }
                int prio = selectedLobbies.IndexOf(target.Name) + 1;
                ProbeLog("auto-joining '" + target.Name + "' (priority #" + prio + ", no prompt)");
                lobbyJoinBusyUntil = Time.unscaledTime + 10f;
                JoinLobbyRoom(target.Name);
                return;
            }
            if (alertOn)
            {
                lobbyAlertRoom = room.Name;
                lobbyAlertCount = room.PlayerCount + "/" + room.MaxPlayers;
                lobbyAlertUntil = Time.unscaledTime + 30f;
            }
        }

        private void JoinLobbyRoom(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            lobbyJoinBusyUntil = Time.unscaledTime + 10f;
            try
            {
                if (Photon.Pun.PhotonNetwork.InRoom)
                {
                    if (Photon.Pun.PhotonNetwork.CurrentRoom != null && Photon.Pun.PhotonNetwork.CurrentRoom.Name == name)
                        return;
                    lobbySwitchActive = true;
                    pendingLobbyJoin = name;
                    pendingLobbyJoinAt = Time.unscaledTime;
                    Photon.Pun.PhotonNetwork.LeaveRoom(false);
                    // Reset the game's session state by returning to the menu; use an async load so
                    // Photon's leave/reconnect handshake can proceed while the menu loads.
                    ProbeLog("leaving current room to switch to '" + name + "' (menu reset + async rejoin)");
                    SceneManager.LoadSceneAsync("MultiplayerMenu");
                }
                else
                {
                    lobbySwitchActive = true;
                    if (!StartGameJoin(name))
                        lobbySwitchActive = false;
                }
                lobbyBrowserOpen = false;
                lobbyAlertUntil = 0f;
            }
            catch (Exception ex)
            {
                lobbySwitchActive = false;
                ProbeLog("join room error: " + ex.Message);
            }
        }

        private bool StartGameJoin(string name)
        {
            // Prefer the game's own join entry (learned from a normal browser join).
            if (gameJoinMethod != null)
            {
                try
                {
                    object instance = null;
                    var dt = gameJoinMethod.DeclaringType;
                    if (!gameJoinMethod.IsStatic && dt != null && typeof(UnityEngine.Object).IsAssignableFrom(dt))
                        instance = UnityEngine.Object.FindObjectOfType(dt);
                    if (gameJoinMethod.IsStatic || instance != null)
                    {
                        ProbeLog("joining '" + name + "' via game join entry (" + gameJoinTypeName + ")");
                        gameJoinMethod.Invoke(instance, new object[] { name });
                        return true;
                    }
                    ProbeLog("game join entry instance not found (" + gameJoinTypeName + ")");
                }
                catch (Exception ex)
                {
                    ProbeLog("game join error: " + ex.Message);
                }
            }
            try
            {
                ProbeLog("falling back to direct JoinRoom for '" + name + "'");
                Photon.Pun.PhotonNetwork.JoinRoom(name);
                return true;
            }
            catch (Exception ex)
            {
                ProbeLog("direct join error: " + ex.Message);
                return false;
            }
        }

        private void TickLobbyJoinPending()
        {
            if (pendingLobbyJoin == null)
                return;
            if (Time.unscaledTime - pendingLobbyJoinAt > 25f)
            {
                ProbeLog("switch to '" + pendingLobbyJoin + "' timed out, aborting");
                pluginLog.LogInfo("[GhostLines] leaderboards: rejoin of '" + pendingLobbyJoin + "' timed out" +
                    (pendingLobbyReturn != null ? " — return watchdog will land in menu" : ""));
                pendingLobbyJoin = null;
                lobbySwitchActive = false;
                if (pendingLobbyReturn != null &&
                    SceneManager.GetActiveScene().name != "MultiplayerMenu")
                {
                    try { SceneManager.LoadScene("MultiplayerMenu"); } catch { }
                }
                return;
            }
            if (Photon.Pun.PhotonNetwork.InRoom)
                return;
            // Wait for the menu scene (loaded immediately at switch start) and the master server.
            // The game's join entry only needs the master connection, no lobby membership.
            if (SceneManager.GetActiveScene().name != "MultiplayerMenu")
                return;
            if (Photon.Pun.PhotonNetwork.NetworkClientState != Photon.Realtime.ClientState.ConnectedToMasterServer)
                return;
            string name = pendingLobbyJoin;
            pendingLobbyJoin = null;
            if (!StartGameJoin(name))
                lobbySwitchActive = false;
        }

        // ---- Phase 0 race probe: locate gate/checkpoint data (logging only) ----
        private bool raceProbeTypesLogged = false;
        private readonly List<System.Type> raceProbeTypes = new List<System.Type>();
        private readonly Dictionary<System.Type, int> raceProbeLastCounts = new Dictionary<System.Type, int>();
        private readonly HashSet<System.Type> raceProbeDumpedTypes = new HashSet<System.Type>();
        private string raceProbeLastScene = null;
        private float raceProbeNextAt = 0f;
        private float raceKeyProbeNextAt = 0f;
        private RaceEventProbe raceEventProbe = null;
        private bool raceEventProbeHooked = false;
        private readonly Dictionary<byte, int> raceEventCounts = new Dictionary<byte, int>();
        private readonly Dictionary<byte, int> raceEventLogCount = new Dictionary<byte, int>();
        private float raceEventSummaryAt = 0f;
    }
}
