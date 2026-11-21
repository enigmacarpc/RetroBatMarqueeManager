using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Forces a window above a fullscreen frontend (RetroBat/EmulationStation). Windows normally refuses to let a
    /// background process take the foreground, so the thread input is attached to the current foreground window first
    /// and the window is re-asserted as topmost for a moment while it opens.
    /// </summary>
    public static class WindowFocus
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        /// <summary>EN: Keeps pushing the (Explorer) file dialog with this title to the front while it opens.</summary>
        public static void KeepDialogOnTop(string title)
        {
            int ticks = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (_, _) =>
            {
                if (++ticks > 10) { timer.Stop(); timer.Dispose(); return; }
                var h = FindWindow("#32770", title);
                if (h != IntPtr.Zero) ForceToFront(h);
            };
            timer.Start();
        }

        public static void ForceToFront(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
                uint thisThread = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);

                // EN: a synthetic Alt press lifts the "foreground lock" on most Windows versions.
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                ShowWindow(hwnd, SW_RESTORE);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetActiveWindow(hwnd);

                if (attached) AttachThreadInput(thisThread, fgThread, false);
            }
            catch { /* best effort */ }
        }

        /// <summary>EN: Brings the form to the front now and keeps re-asserting it for about a second (the frontend may grab the focus back).</summary>
        public static void KeepOnTop(Form form)
        {
            form.TopMost = true;
            ForceToFront(form.Handle);
            int ticks = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (_, _) =>
            {
                if (form.IsDisposed || ++ticks > 7) { timer.Stop(); timer.Dispose(); return; }
                ForceToFront(form.Handle);
            };
            timer.Start();
        }
    }
}
