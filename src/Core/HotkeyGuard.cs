using System.Collections.Generic;
using UnityEngine;

namespace LiftoffGhostLines
{
    public partial class GhostLinePoller
    {
        // Mod bindings that are live in the replay. V is left out: CameraViewToggle
        // already decides who owns it.
        private static readonly string[] ReplayGuardBindings =
        {
            "playpause", "restart", "xray", "smooth", "rewind", "ff", "nametags",
            "alllines", "linesthrough", "delta", "heatmap", "loop", "graph", "holdzoom",
            "freecamforward", "freecamback", "freecamleft", "freecamright",
            "specpilot1", "specpilot2", "specpilot3", "specpilot4", "specpilot5", "pip",
        };

        // Mod bindings that are live in a multiplayer room (besides drone hotkeys).
        private static readonly string[] RoomGuardBindings =
        {
            "mpfreecam", "holdzoom", "nametags", "livegraph",
        };

        // Adds the keys pressed this frame that the mod acts on in the current
        // context, so the game's action on the same key can be skipped (see
        // GhostLinesPlugin.GateRewiredBool). Mirrors the handlers' own gates
        // closely enough: a mod key always wins over a game action on it.
        internal void CollectPressedModHotkeys(List<KeyCode> keys)
        {
            if (!Input.anyKeyDown)
                return;
            if (IsChatTyping())
                return;
            // Capturing a binding: whatever key is pressed belongs to the mod
            // (binding F used to toggle fisheye too).
            if (bindingListeningFor != null)
            {
                KeyCode pressed = DetectPressedKey();
                AddGuardKey(keys, pressed);
                return;
            }
            if (replayReady)
            {
                for (int i = 0; i < ReplayGuardBindings.Length; i++)
                    AddGuardKeyIfDown(keys, GhostLinesPlugin.GetBinding(ReplayGuardBindings[i]));
                return;
            }
            AddGuardKeyIfDown(keys, GhostLinesPlugin.GetBinding("mpmenu"));
            if (!PhotonInRoom())
                return;
            for (int i = 0; i < RoomGuardBindings.Length; i++)
                AddGuardKeyIfDown(keys, GhostLinesPlugin.GetBinding(RoomGuardBindings[i]));
            for (int i = 0; i < droneSwitchEntries.Count; i++)
            {
                DroneSwitchEntry entry = droneSwitchEntries[i];
                if (entry != null && !string.IsNullOrEmpty(entry.localId))
                    AddGuardKeyIfDown(keys, GhostLinesPlugin.GetDroneBinding(entry.localId));
            }
            // Ctrl+1..5 opens MP PiP windows (HandleMultiplayerPipDirectKeys); the
            // bare digits are the game's flight-mode keys.
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            {
                for (int i = 0; i < 5; i++)
                {
                    AddGuardKeyIfDown(keys, KeyCode.Alpha1 + i);
                    AddGuardKeyIfDown(keys, KeyCode.Keypad1 + i);
                }
            }
        }

        private static void AddGuardKeyIfDown(List<KeyCode> keys, KeyCode key)
        {
            if (key != KeyCode.None && Input.GetKeyDown(key))
                AddGuardKey(keys, key);
        }

        private static void AddGuardKey(List<KeyCode> keys, KeyCode key)
        {
            if (key != KeyCode.None && !keys.Contains(key))
                keys.Add(key);
        }
    }
}
