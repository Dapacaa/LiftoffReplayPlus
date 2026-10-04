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
        // Result cache: CalcSize is O(n^2) in the worst case here and this helper runs
        // per pilot/row per OnGUI event. Keys are (maxWidth, text); cleared on theme flip.
        private string FitSettingsName(string text, float maxWidth)
        {
            return FitTextCached(text, maxWidth, settingsNameStyle, "s");
        }

        // Style-aware truncation: the lobby rows draw at a larger font than the
        // settings panel, so measuring with settingsNameStyle made long titles and
        // info lines overflow and word-wrap into each other.
        private string FitTextCached(string text, float maxWidth, GUIStyle style, string tag)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            if (fitNameCacheDarkMode != darkMode)
            {
                fitNameCache.Clear();
                fitNameCacheDarkMode = darkMode;
            }
            string key = tag + "|" + ((int)maxWidth).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\u0001" + text;
            string cached;
            if (fitNameCache.TryGetValue(key, out cached))
                return cached;
            string result = FitTextUncached(text, maxWidth, style);
            if (fitNameCache.Count > 256)
                fitNameCache.Clear();
            fitNameCache[key] = result;
            return result;
        }

        private string FitTextUncached(string text, float maxWidth, GUIStyle style)
        {
            if (style != null && style.CalcSize(TempContent(text)).x <= maxWidth)
                return text;
            for (int i = text.Length - 1; i > 0; i--)
            {
                string cut = text.Substring(0, i) + "...";
                if (style == null || style.CalcSize(TempContent(cut)).x <= maxWidth)
                    return cut;
            }
            return "...";
        }

        // Stick channel letters (throttle, yaw, roll, pitch) as cached strings.
        internal static readonly string[] AxisLetters = { "T", "Y", "R", "P" };

        // Scratch buffers for RectTransform.GetWorldCorners in per-frame row hit tests.
        // Main thread only; read the result before the next call.
        private static readonly Vector3[] cornerScratchA = new Vector3[4];
        private static readonly Vector3[] cornerScratchB = new Vector3[4];

        // Bounded memo of number -> display text, keyed on the value rounded to the
        // display precision (the text is formatted from that rounded value, so two
        // inputs sharing a key always render the same).
        internal sealed class NumberTextCache
        {
            private readonly double scale;
            private readonly System.Func<double, string> format;
            private readonly Dictionary<long, string> texts = new Dictionary<long, string>();

            public NumberTextCache(double scale, System.Func<double, string> format)
            {
                this.scale = scale;
                this.format = format;
            }

            public string Get(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return format(value);
                long key = (long)System.Math.Round(value * scale);
                string text;
                if (texts.TryGetValue(key, out text))
                    return text;
                if (texts.Count >= 1024)
                    texts.Clear();
                text = format(key / scale);
                texts[key] = text;
                return text;
            }
        }

        // Shared scratch content for CalcSize/Label calls, so measuring a string
        // doesn't allocate a GUIContent per OnGUI event. Main thread only; don't hold it.
        private static readonly GUIContent tempContent = new GUIContent();
        internal static GUIContent TempContent(string text)
        {
            tempContent.text = text;
            tempContent.image = null;
            tempContent.tooltip = null;
            return tempContent;
        }

        // Per-style: the same text measures differently in each style.
        private readonly LabelWidthCache labelWidthCache = new LabelWidthCache();
        private bool labelWidthCacheDarkMode = false;

        internal sealed class LabelWidthCache
        {
            private readonly Dictionary<GUIStyle, Dictionary<string, float>> byStyle =
                new Dictionary<GUIStyle, Dictionary<string, float>>();

            public Dictionary<string, float> For(GUIStyle style)
            {
                Dictionary<string, float> widths;
                if (!byStyle.TryGetValue(style, out widths))
                {
                    widths = new Dictionary<string, float>();
                    byStyle[style] = widths;
                }
                return widths;
            }

            public void Clear() { byStyle.Clear(); }
        }

        // Cached CalcSize width for constant UI labels.
        private float CalcLabelWidth(GUIStyle style, string text)
        {
            if (labelWidthCacheDarkMode != darkMode)
            {
                labelWidthCache.Clear();
                labelWidthCacheDarkMode = darkMode;
            }
            Dictionary<string, float> widths = labelWidthCache.For(style);
            float w;
            if (!widths.TryGetValue(text, out w))
            {
                if (widths.Count > 256)
                    widths.Clear();
                w = style.CalcSize(TempContent(text)).x;
                widths[text] = w;
            }
            return w;
        }
    }
}
