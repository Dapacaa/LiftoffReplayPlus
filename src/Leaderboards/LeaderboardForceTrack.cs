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
        // After our jump to the leaderboards scene, force the native track
        // dropdown to the track we came from (the scene otherwise defaults to
        // the last hosted track). Options populate asynchronously, so retry
        // briefly; all outcomes are logged.
        private void TickLbForceTrack()
        {
            if (!lbLoadSceneRequested)
            {
                lbForceTrackUntil = 0f;
                lbForcedTrack = null;
                lbKeywordTried = false;
                lbKeywordCheckAt = 0f;
                lbForceEnv = null;
                lbEnvTried = false;
                lbEnvScanned = false;
                lbForceController = null;
                lbJumpTrack = null;
                lbJumpRace = null;
                lbJumpEnv = null;
                lbJumpRaceId = null;
                lbJumpTrackId = null;
                lbJumpMode = -1;
                lbMissLogged = false;
                lbAttribCheckAt = 0f;
                return;
            }
            string sceneNow = null;
            try { sceneNow = SceneManager.GetActiveScene().name; } catch { }
            if (sceneNow != "MultiplayerLeaderboards")
            {
                // Not in the leaderboards scene (flight/loading, or we've left).
                // Clear the force-arming gate here so a *new* jump re-arms cleanly.
                // lbForcedTrack otherwise persists from the previous successful
                // force and permanently blocks the re-arm gate on the next visit.
                // lbLoadSceneRequested is intentionally left set: it records that a
                // jump is in flight and drives forcing once the scene arrives.
                lbForceTrackUntil = 0f;
                lbForcedTrack = null;
                lbKeywordTried = false;
                lbKeywordCheckAt = 0f;
                lbForceEnv = null;
                lbEnvTried = false;
                lbEnvScanned = false;
                lbForceController = null;
                lbAttribCheckAt = 0f;
                return;
            }
            TickLbAttribCheck();
            float now = Time.unscaledTime;
            // Gate re-arms on lbForcedTrack: a successful force must not re-arm
            // every frame while the request flag stays set for row capture.
            if (lbForceTrackUntil == 0f && lbForcedTrack == null)
            {
                lbForceTrackUntil = now + 10f;
                lbForceTrackNextAt = now;
                lbForceEnv = lbJumpEnv ?? ReadRoomEnvironment() ?? wsEnvironmentName;
                lbEnvTried = false;
                lbEnvScanned = false;
                lbMissLogged = false;
                pluginLog.LogInfo("[GhostLines] leaderboards: forcing track dropdown to '" +
                    (CurrentTrackKey() ?? "null") + "' env='" + (lbForceEnv ?? "null") + "'");
            }
            if (lbForceTrackUntil == 0f)
                return;
            if (now > lbForceTrackUntil)
            {
                DumpLbTrackOptions("timeout");
                pluginLog.LogInfo("[GhostLines] leaderboards: track forcing timed out — leaving scene selection as-is");
                lbForceTrackUntil = 0f;
                lbForcedTrack = null;
                lbKeywordTried = false;
                lbKeywordCheckAt = 0f;
                lbForceEnv = null;
                lbEnvTried = false;
                lbEnvScanned = false;
                lbJumpTrack = null;
                lbJumpRace = null;
                lbJumpEnv = null;
                lbJumpRaceId = null;
                lbJumpTrackId = null;
                lbJumpMode = -1;
                lbMissLogged = false;
                lbAttribCheckAt = 0f;
                lbLoadSceneRequested = false;
                return;
            }
            // Environment first: the track list is filtered by it, and the scene
            // defaults to the last hosted track's environment.
            if (!lbEnvTried)
                lbEnvTried = TryForceLbEnv();
            if (lbKeywordCheckAt != 0f && now >= lbKeywordCheckAt)
            {
                lbKeywordCheckAt = 0f;
                string afterKw = CurrentLbDropdownText();
                if (!string.IsNullOrEmpty(afterKw) && !string.Equals(afterKw.Trim(),
                    (lbKeywordBefore ?? "").Trim(), System.StringComparison.OrdinalIgnoreCase))
                    pluginLog.LogInfo("[GhostLines] leaderboards: keyword load confirmed, dropdown now on '" + afterKw + "'");
                else
                    pluginLog.LogInfo("[GhostLines] leaderboards: keyword load showed no dropdown change — dropdown fallback continues");
                lbAttribCheckAt = now + 1f;
            }
            // Workshop id is exact: try the game's keyword loader first, fall back
            // to dropdown name matching below. Prefers the jump snapshot.
            string wsForceId = !string.IsNullOrEmpty(lbJumpRaceId) ? lbJumpRaceId :
                (!string.IsNullOrEmpty(lbJumpTrackId) ? lbJumpTrackId :
                (!string.IsNullOrEmpty(wsRaceItemId) ? wsRaceItemId : wsTrackItemId));
            if (!lbKeywordTried && !string.IsNullOrEmpty(wsForceId))
            {
                if (TryForceLbKeyword(wsForceId))
                    lbForceTrackNextAt = now + 2.5f;
            }
            if (now < lbForceTrackNextAt)
                return;
            lbForceTrackNextAt = now + 0.5f;
            if (TryForceLbTrack())
            {
                lbForceTrackUntil = 0f;
                lbAttribCheckAt = now + 2.5f;
                // Leave lbLoadSceneRequested set: repopulating rows clear it via
                // CaptureLbRow, which auto-opens the replay list.
            }
        }

        private void DumpLbTrackOptions(string reason)
        {
            try
            {
                var dropdown = FindLbTrackDropdown();
                if (dropdown != null && dropdown.options != null)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[GhostLines] leaderboards: track dropdown (").Append(reason).Append(") has ")
                        .Append(dropdown.options.Count).Append(" options, selected=").Append(dropdown.value).Append(":");
                    for (int i = 0; i < dropdown.options.Count && i < 250; i++)
                        sb.Append(" '").Append(dropdown.options[i] != null ? dropdown.options[i].text : "?").Append("'");
                    pluginLog.LogInfo(sb.ToString());
                    return;
                }
                pluginLog.LogInfo("[GhostLines] leaderboards: track dropdown (" + reason + ") not found for dump");
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: option dump EX " + ex.Message);
            }
        }

        private static string StripTrackPrefix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            int i = 0;
            while (i < name.Length && char.IsDigit(name[i]))
                i++;
            if (i > 0 && i < name.Length && (name[i] == '-' || name[i] == ':'))
            {
                int j = i + 1;
                while (j < name.Length && char.IsWhiteSpace(name[j]))
                    j++;
                if (j < name.Length)
                    return name.Substring(j);
            }
            return name;
        }

        // Dropdown name matching: exact first, then containment — but the fuzzy
        // direction requires substance on both sides so generic option words
        // like 'Track' can't match ('02 - Parcel Tracking' contains 'Track').
        // Comparison is space/punctuation-insensitive: game state uses internal
        // names ('TheDrawingBoard') while dropdowns show display names
        // ('The Drawing Board').
        private static string NormLbName(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            // Keep only letters/digits so punctuation differences don't break matching:
            // room props use internal names ("BardwellsYard") while dropdowns show
            // display names ("Bardwell's Yard", "The Drawing Board").
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!char.IsLetterOrDigit(c))
                    continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static bool LooseNameMatch(string option, string want)
        {
            if (string.IsNullOrEmpty(option) || string.IsNullOrEmpty(want))
                return false;
            string o = option.Trim();
            string w = want.Trim();
            if (string.Equals(o, w, System.StringComparison.OrdinalIgnoreCase))
                return true;
            string n1 = NormLbName(o);
            string n2 = NormLbName(w);
            if (n1.Length > 0 && n1 == n2)
                return true;
            if (System.Math.Min(n1.Length, n2.Length) < 8)
                return false;
            return n1.Contains(n2) || n2.Contains(n1);
        }

        // The leaderboard screen's track dropdown, found by its fixed UI path under
        // any root Canvas. The one lookup for every leaderboard feature.
        private const string LbTrackDropdownPath =
            "LeaderboardSelection/panelFilters/Content/FilterPanel/panelDropdownFilters/ContentFilter/dropdownTrack";

        private Dropdown FindLbTrackDropdown()
        {
            return FindLbTrackDropdown(SceneManager.GetActiveScene());
        }

        private static Dropdown FindLbTrackDropdown(Scene scene)
        {
            try
            {
                if (!scene.IsValid() || !scene.isLoaded)
                    return null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null)
                        continue;
                    Transform canvas = root.name == "Canvas" ? root.transform : root.transform.Find("Canvas");
                    if (canvas == null)
                        continue;
                    Transform target = canvas.Find(LbTrackDropdownPath);
                    if (target == null)
                        continue;
                    var dropdown = target.GetComponent<Dropdown>();
                    if (dropdown == null)
                        dropdown = target.GetComponentInChildren<Dropdown>(true);
                    if (dropdown != null)
                        return dropdown;
                }
            }
            catch { }
            return null;
        }

        private string CurrentLbDropdownText()
        {
            try
            {
                var dropdown = FindLbTrackDropdown();
                if (dropdown != null && dropdown.options != null &&
                    dropdown.value >= 0 && dropdown.value < dropdown.options.Count &&
                    dropdown.options[dropdown.value] != null)
                    return dropdown.options[dropdown.value].text;
            }
            catch { }
            return null;
        }

        private string ReadRoomEnvironment()
        {
            try
            {
                var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                if (room != null && room.CustomProperties != null && room.CustomProperties.ContainsKey("E"))
                    return room.CustomProperties["E"] as string;
            }
            catch { }
            return null;
        }

        // The track dropdown only lists the selected environment's tracks. Point
        // it at our environment first so the wanted track can appear at all.
        // Returns true once executed (dropdown resolved); the tick retries
        // until then. Falls back to a scene-wide dropdown scan when the
        // controller field isn't bound yet.
        private bool TryForceLbEnv()
        {
            try
            {
                if (string.IsNullOrEmpty(lbForceEnv))
                    return true;
                Dropdown dropdown = null;
                Component controller = ResolveLbForceController();
                if (controller != null)
                {
                    const System.Reflection.BindingFlags eflags =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
                    var field = controller.GetType().GetField("dropdownEnvironmentFilter", eflags);
                    dropdown = field != null ? field.GetValue(controller) as Dropdown : null;
                }
                if ((dropdown == null || dropdown.options == null || dropdown.options.Count == 0) && !lbEnvScanned)
                {
                    lbEnvScanned = true;
                    dropdown = ScanLbDropdownsForEnv();
                }
                if (dropdown == null || dropdown.options == null || dropdown.options.Count == 0)
                    return false;
                int best = -1;
                for (int i = 0; i < dropdown.options.Count; i++)
                {
                    string o = dropdown.options[i] != null ? dropdown.options[i].text : null;
                    if (LooseNameMatch(o, lbForceEnv))
                    {
                        best = i;
                        break;
                    }
                }
                if (best < 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[GhostLines] leaderboards: env '").Append(lbForceEnv).Append("' not among ")
                        .Append(dropdown.options.Count).Append(" env options:");
                    for (int i = 0; i < dropdown.options.Count && i < 30; i++)
                        sb.Append(" '").Append(dropdown.options[i] != null ? dropdown.options[i].text : "?").Append("'");
                    pluginLog.LogInfo(sb.ToString());
                    return true;
                }
                string sel = dropdown.options[dropdown.value] != null ? dropdown.options[dropdown.value].text : "?";
                if (dropdown.value == best)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: env dropdown already on '" + sel + "'");
                    return true;
                }
                dropdown.value = best;
                try { dropdown.RefreshShownValue(); } catch { }
                pluginLog.LogInfo("[GhostLines] leaderboards: env dropdown forced '" + sel + "' -> '" +
                    dropdown.options[best].text + "' (track list will repopulate)");
                return true;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: env forcing EX " + ex.Message);
                return true;
            }
        }

        // One-shot scene-wide dropdown census: finds the environment dropdown by
        // content (whichever lists our environment) and logs the full dropdown
        // layout so a miss is diagnosable.
        private Dropdown ScanLbDropdownsForEnv()
        {
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    return null;
                var found = new System.Collections.Generic.List<Dropdown>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null)
                        continue;
                    foreach (var dd in root.GetComponentsInChildren<Dropdown>(true))
                    {
                        if (dd == null || found.Contains(dd))
                            continue;
                        found.Add(dd);
                    }
                }
                var sb = new System.Text.StringBuilder();
                sb.Append("[GhostLines] leaderboards: dropdown census (").Append(found.Count).Append("):");
                Dropdown envPick = null;
                foreach (var dd in found)
                {
                    string path = GetTransformPath(dd.transform);
                    int n = 0;
                    string sel = "?";
                    try
                    {
                        n = dd.options != null ? dd.options.Count : -1;
                        if (dd.options != null && dd.value >= 0 && dd.value < dd.options.Count &&
                            dd.options[dd.value] != null)
                            sel = dd.options[dd.value].text ?? "?";
                    }
                    catch { }
                    sb.Append(" [").Append(path).Append(" sel='").Append(sel).Append("' n=").Append(n).Append("]");
                    if (envPick == null && dd.options != null && !string.IsNullOrEmpty(lbForceEnv))
                    {
                        foreach (var opt in dd.options)
                        {
                            string o = opt != null ? (opt.text ?? "").Trim() : "";
                            if (string.Equals(o, lbForceEnv.Trim(), System.StringComparison.OrdinalIgnoreCase))
                            {
                                envPick = dd;
                                break;
                            }
                        }
                    }
                }
                pluginLog.LogInfo(sb.ToString());
                if (envPick != null)
                    pluginLog.LogInfo("[GhostLines] leaderboards: env dropdown located via census");
                else
                    pluginLog.LogInfo("[GhostLines] leaderboards: no dropdown lists env '" + lbForceEnv + "'");
                return envPick;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: dropdown census EX " + ex.Message);
                return null;
            }
        }

        // Reads the leaderboards scene's live keyword content id — ground truth
        // for which track the scene is actually showing.
        private string ReadLbSceneKeywordId()
        {
            try
            {
                Component controller = lbForceController ?? leaderboardController;
                if (controller == null)
                    return null;
                var kwType = typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardKeyword);
                const System.Reflection.BindingFlags kflags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                foreach (var f in controller.GetType().GetFields(kflags))
                {
                    try
                    {
                        if (f.FieldType != kwType)
                            continue;
                        object kw = f.GetValue(controller);
                        if (kw == null)
                            continue;
                        var fId = AccessTools.Field(kwType, "contentId");
                        string id = fId != null ? fId.GetValue(kw) as string : null;
                        if (!string.IsNullOrEmpty(id))
                            return id;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void TickLbAttribCheck()
        {
            if (lbAttribCheckAt == 0f || !lbLoadSceneRequested)
                return;
            if (Time.unscaledTime < lbAttribCheckAt)
                return;
            lbAttribCheckAt = 0f;
            try
            {
                string sceneId = ReadLbSceneKeywordId();
                string wantId = !string.IsNullOrEmpty(lbJumpRaceId) ? lbJumpRaceId :
                    (!string.IsNullOrEmpty(lbJumpTrackId) ? lbJumpTrackId :
                    (!string.IsNullOrEmpty(wsRaceItemId) ? wsRaceItemId : wsTrackItemId));
                string sel = CurrentLbDropdownText();
                string verdict;
                if (!string.IsNullOrEmpty(sceneId) && !string.IsNullOrEmpty(wantId))
                    verdict = sceneId == wantId ? "MATCH" : "MISMATCH";
                else
                    verdict = "unknown";
                pluginLog.LogInfo("[GhostLines] leaderboards: attribution sceneId='" + (sceneId ?? "null") +
                    "' wantedId='" + (wantId ?? "null") + "' dropdown='" + (sel ?? "null") + "' => " + verdict);
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: attribution EX " + ex.Message);
            }
        }

        // Direct keyword load for the current track's workshop id — covers tracks
        // the dropdown doesn't list. Mirrors the game's own keyword path
        // (contentId + room game mode) resolved from IL.
        private bool TryForceLbKeyword(string contentId)
        {
            try
            {
                Component controller = ResolveLbForceController();
                if (controller == null)
                    return false;
                var kwType = typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardKeyword);
                System.Reflection.MethodInfo loader = null;
                System.Reflection.MethodInfo coroutine = null;
                const System.Reflection.BindingFlags mflags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
                foreach (var me in controller.GetType().GetMethods(mflags))
                {
                    if (me.IsSpecialName)
                        continue;
                    var ps = me.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == kwType)
                    {
                        if (me.ReturnType == typeof(void) && loader == null)
                            loader = me;
                        else if (typeof(System.Collections.IEnumerator).IsAssignableFrom(me.ReturnType) &&
                            coroutine == null)
                            coroutine = me;
                    }
                }
                if (loader == null && coroutine == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: keyword loader methods not found");
                    lbKeywordTried = true;
                    return false;
                }
                lbKeywordTried = true;
                object keyword = System.Activator.CreateInstance(kwType, new object[] { contentId });
                int gm = (lbJumpMode >= 1 && lbJumpMode <= 3) ? lbJumpMode : LeaderboardModeForRoomMode(ReadRoomGameMode());
                if (gm >= 1 && gm <= 3)
                {
                    var fGameMode = AccessTools.Field(kwType, "gameMode");
                    if (fGameMode != null)
                        fGameMode.SetValue(keyword, System.Enum.ToObject(typeof(Liftoff.Leaderboards.GameMode), gm));
                }
                string trackName = CurrentTrackKey();
                AccessTools.Field(kwType, "nameWithContent")?.SetValue(keyword, trackName ?? contentId);
                AccessTools.Field(kwType, "nameWithoutContent")?.SetValue(keyword, trackName ?? contentId);
                lbKeywordBefore = CurrentLbDropdownText();
                if (coroutine != null)
                {
                    var cps = coroutine.GetParameters();
                    object carg2 = null;
                    try
                    {
                        var pt = cps[1].ParameterType;
                        if (pt.IsArray)
                            carg2 = System.Array.CreateInstance(pt.GetElementType(), 0);
                        else if (pt.IsGenericType && pt.GetGenericArguments().Length == 1)
                            carg2 = System.Array.CreateInstance(pt.GetGenericArguments()[0], 0);
                        else
                            carg2 = BuildLbLoaderArg(pt);
                    }
                    catch { }
                    object enumerator = coroutine.Invoke(controller, new object[] { keyword, carg2 });
                    var routine = enumerator as System.Collections.IEnumerator;
                    if (routine == null)
                    {
                        pluginLog.LogInfo("[GhostLines] leaderboards: keyword coroutine did not return an enumerator");
                        return false;
                    }
                    StartCoroutine(routine);
                    lbKeywordCheckAt = Time.unscaledTime + 3f;
                    pluginLog.LogInfo("[GhostLines] leaderboards: keyword coroutine started for '" +
                        (trackName ?? contentId) + "' id=" + contentId + " mode=" + gm);
                    return true;
                }
                var lps = loader.GetParameters();
                pluginLog.LogInfo("[GhostLines] leaderboards: keyword loader 2nd param: " +
                    lps[1].ParameterType.FullName + " valueType=" + lps[1].ParameterType.IsValueType);
                object arg2 = BuildLbLoaderArg(lps[1].ParameterType);
                loader.Invoke(controller, new object[] { keyword, arg2 });
                lbKeywordCheckAt = Time.unscaledTime + 2f;
                pluginLog.LogInfo("[GhostLines] leaderboards: keyword loader invoked for '" +
                    (trackName ?? contentId) + "' id=" + contentId + " mode=" + gm);
                return true;
            }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                string stack = "";
                try
                {
                    var lines = (inner.StackTrace ?? "").Split('\n');
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < lines.Length && i < 5; i++)
                        sb.Append(lines[i].Trim()).Append(" <- ");
                    stack = sb.ToString();
                }
                catch { }
                pluginLog.LogInfo("[GhostLines] leaderboards: keyword load EX " + inner.Message + " @ " + stack);
                return false;
            }
        }

        // Builds a non-null 2nd argument for the keyword loader: a no-op delegate
        // for delegate types, a default instance when constructible, else null.
        private object BuildLbLoaderArg(System.Type argType)
        {
            try
            {
                if (argType == null)
                    return null;
                if (argType.IsValueType)
                    return System.Activator.CreateInstance(argType);
                if (typeof(System.Delegate).IsAssignableFrom(argType))
                {
                    var invoke = argType.GetMethod("Invoke");
                    if (invoke != null)
                    {
                        var ps = invoke.GetParameters();
                        var exprPs = new System.Linq.Expressions.ParameterExpression[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                            exprPs[i] = System.Linq.Expressions.Expression.Parameter(ps[i].ParameterType, "a" + i);
                        System.Linq.Expressions.Expression body;
                        if (invoke.ReturnType == typeof(void))
                            body = System.Linq.Expressions.Expression.Empty();
                        else
                            body = System.Linq.Expressions.Expression.Default(invoke.ReturnType);
                        var lambda = System.Linq.Expressions.Expression.Lambda(argType, body, exprPs);
                        object del = lambda.Compile();
                        pluginLog.LogInfo("[GhostLines] leaderboards: built no-op delegate for " + argType.Name);
                        return del;
                    }
                    return null;
                }
                var ctor = argType.GetConstructor(System.Type.EmptyTypes);
                if (ctor != null)
                    return System.Activator.CreateInstance(argType);
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: loader-arg build EX " + ex.Message);
            }
            return null;
        }

        private bool TryForceLbTrack()
        {
            try
            {
                Dropdown dropdown = FindLbTrackDropdown();
                if (dropdown == null || dropdown.options == null || dropdown.options.Count == 0)
                    return false;
                var want = new List<string>();
                string wantRace = !string.IsNullOrEmpty(lbJumpRace) ? lbJumpRace : mpCurrentRaceName;
                string wantTrack = !string.IsNullOrEmpty(lbJumpTrack) ? lbJumpTrack : mpCurrentTrackName;
                if (!string.IsNullOrEmpty(wantRace))
                    want.Add(wantRace.Trim());
                if (!string.IsNullOrEmpty(wantTrack) && !want.Contains(wantTrack.Trim()))
                    want.Add(wantTrack.Trim());
                try
                {
                    if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.CurrentRoom != null &&
                        Photon.Pun.PhotonNetwork.CurrentRoom.CustomProperties != null &&
                        Photon.Pun.PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey("CN"))
                    {
                        string cn = Photon.Pun.PhotonNetwork.CurrentRoom.CustomProperties["CN"] as string;
                        if (!string.IsNullOrEmpty(cn) && !want.Contains(cn.Trim()))
                            want.Add(cn.Trim());
                    }
                }
                catch { }
                // Stock tracks may be listed without their "NN - " number prefix.
                int wantBase = want.Count;
                for (int w = 0; w < wantBase; w++)
                {
                    string stripped = StripTrackPrefix(want[w]);
                    if (!string.IsNullOrEmpty(stripped) && !want.Contains(stripped))
                        want.Add(stripped);
                }
                if (want.Count == 0)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: no current track known — cannot force dropdown");
                    lbLoadSceneRequested = false;
                    return true;
                }
                int best = -1;
                for (int i = 0; i < dropdown.options.Count; i++)
                {
                    string o = dropdown.options[i] != null ? dropdown.options[i].text : null;
                    for (int w = 0; w < want.Count; w++)
                    {
                        if (LooseNameMatch(o, want[w]))
                        {
                            best = i;
                            break;
                        }
                    }
                    if (best >= 0)
                        break;
                }
                if (best < 0)
                {
                    if (ModLog.DebugEnabled && !lbMissLogged)
                    {
                        lbMissLogged = true;
                        var sb = new System.Text.StringBuilder();
                        sb.Append("[GhostLines] leaderboards: track dropdown has ").Append(dropdown.options.Count)
                            .Append(" options, no match for [").Append(string.Join(",", want.ToArray())).Append("]:");
                        for (int i = 0; i < dropdown.options.Count && i < 40; i++)
                            sb.Append(" '").Append(dropdown.options[i] != null ? dropdown.options[i].text : "?").Append("'");
                        pluginLog.LogInfo(sb.ToString());
                    }
                    return false;
                }
                if (dropdown.value == best)
                {
                    lbForcedTrack = dropdown.options[best].text;
                    pluginLog.LogInfo("[GhostLines] leaderboards: track dropdown already on '" + lbForcedTrack + "'");
                    return true;
                }
                dropdown.value = best;
                try { dropdown.RefreshShownValue(); } catch { }
                lbForcedTrack = dropdown.options[best].text;
                pluginLog.LogInfo("[GhostLines] leaderboards: track dropdown forced to '" + lbForcedTrack +
                    "' (option " + (best + 1) + "/" + dropdown.options.Count + ")");
                return true;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: track forcing EX " + ex.Message);
                return false;
            }
        }

        // Walks OnToWaitingRoom's IL for the `newobj` of its confirm-callback closure,
        // then invokes the closure's single method — the exact code the confirm
        // popup's Confirm button would run.
        private void DrawBackToLobbyButton()
        {
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            var rect = new Rect(Screen.width - 166f, 10f, 156f, 28f);
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            GUI.color = hover ? new Color(0.25f, 0.62f, 0.95f, 0.95f) : new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = hover ? Color.white : ThemeLabel(1f);
            GUI.Label(rect, "Back to Lobby", settingsSmallCenterStyle != null ? settingsSmallCenterStyle : GUI.skin.label);
            GUI.color = Color.white;
            if (e.type == EventType.MouseUp && e.button == 0 && hover)
            {
                e.Use();
                TryReturnToMpLobby();
            }
        }
    }
}
