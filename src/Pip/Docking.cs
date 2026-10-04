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
        private bool IsStickCamDocked(StickCamInstance inst)
        {
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w == null)
                    continue;
                for (int j = 0; j < w.docked.Count; j++)
                {
                    if (w.docked[j].stick == inst)
                        return true;
                }
            }
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w == null)
                    continue;
                for (int j = 0; j < w.docked.Count; j++)
                {
                    if (w.docked[j].stick == inst)
                        return true;
                }
            }
            return false;
        }

        private bool IsLiveGraphDocked(LiveGraphState g)
        {
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w == null)
                    continue;
                for (int j = 0; j < w.docked.Count; j++)
                {
                    if (w.docked[j].graph == g)
                        return true;
                }
            }
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w == null)
                    continue;
                for (int j = 0; j < w.docked.Count; j++)
                {
                    if (w.docked[j].graph == g)
                        return true;
                }
            }
            return false;
        }

        private MultiplayerPipWindow FindMpPipWindowForDock(Vector2 pos)
        {
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w == null || w.target == null || w.target.root == null)
                    continue;
                if (GetPiPContentRect(w.rect).Contains(pos))
                    return w;
            }
            return null;
        }

        // Dock slots are a 3x3 grid over a PiP's content area. These work on both
        // PiP kinds (replay PiPWindow and MultiplayerPipWindow), which used to
        // each have their own copy.
        private int GetPipDockSlotAt(Rect windowRect, Vector2 pos)
        {
            Rect content = GetPiPContentRect(windowRect);
            if (!content.Contains(pos))
                return -1;
            int col = Mathf.Clamp((int)((pos.x - content.x) / (content.width / 3f)), 0, 2);
            int row = Mathf.Clamp((int)((pos.y - content.y) / (content.height / 3f)), 0, 2);
            return row * 3 + col;
        }

        private Rect GetPipDockSlotRect(Rect content, int slot)
        {
            int col = slot % 3;
            int row = slot / 3;
            float cellW = content.width / 3f;
            float cellH = content.height / 3f;
            return new Rect(content.x + col * cellW, content.y + row * cellH, cellW, cellH);
        }

        private static bool PipSlotOccupied(List<PipDockEntry> docked, int slot)
        {
            for (int i = 0; i < docked.Count; i++)
            {
                if (docked[i].slot == slot)
                    return true;
            }
            return false;
        }

        // Puts an item in a slot, replacing whatever was there and moving the item
        // if it was already docked elsewhere in this PiP.
        private static void DockInto(List<PipDockEntry> docked, PipDockEntry entry)
        {
            for (int i = docked.Count - 1; i >= 0; i--)
            {
                PipDockEntry d = docked[i];
                if (d.slot == entry.slot ||
                    (entry.stick != null && d.stick == entry.stick) ||
                    (entry.graph != null && d.graph == entry.graph) ||
                    (entry.stickGraph != null && d.stickGraph == entry.stickGraph))
                    docked.RemoveAt(i);
            }
            docked.Add(entry);
        }

        private static void DockStickCamInto(List<PipDockEntry> docked, StickCamInstance inst, int slot)
        {
            DockInto(docked, new PipDockEntry { stick = inst, slot = slot });
        }

        // Docked graphs are parked off-screen so their floating window stays hidden.
        private static void DockGraphInto(List<PipDockEntry> docked, LiveGraphState g, int slot)
        {
            DockInto(docked, new PipDockEntry { graph = g, slot = slot });
            g.rect = new Rect(-100000f, -100000f, g.rect.width, g.rect.height);
        }

        private PiPWindow FindReplayPipWindowForDock(Vector2 pos)
        {
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w == null || w.entry == null)
                    continue;
                if (GetPiPContentRect(w.rect).Contains(pos))
                    return w;
            }
            return null;
        }


        private bool IsStickGraphDocked(StickGraphState sg)
        {
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w == null)
                    continue;
                for (int j = 0; j < w.docked.Count; j++)
                {
                    if (w.docked[j].stickGraph == sg)
                        return true;
                }
            }
            return false;
        }

        private static void DockStickGraphInto(List<PipDockEntry> docked, StickGraphState sg, int slot)
        {
            DockInto(docked, new PipDockEntry { stickGraph = sg, slot = slot });
            sg.rect = new Rect(-100000f, -100000f, sg.rect.width, sg.rect.height);
        }
    }
}
