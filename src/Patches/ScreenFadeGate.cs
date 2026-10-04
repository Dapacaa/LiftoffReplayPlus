using System;
using HarmonyLib;

namespace LiftoffGhostLines
{
    // In multiplayer the game snaps the screen to black (ScreenFader.FadeOutImmediate)
    // when the spectated pilot resets or the spectate target switches, then fades
    // back in. That hides the cut in first person, but in MP freecam, third person
    // or the spectate orbit the camera isn't on that drone, so it's just a flash.
    // Skip the snap in those views; the following FadeIn then finds the screen
    // already clear and does nothing.
    internal static class ScreenFadeGate
    {
        private static bool patched;
        internal static Func<bool> suppress;

        internal static void Apply(Harmony harmony)
        {
            if (patched || harmony == null)
                return;
            patched = true;
            try
            {
                var target = AccessTools.Method(typeof(ScreenFader), nameof(ScreenFader.FadeOutImmediate));
                if (target == null)
                {
                    ModLog.LogWarning("[GhostLines] screen fade gate: ScreenFader.FadeOutImmediate not found");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ScreenFadeGate), nameof(FadeOutImmediatePrefix)));
            }
            catch (Exception ex)
            {
                ModLog.LogWarning("[GhostLines] screen fade gate patch failed: " + ex.Message);
            }
        }

        private static bool FadeOutImmediatePrefix()
        {
            try { return suppress == null || !suppress(); }
            catch { return true; }
        }
    }
}
