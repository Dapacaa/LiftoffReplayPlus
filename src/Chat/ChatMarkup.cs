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
        private void UpdateChatTracking()
        {
            if (Time.unscaledTime < mpChatScanAt)
                return;
            mpChatScanAt = Time.unscaledTime + 1f;
            // Tracking a chat from a scene we've left (the in-flight chat can
            // persist into the menus): drop it so the current scene's chat, e.g.
            // the waiting room's, is found instead.
            if (mpChatMessageParent != null && (mpChatWindowRoot == null || ChatFromOtherScene()))
                ReleaseChatTracking();
            if (mpChatMessageParent == null)
            {
                // No chat can exist outside a multiplayer room, and nothing was ever
                // tracked: skip the per-second hierarchy find in menus/solo replays.
                // Gate on the Photon room, not mpInSession: that flag needs other
                // pilots' drones, so it is false in a solo room and chat (and its
                // format menu) was never tracked there.
                if (!PhotonInRoom() && mpChatStates.Count == 0)
                {
                    mpChatScanAt = Time.unscaledTime + 5f;
                    return;
                }
                // The in-flight chat path only in flight; in the menus a leftover
                // in-flight chat would be picked, dropped, and picked again.
                GameObject win = IsContentSceneActive()
                    ? GameObject.Find("InGameUI(Clone)/AspectRatio/ChatPanel/ChatWindow")
                    : null;
                Liftoff.Multiplayer.Chat.ChatWindowPanel panel = win != null
                    ? win.GetComponent<Liftoff.Multiplayer.Chat.ChatWindowPanel>()
                    : FindWaitingRoomChatPanel();
                if (panel == null)
                {
                    // Chat UI gone (left the room / main-menu replay): release all
                    // stale tracking so the per-frame draw path early-outs instead
                    // of walking dead entries forever.
                    ReleaseChatTracking();
                    return;
                }
                if (panel == null)
                    return;
                try
                {
                    mpChatMessageParent = AccessTools.Field(typeof(Liftoff.Multiplayer.Chat.ChatWindowPanel), "messageParent").GetValue(panel) as RectTransform;
                    mpChatInputField = AccessTools.Field(typeof(Liftoff.Multiplayer.Chat.ChatWindowPanel), "fieldUserMessage").GetValue(panel) as InputField;
                    if (mpChatInputField != null && mpChatInputField.textComponent != null)
                        mpChatInputField.textComponent.supportRichText = true;
                    mpChatScrollRect = AccessTools.Field(typeof(Liftoff.Multiplayer.Chat.ChatWindowPanel), "scrollRectMessages").GetValue(panel) as ScrollRect;
                    if (mpChatScrollRect != null)
                        mpChatScrollOrigEnabled = mpChatScrollRect.enabled;
                    mpChatWindowRoot = panel.transform as RectTransform;
                }
                catch { }
                if (mpChatMessageParent == null)
                    return;
                mpChatLastChildCount = -1;
                EnsureChatSelectableInteractive();
            }
            List<Text> dead = null;
            foreach (KeyValuePair<Text, ChatTextState> kv in mpChatStates)
            {
                if (kv.Key == null || kv.Value == null)
                {
                    if (dead == null)
                        dead = new List<Text>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++)
                    mpChatStates.Remove(dead[i]);
            }
            // GetComponentsInChildren allocates and walks the chat hierarchy: only do the
            // full registration scan when messages were added/removed, plus a slow safety pass.
            if (mpChatMessageParent.childCount != mpChatLastChildCount || Time.unscaledTime >= mpChatRescanAt)
            {
                mpChatLastChildCount = mpChatMessageParent.childCount;
                mpChatRescanAt = Time.unscaledTime + 10f;
                Text[] texts = mpChatMessageParent.GetComponentsInChildren<Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    Text t = texts[i];
                    if (t == null || mpChatStates.ContainsKey(t))
                        continue;
                    mpChatStates[t] = new ChatTextState();
                }
            }
            foreach (KeyValuePair<Text, ChatTextState> kv in mpChatStates)
            {
                Text t = kv.Key;
                ChatTextState st = kv.Value;
                if (t == null)
                    continue;
                string cur = t.text;
                if (cur == null)
                    cur = "";
                if (cur == st.lastApplied)
                    continue;
                if (mpSelAnchorT == t || mpSelFocusT == t)
                {
                    mpSelAnchorT = mpSelFocusT = null;
                    mpSelAnchorI = mpSelFocusI = -1;
                }
                ParseChatText(t, st);
            }
        }

        // In the menus: a chat not in the active scene is a leftover in-flight
        // one. In flight: only the in-flight chat (under InGameUI, the same one
        // the path lookup finds) counts, so a waiting-room chat whose menu scene
        // is still loaded gets swapped out.
        private bool ChatFromOtherScene()
        {
            if (!IsContentSceneActive())
                return mpChatWindowRoot.gameObject.scene != SceneManager.GetActiveScene();
            return mpChatWindowRoot.root.name != "InGameUI(Clone)";
        }

        private void ReleaseChatTracking()
        {
            if (mpChatStates.Count > 0)
                mpChatStates.Clear();
            mpChatMessageParent = null;
            mpChatWindowRoot = null;
            mpChatInputField = null;
            mpChatScrollRect = null;
            if (mpSelAnchorT != null || mpSelFocusT != null || mpSelDragging)
                ClearMpChatSelection();
        }

        // The waiting room's chat is the same ChatWindowPanel under a different
        // hierarchy. Only looked for outside a flight scene (the menu scene is
        // small, so the scan is cheap), and only the one in the active scene.
        private static Liftoff.Multiplayer.Chat.ChatWindowPanel FindWaitingRoomChatPanel()
        {
            if (IsContentSceneActive())
                return null;
            try
            {
                Scene active = SceneManager.GetActiveScene();
                var panels = UnityEngine.Object.FindObjectsOfType<Liftoff.Multiplayer.Chat.ChatWindowPanel>();
                for (int i = 0; i < panels.Length; i++)
                {
                    var p = panels[i];
                    if (p != null && p.gameObject.activeInHierarchy && p.gameObject.scene == active)
                        return p;
                }
            }
            catch { }
            return null;
        }

        private static string GenericStripChatTags(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return System.Text.RegularExpressions.Regex.Replace(s, "</?[A-Za-z][^<>]{0,63}>", "");
        }

        private static string[] BuildChatUnits(string s)
        {
            List<string> units = new List<string>(s.Length);
            int k = 0;
            while (k < s.Length)
            {
                if (k + 1 < s.Length && char.IsSurrogatePair(s, k))
                {
                    units.Add(s.Substring(k, 2));
                    k += 2;
                }
                else
                {
                    units.Add(s.Substring(k, 1));
                    k++;
                }
            }
            return units.ToArray();
        }

        private void ParseChatText(Text t, ChatTextState st)
        {
            string raw = t.text;
            if (raw == null)
                raw = "";
            st.rawOriginal = raw;
            string stripped = StripChatTags(raw);
            string altStripped = GenericStripChatTags(raw);
            st.visUnits = BuildChatUnits(stripped);
            st.altUnits = BuildChatUnits(altStripped);
            st.displayText = stripped;
            st.lastApplied = raw;
        }

        private void DrawChatOverlays()
        {
            if (mpChatStates.Count == 0)
                return;
            perfChatN = mpChatStates.Count;
            // A replay loaded from the main menu has no chat (we never joined a
            // Photon room), but ChatTextState entries tracked during an earlier MP
            // session can linger. The chat window is destroyed then, so bail out
            // before the expensive selection-entry rebuild; otherwise the stale
            // entries (and a possibly stuck selection) keep costing every frame.
            if (mpChatWindowRoot == null || !mpChatWindowRoot.gameObject.activeInHierarchy)
            {
                if (mpSelAnchorT != null || mpSelFocusT != null || mpSelDragging)
                    ClearMpChatSelection();
                mpFormatUiRect = Rect.zero;
                return;
            }
            // Chat only exists on the in-flight HUD. The tracked chat texts can
            // outlive the flight scene (persisted menus), so never draw the chat
            // overlays or the "T" colour/format button outside a content scene,
            // nor while the game's pause/ESC menu is open (its leaderboard popup
            // etc. would otherwise have the button float on top).
            chatDiagSw.Restart();
            // In flight, or the chat belongs to the scene we're in (the waiting
            // room); never a leftover in-flight chat while in the menus.
            bool gateContent = GhostLinePoller.IsContentSceneActive() ||
                (mpChatWindowRoot != null && mpChatWindowRoot.gameObject.scene == SceneManager.GetActiveScene());
            bool gateMenu = IsGameMenuOpenCached();
            bool gatePopup = IsLbPopupOpenCached();
            perfChatGateMs += (float)chatDiagSw.Elapsed.TotalMilliseconds;
            if (!gateContent || gateMenu || gatePopup)
            {
                mpFormatUiRect = Rect.zero;
                return;
            }
            Event e = Event.current;
            Rect clipRect = GetChatViewportScreenRect();
            bool haveClip = clipRect.width > 1f;
            Vector2 mouse = e.mousePosition;
            bool copyPressed = e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.C;
            bool shiftCopy = copyPressed && e.shift;
            bool inputFocused = mpChatInputField != null && mpChatInputField.isFocused;
            if (mpChatInputField != null && mpChatInputField.textComponent != null)
            {
                if (!mpChatInputField.textComponent.supportRichText)
                    mpChatInputField.textComponent.supportRichText = true;
                if (mpChatInputField.textComponent.verticalOverflow != VerticalWrapMode.Overflow)
                    mpChatInputField.textComponent.verticalOverflow = VerticalWrapMode.Overflow;
                if (mpChatInputField.textComponent.horizontalOverflow != HorizontalWrapMode.Overflow)
                    mpChatInputField.textComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            RepairChatInputMarkup();
            ChatInputState inSt = GetOrParseChatInputStyle();
            if (inSt != null && mpChatInputField != null && mpChatInputField.isFocused)
            {
                try
                {
                    int sa = mpChatInputField.selectionAnchorPosition;
                    int sf = mpChatInputField.selectionFocusPosition;
                    if (sa != sf && Time.frameCount - mpChatApplyFrame > 3
                        && sa < inSt.rawToVis.Length && sf < inSt.rawToVis.Length)
                    {
                        int a = inSt.rawToVis[Mathf.Min(sa, sf)];
                        int b = inSt.rawToVis[Mathf.Max(sa, sf)];
                        if (b > a)
                        {
                            mpStickyVisLo = a;
                            mpStickyVisHi = b;
                            mpStickyPlainText = inSt.plainText;
                        }
                    }
                }
                catch { }
            }
            if (!mpSelDragging && mpSelScrollLocked && mpChatScrollRect != null)
            {
                mpChatScrollRect.enabled = mpChatScrollOrigEnabled;
                mpSelScrollLocked = false;
            }
            List<ChatSelEntry> entries = mpSelEntries;
            bool mpSelActive = mpSelAnchorT != null || mpSelFocusT != null || mpSelDragging;
            bool chatInteractive = mpSelActive || (haveClip && clipRect.Contains(mouse));
            // Full text relayout is expensive: only rebuild the entry list when a
            // selection is in progress or while hovering on the Repaint pass. Other
            // events reuse the cached rects for hit-testing.
            perfChatSel = mpSelActive;
            perfChatHover = chatInteractive;
            if (mpSelActive || (e.type == EventType.Repaint && chatInteractive))
            {
                chatDiagSw.Restart();
                int ei = 0;
            foreach (KeyValuePair<Text, ChatTextState> kv in mpChatStates)
            {
                Text t = kv.Key;
                ChatTextState st = kv.Value;
                if (t == null || !t.gameObject.activeInHierarchy)
                    continue;
                if (st.displayText == null || st.visUnits == null || st.altUnits == null || t.text != st.rawOriginal)
                    ParseChatText(t, st);
                // Only regenerate glyph data when it can actually differ.
                Vector2 rectSize = t.rectTransform.rect.size;
                if (st.genChars == null || st.genText != t.text || st.genRectSize != rectSize ||
                    Time.unscaledTime - st.genAt > 1.5f)
                {
                    TextGenerator gen = t.cachedTextGenerator;
                    try
                    {
                        gen.Populate(t.text, t.GetGenerationSettings(rectSize));
                    }
                    catch { }
                    st.genVc = gen != null ? gen.characterCountVisible : -1;
                    st.genChars = null;
                    float lineH = 0f;
                    if (gen != null && st.genVc > 0)
                    {
                        IList<UICharInfo> src = gen.characters;
                        if (src != null && src.Count >= st.genVc)
                        {
                            if (st.genChars == null || st.genChars.Length < st.genVc)
                                st.genChars = new UICharInfo[st.genVc];
                            for (int ci = 0; ci < st.genVc; ci++)
                                st.genChars[ci] = src[ci];
                            if (gen.lineCount > 0 && gen.lines.Count > 0)
                                lineH = gen.lines[0].height;
                            if (lineH <= 0.01f)
                                lineH = (t.fontSize > 0 ? t.fontSize : 16) * 1.3f;
                            if (gen.lineCount > 1 && st.genVc > 1)
                            {
                                float y0 = st.genChars[0].cursorPos.y;
                                for (int ci = 1; ci < st.genVc; ci++)
                                {
                                    float dy = Mathf.Abs(st.genChars[ci].cursorPos.y - y0);
                                    if (dy > 1f) { lineH = dy; break; }
                                }
                            }
                        }
                    }
                    st.genLineH = lineH;
                    st.genRectSize = rectSize;
                    st.genText = t.text;
                    st.genAt = Time.unscaledTime;
                }
                if (st.genChars == null || st.genVc <= 0)
                    continue;
                string[] units = st.visUnits.Length == st.genVc ? st.visUnits : (st.altUnits.Length == st.genVc ? st.altUnits : null);
                if (units == null || units.Length == 0 || units.Length != st.genVc)
                    continue;
                Rect textScreen = TextToScreenRect(t);
                ChatSelEntry en;
                if (ei < entries.Count)
                {
                    en = entries[ei];
                    if (en == null)
                    {
                        en = new ChatSelEntry();
                        entries[ei] = en;
                    }
                }
                else
                {
                    en = new ChatSelEntry();
                    entries.Add(en);
                }
                ei++;
                en.t = t;
                en.st = st;
                en.units = units;
                en.len = units.Length;
                en.chars = st.genChars;
                en.lineH = st.genLineH;
                en.rect = textScreen;
                en.pivot = t.rectTransform.pivot;
            }
                for (int k = entries.Count - 1; k >= ei; k--)
                    entries.RemoveAt(k);
                if (entries.Count > 1)
                    entries.Sort((a, b) => a.rect.y.CompareTo(b.rect.y));
                perfChatBuildMs += (float)chatDiagSw.Elapsed.TotalMilliseconds;
            }
            perfChatEntriesN = entries.Count;
            ChatSelEntry underEntry = null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].rect.Contains(mouse))
                {
                    underEntry = entries[i];
                    break;
                }
            }
            Rect menuZone = Rect.zero;
            bool onMenu = false;
            if (mpSelShowMenu)
            {
                menuZone = new Rect(mpSelMenuPos.x, mpSelMenuPos.y, 84f, 26f);
                if (menuZone.xMax > Screen.width) menuZone.x = Screen.width - menuZone.width - 4f;
                if (menuZone.yMax > Screen.height) menuZone.y = Screen.height - menuZone.height - 4f;
                onMenu = menuZone.Contains(mouse);
            }
            bool overFormatUi = mpFormatUiRect.width > 1f && mpFormatUiRect.Contains(mouse);
            bool inHistoryArea = !haveClip || clipRect.Contains(mouse);
            if (e.type == EventType.MouseDown && e.button == 0 && !onMenu && !overFormatUi)
            {
                if (inHistoryArea && underEntry != null)
                {
                    mpSelShowMenu = false;
                    mpSelDragging = true;
                    mpStickyVisLo = -1;
                    mpStickyVisHi = -1;
                    mpStickyPlainText = null;
                    if (mpChatInputField != null)
                    {
                        try
                        {
                            int car = Mathf.Clamp(mpChatInputField.caretPosition, 0, (mpChatInputField.text ?? "").Length);
                            mpChatInputField.selectionAnchorPosition = car;
                            mpChatInputField.selectionFocusPosition = car;
                        }
                        catch { }
                    }
                    mpSelAnchorT = mpSelFocusT = underEntry.t;
                    mpSelAnchorI = mpSelFocusI = NearestChatChar(underEntry.t, underEntry.st, underEntry.chars, underEntry.lineH, mouse, clipRect, underEntry.len, underEntry.rect, underEntry.pivot);
                    if (mpChatScrollRect != null && mpChatScrollRect.enabled)
                    {
                        mpChatScrollRect.enabled = false;
                        mpSelScrollLocked = true;
                    }
                    e.Use();
                }
                else
                {
                    mpSelShowMenu = false;
                    ClearMpChatSelection();
                    if (mpSelScrollLocked && mpChatScrollRect != null)
                    {
                        mpChatScrollRect.enabled = mpChatScrollOrigEnabled;
                        mpSelScrollLocked = false;
                    }
                }
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && mpSelDragging)
            {
                ChatSelEntry fe = underEntry != null && (!haveClip || underEntry.rect.Overlaps(clipRect))
                    ? underEntry
                    : FindNearestChatEntry(entries, mouse, clipRect, haveClip);
                if (fe != null)
                {
                    mpSelFocusT = fe.t;
                    mpSelFocusI = NearestChatChar(fe.t, fe.st, fe.chars, fe.lineH, mouse, clipRect, fe.len, fe.rect, fe.pivot);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && mpSelDragging)
            {
                mpSelDragging = false;
                if (mpSelScrollLocked && mpChatScrollRect != null)
                {
                    mpChatScrollRect.enabled = mpChatScrollOrigEnabled;
                    mpSelScrollLocked = false;
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 1 && !onMenu && !overFormatUi && inHistoryArea && underEntry != null && MpChatSelectionActive(entries))
            {
                mpSelShowMenu = true;
                mpSelMenuPos = mouse;
                e.Use();
            }
            bool selActive = MpChatSelectionActive(entries);
            if (copyPressed && !shiftCopy && !inputFocused && selActive && GUI.GetNameOfFocusedControl() != MpWheelHexCtrl)
            {
                CopySelectedChatText(entries);
                e.Use();
            }
            if (selActive)
                DrawMpChatSelectionCoverage(entries, clipRect, haveClip);
            if (mpSelDragging && haveClip && mpChatScrollRect != null && mpChatScrollRect.vertical
                && mpChatScrollRect.content != null
                && Time.frameCount != mpSelScrollFrame)
            {
                mpSelScrollFrame = Time.frameCount;
                float edge = 30f;
                float v = 0f;
                if (mouse.y > clipRect.yMax)
                    v = -Mathf.Min(1f + (mouse.y - clipRect.yMax) / 80f, 5f);
                else if (mouse.y > clipRect.yMax - edge)
                    v = -(1f - (clipRect.yMax - mouse.y) / edge);
                else if (mouse.y < clipRect.y)
                    v = Mathf.Min(1f + (clipRect.y - mouse.y) / 80f, 5f);
                else if (mouse.y < clipRect.y + edge)
                    v = 1f - (mouse.y - clipRect.y) / edge;
                if (v != 0f)
                {
                    float contentH = Mathf.Max(mpChatScrollRect.content.rect.height, 1f);
                    float np = mpChatScrollRect.verticalNormalizedPosition + v * (380f * Time.unscaledDeltaTime) / contentH;
                    mpChatScrollRect.verticalNormalizedPosition = Mathf.Clamp01(np);
                }
            }
            mpChatSelActive = selActive;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                mpSelShowMenu = false;
            if (mpSelShowMenu)
            {
                Rect menuRect = menuZone;
                bool menuHover = menuRect.Contains(mouse);
                bool menuClick = e.type == EventType.MouseDown && e.button == 0 && menuHover;
                if (e.type == EventType.MouseDown && e.button == 0 && !menuHover)
                {
                    mpSelShowMenu = false;
                }
                else
                {
                    Color saved = GUI.color;
                    GUI.color = new Color(0f, 0f, 0f, 0.2f);
                    GUI.DrawTexture(new Rect(menuRect.x + 2f, menuRect.y + 2f, menuRect.width, menuRect.height), whiteTex);
                    GUI.color = ThemePanelBg(1f);
                    GUI.DrawTexture(menuRect, whiteTex);
                    GUI.color = ThemeButtonBorder(1f);
                    GUI.DrawTexture(new Rect(menuRect.x, menuRect.y, menuRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(menuRect.x, menuRect.yMax - 1f, menuRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(menuRect.x, menuRect.y, 1f, menuRect.height), whiteTex);
                    GUI.DrawTexture(new Rect(menuRect.xMax - 1f, menuRect.y, 1f, menuRect.height), whiteTex);
                    Rect btnRect = new Rect(menuRect.x + 2f, menuRect.y + 2f, menuRect.width - 4f, menuRect.height - 4f);
                    GUI.color = menuHover ? new Color(1f, 1f, 1f, 0.18f) : new Color(1f, 1f, 1f, 0f);
                    GUI.DrawTexture(btnRect, whiteTex);
                    GUI.color = ThemeLabel(1f);
                    GUI.Label(new Rect(btnRect.x + 4f, btnRect.y, btnRect.width - 8f, btnRect.height), "Copy", settingsSectionStyle);
                    GUI.color = saved;
                    if (menuClick)
                    {
                        CopySelectedChatText(mpSelEntries);
                        mpSelShowMenu = false;
                        e.Use();
                    }
                }
            }
            if (shiftCopy && !inputFocused && mpChatStates.Count > 0)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (KeyValuePair<Text, ChatTextState> kv in mpChatStates)
                {
                    Text t = kv.Key;
                    ChatTextState st = kv.Value;
                    if (t == null || st == null || st.rawOriginal == null)
                        continue;
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(StripChatTags(st.rawOriginal ?? ""));
                }
                GUIUtility.systemCopyBuffer = sb.ToString();
                e.Use();
            }
            chatDiagSw.Restart();
            DrawChatColorPicker();
            perfChatPickerMs += (float)chatDiagSw.Elapsed.TotalMilliseconds;
        }

        private void EnsureChatWheelTextures()
        {
            if (mpChatWheelTex == null)
            {
                const int ws = 160;
                Texture2D tex = new Texture2D(ws, ws, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                Color32[] px = new Color32[ws * ws];
                float c = (ws - 1) * 0.5f;
                for (int y = 0; y < ws; y++)
                {
                    for (int x = 0; x < ws; x++)
                    {
                        float dx = x - c;
                        float dy = y - c;
                        float r = Mathf.Sqrt(dx * dx + dy * dy) / c;
                        int idx = y * ws + x;
                        if (r > 1f)
                        {
                            px[idx] = new Color32(0, 0, 0, 0);
                            continue;
                        }
                        float hue = (Mathf.Atan2(dy, dx) / (2f * Mathf.PI) + 1f) % 1f;
                        Color rgb = Color.HSVToRGB(hue, Mathf.Clamp01(r), 1f);
                        byte a = r > 0.95f ? (byte)Mathf.RoundToInt(255f * Mathf.Clamp01((1f - r) / 0.05f)) : (byte)255;
                        px[idx] = new Color32((byte)Mathf.RoundToInt(rgb.r * 255f), (byte)Mathf.RoundToInt(rgb.g * 255f), (byte)Mathf.RoundToInt(rgb.b * 255f), a);
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                mpChatWheelTex = tex;
            }
            if (mpChatRampTex == null)
            {
                const int rw = 8;
                const int rh = 64;
                Texture2D tex2 = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                tex2.wrapMode = TextureWrapMode.Clamp;
                Color32[] px2 = new Color32[rw * rh];
                for (int y = 0; y < rh; y++)
                {
                    byte v = (byte)Mathf.RoundToInt(255f * (1f - (float)y / (rh - 1)));
                    for (int x = 0; x < rw; x++)
                        px2[y * rw + x] = new Color32(v, v, v, 255);
                }
                tex2.SetPixels32(px2);
                tex2.Apply(false, true);
                mpChatRampTex = tex2;
            }
            if (mpChatWheelMarkerTex == null)
                mpChatWheelMarkerTex = CreateRoundedRectTexture(16, 8);
        }

        private void DrawChatColorPicker()
        {
            if (mpChatInputField == null || mpChatStates.Count == 0)
                return;
            // The T-button/preset/bar labels rely on settingsSectionStyle and the skin
            // font swap below — without ensured styles a null-style Label aborts this
            // whole function (button rendered, no glyph, no picker panel) until the
            // settings panel happens to create them.
            EnsureSettingsStyles();
            Rect chatRect = GetChatWindowScreenRect();
            if (chatRect.width < 10f)
            {
                mpChatColorPickerOpen = false;
                mpFormatUiRect = Rect.zero;
                return;
            }
            Event e = Event.current;
            Rect btn = new Rect(chatRect.x + 4f, chatRect.y + 4f, 22f, 22f);
            // The waiting room's chat has its "Chat" title in that corner; there
            // the button goes in the top-left corner of the message area.
            if (!IsContentSceneActive())
            {
                Rect messages = GetChatViewportScreenRect();
                if (messages.width > 1f)
                    btn = new Rect(messages.x + 4f, messages.y + 4f, 22f, 22f);
            }
            mpFormatUiRect = btn;
            bool hover = btn.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover && mpChatInputField != null)
            {
                mpChatColorPickerOpen = !mpChatColorPickerOpen;
                e.Use();
                return;
            }
            Color oldColor = GUI.color;
            if (mpChatRoundBtnTex == null)
                mpChatRoundBtnTex = CreateRoundedRectTexture(32, 7);
            GUI.color = ThemeButtonBorder(1f);
            GUI.DrawTexture(new Rect(btn.x - 1f, btn.y - 1f, btn.width + 2f, btn.height + 2f), mpChatRoundBtnTex);
            GUI.color = hover ? new Color(1f, 1f, 1f, 0.92f) : ThemeButtonBg(1f);
            GUI.DrawTexture(btn, mpChatRoundBtnTex);
            GUI.color = ThemeLabel(1f);
            Font prevFont = GUI.skin.label.font;
            if (mpChatColorButtonFont != null)
                GUI.skin.label.font = mpChatColorButtonFont;
            GUI.Label(new Rect(btn.x + 2f, btn.y + 1f, 18f, 18f), "T",
                settingsSectionStyle != null ? settingsSectionStyle : GUI.skin.label);
            GUI.skin.label.font = prevFont;
            GUI.color = Color.white;
            if (!mpChatColorPickerOpen)
                return;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                mpChatColorPickerOpen = false;
                return;
            }
            const float cell = 26f;
            const int colorCols = 6;
            const int presetCols = 2;
            const int cols = colorCols + presetCols;
            const float barH = 28f;
            const float cellGap = 2f;
            const float sectionGap = 10f;
            const float wheelSize = 120f;
            const float sliderW = 18f;
            float pad = 6f;
            int count = ChatColorSet.Length;
            int rows = (count + colorCols - 1) / colorCols;
            float gridW = cols * cell + (cols - 1) * cellGap + sectionGap;
            float panelW = Mathf.Max(gridW, mpChatWheelOpen ? wheelSize + 10f + sliderW + 10f + 60f : 0f) + pad * 2f;
            float barW = (panelW - pad * 2f - 5 * 4f) / 6f;
            float baseH = pad + rows * cell + (rows - 1) * cellGap + 4f + 6f + (barH - 4f) + pad;
            float panelH = mpChatWheelOpen ? baseH + 6f + wheelSize : baseH;
            Rect panel = new Rect(btn.x, btn.yMax + 4f, panelW, panelH);
            if (panel.xMax > Screen.width)
                panel.x = Screen.width - panelW - 4f;
            // No room below (the waiting room's message box is near the bottom):
            // open above the button instead of sliding over it.
            if (panel.yMax > Screen.height)
                panel.y = btn.y - 4f - panelH >= 4f ? btn.y - 4f - panelH : Screen.height - panelH - 4f;
            mpFormatUiRect = Rect.MinMaxRect(
                Mathf.Min(btn.xMin, panel.xMin),
                Mathf.Min(btn.yMin, panel.yMin),
                Mathf.Max(btn.xMax, panel.xMax),
                Mathf.Max(btn.yMax, panel.yMax));
            GUI.color = new Color(0f, 0f, 0f, 0.18f);
            GUI.DrawTexture(new Rect(panel.x + 3f, panel.y + 3f, panel.width, panel.height), whiteTex);
            GUI.color = ThemePanelBg(1f);
            GUI.DrawTexture(panel, whiteTex);
            GUI.color = ThemePanelBorder(1f);
            GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), whiteTex);
            float gridX = panel.x + pad;
            float gridY = panel.y + pad;
            for (int i = 0; i < count; i++)
            {
                int cx = i % colorCols;
                int cy = i / colorCols;
                Rect cellRect = new Rect(gridX + cx * (cell + cellGap), gridY + cy * (cell + cellGap), cell, cell);
                bool cellHover = cellRect.Contains(e.mousePosition);
                GUI.color = ChatColorSet[i];
                GUI.DrawTexture(new Rect(cellRect.x + 2f, cellRect.y + 2f, cell - 4f, cell - 4f), whiteTex);
                GUI.color = ThemeButtonBorder(1f);
                GUI.DrawTexture(new Rect(cellRect.x, cellRect.y, cellRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(cellRect.x, cellRect.yMax - 1f, cellRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(cellRect.x, cellRect.y, 1f, cellRect.height), whiteTex);
                GUI.DrawTexture(new Rect(cellRect.xMax - 1f, cellRect.y, 1f, cellRect.height), whiteTex);
                if (cellHover)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.25f);
                    GUI.DrawTexture(new Rect(cellRect.x + 2f, cellRect.y + 2f, cell - 4f, cell - 4f), whiteTex);
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(cellRect.x, cellRect.y, cellRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(cellRect.x, cellRect.yMax - 1f, cellRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(cellRect.x, cellRect.y, 1f, cellRect.height), whiteTex);
                    GUI.DrawTexture(new Rect(cellRect.xMax - 1f, cellRect.y, 1f, cellRect.height), whiteTex);
                }
                if (e.type == EventType.MouseDown && e.button == 0 && cellRect.Contains(e.mousePosition))
                {
                    ApplyChatColor(ChatColorSet[i]);
                    e.Use();
                    return;
                }
            }
            float presetX = gridX + colorCols * (cell + cellGap) + sectionGap;
            for (int pi = 0; pi < ChatPresetCount; pi++)
            {
                int pcx = pi % presetCols;
                int pcy = pi / presetCols;
                Rect presetRect = new Rect(presetX + pcx * (cell + cellGap), gridY + pcy * (cell + cellGap), cell, cell);
                bool hasColor = mpChatColorPresets[pi].a > 0.01f;
                bool presetHover = presetRect.Contains(e.mousePosition);
                if (hasColor)
                {
                    GUI.color = mpChatColorPresets[pi];
                    GUI.DrawTexture(new Rect(presetRect.x + 2f, presetRect.y + 2f, cell - 4f, cell - 4f), whiteTex);
                }
                else
                {
                    GUI.color = new Color(0.15f, 0.15f, 0.15f, 0.6f);
                    GUI.DrawTexture(new Rect(presetRect.x + 2f, presetRect.y + 2f, cell - 4f, cell - 4f), whiteTex);
                    GUI.color = ThemeLabel(0.4f);
                    Font savedPresetFont = GUI.skin.label.font;
                    if (mpChatColorButtonFont != null)
                        GUI.skin.label.font = mpChatColorButtonFont;
                    GUI.Label(new Rect(presetRect.x + 2f, presetRect.y + 1f, cell - 4f, cell - 2f), "+",
                        settingsSectionStyle != null ? settingsSectionStyle : GUI.skin.label);
                    GUI.skin.label.font = savedPresetFont;
                }
                GUI.color = ThemeButtonBorder(1f);
                if (hasColor)
                    GUI.color = new Color(0.9f, 0.9f, 0.9f, 0.7f);
                GUI.DrawTexture(new Rect(presetRect.x, presetRect.y, presetRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(presetRect.x, presetRect.yMax - 1f, presetRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(presetRect.x, presetRect.y, 1f, presetRect.height), whiteTex);
                GUI.DrawTexture(new Rect(presetRect.xMax - 1f, presetRect.y, 1f, presetRect.height), whiteTex);
                if (presetHover)
                {
                    GUI.color = new Color(1f, 1f, 1f, hasColor ? 0.25f : 0.12f);
                    GUI.DrawTexture(new Rect(presetRect.x + 2f, presetRect.y + 2f, cell - 4f, cell - 4f), whiteTex);
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(presetRect.x, presetRect.y, presetRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(presetRect.x, presetRect.yMax - 1f, presetRect.width, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(presetRect.x, presetRect.y, 1f, presetRect.height), whiteTex);
                    GUI.DrawTexture(new Rect(presetRect.xMax - 1f, presetRect.y, 1f, presetRect.height), whiteTex);
                }
                if (e.type == EventType.MouseDown && presetRect.Contains(e.mousePosition))
                {
                    if (e.button == 0 && hasColor)
                    {
                        ApplyChatColor(mpChatColorPresets[pi]);
                        e.Use();
                        return;
                    }
                    else if (e.button == 1)
                    {
                        Color wc = Color.HSVToRGB(mpChatWheelH, mpChatWheelS, mpChatWheelV);
                        mpChatColorPresets[pi] = wc;
                        string hex = ColorUtility.ToHtmlStringRGB(wc);
                        ConfigEntry<string>[] presetCfgs = new ConfigEntry<string>[] { chatColorPreset1Config, chatColorPreset2Config, chatColorPreset3Config, chatColorPreset4Config };
                        presetCfgs[pi].Value = hex;
                        e.Use();
                        return;
                    }
                }
            }
            float secY = gridY + rows * cell + (rows - 1) * cellGap + 4f;
            GUI.color = ThemePanelBorder(1f);
            GUI.DrawTexture(new Rect(panel.x + pad, secY, panel.width - pad * 2f, 1f), whiteTex);
            float barY = secY + 6f;
            float wheelY = barY + (barH - 4f) + 6f;
            if (mpChatWheelOpen)
            {
                EnsureChatWheelTextures();
                Rect discRect = new Rect(panel.x + pad, wheelY, wheelSize, wheelSize);
                Rect sliderRect = new Rect(discRect.xMax + 10f, wheelY, sliderW, wheelSize);
                Vector2 mp2 = e.mousePosition;
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    Vector2 dd = mp2 - discRect.center;
                    float rr = dd.magnitude / (wheelSize * 0.5f);
                    if (discRect.Contains(mp2) && rr <= 1.05f)
                    {
                        GUI.FocusControl(null);
                        mpChatWheelDrag = 1;
                        mpChatWheelS = Mathf.Clamp01(rr);
                        mpChatWheelH = (Mathf.Atan2(-dd.y, dd.x) / (2f * Mathf.PI) + 1f) % 1f;
                        e.Use();
                    }
                    else if (sliderRect.Contains(mp2))
                    {
                        GUI.FocusControl(null);
                        mpChatWheelDrag = 2;
                        mpChatWheelV = Mathf.Clamp01(1f - (mp2.y - sliderRect.y) / sliderRect.height);
                        e.Use();
                    }
                }
                else if (e.type == EventType.MouseDrag && e.button == 0 && mpChatWheelDrag == 1)
                {
                    Vector2 dd = mp2 - discRect.center;
                    float rr = dd.magnitude / (wheelSize * 0.5f);
                    mpChatWheelS = Mathf.Clamp01(rr);
                    mpChatWheelH = (Mathf.Atan2(-dd.y, dd.x) / (2f * Mathf.PI) + 1f) % 1f;
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag && e.button == 0 && mpChatWheelDrag == 2)
                {
                    mpChatWheelV = Mathf.Clamp01(1f - (mp2.y - sliderRect.y) / sliderRect.height);
                    e.Use();
                }
                else if (e.type == EventType.MouseUp && e.button == 0 && mpChatWheelDrag != 0)
                {
                    mpChatWheelDrag = 0;
                    ApplyChatColor(Color.HSVToRGB(mpChatWheelH, mpChatWheelS, mpChatWheelV));
                    e.Use();
                }
                GUI.color = Color.white;
                GUI.DrawTexture(discRect, mpChatWheelTex);
                float marg = mpChatWheelH * 2f * Mathf.PI;
                float mrad = mpChatWheelS * (wheelSize * 0.5f);
                Vector2 markPos = discRect.center + new Vector2(Mathf.Cos(marg) * mrad, -Mathf.Sin(marg) * mrad);
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.DrawTexture(new Rect(markPos.x - 5f, markPos.y - 5f, 10f, 10f), mpChatWheelMarkerTex);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(markPos.x - 3f, markPos.y - 3f, 6f, 6f), mpChatWheelMarkerTex);
                GUI.color = ThemeButtonBg(1f);
                GUI.DrawTexture(sliderRect, whiteTex);
                GUI.color = Color.white;
                GUI.DrawTexture(sliderRect, mpChatRampTex);
                GUI.color = ThemeButtonBorder(1f);
                GUI.DrawTexture(new Rect(sliderRect.x, sliderRect.y, sliderRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(sliderRect.x, sliderRect.yMax - 1f, sliderRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(sliderRect.x, sliderRect.y, 1f, sliderRect.height), whiteTex);
                GUI.DrawTexture(new Rect(sliderRect.xMax - 1f, sliderRect.y, 1f, sliderRect.height), whiteTex);
                float vLine = sliderRect.y + (1f - mpChatWheelV) * sliderRect.height;
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.DrawTexture(new Rect(sliderRect.x - 2f, vLine - 2f, sliderRect.width + 4f, 4f), whiteTex);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(sliderRect.x - 1f, vLine - 1f, sliderRect.width + 2f, 2f), whiteTex);
                Color wheelCol = Color.HSVToRGB(mpChatWheelH, mpChatWheelS, mpChatWheelV);
                float prevColX = sliderRect.xMax + 10f;
                float prevColW = panel.xMax - pad - prevColX;
                Rect prevRect = new Rect(prevColX, wheelY, prevColW, wheelSize - 26f);
                GUI.color = wheelCol;
                GUI.DrawTexture(prevRect, whiteTex);
                GUI.color = ThemeButtonBorder(1f);
                GUI.DrawTexture(new Rect(prevRect.x, prevRect.y, prevRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(prevRect.x, prevRect.yMax - 1f, prevRect.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(prevRect.x, prevRect.y, 1f, prevRect.height), whiteTex);
                GUI.DrawTexture(new Rect(prevRect.xMax - 1f, prevRect.y, 1f, prevRect.height), whiteTex);
                string curHex = "#" + ColorUtility.ToHtmlStringRGB(wheelCol);
                if (mpWheelHexText == null) mpWheelHexText = curHex;
                bool wasFocused = GUI.GetNameOfFocusedControl() == MpWheelHexCtrl;
                if (!wasFocused) mpWheelHexText = curHex;
                GUI.color = ThemeLabel(1f);
                GUI.SetNextControlName(MpWheelHexCtrl);
                string newHex = GUI.TextField(new Rect(prevRect.x, prevRect.yMax + 4f, prevRect.width, 18f), mpWheelHexText, 8, settingsSectionStyle);
                if (newHex != mpWheelHexText)
                {
                    mpWheelHexText = newHex;
                    string t = newHex.Trim();
                    if (t.Length > 0 && t[0] != '#') t = "#" + t;
                    Color pc;
                    if (ColorUtility.TryParseHtmlString(t, out pc))
                    {
                        float hh, ss, vv;
                        Color.RGBToHSV(pc, out hh, out ss, out vv);
                        mpChatWheelH = hh;
                        mpChatWheelS = ss;
                        mpChatWheelV = vv;
                    }
                }
                bool nowFocused = GUI.GetNameOfFocusedControl() == MpWheelHexCtrl;
                if (wasFocused && !nowFocused) CommitWheelHex();
                if (nowFocused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    CommitWheelHex();
                    e.Use();
                }
            }
            string[] barLabels = new string[] { "B", "I", "S-", "S+", "C", "W" };
            for (int bi = 0; bi < 6; bi++)
            {
                Rect br = new Rect(panel.x + pad + bi * (barW + 4f), barY, barW, barH - 4f);
                bool bh = br.Contains(e.mousePosition) || (bi == 5 && mpChatWheelOpen);
                GUI.color = new Color(0f, 0f, 0f, 0.15f);
                GUI.DrawTexture(new Rect(br.x, br.y + 2f, br.width, br.height), whiteTex);
                GUI.color = bh ? new Color(1f, 1f, 1f, 0.92f) : ThemeButtonBg(1f);
                GUI.DrawTexture(br, whiteTex);
                GUI.color = ThemeButtonBorder(1f);
                GUI.DrawTexture(new Rect(br.x, br.y, br.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(br.x, br.yMax - 1f, br.width, 1f), whiteTex);
                GUI.DrawTexture(new Rect(br.x, br.y, 1f, br.height), whiteTex);
                GUI.DrawTexture(new Rect(br.xMax - 1f, br.y, 1f, br.height), whiteTex);
                GUI.color = ThemeLabel(1f);
                Font savedFont = GUI.skin.label.font;
                if (mpChatColorButtonFont != null)
                    GUI.skin.label.font = mpChatColorButtonFont;
                if (mpChatBarStylePlain == null)
                {
                    mpChatBarStylePlain = new GUIStyle(settingsSectionStyle) { fontSize = 14 };
                    mpChatBarStyleBold = new GUIStyle(mpChatBarStylePlain) { fontStyle = FontStyle.Bold };
                    mpChatBarStyleItalic = new GUIStyle(mpChatBarStylePlain) { fontStyle = FontStyle.Italic };
                }
                GUIStyle barBtnStyle = bi == 0 ? mpChatBarStyleBold : bi == 1 ? mpChatBarStyleItalic : mpChatBarStylePlain;
                GUI.Label(new Rect(br.x + 2f, br.y + 1f, br.width - 4f, br.height - 2f), barLabels[bi], barBtnStyle);
                GUI.skin.label.font = savedFont;
                if (e.type == EventType.MouseDown && e.button == 0 && br.Contains(e.mousePosition))
                {
                    switch (bi)
                    {
                        case 0: ApplyChatFormatToggle("<b>", "</b>"); break;
                        case 1: ApplyChatFormatToggle("<i>", "</i>"); break;
                        case 2: ApplyChatSizeAdjust(-1); break;
                        case 3: ApplyChatSizeAdjust(1); break;
                        case 5: mpChatWheelOpen = !mpChatWheelOpen; mpChatWheelDrag = 0; break;
                        default: ApplyChatClearFormatting(); break;
                    }
                    e.Use();
                    return;
                }
            }
            if (Time.unscaledTime < mpChatLimitNoteUntil && !string.IsNullOrEmpty(mpChatLimitNote))
            {
                Rect note = new Rect(panel.x, panel.yMax + 3f, panel.width, 20f);
                GUI.color = ThemePanelBg(1f);
                GUI.DrawTexture(note, whiteTex);
                GUI.color = new Color(1f, 0.55f, 0.45f, 1f);
                GUI.Label(new Rect(note.x + 6f, note.y, note.width - 12f, note.height), mpChatLimitNote, settingsSmallStyle);
            }
            GUI.color = oldColor;
        }

        private static readonly Color[] ChatColorSet = new Color[]
        {
            new Color(1f, 1f, 1f), new Color(0.55f, 0.55f, 0.55f), new Color(0.15f, 0.15f, 0.15f), new Color(1f, 0.4f, 0.4f),
            new Color(1f, 0.75f, 0.4f), new Color(1f, 0.95f, 0.4f), new Color(0.55f, 1f, 0.4f), new Color(0.2f, 1f, 0.75f),
            new Color(0.4f, 0.9f, 1f), new Color(0.45f, 0.55f, 1f), new Color(0.8f, 0.5f, 1f), new Color(1f, 0.5f, 0.85f)
        };

        private static void ClassifyChatTag(string tag, out string type, out bool isClose)
        {
            type = null;
            isClose = tag.Length > 1 && tag[1] == '/';
            if (!isClose)
            {
                if (tag == "<b>") type = "b";
                else if (tag == "<i>") type = "i";
                else if (tag.StartsWith("<size=", StringComparison.Ordinal)) type = "size";
                else if (tag.StartsWith("<color=", StringComparison.Ordinal)) type = "color";
            }
            else
            {
                if (tag == "</b>") type = "b";
                else if (tag == "</i>") type = "i";
                else if (tag == "</size>") type = "size";
                else if (tag == "</color>") type = "color";
            }
        }

        private static void CurrentChatStyle(List<string> openStack, out string color, out bool bold, out bool italic, out int size)
        {
            color = null;
            bold = false;
            italic = false;
            size = 0;
            for (int k = 0; k < openStack.Count; k++)
            {
                string t = openStack[k];
                if (t.StartsWith("<color=", StringComparison.Ordinal))
                    color = t.Substring(7, t.Length - 8).TrimStart('#');
                else if (t == "<b>")
                    bold = true;
                else if (t == "<i>")
                    italic = true;
                else if (t.StartsWith("<size=", StringComparison.Ordinal))
                {
                    int v;
                    if (int.TryParse(t.Substring(6, t.Length - 7), out v))
                        size = v;
                }
            }
        }

        private ChatInputState ParseChatInputStyle(string raw)
        {
            ChatInputState st = new ChatInputState();
            if (raw == null)
                raw = "";
            st.rawOriginal = raw;
            System.Text.StringBuilder plain = new System.Text.StringBuilder(raw.Length);
            List<int> r2v = new List<int>(raw.Length + 1);
            List<int> v2r = new List<int>(raw.Length + 1);
            List<string> openStack = new List<string>();
            string curColor;
            bool curBold, curItalic;
            int curSize;
            CurrentChatStyle(openStack, out curColor, out curBold, out curItalic, out curSize);
            bool hasRun = false;
            int runStart = 0;
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '<')
                {
                    int gt = raw.IndexOf('>', i + 1);
                    string tag = (gt >= i && gt - i <= 40) ? raw.Substring(i, gt - i + 1) : null;
                    string type;
                    bool isClose;
                    if (tag != null)
                        ClassifyChatTag(tag, out type, out isClose);
                    else
                    {
                        type = null;
                        isClose = false;
                    }
                    if (tag != null && type != null)
                    {
                        for (int k = i; k <= gt; k++)
                            r2v.Add(plain.Length);
                        if (isClose)
                        {
                            int k = -1;
                            for (int j = openStack.Count - 1; j >= 0; j--)
                            {
                                string t2;
                                bool c2;
                                ClassifyChatTag(openStack[j], out t2, out c2);
                                if (t2 == type)
                                {
                                    k = j;
                                    break;
                                }
                            }
                            if (k >= 0)
                                openStack.RemoveRange(k, openStack.Count - k);
                        }
                        else
                        {
                            openStack.Add(tag);
                        }
                        i = gt + 1;
                        continue;
                    }
                }
                string nc; bool nb, ni;
                int ns;
                CurrentChatStyle(openStack, out nc, out nb, out ni, out ns);
                if (!hasRun || nc != curColor || nb != curBold || ni != curItalic || ns != curSize)
                {
                    if (hasRun && runStart < plain.Length)
                        st.runs.Add(new StyleRun { visStart = runStart, visEnd = plain.Length, color = curColor, bold = curBold, italic = curItalic, size = curSize });
                    runStart = plain.Length;
                    curColor = nc;
                    curBold = nb;
                    curItalic = ni;
                    curSize = ns;
                    hasRun = true;
                }
                r2v.Add(plain.Length);
                v2r.Add(i);
                plain.Append(raw[i]);
                i++;
            }
            if (hasRun && runStart < plain.Length)
                st.runs.Add(new StyleRun { visStart = runStart, visEnd = plain.Length, color = curColor, bold = curBold, italic = curItalic, size = curSize });
            while (r2v.Count <= raw.Length)
                r2v.Add(plain.Length);
            while (v2r.Count <= plain.Length)
                v2r.Add(raw.Length);
            st.plainText = plain.ToString();
            st.rawToVis = r2v.ToArray();
            st.visToRaw = v2r.ToArray();
            return st;
        }

        private ChatInputState GetOrParseChatInputStyle()
        {
            InputField f = mpChatInputField;
            string cur = f != null ? (f.text ?? "") : "";
            if (mpChatInputStyle.rawOriginal != cur)
                mpChatInputStyle = ParseChatInputStyle(cur);
            return mpChatInputStyle;
        }

        private static string SerializeChatInputStyle(ChatInputState st, out int[] visToRawNew)
        {
            string plain = st != null ? st.plainText : null;
            if (st == null || string.IsNullOrEmpty(plain) || st.runs == null || st.runs.Count == 0)
            {
                visToRawNew = new int[1];
                return plain ?? "";
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder(plain.Length + 32);
            List<int> v2r = new List<int>(plain.Length + 1);
            foreach (StyleRun r in st.runs)
            {
                List<string> opens = new List<string>(4);
                if (!string.IsNullOrEmpty(r.color)) opens.Add("<color=#" + r.color.TrimStart('#') + ">");
                if (r.bold) opens.Add("<b>");
                if (r.italic) opens.Add("<i>");
                if (r.size > 0) opens.Add("<size=" + r.size + ">");
                foreach (string o in opens)
                    sb.Append(o);
                for (int k = r.visStart; k < r.visEnd; k++)
                {
                    v2r.Add(sb.Length);
                    sb.Append(plain[k]);
                }
                for (int k = opens.Count - 1; k >= 0; k--)
                    sb.Append(GetTagClose(opens[k]));
            }
            v2r.Add(sb.Length);
            visToRawNew = v2r.ToArray();
            return sb.ToString();
        }

        private static void SplitRunsAt(List<StyleRun> runs, int pos)
        {
            if (pos <= 0)
                return;
            for (int i = 0; i < runs.Count; i++)
            {
                StyleRun r = runs[i];
                if (r.visStart < pos && r.visEnd > pos)
                {
                    StyleRun copy = new StyleRun { visStart = pos, visEnd = r.visEnd, color = r.color, bold = r.bold, italic = r.italic, size = r.size };
                    r.visEnd = pos;
                    runs.Insert(i + 1, copy);
                    return;
                }
            }
        }

        private static bool SameRunStyle(StyleRun a, StyleRun b)
        {
            return a.color == b.color && a.bold == b.bold && a.italic == b.italic && a.size == b.size;
        }

        private static void MergeAdjacentRuns(List<StyleRun> runs)
        {
            for (int i = runs.Count - 1; i > 0; i--)
            {
                if (SameRunStyle(runs[i - 1], runs[i]))
                {
                    runs[i - 1].visEnd = runs[i].visEnd;
                    runs.RemoveAt(i);
                }
            }
        }

        private static void RunsSetColor(List<StyleRun> runs, int visLo, int visHi, string hex)
        {
            SplitRunsAt(runs, visLo);
            SplitRunsAt(runs, visHi);
            string norm = hex != null ? hex.TrimStart('#') : null;
            foreach (StyleRun r in runs)
                if (r.visStart < visHi && r.visEnd > visLo)
                    r.color = norm;
            MergeAdjacentRuns(runs);
        }

        private static void RunsSetBold(List<StyleRun> runs, int visLo, int visHi, bool value)
        {
            SplitRunsAt(runs, visLo);
            SplitRunsAt(runs, visHi);
            foreach (StyleRun r in runs)
                if (r.visStart < visHi && r.visEnd > visLo)
                    r.bold = value;
            MergeAdjacentRuns(runs);
        }

        private static void RunsSetItalic(List<StyleRun> runs, int visLo, int visHi, bool value)
        {
            SplitRunsAt(runs, visLo);
            SplitRunsAt(runs, visHi);
            foreach (StyleRun r in runs)
                if (r.visStart < visHi && r.visEnd > visLo)
                    r.italic = value;
            MergeAdjacentRuns(runs);
        }

        private static void RunsSetSize(List<StyleRun> runs, int visLo, int visHi, int sizeVal)
        {
            SplitRunsAt(runs, visLo);
            SplitRunsAt(runs, visHi);
            foreach (StyleRun r in runs)
                if (r.visStart < visHi && r.visEnd > visLo)
                    r.size = sizeVal;
            MergeAdjacentRuns(runs);
        }

        private static void RunsClearStyle(List<StyleRun> runs, int visLo, int visHi)
        {
            SplitRunsAt(runs, visLo);
            SplitRunsAt(runs, visHi);
            foreach (StyleRun r in runs)
            {
                if (r.visStart < visHi && r.visEnd > visLo)
                {
                    r.color = null;
                    r.bold = false;
                    r.italic = false;
                    r.size = 0;
                }
            }
            MergeAdjacentRuns(runs);
        }

        private bool GetApplyRangeVis(ChatInputState st, out int visLo, out int visHi, out bool whole)
        {
            visLo = 0;
            visHi = st != null ? st.plainText.Length : 0;
            whole = true;
            if (st == null || st.rawToVis == null || st.rawToVis.Length == 0)
                return false;
            InputField f = mpChatInputField;
            if (f != null)
            {
                try
                {
                    int sa = f.selectionAnchorPosition;
                    int sf = f.selectionFocusPosition;
                    if (sa != sf)
                    {
                        int ra = Mathf.Min(sa, sf);
                        int rb = Mathf.Max(sa, sf);
                        if (rb < st.rawToVis.Length)
                        {
                            int a = st.rawToVis[ra];
                            int b = st.rawToVis[rb];
                            if (b > a)
                            {
                                visLo = a;
                                visHi = b;
                                whole = false;
                                return true;
                            }
                        }
                    }
                }
                catch { }
            }
            if (mpStickyVisLo >= 0 && mpStickyVisHi > mpStickyVisLo
                && mpStickyVisHi <= st.plainText.Length
                && mpStickyPlainText == st.plainText)
            {
                visLo = mpStickyVisLo;
                visHi = mpStickyVisHi;
                whole = false;
                return true;
            }
            return false;
        }

        private void SetInputFromStyle(ChatInputState st, int visLo, int visHi, bool whole)
        {
            InputField field = mpChatInputField;
            if (field == null || st == null)
                return;
            int[] v2r;
            string newText = SerializeChatInputStyle(st, out v2r);
            int rawLo = 0;
            int rawHi = newText.Length;
            if (!whole && v2r != null && v2r.Length > 0)
            {
                int a = Mathf.Clamp(visLo, 0, v2r.Length - 1);
                int b = Mathf.Clamp(visHi, 0, v2r.Length - 1);
                if (b > a)
                {
                    rawLo = v2r[a];
                    rawHi = v2r[b];
                }
                else
                {
                    rawLo = 0;
                    rawHi = newText.Length;
                    whole = true;
                }
            }
            ApplyChatText(field, newText, rawLo, rawHi, whole);
            if (whole)
            {
                mpStickyVisLo = 0;
                mpStickyVisHi = st.plainText.Length;
                mpStickyPlainText = st.plainText;
            }
            else
            {
                mpStickyVisLo = visLo;
                mpStickyVisHi = visHi;
                mpStickyPlainText = st.plainText;
            }
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] styleApply whole=" + whole + " vis=" + visLo + "," + visHi
                + " text='" + newText + "'");
        }

        private void ApplyChatColor(Color c)
        {
            InputField field = mpChatInputField;
            if (field == null)
                return;
            ChatInputState st = GetOrParseChatInputStyle();
            if (st == null || st.plainText.Length == 0)
                return;
            int visLo, visHi;
            bool whole;
            GetApplyRangeVis(st, out visLo, out visHi, out whole);
            if (whole)
                ClearMpChatSelection();
            string hex = ColorUtility.ToHtmlStringRGB(c);
            RunsSetColor(st.runs, visLo, visHi, hex);
            SetInputFromStyle(st, visLo, visHi, whole);
        }

        private void ApplyChatFormatToggle(string open, string close)
        {
            InputField field = mpChatInputField;
            if (field == null)
                return;
            ChatInputState st = GetOrParseChatInputStyle();
            if (st == null || st.plainText.Length == 0)
                return;
            int visLo, visHi;
            bool whole;
            GetApplyRangeVis(st, out visLo, out visHi, out whole);
            if (whole)
                ClearMpChatSelection();
            bool isBold = open == "<b>";
            bool allSet = true;
            foreach (StyleRun r in st.runs)
            {
                if (r.visStart < visHi && r.visEnd > visLo)
                {
                    if (isBold ? !r.bold : !r.italic)
                    {
                        allSet = false;
                        break;
                    }
                }
            }
            bool setValue = !allSet;
            if (isBold)
                RunsSetBold(st.runs, visLo, visHi, setValue);
            else
                RunsSetItalic(st.runs, visLo, visHi, setValue);
            SetInputFromStyle(st, visLo, visHi, whole);
        }

        private void ApplyChatSizeAdjust(int delta)
        {
            InputField field = mpChatInputField;
            if (field == null)
                return;
            ChatInputState st = GetOrParseChatInputStyle();
            if (st == null || st.plainText.Length == 0)
                return;
            int visLo, visHi;
            bool whole;
            GetApplyRangeVis(st, out visLo, out visHi, out whole);
            if (whole)
                ClearMpChatSelection();
            int fs = field.textComponent != null ? field.textComponent.fontSize : 16;
            int maxSz = Mathf.Clamp(fs * 2, fs + 4, 36);
            int curSize = -1;
            foreach (StyleRun r in st.runs)
            {
                if (r.visStart < visHi && r.visEnd > visLo)
                {
                    curSize = r.size > 0 ? r.size : fs;
                    break;
                }
            }
            if (curSize < 0)
                curSize = fs;
            int nv = Mathf.Clamp(curSize + delta, 8, maxSz);
            RunsSetSize(st.runs, visLo, visHi, nv);
            SetInputFromStyle(st, visLo, visHi, whole);
        }




        private void ApplyChatClearFormatting()
        {
            InputField field = mpChatInputField;
            if (field == null)
                return;
            ChatInputState st = GetOrParseChatInputStyle();
            if (st == null || st.plainText.Length == 0)
                return;
            int visLo, visHi;
            bool whole;
            GetApplyRangeVis(st, out visLo, out visHi, out whole);
            if (whole)
                ClearMpChatSelection();
            RunsClearStyle(st.runs, visLo, visHi);
            SetInputFromStyle(st, visLo, visHi, whole);
        }

        private void CommitWheelHex()
        {
            string s = (mpWheelHexText ?? "").Trim();
            if (s.Length > 0 && s[0] != '#') s = "#" + s;
            Color c;
            if (s.Length >= 1 && ColorUtility.TryParseHtmlString(s, out c))
            {
                float h, s2, v;
                Color.RGBToHSV(c, out h, out s2, out v);
                mpChatWheelH = h;
                mpChatWheelS = s2;
                mpChatWheelV = v;
                ApplyChatColor(c);
            }
            else
                mpWheelHexText = "#" + ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(mpChatWheelH, mpChatWheelS, mpChatWheelV));
        }

        // Shown under the format panel when a format change was refused.
        private float mpChatLimitNoteUntil = 0f;
        private string mpChatLimitNote = null;

        private void ApplyChatText(InputField field, string newText, int selStart, int selEnd, bool whole)
        {
            string final = SanitizeChatMarkup(newText);
            // Tags count toward the chat's character limit. Never write past it:
            // the game sends the field as-is, and a message cut at the limit
            // would show raw "<color=..." tags to everyone. Refuse the change.
            int limit = field.characterLimit;
            if (limit > 0 && final.Length > limit)
            {
                mpChatLimitNote = "Too long to format (" + final.Length + "/" + limit + ")";
                mpChatLimitNoteUntil = Time.unscaledTime + 2.5f;
                // The style edit was made on the cached parse; drop it so the
                // next press starts from what the field really holds.
                mpChatInputStyle.rawOriginal = null;
                FocusChatField(field);
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] chat format refused: " + final.Length + "/" + limit);
                return;
            }
            field.text = final;
            FocusChatField(field);
            mpChatApplyFrame = Time.frameCount;
            if (whole)
            {
                selStart = final.Length;
                selEnd = final.Length;
            }
            else if (final != newText && selStart >= 0 && selEnd > selStart && selEnd <= newText.Length)
            {
                string selectedText = newText.Substring(selStart, selEnd - selStart);
                int searchStart = Math.Min(selStart, Math.Max(0, final.Length - selectedText.Length));
                int foundIdx = final.IndexOf(selectedText, searchStart, StringComparison.Ordinal);
                if (foundIdx < 0)
                    foundIdx = final.IndexOf(selectedText, StringComparison.Ordinal);
                if (foundIdx >= 0)
                {
                    selStart = foundIdx;
                    selEnd = foundIdx + selectedText.Length;
                }
                else
                {
                    selStart = Mathf.Clamp(selStart, 0, final.Length);
                    selEnd = Mathf.Clamp(selEnd, selStart, final.Length);
                    if (selEnd <= selStart)
                    {
                        selStart = -1;
                        selEnd = -1;
                    }
                }
            }
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] applyText whole=" + whole + " sel=" + selStart + "," + selEnd
                + " sanitized=" + (final != newText)
                + " text='" + final + "'");
            if (selStart >= 0)
            {
                field.caretPosition = selEnd;
                field.selectionAnchorPosition = selStart;
                field.selectionFocusPosition = selEnd;
            }
            ScheduleChatSelectionCorrection(selStart, selEnd);
        }

        private static string RepairChatMarkup(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] != '<')
                {
                    i++;
                    continue;
                }
                int gt = s.IndexOf('>', i + 1);
                if (gt < 0 || gt - i > 40)
                {
                    i++;
                    continue;
                }
                string inner = s.Substring(i + 1, gt - i - 1);
                if (inner.Length == 0)
                {
                    i++;
                    continue;
                }
                char c0 = inner[0];
                bool tagLike = c0 == 'b' || c0 == 'i' || c0 == 's' || c0 == 'c' || c0 == '/';
                bool valid = inner == "b" || inner == "i" || inner == "/b" || inner == "/i" ||
                             inner == "/size" || inner == "/color" ||
                             inner.StartsWith("size=", StringComparison.Ordinal) ||
                             inner.StartsWith("color=", StringComparison.Ordinal);
                if (tagLike && !valid)
                    s = s.Remove(i, 1);
                else
                    i = gt + 1;
            }
            return s;
        }

        private void RepairChatInputMarkup()
        {
            if (mpChatInputField == null)
                return;
            string cur = mpChatInputField.text ?? "";
            if (cur.IndexOf('<') < 0)
                return;
            int limit = mpChatInputField.characterLimit;
            string repaired = FixChatMarkup(RepairChatMarkup(cur), limit);
            if (repaired == cur)
                return;
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Repaired chat markup: '" + cur + "' -> '" + repaired + "'");
            mpChatInputField.text = repaired;
            FocusChatField(mpChatInputField);
            mpChatApplyFrame = Time.frameCount;
            mpStickyVisLo = -1;
            mpStickyVisHi = -1;
            mpStickyPlainText = null;
            ScheduleChatSelectionCorrection(repaired.Length, repaired.Length);
        }

        private static string SanitizeChatMarkup(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return FixChatMarkup(s);
        }

        private static string StripEmptyTagPairs(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0)
                return s;
            string prev;
            do
            {
                prev = s;
                s = System.Text.RegularExpressions.Regex.Replace(
                    s,
                    "<color=[^>]{0,40}></color>|<size=[^>]{0,40}></size>|<b></b>|<i></i>",
                    "",
                    System.Text.RegularExpressions.RegexOptions.None);
            } while (s != prev);
            return s;
        }

        private static string GetTagType(string openTag)
        {
            if (openTag.StartsWith("<color=", StringComparison.Ordinal))
                return "color";
            if (openTag.StartsWith("<size=", StringComparison.Ordinal))
                return "size";
            if (openTag == "<b>")
                return "b";
            return "i";
        }

        private static string GetTagClose(string openTag)
        {
            return "</" + GetTagType(openTag) + ">";
        }

        private static string FixChatMarkup(string s)
        {
            return FixChatMarkup(s, 0);
        }

        // Balances the tags. Unclosed tags get their closers appended, unless that
        // would pass limit (the chat's character limit, 0 = none): then the
        // unclosed opening tags are dropped instead, which only makes it shorter.
        private static string FixChatMarkup(string s, int limit)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            s = StripEmptyTagPairs(s);
            System.Text.StringBuilder outb = new System.Text.StringBuilder(s.Length + 16);
            List<string> stack = new List<string>();
            List<int> stackAt = new List<int>();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] != '<')
                {
                    outb.Append(s[i]);
                    i++;
                    continue;
                }
                int gt = s.IndexOf('>', i + 1);
                if (gt < 0 || gt - i > 40)
                {
                    outb.Append(s[i]);
                    i++;
                    continue;
                }
                string tag = s.Substring(i, gt - i + 1);
                bool isClose = tag[1] == '/';
                string type = null;
                if (!isClose)
                {
                    if (tag == "<b>") type = "b";
                    else if (tag == "<i>") type = "i";
                    else if (tag.StartsWith("<size=", StringComparison.Ordinal)) type = "size";
                    else if (tag.StartsWith("<color=", StringComparison.Ordinal)) type = "color";
                }
                else
                {
                    if (tag == "</b>") type = "b";
                    else if (tag == "</i>") type = "i";
                    else if (tag == "</size>") type = "size";
                    else if (tag == "</color>") type = "color";
                }
                if (type == null)
                {
                    outb.Append(s[i]);
                    i++;
                    continue;
                }
                if (!isClose)
                {
                    stack.Add(tag);
                    stackAt.Add(outb.Length);
                    outb.Append(tag);
                    i = gt + 1;
                    continue;
                }
                int k = -1;
                for (int j = stack.Count - 1; j >= 0; j--)
                {
                    if (GetTagType(stack[j]) == type)
                    {
                        k = j;
                        break;
                    }
                }
                if (k < 0)
                {
                    i = gt + 1;
                    continue;
                }
                for (int j = stack.Count - 1; j > k; j--)
                    outb.Append(GetTagClose(stack[j]));
                outb.Append(tag);
                stack.RemoveRange(k, stack.Count - k);
                stackAt.RemoveRange(k, stackAt.Count - k);
                i = gt + 1;
            }
            int closersLen = 0;
            for (int j = 0; j < stack.Count; j++)
                closersLen += GetTagClose(stack[j]).Length;
            if (limit > 0 && outb.Length + closersLen > limit)
            {
                // Later openers sit after earlier ones, so removing from the end
                // keeps the recorded positions valid.
                for (int j = stack.Count - 1; j >= 0; j--)
                    outb.Remove(stackAt[j], stack[j].Length);
                return outb.ToString();
            }
            for (int j = stack.Count - 1; j >= 0; j--)
                outb.Append(GetTagClose(stack[j]));
            return outb.ToString();
        }

        private static string StripChatTags(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return System.Text.RegularExpressions.Regex.Replace(s,
                "</?(?:b|i|size|color|material|quad|link|mark)(?:\\s*=[^>]*)?\\s*/?>",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static Texture2D CreateRoundedRectTexture(int size, int radius)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = Mathf.Max(1f, radius);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float cx = Mathf.Clamp(px, r, size - r);
                    float cy = Mathf.Clamp(py, r, size - r);
                    float dx = px - cx;
                    float dy = py - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(r - d + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
