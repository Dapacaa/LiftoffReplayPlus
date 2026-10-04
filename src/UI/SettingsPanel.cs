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
        private float GetSettingsPanelHeight()
        {
            int pilotCount = GhostLinesPlugin.entries.Count == 0 ? 1 : GhostLinesPlugin.entries.Count;
            float height = 42f;
            height += 20f + 28f * pilotCount;
            height += 18f * 4f;
            height += 34f * 4f;
            height += 34f * 3f;
            height += 30f * 3f;
            return height + 12f;
        }

        private void EnsureSettingsPanelRect(bool initializePosition = true)
        {
            float panelH = GetSettingsPanelHeight();
            float panelW = 400f;
            if (initializePosition && settingsRect.x < 0f)
            {
                float defaultX = Screen.width - panelW - 24f;
                float defaultY = Screen.height - panelH - 24f;
                if (savedSettingsPanelPositionCustomized)
                {
                    defaultX = savedSettingsPanelPositionX * Screen.width;
                    defaultY = savedSettingsPanelPositionY * Screen.height;
                }
                settingsRect = new Rect(defaultX, defaultY, panelW, panelH);
            }
            else
            {
                float bottom = settingsRect.y + settingsRect.height;
                settingsRect.width = panelW;
                settingsRect.height = panelH;
                settingsRect.y = bottom - panelH;
            }

            settingsRect.x = Mathf.Clamp(settingsRect.x, 12f, Mathf.Max(12f, Screen.width - settingsRect.width - 12f));
            settingsRect.y = Mathf.Clamp(settingsRect.y, 12f, Mathf.Max(12f, Screen.height - settingsRect.height - 12f));
        }

        private Rect GetSettingsPanelRect()
        {
            EnsureSettingsPanelRect();
            return settingsRect;
        }

        private void EnsureSettingsStyles()
        {
            if (settingsLabelStyle == null)
            {
            settingsLabelStyle = new GUIStyle(GUI.skin.label);
            settingsLabelStyle.fontSize = 12;
            settingsLabelStyle.alignment = TextAnchor.MiddleLeft;
            settingsLabelStyle.wordWrap = false;
                settingsSmallStyle = new GUIStyle(settingsLabelStyle);
                settingsSmallStyle.fontSize = 10;
                settingsNameStyle = new GUIStyle(settingsSmallStyle);
                settingsNameStyle.fontSize = 12;
                settingsNameStyle.fontStyle = FontStyle.Bold;
                settingsNameStyle.clipping = TextClipping.Overflow;
                // Flat: the caller draws the box in the option colours like every
                // other control (the skin's text field has rounded corners).
                settingsTextFieldStyle = new GUIStyle(GUI.skin.textField);
                settingsTextFieldStyle.fontSize = settingsSmallStyle.fontSize;
                settingsTextFieldStyle.alignment = TextAnchor.MiddleLeft;
                settingsTextFieldStyle.border = new RectOffset(0, 0, 0, 0);
                settingsTextFieldStyle.padding = new RectOffset(6, 6, 2, 2);
                foreach (GUIStyleState st in new[] { settingsTextFieldStyle.normal, settingsTextFieldStyle.hover,
                    settingsTextFieldStyle.focused, settingsTextFieldStyle.active, settingsTextFieldStyle.onNormal,
                    settingsTextFieldStyle.onHover, settingsTextFieldStyle.onFocused, settingsTextFieldStyle.onActive })
                    st.background = null;
                settingsSmallCenterStyle = new GUIStyle(settingsSmallStyle);
                settingsSmallCenterStyle.alignment = TextAnchor.MiddleCenter;
                settingsAxisLabelStyle = new GUIStyle(settingsSmallCenterStyle);
                settingsAxisLabelStyle.clipping = TextClipping.Overflow;
                settingsHeaderStyle = new GUIStyle(settingsLabelStyle);
                settingsHeaderStyle.fontSize = 13;
                settingsHeaderStyle.fontStyle = FontStyle.Bold;
                settingsHeaderStyle.wordWrap = false;
                settingsHeaderStyle.clipping = TextClipping.Clip;
                settingsPlusStyle = new GUIStyle(settingsHeaderStyle);
                settingsPlusStyle.fontSize = 14;
                settingsPlusStyle.alignment = TextAnchor.MiddleCenter;
                settingsCloseStyle = new GUIStyle(settingsHeaderStyle);
                settingsCloseStyle.alignment = TextAnchor.MiddleCenter;
                // Larger X for window headers (matches the pin button height range).
                settingsCloseBigStyle = new GUIStyle(settingsCloseStyle);
                settingsCloseBigStyle.fontSize = 24;
                settingsSectionStyle = new GUIStyle(settingsLabelStyle);
                settingsSectionStyle.fontSize = 10;
                settingsSectionStyle.fontStyle = FontStyle.Bold;
                settingsSectionStyle.alignment = TextAnchor.MiddleCenter;
                settingsSmallRightStyle = new GUIStyle(settingsSmallStyle);
                settingsSmallRightStyle.alignment = TextAnchor.MiddleRight;
                settingsSectionRightStyle = new GUIStyle(settingsSectionStyle);
                settingsSectionRightStyle.alignment = TextAnchor.MiddleRight;
                settingsSectionLeftStyle = new GUIStyle(settingsSectionStyle);
                settingsSectionLeftStyle.alignment = TextAnchor.MiddleLeft;
                settingsSectionLeftStyle.clipping = TextClipping.Overflow;
                settingsDropdownBarStyle = new GUIStyle(settingsSmallStyle);
                settingsDropdownBarStyle.alignment = TextAnchor.MiddleLeft;
                settingsDropdownBarStyle.clipping = TextClipping.Overflow;
                settingsDropdownBarStyle.contentOffset = new Vector2(8f, 0f);
                settingsPilotHeaderStyle = new GUIStyle(settingsSectionStyle);
                settingsPilotHeaderStyle.fontSize = 9;
            }
            if (settingsStylesDarkMode != darkMode)
            {
                settingsStylesDarkMode = darkMode;
                if (darkMode)
                {
                    settingsLabelStyle.normal.textColor = new Color(0.88f, 0.88f, 0.88f);
                    settingsSmallStyle.normal.textColor = new Color(0.74f, 0.74f, 0.76f);
                    if (settingsNameStyle != null)
                        settingsNameStyle.normal.textColor = new Color(0.92f, 0.92f, 0.94f);
                    settingsHeaderStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
                    settingsSectionStyle.normal.textColor = new Color(0.6f, 0.6f, 0.62f);
                    if (settingsPilotHeaderStyle != null)
                        settingsPilotHeaderStyle.normal.textColor = new Color(0.6f, 0.6f, 0.62f);
                    if (settingsTextFieldStyle != null)
                        SetTextFieldTextColor(settingsTextFieldStyle, new Color(0.92f, 0.92f, 0.92f));
                    if (settingsAxisLabelStyle != null)
                        settingsAxisLabelStyle.normal.textColor = new Color(0.74f, 0.74f, 0.76f);
                    if (settingsSmallRightStyle != null)
                        settingsSmallRightStyle.normal.textColor = new Color(0.74f, 0.74f, 0.76f);
                    if (settingsSectionRightStyle != null)
                        settingsSectionRightStyle.normal.textColor = new Color(0.6f, 0.6f, 0.62f);
                    if (settingsSectionLeftStyle != null)
                        settingsSectionLeftStyle.normal.textColor = new Color(0.6f, 0.6f, 0.62f);
                }
                else
                {
                    settingsLabelStyle.normal.textColor = new Color(0.12f, 0.12f, 0.12f);
                    settingsSmallStyle.normal.textColor = new Color(0.38f, 0.38f, 0.38f);
                    if (settingsNameStyle != null)
                        settingsNameStyle.normal.textColor = new Color(0.08f, 0.08f, 0.1f);
                    settingsHeaderStyle.normal.textColor = new Color(0.12f, 0.12f, 0.12f);
                    settingsSectionStyle.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
                    if (settingsPilotHeaderStyle != null)
                        settingsPilotHeaderStyle.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
                    if (settingsTextFieldStyle != null)
                        SetTextFieldTextColor(settingsTextFieldStyle, new Color(0.12f, 0.12f, 0.12f));
                    if (settingsAxisLabelStyle != null)
                        settingsAxisLabelStyle.normal.textColor = new Color(0.38f, 0.38f, 0.38f);
                    if (settingsSmallRightStyle != null)
                        settingsSmallRightStyle.normal.textColor = new Color(0.38f, 0.38f, 0.38f);
                    if (settingsSectionRightStyle != null)
                        settingsSectionRightStyle.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
                    if (settingsSectionLeftStyle != null)
                        settingsSectionLeftStyle.normal.textColor = new Color(0.25f, 0.25f, 0.25f);
                }
            }
            if (settingsTooltipStyle == null || settingsTooltipDarkMode != darkMode)
            {
                settingsTooltipStyle = new GUIStyle(settingsSmallStyle);
                settingsTooltipStyle.normal.textColor = darkMode ? Color.white : new Color(0.1f, 0.1f, 0.1f);
                settingsTooltipDarkMode = darkMode;
            }
        }

        private Texture2D CreateTargetTexture(int size, int kind)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size - 0.5f;
                    float ny = (y + 0.5f) / size - 0.5f;
                    float radius = Mathf.Sqrt(nx * nx + ny * ny);
                    bool filled = kind == 0
                        ? radius > 0.32f && radius < 0.47f
                        : kind == 1 ? radius < 0.25f : radius < 0.5f;
                    tex.SetPixel(x, y, filled ? Color.white : Color.clear);
                }
            }
            tex.Apply(true);
            return tex;
        }

        private void EnsureSettingsTextures()
        {
            if (targetRingTex != null) return;
            targetRingTex = CreateTargetTexture(72, 0);
            targetDotTex = CreateTargetTexture(72, 1);
            targetFillTex = CreateTargetTexture(72, 2);
        }

        private Color ThemePanelBg(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.12f, 0.12f, 0.13f, 0.96f * alpha) : new Color(0.97f, 0.97f, 0.97f, 0.96f * alpha); }
        private Color ThemePanelBorder(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.48f, 0.48f, 0.5f, 0.6f * alpha) : new Color(0.32f, 0.32f, 0.32f, 0.65f * alpha); }
        private Color ThemeLabel(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(1f, 1f, 1f, alpha) : new Color(0.13f, 0.13f, 0.13f, alpha); }
        private Color ThemeDimLabel(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.8f, 0.8f, 0.8f, alpha) : new Color(0.42f, 0.42f, 0.42f, alpha); }
        private Color ThemeCloseLabel(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.85f, 0.85f, 0.85f, alpha) : new Color(0.35f, 0.35f, 0.35f, alpha); }
        private static void SetTextFieldTextColor(GUIStyle style, Color c)
        {
            style.normal.textColor = c;
            style.hover.textColor = c;
            style.focused.textColor = c;
            style.active.textColor = c;
        }

        private Color ThemeOptionBg(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.24f, 0.24f, 0.26f, alpha) : new Color(0.93f, 0.93f, 0.93f, alpha); }
        private Color ThemeOptionBorder(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.42f, 0.42f, 0.45f, 0.6f * alpha) : new Color(0.55f, 0.55f, 0.55f, 0.65f * alpha); }
        private Color ThemeOptionText(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.9f, 0.9f, 0.9f, alpha) : new Color(0.15f, 0.15f, 0.15f, alpha); }
        private Color ThemeToggleOff(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.4f, 0.4f, 0.42f, alpha) : new Color(0.68f, 0.68f, 0.68f, alpha); }
        private Color ThemeTrack(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.45f, 0.45f, 0.48f, 0.45f * alpha) : new Color(0.3f, 0.3f, 0.3f, 0.4f * alpha); }
        private Color ThemeHandle(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.7f, 0.7f, 0.72f, alpha) : new Color(0.85f, 0.85f, 0.85f, alpha); }
        private Color ThemeBarBg(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.13f, 0.13f, 0.14f, 0.9f * alpha) : new Color(1f, 1f, 1f, 0.9f * alpha); }
        private Color ThemeBarBorder(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.45f, 0.45f, 0.47f, alpha) : new Color(0.6f, 0.6f, 0.6f, alpha); }
        private Color ThemeButtonBg(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.27f, 0.27f, 0.29f, 0.92f * alpha) : new Color(0.9f, 0.9f, 0.9f, 0.92f * alpha); }
        private Color ThemeButtonBorder(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.5f, 0.5f, 0.52f, 0.55f * alpha) : new Color(0.35f, 0.35f, 0.35f, 0.5f * alpha); }
        private Color ThemeButtonIcon(float alpha) { alpha *= uiFadeAlpha; return darkMode ? new Color(0.88f, 0.88f, 0.88f, alpha) : new Color(0.2f, 0.2f, 0.2f, alpha); }

        private bool DrawSettingsOption(Rect rect, string label, bool selected, float alpha, TextAnchor align = TextAnchor.MiddleLeft, int guardLayer = -1, bool allowInteraction = true, bool disabled = false, GUIStyle styleOverride = null)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition) && !disabled;
            bool blocked = guardLayer >= 0 && IsUiBlockedFor(guardLayer, e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover && !blocked && allowInteraction)
                settingsControlPressed = true;
            Color old = GUI.color;
            GUI.color = disabled
                ? new Color(0.36f, 0.36f, 0.4f, 0.6f * alpha)
                : selected
                    ? new Color(0.25f, 0.62f, 0.95f, alpha)
                    : ThemeOptionBg(alpha);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = hover
                ? new Color(0.2f, 0.2f, 0.2f, alpha)
                : ThemeOptionBorder(alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), whiteTex);
            GUI.color = disabled
                ? new Color(0.75f, 0.75f, 0.78f, 0.5f * alpha)
                : selected ? new Color(1f, 1f, 1f, alpha) : ThemeOptionText(alpha);
            GUIStyle labelStyle = styleOverride != null ? styleOverride : (align == TextAnchor.MiddleCenter ? settingsSmallCenterStyle : settingsSmallStyle);
            GUI.Label(rect, label, labelStyle);
            GUI.color = old;
            if (e.type == EventType.MouseUp && e.button == 0 && hover && !settingsDrag.Dragging && !blocked && allowInteraction)
            {
                e.Use();
                if (guardLayer >= 0)
                    uiFrontLayer = guardLayer;
                return true;
            }
            return false;
        }

        private bool DrawSettingsToggle(Rect rect, bool enabled, float alpha, bool allowInteraction = true)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            bool pipBlocked = IsUiBlockedFor(UI_LAYER_SETTINGS, e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover && !pipBlocked && allowInteraction)
                settingsControlPressed = true;
            Color old = GUI.color;
            GUI.color = enabled
                ? new Color(0.25f, 0.62f, 0.95f, alpha)
                : ThemeToggleOff(alpha);
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            float knob = rect.height - 4f;
            float knobX = enabled ? rect.xMax - knob - 2f : rect.x + 2f;
            GUI.DrawTexture(new Rect(knobX, rect.y + 2f, knob, knob), whiteTex);
            GUI.color = old;
            if (e.type == EventType.MouseUp && e.button == 0 && hover && !settingsDrag.Dragging && !pipBlocked && allowInteraction)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private float DrawSettingsSectionDivider(float x, float w, float y, string label, float alpha)
        {
            Color prev = GUI.color;
            GUI.color = darkMode ? new Color(0.88f, 0.9f, 0.93f, alpha) : new Color(0.25f, 0.25f, 0.28f, alpha);
            GUI.Label(new Rect(x + 4f, y, 180f, 16f), label, settingsSectionLeftStyle);
            GUI.color = darkMode ? new Color(1f, 1f, 1f, 0.25f * alpha) : new Color(0.38f, 0.38f, 0.4f, 0.7f * alpha);
            GUI.DrawTexture(new Rect(x, y + 15f, w, 1f), whiteTex);
            GUI.color = prev;
            return y + 18f;
        }

        private bool DrawSettingsTarget(Rect rect, bool selected, float alpha, int column, int rowIndex)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            bool blocked = IsUiBlockedFor(UI_LAYER_SETTINGS, e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover && !blocked)
            {
                settingsControlPressed = true;
                settingsDragSelecting = true;
                settingsDragSelectMoved = false;
                settingsDragSelectColumn = column;
                settingsDragSelectStartColumn = column;
                settingsDragSelectStartRow = rowIndex;
                settingsDragSelectStartValue = selected;
                settingsDragSelectLastIndex = rowIndex;
                settingsDragSelectLastColumn = column;
                settingsDragSelectProcessed.Clear();
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
            if (e.type == EventType.MouseUp && e.button == 0 && hover && !settingsDrag.Dragging && !settingsDragSelectMoved && !blocked)
            {
                e.Use();
                return true;
            }
            return false;
        }

        private void HandleSettingsRadioDrag(int rowIndex, int column, Rect rect)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDrag || e.button != 0)
                return;
            if (!settingsDragSelecting)
                return;
            if (!rect.Contains(e.mousePosition))
                return;
            if (rowIndex == settingsDragSelectLastIndex && column == settingsDragSelectLastColumn)
                return;
            settingsDragSelectMoved = true;
            int minRow = Mathf.Min(settingsDragSelectStartRow, rowIndex);
            int maxRow = Mathf.Max(settingsDragSelectStartRow, rowIndex);
            int minCol = Mathf.Min(settingsDragSelectStartColumn, column);
            int maxCol = Mathf.Max(settingsDragSelectStartColumn, column);
            bool target = !settingsDragSelectStartValue;
            for (int r = minRow; r <= maxRow; r++)
            {
                for (int c = minCol; c <= maxCol; c++)
                {
                    if (settingsDragSelectProcessed.Add(r * 16 + c))
                        ApplySettingsRadioState(r, c, target);
                }
            }
            settingsDragSelectLastIndex = rowIndex;
            settingsDragSelectLastColumn = column;
        }

        private void ApplySettingsRadioState(int rowIndex, int column, bool value)
        {
            if (rowIndex < 0 || rowIndex >= GhostLinesPlugin.entries.Count)
                return;
            GhostLineEntry entry = GhostLinesPlugin.entries[rowIndex];
            switch (column)
            {
                case 0:
                    if (value)
                    {
                        referenceEntry = entry;
                        MarkPiPWindowsDirty();
                    }
                    break;
                case 1:
                {
                    if (IsStickGraphOpenFor(entry) != value)
                        ToggleStickGraphFor(entry);
                    break;
                }
                case 2:
                {
                    bool hasInstance = false;
                    for (int s = 0; s < stickCamInstances.Count; s++)
                    {
                        if (stickCamInstances[s].entry == entry)
                        {
                            hasInstance = true;
                            break;
                        }
                    }
                    if (value && !hasInstance)
                        AddStickCamInstance(entry, null);
                    else if (!value && hasInstance)
                        RemoveStickCamInstance(entry, null);
                    break;
                }
                case 3:
                    entry.deltaDisplayVisible = value;
                    break;
                case 4:
                    entry.nameTagVisible = value;
                    break;
                case 5:
                {
                    bool open = false;
                    for (int j = 0; j < pipWindows.Count; j++)
                    {
                        if (pipWindows[j].entry == entry)
                        {
                            open = true;
                            break;
                        }
                    }
                    if (open != value)
                        TogglePiPWindow(entry);
                    break;
                }
                case 6:
                    SetRaceLineVisible(entry, value);
                    break;
            }
        }

        private void DrawSettingsPanel()
        {
            if (!settingsOpen || settingsFadeAmount < 0.01f)
            {
                // Faded out, so this panel sees no events; a leftover arm would
                // promote on the first drag after it fades back in (e.g. dragging
                // the stick cam) and snap the panel to the mouse.
                if (!settingsDrag.Dragging)
                    settingsDrag.Cancel();
                return;
            }
            EnsureSettingsStyles();
            EnsureSettingsTextures();
            Rect panel = GetSettingsPanelRect();
            float alpha = settingsFadeAmount;
            Color old = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.12f * alpha);
            GUI.DrawTexture(new Rect(panel.x + 3f, panel.y + 3f, panel.width - 6f, panel.height), whiteTex);
            GUI.color = ThemePanelBg(alpha);
            GUI.DrawTexture(panel, whiteTex);
            GUI.color = ThemePanelBorder(alpha);
            GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 1f, panel.width, 1f), whiteTex);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 1f, panel.height), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 1f, panel.y, 1f, panel.height), whiteTex);

            Event e = Event.current;
            Rect close = new Rect(panel.xMax - 36f, panel.y + 6f, 28f, 22f);
            Rect pin = new Rect(panel.xMax - 64f, panel.y + 7f, 22f, 20f);
            if (e.type == EventType.MouseDown && e.button == 0 && panel.Contains(e.mousePosition) && !close.Contains(e.mousePosition)
                && !IsUiBlockedFor(UI_LAYER_SETTINGS, e.mousePosition))
            {
                settingsControlPressed = false;
                uiFrontLayer = UI_LAYER_SETTINGS;
                settingsDrag.Arm(e.mousePosition, new Vector2(panel.x, panel.y));
            }
            // Controls set settingsControlPressed on press, so dragging a toggle or
            // slider doesn't move the panel.
            Vector2 settingsPos = new Vector2(settingsRect.x, settingsRect.y);
            if (settingsDrag.Drag(e, settingsControlPressed || pipFarSliderDragging || close.Contains(e.mousePosition), ref settingsPos))
            {
                settingsPanelPositionCustomized = true;
                settingsRect.x = settingsPos.x;
                settingsRect.y = settingsPos.y;
                EnsureSettingsPanelRect(false);
            }

            GUI.color = ThemeLabel(alpha);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 7f, 220f, 20f), "Settings", settingsHeaderStyle);
            EnsureIcons();
            if (DrawStyledToggleButton(pin, iconPin, settingsPinned, alpha, true, UI_LAYER_SETTINGS))
                settingsPinned = !settingsPinned;
            GUI.color = ThemeCloseLabel(alpha);
            GUI.Label(new Rect(close.x, close.y - 3f, close.width, close.height),
                "x", settingsCloseBigStyle != null ? settingsCloseBigStyle : settingsCloseStyle);
            if (e.type == EventType.MouseUp && e.button == 0 && close.Contains(e.mousePosition))
            {
                settingsOpen = false;
                SaveSettingsPanelOpen(false);
                settingsControlPressed = false;
                settingsDrag.Cancel();
                pipFarSliderDragging = false;
                settingsDragSelecting = false;
                settingsDragSelectMoved = false;
                settingsDragSelectColumn = -1;
                settingsDragSelectLastIndex = -1;
                settingsDragSelectProcessed.Clear();
                e.Use();
                GUI.color = old;
                return;
            }

            float x = panel.x + 14f;
            float w = panel.width - 28f;
            float y = panel.y + 42f;
            bool anyVisible = false;
            foreach (var entry in GhostLinesPlugin.entries)
                if (entry.visible) { anyVisible = true; break; }

            string headerTip;
            y = DrawPilotRows(panel, x, w, alpha, y, out headerTip);

            GUI.color = ThemeLabel(alpha);
            y = DrawSettingsSectionDivider(x, w, y, "Lines", alpha);

            float halfW = w * 0.5f;
            float col1ToggleX = x + halfW - 48f;
            float col2X = x + halfW;
            float col2ToggleX = col2X + halfW - 48f;
            float colLabelW = halfW - 48f;

            GUI.Label(new Rect(x, y, colLabelW, 24f), "Race Lines", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col1ToggleX, y + 3f, 30f, 18f), anyVisible, alpha))
                SetAllRaceLinesVisible(!anyVisible);
            GUI.Label(new Rect(col2X, y, colLabelW, 24f), "Through Objects", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col2ToggleX, y + 3f, 30f, 18f), GhostLinesPlugin.linesThroughObjects, alpha))
                GhostLinesPlugin.SetLinesThroughObjects(!GhostLinesPlugin.linesThroughObjects);
            y += 34f;

            GUI.Label(new Rect(x, y, colLabelW, 24f), "Line Smoothing", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col1ToggleX, y + 3f, 30f, 18f), GhostLinesPlugin.smoothingEnabled, alpha))
                GhostLinesPlugin.ToggleSmoothing();
            GUI.Label(new Rect(col2X, y, colLabelW, 24f), "Speed Heatmap", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col2ToggleX, y + 3f, 30f, 18f), GhostLinesPlugin.speedHeatmapEnabled, alpha))
                GhostLinesPlugin.ToggleSpeedHeatmap();
            y += 34f;

            GUI.color = ThemeLabel(alpha);
            y = DrawSettingsSectionDivider(x, w, y, "Overlay", alpha);

            GUI.Label(new Rect(x, y, colLabelW, 24f), "Delta Display", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col1ToggleX, y + 3f, 30f, 18f), GhostLinesPlugin.showDeltaDisplay, alpha))
                GhostLinesPlugin.SetDeltaDisplay(!GhostLinesPlugin.showDeltaDisplay);
            GUI.Label(new Rect(col2X, y, colLabelW, 24f), "Name Tags", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col2ToggleX, y + 3f, 30f, 18f), GhostLinesPlugin.showNameTags, alpha))
                SetNameTagsVisible(!GhostLinesPlugin.showNameTags);
            y += 34f;

            GUI.color = ThemeLabel(alpha);
            y = DrawSettingsSectionDivider(x, w, y, "Effects/UI", alpha);

            GUI.Label(new Rect(x, y, colLabelW, 24f), "Dark Mode", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col1ToggleX, y + 3f, 30f, 18f), darkMode, alpha))
            {
                darkMode = !darkMode;
                SaveDisplayPreferences();
            }
            GUI.Label(new Rect(col2X, y, colLabelW, 24f), "Xray", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col2ToggleX, y + 3f, 30f, 18f), xrayEnabled, alpha))
                SetXrayEnabled(!xrayEnabled);
            y += 34f;

            GUI.Label(new Rect(x, y, colLabelW, 24f), "Stick Cam", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col1ToggleX, y + 3f, 30f, 18f), stickCamEnabled, alpha))
            {
                stickCamEnabled = !stickCamEnabled;
                SaveDisplayPreferences();
            }
            GUI.Label(new Rect(col2X, y, colLabelW, 24f), "Drone Trails", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col2ToggleX, y + 3f, 30f, 18f), smoothDroneTrails, alpha))
                SetSmoothDroneTrails(!smoothDroneTrails);
            y += 34f;

            GUI.color = ThemeLabel(alpha);
            y = DrawSettingsSectionDivider(x, w, y, "Picture in Picture", alpha);

            float pipToggleLabelW = CalcLabelWidth(settingsLabelStyle, "Picture in Picture");
            GUI.Label(new Rect(x, y, pipToggleLabelW, 24f), "Picture in Picture", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(x + pipToggleLabelW + 12f, y + 3f, 30f, 18f), pipEnabled, alpha))
                SetPipEnabled(!pipEnabled);
            float ndLabelW = CalcLabelWidth(settingsLabelStyle, "Names/Delta in PiP");
            GUI.Label(new Rect(col2X, y, ndLabelW, 24f), "Names/Delta in PiP", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(col2X + ndLabelW + 12f, y + 3f, 30f, 18f), pipShowNamesDelta, alpha))
                SetPipShowNamesDelta(!pipShowNamesDelta);
            y += 34f;

            float extLabelW = CalcLabelWidth(settingsLabelStyle, "External Monitor PiP");
            GUI.Label(new Rect(x, y, extLabelW, 24f), "External Monitor PiP", settingsLabelStyle);
            if (DrawSettingsToggle(new Rect(x + extLabelW + 12f, y + 3f, 30f, 18f), pipExternalMonitor, alpha))
                SetPipExternalMonitor(!pipExternalMonitor);
            y += 34f;

            GUI.Label(new Rect(x, y, 110f, 20f), "PIP Refresh Rate", settingsLabelStyle);
            float pipGap = 6f;
            float pipPad = 16f;
            float pipAutoW = CalcLabelWidth(settingsSmallStyle, "Auto") + pipPad;
            float pip15W = CalcLabelWidth(settingsSmallStyle, "15 Hz") + pipPad;
            float pip30W = CalcLabelWidth(settingsSmallStyle, "30 Hz") + pipPad;
            float pip60W = CalcLabelWidth(settingsSmallStyle, "60 Hz") + pipPad;
            float pipBtnsX = panel.xMax - 14f - (pipAutoW + pipGap + pip15W + pipGap + pip30W + pipGap + pip60W);
            if (DrawSettingsOption(new Rect(pipBtnsX, y - 2f, pipAutoW, 22f), "Auto", pipRefreshAuto, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS) && !pipRefreshAuto)
                SetPipRefreshAuto(true);
            if (DrawSettingsOption(new Rect(pipBtnsX + pipAutoW + pipGap, y - 2f, pip15W, 22f), "15 Hz", !pipRefreshAuto && pipRefreshHz == 15, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS))
                SetPipRefreshHz(15);
            if (DrawSettingsOption(new Rect(pipBtnsX + pipAutoW + pipGap + pip15W + pipGap, y - 2f, pip30W, 22f), "30 Hz", !pipRefreshAuto && pipRefreshHz == 30, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS))
                SetPipRefreshHz(30);
            if (DrawSettingsOption(new Rect(pipBtnsX + pipAutoW + pipGap + pip15W + pipGap + pip30W + pipGap, y - 2f, pip60W, 22f), "60 Hz", !pipRefreshAuto && pipRefreshHz == 60, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS))
                SetPipRefreshHz(60);
            y += 30f;

            GUI.Label(new Rect(x, y, 110f, 20f), "PIP Resolution", settingsLabelStyle);
            float pip25W = CalcLabelWidth(settingsSmallStyle, "25%") + pipPad;
            float pip50W = CalcLabelWidth(settingsSmallStyle, "50%") + pipPad;
            float pip75W = CalcLabelWidth(settingsSmallStyle, "75%") + pipPad;
            float pip100W = CalcLabelWidth(settingsSmallStyle, "100%") + pipPad;
            float pipResBtnsX = panel.xMax - 14f - (pip25W + pipGap + pip50W + pipGap + pip75W + pipGap + pip100W);
            if (DrawSettingsOption(new Rect(pipResBtnsX, y - 2f, pip25W, 22f), "25%", pipResolutionScale == 0.25f, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS) && pipResolutionScale != 0.25f)
                SetPipResolutionScale(0.25f);
            if (DrawSettingsOption(new Rect(pipResBtnsX + pip25W + pipGap, y - 2f, pip50W, 22f), "50%", pipResolutionScale == 0.5f, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS) && pipResolutionScale != 0.5f)
                SetPipResolutionScale(0.5f);
            if (DrawSettingsOption(new Rect(pipResBtnsX + pip25W + pipGap + pip50W + pipGap, y - 2f, pip75W, 22f), "75%", pipResolutionScale == 0.75f, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS) && pipResolutionScale != 0.75f)
                SetPipResolutionScale(0.75f);
            if (DrawSettingsOption(new Rect(pipResBtnsX + pip25W + pipGap + pip50W + pipGap + pip75W + pipGap, y - 2f, pip100W, 22f), "100%", pipResolutionScale == 1f, alpha, TextAnchor.MiddleCenter, UI_LAYER_SETTINGS) && pipResolutionScale != 1f)
                SetPipResolutionScale(1f);
            y += 30f;

            GUI.Label(new Rect(x, y, 110f, 20f), "PIP Draw Distance", settingsLabelStyle);
            float distTrackW = 110f;
            float distValW = 46f;
            float distTrackX = panel.xMax - 14f - distValW - 6f - distTrackW;
            Rect distTrack = new Rect(distTrackX, y + 8f, distTrackW, 6f);
            Rect distValRect = new Rect(panel.xMax - 14f - distValW, y, distValW, 20f);
            Rect distHandle = new Rect(distTrack.x + Mathf.Clamp01((pipFarClip - 25f) / 475f) * (distTrack.width - 10f), distTrack.y - 4f, 10f, 14f);
            bool distHit = distHandle.Contains(e.mousePosition) || distTrack.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && distHit && !IsUiBlockedFor(UI_LAYER_SETTINGS, e.mousePosition))
            {
                pipFarSliderDragging = true;
                settingsDrag.Cancel();
                settingsControlPressed = false;
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
            GUI.color = darkMode ? new Color(0.2f, 0.22f, 0.26f, alpha) : new Color(0.6f, 0.6f, 0.62f, alpha);
            GUI.DrawTexture(distTrack, whiteTex);
            GUI.color = pipFarSliderDragging || distHit ? new Color(0.25f, 0.62f, 0.95f, alpha) : darkMode ? new Color(0.7f, 0.72f, 0.76f, alpha) : new Color(0.42f, 0.42f, 0.45f, alpha);
            GUI.DrawTexture(distHandle, whiteTex);
            GUI.color = ThemeLabel(alpha);
            GUI.Label(distValRect, Mathf.RoundToInt(pipFarClip) + "m", settingsSmallRightStyle);
            y += 30f;

            // rawType: a toggle/button above may have used the release, and the
            // press state below must still clear or it leaks into the next click.
            if (e.rawType == EventType.MouseUp && e.button == 0)
            {
                if (settingsDrag.End())
                {
                    SaveCurrentOverlayPositions();
                    e.Use();
                }
                settingsControlPressed = false;
                settingsDragSelecting = false;
                settingsDragSelectMoved = false;
                settingsDragSelectColumn = -1;
                settingsDragSelectLastIndex = -1;
                settingsDragSelectProcessed.Clear();
            }

            if (headerTip != null)
            {
                Vector2 mousePos = Event.current.mousePosition;
                float tipW = 230f;
                float tipH = 20f;
                float tipX = Mathf.Clamp(mousePos.x + 16f, 4f, Screen.width - tipW - 4f);
                float tipY = Mathf.Clamp(mousePos.y + 20f, 4f, Screen.height - tipH - 4f);
                if (darkMode)
                {
                    GUI.color = new Color(0.05f, 0.05f, 0.06f, 0.95f * alpha);
                    GUI.DrawTexture(new Rect(tipX, tipY, tipW, tipH), whiteTex);
                    GUI.color = new Color(0.35f, 0.55f, 0.8f, alpha);
                    GUI.DrawTexture(new Rect(tipX, tipY, tipW, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(tipX, tipY + tipH - 1f, tipW, 1f), whiteTex);
                    GUI.DrawTexture(new Rect(tipX, tipY, 1f, tipH), whiteTex);
                    GUI.DrawTexture(new Rect(tipX + tipW - 1f, tipY, 1f, tipH), whiteTex);
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.Label(new Rect(tipX + 6f, tipY + 2f, tipW - 12f, tipH - 4f), headerTip, settingsTooltipStyle);
                }
                else
                {
                    GUI.color = new Color(0f, 0f, 0f, alpha);
                    GUI.DrawTexture(new Rect(tipX - 1f, tipY - 1f, tipW + 2f, tipH + 2f), whiteTex);
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.DrawTexture(new Rect(tipX, tipY, tipW, tipH), whiteTex);
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.Label(new Rect(tipX + 6f, tipY + 2f, tipW - 12f, tipH - 4f), headerTip, settingsTooltipStyle);
                }
            }

            GUI.color = old;
        }

        private float DrawPilotRows(Rect panel, float x, float w, float alpha, float colTop, out string headerTip)
        {
            Event e = Event.current;
            float y = colTop;
            GUI.color = ThemeLabel(alpha);
            Rect headerDelta = new Rect(panel.xMax - 262f, y, 34f, 18f);
            Rect headerRef = new Rect(panel.xMax - 221f, y, 36f, 18f);
            Rect headerGraph = new Rect(panel.xMax - 191f, y, 36f, 18f);
            Rect headerStick = new Rect(panel.xMax - 161f, y, 36f, 18f);
            Rect headerDText = new Rect(panel.xMax - 131f, y, 36f, 18f);
            Rect headerTag = new Rect(panel.xMax - 101f, y, 36f, 18f);
            Rect headerPip = new Rect(panel.xMax - 71f, y, 36f, 18f);
            Rect headerLine = new Rect(panel.xMax - 41f, y, 36f, 18f);
            GUI.Label(headerDelta, "Delta", settingsPilotHeaderStyle);
            GUI.Label(headerRef, "Ref", settingsPilotHeaderStyle);
            GUI.Label(headerGraph, "Graph", settingsPilotHeaderStyle);
            GUI.Label(headerStick, "Stick", settingsPilotHeaderStyle);
            GUI.Label(headerDText, "DText", settingsPilotHeaderStyle);
            GUI.Label(headerTag, "Tag", settingsPilotHeaderStyle);
            GUI.Label(headerPip, "PIP", settingsPilotHeaderStyle);
            GUI.Label(headerLine, "Line", settingsPilotHeaderStyle);
            GUI.Label(new Rect(x, y, 100f, 18f), "Pilots", settingsSectionLeftStyle);
            Vector2 mousePos = e.mousePosition;
            headerTip = null;
            if (headerDelta.Contains(mousePos)) headerTip = "Seconds ahead/behind vs the reference pilot";
            else if (headerRef.Contains(mousePos)) headerTip = "Reference pilot for delta times";
            else if (headerGraph.Contains(mousePos)) headerTip = "Live input graph for this pilot";
            else if (headerStick.Contains(mousePos)) headerTip = "Pin the Stick Cam overlay to this pilot";
            else if (headerRef.Contains(mousePos)) headerTip = "Reference pilot for delta times";
            else if (headerDText.Contains(mousePos)) headerTip = "Delta text floating above this drone";
            else if (headerTag.Contains(mousePos)) headerTip = "Name tag floating above this drone";
            else if (headerPip.Contains(mousePos)) headerTip = "Picture-in-picture window for this pilot";
            else if (headerLine.Contains(mousePos)) headerTip = "Ghost race line for this pilot";
            y += 20f;
            if (GhostLinesPlugin.entries.Count == 0)
            {
                GUI.color = ThemeDimLabel(alpha);
                GUI.Label(new Rect(x, y, w, 24f), "Waiting for pilots...", settingsSmallStyle);
                y += 28f;
            }
            else
            {
                for (int i = 0; i < GhostLinesPlugin.entries.Count; i++)
                {
                    GhostLineEntry entry = GhostLinesPlugin.entries[i];
                    Color entryColor = entry.color;
                    GUI.color = new Color(entryColor.r, entryColor.g, entryColor.b, alpha);
                    GUI.DrawTexture(new Rect(x, y + 2f, 18f, 18f), whiteTex);

                    string name = string.IsNullOrEmpty(entry.pilotName) ? "Pilot " + (i + 1) : entry.pilotName;
                    string delta = string.IsNullOrEmpty(entry.guiDeltaText) ? "" : entry.guiDeltaText;
                    GUI.color = ThemeLabel(alpha);
                    GUI.Label(new Rect(x + 28f, y, panel.width - 316f, 22f), FitSettingsName(name, panel.width - 316f), settingsNameStyle);
                    GUI.Label(new Rect(panel.xMax - 262f, y, 34f, 22f), delta, settingsSmallRightStyle);
                    bool isReference = entry == referenceEntry;
                    if (DrawSettingsTarget(new Rect(panel.xMax - 212f, y + 3f, 18f, 18f), isReference, alpha, 0, i))
                    {
                        if (!isReference)
                        {
                            referenceEntry = entry;
                            MarkPiPWindowsDirty();
                        }
                    }
                    HandleSettingsRadioDrag(i, 0, new Rect(panel.xMax - 212f, y + 3f, 18f, 18f));
                    // Replay pilots get the full-lap stick graph; the live graph is
                    // for multiplayer's live inputs.
                    bool graphOpen = IsStickGraphOpenFor(entry);
                    if (DrawSettingsTarget(new Rect(panel.xMax - 182f, y + 3f, 18f, 18f), graphOpen, alpha, 1, i))
                        ToggleStickGraphFor(entry);
                    HandleSettingsRadioDrag(i, 1, new Rect(panel.xMax - 182f, y + 3f, 18f, 18f));
                    bool stickPinned = false;
                    for (int sc = 0; sc < stickCamInstances.Count; sc++)
                    {
                        if (stickCamInstances[sc].entry == entry)
                        {
                            stickPinned = true;
                            break;
                        }
                    }
                    if (DrawSettingsTarget(new Rect(panel.xMax - 152f, y + 3f, 18f, 18f), stickPinned, alpha, 2, i))
                        ToggleStickCamFor(entry);
                    HandleSettingsRadioDrag(i, 2, new Rect(panel.xMax - 152f, y + 3f, 18f, 18f));
                    if (DrawSettingsTarget(new Rect(panel.xMax - 122f, y + 3f, 18f, 18f), entry.deltaDisplayVisible, alpha, 3, i))
                    {
                        entry.deltaDisplayVisible = !entry.deltaDisplayVisible;
                        MarkPiPWindowsDirty();
                    }
                    HandleSettingsRadioDrag(i, 3, new Rect(panel.xMax - 122f, y + 3f, 18f, 18f));
                    if (DrawSettingsTarget(new Rect(panel.xMax - 92f, y + 3f, 18f, 18f), entry.nameTagVisible, alpha, 4, i))
                    {
                        entry.nameTagVisible = !entry.nameTagVisible;
                        MarkPiPWindowsDirty();
                    }
                    HandleSettingsRadioDrag(i, 4, new Rect(panel.xMax - 92f, y + 3f, 18f, 18f));
                    bool pipOpen = false;
                    for (int j = 0; j < pipWindows.Count; j++)
                    {
                        if (pipWindows[j].entry == entry)
                        {
                            pipOpen = true;
                            break;
                        }
                    }
                    if (DrawSettingsTarget(new Rect(panel.xMax - 62f, y + 3f, 18f, 18f), pipOpen, alpha, 5, i))
                        TogglePiPWindow(entry);
                    HandleSettingsRadioDrag(i, 5, new Rect(panel.xMax - 62f, y + 3f, 18f, 18f));
                    if (DrawSettingsTarget(new Rect(panel.xMax - 32f, y + 3f, 18f, 18f), entry.visible, alpha, 6, i))
                        SetRaceLineVisible(entry, !entry.visible);
                    HandleSettingsRadioDrag(i, 6, new Rect(panel.xMax - 32f, y + 3f, 18f, 18f));
                    y += 28f;
                }
            }

            GUI.color = ThemeOptionBorder(alpha);
            GUI.DrawTexture(new Rect(panel.xMax - 218f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 188f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 158f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 128f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 98f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 68f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.DrawTexture(new Rect(panel.xMax - 38f, colTop + 18f, 1f, y - colTop - 22f), whiteTex);
            GUI.color = ThemeLabel(alpha);
            return y;
        }
    }
}
