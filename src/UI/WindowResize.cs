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
        // Shared window-resize helper (StickGraph-style): blue edge/corner bars on
        // hover, drag to resize. Flags reuse STICK_GRAPH_EDGE_*.
        private static int ComputeWindowResizeEdge(Rect r, Vector2 m, bool skipTopEdge)
        {
            float edgeT = STICK_GRAPH_EDGE_THICK;
            float cornerT = STICK_GRAPH_CORNER_GRAB;
            Rect edgeTop = new Rect(r.x, r.y, r.width, edgeT);
            Rect edgeBottom = new Rect(r.x, r.yMax - edgeT, r.width, edgeT);
            Rect edgeLeft = new Rect(r.x, r.y, edgeT, r.height);
            Rect edgeRight = new Rect(r.xMax - edgeT, r.y, edgeT, r.height);
            Rect cornerTopLeft = new Rect(r.x, r.y, cornerT, cornerT);
            Rect cornerTopRight = new Rect(r.xMax - cornerT, r.y, cornerT, cornerT);
            Rect cornerBottomLeft = new Rect(r.x, r.yMax - cornerT, cornerT, cornerT);
            Rect cornerBottomRight = new Rect(r.xMax - cornerT, r.yMax - cornerT, cornerT, cornerT);
            int hover = 0;
            if (cornerTopLeft.Contains(m))
                hover = STICK_GRAPH_EDGE_LEFT | STICK_GRAPH_EDGE_TOP;
            else if (cornerTopRight.Contains(m))
                hover = STICK_GRAPH_EDGE_RIGHT | STICK_GRAPH_EDGE_TOP;
            else if (cornerBottomLeft.Contains(m))
                hover = STICK_GRAPH_EDGE_LEFT | STICK_GRAPH_EDGE_BOTTOM;
            else if (cornerBottomRight.Contains(m))
                hover = STICK_GRAPH_EDGE_RIGHT | STICK_GRAPH_EDGE_BOTTOM;
            else
            {
                if (edgeRight.Contains(m)) hover |= STICK_GRAPH_EDGE_RIGHT;
                if (edgeBottom.Contains(m)) hover |= STICK_GRAPH_EDGE_BOTTOM;
                if (edgeLeft.Contains(m)) hover |= STICK_GRAPH_EDGE_LEFT;
                if (edgeTop.Contains(m) && !skipTopEdge) hover |= STICK_GRAPH_EDGE_TOP;
            }
            return hover;
        }

        private void DrawWindowResizeBars(Rect r, int shown, float alpha = 1f)
        {
            if (shown == 0)
                return;
            float edgeT = STICK_GRAPH_EDGE_THICK;
            GUI.color = new Color(0.25f, 0.62f, 0.95f, 0.55f * alpha);
            if ((shown & STICK_GRAPH_EDGE_LEFT) != 0)
                GUI.DrawTexture(new Rect(r.x, r.y, edgeT, r.height), whiteTex);
            if ((shown & STICK_GRAPH_EDGE_RIGHT) != 0)
                GUI.DrawTexture(new Rect(r.xMax - edgeT, r.y, edgeT, r.height), whiteTex);
            if ((shown & STICK_GRAPH_EDGE_TOP) != 0)
            {
                float lt = (shown & STICK_GRAPH_EDGE_LEFT) != 0 ? edgeT : 0f;
                float rt = (shown & STICK_GRAPH_EDGE_RIGHT) != 0 ? edgeT : 0f;
                GUI.DrawTexture(new Rect(r.x + lt, r.y, r.width - lt - rt, edgeT), whiteTex);
            }
            if ((shown & STICK_GRAPH_EDGE_BOTTOM) != 0)
            {
                float lt = (shown & STICK_GRAPH_EDGE_LEFT) != 0 ? edgeT : 0f;
                float rt = (shown & STICK_GRAPH_EDGE_RIGHT) != 0 ? edgeT : 0f;
                GUI.DrawTexture(new Rect(r.x + lt, r.yMax - edgeT, r.width - lt - rt, edgeT), whiteTex);
            }
        }

        // Aspect-locked resize shared by the replay and MP PiP windows. A corner
        // scales toward whichever axis moved more; a side scales about the
        // opposite side's centre. (dx, dy) is the mouse travel since the start.
        private static Rect ApplyPipAspectResize(int edge, Rect start, float dx, float dy)
        {
            bool leftE = (edge & PIP_EDGE_LEFT) != 0;
            bool topE = (edge & PIP_EDGE_TOP) != 0;
            bool rightE = (edge & PIP_EDGE_RIGHT) != 0;
            bool bottomE = (edge & PIP_EDGE_BOTTOM) != 0;
            bool corner = (leftE || rightE) && (topE || bottomE);
            float maxW = Mathf.Max(PIP_MIN_WIDTH, Screen.width - 24f);
            float maxH = Mathf.Max(PIP_MIN_HEIGHT, Screen.height - 24f);
            float sx;
            float sy;
            if (corner)
            {
                sx = leftE ? -dx : dx;
                sy = topE ? -dy : dy;
            }
            else if (leftE || rightE)
            {
                sx = leftE ? -dx : dx;
                sy = sx;
            }
            else
            {
                sy = topE ? -dy : dy;
                sx = sy;
            }
            float fw = start.width > 0f ? 1f + sx / start.width : 1f;
            float fh = start.height > 0f ? 1f + sy / start.height : 1f;
            float f = Mathf.Max(fw, fh);
            f = Mathf.Max(f, Mathf.Max(PIP_MIN_WIDTH / start.width, PIP_MIN_HEIGHT / start.height));
            f = Mathf.Min(f, maxW / start.width, maxH / start.height);
            float newW = start.width * f;
            float newH = start.height * f;
            float newX = start.x;
            float newY = start.y;
            if (leftE)
                newX = start.x + (start.width - newW);
            if (topE)
                newY = start.y + (start.height - newH);
            if (!corner && (leftE || rightE))
                newY = start.y + (start.height - newH) * 0.5f;
            else if (!corner)
                newX = start.x + (start.width - newW) * 0.5f;
            return new Rect(newX, newY, newW, newH);
        }

        private Rect ApplyWindowResizeDrag(int edge, Vector2 startMouse, Rect startRect, Vector2 mouse, float minW, float minH)
        {
            float dx = mouse.x - startMouse.x;
            float dy = mouse.y - startMouse.y;
            float newX = startRect.x;
            float newY = startRect.y;
            float newW = startRect.width;
            float newH = startRect.height;
            float maxW = Mathf.Max(minW, Screen.width - 24f);
            float maxH = Mathf.Max(minH, Screen.height - 24f);
            if ((edge & STICK_GRAPH_EDGE_RIGHT) != 0)
                newW = Mathf.Clamp(startRect.width + dx, minW, maxW);
            if ((edge & STICK_GRAPH_EDGE_BOTTOM) != 0)
                newH = Mathf.Clamp(startRect.height + dy, minH, maxH);
            if ((edge & STICK_GRAPH_EDGE_LEFT) != 0)
            {
                newW = Mathf.Clamp(startRect.width - dx, minW, maxW);
                newX = startRect.x + (startRect.width - newW);
            }
            if ((edge & STICK_GRAPH_EDGE_TOP) != 0)
            {
                newH = Mathf.Clamp(startRect.height - dy, minH, maxH);
                newY = startRect.y + (startRect.height - newH);
            }
            return new Rect(newX, newY, newW, newH);
        }
    }
}
