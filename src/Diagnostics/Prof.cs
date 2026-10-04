using System;
using System.Text;
using UnityEngine;

namespace LiftoffGhostLines
{
    // Per-section time + managed allocation, summed per second and written to the
    // debug log as one "prof 1s" line. Debug logging only; otherwise Begin()
    // returns an empty span and End() does nothing.
    //
    // Allocation would come from GC.GetAllocatedBytesForCurrentThread (not in the
    // net472 reference assemblies, hence the delegate). Liftoff's Mono has the
    // method but it does not count, so the probe fails and only times are logged.
    internal static class Prof
    {
        public const int Update = 0, LateUpdate = 1, OnGui = 2,
            GuiMpMenu = 3, GuiMpWindows = 4, GuiStickGraph = 5, GuiSettings = 6, GuiBindings = 7,
            GuiPip = 8, GuiStickCam = 9, GuiScrubber = 10, GuiLiveGraph = 11, GuiNames = 12,
            GuiChat = 13, GuiLabelClick = 14, GuiLobby = 15, GuiSplits = 16, GuiMisc = 17,
            UpdLobby = 18, UpdLbUi = 19, UpdRaceProbe = 20, UpdMpPip = 21, UpdChatTrack = 22,
            UpdDrones = 23, UpdScan = 24, UpdEnforce = 25, UpdReplay = 26, PreCull = 27, PostRender = 28,
            ConfigSave = 29, UpdLbTicks = 30, UpdWorkshop = 31, UpdUiDedup = 32, UpdReplayLife = 33,
            UpdWinMouse = 34, UpdDiag = 35,
            Count = 36;

        private static readonly string[] Names =
        {
            "upd", "late", "gui",
            "gMpMenu", "gMpWin", "gStickGraph", "gSettings", "gBindings",
            "gPip", "gStickCam", "gScrubber", "gLiveGraph", "gNames",
            "gChat", "gLabelClick", "gLobby", "gSplits", "gMisc",
            "uLobby", "uLbUi", "uRaceProbe", "uMpPip", "uChatTrack",
            "uDrones", "uScan", "uEnforce", "uReplay", "preCull", "postRender",
            "cfgSave", "uLbTicks", "uWorkshop", "uUiDedup", "uReplayLife",
            "uWinMouse", "uDiag",
        };

        public struct Span
        {
            public long T;
            public long A;
        }

        private static readonly double[] ms = new double[Count];
        private static readonly long[] bytes = new long[Count];
        private static readonly int[] calls = new int[Count];
        // Same sums for the current frame only, for the hitch breakdown.
        private static readonly double[] frameMs = new double[Count];
        private static readonly long[] frameBytes = new long[Count];
        private static long frameStartAlloc = -1;
        private static int frameStartGc = -1;
        private static Func<long> allocFn;
        private static bool allocProbed;
        private static long frameAllocStart = -1;
        private static int gc0Start = -1;
        private static readonly StringBuilder sb = new StringBuilder(512);

        public static bool Enabled { get { return ModLog.DebugEnabled; } }

        private static long Alloc()
        {
            if (!allocProbed)
            {
                allocProbed = true;
                try
                {
                    var mi = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (mi != null)
                    {
                        var fn = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), mi);
                        long before = fn();
                        byte[] probe = new byte[4096];
                        long after = fn();
                        if (probe.Length > 0 && after - before >= 4096)
                            allocFn = fn;
                    }
                }
                catch { allocFn = null; }
                ModLog.LogFile("[GhostLines] prof: allocation counter " + (allocFn != null ? "per-thread" : "unavailable (time only)"));
            }
            return allocFn != null ? allocFn() : 0L;
        }

        public static Span Begin()
        {
            if (!Enabled)
                return default(Span);
            Span s;
            s.A = Alloc();
            s.T = System.Diagnostics.Stopwatch.GetTimestamp();
            return s;
        }

        public static void End(int slot, Span s)
        {
            if (s.T == 0)
                return;
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = (t - s.T) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long da = Alloc() - s.A;
            ms[slot] += dt;
            bytes[slot] += da;
            calls[slot]++;
            frameMs[slot] += dt;
            frameBytes[slot] += da;
        }

        // Called at the top of Update. When the frame that just ended ran long,
        // logs what each mod section cost in that frame, so a hitch can be pinned
        // on the mod (a large section) or ruled out (everything small: the game,
        // rendering, or a Harmony patch inside game code).
        public static void FrameBoundary(float lastFrameSeconds)
        {
            if (!Enabled)
                return;
            long alloc = Alloc();
            int gc = GC.CollectionCount(0);
            if (frameStartGc >= 0 && lastFrameSeconds > 0.025f)
            {
                // Only the top-level sections; the rest are nested inside them.
                double modMs = frameMs[Update] + frameMs[LateUpdate] + frameMs[OnGui]
                    + frameMs[PreCull] + frameMs[PostRender];
                sb.Length = 0;
                sb.Append("[GhostLines] hitch ").Append((lastFrameSeconds * 1000f).ToString("F0"))
                    .Append("ms frame=").Append(Time.frameCount - 1)
                    .Append(" gc=").Append(gc - frameStartGc)
                    .Append(" modMs=").Append(modMs.ToString("F1"));
                if (allocFn != null)
                    sb.Append(" mainKB=").Append(((alloc - frameStartAlloc) / 1024.0).ToString("F0"));
                for (int i = 0; i < Count; i++)
                {
                    if (frameMs[i] < 0.2 && frameBytes[i] < 16 * 1024)
                        continue;
                    sb.Append(' ').Append(Names[i]).Append('=').Append(frameMs[i].ToString("F1")).Append("ms");
                    if (allocFn != null)
                        sb.Append('/').Append((frameBytes[i] / 1024.0).ToString("F0")).Append("KB");
                }
                ModLog.LogFile(sb.ToString());
            }
            frameStartAlloc = alloc;
            frameStartGc = gc;
            Array.Clear(frameMs, 0, Count);
            Array.Clear(frameBytes, 0, Count);
        }

        // Called once a second from PerfWindowTick. "mainKB" is everything the
        // main thread allocated in that second (game + mod), for comparison
        // with the mod's own sections.
        public static void Flush(float maxFrameMs, float seconds)
        {
            if (!Enabled)
                return;
            long nowAlloc = Alloc();
            int gc0 = GC.CollectionCount(0);
            if (gc0Start >= 0)
            {
                sb.Length = 0;
                sb.Append("[GhostLines] prof 1s: maxFrame=").Append(maxFrameMs.ToString("F1"))
                    .Append("ms gc=").Append(gc0 - gc0Start)
                    .Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F0"));
                if (allocFn != null)
                    sb.Append(" mainKB=").Append(((nowAlloc - frameAllocStart) / 1024.0 / Math.Max(0.01f, seconds)).ToString("F0"));
                for (int i = 0; i < Count; i++)
                {
                    if (calls[i] == 0)
                        continue;
                    // Skip sections that did nothing measurable.
                    if (ms[i] < 0.05 && bytes[i] < 1024)
                        continue;
                    sb.Append(' ').Append(Names[i]).Append('=').Append(ms[i].ToString("F1")).Append("ms");
                    if (allocFn != null)
                        sb.Append('/').Append((bytes[i] / 1024.0).ToString("F0")).Append("KB");
                }
                ModLog.LogFile(sb.ToString());
            }
            frameAllocStart = nowAlloc;
            gc0Start = gc0;
            Array.Clear(ms, 0, Count);
            Array.Clear(bytes, 0, Count);
            Array.Clear(calls, 0, Count);
        }
    }
}
