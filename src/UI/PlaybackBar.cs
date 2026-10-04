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
        private void UpdateOverlayResolution()
        {
            int width = Screen.width;
            int height = Screen.height;
            if (overlayScreenWidth <= 0 || overlayScreenHeight <= 0)
            {
                if (stickCamPositionCustomized)
                {
                    stickCamX = savedStickCamPositionX * width;
                    stickCamY = savedStickCamPositionY * height;
                }
                if (scrubberPositionCustomized)
                {
                    scrubberX = savedPlaybackUiPositionX * width;
                    scrubberY = savedPlaybackUiPositionY * height;
                }
            }
            if (overlayScreenWidth > 0 && overlayScreenHeight > 0 &&
                (overlayScreenWidth != width || overlayScreenHeight != height))
            {
                float widthScale = width / (float)overlayScreenWidth;
                float heightScale = height / (float)overlayScreenHeight;
                if (scrubberPositionCustomized)
                {
                    if (scrubberX >= 0f) scrubberX *= widthScale;
                    if (scrubberY >= 0f) scrubberY *= heightScale;
                }
                else
                {
                    scrubberX = width * 0.5f;
                    scrubberY = -1f;
                }

                if (stickCamPositionCustomized)
                {
                    if (stickCamX >= 0f) stickCamX *= widthScale;
                    if (stickCamY >= 0f) stickCamY *= heightScale;
                }
                else
                {
                    stickCamX = width * 0.5f;
                    stickCamY = -1f;
                }

                if (settingsPanelPositionCustomized && settingsRect.x >= 0f && settingsRect.y >= 0f)
                {
                    settingsRect.x *= widthScale;
                    settingsRect.y *= heightScale;
                }
            }

            overlayScreenWidth = width;
            overlayScreenHeight = height;
        }

        // Two-slot cache keyed on whole milliseconds: the bar formats the current and
        // total time on every OnGUI event, but the text changes at most once a frame.
        private readonly int[] formatTimeKeys = { int.MinValue, int.MinValue };
        private readonly string[] formatTimeValues = new string[2];
        private int formatTimeNextSlot;

        private string FormatTime(float t)
        {
            int key = Mathf.RoundToInt(t * 1000f);
            for (int i = 0; i < 2; i++)
                if (formatTimeKeys[i] == key)
                    return formatTimeValues[i];
            int mins = (int)(t / 60);
            float secs = t % 60;
            string text = $"{mins}:{secs:F3}";
            formatTimeKeys[formatTimeNextSlot] = key;
            formatTimeValues[formatTimeNextSlot] = text;
            formatTimeNextSlot ^= 1;
            return text;
        }

        private void EnsurePlaybackTimeStyles(float scale)
        {
            if (playbackTimeLeftStyle == null)
            {
                playbackTimeLeftStyle = new GUIStyle(GUI.skin.label);
                playbackTimeRightStyle = new GUIStyle(GUI.skin.label);
                playbackSpeedStyle = new GUIStyle(GUI.skin.label);
                playbackTimeLeftStyle.alignment = TextAnchor.MiddleLeft;
                playbackTimeRightStyle.alignment = TextAnchor.MiddleRight;
                playbackSpeedStyle.alignment = TextAnchor.MiddleCenter;
                playbackTimeLeftStyle.wordWrap = false;
                playbackTimeRightStyle.wordWrap = false;
                playbackSpeedStyle.wordWrap = false;
                playbackTimeLeftStyle.padding = new RectOffset(0, 0, 0, 0);
                playbackTimeRightStyle.padding = new RectOffset(0, 0, 0, 0);
                playbackSpeedStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            int fontSize = Mathf.Max(10, Mathf.RoundToInt(12f * scale));
            playbackTimeLeftStyle.fontSize = fontSize;
            playbackTimeRightStyle.fontSize = fontSize;
            playbackSpeedStyle.fontSize = fontSize;
        }

        private Texture2D CreateTriangleTex(int w, int h, bool right)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color wc = Color.white;
            const int samples = 4;
            const float left = 0.16f;
            const float top = 0.14f;
            const float bottom = 0.86f;
            const float tip = 0.88f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        float ny = (y + (sy + 0.5f) / samples) / h;
                        if (ny < top || ny > bottom)
                            continue;

                        float vertical = Mathf.Abs(ny - 0.5f) / ((bottom - top) * 0.5f);
                        float edge = left + (tip - left) * (1f - vertical);
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float nx = (x + (sx + 0.5f) / samples) / w;
                            bool inside = right
                                ? nx <= 1f - left && nx >= 1f - edge
                                : nx >= left && nx <= edge;
                            if (inside)
                                covered++;
                        }
                    }

                    float alpha = covered / (float)(samples * samples);
                    tex.SetPixel(x, y, new Color(wc.r, wc.g, wc.b, alpha));
                }
            }
            tex.Apply();
            return tex;
        }

        private Texture2D CreatePauseTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h);
            Color wc = Color.white;
            Color t = Color.clear;
            int bw = Mathf.RoundToInt(w * 0.28f);
            int gap = w - 2 * bw - 2 * Mathf.RoundToInt(w * 0.1f);
            int m = Mathf.RoundToInt(w * 0.1f);
            int l1 = m;
            int r1 = l1 + bw - 1;
            int l2 = w - m - bw;
            int r2 = w - m - 1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, (x >= l1 && x <= r1) || (x >= l2 && x <= r2) ? wc : t);
            tex.Apply();
            return tex;
        }

        private Texture2D CreateDoubleTriangleTex(int w, int h, bool right)
        {
            int scale = 4;
            int W = w * scale, H = h * scale;
            Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color wc = Color.white;
            float gap = 2f * scale;
            float half = (W - gap) * 0.5f;
            float half1 = half - 1f;
            float h1 = H - 1f;
            System.Func<float, float, bool> inside = (fx, fy) =>
            {
                float dist = Mathf.Abs(fy / h1 - 0.5f) * 2f;
                float nx;
                if (fx < half)
                    nx = fx / half1;
                else if (fx >= half + gap)
                    nx = (fx - half - gap) / half1;
                else
                    return false;
                return right ? nx >= dist : nx <= 1f - dist;
            };
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                            if (inside(x + 0.25f + 0.5f * sx, y + 0.25f + 0.5f * sy))
                                hits++;
                    float a = hits * 0.25f;
                    tex.SetPixel(x, y, a <= 0f ? Color.clear : new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply(false);
            return tex;
        }

        private Texture2D CreateLoopTex(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x + 0.5f) / size;
                    float ny = (y + 0.5f) / size;
                    bool top = nx > 0.20f && nx < 0.72f && ny > 0.20f && ny < 0.32f;
                    bool right = nx > 0.66f && nx < 0.78f && ny > 0.26f && ny < 0.66f;
                    bool bottom = nx > 0.28f && nx < 0.80f && ny > 0.68f && ny < 0.80f;
                    bool left = nx > 0.22f && nx < 0.34f && ny > 0.36f && ny < 0.74f;
                    bool topArrow = nx >= 0.68f && nx <= 0.92f && Mathf.Abs(ny - 0.26f) <= (0.92f - nx) * 0.65f;
                    bool bottomArrow = nx >= 0.08f && nx <= 0.32f && Mathf.Abs(ny - 0.74f) <= (nx - 0.08f) * 0.65f;
                    tex.SetPixel(x, y, top || right || bottom || left || topArrow || bottomArrow ? Color.white : Color.clear);
                }
            }
            tex.Apply(false);
            return tex;
        }

        private Texture2D CreateTrimTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color32[] pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(0, 0, 0, 0);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool topThickPart = (y >= 7 && y <= 13) &&
                        ((x >= 0 && x <= 4) || (x >= 9 && x <= 13));
                    bool bottomThinPart = (y >= 1 && y <= 6) &&
                        ((x >= 3 && x <= 4) || (x >= 9 && x <= 10));
                    if (topThickPart || bottomThinPart)
                        pixels[y * w + x] = new Color32(255, 255, 255, 255);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        private Texture2D CreateGearTex(int w, int h)
        {
            const int SS = 3;
            int W = w * SS, H = h * SS;
            Color32[] px = new Color32[W * H];
            const float discR = 0.36f;
            const float toothR = 0.50f;
            const float holeR = 0.16f;
            const float toothHalfDeg = 13f;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < SS; sy++)
                    {
                        for (int sx = 0; sx < SS; sx++)
                        {
                            float nx = (x + (sx + 0.5f) / SS) / W - 0.5f;
                            float ny = (y + (sy + 0.5f) / SS) / H - 0.5f;
                            float r = Mathf.Sqrt(nx * nx + ny * ny);
                            if (r < holeR || r > toothR)
                                continue;
                            if (r <= discR)
                            {
                                hits++;
                                continue;
                            }
                            float ang = Mathf.Atan2(ny, nx) * Mathf.Rad2Deg;
                            float sector = Mathf.Repeat(ang / 45f + 0.5f, 1f) - 0.5f;
                            if (Mathf.Abs(sector) * 45f <= toothHalfDeg)
                                hits++;
                        }
                    }
                    px[y * W + x] = new Color32(255, 255, 255, (byte)(hits * 255 / (SS * SS)));
                }
            }
            Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

