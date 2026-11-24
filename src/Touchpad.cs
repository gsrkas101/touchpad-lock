// Windows calls, and the part that reads and switches the real touchpad state.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace TouchpadLock
{
    static class Native
    {
        public const int WS_EX_TOPMOST = 0x08, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        public const uint SPI_GETTOUCHPADPARAMETERS = 0x00AE;
        public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        // The pad makes it as large as the biggest member (MOUSEINPUT) of the native union.
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint action, uint size, uint[] data, uint flags);
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        public static void KeepOnTop(IntPtr hwnd)
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // no size, move or activate
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POWER_THROTTLING { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref POWER_THROTTLING info, int size);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        // Otherwise Windows slows down animation timing of a background app.
        public static void DisablePowerThrottling()
        {
            try
            {
                var p = new POWER_THROTTLING { Version = 1, ControlMask = 1 | 4 };
                SetProcessInformation(GetCurrentProcess(), 4, ref p, Marshal.SizeOf(p));
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        // Shows a 32-bit ARGB bitmap as the content of a WS_EX_LAYERED window, with per-pixel alpha.
        public static void SetLayeredBitmap(IntPtr hwnd, Bitmap bmp, int x, int y, byte opacity)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr hbitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memory, hbitmap);
            try
            {
                var dst = new POINT { x = x, y = y };
                var src = new POINT();
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var blend = new BLENDFUNCTION { SourceConstantAlpha = opacity, AlphaFormat = 1 };
                UpdateLayeredWindow(hwnd, screen, ref dst, ref size, memory, ref src, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(hbitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    static class Touchpad
    {
        const string StatusKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";
        const int VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LCONTROL = 0xA2, VK_LWIN = 0x5B, VK_F24 = 0x87;
        static readonly IntPtr Signature = new IntPtr(0x54504C4B);

        // TOUCHPAD_PARAMETERS bits: 0 = touchpad present, 2 = external mouse present, 3 = enabled.
        static bool ReadFlags(out uint flags)
        {
            var data = new uint[11];
            data[0] = 1;
            bool ok = Native.SystemParametersInfo(Native.SPI_GETTOUCHPADPARAMETERS, 44, data, 0);
            flags = data[3];
            return ok;
        }

        public static bool IsEnabled()
        {
            uint flags;
            if (ReadFlags(out flags)) return (flags & 8) != 0;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StatusKey))   // older Windows 11
                return key == null || Convert.ToInt32(key.GetValue("Enabled", 1)) != 0;
        }

        // Unknown counts as "a mouse is there".
        public static bool MousePresent()
        {
            uint flags;
            return !ReadFlags(out flags) || (flags & 4) != 0;
        }

        // Blocks for up to about 5 seconds, so call it from a worker thread. The shortcut is a toggle,
        // so it's only pressed while the state is wrong, and the state is checked after every press.
        public static bool SetEnabled(bool enabled, Action beforeRetry)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (IsEnabled() == enabled) return true;
                if (attempt > 0) beforeRetry();
                PressShortcut(() => IsEnabled() == enabled);
            }
            return IsEnabled() == enabled;
        }

        // Keyboard Lock blocks all keys while it is locked, including ones typed by software. A hook
        // installed just now runs before its hook, and lets our own key presses (only those) skip it.
        static void PressShortcut(Func<bool> done)
        {
            IntPtr hook = IntPtr.Zero;
            Native.HookProc proc = (nCode, wParam, lParam) =>
            {
                if (nCode >= 0)
                {
                    var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (k.dwExtraInfo == Signature) return IntPtr.Zero;
                }
                return Native.CallNextHookEx(hook, nCode, wParam, lParam);
            };

            // A held Alt or Shift (from the lock shortcut) would turn this into a different shortcut.
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 1000 && (IsDown(VK_MENU) || IsDown(VK_SHIFT))) Thread.Sleep(10);

            Native.MSG msg;
            Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);   // create this thread's message queue
            hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(null), 0);
            try
            {
                var keys = new List<Native.INPUT>();
                foreach (int vk in new[] { 0xA4, 0xA5, 0xA0, 0xA1 })
                    if (IsDown(vk)) keys.Add(Key(vk, false));
                keys.Add(Key(VK_LCONTROL, true));
                keys.Add(Key(VK_LWIN, true));
                keys.Add(Key(VK_F24, true));
                keys.Add(Key(VK_F24, false));
                keys.Add(Key(VK_LWIN, false));
                keys.Add(Key(VK_LCONTROL, false));
                Native.SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));

                wait.Restart();
                while (wait.ElapsedMilliseconds < 900 && !done())
                {
                    while (Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 1)) { }   // keeps the hook answering
                    Thread.Sleep(5);
                }
            }
            finally
            {
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                GC.KeepAlive(proc);
            }
        }

        static bool IsDown(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        static Native.INPUT Key(int vk, bool down)
        {
            uint flags = down ? 0 : Native.KEYEVENTF_KEYUP;
            if (vk == VK_LWIN || vk == 0xA5) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            return new Native.INPUT
            {
                type = 1,
                ki = new Native.KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = Signature },
            };
        }
    }
}
