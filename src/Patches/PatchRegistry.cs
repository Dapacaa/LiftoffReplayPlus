namespace LiftoffGhostLines
{
    // Process-wide record of one-time Harmony patches. Patches outlive any single
    // GhostLinePoller (EnsurePoller recreates it after a teardown), so the
    // "already patched" state must be static: an instance flag would let a
    // recreated poller patch the same method twice and double every hook.
    // Per-instance callback registrations (Photon targets, log handlers) are not
    // tracked here; the poller registers them itself and removes them in OnDestroy.
    internal static class PatchRegistry
    {
        internal static bool LoadFastAttempted;
        internal static bool PhotonProbeHooked;
        internal static bool RaceTriggerPatched;
        internal static bool RaceLapTimerPatched;
    }
}
