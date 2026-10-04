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
        private void TickBindingsKeyCapture()
        {
            // Kept only for the click that cancelled listening (see below).
            if (bindingClickCancelledId != null && !Input.GetMouseButton(0) && !Input.GetMouseButtonUp(0))
                bindingClickCancelledId = null;
            if (bindingListeningFor == null)
                return;
            if (bindingListeningJustStarted)
            {
                bindingListeningJustStarted = false;
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                bindingListeningFor = null;
                bindingCaptureSuppressFrame = Time.frameCount;
                return;
            }

            // A click cancels instead of binding the mouse button: clicking the
            // key button again (or right-dragging to look) used to bind Mouse0 or
            // Mouse1 to the action and break the camera. The key button's release
            // lands after this, so remember which one it was (ToggleBindingListen).
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
            {
                bindingClickCancelledId = bindingListeningFor;
                bindingListeningFor = null;
                bindingCaptureSuppressFrame = Time.frameCount;
                return;
            }

            KeyCode pressed = DetectPressedKey();
            if (pressed == KeyCode.None)
                return;

            if (GhostLinesPlugin.IsDroneBindingId(bindingListeningFor))
                GhostLinesPlugin.SetDroneBinding(GhostLinesPlugin.DroneBindingLocalId(bindingListeningFor), pressed);
            else
                GhostLinesPlugin.SetBinding(bindingListeningFor, pressed);
            bindingListeningFor = null;
            bindingCaptureSuppressFrame = Time.frameCount;
        }

        // Click handler for a "Press key..." button.
        private void ToggleBindingListen(string id)
        {
            bool cancelledByThisClick = bindingClickCancelledId == id;
            bindingClickCancelledId = null;
            if (cancelledByThisClick)
                return;
            bindingListeningFor = bindingListeningFor == id ? null : id;
            bindingListeningJustStarted = true;
        }

        private static KeyCode DetectPressedKey()
        {
            foreach (KeyCode code in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (code == KeyCode.None || (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6))
                    continue;
                if (code == KeyCode.LeftShift || code == KeyCode.RightShift ||
                    code == KeyCode.LeftControl || code == KeyCode.RightControl ||
                    code == KeyCode.LeftAlt || code == KeyCode.RightAlt ||
                    code == KeyCode.LeftCommand || code == KeyCode.RightCommand)
                    continue;
                if (Input.GetKeyDown(code))
                    return code;
            }
            return KeyCode.None;
        }

        private void UpdateInputGate()
        {
            UpdateTextFieldFocusFlag();
            bool newActive;
            string reason;
            // Never swallow input while any text field (chat, search, etc.) is
            // focused — the input gate would otherwise break typing.
            if (GhostLinesPlugin.textFieldFocused)
            {
                newActive = false;
                reason = "textfield";
            }
            else if (freeCamActive || mpFreeCamActive || mpZoomActive)
            {
                newActive = true;
                reason = "freecam";
                RefreshSuppressedInputKeys();
            }
            else if (bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame &&
                mpChatSelActive && !IsChatTyping() &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                newActive = true;
                reason = "ctrl+chatsel";
                RefreshSuppressedInputKeys();
            }
            else
            {
                // The gate never turns on from a key hold alone: holding W/A/S/D is
                // normal flying for keyboard pilots. Only the hold timer for replay
                // freecam entry is tracked here (consumed in the replay camera update);
                // the gate engages once freecam is actually active.
                bool entryHeld = replayReady && bindingListeningFor == null && Time.frameCount != bindingCaptureSuppressFrame && !IsChatTyping() &&
                    FreeCamMove() != Vector3.zero;
                if (entryHeld)
                {
                    if (freeCamEntryHoldStart < 0f)
                        freeCamEntryHoldStart = Time.unscaledTime;
                }
                else
                    freeCamEntryHoldStart = -1f;
                newActive = false;
                reason = "idle";
            }

            if (newActive != GhostLinesPlugin.inputGateActive &&
                Time.unscaledTime >= GhostLinesPlugin.inputGateLogAt)
            {
                GhostLinesPlugin.inputGateLogAt = Time.unscaledTime + 0.5f;
                pluginLog.LogInfo("[GhostLines] input gate " + (newActive ? "ON" : "off") +
                    " reason=" + reason + " replay=" + replayReady + " mp=" + mpInSession);
            }
            GhostLinesPlugin.inputGateActive = newActive;
            if (!newActive)
                suppressedInputKeyCount = 0;
        }

        private static readonly string[] FreeCamBindingNames =
            { "freecamforward", "freecamleft", "freecamback", "freecamright" };

        // Freecam movement from the four freecam bindings (W/A/S/D by default).
        // The only place movement keys are read, so a rebind applies everywhere.
        internal static Vector3 FreeCamMove()
        {
            Vector3 move = Vector3.zero;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamforward"))) move += Vector3.forward;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamback"))) move += Vector3.back;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamleft"))) move += Vector3.left;
            if (Input.GetKey(GhostLinesPlugin.GetBinding("freecamright"))) move += Vector3.right;
            return move;
        }

        private void RefreshSuppressedInputKeys()
        {
            int suppressedKeyCount = 0;
            suppressedInputKeys[suppressedKeyCount++] = KeyCode.W;
            suppressedInputKeys[suppressedKeyCount++] = KeyCode.A;
            suppressedInputKeys[suppressedKeyCount++] = KeyCode.S;
            suppressedInputKeys[suppressedKeyCount++] = KeyCode.D;
            for (int i = 0; i < 26 && suppressedKeyCount < suppressedInputKeys.Length; i++)
                suppressedInputKeys[suppressedKeyCount++] = (KeyCode)(KeyCode.A + i);
            for (int i = 0; i < 10 && suppressedKeyCount < suppressedInputKeys.Length; i++)
            {
                suppressedInputKeys[suppressedKeyCount++] = (KeyCode)(KeyCode.Alpha0 + i);
                if (suppressedKeyCount < suppressedInputKeys.Length)
                    suppressedInputKeys[suppressedKeyCount++] = (KeyCode)(KeyCode.Keypad0 + i);
            }
            for (int bi = 0; bi < FreeCamBindingNames.Length; bi++)
            {
                string bind = FreeCamBindingNames[bi];
                if (suppressedKeyCount >= suppressedInputKeys.Length)
                    break;
                suppressedInputKeys[suppressedKeyCount++] = GhostLinesPlugin.GetBinding(bind);
            }
            suppressedInputKeyCount = suppressedKeyCount;
        }
    }
}
