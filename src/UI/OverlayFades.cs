using UnityEngine;

namespace LiftoffGhostLines
{
    // The one fade system for every overlay window. It ticks once per frame from
    // Update on unscaled time. The replay fades used to advance inside
    // DrawBottomScrubber, i.e. once per OnGUI event (2-4+ per frame), so their speed
    // and the idle threshold depended on mouse/keyboard activity, froze at
    // timeScale 0, and windows only faded while the playback bar was drawn.
    public partial class GhostLinePoller
    {
        // Mouse still this long hides unpinned, unhovered windows.
        private const float OVERLAY_IDLE_SECONDS = 0.8f;
        // Exponential approach rate (1/s): ~95% of the way in 0.25 s.
        private const float OVERLAY_FADE_RATE = 12f;
        private const float OVERLAY_CHROME_FADE_RATE = 8f;

        private static float StepOverlayFade(float current, float target, float rate)
        {
            float next = Mathf.Lerp(current, target, 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime));
            return Mathf.Abs(next - target) < 0.005f ? target : next;
        }

        private static float StepOverlayFade(float current, float target)
        {
            return StepOverlayFade(current, target, OVERLAY_FADE_RATE);
        }

        private void TickOverlayFades()
        {
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if ((mouse - overlayFadeLastMouse).sqrMagnitude > 1f)
            {
                overlayFadeLastMouse = mouse;
                overlayMouseMovedAt = Time.unscaledTime;
            }
            bool mouseActive = Time.unscaledTime - overlayMouseMovedAt < OVERLAY_IDLE_SECONDS;

            if (replayReady)
                TickReplayOverlayFades(mouse, mouseActive);
            else
            {
                // Outside a replay the bindings window is opened from the MP menu.
                bool hoveringBindings = bindingsOpen && GetBindingsWindowRect().Contains(mouse);
                bindingsFadeAmount = bindingsOpen
                    ? StepOverlayFade(bindingsFadeAmount, mouseActive || hoveringBindings || bindingsPinned ? 1f : 0f)
                    : 0f;
            }
            TickMultiplayerOverlayFades(mouse, mouseActive);
        }

        private void TickReplayOverlayFades(Vector2 mouse, bool mouseActive)
        {
            // Hit rects are the ones the last OnGUI pass laid out.
            bool hoveringSettings = settingsOpen && GetSettingsPanelRect().Contains(mouse);
            bool hoveringStickCam = currentStickCamOverlayRectValid && currentStickCamOverlayRect.Contains(mouse);
            bool hoveringAnyGraph = false;
            for (int i = 0; i < stickGraphs.Count && !hoveringAnyGraph; i++)
                hoveringAnyGraph = stickGraphs[i].rect.Contains(mouse);
            for (int i = 0; i < liveInputGraphs.Count && !hoveringAnyGraph; i++)
                hoveringAnyGraph = liveInputGraphs[i].rect.Contains(mouse);
            bool hoveringAnyPip = false;
            for (int i = 0; i < pipWindows.Count && !hoveringAnyPip; i++)
            {
                PiPWindow pw = pipWindows[i];
                hoveringAnyPip = pw.entry != null && pw.fadeAmount > 0.01f && pw.rect.Contains(mouse);
            }
            bool hoveringPlayback = currentPlaybackOverlayRectValid &&
                (currentPlaybackOverlayRect.Contains(mouse) || IsPlaybackTrimHit(mouse));
            bool hoveringOverlay = hoveringPlayback || hoveringSettings || hoveringStickCam ||
                hoveringAnyGraph || hoveringAnyPip;
            bool awake = mouseActive || hoveringOverlay;

            bool playbackVisible = awake || Time.unscaledTime < replayEnterTime + 3f || playbackPinned;
            fadeAmount = StepOverlayFade(fadeAmount, playbackVisible ? 1f : 0f);

            settingsFadeAmount = settingsOpen
                ? StepOverlayFade(settingsFadeAmount, awake || settingsPinned ? 1f : 0f)
                : 0f;

            bool hoveringBindings = bindingsOpen && GetBindingsWindowRect().Contains(mouse);
            bindingsFadeAmount = bindingsOpen
                ? StepOverlayFade(bindingsFadeAmount, awake || hoveringBindings || bindingsPinned ? 1f : 0f)
                : 0f;

            for (int i = 0; i < stickGraphs.Count; i++)
            {
                StickGraphState g = stickGraphs[i];
                g.fadeAmount = g.pinned ? 1f : StepOverlayFade(g.fadeAmount, awake ? 1f : 0f);
            }

            for (int i = 0; i < liveInputGraphs.Count; i++)
            {
                LiveGraphState lg = liveInputGraphs[i];
                bool keep = awake || lg.channelDropdown || lg.pilotDropdown;
                lg.fadeAmount = lg.pinned ? 1f : StepOverlayFade(lg.fadeAmount, keep ? 1f : 0f);
            }

            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow pw = pipWindows[i];
                if (pw.entry == null)
                    continue;
                pw.fadeAmount = pw.pinned ? 1f : StepOverlayFade(pw.fadeAmount, awake || pw.dropdownOpen ? 1f : 0f);
                bool chromeHover = pw.rect.Contains(mouse) || pw.dragging || pw.resizing || pw.dropdownOpen;
                pw.chromeAlpha = StepOverlayFade(pw.chromeAlpha, chromeHover ? 1f : 0f, OVERLAY_CHROME_FADE_RATE);
            }
        }

        private void TickMultiplayerOverlayFades(Vector2 mouse, bool mouseActive)
        {
            // Live input graphs don't idle-fade in MP: you watch live inputs with
            // the mouse still.
            if (!replayReady)
            {
                for (int i = 0; i < liveInputGraphs.Count; i++)
                    liveInputGraphs[i].fadeAmount = 1f;
            }

            bool menuHover = mpMenuScreenRect.width > 1f && mpMenuScreenRect.Contains(mouse);
            mpMenuIdleAlpha = StepOverlayFade(mpMenuIdleAlpha,
                (mouseActive || menuHover || mpMenuPinned) && multiplayerPipMenuOpen ? 1f : 0f);
            bool lobbyHover = lobbyBrowserScreenRect.width > 1f && lobbyBrowserScreenRect.Contains(mouse);
            lobbyBrowserIdleAlpha = StepOverlayFade(lobbyBrowserIdleAlpha,
                (mouseActive || lobbyHover || lobbyBrowserPinned) && lobbyBrowserOpen ? 1f : 0f);
            bool splitsVisible = Prefs.gateSplitsConfig != null && Prefs.gateSplitsConfig.Value;
            bool splitsHover = raceSplitsScreenRect.width > 1f && raceSplitsScreenRect.Contains(mouse);
            raceSplitsIdleAlpha = StepOverlayFade(raceSplitsIdleAlpha,
                (mouseActive || splitsHover || raceSplitsPinned) && splitsVisible ? 1f : 0f);
            bool histHover = raceHistoryScreenRect.width > 1f && raceHistoryScreenRect.Contains(mouse);
            raceHistoryIdleAlpha = StepOverlayFade(raceHistoryIdleAlpha,
                (mouseActive || histHover || raceHistoryPinned) && raceHistoryOpen ? 1f : 0f);

            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w == null)
                    continue;
                bool chromeHover = w.rect.Contains(mouse) || w.dragging || w.resizing || w.dropdownOpen;
                w.chromeAlpha = StepOverlayFade(w.chromeAlpha, chromeHover ? 1f : 0f, OVERLAY_CHROME_FADE_RATE);
            }
        }
    }
}
