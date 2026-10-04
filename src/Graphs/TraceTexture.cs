using UnityEngine;

namespace LiftoffGhostLines
{
    // Value in [-1, 1] and colour for the trace column at fraction f of the plot.
    internal delegate float TraceColumnSampler(float f, out Color32 color);

    // The input-trace texture shared by the stick graph, the live graph and their
    // PiP-docked copies: one column per plot pixel, each joined to the previous
    // column by a vertical band 3px taller than the step on either side.
    internal static class TraceTexture
    {
        // Samples column i at f = (i + 1) / width; seedF gives the value the first
        // column is joined from. The texture is reused while its size is unchanged.
        public static void Build(ref Texture2D tex, ref Color32[] pixels, int width, int height,
            float seedF, TraceColumnSampler sample)
        {
            if (tex == null || tex.width != width || tex.height != height)
            {
                if (tex != null)
                    Object.Destroy(tex);
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.hideFlags = HideFlags.HideAndDontSave;
            }
            int count = width * height;
            if (pixels == null || pixels.Length != count)
                pixels = new Color32[count];
            else
                System.Array.Clear(pixels, 0, count);

            Color32 unused;
            int prevRow = ValueToRow(sample(seedF, out unused), height);
            for (int x = 0; x < width; x++)
            {
                Color32 color;
                float value = sample((x + 1) / (float)width, out color);
                int row = ValueToRow(value, height);
                int yMin = Mathf.Max(0, Mathf.Min(prevRow, row) - 3);
                int yMax = Mathf.Min(height - 1, Mathf.Max(prevRow, row) + 3);
                for (int y = yMin; y <= yMax; y++)
                    pixels[(height - 1 - y) * width + x] = color;
                prevRow = row;
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
        }

        // Row from the top: +1 at row 0, -1 at the bottom.
        private static int ValueToRow(float value, int height)
        {
            return Mathf.Clamp((int)((1f - (value + 1f) * 0.5f) * height), 0, height - 1);
        }
    }

    // Stick-input channel maths shared by every graph and the stick cam.
    internal static class InputSampler
    {
        public const int CHANNEL_THROTTLE = 0;

        // Recorded input (x throttle 0..1, y yaw, z pitch, w roll) to a channel in
        // [-1, 1]: 0 throttle, 1 yaw, 2 roll, 3 pitch.
        public static float Channel(Vector4 input, int channel)
        {
            switch (channel)
            {
                case 0: return Mathf.Clamp(input.x * 2f - 1f, -1f, 1f);
                case 1: return Mathf.Clamp(input.y, -1f, 1f);
                case 2: return Mathf.Clamp(-input.w, -1f, 1f);
                default: return Mathf.Clamp(input.z, -1f, 1f);
            }
        }

        // Linear interpolation of a time-sorted input track, clamped at both ends.
        public static Vector4 SampleTimeline(float[] times, Vector4[] inputs, float time)
        {
            int count = Mathf.Min(times.Length, inputs.Length);
            if (count == 0)
                return Vector4.zero;
            if (time <= times[0])
                return inputs[0];
            if (time >= times[count - 1])
                return inputs[count - 1];
            int lo = 0;
            int hi = count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] <= time) lo = mid;
                else hi = mid;
            }
            float t0 = times[lo];
            float t1 = times[hi];
            float u = Mathf.Approximately(t1, t0) ? 0f : Mathf.InverseLerp(t0, t1, time);
            return Vector4.LerpUnclamped(inputs[lo], inputs[hi], u);
        }
    }
}
