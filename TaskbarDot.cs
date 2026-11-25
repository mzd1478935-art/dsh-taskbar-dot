using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class TaskbarDot
{
    // 9.5 KB helper. No UI, no polling of DSH, no UI Automation: it finds the
    // DSH window by title, paints one overlay icon, then waits for the user to
    // come back and clears it. It only lives while the dot is up.

    private static string Title = "DeepSeek Harness";

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr insertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr insertBefore, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig]
        int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr instance);
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private static string LogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DSH", "taskbar-dot", "taskbar-dot.log");
    }

    private static void Log(string message)
    {
        try
        {
            string path = LogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
            File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static IntPtr FindDshWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, p) =>
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            if (sb.ToString().IndexOf(Title, StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r;
            GetWindowRect(h, out r);
            long area = Math.Max(0, r.R - r.L) * (long)Math.Max(0, r.B - r.T);
            if (area >= bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static IntPtr DotIcon()
    {
        // 24x24 canvas with an 8px disc: the shell draws this as the overlay
        // badge, so the visible dot ends up small and sits on the icon corner.
        using (var bmp = new Bitmap(24, 24))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(255, 77, 107, 254)))
                {
                    g.FillEllipse(brush, 8f, 8f, 8f, 8f);
                }
            }
            return bmp.GetHicon();
        }
    }

    private static ITaskbarList3 CreateTaskbar()
    {
        Guid clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
        Guid iid = new Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");
        IntPtr pointer;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out pointer);
        if (hr != 0) throw new Exception("CoCreateInstance 0x" + hr.ToString("X8"));
        var taskbar = (ITaskbarList3)Marshal.GetObjectForIUnknown(pointer);
        Marshal.Release(pointer);
        taskbar.HrInit();
        return taskbar;
    }

    private static bool Apply(ITaskbarList3 taskbar, IntPtr hwnd, bool on, out int hr)
    {
        IntPtr icon = on ? DotIcon() : IntPtr.Zero;
        hr = taskbar.SetOverlayIcon(hwnd, icon, on ? "Task completed" : "");
        return hr == 0;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool clear = false;
        bool hold = false;
        foreach (string arg in args)
        {
            if (string.Equals(arg, "clear", StringComparison.OrdinalIgnoreCase)) clear = true;
            else if (string.Equals(arg, "hold", StringComparison.OrdinalIgnoreCase)) hold = true;
            else if (arg.StartsWith("title=", StringComparison.OrdinalIgnoreCase)) Title = arg.Substring(6);
        }

        IntPtr hwnd = FindDshWindow();
        if (hwnd == IntPtr.Zero) { Log("no DSH window"); return 2; }

        ITaskbarList3 taskbar;
        try { taskbar = CreateTaskbar(); } catch (Exception e) { Log(e.Message); return 3; }

        int hr = 0;
        if (clear)
        {
            Apply(taskbar, hwnd, false, out hr);
            Log("clear hwnd=" + hwnd + " hr=" + hr);
            return hr == 0 ? 0 : 1;
        }

        // Already looking at DSH? The answer is on screen; a dot adds nothing.
        if (!hold && GetForegroundWindow() == hwnd)
        {
            Log("skip (foreground at completion)");
            return 0;
        }

        bool ok = false;
        for (int attempt = 0; attempt < 3 && !ok; attempt++)
        {
            ok = Apply(taskbar, hwnd, true, out hr);
            if (!ok) Thread.Sleep(300);
        }
        Log("show hwnd=" + hwnd + " ok=" + ok + " hr=" + hr);
        if (!ok) return 1;

        // Stay alive only while the dot is up: the moment DSH comes to the
        // foreground the turn has been seen, so clear and exit.
        DateTime deadline = DateTime.UtcNow.AddMinutes(hold ? 60 : 30);
        while (DateTime.UtcNow < deadline)
        {
            if (hold) { Thread.Sleep(1000); continue; }
            Thread.Sleep(400);
            RECT probe;
            if (!GetWindowRect(hwnd, out probe)) break;
            if (GetForegroundWindow() == hwnd)
            {
                Thread.Sleep(1200); // let the window paint the answer first
                Apply(taskbar, hwnd, false, out hr);
                Log("auto-clear (focused) hr=" + hr);
                return 0;
            }
        }
        return 0;
    }
}
