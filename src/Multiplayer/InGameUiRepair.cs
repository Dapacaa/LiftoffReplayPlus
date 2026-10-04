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
        // After a direct in-room reload the cursor can be left locked/captured,
        // which makes the game's Rewired pointer input module ignore every UI
        // click (chat can't be focused). Restore a usable cursor once we're back
        // in the flight scene.
        private void TickMpCursorRestore()
        {
            if (!lbAwaitCursorRestore)
                return;
            if (replayReady)
                return;
            string scene = null;
            try { scene = SceneManager.GetActiveScene().name; } catch { }
            if (string.IsNullOrEmpty(scene) || scene == "LoadingScreen" ||
                scene == "MultiplayerLeaderboards" || scene == "MultiplayerMenu")
                return;
            try
            {
                if (Cursor.lockState != CursorLockMode.None)
                    Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible)
                    Cursor.visible = true;
            }
            catch { }
            lbAwaitCursorRestore = false;
        }

        // The game's InGameUI is a LugusSingletonExisting; its CacheInstance
        // refuses to resolve (logs an error) when more than one exists. Our direct
        // in-room level reload reloads the flight/replay scene, whose own InGameUI
        // is added alongside the DontDestroyOnLoad one, so chat breaks. Restore the
        // singleton invariant: keep one InGameUI, destroy the rest, and re-point
        // the singleton at the survivor.
        private void TickInGameUiSingleton()
        {
            float now = Time.unscaledTime;
            // Only a repair for our own in-room reloads, which open the urgent
            // window. Outside it the game may legitimately hold two InGameUIs (a
            // scene transition), and destroying one would break it.
            if (now >= chatDedupUrgentUntil || now < nextChatDedupAt)
                return;
            nextChatDedupAt = now + 0.5f;
            string active = SceneManager.GetActiveScene().name;
            // Mid-load both copies are expected; wait for the flight scene. Keep
            // the window open while loading so a long (workshop) load can't use
            // it up before the new scene exists.
            if (string.IsNullOrEmpty(active) || active == "LoadingScreen")
            {
                chatDedupUrgentUntil = Mathf.Max(chatDedupUrgentUntil, now + 20f);
                return;
            }
            chatDedupSw.Restart();
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<InGameUI>(true);
                if (all == null || all.Length <= 1)
                    return;
                // Prefer an InGameUI that actually lives in the active scene (the
                // freshly loaded one); the DontDestroyOnLoad legacy instance is the
                // one to drop. Fall back to the game's current singleton, then [0].
                InGameUI keep = null;
                InGameUI current = null;
                try { current = InGameUI.use; } catch { }
                foreach (var igu in all)
                {
                    if (igu == null)
                        continue;
                    string sname = igu.gameObject.scene.IsValid() ? igu.gameObject.scene.name : "?";
                    if (sname == active)
                    {
                        keep = igu;
                        break;
                    }
                }
                if (keep == null)
                    keep = current != null ? current : all[0];
                for (int i = 0; i < all.Length; i++)
                {
                    var igu = all[i];
                    if (igu == null || igu == keep)
                        continue;
                    UnityEngine.Object.Destroy(igu.gameObject);
                }
                try { InGameUI.Change(keep); } catch { }
                // Re-resolve chat against the survivor.
                mpChatMessageParent = null;
                mpChatInputField = null;
                mpChatScrollRect = null;
                mpChatWindowRoot = null;
                mpChatLastChildCount = -1;
                mpChatStates.Clear();
            }
            catch { }
            finally
            {
                float ms = (float)chatDedupSw.Elapsed.TotalMilliseconds;
                if (ms > 3f && now >= nextChatDedupCostLogAt)
                {
                    nextChatDedupCostLogAt = now + 30f;
                    pluginLog.LogInfo("[GhostLines] InGameUI dedup scan took " + ms.ToString("F1") + "ms");
                }
            }
        }

        // Selectable.interactable (the property) can read True while
        // IsInteractable() returns False, because the latter also checks the cached
        // m_GroupsAllowInteraction flag. That flag is only refreshed by
        // OnCanvasGroupChanged/OnEnable; if an ancestor CanvasGroup was
        // non-interactable when the field was enabled (e.g. during the replay), the
        // field refuses activation forever even after the group is restored. Repair
        // the ancestor chain, refresh the flag, and force it true as a last resort.
        private void EnsureChatSelectableInteractive()
        {
            InputField f = mpChatInputField;
            if (f == null)
                return;
            try
            {
                for (Transform t = f.transform; t != null; t = t.parent)
                {
                    var cg = t.GetComponent<CanvasGroup>();
                    if (cg == null)
                        continue;
                    if (!cg.interactable) cg.interactable = true;
                    if (!cg.blocksRaycasts) cg.blocksRaycasts = true;
                    if (cg.alpha < 1f) cg.alpha = 1f;
                }
                var ocg = AccessTools.Method(typeof(Selectable), "OnCanvasGroupChanged");
                if (ocg != null)
                {
                    try { ocg.Invoke(f, null); } catch { }
                }
                if (!f.IsInteractable())
                {
                    var gf = AccessTools.Field(typeof(Selectable), "m_GroupsAllowInteraction");
                    if (gf != null)
                        gf.SetValue(f, true);
                }
            }
            catch { }
        }
    }
}
