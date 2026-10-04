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
        private void HookUnityLog()
        {
            if (wsLogHooked)
                return;
            wsLogHooked = true;
            Application.logMessageReceived += OnUnityLogMessage;
        }

        private void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (string.IsNullOrEmpty(condition))
                    return;
                if (lobbySwitchActive && condition.IndexOf("Level setup:", StringComparison.OrdinalIgnoreCase) >= 0)
                    lobbySwitchActive = false;
                if (pendingLobbyReturn != null && condition.IndexOf("Level setup:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: returned to session (room " + pendingLobbyReturn + ")");
                    pendingLobbyReturn = null;
                }
                if (condition.IndexOf("Level setup:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (var rawLine in condition.Split('\n'))
                    {
                        string line = rawLine.TrimEnd('\r').Trim();
                        if (!string.IsNullOrEmpty(line))
                            ParseLevelSetupLine(line);
                    }
                    FlushPendingWsItem();
                    return;
                }
                if (!wsLogCaptureActive)
                    return;
                if (condition.StartsWith("Flags:", StringComparison.OrdinalIgnoreCase) ||
                    condition.StartsWith("Environment:", StringComparison.OrdinalIgnoreCase) ||
                    condition.StartsWith("Type:", StringComparison.OrdinalIgnoreCase) ||
                    condition.StartsWith("Name:", StringComparison.OrdinalIgnoreCase) ||
                    condition.StartsWith("Status:", StringComparison.OrdinalIgnoreCase) ||
                    condition.StartsWith("Local ID:", StringComparison.OrdinalIgnoreCase) ||
                    condition.StartsWith("Managed ID:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseLevelSetupLine(condition);
                    return;
                }
                FlushPendingWsItem();
                wsLogCaptureActive = false;
            }
            catch { }
        }

        private void FlushPendingWsItem()
        {
            if (wsPendingItemKind == 0 || string.IsNullOrEmpty(wsPendingItemName))
            {
                wsPendingItemKind = 0;
                wsPendingItemName = null;
                return;
            }
            if (wsPendingItemKind == 1)
            {
                wsLogCaptured = true;
                if (mpCurrentTrackName != wsPendingItemName)
                {
                    pluginLog.LogInfo("[GhostLines][WS] level-setup track (internal): '" + wsPendingItemName + "'");
                    mpCurrentTrackName = wsPendingItemName;
                }
                wsTrackItemId = null;
            }
            else if (wsPendingItemKind == 2)
            {
                wsLogCaptured = true;
                if (mpCurrentRaceName != wsPendingItemName)
                {
                    pluginLog.LogInfo("[GhostLines][WS] level-setup race (internal): '" + wsPendingItemName + "'");
                    mpCurrentRaceName = wsPendingItemName;
                }
                wsRaceItemId = null;
            }
            wsOfficialContent = true;
            wsPendingItemKind = 0;
            wsPendingItemName = null;
            wsPendingItemStatusOfficial = false;
        }

        private void ParseLevelSetupLine(string line)
        {
            if (line.StartsWith("Environment:", StringComparison.OrdinalIgnoreCase))
            {
                wsEnvironmentName = line.Substring(12).Trim();
                return;
            }
            if (line.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
            {
                FlushPendingWsItem();
                string val = line.Substring(5).Trim();
                if (val.IndexOf("Track", StringComparison.OrdinalIgnoreCase) >= 0)
                    wsPendingItemKind = 1;
                else if (val.IndexOf("Race", StringComparison.OrdinalIgnoreCase) >= 0)
                    wsPendingItemKind = 2;
                else
                    wsPendingItemKind = 0;
                wsPendingItemName = null;
                return;
            }
            if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
            {
                wsPendingItemName = line.Substring(5).Trim();
                return;
            }
            if (line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            {
                wsPendingItemStatusOfficial = line.IndexOf("official", StringComparison.OrdinalIgnoreCase) >= 0;
                return;
            }
            if (line.StartsWith("Managed ID:", StringComparison.OrdinalIgnoreCase))
            {
                string idVal = line.Substring(11).Trim();
                bool idValid = !string.IsNullOrEmpty(idVal) && idVal != "0";
                if (wsPendingItemKind == 1 && !string.IsNullOrEmpty(wsPendingItemName))
                {
                    wsLogCaptured = true;
                    if (mpCurrentTrackName != wsPendingItemName)
                    {
                        pluginLog.LogInfo("[GhostLines][WS] level-setup track: '" + wsPendingItemName + "' id=" + idVal);
                        mpCurrentTrackName = wsPendingItemName;
                    }
                    if (idValid && !wsPendingItemStatusOfficial)
                    {
                        wsTrackItemId = idVal;
                        wsOfficialContent = false;
                    }
                    else
                        wsOfficialContent = true;
                    if (!wsOfficialContent)
                        TriggerWsAutoDownload();
                }
                else if (wsPendingItemKind == 2 && !string.IsNullOrEmpty(wsPendingItemName))
                {
                    wsLogCaptured = true;
                    if (mpCurrentRaceName != wsPendingItemName)
                    {
                        pluginLog.LogInfo("[GhostLines][WS] level-setup race: '" + wsPendingItemName + "' id=" + idVal);
                        mpCurrentRaceName = wsPendingItemName;
                    }
                    if (idValid && !wsPendingItemStatusOfficial)
                    {
                        wsRaceItemId = idVal;
                        wsOfficialContent = false;
                    }
                    else
                        wsOfficialContent = true;
                    if (!wsOfficialContent)
                        TriggerWsAutoDownload();
                }
                wsPendingItemKind = 0;
                wsPendingItemName = null;
                wsPendingItemStatusOfficial = false;
            }
        }

        private void TickWorkshopDownload()
        {
            PollWorkshopTrackName();
            if (wsQueryAdvancePending)
            {
                wsQueryAdvancePending = false;
                QueryNextPendingWsName();
            }
            if (wsQueryBusy && wsQueryStartedAt >= 0f && Time.unscaledTime - wsQueryStartedAt > 25f)
            {
                pluginLog.LogInfo("[GhostLines][WS] query watchdog: resetting stuck search after 25s");
                wsQueryBusy = false;
                wsQueryStartedAt = -1f;
                wsCurrentTargetIndex = 0;
                wsPendingNames.Clear();
                wsPendingTargetName = null;
                wsStatus = "Workshop search timed out";
            }
            if (wsDownloadWatchIds.Count == 0)
                return;
            if (Time.unscaledTime < wsStatePollAt)
                return;
            wsStatePollAt = Time.unscaledTime + 0.5f;
            try
            {
                wsCompletedIds.Clear();
                foreach (ulong id in wsDownloadWatchIds)
                {
                    uint state = Steamworks.SteamUGC.GetItemState(new Steamworks.PublishedFileId_t(id));
                    if ((state & 4u) != 0 && (state & 16u) == 0)
                        wsCompletedIds.Add(id);
                }
                foreach (ulong id in wsCompletedIds)
                {
                    wsDownloadWatchIds.Remove(id);
                    if (wsDownloadingId == id)
                    {
                        wsStatus = null;
                        wsDownloadingId = 0;
                        wsDownloadingName = null;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] workshop state error: " + ex.Message);
            }
        }

        // Typed read of the live level's content (CurrentContentContainer is a cached
        // game singleton). Workshop items carry a Steam managed id; official content
        // has none. Replaces the old scene/ScriptableObject member scanners.
        private void ReadCurrentContent()
        {
            try
            {
                CurrentContentContainer ccc = CurrentContentContainer.use;
                if (ccc == null || ccc.Level == null)
                    return;
                // Only the settled live MP level: while a replay level is loaded or
                // a level switch is in progress this holds the wrong track.
                try
                {
                    if (ccc.IsUpdatingLevel || ccc.InReplay || !ccc.InMultiplayer)
                        return;
                }
                catch { return; }
                Track track = null;
                Race race = null;
                try { track = ccc.Track; } catch { }
                try { race = ccc.Race; } catch { }
                if (track != null)
                {
                    string n = track.Name;
                    string idStr = ManagedIdString(track);
                    if (!string.IsNullOrEmpty(n))
                    {
                        if (n != mpCurrentTrackName)
                        {
                            pluginLog.LogInfo("[GhostLines][WS] content track: '" + n + "' id=" + (idStr ?? "null"));
                            mpCurrentTrackName = n;
                            if (!wsOfficialContent)
                                TriggerWsAutoDownload();
                        }
                        if (idStr != null)
                            wsTrackItemId = idStr;
                        else
                            wsOfficialContent = true;
                    }
                }
                else
                {
                    string officialName = null;
                    try
                    {
                        global::Environment env = ccc.Environment;
                        officialName = env != null ? env.DisplayName : null;
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(officialName) && officialName != mpCurrentTrackName)
                    {
                        pluginLog.LogInfo("[GhostLines][WS] official content: '" + officialName + "'");
                        mpCurrentTrackName = officialName;
                        wsOfficialContent = true;
                        wsTrackItemId = null;
                        wsRaceItemId = null;
                    }
                }
                if (race != null)
                {
                    string n = race.Name;
                    string idStr = ManagedIdString(race);
                    if (!string.IsNullOrEmpty(n))
                    {
                        if (n != mpCurrentRaceName)
                        {
                            pluginLog.LogInfo("[GhostLines][WS] content race: '" + n + "' id=" + (idStr ?? "null"));
                            mpCurrentRaceName = n;
                            if (!wsOfficialContent)
                                TriggerWsAutoDownload();
                        }
                        if (idStr != null)
                            wsRaceItemId = idStr;
                        else
                            wsOfficialContent = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] content read error: " + ex.Message);
            }
        }

        // The Steam workshop id ("Managed ID" in the level-setup log), or null for
        // official/local content.
        private static string ManagedIdString(ShareableContent content)
        {
            ContentId id = null;
            try { id = content.ManagedID; } catch { }
            string str = id != null ? id.str : null;
            return !string.IsNullOrEmpty(str) && str != "0" ? str : null;
        }

        private void PollWorkshopTrackName()
        {
            if (replayReady || !mpInSession)
            {
                if (ModLog.DebugEnabled && Time.unscaledTime >= wsSectionLogAt)
                {
                    wsSectionLogAt = Time.unscaledTime + 5f;
                    pluginLog.LogInfo("[GhostLines][WS] poll idle: replayReady=" + replayReady + " mpInSession=" + mpInSession);
                }
                return;
            }
            if (Time.unscaledTime < wsNamePollAt)
                return;
            // Content read costs a scene-wide FindObjectsOfType: once both names are
            // known and nothing is pending/downloading, drop to a slow 10s refresh.
            bool namesSettled = !string.IsNullOrEmpty(mpCurrentTrackName) &&
                !string.IsNullOrEmpty(mpCurrentRaceName) &&
                wsDownloadingId == 0 && !wsQueryBusy && string.IsNullOrEmpty(wsStatus);
            wsNamePollAt = Time.unscaledTime + (namesSettled ? 10f : 1.5f);
            // The game's level-setup log is authoritative once seen; the content
            // read below only fills in when it hasn't fired this session.
            if (wsLogCaptured)
                return;
            // Stop reading once there is nothing left to learn: both names known —
            // treating "no race name after several reads" as settled for free-flight
            // sessions — and no download active.
            bool noRace = string.IsNullOrEmpty(mpCurrentRaceName);
            if (noRace) wsNoRaceReads++; else wsNoRaceReads = 0;
            bool fullyResolved = !string.IsNullOrEmpty(mpCurrentTrackName) &&
                (!noRace || wsNoRaceReads >= 5) &&
                wsDownloadingId == 0 && !wsQueryBusy && string.IsNullOrEmpty(wsStatus);
            if (fullyResolved)
                return;
            ReadCurrentContent();
        }

        private void TriggerWsAutoDownload()
        {
            if (!autoDownloadWorkshop || wsQueryBusy || wsOfficialContent)
                return;
            if (mpCurrentTrackName == wsAutoAppliedTrackName && mpCurrentRaceName == wsAutoAppliedRaceName)
                return;
            wsAutoAppliedTrackName = mpCurrentTrackName;
            wsAutoAppliedRaceName = mpCurrentRaceName;
            StartWorkshopDownloadAll();
        }

        private void StartWorkshopDownloadAll()
        {
            if (wsQueryBusy)
                return;
            if (Time.unscaledTime - wsLastDownloadClickAt < 10f)
            {
                wsStatus = "Please wait before starting again";
                return;
            }
            wsLastDownloadClickAt = Time.unscaledTime;
            pluginLog.LogInfo("[GhostLines][WS] download requested: track='" + mpCurrentTrackName + "' race='" + mpCurrentRaceName + "' busy=" + wsQueryBusy);
            if (wsOfficialContent)
            {
                wsStatus = null;
                return;
            }
            if (string.IsNullOrEmpty(mpCurrentTrackName) && string.IsNullOrEmpty(mpCurrentRaceName))
            {
                wsStatus = "No track name detected yet";
                return;
            }
            wsPendingNames.Clear();
            wsDownloadWatchIds.Clear();
            // The level-setup log gives the exact workshop id; seeding it skips the
            // name search, whose loose title match could pick a different item.
            SeedWsNameCache(mpCurrentRaceName, wsRaceItemId);
            SeedWsNameCache(mpCurrentTrackName, wsTrackItemId);
            if (!string.IsNullOrEmpty(mpCurrentRaceName))
                wsPendingNames.Add(mpCurrentRaceName);
            if (!string.IsNullOrEmpty(mpCurrentTrackName))
                wsPendingNames.Add(mpCurrentTrackName);
            wsCurrentTargetIndex = 0;
            wsQueryBusy = true;
            wsQueryStartedAt = Time.unscaledTime;
            QueryNextPendingWsName();
        }

        private void QueryNextPendingWsName()
        {
            while (wsCurrentTargetIndex < wsPendingNames.Count)
            {
                string name = wsPendingNames[wsCurrentTargetIndex];
                if (string.IsNullOrEmpty(name))
                {
                    wsCurrentTargetIndex++;
                    continue;
                }
                if (wsNameCache.TryGetValue(name, out Steamworks.PublishedFileId_t cachedId) && cachedId.m_PublishedFileId != 0)
                {
                    SubscribeAndDownloadWs(cachedId, name);
                    wsCurrentTargetIndex++;
                    continue;
                }
                Steamworks.UGCQueryHandle_t handle = Steamworks.UGCQueryHandle_t.Invalid;
                try
                {
                    Steamworks.AppId_t appId = Steamworks.SteamUtils.GetAppID();
                    handle = Steamworks.SteamUGC.CreateQueryAllUGCRequest(
                        Steamworks.EUGCQuery.k_EUGCQuery_RankedByTextSearch,
                        Steamworks.EUGCMatchingUGCType.k_EUGCMatchingUGCType_All,
                        appId, appId, 1);
                    Steamworks.SteamUGC.SetSearchText(handle, name);
                    Steamworks.SteamAPICall_t call = Steamworks.SteamUGC.SendQueryUGCRequest(handle);
                    if (call == Steamworks.SteamAPICall_t.Invalid)
                    {
                        if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines][WS] query send failed (invalid APICall) for '" + name + "'");
                        try { Steamworks.SteamUGC.ReleaseQueryUGCRequest(handle); } catch { }
                        handle = Steamworks.UGCQueryHandle_t.Invalid;
                        wsStatus = "Workshop search failed";
                        wsCurrentTargetIndex++;
                        continue;
                    }
                    if (wsQueryCallResult == null)
                        wsQueryCallResult = Steamworks.CallResult<Steamworks.SteamUGCQueryCompleted_t>.Create(OnWsQueryCompleted);
                    wsPendingTargetName = name;
                    wsQueryCallResult.Set(call);
                    wsStatus = "Searching: " + name;
                    pluginLog.LogInfo("[GhostLines][WS] query sent: '" + name + "'");
                    return;
                }
                catch (Exception ex)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] ws query error: " + ex.Message);
                    if (handle != Steamworks.UGCQueryHandle_t.Invalid)
                    {
                        try { Steamworks.SteamUGC.ReleaseQueryUGCRequest(handle); } catch { }
                        handle = Steamworks.UGCQueryHandle_t.Invalid;
                    }
                    wsStatus = "Workshop search failed";
                    wsCurrentTargetIndex++;
                }
            }
            wsQueryBusy = false;
            wsQueryStartedAt = -1f;
            if (!string.IsNullOrEmpty(wsStatus) && wsStatus.StartsWith("Searching"))
                wsStatus = "Nothing to download";
        }

        // Set by the query callback; the next query starts from TickWorkshopDownload.
        // Starting it inside the callback re-arms the same CallResult while Steam is
        // still dispatching it, and Steamworks.NET clears the handle after the
        // callback returns — the second query's result was never delivered.
        private bool wsQueryAdvancePending;

        private void OnWsQueryCompleted(Steamworks.SteamUGCQueryCompleted_t result, bool ioFailure)
        {
            try
            {
                string target = wsPendingTargetName;
                wsPendingTargetName = null;
                pluginLog.LogInfo("[GhostLines][WS] query completed: target='" + target + "' result=" + result.m_eResult +
                    " results=" + result.m_unNumResultsReturned + " ioFailure=" + ioFailure);
                if (ioFailure || result.m_eResult != Steamworks.EResult.k_EResultOK || string.IsNullOrEmpty(target))
                {
                    wsStatus = "Workshop search failed";
                    wsCurrentTargetIndex++;
                    wsQueryAdvancePending = true;
                    return;
                }
                Steamworks.PublishedFileId_t best = Steamworks.PublishedFileId_t.Invalid;
                int bestScore = 0;
                for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                {
                    try
                    {
                        if (!Steamworks.SteamUGC.GetQueryUGCResult(result.m_handle, i, out Steamworks.SteamUGCDetails_t details))
                            continue;
                        string title = details.m_rgchTitle ?? "";
                        int score = string.Equals(title.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase) ? 100
                            : title.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 ? 50 : 0;
                        if (score > bestScore)
                        {
                            best = details.m_nPublishedFileId;
                            bestScore = score;
                        }
                        if (score == 100)
                            break;
                    }
                    catch { }
                }
                if (best.m_PublishedFileId == 0 || bestScore <= 0)
                {
                    wsStatus = "No workshop match: " + target;
                    wsCurrentTargetIndex++;
                    wsQueryAdvancePending = true;
                    return;
                }
                wsNameCache[target] = best;
                SubscribeAndDownloadWs(best, target);
                wsCurrentTargetIndex++;
                wsQueryAdvancePending = true;
            }
            catch (Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] ws query completion error: " + ex.Message);
                wsQueryBusy = false;
                wsQueryStartedAt = -1f;
                wsStatus = "Workshop search failed";
            }
            finally
            {
                // The query handle must be released on every completion path
                // (success, no-match, io-failure, exception) or it leaks.
                try { Steamworks.SteamUGC.ReleaseQueryUGCRequest(result.m_handle); } catch { }
            }
        }

        private void SeedWsNameCache(string name, string itemId)
        {
            ulong id;
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(itemId) &&
                ulong.TryParse(itemId, out id) && id != 0)
                wsNameCache[name] = new Steamworks.PublishedFileId_t(id);
        }

        // EItemState bits: 4 = installed, 8 = needs update.
        private const uint WsStateInstalled = 4u;
        private const uint WsStateNeedsUpdate = 8u;

        private void SubscribeAndDownloadWs(Steamworks.PublishedFileId_t id, string name)
        {
            try
            {
                // Already installed and current: nothing to subscribe or download.
                // No status text: a non-empty status keeps the content poll (a
                // scene-wide scan) running every 1.5s.
                uint state = Steamworks.SteamUGC.GetItemState(id);
                if ((state & WsStateInstalled) != 0 && (state & WsStateNeedsUpdate) == 0)
                {
                    if (wsStatus != null && wsStatus.StartsWith("Searching"))
                        wsStatus = null;
                    pluginLog.LogInfo("[GhostLines][WS] already installed, skipped: '" + name + "' id=" + id.m_PublishedFileId);
                    return;
                }
                Steamworks.SteamUGC.SubscribeItem(id);
                Steamworks.SteamUGC.DownloadItem(id, true);
                wsDownloadingId = id.m_PublishedFileId;
                wsDownloadingName = name;
                wsDownloadWatchIds.Add(id.m_PublishedFileId);
                wsStatus = "Downloading: " + name;
                pluginLog.LogInfo("[GhostLines][WS] subscribed + downloading: '" + name + "' id=" + id.m_PublishedFileId);
            }
            catch (Exception ex)
            {
                wsStatus = "Subscribe failed";
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] ws subscribe error: " + ex.Message);
            }
        }

        private void CopyWorkshopLink()
        {
            if (wsOfficialContent)
                return;
            try
            {
                string url = null;
                if (!string.IsNullOrEmpty(wsTrackItemId) && wsTrackItemId != "0")
                    url = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + wsTrackItemId;
                else
                {
                    string key = !string.IsNullOrEmpty(mpCurrentRaceName) ? mpCurrentRaceName : mpCurrentTrackName;
                    if (!string.IsNullOrEmpty(key))
                        url = "https://steamcommunity.com/workshop/browse/?appid=" + Steamworks.SteamUtils.GetAppID().m_AppId +
                            "&searchtext=" + Uri.EscapeDataString(key);
                }
                if (url != null)
                {
                    GUIUtility.systemCopyBuffer = url;
                    wsStatus = "Workshop link copied";
                }
            }
            catch (Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] ws copy error: " + ex.Message);
            }
        }
    }
}