private Texture2D CreateKeyboardTex()
        {
            const int w = 20;
            const int h = 20;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color32[] pixels = new Color32[w * h];
            const int capW = 5;
            const int capH = 4;
            const int gapX = 1;
            const int gapY = 2;
            const int startY = 5;
            for (int row = 0; row < 2; row++)
            {
                int startX = row == 0 ? 1 : 2;
                int y0 = startY + row * (capH + gapY);
                for (int col = 0; col < 3; col++)
                {
                    int x0 = startX + col * (capW + gapX);
                    for (int y = y0; y < y0 + capH; y++)
                    {
                        for (int x = x0; x < x0 + capW; x++)
                        {
                            pixels[y * w + x] = new Color32(255, 255, 255, 255);
                        }
                    }
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        private Texture2D CreatePinTex(int w, int h)
        {
            Texture2D tex = new Texture2D(w, h);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (x + 0.5f) / w - 0.5f;
                    float ny = (y + 0.5f) / h - 0.5f;
                    float localX = (nx + ny) * 0.7071f;
                    float localY = (nx - ny) * 0.7071f;
                    bool head = Mathf.Abs(localX) < 0.28f && localY > -0.42f && localY < -0.1f;
                    bool cap = Mathf.Abs(localX) < 0.43f && localY >= -0.1f && localY < 0.06f;
                    bool stem = Mathf.Abs(localX) < 0.12f && localY >= 0.06f && localY < 0.34f;
                    bool tip = Mathf.Abs(localX) < 0.06f && localY >= 0.34f && localY < 0.48f;
                    tex.SetPixel(x, y, head || cap || stem || tip ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            return tex;
        }

        private void EnsureIcons()
        {
            if (iconsInitialized) return;
            iconsInitialized = true;
            iconPlay = CreateTriangleTex(64, 64, false);
            iconPause = CreatePauseTex(14, 14);
            iconRewind = CreateDoubleTriangleTex(22, 14, true);
            iconFF = CreateDoubleTriangleTex(22, 14, false);
            iconLoop = CreateLoopTex(64);
            iconTrim = CreateTrimTex(14, 14);
            iconSettings = CreateGearTex(64, 64);
            iconPin = CreatePinTex(14, 14);
            iconBindings = CreateKeyboardTex();
        }

        private bool DrawStyledButton(Rect rect, Texture2D icon, float alpha, int guardLayer = -1)
        {
            Event e = Event.current;
            bool hover = rect.Contains(e.mousePosition);
            bool blocked = guardLayer >= 0 && IsUiBlockedFor(guardLayer, e.mousePosition);
            Color old = GUI.color;

            float shadowOff = 2f;
            GUI.color = new Color(0f, 0f, 0f, 0.22f * alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y + shadowOff, rect.width, rect.height), whiteTex);

            GUI.color = hover
                ? new Color(1f, 1f, 1f, 0.92f * alpha)
                : ThemeButtonBg(alpha);
            GUI.DrawTexture(rect, whiteTex);

            float db = 1;
            GUI.color = ThemeButtonBorder(alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - db, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, db, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - db, rect.y, db, rect.height), whiteTex);

            if (icon != null)
            {
                float iconS = Mathf.Max(Mathf.Min(rect.width - 10, rect.height - 8, 18), 10f);
                if (icon == iconPlay)
                    iconS *= 1.15f;
                if (icon == iconBindings)
                    iconS = 20f;
                float drawX = Mathf.Round(rect.x + (rect.width - iconS) * 0.5f);
                float drawY = Mathf.Round(rect.y + (rect.height - iconS) * 0.5f);
                Rect ir = new Rect(drawX, drawY, iconS, iconS);
                GUI.color = ThemeButtonIcon(alpha);
                GUI.DrawTexture(ir, icon);
            }

            GUI.color = old;

            if (e.type == EventType.MouseUp && e.button == 0 && hover && !blocked)
            {
                e.Use();
                if (guardLayer >= 0)
                    uiFrontLayer = guardLayer;
                return true;
            }
            return false;
        }

        private bool DrawStyledToggleButton(Rect rect, Texture2D icon, bool selected, float alpha, bool allowHover = true, int guardLayer = -1)
        {
            Event e = Event.current;
            bool hover = allowHover && rect.Contains(e.mousePosition);
            bool blocked = guardLayer >= 0 && IsUiBlockedFor(guardLayer, e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover && !blocked)
                settingsControlPressed = true;

            Color old = GUI.color;
            float shadowOff = 2f;
            GUI.color = new Color(0f, 0f, 0f, 0.22f * alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y + shadowOff, rect.width, rect.height), whiteTex);

            GUI.color = selected
                ? new Color(0.25f, 0.62f, 0.95f, 0.95f * alpha)
                : hover ? new Color(1f, 1f, 1f, 0.92f * alpha) : ThemeButtonBg(alpha);
            GUI.DrawTexture(rect, whiteTex);

            float db = 1f;
            GUI.color = ThemeButtonBorder(alpha);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - db, rect.width, db), whiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, db, rect.height), whiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - db, rect.y, db, rect.height), whiteTex);

            if (icon != null)
            {
                float iconS = icon == iconLoop ? 18f : 14f;
                float drawX = Mathf.Round(rect.x + (rect.width - iconS) * 0.5f);
                float drawY = Mathf.Round(rect.y + (rect.height - iconS) * 0.5f);
                Rect iconRect = new Rect(drawX, drawY, iconS, iconS);
                GUI.color = selected ? new Color(1f, 1f, 1f, alpha) : ThemeButtonIcon(alpha);
                GUI.DrawTexture(iconRect, icon);
            }
            GUI.color = old;

            if (e.type == EventType.MouseUp && e.button == 0 && hover && !blocked)
            {
                e.Use();
                if (guardLayer >= 0)
                    uiFrontLayer = guardLayer;
                return true;
            }
            return false;
        }

        private void SetPlaybackSpeed(float speed)
        {
            if (Mathf.Approximately(speed, 1f))
            {
                if (overridePlayback)
                {
                    overridePlayback = false;
                    playbackSpeed = 1f;
                    SetNativeReplayPlaying(isPlaying);
                }
                return;
            }
            bool wasPlaying = isPlaying;
            if (!overridePlayback)
            {
                overridePlayback = true;
                SetNativeReplayPlaying(false);
            }
            playbackSpeed = speed;
            isPlaying = wasPlaying;
        }
        private void DrawBottomScrubber()
        {
            float s = playbackUiSize;
            ComputePlaybackBar(out _, out _, out float barW, out float barH, out Rect _, out float _, out float _, out float _);
            if (scrubberX < 0) scrubberX = Screen.width * 0.5f;
            if (scrubberY < 0) scrubberY = Screen.height - barH * 0.5f;
            scrubberX = Mathf.Clamp(scrubberX, barW * 0.5f, Screen.width - barW * 0.5f);
            scrubberY = Mathf.Clamp(scrubberY, barH * 0.5f + 2f, Screen.height - barH * 0.5f);
            ComputePlaybackBar(out float barX, out float barY, out barW, out barH, out Rect sliderRect, out float labelW, out float labelInset, out float row1Y);

            float val = 0, min = 0, max = 1;
            // LateUpdate snapshot — no per-event reflection here.
            val = scrubberTimeSnapshot;
            max = scrubberMaxSnapshot;
            if (replayController == null && sliderComponent != null)
            {
                min = sliderComponent.minValue;
            }

            max = GetEffectiveReplayEnd(max);
            val = Mathf.Clamp(val, min, max);

            Rect totalRect = new Rect(barX - 5f * s, barY - 5f * s, barW + 10f * s, barH + 10f * s);
            currentPlaybackOverlayRect = totalRect;
            currentPlaybackOverlayRectValid = true;
            Vector2 mp = Event.current.mousePosition;
            Rect trimStartRect = new Rect();
            Rect trimEndRect = new Rect();
            float tsN = 0f;
            float teN = 0f;
            if (trimEnabled)
            {
                float thandleW = 12f * s;
                float ttX = sliderRect.x + thandleW * 0.5f;
                float ttW = sliderRect.width - thandleW;
                float trange = Mathf.Max(max - min, 0.0001f);
                tsN = Mathf.Clamp01((trimStart - min) / trange);
                teN = Mathf.Clamp01((trimEnd - min) / trange);
                float tsX = ttX + tsN * ttW;
                float teX = ttX + teN * ttW;
                float poleW = 8f * s;
                float poleH = 20f * s;
                float poleTop = sliderRect.y + (sliderRect.height - poleH) * 0.5f;
                float flagW = 14f * s;
                float flagOverlap = 4f * s;
                float flagTop = barY - 13f * s;
                float flagH = (poleTop - flagTop) + flagOverlap;
                float hitPad = 2f * s;
                float sMinX = tsX - flagW - hitPad;
                float sMaxX = tsX + hitPad;
                float eMinX = teX - hitPad;
                float eMaxX = teX + flagW + hitPad;
                float hitY = flagTop - hitPad;
                float hitH = (poleTop + poleH) - flagTop + hitPad * 2f;
                trimStartRect = new Rect(sMinX, hitY, sMaxX - sMinX, hitH);
                trimEndRect = new Rect(eMinX, hitY, eMaxX - eMinX, hitH);
            }
            // Fades are stepped once per frame in TickOverlayFades.

            if (fadeAmount < 0.01f) return;

            EnsurePlaybackTimeStyles(s);
            float labelH = Mathf.Max(18f * s, 18f);
            float btnW = 34f * s;
            float btnH = 24f * s;
            float row2Y = barY + 28f * s;

            Rect btnPlayRect = new Rect(barX + 5f * s, row2Y, btnW, btnH);
            Rect btnRewindRect = new Rect(barX + 5f * s + btnW + 4f * s, row2Y, btnW, btnH);
            Rect btnFFRect = new Rect(barX + 5f * s + (btnW + 4f * s) * 2f, row2Y, btnW, btnH);
            float speedW = Mathf.Max(34f * s, 32f);
            Rect speedRect = new Rect(btnFFRect.xMax + 4f * s, row2Y, speedW, btnH);
            Rect btnLoopRect = new Rect(speedRect.xMax + 4f * s, row2Y, btnW, btnH);
            Rect btnTrimRect = new Rect(btnLoopRect.xMax + 4f * s, row2Y, btnW, btnH);
            float pinW = btnW;

            Color oldColor = GUI.color;

            GUI.color = ThemeBarBg(fadeAmount);
            GUI.DrawTexture(totalRect, whiteTex);
            float bd = 1;
            GUI.color = ThemeBarBorder(fadeAmount);
            GUI.DrawTexture(new Rect(totalRect.x, totalRect.y, totalRect.width, bd), whiteTex);
            GUI.DrawTexture(new Rect(totalRect.x, totalRect.yMax - bd, totalRect.width, bd), whiteTex);
            GUI.DrawTexture(new Rect(totalRect.x, totalRect.y, bd, totalRect.height), whiteTex);
            GUI.DrawTexture(new Rect(totalRect.xMax - bd, totalRect.y, bd, totalRect.height), whiteTex);

            DrawOverlayEdgeHighlights(totalRect, playbackResizing ? playbackResizeEdge : playbackEdgeHover, fadeAmount);

            EnsureIcons();
            Texture2D playTex = isPlaying ? iconPause : iconPlay;
            if (DrawStyledButton(btnPlayRect, playTex, fadeAmount, UI_LAYER_PLAYBAR))
            {
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] GUI: play pressed");
                RequestPlayButton();
            }
            if (DrawStyledButton(btnRewindRect, iconRewind, fadeAmount, UI_LAYER_PLAYBAR))
            {
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] GUI: rewind pressed");
                ClickGameButton("btnRewind");
            }
            if (DrawStyledButton(btnFFRect, iconFF, fadeAmount, UI_LAYER_PLAYBAR))
            {
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] GUI: ff pressed");
                ClickGameButton("btnFastForward");
            }
            if (DrawStyledToggleButton(btnLoopRect, iconLoop, loopReplayEnabled, fadeAmount, true, UI_LAYER_PLAYBAR))
                loopReplayEnabled = !loopReplayEnabled;
            if (DrawStyledToggleButton(btnTrimRect, iconTrim, trimEnabled, fadeAmount, true, UI_LAYER_PLAYBAR))
            {
                trimEnabled = !trimEnabled;
                if (trimEnabled && trimEnd <= 0f)
                {
                    float trimMax = GetNativeReplayMaximum(0f);
                    trimStart = 0f;
                    trimEnd = GetEffectiveReplayEnd(trimMax);
                    if (trimEnd <= 0f)
                        trimEnd = trimMax;
                }
            }

            GUI.color = ThemeLabel(fadeAmount);
            GUI.Label(speedRect, playbackSpeed.ToString("0.##") + "x", playbackSpeedStyle);

            Rect btnSettingsRect = new Rect(btnTrimRect.xMax + 4f * s, row2Y, btnW, btnH);
            if (DrawStyledButton(btnSettingsRect, iconSettings, fadeAmount, UI_LAYER_PLAYBAR))
            {
                settingsOpen = !settingsOpen;
                SaveSettingsPanelOpen(settingsOpen);
                if (settingsOpen)
                {
                    EnsureSettingsPanelRect();
                    settingsFadeAmount = 1f;
                }
            }
            Rect btnBindingsRect = new Rect(btnSettingsRect.xMax + 4f * s, row2Y, btnW, btnH);
            if (DrawStyledButton(btnBindingsRect, iconBindings, fadeAmount, UI_LAYER_PLAYBAR))
                ToggleBindingsWindow();
            Rect btnPinRect = new Rect(btnBindingsRect.xMax + 4f * s, row2Y, pinW, btnH);
            if (DrawStyledToggleButton(btnPinRect, iconPin, playbackPinned, fadeAmount, true, UI_LAYER_PLAYBAR))
                playbackPinned = !playbackPinned;

            bool onControl = sliderRect.Contains(mp) || btnPlayRect.Contains(mp)
                          || btnRewindRect.Contains(mp) || btnFFRect.Contains(mp) || btnLoopRect.Contains(mp)
                          || btnTrimRect.Contains(mp)
                          || trimStartRect.Contains(mp) || trimEndRect.Contains(mp)
                          || btnSettingsRect.Contains(mp) || btnBindingsRect.Contains(mp) || btnPinRect.Contains(mp);
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0 && totalRect.Contains(mp) && !onControl && !IsUiBlockedFor(UI_LAYER_PLAYBAR, mp))
            {
                uiFrontLayer = UI_LAYER_PLAYBAR;
                if (!playbackResizing && playbackEdgeHover == 0)
                {
                    draggingScrubber = true;
                    playbackMouseCaptured = true;
                    isOrbiting = false;
                    CancelCameraLook();
                    scrubberPositionCustomized = true;
                    dragStartMouse = mp;
                    dragStartBarPos = new Vector2(scrubberX, scrubberY);
                }
                ev.Use();
            }
            if (draggingScrubber)
            {
                if (ev.type == EventType.MouseUp && ev.button == 0)
                {
                    draggingScrubber = false;
                    playbackMouseCaptured = false;
                    SaveCurrentOverlayPositions();
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && ev.button == 0)
                {
                    Vector2 offset = mp - dragStartMouse;
                    float draggedX = SnapOverlayToHorizontalCenter(dragStartBarPos.x + offset.x);
                    scrubberX = Mathf.Clamp(draggedX, barW * 0.5f, Screen.width - barW * 0.5f);
                    scrubberY = Mathf.Clamp(dragStartBarPos.y + offset.y, barH * 0.5f + 2f, Screen.height - barH * 0.5f);
                    ev.Use();
                }
            }
            else if (playbackResizing && ev.type == EventType.MouseUp && ev.button == 0)
            {
                playbackResizing = false;
                playbackResizeEdge = 0;
                SaveCurrentOverlayPositions();
                SaveDisplayPreferences();
                ev.Use();
            }

            float currentTime = Mathf.Max(0f, val);
            float totalTime = Mathf.Max(0f, max);

            GUI.color = ThemeLabel(fadeAmount);
            GUI.Label(new Rect(barX + labelInset, row1Y, labelW, labelH), FormatTime(currentTime), playbackTimeLeftStyle);
            GUI.color = oldColor;

            float trackH = 10f * s;
            float handleW = 12f * s;
            float handleH = 20f * s;
            float pad = handleW / 2f;
            float tX = sliderRect.x + pad;
            float tW = sliderRect.width - handleW;
            float tY = sliderRect.y + (sliderRect.height - trackH) / 2f;
            float range = Mathf.Max(max - min, 0.0001f);
            float normV = Mathf.Clamp01((val - min) / range);

            GUI.color = ThemeTrack(fadeAmount);
            GUI.DrawTexture(new Rect(tX, tY, tW, trackH), whiteTex);

            float fillStartN = 0f;
            float fillEndN = normV;
            if (trimEnabled)
            {
                GUI.color = new Color(1f, 0.6f, 0f, 0.12f * fadeAmount);
                GUI.DrawTexture(new Rect(tX + tsN * tW, tY, (teN - tsN) * tW, trackH), whiteTex);
                fillStartN = tsN;
                fillEndN = Mathf.Clamp(normV, tsN, teN);
            }

            if (fillEndN - fillStartN > 0.001f)
            {
                GUI.color = new Color(0.25f, 0.62f, 0.95f, fadeAmount);
                GUI.DrawTexture(new Rect(tX + fillStartN * tW, tY, (fillEndN - fillStartN) * tW, trackH), whiteTex);
            }

            float handleX = tX + normV * tW;
            if (trimEnabled)
            {
                float tsX = tX + tsN * tW;
                float teX = tX + teN * tW;
                handleX = Mathf.Clamp(handleX, tsX + handleW * 0.5f, teX - handleW * 0.5f);
            }
            Rect handleRect = new Rect(handleX - handleW / 2f, sliderRect.y + (sliderRect.height - handleH) / 2f, handleW, handleH);
            Event ce = Event.current;
            bool hHit = new Rect(handleRect.x - 4, handleRect.y - 4, handleRect.width + 8, handleRect.height + 8).Contains(ce.mousePosition);
            bool tHit = new Rect(tX, sliderRect.y, tW, sliderRect.height).Contains(ce.mousePosition);
            GUI.color = hHit ? new Color(1f, 1f, 1f, fadeAmount) : ThemeHandle(fadeAmount);
            GUI.DrawTexture(handleRect, whiteTex);

            float hb = 1;
            GUI.color = new Color(0.35f, 0.35f, 0.35f, 0.55f * fadeAmount);
            GUI.DrawTexture(new Rect(handleRect.x, handleRect.y, handleRect.width, hb), whiteTex);
            GUI.DrawTexture(new Rect(handleRect.x, handleRect.yMax - hb, handleRect.width, hb), whiteTex);
            GUI.DrawTexture(new Rect(handleRect.x, handleRect.y, hb, handleRect.height), whiteTex);
            GUI.DrawTexture(new Rect(handleRect.xMax - hb, handleRect.y, hb, handleRect.height), whiteTex);

            if (ce.type == EventType.MouseDown && ce.button == 0 && (hHit || tHit) && !isDraggingHandle
                && !trimStartRect.Contains(ce.mousePosition) && !trimEndRect.Contains(ce.mousePosition)
                && !IsUiBlockedFor(UI_LAYER_PLAYBAR, ce.mousePosition))
            {
                uiFrontLayer = UI_LAYER_PLAYBAR;
                float nn = Mathf.Clamp01((ce.mousePosition.x - tX) / tW);
                bool allowSeek = true;
                if (trimEnabled)
                {
                    if (nn < tsN || nn > teN)
                        allowSeek = false;
                }
                if (!allowSeek)
                {
                    ce.Use();
                    goto skipHandleDrag;
                }
                isDraggingHandle = true;
                playbackMouseCaptured = true;
                isOrbiting = false;
                CancelCameraLook();
                resumePlaybackAfterSeek = overridePlayback ? isPlaying : IsNativeReplayPlaying();
                float nv = min + nn * range;
                SeekReplayTimeKeepingPlayback(nv);
                ce.Use();
            }
        skipHandleDrag:
            if (isDraggingHandle)
            {
                if (ce.type == EventType.MouseUp && ce.button == 0)
                {
                    isDraggingHandle = false;
                    playbackMouseCaptured = false;
                    if (resumePlaybackAfterSeek && !overridePlayback)
                        SetNativeReplayPlaying(true);
                    resumePlaybackAfterSeek = false;
                    ce.Use();
                }
                else if (ce.type == EventType.MouseDrag && ce.button == 0)
                {
                    float nn = Mathf.Clamp01((ce.mousePosition.x - tX) / tW);
                    bool allowSeek = true;
                    if (trimEnabled)
                    {
                        if (nn < tsN || nn > teN)
                            allowSeek = false;
                    }
                    if (allowSeek)
                    {
                        float nv = min + nn * range;
                        SeekReplayTimeKeepingPlayback(nv);
                    }
                    ce.Use();
                }
            }

            if (trimEnabled)
            {
                Color trimColor = new Color(1f, 0.6f, 0f, 0.95f * fadeAmount);
                Color trimHover = new Color(1f, 0.75f, 0.25f, fadeAmount);
                bool startHit = trimStartRect.Contains(ce.mousePosition);
                bool endHit = trimEndRect.Contains(ce.mousePosition);

                float tsX = tX + tsN * tW;
                float teX = tX + teN * tW;
                float poleW = 8f * s;
                float poleH = 20f * s;
                float poleY = sliderRect.y + (sliderRect.height - poleH) * 0.5f;
                float flagW = 14f * s;
                float flagOverlap = 4f * s;
                float flagTop = barY - 13f * s;
                float flagH = (poleY - flagTop) + flagOverlap;
                Rect startPole = new Rect(tsX - poleW, poleY, poleW, poleH);
                Rect startFlag = new Rect(tsX - flagW, flagTop, flagW, flagH);
                Rect endPole = new Rect(teX, poleY, poleW, poleH);
                Rect endFlag = new Rect(teX, flagTop, flagW, flagH);

                GUI.color = (startHit || draggingTrimStart) ? trimHover : trimColor;
                GUI.DrawTexture(startPole, whiteTex);
                GUI.color = (endHit || draggingTrimEnd) ? trimHover : trimColor;
                GUI.DrawTexture(endPole, whiteTex);

                GUI.color = new Color(0.2f, 0.12f, 0f, 0.9f * fadeAmount);
                float tb = 1f;
                GUI.DrawTexture(new Rect(startPole.x, startPole.yMax - tb, startPole.width, tb), whiteTex);
                GUI.DrawTexture(new Rect(startPole.x, startPole.y, tb, startPole.height), whiteTex);
                GUI.DrawTexture(new Rect(startPole.xMax - tb, startPole.y, tb, startPole.height), whiteTex);
                GUI.DrawTexture(new Rect(endPole.x, endPole.yMax - tb, endPole.width, tb), whiteTex);
                GUI.DrawTexture(new Rect(endPole.x, endPole.y, tb, endPole.height), whiteTex);
                GUI.DrawTexture(new Rect(endPole.xMax - tb, endPole.y, tb, endPole.height), whiteTex);

                GUI.color = (startHit || draggingTrimStart) ? trimHover : trimColor;
                GUI.DrawTexture(startFlag, whiteTex);
                GUI.color = (endHit || draggingTrimEnd) ? trimHover : trimColor;
                GUI.DrawTexture(endFlag, whiteTex);

                GUI.color = new Color(0.2f, 0.12f, 0f, 0.9f * fadeAmount);
                GUI.DrawTexture(new Rect(startFlag.x, startFlag.y, startFlag.width, tb), whiteTex);
                GUI.DrawTexture(new Rect(startFlag.x, startFlag.y, tb, startFlag.height), whiteTex);
                GUI.DrawTexture(new Rect(startFlag.xMax - tb, startFlag.y, tb, startFlag.height), whiteTex);
                GUI.DrawTexture(new Rect(endFlag.x, endFlag.y, endFlag.width, tb), whiteTex);
                GUI.DrawTexture(new Rect(endFlag.x, endFlag.y, tb, endFlag.height), whiteTex);
                GUI.DrawTexture(new Rect(endFlag.xMax - tb, endFlag.y, tb, endFlag.height), whiteTex);

                if (ce.type == EventType.MouseDown && ce.button == 0 && (startHit || endHit)
                    && !isDraggingHandle && !draggingScrubber && !IsUiBlockedFor(UI_LAYER_PLAYBAR, ce.mousePosition))
                {
                    uiFrontLayer = UI_LAYER_PLAYBAR;
                    draggingTrimStart = startHit;
                    draggingTrimEnd = endHit;
                    playbackMouseCaptured = true;
                    trimResumePlayback = overridePlayback ? isPlaying : IsNativeReplayPlaying();
                    SetNativeReplayPlaying(false);
                    isPlaying = false;
                    float nv = min + Mathf.Clamp01((ce.mousePosition.x - tX) / tW) * range;
                    const float minGap = 0.1f;
                    if (draggingTrimStart)
                        trimStart = Mathf.Clamp(nv, min, trimEnd - minGap);
                    else
                        trimEnd = Mathf.Clamp(nv, trimStart + minGap, max);
                    resumePlaybackAfterSeek = false;
                    SeekReplayTimeKeepingPlayback(draggingTrimStart ? trimStart : trimEnd);
                    ce.Use();
                }
                else if (draggingTrimStart || draggingTrimEnd)
                {
                    if (ce.type == EventType.MouseUp && ce.button == 0)
                    {
                        draggingTrimStart = false;
                        draggingTrimEnd = false;
                        playbackMouseCaptured = false;
                        if (trimResumePlayback)
                        {
                            if (!overridePlayback)
                                SetNativeReplayPlaying(true);
                            isPlaying = true;
                        }
                        trimResumePlayback = false;
                        ce.Use();
                    }
                    else if (ce.type == EventType.MouseDrag && ce.button == 0)
                    {
                        float nn = Mathf.Clamp01((ce.mousePosition.x - tX) / tW);
                        float nv = min + nn * range;
                        const float minGap = 0.1f;
                        if (draggingTrimStart)
                            trimStart = Mathf.Clamp(nv, min, trimEnd - minGap);
                        else
                            trimEnd = Mathf.Clamp(nv, trimStart + minGap, max);
                        resumePlaybackAfterSeek = false;
                        SeekReplayTimeKeepingPlayback(draggingTrimStart ? trimStart : trimEnd);
                        ce.Use();
                    }
                }
            }

            GUI.color = ThemeLabel(fadeAmount);
            GUI.Label(new Rect(barX + barW - labelInset - labelW, row1Y, labelW, labelH), FormatTime(totalTime), playbackTimeRightStyle);

            GUI.color = oldColor;
        }
    }
}
