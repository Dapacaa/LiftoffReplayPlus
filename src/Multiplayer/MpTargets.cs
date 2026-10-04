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
        private InGamePlayerActivityIndicator[] GetUiIndicators()
        {
            if (mpUiIndicatorCache == null || Time.unscaledTime - mpUiIndicatorCacheAt > 15f)
            {
                mpUiIndicatorCacheAt = Time.unscaledTime;
                try
                {
                    mpUiIndicatorCache = Resources.FindObjectsOfTypeAll<InGamePlayerActivityIndicator>();
                }
                catch (System.Exception ex)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP uicolor find EX " + ex);
                    mpUiIndicatorCache = null;
                }
            }
            return mpUiIndicatorCache;
        }

        private bool TryResolveMpPilotColorUi(object owner, string playerName, out Color color)
        {
            color = Color.white;
            InGamePlayerActivityIndicator[] indicators = GetUiIndicators();
            if (indicators == null)
                return false;
            for (int i = 0; i < indicators.Length; i++)
            {
                InGamePlayerActivityIndicator ind = indicators[i];
                if (ind == null)
                    continue;
                object p = null;
                try { p = ind.Player; } catch { }
                string primary = "";
                try
                {
                    Text t = AccessTools.Field(typeof(InGamePlayerActivityIndicator), "txtPrimaryInfo").GetValue(ind) as Text;
                    if (t != null)
                        primary = t.text ?? "";
                }
                catch { }
                bool match = (p != null && owner != null && object.ReferenceEquals(p, owner)) ||
                    (primary.Trim().Length > 0 &&
                     primary.Trim().StartsWith(playerName, System.StringComparison.OrdinalIgnoreCase));
                if (!match)
                    continue;
                try
                {
                    Image bg = AccessTools.Field(typeof(InGamePlayerActivityIndicator), "imgBackground").GetValue(ind) as Image;
                    if (bg != null && bg.color.a > 0.5f)
                    {
                        color = bg.color;
                        return true;
                    }
                    Image img = AccessTools.Field(typeof(InGamePlayerActivityIndicator), "imgPlayer").GetValue(ind) as Image;
                    if (img != null && img.color.a > 0.5f)
                    {
                        color = img.color;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private void DrawMultiplayerNames()
        {
            if (replayReady || !mpInSession)
                return;
            // Pure drawing (no input handlers) — skip Layout/input events entirely.
            if (Event.current.type != EventType.Repaint)
                return;
            Camera main = MainCameraCached();
            if (main == null)
                return;
            EnsureSettingsStyles();
            if (whiteTex == null)
            {
                whiteTex = new Texture2D(1, 1);
                whiteTex.SetPixel(0, 0, Color.white);
                whiteTex.Apply();
            }
            Color oldColor = GUI.color;
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                MultiplayerPipTarget t = multiplayerPipTargets[i];
                if (t == null || t.root == null || !t.tagVisible)
                    continue;
                Vector3 dronePos = t.mount != null ? t.mount.position : t.root.position;
                Vector3 lineBase = dronePos + Vector3.up * 0.35f;
                Vector3 anchor = dronePos + Vector3.up * 1.15f;
                Vector3 spBase = main.WorldToScreenPoint(lineBase);
                Vector3 spAnchor = main.WorldToScreenPoint(anchor);
                if (spBase.z <= 0.01f || spAnchor.z <= 0.01f)
                    continue;
                Vector2 basePx = new Vector2(spBase.x, Screen.height - spBase.y);
                Vector2 anchorPx = new Vector2(spAnchor.x, Screen.height - spAnchor.y);
                Color pilotColor = t.pilotColor;
                Vector2 dir = anchorPx - basePx;
                float len = dir.magnitude;
                if (len > 1f)
                {
                    Matrix4x4 oldMatrix = GUI.matrix;
                    Vector2 nd = dir / len;
                    float ang = Mathf.Atan2(nd.y, nd.x) * Mathf.Rad2Deg;
                    GUIUtility.RotateAroundPivot(ang, basePx);
                    GUI.color = pilotColor;
                    GUI.DrawTexture(new Rect(basePx.x, basePx.y - 1f, len, 2f), whiteTex);
                    GUI.matrix = oldMatrix;
                }
                if (mpNameLabelStyle == null)
                {
                    mpNameLabelStyle = new GUIStyle(GUI.skin.label);
                    mpNameLabelStyle.fontSize = 13;
                    mpNameLabelStyle.fontStyle = FontStyle.Bold;
                    mpNameLabelStyle.alignment = TextAnchor.MiddleCenter;
                    mpNameLabelStyle.normal.textColor = Color.white;
                }
                Rect labelRect = new Rect(anchorPx.x - 80f, anchorPx.y - 25f, 160f, 20f);
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.Label(new Rect(labelRect.x + 1f, labelRect.y + 1f, labelRect.width, labelRect.height), t.playerName, mpNameLabelStyle);
                GUI.color = pilotColor;
                GUI.Label(labelRect, t.playerName, mpNameLabelStyle);
            }
            GUI.color = oldColor;
        }

        private bool DrawMultiplayerTarget(Rect rect, bool selected, float alpha, bool blocked, int column, int rowIndex)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover && !blocked)
            {
                mpRadioStartColumn = column;
                mpRadioStartRow = rowIndex;
                mpRadioDragSelecting = true;
                mpRadioDragMoved = false;
                mpRadioStartValue = selected;
                mpRadioLastIndex = rowIndex;
                mpRadioLastColumn = column;
                mpRadioProcessed.Clear();
            }
            Color old = GUI.color;
            if (selected)
            {
                GUI.color = new Color(0.15f, 0.65f, 1f, 0.95f * alpha);
                GUI.DrawTexture(rect, targetFillTex);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(rect, targetDotTex);
            }
            else
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f, alpha * (hover ? 1f : 0.7f));
                GUI.DrawTexture(rect, targetRingTex);
            }
            GUI.color = old;
            if (e.type == EventType.MouseUp && e.button == 0 && hover && !mpRadioDragMoved && !blocked)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private void HandleMultiplayerRadioDrag(int rowIndex, int column, Rect rect)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDrag || e.button != 0)
                return;
            if (!mpRadioDragSelecting)
                return;
            if (!rect.Contains(e.mousePosition))
                return;
            if (rowIndex == mpRadioLastIndex && column == mpRadioLastColumn)
                return;
            mpRadioDragMoved = true;
            int minRow = Mathf.Min(mpRadioStartRow, rowIndex);
            int maxRow = Mathf.Max(mpRadioStartRow, rowIndex);
            int minCol = Mathf.Min(mpRadioStartColumn, column);
            int maxCol = Mathf.Max(mpRadioStartColumn, column);
            bool target = !mpRadioStartValue;
            for (int r = minRow; r <= maxRow; r++)
            {
                for (int c = minCol; c <= maxCol; c++)
                {
                    if (mpRadioProcessed.Add(r * 16 + c))
                        ApplyMultiplayerRadioState(r, c, target);
                }
            }
            mpRadioLastIndex = rowIndex;
            mpRadioLastColumn = column;
        }

        // The name-tags key in a room: flips "Names above Drones". Turning it on
        // with every pilot's tag radio off turns them all on, so the key always
        // shows names. Session-only, like the radios (the replay pref is untouched).
        private void ToggleMultiplayerNames()
        {
            mpShowDroneNames = !mpShowDroneNames;
            if (!mpShowDroneNames || multiplayerPipTargets.Count == 0)
                return;
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                if (multiplayerPipTargets[i] != null && multiplayerPipTargets[i].tagVisible)
                    return;
            }
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                if (multiplayerPipTargets[i] != null)
                    multiplayerPipTargets[i].tagVisible = true;
            }
            SyncShowNameTagsFromTargets();
        }

        // Leaving MP: drop the radios' session mirror and go back to the saved flag.
        private void RestoreSavedNameTags()
        {
            if (showNameTagsConfig != null)
                GhostLinesPlugin.showNameTags = showNameTagsConfig.Value;
        }

        private void SyncShowNameTagsFromTargets()
        {
            if (multiplayerPipTargets.Count == 0)
                return;
            bool any = false;
            for (int i = 0; i < multiplayerPipTargets.Count; i++)
            {
                MultiplayerPipTarget t = multiplayerPipTargets[i];
                if (t != null && t.tagVisible)
                {
                    any = true;
                    break;
                }
            }
            // Session mirror only (keeps the N key and settings toggle in step with
            // the radios); the saved replay Name Tags preference is left alone.
            GhostLinesPlugin.showNameTags = any;
        }

        private void ApplyMultiplayerRadioState(int rowIndex, int column, bool value)
        {
            if (rowIndex < 0 || rowIndex >= multiplayerPipTargets.Count)
                return;
            MultiplayerPipTarget t = multiplayerPipTargets[rowIndex];
            if (t == null)
                return;
            if (column == 0)
            {
                t.tagVisible = value;
                SyncShowNameTagsFromTargets();
                return;
            }
            if (column == 1)
            {
                bool hasInstance = false;
                for (int s = 0; s < stickCamInstances.Count; s++)
                {
                    if (stickCamInstances[s].mpTarget == t)
                    {
                        hasInstance = true;
                        break;
                    }
                }
                if (value && !hasInstance)
                    AddStickCamInstance(null, t);
                else if (!value && hasInstance)
                    RemoveStickCamInstance(null, t);
                return;
            }
            if (column == 2)
            {
                SetMpLiveGraphOpen(t, value);
                return;
            }
            bool open = false;
            for (int j = 0; j < multiplayerPipWindows.Count; j++)
            {
                if (multiplayerPipWindows[j].target == t)
                {
                    open = true;
                    break;
                }
            }
            if (open != value)
                ToggleMultiplayerPip(t);
        }

        private class ChatTextState
        {
            public string rawOriginal;
            public string lastApplied;
            public string displayText;
            public string[] visUnits;
            public string[] altUnits;
            // Cached text-generator output. Rebuilding this via TextGenerator.Populate
            // on every Repaint was the dominant chat cost while hovering; the glyph
            // positions are text-local, so they only need refreshing when the text or
            // rect size changes (plus a slow safety pass).
            public UICharInfo[] genChars;
            public int genVc;
            public float genLineH;
            public Vector2 genRectSize;
            public string genText;
            public float genAt;
        }

        private class StyleRun
        {
            public int visStart;
            public int visEnd;
            public string color;
            public bool bold;
            public bool italic;
            public int size;
        }

        private class ChatInputState
        {
            public string plainText = "";
            public List<StyleRun> runs = new List<StyleRun>();
            public int[] rawToVis = new int[1];
            public int[] visToRaw = new int[1];
            public string rawOriginal = null;
        }

        private class ChatSelEntry
        {
            public Text t;
            public ChatTextState st;
            public string[] units;
            public int len;
            public IList<UICharInfo> chars;
            public float lineH;
            public Rect rect;
            public Vector2 pivot;
        }

        private readonly List<ChatSelEntry> mpSelEntries = new List<ChatSelEntry>();

        private RectTransform mpChatWindowRoot = null;

        private float nextChatDedupAt = 0f;
        // The dedup scan only runs briefly after our own in-room level reload
        // (the only thing that can duplicate InGameUI); it never runs otherwise.
        private float chatDedupUrgentUntil = 0f;
        private float nextChatDedupCostLogAt = 0f;
        private readonly System.Diagnostics.Stopwatch chatDedupSw = new System.Diagnostics.Stopwatch();

        private bool lbAwaitCursorRestore = false;
    }
}
