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
        // Transient on-screen feedback for the popup replay flow.
        // (Status text intentionally removed — the native Load button is the only
        // feedback surface we keep.)

        // Drops the Unity EventSystem's UI selection so the red "selected" highlight
        // doesn't stick to a row after dragging. Never clears a focused text field
        // (chat) so typing stays intact.
        private static void ClearLbEventSelection()
        {
            try
            {
                if (EventSystem.current == null)
                    return;
                GameObject sel = EventSystem.current.currentSelectedGameObject;
                if (sel == null)
                    return;
                if (sel.GetComponent<UnityEngine.UI.InputField>() != null ||
                    sel.GetComponentInParent<UnityEngine.UI.InputField>() != null)
                    return;
                EventSystem.current.SetSelectedGameObject(null);
            }
            catch { }
        }

        // The controller's Start runs once per MultiplayerLeaderboards visit.
        // Rows/consumers from a previous visit reference destroyed objects, so
        // drop them and capture fresh ones; forcing uses the in-flight jump flag.
        public void ResetLbCaptureForNewScene()
        {
            lbDisplayRows.Clear();
            lbConsumers.Clear();
            lbCachedTrack = null;
            lbSelectedKeys.Clear();
            lbForceController = null;
            lbDirectPanelField = null;
            lbDirectKeywordGetter = null;
            lbDirectLoadCoroutine = null;
            lbDirectPanelType = null;
        }

        // The controller reference for track forcing. Stored unconditionally —
        // unlike leaderboardController, which only sticks once dropdowns resolve.
        public void NoteLbForceController(Component controller)
        {
            if (controller != null)
                lbForceController = controller;
        }

        // Self-sufficient controller resolution: stored ref first, then the
        // controller ancestor of the scene's track dropdown (same relationship
        // the attach path uses). Cached once found.
        private Component ResolveLbForceController()
        {
            try
            {
                if (lbForceController != null)
                    return lbForceController;
                if (leaderboardController != null)
                {
                    lbForceController = leaderboardController;
                    return lbForceController;
                }
                var dropdown = FindLbTrackDropdown();
                if (dropdown == null)
                    return null;
                const System.Reflection.BindingFlags rflags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                Transform t = dropdown.transform;
                for (int up = 0; up < 12 && t != null; up++)
                {
                    MonoBehaviour[] comps = null;
                    try { comps = t.GetComponents<MonoBehaviour>(); } catch { }
                    if (comps != null)
                    {
                        foreach (var c in comps)
                        {
                            if (c == null)
                                continue;
                            try
                            {
                                if (c.GetType().GetField("dropdownEnvironmentFilter", rflags) != null)
                                {
                                    lbForceController = c;
                                    pluginLog.LogInfo("[GhostLines] leaderboards: controller resolved via dropdown ancestor");
                                    return lbForceController;
                                }
                            }
                            catch { }
                        }
                    }
                    t = t.parent;
                }
            }
            catch { }
            return null;
        }
        private string lastMpRoomName = null;
        private readonly List<LbConsumer> lbConsumers = new List<LbConsumer>();
        private readonly List<LbDisplayRow> lbDisplayRows = new List<LbDisplayRow>();
        private string lbCachedTrack = null;
        private System.Reflection.MethodInfo lbRankGetter = null;
        private System.Reflection.MethodInfo lbScoreGetter = null;
        private System.Reflection.MethodInfo lbHasRecGetter = null;
        private System.Reflection.MethodInfo lbStampGetter = null;

        public void CaptureLbRow(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry row)
        {
            if (row == null)
                return;
            try
            {
                var tEntry = typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry);
                var fDel = AccessTools.Field(tEntry, "onRecordingToggled");
                var fEntry = AccessTools.Field(tEntry, "entry");
                var d = fDel != null ? fDel.GetValue(row) as System.Delegate : null;
                if (d != null)
                {
                    foreach (var inv in d.GetInvocationList())
                    {
                        bool exists = false;
                        for (int i = 0; i < lbConsumers.Count; i++)
                            if (lbConsumers[i].Target == inv.Target && lbConsumers[i].Method == inv.Method)
                            {
                                exists = true;
                                break;
                            }
                        if (!exists)
                            lbConsumers.Add(new LbConsumer { Target = inv.Target, Method = inv.Method });
                    }
                }
                object ev = fEntry != null ? fEntry.GetValue(row) : null;
                if (ev == null)
                    return;
                if (lbRankGetter == null)
                {
                    var iface = fEntry.FieldType;
                    lbRankGetter = iface.GetMethod("get_Rank");
                    lbScoreGetter = iface.GetMethod("get_Score");
                    lbHasRecGetter = iface.GetMethod("get_HasRecording");
                    lbStampGetter = iface.GetMethod("get_Timestamp");
                }
                string key = null;
                try { key = ev.ToString(); } catch { }
                if (string.IsNullOrEmpty(key))
                    return;
                for (int i = 0; i < lbDisplayRows.Count; i++)
                    if (lbDisplayRows[i].Key == key)
                        return;
                var row2 = new LbDisplayRow { Entry = ev, Row = row, Key = key };
                try { row2.Rank = lbRankGetter != null ? (int)lbRankGetter.Invoke(ev, null) : 0; } catch { }
                try { row2.Score = lbScoreGetter != null ? System.Convert.ToInt64(lbScoreGetter.Invoke(ev, null)) : 0; } catch { }
                try { row2.HasRec = lbHasRecGetter != null && (bool)lbHasRecGetter.Invoke(ev, null); } catch { }
                try
                {
                    var dt = lbStampGetter != null ? (System.DateTime)lbStampGetter.Invoke(ev, null) : System.DateTime.MinValue;
                    row2.Date = dt == System.DateTime.MinValue ? "" : dt.ToString("MM/dd/yy HH:mm");
                }
                catch { row2.Date = ""; }
                lbDisplayRows.Add(row2);
                lbDisplayRows.Sort((a, b) => a.Rank.CompareTo(b.Rank));
                lbCachedTrack = !string.IsNullOrEmpty(mpCurrentTrackName) ? mpCurrentTrackName : mpCurrentRaceName;
                // After auto-navigating to the leaderboards scene, open the list as
                // soon as rows are populated so the next click loads a replay.
                if (lbLoadSceneRequested && lbConsumers.Count > 0)
                {
                    lbLoadSceneRequested = false;
                    lbReplayWindowOpen = true;
                    lbSelectedKeys.Clear();
                }
            }
            catch { }
        }

        private string CurrentTrackKey()
        {
            return !string.IsNullOrEmpty(mpCurrentRaceName) ? mpCurrentRaceName :
                !string.IsNullOrEmpty(mpCurrentTrackName) ? mpCurrentTrackName : null;
        }

        // Resolves (once) the controller's panel field, the panel's
        // GetCurrentKeyword getter, and the controller's "download selected
        // recordings -> load replay" coroutine (the native View Replay path).
        private bool EnsureLbDirectReflection()
        {
            if (lbDirectLoadCoroutine != null && lbDirectPanelField != null && lbDirectKeywordGetter != null)
                return true;
            Component controller = lbPersistController ?? lbForceController ?? leaderboardController;
            if (controller == null)
                return false;
            System.Type ctrlType = controller.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            if (lbDirectPanelField == null)
                lbDirectPanelField = AccessTools.Field(ctrlType, "leaderboardPanel");
            if (lbDirectPanelField == null)
                return false;
            lbDirectPanelType = lbDirectPanelField.FieldType;
            if (lbDirectKeywordGetter == null)
            {
                foreach (var m in lbDirectPanelType.GetMethods(flags))
                {
                    if (m.GetParameters().Length == 0 &&
                        m.ReturnType.Name == "CommunityLeaderboardKeyword")
                    {
                        lbDirectKeywordGetter = m;
                        break;
                    }
                }
            }
            if (lbDirectLoadCoroutine == null)
            {
                foreach (var m in ctrlType.GetMethods(flags))
                {
                    if (m.ReturnType != typeof(System.Collections.IEnumerator))
                        continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 &&
                        ps[0].ParameterType.Name == "CommunityLeaderboardKeyword" &&
                        ps[1].ParameterType.Name.StartsWith("IEnumerable"))
                    {
                        lbDirectLoadCoroutine = m;
                        break;
                    }
                }
            }
            return lbDirectLoadCoroutine != null && lbDirectKeywordGetter != null;
        }

        // Keeps the leaderboards controller (and its UI hierarchy) alive after its
        // scene unloads, so the in-game ESC popup can reuse the native download +
        // Level-load coroutine without a scene visit. Called from the controller's
        // Start postfix, once per MultiplayerLeaderboards visit.
        public void PersistLbController(Component controller)
        {
            try
            {
                if (controller == null)
                    return;
                string scene = controller.gameObject.scene.name;
                if (scene != "MultiplayerLeaderboards")
                    return;
                // Only hijack the controller out of its scene when it was loaded
                // additively for provisioning. A normal visit must leave the scene's
                // own UI in place: moving its root Canvas to DontDestroyOnLoad
                // detaches it from the scene and the screen stays black. (The Start
                // callback fires while LoadingScreen is still the active scene, so
                // the provisioning flag — not the active scene name — is the signal.)
                if (!lbProvisioning)
                    return;
                var mb = controller as MonoBehaviour;
                if (mb == null)
                    return;
                // DontDestroyOnLoad only works on a root GameObject. Persist the
                // controller's scene root (not the nearest Canvas, which is often
                // nested and makes DDOL silently no-op, after which the scene
                // unload destroys the controller we needed).
                Transform canvasT = null;
                System.Text.StringBuilder chain = new System.Text.StringBuilder();
                for (Transform t = controller.transform; t != null; t = t.parent)
                {
                    chain.Append('/').Append(t.name);
                    if (canvasT == null && t.GetComponent<Canvas>() != null)
                        canvasT = t;
                }
                Transform sceneRoot = controller.transform.root;
                GameObject root = sceneRoot != null ? sceneRoot.gameObject : controller.gameObject;
                if (root == null)
                    return;
                if (lbPersistRoot != null && lbPersistRoot != root)
                {
                    try { UnityEngine.Object.Destroy(lbPersistRoot); } catch { }
                    lbPersistRoot = null;
                    lbPersistController = null;
                    lbPersistPanel = null;
                    lbPersistHidden = false;
                }
                UnityEngine.Object.DontDestroyOnLoad(root);
                lbPersistRoot = root;
                lbPersistController = mb;
                lbPersistHidden = false;
                try
                {
                    if (lbDirectPanelField != null)
                        lbPersistPanel = lbDirectPanelField.GetValue(controller) as Component;
                }
                catch { }
                if (!lbPersistLogged)
                {
                    lbPersistLogged = true;
                    pluginLog.LogInfo("[GhostLines] leaderboards: persisted root='" + root.name +
                        "' (canvas=" + (canvasT != null) + ") scene='" + scene + "' chain=" + chain);
                }
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: persist EX " + ex.Message);
            }
        }

        // Once the leaderboards scene has unloaded, stop the carried-over UI from
        // swallowing UI clicks. We must NOT deactivate the root — Unity stops
        // coroutines on inactive GameObjects, which would break the load
        // coroutine. Instead disable its Canvas + GraphicRaycaster so it cannot
        // raycast/render, while keeping it active for coroutines.
        private void TickLbPersistHide()
        {
            if (lbPersistRoot == null || lbPersistHidden)
                return;
            string scene = null;
            try { scene = SceneManager.GetActiveScene().name; } catch { }
            if (scene == "MultiplayerLeaderboards")
                return;
            try
            {
                foreach (var c in lbPersistRoot.GetComponentsInChildren<Canvas>(true))
                    c.enabled = false;
                foreach (var g in lbPersistRoot.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true))
                    g.enabled = false;
                foreach (var g in lbPersistRoot.GetComponentsInChildren<CanvasGroup>(true))
                {
                    g.blocksRaycasts = false;
                    g.interactable = false;
                }
                // If the carried-over hierarchy brought an EventSystem (the
                // MultiplayerLeaderboards scene has its own), it must not remain
                // Unity's current EventSystem — that hijacks all UI input in the
                // flight scene and stops chat from being focusable. Re-point to the
                // flight scene's EventSystem and disable the carried-over one.
                var carriedEs = lbPersistRoot.GetComponentsInChildren<EventSystem>(true);
                foreach (var es in carriedEs)
                {
                    if (es == null)
                        continue;
                    if (EventSystem.current == es)
                    {
                        EventSystem replacement = null;
                        var all = UnityEngine.Object.FindObjectsOfType<EventSystem>(true);
                        foreach (var other in all)
                        {
                            if (other != null && other != es && other.gameObject.activeInHierarchy)
                            {
                                replacement = other;
                                break;
                            }
                        }
                        if (replacement != null)
                        {
                            EventSystem.current = replacement;
                        }
                        else
                        {
                            // Nothing to hand off to: clear it rather than leaving
                            // current pointing at an EventSystem we are about to
                            // disable (which would kill all UI input).
                            EventSystem.current = null;
                            pluginLog.LogInfo("[GhostLines] leaderboards: no replacement EventSystem; cleared current before disabling carried one");
                        }
                    }
                    es.enabled = false;
                }
            }
            catch { }
            lbPersistHidden = true;
            pluginLog.LogInfo("[GhostLines] leaderboards: carried-over controller raycasters disabled (coroutines kept alive)");
        }

        // The carried-over root is never deactivated (so coroutines keep running);
        // this just guards against a stray deactivation that would freeze them.
        private void EnsureLbPersistActive()
        {
            if (lbPersistRoot == null)
                return;
            try
            {
                if (!lbPersistRoot.activeSelf)
                    lbPersistRoot.SetActive(true);
            }
            catch { }
        }

        // Finds the popup's CommunityLeaderboardKeyword (declared on its base type).
        private static object ReadPopupKeyword(Component popup)
        {
            if (popup == null)
                return null;
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            try
            {
                for (System.Type t = popup.GetType(); t != null && t != typeof(UnityEngine.MonoBehaviour); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(flags))
                    {
                        if (f.FieldType == LbKeywordType)
                        {
                            object v = f.GetValue(popup);
                            if (v != null)
                                return v;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // In the flight scene, upgrade the in-game popup's recording rows into a
        // single-select radio: ensure the game's own toggle is visible/usable and
        // wire it to the native replay-load path.
        private void TickLbPopupMultiSelect()
        {
            // The popup only exists in a flight/content scene; gate on that instead
            // of the MP pilot scan. mpInSession is false in a solo room (and after a
            // replay round-trip drops the target list), which used to stop the radio
            // controls from being enabled for the rest of the session.
            if (replayReady || !IsContentSceneActive())
            {
                if (lbPopupPresent || lbPopupDragSelecting)
                    ClearLbPopupState();
                return;
            }
            // Never touch selection/EventSystem while the player is typing in chat.
            if (IsChatTyping())
            {
                if (lbPopupDragSelecting)
                    EndLbPopupDrag();
                return;
            }
            // Drag must be polled every frame — do it before the rescan throttle.
            if (lbPopupPresent)
                TickLbPopupDrag(lbPopupRowsCache);
            if (Time.unscaledTime < lbPopupRadioNextAt)
                return;
            lbPopupRadioNextAt = Time.unscaledTime + 0.5f;
            string scene = null;
            try { scene = SceneManager.GetActiveScene().name; } catch { }
            if (scene == "MultiplayerLeaderboards")
                return;
            Component popup = FindActiveLbPopup();
            if (popup == null)
            {
                if (lbPopupPresent || lbPopupDragSelecting)
                    ClearLbPopupState();
                return;
            }
            lbPopupPresent = true;
            if (lbPopupToggleField == null)
                lbPopupToggleField = AccessTools.Field(typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "toggleRecording");
            if (lbPopupGroupField == null)
                lbPopupGroupField = AccessTools.Field(typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "groupToggle");
            var rows = GetLbPopupRows(popup);
            lbPopupRowsCache = rows;
            EnsureLbPopupLoadButton(popup);
            if (!lbPopupRowHierarchyDumped && rows.Count > 0)
            {
                lbPopupRowHierarchyDumped = true;
                DumpLbPopupRowHierarchy(rows[0]);
            }
            // Prune selections whose row was destroyed.
            for (int i = lbPopupSelected.Count - 1; i >= 0; i--)
                if (lbPopupSelected[i] == null)
                    lbPopupSelected.RemoveAt(i);
            foreach (var r in rows)
            {
                // The achievement date is useful on every row, including entries
                // without a downloadable recording (those have no toggle at all).
                EnsureLbPopupDateLabel(r);
                bool hasRec = false;
                try { hasRec = r.HasRecording; } catch { }
                var tog = lbPopupToggleField != null ? lbPopupToggleField.GetValue(r) as UnityEngine.UI.Toggle : null;
                if (tog == null)
                    continue;
                if (hasRec)
                {
                    if (!tog.gameObject.activeSelf)
                        tog.gameObject.SetActive(true);
                    tog.interactable = true;
                    var grp = lbPopupGroupField != null ? lbPopupGroupField.GetValue(r) as CanvasGroup : null;
                    if (grp != null)
                    {
                        grp.alpha = 1f;
                        grp.interactable = true;
                        grp.blocksRaycasts = true;
                    }
                }
                if (lbPopupWired.Add(tog))
                {
                    tog.isOn = false;
                    var capturedRow = r;
                    tog.onValueChanged.AddListener(new UnityEngine.Events.UnityAction<bool>(
                        on => OnLbPopupRowToggled(on, capturedRow)));
                }
            }
            // Drop labels whose row was destroyed.
            if (lbPopupDateLabels.Count > 0)
            {
                List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> deadDates = null;
                foreach (var kv in lbPopupDateLabels)
                {
                    if (kv.Key == null || kv.Value == null || kv.Value.Text == null)
                    {
                        if (deadDates == null)
                            deadDates = new List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry>();
                        deadDates.Add(kv.Key);
                    }
                }
                if (deadDates != null)
                    for (int i = 0; i < deadDates.Count; i++)
                        lbPopupDateLabels.Remove(deadDates[i]);
            }
            if (!lbPopupRadiosLogged && rows.Count > 0)
            {
                lbPopupRadiosLogged = true;
                pluginLog.LogInfo("[GhostLines] leaderboards: popup radios enabled rows=" + rows.Count);
            }
            if (!lbPopupHiddenLogged && lbPopupDateLabels.Count > 0)
            {
                lbPopupHiddenLogged = true;
                var sb = new System.Text.StringBuilder("[GhostLines] LBPOPUP dates:");
                int shown = 0, hiddenLogs = 0;
                foreach (var kv in lbPopupDateLabels)
                {
                    LbPopupDateLabel e = kv.Value;
                    if (e == null || e.Text == null)
                        continue;
                    if (e.Text.enabled)
                    {
                        shown++;
                        if (string.IsNullOrEmpty(e.Text.text) && hiddenLogs < 8)
                        {
                            hiddenLogs++;
                            sb.Append(" | EMPTY '").Append(e.Name != null ? e.Name.text : "?")
                              .Append("' {").Append(e.HiddenReason).Append('}');
                        }
                        continue;
                    }
                    if (hiddenLogs < 8)
                    {
                        hiddenLogs++;
                        sb.Append(" | HIDDEN '").Append(e.Name != null ? e.Name.text : "?")
                          .Append("' {").Append(e.HiddenReason).Append('}');
                    }
                }
                sb.Append(" shown=").Append(shown).Append(" hiddenLogged=").Append(hiddenLogs);
                pluginLog.LogInfo(sb.ToString());
            }
        }

        // Injects a small "time achieved" label just left of the row's score column.
        // Shows a relative age ("20m ago") normally; hover shows the full date.
        private void EnsureLbPopupDateLabel(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry row)
        {
            if (row == null)
                return;
            if (lbPopupScoreTextField == null)
                lbPopupScoreTextField = AccessTools.Field(
                    typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "txtScore");
            UnityEngine.UI.Text score = lbPopupScoreTextField != null
                ? lbPopupScoreTextField.GetValue(row) as UnityEngine.UI.Text
                : null;
            if (score == null)
                return;
            if (lbPopupRankTextField == null)
                lbPopupRankTextField = AccessTools.Field(
                    typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "txtRank");
            if (lbPopupNameTextField == null)
                lbPopupNameTextField = AccessTools.Field(
                    typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "txtPlayerName");
            if (lbPopupFriendField == null)
                lbPopupFriendField = AccessTools.Field(
                    typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), "imgFriendIcon");
            LbPopupDateLabel entry = null;
            if (!lbPopupDateLabels.TryGetValue(row, out entry) || entry == null || entry.Text == null)
            {
                try
                {
                    GameObject go = UnityEngine.Object.Instantiate(score.gameObject, score.transform.parent);
                    go.name = "LbAchievedDate";
                    var label = go.GetComponent<UnityEngine.UI.Text>();
                    if (label == null)
                    {
                        UnityEngine.Object.Destroy(go);
                        return;
                    }
                    // Hover is detected geometrically, so keep it non-raycastable to
                    // avoid stealing scroll/drag input from the list.
                    label.raycastTarget = false;
                    label.supportRichText = false;
                    label.alignment = TextAnchor.MiddleRight;
                    label.horizontalOverflow = HorizontalWrapMode.Overflow;
                    label.verticalOverflow = VerticalWrapMode.Overflow;
                    label.fontSize = Mathf.Max(10, score.fontSize - 4);
                    // Italic + slightly muted so it reads as a subtle caption next to
                    // the pilot, not another column of data.
                    label.fontStyle = FontStyle.Italic;
                    // A ContentSizeFitter on the clone would fight our explicit size.
                    var csf = go.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                    if (csf != null)
                        UnityEngine.Object.Destroy(csf);
                    // If the row is laid out by a group, exclude the label from it so
                    // the injected text can't reflow the native columns.
                    if (score.transform.parent != null &&
                        score.transform.parent.GetComponent<UnityEngine.UI.LayoutGroup>() != null)
                    {
                        var le = go.GetComponent<UnityEngine.UI.LayoutElement>();
                        if (le == null)
                            le = go.AddComponent<UnityEngine.UI.LayoutElement>();
                        le.ignoreLayout = true;
                    }
                    entry = new LbPopupDateLabel
                    {
                        Text = label,
                        PreferredFont = Mathf.Max(10, score.fontSize - 4),
                        MinFont = Mathf.Max(9, score.fontSize - 8)
                    };
                    lbPopupDateLabels[row] = entry;
                }
                catch (System.Exception ex)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LBPOPUP date label create EX " + ex.Message);
                    return;
                }
            }
            entry.Rank = lbPopupRankTextField != null
                ? lbPopupRankTextField.GetValue(row) as UnityEngine.UI.Text
                : null;
            entry.Name = lbPopupNameTextField != null
                ? lbPopupNameTextField.GetValue(row) as UnityEngine.UI.Text
                : null;
            entry.FriendIcon = lbPopupFriendField != null
                ? lbPopupFriendField.GetValue(row) as UnityEngine.UI.Image
                : null;
            entry.BaseColor = score.color;
            UpdateLbPopupDateLabelText(row, entry);
        }

        // Left/right edge of a Text's rendered glyphs within its own rect (relative
        // to the pivot), accounting for its alignment.
        private static float LbPopupTextLeftLocal(UnityEngine.UI.Text t, float textW)
        {
            RectTransform rt = t.rectTransform;
            float left = rt.rect.xMin;
            TextAnchor a = t.alignment;
            if (a == TextAnchor.MiddleCenter || a == TextAnchor.UpperCenter || a == TextAnchor.LowerCenter)
                left += (rt.rect.width - textW) * 0.5f;
            else if (a == TextAnchor.MiddleRight || a == TextAnchor.UpperRight || a == TextAnchor.LowerRight)
                left += rt.rect.width - textW;
            return left;
        }

        // Refreshes text + colour and re-places the label. Runs every popup tick, so
        // it also picks up layout/resolution changes.
        private void UpdateLbPopupDateLabelText(
            Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry row, LbPopupDateLabel entry)
        {
            if (row == null || entry == null || entry.Text == null)
                return;
            DateTime ts;
            try { ts = row.Timestamp; }
            catch (System.Exception tsEx)
            {
                entry.Text.text = "";
                entry.Text.enabled = false;
                if (!lbPopupNoTimestampLogged)
                {
                    lbPopupNoTimestampLogged = true;
                    string who = entry.Name != null ? entry.Name.text : "?";
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup timestamp read failed for '" + who + "': " + tsEx.Message);
                }
                return;
            }
            if (ts == DateTime.MinValue)
            {
                entry.Text.text = "";
                entry.Text.enabled = false;
                if (!lbPopupNoTimestampLogged)
                {
                    lbPopupNoTimestampLogged = true;
                    string who = entry.Name != null ? entry.Name.text : "?";
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup row has no timestamp (e.g. '" + who + "')");
                }
                return;
            }
            // Timestamps from the server are UTC (Unspecified kind); a local one
            // would otherwise read as hours in the future.
            DateTime now = ts.Kind == DateTimeKind.Local ? DateTime.Now : DateTime.UtcNow;
            TimeSpan age = now - ts;
            entry.Text.text = LbRelativeAge(age);
            RectTransform lrt = entry.Text.rectTransform;
            lrt.anchorMin = new Vector2(0f, 1f);
            lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0f, 0.5f);

            // Right after the pilot's placement (rank) number: "1  1w ago  [pic] name".
            float rankRight = -1f;
            float rankW = -1f;
            if (entry.Rank != null && entry.Rank.gameObject.activeInHierarchy)
            {
                try
                {
                    RectTransform rrt = entry.Rank.rectTransform;
                    rankW = Mathf.Max(1f, entry.Rank.preferredWidth);
                    rankRight = rrt.anchoredPosition.x + LbPopupTextLeftLocal(entry.Rank, rankW) + rankW;
                }
                catch { }
            }
            // The friend icon Image can stay active with a null sprite, which draws
            // nothing — only reserve its space when it really renders an icon.
            bool friendShown = false;
            try
            {
                UnityEngine.UI.Image fi = entry.FriendIcon;
                friendShown = fi != null && fi.gameObject.activeInHierarchy && fi.enabled &&
                    fi.sprite != null && fi.color.a > 0.05f;
            }
            catch { }
            float limit = friendShown ? LbPopupDateFriendMax : LbPopupDateAvatarMax;

            // Fit by shrinking the font one step at a time so the date keeps its spot
            // after the rank instead of vanishing.
            bool placed = false;
            float w = 0f;
            float x = 0f;
            for (int fs = Mathf.Max(entry.MinFont, entry.PreferredFont); fs >= entry.MinFont; fs--)
            {
                entry.Text.fontSize = fs;
                w = entry.Text.preferredWidth;
                if (w < 1f)
                    w = 52f;
                if (rankRight >= 0f)
                {
                    x = rankRight + 8f;
                    if (x + w <= limit)
                    {
                        placed = true;
                        break;
                    }
                    x = limit - w;
                    if (x >= rankRight + 6f)
                    {
                        placed = true;
                        break;
                    }
                }
                else
                {
                    x = limit - w;
                    if (x >= 14f)
                    {
                        placed = true;
                        break;
                    }
                }
            }
            if (!placed)
                entry.Text.fontSize = entry.MinFont;
            lrt.sizeDelta = new Vector2(w + 4f, lrt.sizeDelta.y);
            float rowY = -20f;
            try
            {
                if (entry.Rank != null)
                    rowY = entry.Rank.rectTransform.anchoredPosition.y;
            }
            catch { }
            lrt.anchoredPosition = new Vector2(x, rowY);
            entry.Text.enabled = placed;
            if (placed)
            {
                entry.HiddenReason = null;
            }
            else
            {
                string friendInfo = friendShown ? "yes" : "no";
                entry.HiddenReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "rankRight={0:F0} rankW={1:F0} w={2:F0} limit={3:F0} x={4:F0} friend={5}",
                    rankRight, rankW, w, limit, x, friendInfo);
            }
            if (!placed)
                entry.Hovered = false;
            entry.Text.color = LbDateAgeColor(age, entry.BaseColor);
        }

        // Row-space limits for the rank gap (from the prefab dump: friend icon starts
        // at x=85, avatar at x=110; leave a couple of px of breathing room).
        private const float LbPopupDateFriendMax = 83f;
        private const float LbPopupDateAvatarMax = 108f;

        // Per-frame hover handling for the injected date labels (the popup tick only
        // runs twice a second, which is too coarse for hover feedback).
        private void TickLbPopupDateHover()
        {
            if (lbPopupDateLabels.Count == 0)
                return;
            bool anyHover = false;
            foreach (var kv in lbPopupDateLabels)
            {
                LbPopupDateLabel entry = kv.Value;
                if (entry == null || entry.Text == null || !entry.Text.enabled)
                    continue;
                bool hover = false;
                try
                {
                    RectTransform rt = entry.Text.rectTransform;
                    Canvas canvas = entry.Text.canvas;
                    Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                        ? canvas.worldCamera
                        : null;
                    hover = RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
                }
                catch { }
                entry.Hovered = hover;
                if (!hover)
                    continue;
                DateTime ts;
                try { ts = kv.Key.Timestamp; } catch { continue; }
                if (ts == DateTime.MinValue)
                    continue;
                anyHover = true;
                lbPopupDateHoverText = (ts.Kind == DateTimeKind.Local ? ts : ts.ToLocalTime())
                    .ToString("dddd, MMMM d yyyy  HH:mm");
                lbPopupDateHoverPos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            }
            if (!anyHover)
                lbPopupDateHoverText = null;
        }

        // Full-date tooltip drawn in IMGUI so the scroll viewport can't clip it, and
        // so hovering never widens/overlaps the inline relative-age label.
        private void DrawLbPopupDateTooltip()
        {
            if (string.IsNullOrEmpty(lbPopupDateHoverText) || whiteTex == null)
                return;
            EnsureSettingsStyles();
            if (lbPopupDateTooltipStyle == null)
            {
                lbPopupDateTooltipStyle = new GUIStyle(GUI.skin.label);
                lbPopupDateTooltipStyle.fontSize = 13;
                lbPopupDateTooltipStyle.alignment = TextAnchor.MiddleLeft;
                lbPopupDateTooltipStyle.wordWrap = false;
                lbPopupDateTooltipStyle.normal.textColor = new Color(0.95f, 0.95f, 0.97f);
            }
            GUIContent content = TempContent(lbPopupDateHoverText);
            Vector2 size = lbPopupDateTooltipStyle.CalcSize(content);
            float w = size.x + 14f;
            float h = size.y + 6f;
            float x = lbPopupDateHoverPos.x + 14f;
            float y = lbPopupDateHoverPos.y + 18f;
            if (x + w > Screen.width - 4f)
                x = lbPopupDateHoverPos.x - w - 10f;
            if (y + h > Screen.height - 4f)
                y = lbPopupDateHoverPos.y - h - 8f;
            Rect rect = new Rect(x, y, w, h);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.66f);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = old;
            GUI.Label(new Rect(rect.x + 7f, rect.y + (h - size.y) * 0.5f, size.x, size.y), content, lbPopupDateTooltipStyle);
        }

        private static string LbRelativeAge(TimeSpan age)
        {
            double secs = age.TotalSeconds;
            if (secs < 0) secs = 0;
            if (secs < 60) return "just now";
            double mins = age.TotalMinutes;
            if (mins < 60) return (int)mins + "m ago";
            double hours = age.TotalHours;
            if (hours < 24) return (int)hours + "h ago";
            double days = age.TotalDays;
            if (days < 7) return (int)days + "d ago";
            if (days < 30) return (int)(days / 7) + "w ago";
            if (days < 365) return (int)(days / 30) + "mo ago";
            return (int)(days / 365) + "y ago";
        }

        // Most recent = the game's own score colour (dark on the light popup), then
        // progressively greyer with age. Log scale so hours/days are distinguishable
        // rather than all reading as "grey" within the first month.
        private static Color LbDateAgeColor(TimeSpan age, Color baseColor)
        {
            double days = age.TotalDays;
            if (days < 0)
                days = 0;
            float t = Mathf.Clamp01((float)(Math.Log10(1.0 + days) / Math.Log10(366.0)));
            float a = baseColor.a <= 0.01f ? 0.9f : baseColor.a * 0.9f;
            Color grey = new Color(0.64f, 0.64f, 0.68f, Mathf.Max(0.4f, a * 0.75f));
            // Start from a softened version of the game's colour so the freshest date
            // doesn't look like a second copy of the score/rank text.
            Color fresh = Color.Lerp(new Color(baseColor.r, baseColor.g, baseColor.b, a), grey, 0.22f);
            return Color.Lerp(fresh, grey, t);
        }

        // One-shot geometry dump so the injected date can be positioned exactly in
        // the row's free space across resolutions/heights.
        private void DumpLbPopupRowHierarchy(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry row)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                RectTransform rowRt = row.transform as RectTransform;
                sb.Append("[GhostLines] LBPOPUP row '").Append(row.name).Append("'");
                if (rowRt != null)
                    sb.Append(" rect=").Append(rowRt.rect).Append(" sizeDelta=").Append(rowRt.sizeDelta)
                      .Append(" anchor=").Append(rowRt.anchorMin).Append("..").Append(rowRt.anchorMax);
                Transform par = row.transform.parent;
                if (par != null)
                    sb.Append(" parent='").Append(par.name).Append("'");
                pluginLog.LogInfo(sb.ToString());
                DumpLbPopupField(row, "txtRank");
                DumpLbPopupField(row, "txtPlayerName");
                DumpLbPopupField(row, "txtScore");
                DumpLbPopupField(row, "toggleRecording");
                DumpLbPopupField(row, "imgPlayerAvatar");
                DumpLbPopupField(row, "imgFriendIcon");
                DumpLbPopupChildren(row.transform, 0);
            }
            catch (System.Exception ex)
            {
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] LBPOPUP dump EX " + ex.Message);
            }
        }

        private void DumpLbPopupField(UnityEngine.Object row, string fieldName)
        {
            try
            {
                var f = AccessTools.Field(typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry), fieldName);
                Component c = f != null ? f.GetValue(row) as Component : null;
                if (c == null)
                {
                    pluginLog.LogInfo("[GhostLines] LBPOPUP  " + fieldName + " = null");
                    return;
                }
                RectTransform rt = c.transform as RectTransform;
                string info = "[GhostLines] LBPOPUP  " + fieldName + " path='" + PathOf(c.transform) + "'";
                if (rt != null)
                    info += " anchor=" + rt.anchorMin + ".." + rt.anchorMax + " pivot=" + rt.pivot +
                        " pos=" + rt.anchoredPosition + " sizeDelta=" + rt.sizeDelta + " rect=" + rt.rect;
                pluginLog.LogInfo(info);
            }
            catch { }
        }

        private void DumpLbPopupChildren(Transform t, int depth)
        {
            if (t == null || depth > 3)
                return;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (c == null)
                    continue;
                string comps = "";
                if (c.GetComponent<UnityEngine.UI.Text>() != null) comps += "Text ";
                if (c.GetComponent<UnityEngine.UI.Image>() != null) comps += "Image ";
                if (c.GetComponent<UnityEngine.UI.Toggle>() != null) comps += "Toggle ";
                if (c.GetComponent<UnityEngine.UI.Button>() != null) comps += "Button ";
                if (c.GetComponent<UnityEngine.UI.LayoutGroup>() != null) comps += "Layout ";
                if (c.GetComponent<UnityEngine.UI.ContentSizeFitter>() != null) comps += "CSF ";
                if (c.GetComponent<UnityEngine.UI.LayoutElement>() != null) comps += "LE ";
                RectTransform rt = c as RectTransform;
                string info = "[GhostLines] LBPOPUP  " + new string(' ', depth * 2) + c.name +
                    " [" + comps.Trim() + "] active=" + c.gameObject.activeSelf;
                if (rt != null)
                    info += " pos=" + rt.anchoredPosition + " sizeDelta=" + rt.sizeDelta + " rect=" + rt.rect;
                pluginLog.LogInfo(info);
                DumpLbPopupChildren(c, depth + 1);
            }
        }

        private static string PathOf(Transform t)
        {
            if (t == null)
                return "?";
            string p = t.name;
            for (Transform cur = t.parent; cur != null && p.Length < 120; cur = cur.parent)
                p = cur.name + "/" + p;
            return p;
        }

        private void DestroyLbPopupLoadButton()
        {
            if (lbPopupLoadButton != null)
            {
                try { UnityEngine.Object.Destroy(lbPopupLoadButton); } catch { }
                lbPopupLoadButton = null;
            }
            lbPopupLoadButtonPopup = null;
        }

        // Full teardown of the popup overlay state. Critically, restores the
        // ScrollRect that drag-select disables and clears the cached reference —
        // otherwise the leaderboard list stays unscrollable for the rest of the
        // session after the popup closes mid-drag.
        private void ClearLbPopupState()
        {
            if (lbPopupDragSelecting)
                EndLbPopupDrag();
            lbPopupPresent = false;
            lbPopupWired.Clear();
            lbPopupRadiosLogged = false;
            lbPopupNoTimestampLogged = false;
            lbPopupHiddenLogged = false;
            lbPopupDateLabels.Clear();
            lbPopupDateHoverText = null;
            lbPopupSelected.Clear();
            lbPopupRowsCache.Clear();
            lbPopupCachedScrollRect = null;
            DestroyLbPopupLoadButton();
        }

        // Clones the popup's own bottom button so the injected "Load Replay" button
        // matches size/aesthetic exactly and sits next to it.
        private void EnsureLbPopupLoadButton(Component popup)
        {
            if (popup == null)
                return;
            if (lbPopupLoadButton != null && lbPopupLoadButtonPopup == popup)
            {
                UpdateLbPopupLoadButtonLabel();
                return;
            }
            DestroyLbPopupLoadButton();
            try
            {
                var field = AccessTools.Field(popup.GetType(), "btnResume");
                var src = field != null ? field.GetValue(popup) as UnityEngine.UI.Button : null;
                if (src == null)
                {
                    var btns = popup.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                    if (btns != null && btns.Length > 0)
                        src = btns[0];
                }
                if (src == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup load button — no source button to clone");
                    return;
                }
                GameObject go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
                go.name = "LbLoadReplayButton";
                try { go.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1); } catch { }
                // If the row isn't auto-laid-out, shift the clone clear of its
                // source so they don't stack on top of each other.
                try
                {
                    bool hasLayout = src.transform.parent != null &&
                        src.transform.parent.GetComponent<UnityEngine.UI.LayoutGroup>() != null;
                    if (!hasLayout && go.transform is RectTransform cloneRt && src.transform is RectTransform srcRt)
                    {
                        float w = srcRt.rect.width > 0f ? srcRt.rect.width : 120f;
                        cloneRt.anchoredPosition = srcRt.anchoredPosition + new Vector2(w + 8f, 0f);
                    }
                }
                catch { }
                SetLbButtonLabel(go, "Load Replay");
                var btn = go.GetComponent<UnityEngine.UI.Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(new UnityEngine.Events.UnityAction(LoadLbPopupSelection));
                    btn.interactable = true;
                }
                lbPopupLoadButton = go;
                lbPopupLoadButtonPopup = popup;
                UpdateLbPopupLoadButtonLabel();
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load button injected (cloned '" + src.name +
                    "' parent='" + (src.transform.parent != null ? src.transform.parent.name : "?") + "')");
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load button EX " + ex.Message);
            }
        }

        private void UpdateLbPopupLoadButtonLabel()
        {
            if (lbPopupLoadButton == null)
                return;
            int count = lbPopupSelected.Count;
            SetLbButtonLabel(lbPopupLoadButton, count > 0 ? "Load Replay (" + count + ")" : "Load Replay");
            var btn = lbPopupLoadButton.GetComponent<UnityEngine.UI.Button>();
            if (btn != null)
                btn.interactable = count > 0;
        }

        private static void SetLbButtonLabel(GameObject go, string text)
        {
            if (go == null)
                return;
            try
            {
                foreach (var t in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                {
                    t.text = text;
                    break;
                }
            }
            catch { }
        }

        // Called from the popup's Awake postfix: registers the live instance so the
        // poller never has to scene-scan for it in the common case.
        public void NoteLbPopupInstance(Component popup)
        {
            if (popup != null)
                lbPopupAwakeInstance = popup;
        }

        private Component FindActiveLbPopup()
        {
            try
            {
                // Fast path: the popup told us it exists from its own Awake.
                Component cached = lbPopupAwakeInstance;
                if (cached != null)
                {
                    if (!cached.gameObject.activeInHierarchy)
                        return null;
                    // Never treat the carried-over leaderboard hierarchy as a live
                    // popup: it lives in the flight scene only so its controller can
                    // run the load coroutine. Its rows would otherwise drive our
                    // multiselect/drag every frame and clear the EventSystem
                    // selection (breaking chat focus).
                    if (lbPersistRoot != null && cached.transform.IsChildOf(lbPersistRoot.transform))
                        return null;
                    return cached;
                }
                // Once the Awake hook is installed it is authoritative — a null cache
                // simply means no popup is alive, so no scan is needed.
                if (GhostLinesPlugin.lbPopupHookInstalled)
                    return null;
                // Fallback for a failed hook: a popup that already existed. Throttled
                // so it can't hitch big maps.
                if (Time.unscaledTime < lbPopupRescanAt)
                    return null;
                lbPopupRescanAt = Time.unscaledTime + 5f;
                var objs = UnityEngine.Object.FindObjectsOfType(LbPopupType);
                if (objs != null)
                {
                    foreach (var o in objs)
                    {
                        var c = o as Component;
                        if (c == null || !c.gameObject.activeInHierarchy)
                            continue;
                        if (lbPersistRoot != null && c.transform.IsChildOf(lbPersistRoot.transform))
                            continue;
                        lbPopupAwakeInstance = c;
                        return c;
                    }
                }
            }
            catch { }
            return null;
        }

        private List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> GetLbPopupRows(Component popup)
        {
            var list = new List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry>();
            try
            {
                var all = popup.GetComponentsInChildren<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry>(true);
                foreach (var e in all)
                    if (e != null && e.gameObject.activeInHierarchy)
                        list.Add(e);
            }
            catch { }
            // Top-to-bottom ordering for predictable drag ranges.
            list.Sort((a, b) =>
            {
                try { return b.transform.position.y.CompareTo(a.transform.position.y); }
                catch { return 0; }
            });
            return list;
        }

        private void OnLbPopupRowToggled(bool on, Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry row)
        {
            if (lbPopupBusy || row == null)
                return;
            lbPopupBusy = true;
            try
            {
                if (on)
                {
                    if (!lbPopupSelected.Contains(row))
                        lbPopupSelected.Add(row);
                    while (lbPopupSelected.Count > LbPopupMaxSelect)
                    {
                        var oldest = lbPopupSelected[0];
                        lbPopupSelected.RemoveAt(0);
                        if (oldest != null)
                        {
                            var t = lbPopupToggleField != null ? lbPopupToggleField.GetValue(oldest) as UnityEngine.UI.Toggle : null;
                            if (t != null && t.isOn)
                                t.isOn = false;
                        }
                    }
                }
                else
                {
                    lbPopupSelected.Remove(row);
                }
            }
            finally
            {
                lbPopupBusy = false;
                UpdateLbPopupLoadButtonLabel();
            }
        }

        private void TickLbPopupDrag(List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                if (lbPopupDragSelecting)
                    EndLbPopupDrag();
                return;
            }
            if (lbPopupToggleField == null)
                return;
            Vector2 mouse = Input.mousePosition;
            int HitRow()
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var t = lbPopupToggleField.GetValue(rows[i]) as UnityEngine.UI.Toggle;
                    if (t == null)
                        continue;
                    var canvas = t.GetComponentInParent<Canvas>();
                    Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
                    var rt = t.GetComponent<RectTransform>();
                    if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, mouse, cam))
                        return i;
                    // Accept the whole row rect so dragging anywhere along the row works.
                    var rowRt = rows[i].transform as RectTransform;
                    if (rowRt != null && RectTransformUtility.RectangleContainsScreenPoint(rowRt, mouse, cam))
                        return i;
                }
                return -1;
            }
            if (Input.GetMouseButtonDown(0))
            {
                int hit = HitRow();
                if (hit >= 0)
                {
                    lbPopupDragSelecting = true;
                    lbPopupDragProcessed.Clear();
                    lbPopupDragStartIndex = hit;
                    lbPopupDragLastIndex = hit;
                    var t = lbPopupToggleField.GetValue(rows[hit]) as UnityEngine.UI.Toggle;
                    lbPopupDragStartWasSelected = t != null && t.isOn;
                    ClearLbEventSelection();
                    if (lbPopupCachedScrollRect == null)
                        lbPopupCachedScrollRect = rows[hit].GetComponentInParent<ScrollRect>();
                    if (lbPopupCachedScrollRect != null)
                    {
                        lbPopupScrollRectWasEnabled = lbPopupCachedScrollRect.enabled;
                        lbPopupCachedScrollRect.enabled = false;
                    }
                }
            }
            if (lbPopupDragSelecting && Input.GetMouseButton(0))
            {
                int cur = HitRow();
                if (cur < 0)
                {
                    float best = float.MaxValue;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var rowRt = rows[i].transform as RectTransform;
                        if (rowRt == null)
                            continue;
                        Vector3[] c = cornerScratchA;
                        rowRt.GetWorldCorners(c);
                        float yMin = Mathf.Min(c[0].y, c[2].y);
                        float yMax = Mathf.Max(c[0].y, c[2].y);
                        if (mouse.y >= yMin && mouse.y <= yMax) { cur = i; break; }
                        float d = Mathf.Min(Mathf.Abs(mouse.y - yMin), Mathf.Abs(mouse.y - yMax));
                        if (d < best) { best = d; cur = i; }
                    }
                }
                if (cur != lbPopupDragStartIndex)
                {
                    if (cur >= 0 && lbPopupDragLastIndex >= 0)
                    {
                        int minR = Mathf.Min(lbPopupDragLastIndex, cur);
                        int maxR = Mathf.Max(lbPopupDragLastIndex, cur);
                        if (cur >= lbPopupDragLastIndex)
                            for (int r = minR; r <= maxR; r++) ApplyLbPopupDrag(rows[r]);
                        else
                            for (int r = maxR; r >= minR; r--) ApplyLbPopupDrag(rows[r]);
                        lbPopupDragLastIndex = cur;
                    }
                }
                else if (cur >= 0)
                {
                    lbPopupDragLastIndex = cur;
                }
            }
            if (lbPopupDragSelecting && (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0)))
                EndLbPopupDrag();
        }

        private void ApplyLbPopupDrag(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry row)
        {
            if (row == null)
                return;
            var t = lbPopupToggleField != null ? lbPopupToggleField.GetValue(row) as UnityEngine.UI.Toggle : null;
            if (t == null)
                return;
            if (lbPopupDragProcessed.Contains(t.GetInstanceID()))
                return;
            lbPopupDragProcessed.Add(t.GetInstanceID());
            if (lbPopupDragStartWasSelected)
            {
                if (t.isOn)
                    t.isOn = false;
            }
            else
            {
                // When full, the toggle listener evicts the oldest selection —
                // mirror the official leaderboard drag behaviour instead of
                // refusing the new row.
                if (!t.isOn)
                    t.isOn = true;
            }
        }

        private void EndLbPopupDrag()
        {
            lbPopupDragSelecting = false;
            lbPopupDragStartIndex = -1;
            lbPopupDragLastIndex = -1;
            ClearLbEventSelection();
            if (lbPopupCachedScrollRect != null)
                lbPopupCachedScrollRect.enabled = lbPopupScrollRectWasEnabled;
        }

        private void LoadLbPopupSelection()
        {
            if (lbPopupSelected.Count == 0)
                return;
            Component popup = FindActiveLbPopup();
            object keyword = ReadPopupKeyword(popup);
            if (keyword == null)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load — no keyword on popup");
                return;
            }
            var entries = new List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry>();
            foreach (var r in lbPopupSelected)
                if (r != null)
                    entries.Add(r);
            if (entries.Count == 0)
                return;
            TriggerLbPopupLoad(entries, keyword);
        }

        // Invokes the carried-over controller's native load coroutine for the
        // selected pilots.
        private void TriggerLbPopupLoad(List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> rows, object keyword)
        {
            // When the leaderboards screen is the live scene its own controller is
            // already running: use it directly instead of provisioning a copy.
            Component controller = lbPersistController;
            if (controller == null && GhostLinePoller.IsLeaderboardSceneLoaded())
                controller = lbForceController ?? leaderboardController;
            if (controller == null)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load needs the controller — provisioning in background");
                lbPendingPopupEntries = rows;
                lbPendingPopupKeyword = keyword;
                ProvisionLbController();
                return;
            }
            MonoBehaviour mb = controller as MonoBehaviour;
            if (mb == null)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load — controller is not a MonoBehaviour");
                return;
            }
            if (!EnsureLbDirectReflection())
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load reflection incomplete");
                return;
            }
            // The environment dropdown is only populated a moment after the
            // controller starts, and the game builds the replay level from it. If
            // we fire the load before it's populated the level uses whatever env
            // the dropdown last held (a different map/gates). Wait for it first.
            StartCoroutine(RunLbPopupLoadWhenReady(controller, mb, rows, keyword));
        }

        private System.Collections.IEnumerator RunLbPopupLoadWhenReady(
            Component controller, MonoBehaviour mb,
            List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> rows, object keyword)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 4f)
            {
                if (ApplyLbPopupEnvironment())
                    break;
                yield return null;
            }
            InvokeLbPopupLoad(controller, mb, rows, keyword);
        }

        private void InvokeLbPopupLoad(
            Component controller, MonoBehaviour mb,
            List<Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry> rows, object keyword)
        {
            try
            {
                string kwInfo = "?";
                try
                {
                    var kwContent = AccessTools.Property(LbKeywordType, "ContentID");
                    var kwDisplay = AccessTools.Property(LbKeywordType, "DisplayName");
                    kwInfo = "contentId='" + (kwContent != null ? kwContent.GetValue(keyword) as string : "?") +
                        "' name='" + (kwDisplay != null ? kwDisplay.GetValue(keyword) as string : "?") + "'";
                }
                catch { }
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load selecting " + kwInfo +
                    " pilots=" + rows.Count);
                EnsureLbPersistActive();
                var listType = typeof(List<>).MakeGenericType(
                    typeof(Liftoff.Multiplayer.Leaderboards.CommunityLeaderboardEntry));
                var list = (System.Collections.IList)Activator.CreateInstance(listType);
                foreach (var r in rows)
                    if (r != null)
                        list.Add(r);
                object routine = lbDirectLoadCoroutine.Invoke(controller, new object[] { keyword, list });
                if (routine is System.Collections.IEnumerator en)
                {
                    mb.StartCoroutine(en);
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup load started via " +
                        (controller == lbPersistController ? "carried-over controller" : "live leaderboard controller"));
                }
                else
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup load — coroutine invoke failed");
                }
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup load EX " + ex.Message);
            }
        }

        // The game's Level builder takes the environment variant from the
        // controller's environment dropdown (not from the track), so point that
        // dropdown at the room's environment without firing its change callback.
        // Returns true when it is safe to start the load: either the dropdown is
        // set (or already correct), there is nothing to set, or it is unusable.
        // Returns false only while the options list has not populated yet, so the
        // caller can wait a frame and retry.
        private bool ApplyLbPopupEnvironment()
        {
            try
            {
                Component ctrl = lbPersistController ?? lbForceController ?? leaderboardController;
                if (ctrl == null)
                    return true;
                // The level-setup log name is the authoritative env name; the room
                // property and the captured live Level are fallbacks.
                string env = !string.IsNullOrEmpty(wsEnvironmentName) ? wsEnvironmentName : ReadRoomEnvironment();
                if (string.IsNullOrEmpty(env))
                {
                    try
                    {
                        Level lv = lbLiveLevel as Level;
                        if (lv != null && lv.Environment != null)
                            env = lv.Environment.name;
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(env))
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup env — no room environment known; leaving dropdown as-is");
                    return true;
                }
                var field = AccessTools.Field(ctrl.GetType(), "dropdownEnvironmentFilter");
                var dd = field != null ? field.GetValue(ctrl) as Dropdown : null;
                if (dd == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup env — no environment dropdown on controller (field=" +
                        (field != null) + ")");
                    return true;
                }
                if (dd.options == null || dd.options.Count == 0)
                    return false; // options not populated yet — caller retries
                int best = -1;
                for (int i = 0; i < dd.options.Count; i++)
                    if (dd.options[i] != null && LooseNameMatch(dd.options[i].text, env))
                    {
                        best = i;
                        break;
                    }
                if (best < 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[GhostLines] leaderboards: popup env '").Append(env).Append("' not among ")
                        .Append(dd.options.Count).Append(" options, selected=").Append(dd.value).Append(":");
                    for (int i = 0; i < dd.options.Count && i < 40; i++)
                        sb.Append(" '").Append(dd.options[i] != null ? dd.options[i].text : "?").Append("'");
                    pluginLog.LogInfo(sb.ToString());
                    return true;
                }
                if (dd.value == best)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: popup env already '" + env + "'");
                    return true;
                }
                var noNotify = AccessTools.Method(typeof(Dropdown), "SetValueWithoutNotify");
                if (noNotify != null)
                    noNotify.Invoke(dd, new object[] { best });
                else
                    dd.value = best;
                try { dd.RefreshShownValue(); } catch { }
                pluginLog.LogInfo("[GhostLines] leaderboards: popup env dropdown set to '" +
                    (dd.options[best] != null ? dd.options[best].text : "?") + "' (want env='" + env + "')");
                return true;
            }
            catch (System.Exception ex)
            {
                pluginLog.LogInfo("[GhostLines] leaderboards: popup env set EX " + ex.Message);
                return true;
            }
        }

        // Loads the leaderboards scene additively just long enough for its
        // controller to persist itself, then removes the additive copy and runs any
        // pending popup load. Keeps the player in flight.
        private void ProvisionLbController()
        {
            if (lbProvisioning)
                return;
            lbProvisioning = true;
            StartCoroutine(ProvisionLbControllerRoutine());
        }

        private System.Collections.IEnumerator ProvisionLbControllerRoutine()
        {
            const string sceneName = "MultiplayerLeaderboards";
            try
            {
                AsyncOperation op = null;
                try { op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive); }
                catch (System.Exception ex)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: provision load EX " + ex.Message);
                }
                if (op == null)
                {
                    pluginLog.LogInfo("[GhostLines] leaderboards: provision failed — additive load returned null");
                    lbPendingPopupEntries = null;
                    lbPendingPopupKeyword = null;
                    yield break;
                }
                float t0 = Time.unscaledTime;
                while (!op.isDone && Time.unscaledTime - t0 < 10f)
                    yield return null;
                float t1 = Time.unscaledTime;
                while (lbPersistController == null && Time.unscaledTime - t1 < 10f)
                    yield return null;
                bool ok = lbPersistController != null;
                pluginLog.LogInfo("[GhostLines] leaderboards: provision complete (controller=" + ok + ")");
                try { SceneManager.UnloadSceneAsync(sceneName); } catch { }
                var entries = lbPendingPopupEntries;
                var kw = lbPendingPopupKeyword;
                lbPendingPopupEntries = null;
                lbPendingPopupKeyword = null;
                if (ok && entries != null && kw != null)
                    TriggerLbPopupLoad(entries, kw);
            }
            finally
            {
                // Always release the provisioning gate, even if the coroutine is
                // stopped or an exception escapes: a stuck true flag would let the
                // next real leaderboard visit hijack the live scene's Canvas (black screen).
                lbProvisioning = false;
            }
        }
    }
}
