using System.Collections.Generic;
using UnityEngine;

namespace LiftoffGhostLines
{
    // Records anti-aliased polylines and filled boxes, then rasterizes them into
    // one texture drawn with a single GUI.DrawTexture. The race-history graph used
    // to draw every line as one 2x2 IMGUI quad per pixel (thousands of draw calls
    // per repaint). Recording is cheap; the texture is only rebuilt when the
    // recorded content differs from the last build.
    internal sealed class LineRaster
    {
        private const float CMD_POLYLINE = 1f;
        private const float CMD_BOX = 2f;

        public Texture2D Texture { get; private set; }

        private List<float> commands = new List<float>();
        private List<float> builtCommands = new List<float>();
        private Color[] pixels;
        private float[] coverage;
        private int width;
        private int height;

        public void Begin(int width, int height)
        {
            this.width = Mathf.Max(1, width);
            this.height = Mathf.Max(1, height);
            commands.Clear();
            commands.Add(this.width);
            commands.Add(this.height);
        }

        // Points are in GUI space (y down) relative to the texture's top-left.
        public void Polyline(List<Vector2> points, Color color, float lineWidth)
        {
            if (points.Count < 2)
                return;
            commands.Add(CMD_POLYLINE);
            AddColor(color);
            commands.Add(lineWidth);
            commands.Add(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                commands.Add(points[i].x);
                commands.Add(points[i].y);
            }
        }

        public void Box(Rect rect, Color color)
        {
            commands.Add(CMD_BOX);
            AddColor(color);
            commands.Add(rect.x);
            commands.Add(rect.y);
            commands.Add(rect.width);
            commands.Add(rect.height);
        }

        // Rasterizes if anything changed since the last build.
        public void End()
        {
            if (Texture != null && SameAsBuilt())
                return;

            int count = width * height;
            if (pixels == null || pixels.Length != count)
                pixels = new Color[count];
            else
                System.Array.Clear(pixels, 0, count);

            int i = 2;
            while (i < commands.Count)
            {
                float type = commands[i++];
                Color color = new Color(commands[i], commands[i + 1], commands[i + 2], commands[i + 3]);
                i += 4;
                if (type == CMD_POLYLINE)
                {
                    float lineWidth = commands[i++];
                    int n = (int)commands[i++];
                    RasterPolyline(i, n, color, lineWidth);
                    i += n * 2;
                }
                else
                {
                    RasterBox(new Rect(commands[i], commands[i + 1], commands[i + 2], commands[i + 3]), color);
                    i += 4;
                }
            }

            if (Texture == null || Texture.width != width || Texture.height != height)
            {
                Release();
                Texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Texture.filterMode = FilterMode.Point;
                Texture.wrapMode = TextureWrapMode.Clamp;
                Texture.hideFlags = HideFlags.HideAndDontSave;
            }
            Texture.SetPixels(pixels);
            Texture.Apply(false);

            List<float> swap = builtCommands;
            builtCommands = commands;
            commands = swap;
        }

        public void Release()
        {
            if (Texture != null)
                Object.Destroy(Texture);
            Texture = null;
            builtCommands.Clear();
        }

        private void AddColor(Color c)
        {
            commands.Add(c.r);
            commands.Add(c.g);
            commands.Add(c.b);
            commands.Add(c.a);
        }

        private bool SameAsBuilt()
        {
            if (commands.Count != builtCommands.Count)
                return false;
            for (int i = 0; i < commands.Count; i++)
            {
                if (commands[i] != builtCommands[i])
                    return false;
            }
            return true;
        }

        // Coverage is the max over the polyline's segments, then blended once, so
        // the joints between segments don't double up into darker beads.
        private void RasterPolyline(int start, int n, Color color, float lineWidth)
        {
            float half = lineWidth * 0.5f;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int p = 0; p < n; p++)
            {
                float x = commands[start + p * 2];
                float y = commands[start + p * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            int bx0 = Mathf.Max(0, Mathf.FloorToInt(minX - half - 1f));
            int by0 = Mathf.Max(0, Mathf.FloorToInt(minY - half - 1f));
            int bx1 = Mathf.Min(width - 1, Mathf.CeilToInt(maxX + half + 1f));
            int by1 = Mathf.Min(height - 1, Mathf.CeilToInt(maxY + half + 1f));
            if (bx0 > bx1 || by0 > by1)
                return;
            int bw = bx1 - bx0 + 1;
            int bh = by1 - by0 + 1;
            if (coverage == null || coverage.Length < bw * bh)
                coverage = new float[bw * bh];
            else
                System.Array.Clear(coverage, 0, bw * bh);

            for (int p = 0; p < n - 1; p++)
            {
                Vector2 a = new Vector2(commands[start + p * 2], commands[start + p * 2 + 1]);
                Vector2 b = new Vector2(commands[start + p * 2 + 2], commands[start + p * 2 + 3]);
                int sx0 = Mathf.Max(bx0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - half - 1f));
                int sx1 = Mathf.Min(bx1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + half + 1f));
                int sy0 = Mathf.Max(by0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - half - 1f));
                int sy1 = Mathf.Min(by1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + half + 1f));
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                for (int y = sy0; y <= sy1; y++)
                {
                    for (int x = sx0; x <= sx1; x++)
                    {
                        Vector2 c = new Vector2(x + 0.5f, y + 0.5f);
                        float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(c - a, ab) / len2) : 0f;
                        float d = Vector2.Distance(c, a + ab * t);
                        float cov = Mathf.Clamp01(half + 0.5f - d);
                        int ci = (y - by0) * bw + (x - bx0);
                        if (cov > coverage[ci])
                            coverage[ci] = cov;
                    }
                }
            }

            for (int y = by0; y <= by1; y++)
            {
                for (int x = bx0; x <= bx1; x++)
                {
                    float cov = coverage[(y - by0) * bw + (x - bx0)];
                    if (cov > 0f)
                        Blend(x, y, color, cov);
                }
            }
        }

        private void RasterBox(Rect r, Color color)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(r.xMin));
            int x1 = Mathf.Min(width - 1, Mathf.CeilToInt(r.xMax) - 1);
            int y0 = Mathf.Max(0, Mathf.FloorToInt(r.yMin));
            int y1 = Mathf.Min(height - 1, Mathf.CeilToInt(r.yMax) - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    // Partial coverage on fractional edges keeps boxes at sub-pixel positions.
                    float cx = Mathf.Clamp01(Mathf.Min(x + 1f, r.xMax) - Mathf.Max(x, r.xMin));
                    float cy = Mathf.Clamp01(Mathf.Min(y + 1f, r.yMax) - Mathf.Max(y, r.yMin));
                    Blend(x, y, color, cx * cy);
                }
            }
        }

        // Straight-alpha "over" into the buffer; GUI y-down to texture y-up.
        private void Blend(int x, int guiY, Color src, float coverage)
        {
            int idx = (height - 1 - guiY) * width + x;
            Color dst = pixels[idx];
            float sa = src.a * coverage;
            float outA = sa + dst.a * (1f - sa);
            if (outA <= 0f)
                return;
            float dw = dst.a * (1f - sa);
            pixels[idx] = new Color(
                (src.r * sa + dst.r * dw) / outA,
                (src.g * sa + dst.g * dw) / outA,
                (src.b * sa + dst.b * dw) / outA,
                outA);
        }
    }
}
