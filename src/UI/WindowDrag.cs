using UnityEngine;

namespace LiftoffGhostLines
{
    // Drag-to-move for the overlay windows (settings, bindings, MP menu, lobby,
    // gate splits, lap history), which each used to hand-roll the same
    // candidate/promote/offset bookkeeping.
    //
    // Arm on MouseDown in the window's drag zone; the first MouseDrag promotes it
    // to a real drag unless a control inside the window owns the gesture
    // ("blocked"). The window's position (absolute or an offset, the caller's
    // choice) follows start position + mouse delta.
    internal sealed class WindowDrag
    {
        public bool Armed { get; private set; }
        public bool Dragging { get; private set; }

        private Vector2 startMouse;
        private Vector2 startPos;
        private bool blockedAtArm;
        private bool armedThisEvent;

        public void Arm(Vector2 mouse, Vector2 pos, bool blocked = false)
        {
            Armed = true;
            Dragging = false;
            startMouse = mouse;
            startPos = pos;
            blockedAtArm = blocked;
            armedThisEvent = true;
        }

        // Drag right away, for title bars.
        public void Begin(Vector2 mouse, Vector2 pos)
        {
            Arm(mouse, pos);
            Promote();
        }

        // Turn an armed click into a drag without waiting for movement.
        public void Promote()
        {
            if (!Armed)
                return;
            Armed = false;
            Dragging = !blockedAtArm;
        }

        public void Cancel()
        {
            Armed = false;
            Dragging = false;
        }

        // Feed OnGUI events (every event, after any Arm for it). Moves pos on
        // left-button MouseDrag and uses the event while dragging. Returns true
        // when pos changed.
        public bool Drag(Event e, bool blocked, ref Vector2 pos)
        {
            // A control inside the window (toggle, button) often uses the
            // MouseUp, before or after this call, so the caller's End() never
            // sees it. Left armed, the stale gesture would promote on the next
            // drag anywhere and jump the window by the old click offset. An
            // armed-only click needs no End() (nothing moved), so drop it on any
            // release; a real drag is left for End() unless someone used the
            // release. rawType survives Use().
            if (e.rawType == EventType.MouseUp && (!Dragging || e.type == EventType.Used))
                Cancel();
            // Any press this window didn't arm for starts a new gesture.
            if (e.rawType == EventType.MouseDown)
            {
                if (!armedThisEvent)
                    Cancel();
                armedThisEvent = false;
            }
            if (e.type != EventType.MouseDrag || e.button != 0)
                return false;
            if (Armed)
            {
                Armed = false;
                Dragging = !(blocked || blockedAtArm);
            }
            if (!Dragging)
                return false;
            pos = startPos + (e.mousePosition - startMouse);
            e.Use();
            return true;
        }

        // Mouse released: clears the gesture and says whether a drag was active.
        public bool End()
        {
            bool wasDragging = Dragging;
            Cancel();
            return wasDragging;
        }
    }
}
