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
        // Window lifetime, shared between the main thread and the window thread.
        // Transitions only via Interlocked: Starting -> Open (window thread, after
        // CreateWindowEx), Starting/Open -> Closing (main thread), any -> Closed
        // (window thread, when its message loop ends or creation fails).
        private enum NativePipState { Starting = 0, Open = 1, Closing = 2, Closed = 3 }

        private class NativePipExternal
        {
            // Written by the window thread before it publishes Open; read by the
            // main thread only after it has seen Open.
            public volatile IntPtr hwnd = IntPtr.Zero;
            private int state = (int)NativePipState.Starting;
            public readonly object sync = new object();
            public byte[] bgra;
            public int width;
            public int height;
            public bool readbackPending;
            public bool framePushedLogged;
            public bool hwndPushed;
            public float aspect = 1.5f;
            public int cascade;
            public object owner;
            public string label;
            public int labelColorBgr;
            public IntPtr font = IntPtr.Zero;
            public bool fontOwned;
            // Window-thread only: whether the close X is showing.
            public bool hovered;

            public NativePipState State { get { return (NativePipState)System.Threading.Volatile.Read(ref state); } }
            public bool IsOpen { get { return State == NativePipState.Open; } }
            public bool IsClosed { get { return State == NativePipState.Closed; } }

            public bool TryTransition(NativePipState from, NativePipState to)
            {
                return System.Threading.Interlocked.CompareExchange(ref state, (int)to, (int)from) == (int)from;
            }

            public void MarkClosed()
            {
                System.Threading.Interlocked.Exchange(ref state, (int)NativePipState.Closed);
            }
        }

        private delegate IntPtr NativePipWndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static NativePipWndProcDelegate nativePipWndProc = NativePipWndProcHandler;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterClass(ref WNDCLASS lpWndClass);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool PatBlt(IntPtr hdc, int nXLeft, int nYTop, int nWidth, int nHeight, uint dwRop);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int fnObject);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc, byte[] lpBits, ref BITMAPINFO lpbmi, uint iUsage, uint rop);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern uint SetTextColor(IntPtr hdc, uint crColor);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool TextOutW(IntPtr hdc, int x, int y, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpString, int c);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateFontA(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint dwItalic, uint dwUnderline, uint dwStrikeOut, uint dwCharSet, uint dwOutputPrecision, uint dwClipPrecision, uint dwQuality, uint dwPitchAndFamily, string lpszFace);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreatePen(int iStyle, int cWidth, uint color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool LineTo(IntPtr hdc, int x, int y);


        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int SW_SHOW = 5;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_CXVIRTUALSCREEN = 78;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_NCHITTEST = 0x0084;
        private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
        private const uint WM_SIZING = 0x0214;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const int PS_SOLID = 0;
        private const int NATIVE_CLOSE_SIZE = 22;
        private const int NATIVE_CLOSE_MARGIN = 8;
        private static readonly IntPtr NATIVE_HOVER_TIMER = new IntPtr(1);
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int NATIVE_RESIZE_EDGE = 6;
        private const int WMSZ_LEFT = 1;
        private const int WMSZ_RIGHT = 2;
        private const int WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4;
        private const int WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6;
        private const int WMSZ_BOTTOMLEFT = 7;
        private const int WMSZ_BOTTOMRIGHT = 8;
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;
        private const uint BLACKNESS = 0x00000042;
        private const uint SRCCOPY = 0x00CC0020;
        private const int BLACK_BRUSH = 4;
        private const int DEFAULT_GUI_FONT = 17;
        private const int IDC_ARROW = 32512;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int TRANSPARENT = 1;
        private const int FW_NORMAL = 400;
        private const uint DEFAULT_CHARSET = 1;
        private const uint OUT_DEFAULT_PRECIS = 0;
        private const uint CLIP_DEFAULT_PRECIS = 0;
        private const uint CLEARTYPE_QUALITY = 5;
        private const uint DEFAULT_PITCH = 0;

        private static NativePipExternal FindNativePip(IntPtr hWnd)
        {
            lock (nativePipRegistryLock)
            {
                for (int i = 0; i < nativePipRegistry.Count; i++)
                {
                    if (nativePipRegistry[i].hwnd == hWnd)
                        return nativePipRegistry[i];
                }
            }
            return null;
        }

        private static RECT NativePipCloseRect(int clientW)
        {
            RECT r = new RECT();
            r.right = clientW - NATIVE_CLOSE_MARGIN;
            r.left = r.right - NATIVE_CLOSE_SIZE;
            r.top = NATIVE_CLOSE_MARGIN;
            r.bottom = r.top + NATIVE_CLOSE_SIZE;
            return r;
        }

        private static bool InRect(RECT r, int x, int y)
        {
            return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
        }

        // Plain white X like the other windows' close glyphs; the rect is the
        // (larger) hit area.
        private static void DrawNativePipCloseButton(IntPtr hdc, RECT r)
        {
            IntPtr pen = CreatePen(PS_SOLID, 2, 0x00FFFFFF);
            IntPtr oldPen = SelectObject(hdc, pen);
            int inset = 6;
            MoveToEx(hdc, r.left + inset, r.top + inset, IntPtr.Zero);
            LineTo(hdc, r.right - inset, r.bottom - inset);
            MoveToEx(hdc, r.right - inset - 1, r.top + inset, IntPtr.Zero);
            LineTo(hdc, r.left + inset - 1, r.bottom - inset);
            SelectObject(hdc, oldPen);
            DeleteObject(pen);
        }

        // Polls the cursor so the close button shows while the mouse is anywhere
        // over the window (most of it is a caption area, which gets no client
        // mouse-move messages).
        private static void UpdateNativePipHover(IntPtr hWnd, NativePipExternal np)
        {
            POINT pt;
            RECT client;
            if (!GetCursorPos(out pt) || !ScreenToClient(hWnd, ref pt) || !GetClientRect(hWnd, out client))
                return;
            int cw = client.right - client.left;
            int ch = client.bottom - client.top;
            bool hovered = pt.x >= 0 && pt.y >= 0 && pt.x < cw && pt.y < ch;
            if (hovered == np.hovered)
                return;
            np.hovered = hovered;
            InvalidateRect(hWnd, IntPtr.Zero, false);
        }

        private static IntPtr NativePipWndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_PAINT)
            {
                PAINTSTRUCT ps;
                IntPtr hdc = BeginPaint(hWnd, out ps);
                RECT client;
                GetClientRect(hWnd, out client);
                int cw = client.right - client.left;
                int ch = client.bottom - client.top;
                NativePipExternal np = FindNativePip(hWnd);
                if (np != null)
                {
                    lock (np.sync)
                    {
                        if (np.bgra != null && np.width > 0 && np.height > 0)
                        {
                            BITMAPINFO bi = new BITMAPINFO();
                            bi.biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(BITMAPINFO));
                            bi.biWidth = np.width;
                            bi.biHeight = np.height;
                            bi.biPlanes = 1;
                            bi.biBitCount = 32;
                            bi.biCompression = (int)BI_RGB;
                            StretchDIBits(hdc, 0, 0, cw, ch, 0, 0, np.width, np.height, np.bgra, ref bi, DIB_RGB_COLORS, SRCCOPY);
                        }
                        else
                        {
                            PatBlt(hdc, 0, 0, cw, ch, BLACKNESS);
                        }
                        if (np.label != null && np.label.Length > 0)
                        {
                            IntPtr oldFont = SelectObject(hdc, np.font != IntPtr.Zero ? np.font : GetStockObject(DEFAULT_GUI_FONT));
                            SetBkMode(hdc, TRANSPARENT);
                            SetTextColor(hdc, 0x00000000);
                            TextOutW(hdc, 11, 9, np.label, np.label.Length);
                            SetTextColor(hdc, (uint)np.labelColorBgr);
                            TextOutW(hdc, 10, 8, np.label, np.label.Length);
                            SelectObject(hdc, oldFont);
                        }
                    }
                    if (np.hovered)
                        DrawNativePipCloseButton(hdc, NativePipCloseRect(cw));
                }
                else
                {
                    PatBlt(hdc, 0, 0, cw, ch, BLACKNESS);
                }
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }
            if (msg == WM_NCHITTEST)
            {
                int sx = lParam.ToInt32() & 0xFFFF;
                int sy = (lParam.ToInt32() >> 16) & 0xFFFF;
                if (sx >= 32768) sx -= 65536;
                if (sy >= 32768) sy -= 65536;
                POINT pt = new POINT();
                pt.x = sx;
                pt.y = sy;
                if (ScreenToClient(hWnd, ref pt))
                {
                    RECT client;
                    GetClientRect(hWnd, out client);
                    int cw = client.right - client.left;
                    int ch = client.bottom - client.top;
                    if (pt.x >= 0 && pt.y >= 0 && pt.x < cw && pt.y < ch)
                    {
                        bool l = pt.x < NATIVE_RESIZE_EDGE;
                        bool r = pt.x >= cw - NATIVE_RESIZE_EDGE;
                        bool t = pt.y < NATIVE_RESIZE_EDGE;
                        bool b = pt.y >= ch - NATIVE_RESIZE_EDGE;
                        if (l && t) return new IntPtr(HTTOPLEFT);
                        if (r && t) return new IntPtr(HTTOPRIGHT);
                        if (l && b) return new IntPtr(HTBOTTOMLEFT);
                        if (r && b) return new IntPtr(HTBOTTOMRIGHT);
                        if (l) return new IntPtr(HTLEFT);
                        if (r) return new IntPtr(HTRIGHT);
                        if (t) return new IntPtr(HTTOP);
                        if (b) return new IntPtr(HTBOTTOM);
                        if (InRect(NativePipCloseRect(cw), pt.x, pt.y))
                            return new IntPtr(HTCLIENT);
                        return new IntPtr(HTCAPTION);
                    }
                }
                return new IntPtr(HTCLIENT);
            }
            if (msg == WM_NCLBUTTONDBLCLK)
            {
                return IntPtr.Zero;
            }
            if (msg == WM_TIMER && wParam == NATIVE_HOVER_TIMER)
            {
                NativePipExternal np = FindNativePip(hWnd);
                if (np != null)
                    UpdateNativePipHover(hWnd, np);
                return IntPtr.Zero;
            }
            if (msg == WM_LBUTTONDOWN)
                return IntPtr.Zero;
            if (msg == WM_LBUTTONUP)
            {
                int cx = (short)(lParam.ToInt32() & 0xFFFF);
                int cy = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
                RECT client;
                GetClientRect(hWnd, out client);
                if (InRect(NativePipCloseRect(client.right - client.left), cx, cy))
                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
            }
            if (msg == WM_SIZING)
            {
                RECT rc = (RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(RECT));
                NativePipExternal np = FindNativePip(hWnd);
                if (np != null && np.aspect > 0.01f)
                {
                    int edge = wParam.ToInt32();
                    int w = rc.right - rc.left;
                    int h = rc.bottom - rc.top;
                    if (w < 80)
                        w = 80;
                    if (h < 60)
                        h = 60;
                    int newH = (int)((float)w / np.aspect + 0.5f);
                    if (newH < 60)
                        newH = 60;
                    int newW = (int)((float)newH * np.aspect + 0.5f);
                    switch (edge)
                    {
                        case WMSZ_LEFT:
                        case WMSZ_RIGHT:
                            rc.bottom = rc.top + newH;
                            break;
                        case WMSZ_TOP:
                        case WMSZ_BOTTOM:
                            rc.right = rc.left + newW;
                            break;
                        case WMSZ_TOPLEFT:
                            rc.left = rc.right - newW;
                            rc.top = rc.bottom - newH;
                            break;
                        case WMSZ_TOPRIGHT:
                            rc.top = rc.bottom - newH;
                            break;
                        case WMSZ_BOTTOMLEFT:
                            rc.left = rc.right - newW;
                            rc.bottom = rc.top + newH;
                            break;
                        case WMSZ_BOTTOMRIGHT:
                            rc.bottom = rc.top + newH;
                            break;
                    }
                    System.Runtime.InteropServices.Marshal.StructureToPtr(rc, lParam, false);
                }
                return IntPtr.Zero;
            }
            if (msg == WM_CLOSE)
            {
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            }
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static void NativePipThreadMain(object state)
        {
            NativePipExternal np = (NativePipExternal)state;
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external thread entered");
            WNDCLASS wc = new WNDCLASS();
            wc.style = CS_HREDRAW | CS_VREDRAW;
            wc.lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(nativePipWndProc);
            wc.hInstance = GetModuleHandle(null);
            wc.lpszClassName = "GhostLinesNativePipWindow";
            wc.hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW);
            wc.hbrBackground = GetStockObject(BLACK_BRUSH);
            if (!RegisterClass(ref wc))
            {
                int regErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (regErr != 1410)
                {
                    np.MarkClosed();
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external RegisterClass failed err=" + regErr);
                    return;
                }
            }
            int startX = 120;
            int startY = 120;
            int winW = 480;
            int winH = 320;
            int workL = 0;
            int workT = 0;
            int workR = GetSystemMetrics(SM_CXSCREEN);
            int workB = GetSystemMetrics(SM_CYSCREEN);
            try
            {
                int primaryW = GetSystemMetrics(SM_CXSCREEN);
                int virtualW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                POINT probe;
                probe.x = primaryW + 40;
                probe.y = 40;
                IntPtr mon = MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(mon, ref mi))
                {
                    workL = mi.rcWork.left;
                    workT = mi.rcWork.top;
                    workR = mi.rcWork.right;
                    workB = mi.rcWork.bottom;
                }
                else if (virtualW <= primaryW)
                {
                    workL = 0;
                    workT = 0;
                    workR = primaryW;
                    workB = GetSystemMetrics(SM_CYSCREEN);
                }
                else
                {
                    workL = primaryW + 40;
                    workT = 40;
                    workR = virtualW;
                    workB = workT + GetSystemMetrics(SM_CYSCREEN);
                }
                startX = workL + 24 + np.cascade * 28;
                startY = workT + 24 + np.cascade * 28;
                if (startX + winW > workR - 8)
                    startX = workR - winW - 8;
                if (startX < workL)
                    startX = workL;
                if (startY + winH > workB - 8)
                    startY = workB - winH - 8;
                if (startY < workT)
                    startY = workT;
                int availW = workR - workL;
                int availH = workB - workT;
                if (winW > availW - 48)
                    winW = availW - 48;
                if (winH > availH - 48)
                    winH = availH - 48;
                if (winW < 240)
                    winW = 240;
                if (winH < 160)
                    winH = 160;
            }
            catch { }
            if (winH > 0)
                np.aspect = (float)winW / winH;
            if (np.State != NativePipState.Starting)
            {
                // Closed before we even created the window.
                np.MarkClosed();
                return;
            }
            IntPtr hwnd = CreateWindowEx(WS_EX_TOPMOST, wc.lpszClassName, "GhostLines PiP", WS_POPUP,
                startX, startY, winW, winH, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                np.MarkClosed();
                if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external CreateWindowEx failed");
                return;
            }
            np.hwnd = hwnd;
            lock (nativePipRegistryLock)
            {
                nativePipRegistry.Add(np);
            }
            if (!np.TryTransition(NativePipState.Starting, NativePipState.Open))
            {
                // The main thread asked to close while CreateWindowEx ran. It saw
                // Starting, so it didn't post WM_CLOSE; tear the window down here.
                lock (nativePipRegistryLock)
                {
                    nativePipRegistry.Remove(np);
                }
                DestroyWindow(hwnd);
                np.hwnd = IntPtr.Zero;
                np.MarkClosed();
                return;
            }
            np.font = CreateFontA(-16, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, "Segoe UI");
            if (np.font == IntPtr.Zero)
                np.font = GetStockObject(DEFAULT_GUI_FONT);
            else
                np.fontOwned = true;
            ShowWindow(np.hwnd, SW_SHOW);
            try
            {
                SetWindowPos(np.hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
            UpdateWindow(np.hwnd);
            SetTimer(np.hwnd, NATIVE_HOVER_TIMER, 50, IntPtr.Zero);
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external window created at " + startX + "," + startY + " size " + winW + "x" + winH + " work " + workL + "," + workT + "-" + workR + "," + workB);
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (np.fontOwned && np.font != IntPtr.Zero)
            {
                DeleteObject(np.font);
                np.font = IntPtr.Zero;
            }
            lock (nativePipRegistryLock)
            {
                nativePipRegistry.Remove(np);
            }
            np.hwnd = IntPtr.Zero;
            np.MarkClosed();
        }

        private void StartNativePipWindow(NativePipExternal np, int cascadeIndex)
        {
            if (np == null)
                return;
            np.cascade = cascadeIndex;
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external start requested cascade=" + cascadeIndex);
            System.Threading.Thread t = new System.Threading.Thread(NativePipThreadMain);
            t.IsBackground = true;
            t.Name = "GhostLinesNativePip";
            t.Start(np);
        }

        private void DetachNativePipOwner(NativePipExternal np)
        {
            if (np.owner is PiPWindow && ((PiPWindow)np.owner).nativePip == np)
                ((PiPWindow)np.owner).nativePip = null;
            else if (np.owner is MultiplayerPipWindow && ((MultiplayerPipWindow)np.owner).nativePip == np)
                ((MultiplayerPipWindow)np.owner).nativePip = null;
            np.owner = null;
        }

        private void CloseNativePipWindow(NativePipExternal np)
        {
            if (np == null)
                return;
            nativePips.Remove(np);
            DetachNativePipOwner(np);
            // Starting -> Closing: the window thread sees it after CreateWindowEx
            // and destroys the window itself. Open -> Closing: hwnd is published,
            // so ask the window thread to close it.
            if (np.TryTransition(NativePipState.Starting, NativePipState.Closing))
                return;
            if (np.TryTransition(NativePipState.Open, NativePipState.Closing))
                PostMessage(np.hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private void CloseAllNativePipWindows()
        {
            for (int i = nativePips.Count - 1; i >= 0; i--)
                CloseNativePipWindow(nativePips[i]);
        }

        private void UpdateNativePipExternal()
        {
            if (!pipExternalMonitor)
            {
                if (nativePips.Count > 0)
                {
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external windows closed (toggle off)");
                    CloseAllNativePipWindows();
                }
                return;
            }
            for (int i = nativePips.Count - 1; i >= 0; i--)
            {
                NativePipExternal np = nativePips[i];
                if (np.IsClosed)
                {
                    nativePips.RemoveAt(i);
                    if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external window closed by user");
                    object closedOwner = np.owner;
                    DetachNativePipOwner(np);
                    if (closedOwner is PiPWindow)
                        ClosePiPWindow((PiPWindow)closedOwner);
                    else if (closedOwner is MultiplayerPipWindow)
                        CloseMultiplayerPipWindow((MultiplayerPipWindow)closedOwner);
                }
            }
            for (int i = 0; i < pipWindows.Count; i++)
            {
                PiPWindow w = pipWindows[i];
                if (w.entry == null)
                    continue;
                if (w.nativePip == null)
                {
                    NativePipExternal np = new NativePipExternal();
                    np.owner = w;
                    w.nativePip = np;
                    nativePips.Add(np);
                    StartNativePipWindow(np, nativePips.Count - 1);
                }
                else if (w.nativePip.IsOpen && !w.nativePip.hwndPushed)
                {
                    w.nativePip.hwndPushed = true;
                    try
                    {
                        EnsurePiPRenderTexture(w);
                        RenderPiPWindow(w);
                    }
                    catch (System.Exception catchEx) { ModLog.LogFile("[GhostLines] MPPIP external catchup EX " + catchEx); }
                }
            }
            for (int i = 0; i < multiplayerPipWindows.Count; i++)
            {
                MultiplayerPipWindow w = multiplayerPipWindows[i];
                if (w == null || w.target == null)
                    continue;
                if (w.nativePip == null)
                {
                    NativePipExternal np = new NativePipExternal();
                    np.owner = w;
                    w.nativePip = np;
                    nativePips.Add(np);
                    StartNativePipWindow(np, nativePips.Count - 1);
                }
                else if (w.nativePip.IsOpen && !w.nativePip.hwndPushed)
                {
                    w.nativePip.hwndPushed = true;
                    try
                    {
                        EnsureMultiplayerPipRenderTexture(w);
                        RenderMultiplayerPipWindow(w);
                    }
                    catch (System.Exception catchEx) { ModLog.LogFile("[GhostLines] MPPIP external catchup EX " + catchEx); }
                }
            }
            for (int i = nativePips.Count - 1; i >= 0; i--)
            {
                NativePipExternal np = nativePips[i];
                bool ownerGone = (np.owner is PiPWindow && !pipWindows.Contains((PiPWindow)np.owner))
                    || (np.owner is MultiplayerPipWindow && !multiplayerPipWindows.Contains((MultiplayerPipWindow)np.owner));
                if (ownerGone)
                    CloseNativePipWindow(np);
            }
        }

        private void SetPipExternalMonitor(bool enabled)
        {
            pipExternalMonitor = enabled;
            if (ModLog.DebugEnabled) ModLog.LogFile("[GhostLines] MPPIP external toggle -> " + enabled);
            if (pipExternalMonitorConfig != null)
            {
                pipExternalMonitorConfig.Value = enabled;
                Prefs.RequestSave();
            }
        }

        private void PushNativePipFrame(NativePipExternal np, RenderTexture rt)
        {
            if (np == null || !np.IsOpen || rt == null || rt.width <= 0 || rt.height <= 0)
                return;
            // Called right after the PiP camera renders, so the push rate follows
            // the "PIP Refresh Rate" setting.
            if (np.readbackPending)
                return;
            UpdateNativePipLabel(np);
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                // Non-blocking: the GPU copies the frame and the callback runs on the
                // main thread a frame or two later. BGRA32 is what GDI's 32-bit DIB
                // expects, so the bytes can be copied straight across.
                np.readbackPending = true;
                UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0,
                    nativePipAsyncBgra ? TextureFormat.BGRA32 : TextureFormat.RGBA32,
                    req => OnNativePipReadback(np, req));
                return;
            }
            PushNativePipFrameSync(np, rt);
        }

        // Converts to BGRA32 on the GPU. Falls back to RGBA32 plus a CPU swizzle
        // only if BGRA32 fails repeatedly without ever succeeding: a single error
        // (e.g. the render texture was resized mid-request) says nothing about
        // format support.
        private static bool nativePipAsyncBgra = true;
        private static bool nativePipBgraConfirmed = false;
        private static int nativePipBgraErrors = 0;

        private void OnNativePipReadback(NativePipExternal np, UnityEngine.Rendering.AsyncGPUReadbackRequest req)
        {
            np.readbackPending = false;
            if (req.hasError)
            {
                if (nativePipAsyncBgra && !nativePipBgraConfirmed && ++nativePipBgraErrors >= 3)
                {
                    nativePipAsyncBgra = false;
                    ModLog.LogFile("[GhostLines] MPPIP external BGRA32 readback failed; using RGBA32");
                }
                return;
            }
            if (nativePipAsyncBgra)
            {
                nativePipBgraConfirmed = true;
                nativePipBgraErrors = 0;
            }
            if (!np.IsOpen)
                return;
            int w = req.width;
            int h = req.height;
            Unity.Collections.NativeArray<byte> data = req.GetData<byte>();
            if (data.Length < w * h * 4)
                return;
            bool bgra = nativePipAsyncBgra;
            lock (np.sync)
            {
                EnsureNativePipBuffer(np, w, h);
                if (bgra)
                    Unity.Collections.NativeArray<byte>.Copy(data, np.bgra, w * h * 4);
                else
                    SwizzleRgbaToBgra(data, np.bgra, w * h);
            }
            FinishNativePipPush(np, w, h);
        }

        private void PushNativePipFrameSync(NativePipExternal np, RenderTexture rt)
        {
            int w = rt.width;
            int h = rt.height;
            if (nativePipReadTex == null || nativePipReadTex.width != w || nativePipReadTex.height != h)
            {
                if (nativePipReadTex != null)
                    Destroy(nativePipReadTex);
                nativePipReadTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                nativePipReadTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            }
            finally
            {
                RenderTexture.active = prev;
            }
            nativePipReadTex.Apply(false);
            Unity.Collections.NativeArray<byte> raw = nativePipReadTex.GetRawTextureData<byte>();
            lock (np.sync)
            {
                EnsureNativePipBuffer(np, w, h);
                SwizzleRgbaToBgra(raw, np.bgra, w * h);
            }
            FinishNativePipPush(np, w, h);
        }

        private void UpdateNativePipLabel(NativePipExternal np)
        {
            string label = np.label;
            int color = np.labelColorBgr;
            if (np.owner is PiPWindow)
            {
                PiPWindow pw = (PiPWindow)np.owner;
                if (pw.entry != null)
                {
                    label = GetPilotLabel(pw.entry, GhostLinesPlugin.entries.IndexOf(pw.entry));
                    Color pc = pw.entry.color;
                    color = (((int)(pc.b * 255f) & 255) << 16) | (((int)(pc.g * 255f) & 255) << 8) | ((int)(pc.r * 255f) & 255);
                }
            }
            else if (np.owner is MultiplayerPipWindow)
            {
                MultiplayerPipWindow mw = (MultiplayerPipWindow)np.owner;
                if (mw.target != null && mw.target.playerName != null)
                {
                    label = mw.target.playerName;
                    color = (220 << 16) | (220 << 8) | 220;
                }
            }
            if (label == np.label && color == np.labelColorBgr)
                return;
            lock (np.sync)
            {
                np.label = label;
                np.labelColorBgr = color;
            }
        }

        // Caller holds np.sync. Dimensions are updated even when the byte count
        // is unchanged (e.g. 200x100 -> 100x200).
        private static void EnsureNativePipBuffer(NativePipExternal np, int w, int h)
        {
            if (np.bgra == null || np.bgra.Length != w * h * 4)
                np.bgra = new byte[w * h * 4];
            np.width = w;
            np.height = h;
        }

        private static void SwizzleRgbaToBgra(Unity.Collections.NativeArray<byte> src, byte[] dst, int pixels)
        {
            for (int i = 0, n = pixels * 4; i < n; i += 4)
            {
                dst[i] = src[i + 2];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i];
                dst[i + 3] = 255;
            }
        }

        private void FinishNativePipPush(NativePipExternal np, int w, int h)
        {
            IntPtr hwnd = np.hwnd;
            if (np.IsOpen && hwnd != IntPtr.Zero)
                InvalidateRect(hwnd, IntPtr.Zero, false);
            if (np.framePushedLogged || !ModLog.DebugEnabled)
                return;
            np.framePushedLogged = true;
            long lumSum = 0;
            long lumCount = 0;
            lock (np.sync)
            {
                for (int pi = 0; pi + 2 < np.bgra.Length; pi += 64)
                {
                    int r = np.bgra[pi];
                    int g = np.bgra[pi + 1];
                    int b = np.bgra[pi + 2];
                    int mx = r > g ? r : g;
                    if (b > mx) mx = b;
                    lumSum += mx;
                    lumCount++;
                }
            }
            ModLog.LogFile("[GhostLines] MPPIP external frame pushed " + w + "x" + h + " lum=" + (lumSum / (double)Math.Max(1L, lumCount)).ToString("F1"));
        }
        private bool multiplayerPipMenuOpen = false;
        private float multiplayerPipMenuFade = 0f;
        // Replay-style idle fading + pinning for the MP windows.
        private bool mpMenuPinned = false;
        private float mpMenuIdleAlpha = 1f;
        private Rect mpMenuScreenRect = new Rect(0f, 0f, -1f, -1f);
        private bool lobbyBrowserPinned = false;
        private float lobbyBrowserIdleAlpha = 1f;
        private Rect lobbyBrowserScreenRect = new Rect(0f, 0f, -1f, -1f);
        private Vector2 overlayFadeLastMouse = new Vector2(-99999f, -99999f);
        private float overlayMouseMovedAt = 0f;
        // Gate-splits panel + lap-history window pins/fade (same idle-fade system).
        // Pinned by default so they stay fully visible unless the user unpins them.
        private bool raceSplitsPinned = true;
        private float raceSplitsIdleAlpha = 1f;
        private Rect raceSplitsScreenRect = new Rect(0f, 0f, -1f, -1f);
        private bool raceHistoryPinned = true;
        private float raceHistoryIdleAlpha = 1f;
        private Rect raceHistoryScreenRect = new Rect(0f, 0f, -1f, -1f);
        // Multiplier applied by the Theme* helpers so a whole window can fade.
        private float uiFadeAlpha = 1f;
        private int mpF10ToggleFrame = -1;
        private bool multiplayerPipAutoShownForTargets = false;
        private Vector2 mpMenuDragOffset = Vector2.zero;
        private readonly WindowDrag mpMenuDrag = new WindowDrag();
        private float multiplayerPipLastMouseMove = 0f;
        private Vector2 multiplayerPipLastMouse = Vector2.zero;
        private float nextMultiplayerPipScan = 0f;
        private float nextOrphanRescanAt = 0f;
        private float nextPollerStatusLog = 0f;
        private readonly System.Diagnostics.Stopwatch perfSw = new System.Diagnostics.Stopwatch();
        private float perfWindowStart = 0f;
        private float perfMaxFrame = 0f;
        private int perfGc0 = 0;
        private int perfGc1 = 0;
        private float perfLbMs;
        private float perfMatchMs;
        private float perfScanMs;
        private float perfLobbyMs;
        private float perfPipMs;
        private float perfChatTrackMs;
        private float perfChatMs;
        // Breakdown of DrawChatOverlays: the UI-gate scene scans, the selection
        // entry rebuild (TextGenerator.Populate), and the colour picker button.
        private readonly System.Diagnostics.Stopwatch chatDiagSw = new System.Diagnostics.Stopwatch();
        private float perfChatGateMs;
        private float perfChatBuildMs;
        private float perfChatPickerMs;
        private int perfChatN;
        private int perfChatEntriesN;
        private bool perfChatSel;
        private bool perfChatHover;
        // Camera callbacks (ghost transforms / trails / xray) run per-camera and are
        // invisible to the Update/OnGUI buckets, so time them explicitly.
        private readonly System.Diagnostics.Stopwatch cameraCbSw = new System.Diagnostics.Stopwatch();
        private float perfPreCullMs;
        private float perfPostCullMs;
        private float perfEnforceMs;
        private float perfInGateMs;
        private float perfLbUiMs;
        private float perfMpPipMs;
        private float perfFreeCamMs;
        private float perfNativePipMs;
        private float perfCamMs;
        private float perfFcKeysMs;
        private float perfFcZoomMs;
        private float perfBindMs;
        private float perfPwMouseMs;
        private float perfMpInMs;
        private float perfOvMouseMs;
        private float perfDroneMs;
        private float perfDiagMs;
        private float nextGateDiagHeavy = 0f;
        private readonly System.Diagnostics.Stopwatch perfMpPipSw = new System.Diagnostics.Stopwatch();
        private bool perfWindowActive = false;

        private int enforceOverridesFrame = -1;
    }
}
