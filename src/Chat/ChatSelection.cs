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
        private void UpdateTextFieldFocusFlag()
        {
            bool focused = false;
            try
            {
                if (mpChatInputField != null && mpChatInputField.isFocused)
                    focused = true;
                if (!focused && EventSystem.current != null)
                {
                    GameObject go = EventSystem.current.currentSelectedGameObject;
                    if (go != null &&
                        (go.GetComponent<UnityEngine.UI.InputField>() != null ||
                         go.GetComponentInParent<UnityEngine.UI.InputField>() != null))
                        focused = true;
                }
            }
            catch { }
            GhostLinesPlugin.textFieldFocused = focused;
            if (focused && GhostLinesPlugin.inputGateActive &&
                Time.frameCount - GhostLinesPlugin.textFieldFocusedLoggedFrame > 300)
            {
                GhostLinesPlugin.textFieldFocusedLoggedFrame = Time.frameCount;
                pluginLog.LogWarning("[GhostLines] text field focused while input gate active — releasing gate so typing works");
            }
        }

        private bool IsChatTyping()
        {
            return mpChatInputField != null && mpChatInputField.isFocused;
        }

        private void ScheduleChatSelectionCorrection(int anchor, int focus)
        {
            if (anchor < 0 || focus < 0)
                return;
            if (chatSelectionCoroutine != null)
                StopCoroutine(chatSelectionCoroutine);
            chatSelectionCoroutine = CorrectChatSelectionAtEndOfFrame(anchor, focus);
            StartCoroutine(chatSelectionCoroutine);
        }

        private System.Collections.IEnumerator CorrectChatSelectionAtEndOfFrame(int anchor, int focus)
        {
            yield return new WaitForEndOfFrame();
            if (mpChatInputField == null || !mpChatInputField.isActiveAndEnabled)
            {
                chatSelectionCoroutine = null;
                yield break;
            }
            if (anchor < 0 || focus < 0)
            {
                chatSelectionCoroutine = null;
                yield break;
            }
            mpChatInputField.caretPosition = focus;
            mpChatInputField.selectionAnchorPosition = anchor;
            mpChatInputField.selectionFocusPosition = focus;
            chatSelectionCoroutine = null;
        }

        private void FocusChatField(InputField field)
        {
            if (field == null)
                return;
            if (field.isFocused)
                return;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != field.gameObject)
                EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
        }

        private void DrawChatSelection(ChatSelEntry en, int lo, int hi, Rect clipRect, bool haveClip)
        {
            if (en == null || en.chars == null)
                return;
            if (hi > en.len) hi = en.len;
            if (hi > en.chars.Count) hi = en.chars.Count;
            if (lo < 0) lo = 0;
            if (hi <= lo)
                return;
            Color old = GUI.color;
            GUI.color = new Color(0.25f, 0.55f, 1f, 0.35f);
            Rect run = new Rect(0f, 0f, 0f, 0f);
            bool hasRun = false;
            for (int ci = lo; ci < hi; ci++)
            {
                Rect q = CharBoxToScreen(en.rect, en.pivot, en.chars, ci, en.lineH);
                if (haveClip)
                    q = IntersectRect(q, clipRect);
                if (q.height > 2f)
                    q = new Rect(q.x, q.y + 1f, q.width, q.height - 1f);
                if (q.width < 0.5f || q.height < 0.5f)
                    continue;
                if (!hasRun)
                {
                    run = q;
                    hasRun = true;
                }
                else
                {
                    float ovTop = Mathf.Max(run.y, q.y);
                    float ovBot = Mathf.Min(run.yMax, q.yMax);
                    float minH = Mathf.Min(run.height, q.height);
                    bool sameLine = minH <= 0f || Mathf.Abs((run.y + run.height * 0.5f) - (q.y + q.height * 0.5f)) < minH * 0.25f;
                    if (sameLine)
                    {
                        float xMin = Mathf.Min(run.xMin, q.xMin);
                        float xMax = Mathf.Max(run.xMax, q.xMax);
                        float yMin = Mathf.Min(run.yMin, q.yMin);
                        float yMax = Mathf.Max(run.yMax, q.yMax);
                        run = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
                    }
                    else
                    {
                        if (haveClip)
                            run = IntersectRect(run, clipRect);
                        if (!haveClip || (run.width >= 0.5f && run.height >= 0.5f))
                            GUI.DrawTexture(run, whiteTex);
                        run = q;
                    }
                }
            }
            if (hasRun)
            {
                if (haveClip)
                    run = IntersectRect(run, clipRect);
                if (!haveClip || (run.width >= 0.5f && run.height >= 0.5f))
                    GUI.DrawTexture(run, whiteTex);
            }
            GUI.color = old;
        }

        private static int IndexOfChatSelEntry(List<ChatSelEntry> entries, Text t)
        {
            for (int i = 0; i < entries.Count; i++)
                if (ReferenceEquals(entries[i].t, t))
                    return i;
            return -1;
        }

        private void ClearMpChatSelection()
        {
            mpSelAnchorT = mpSelFocusT = null;
            mpSelAnchorI = mpSelFocusI = -1;
        }

        private ChatSelEntry FindNearestChatEntry(List<ChatSelEntry> entries, Vector2 mouse, Rect clipRect, bool haveClip)
        {
            ChatSelEntry best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < entries.Count; i++)
            {
                Rect r = entries[i].rect;
                if (haveClip && !r.Overlaps(clipRect))
                    continue;
                float dy = mouse.y < r.y ? r.y - mouse.y : (mouse.y > r.yMax ? mouse.y - r.yMax : 0f);
                if (dy < bestD)
                {
                    bestD = dy;
                    best = entries[i];
                }
            }
            return best;
        }

        private bool MpChatSelectionActive(List<ChatSelEntry> entries)
        {
            if (mpSelAnchorT == null || mpSelFocusT == null)
                return false;
            int ia = IndexOfChatSelEntry(entries, mpSelAnchorT);
            int ib = IndexOfChatSelEntry(entries, mpSelFocusT);
            if (ia < 0 || ib < 0)
                return false;
            if (ia == ib)
                return mpSelAnchorI != mpSelFocusI;
            return true;
        }

        private void DrawMpChatSelectionCoverage(List<ChatSelEntry> entries, Rect clipRect, bool haveClip)
        {
            int ia = IndexOfChatSelEntry(entries, mpSelAnchorT);
            int ib = IndexOfChatSelEntry(entries, mpSelFocusT);
            if (ia < 0 || ib < 0)
                return;
            if (ia == ib)
            {
                DrawChatSelection(entries[ia], Mathf.Min(mpSelAnchorI, mpSelFocusI), Mathf.Max(mpSelAnchorI, mpSelFocusI) + 1, clipRect, haveClip);
                return;
            }
            int f = Mathf.Min(ia, ib);
            int l = Mathf.Max(ia, ib);
            int startIdx = (ia == f) ? mpSelAnchorI : mpSelFocusI;
            int endIdx = ((ib == l) ? mpSelFocusI : mpSelAnchorI) + 1;
            ChatSelEntry fe = entries[f];
            DrawChatSelection(fe, startIdx, fe.len, clipRect, haveClip);
            for (int k = f + 1; k < l; k++)
            {
                ChatSelEntry mid = entries[k];
                if (haveClip)
                {
                    Rect expanded = new Rect(clipRect.x - 4f, clipRect.y - 4f, clipRect.width + 8f, clipRect.height + 8f);
                    if (!mid.rect.Overlaps(expanded))
                        continue;
                }
                DrawChatSelection(mid, 0, mid.len, clipRect, haveClip);
            }
            ChatSelEntry le = entries[l];
            DrawChatSelection(le, 0, endIdx, clipRect, haveClip);
        }

        private static void AppendChatUnits(System.Text.StringBuilder sb, ChatSelEntry en, int lo, int hi)
        {
            if (sb == null || en == null || en.units == null)
                return;
            if (lo < 0) lo = 0;
            if (hi > en.len) hi = en.len;
            for (int ci = lo; ci < hi; ci++)
                sb.Append(en.units[ci]);
        }

        private void CopySelectedChatText(List<ChatSelEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return;
            int ia = IndexOfChatSelEntry(entries, mpSelAnchorT);
            int ib = IndexOfChatSelEntry(entries, mpSelFocusT);
            if (ia < 0 || ib < 0)
                return;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (ia == ib)
            {
                AppendChatUnits(sb, entries[ia], Mathf.Min(mpSelAnchorI, mpSelFocusI), Mathf.Max(mpSelAnchorI, mpSelFocusI) + 1);
            }
            else
            {
                int f = Mathf.Min(ia, ib);
                int l = Mathf.Max(ia, ib);
                int startIdx = (ia == f) ? mpSelAnchorI : mpSelFocusI;
                int endIdx = ((ib == l) ? mpSelFocusI : mpSelAnchorI) + 1;
                AppendChatUnits(sb, entries[f], startIdx, entries[f].len);
                for (int k = f + 1; k <= l; k++)
                {
                    sb.Append('\n');
                    AppendChatUnits(sb, entries[k], 0, k == l ? endIdx : entries[k].len);
                }
            }
            if (sb.Length == 0)
                return;
            GUIUtility.systemCopyBuffer = sb.ToString();
        }

        private int NearestChatChar(Text t, ChatTextState st, IList<UICharInfo> chars, float lineH, Vector2 mouse, Rect clipRect, int len, Rect textBox, Vector2 pivot)
        {
            int best = -1;
            float bestD = float.MaxValue;
            if (len > chars.Count) len = chars.Count;
            for (int ci = 0; ci < len; ci++)
            {
                Rect q = CharBoxToScreen(textBox, pivot, chars, ci, lineH);
                if (clipRect.width > 1f)
                {
                    Rect qi = IntersectRect(q, clipRect);
                    if (qi.width < 1f)
                        continue;
                }
                Vector2 c = q.center;
                float d = (c - mouse).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = ci;
                }
            }
            return best;
        }

        private static Rect TextToScreenRect(Text t)
        {
            t.rectTransform.GetWorldCorners(mpUiCorners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(null, mpUiCorners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(null, mpUiCorners[2]);
            float x = Mathf.Min(a.x, b.x);
            float y = Screen.height - Mathf.Max(a.y, b.y);
            return new Rect(x, y, Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private static Rect CharBoxToScreen(Rect textBox, Vector2 pivot, IList<UICharInfo> chars, int idx, float lineH)
        {
            UICharInfo c = chars[idx];
            float sx = textBox.x + pivot.x * textBox.width + c.cursorPos.x;
            float syTop = textBox.y + (1f - pivot.y) * textBox.height - c.cursorPos.y;
            return new Rect(sx, syTop, Mathf.Max(c.charWidth, 0f), Mathf.Max(lineH, 0f));
        }

        private static readonly Vector3[] mpUiCorners = new Vector3[4];

        private Rect GetChatViewportScreenRect()
        {
            if (mpChatScrollRect == null || mpChatScrollRect.viewport == null)
                return new Rect(0f, 0f, -1f, -1f);
            mpChatScrollRect.viewport.GetWorldCorners(mpUiCorners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(null, mpUiCorners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(null, mpUiCorners[2]);
            float x = Mathf.Min(a.x, b.x);
            float y = Screen.height - Mathf.Max(a.y, b.y);
            return new Rect(x, y, Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private InGameMenu cachedInGameMenu = null;
        private float nextInGameMenuScanAt = 0f;

        // The game menu singleton can become unresolved (duplicate instances after
        // our scene juggling), so also scan for the live instance directly. Cheap
        // because it is cached and only rescanned a few times per second.
        private bool IsGameMenuOpenCached()
        {
            float now = Time.unscaledTime;
            // Fast path: if the singleton resolves, it already knows the state and
            // the scene-wide scan can be skipped entirely. Only fall back to the
            // cached instance then the scan when the singleton is unresolved
            // (duplicate instances after our scene juggling).
            try
            {
                if (InGameMenu.Exists())
                {
                    InGameMenu m = InGameMenu.use;
                    if (m != null)
                    {
                        cachedInGameMenu = m;
                        return m.IsMenuOpen;
                    }
                }
            }
            catch { }
            if (cachedInGameMenu != null)
            {
                try { return cachedInGameMenu.IsMenuOpen; } catch { }
            }
            if (now >= nextInGameMenuScanAt)
            {
                nextInGameMenuScanAt = now + 0.5f;
                cachedInGameMenu = null;
                try
                {
                    var all = UnityEngine.Object.FindObjectsOfType<InGameMenu>(true);
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null)
                        {
                            cachedInGameMenu = all[i];
                            break;
                        }
                    }
                }
                catch { cachedInGameMenu = null; }
            }
            try { return cachedInGameMenu != null && cachedInGameMenu.IsMenuOpen; }
            catch { return false; }
        }

        private float lbPopupOpenScanAt = 0f;
        private bool lbPopupOpenCached = false;

        // True while the in-game community leaderboard popup is on screen (whether
        // reached from the ESC menu or elsewhere). FindActiveLbPopup does a
        // scene-wide FindObjectsOfType, which is expensive on big maps, so cache it
        // for a short interval instead of scanning every frame.
        private bool IsLbPopupOpenCached()
        {
            float now = Time.unscaledTime;
            if (now < lbPopupOpenScanAt)
                return lbPopupOpenCached;
            lbPopupOpenScanAt = now + 0.25f;
            lbPopupOpenCached = FindActiveLbPopup() != null;
            return lbPopupOpenCached;
        }

        private Rect GetChatWindowScreenRect()
        {
            if (mpChatWindowRoot == null)
                return new Rect(0f, 0f, -1f, -1f);
            if (!mpChatWindowRoot.gameObject.activeInHierarchy)
                return new Rect(0f, 0f, -1f, -1f);
            mpChatWindowRoot.GetWorldCorners(mpUiCorners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(null, mpUiCorners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(null, mpUiCorners[2]);
            float x = Mathf.Min(a.x, b.x);
            float y = Screen.height - Mathf.Max(a.y, b.y);
            return new Rect(x, y, Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private static Rect IntersectRect(Rect a, Rect b)
        {
            float x = Mathf.Max(a.x, b.x);
            float y = Mathf.Max(a.y, b.y);
            float x2 = Mathf.Min(a.xMax, b.xMax);
            float y2 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x, y, Mathf.Max(0f, x2 - x), Mathf.Max(0f, y2 - y));
        }
    }
}
