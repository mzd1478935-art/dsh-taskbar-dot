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
    // ~10 KB helper. No UI, no polling of DSH: it finds the DSH window by
    // title, optionally plays a short synthesized tone, paints one overlay
    // icon, then waits for the user to come back and clears it.

    private static string Title = "DeepSeek Harness";
    private static readonly int[] DotSizes = { 6, 7, 8, 9, 10 };
    private static int SizeLevel = 3;          // 1..5, 3 = 8px (the original dot)
    private static string SoundPreset = "";    // "" | tick | blip | chime | pop | soft
    private static string SoundOnly = "";
    private static byte[] KeepWav;   // PlaySound(SND_MEMORY) 播放期间缓冲必须存活

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
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr instance);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(byte[] data, IntPtr module, uint flags);
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

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
        int diameter = DotSizes[Math.Max(0, Math.Min(DotSizes.Length - 1, SizeLevel - 1))];
        using (var bmp = new Bitmap(24, 24))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(255, 77, 107, 254)))
                {
                    float offset = (24f - diameter) / 2f;
                    g.FillEllipse(brush, offset, offset, diameter, diameter);
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

    /* ---- short synthesized notification tones (no audio files shipped) ---- */

    private sealed class Tone { public double Freq; public int Ms; public double Gain; public double Decay; public int AtMs; }

    private static Tone[] Preset(string name)
    {
        switch (name)
        {
            case "tick": return new[] { new Tone { Freq = 1600, Ms = 45, Gain = 0.35, Decay = 6, AtMs = 0 } };
            case "blip": return new[] { new Tone { Freq = 1000, Ms = 60, Gain = 0.5, Decay = 4, AtMs = 0 } };
            case "pop": return new[] { new Tone { Freq = 720, Ms = 55, Gain = 0.55, Decay = 8, AtMs = 0 } };
            case "soft": return new[] { new Tone { Freq = 620, Ms = 130, Gain = 0.3, Decay = 2.4, AtMs = 0 } };
            case "chime":
            default:
                return new[]
                {
                    new Tone { Freq = 880, Ms = 90, Gain = 0.38, Decay = 3, AtMs = 0 },
                    new Tone { Freq = 1320, Ms = 110, Gain = 0.32, Decay = 3, AtMs = 70 }
                };
        }
    }

    private static byte[] BuildWav(string preset)
    {
        const int rate = 22050;
        Tone[] tones = Preset(preset);
        int totalMs = 0;
        foreach (var t in tones) totalMs = Math.Max(totalMs, t.AtMs + t.Ms);
        int samples = (int)(rate * (totalMs + 20) / 1000.0);
        var data = new short[samples];
        foreach (var t in tones)
        {
            int start = (int)(rate * t.AtMs / 1000.0);
            int count = (int)(rate * t.Ms / 1000.0);
            for (int i = 0; i < count && start + i < samples; i++)
            {
                double x = (double)i / count;
                double env = Math.Pow(1 - x, t.Decay) * t.Gain;
                double v = Math.Sin(2 * Math.PI * t.Freq * i / rate) * env;
                data[start + i] += (short)(v * 32767 * 0.55);
            }
        }
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            int byteCount = samples * 2;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + byteCount);
            bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(rate);
            bw.Write(rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(byteCount);
            foreach (short s in data) bw.Write(s);
            bw.Flush();
            return ms.ToArray();
        }
    }

    private static void Play(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset) || preset == "off") return;
        try
        {
            byte[] wav = BuildWav(preset);
            KeepWav = wav;
            PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception e) { Log("sound failed: " + e.Message); }
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
            else if (arg.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
            {
                int level;
                if (int.TryParse(arg.Substring(5), out level)) SizeLevel = Math.Max(1, Math.Min(5, level));
            }
            else if (arg.StartsWith("soundonly=", StringComparison.OrdinalIgnoreCase)) SoundOnly = arg.Substring(10);
            else if (arg.StartsWith("sound=", StringComparison.OrdinalIgnoreCase)) SoundPreset = arg.Substring(6);
        }

        if (!string.IsNullOrEmpty(SoundOnly))
        {
            Play(SoundOnly);
            Thread.Sleep(350);
            Log("sound-only " + SoundOnly);
            return 0;
        }

        if (!string.IsNullOrEmpty(SoundPreset)) Play(SoundPreset);

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

        if (!hold && GetForegroundWindow() == hwnd)
        {
            if (!string.IsNullOrEmpty(SoundPreset) && SoundPreset != "off") Thread.Sleep(260); // 让异步提示音播完再退出
            Log("skip (foreground at completion) size=" + SizeLevel + " sound=" + SoundPreset);
            return 0;
        }

        bool ok = false;
        for (int attempt = 0; attempt < 3 && !ok; attempt++)
        {
            ok = Apply(taskbar, hwnd, true, out hr);
            if (!ok) Thread.Sleep(300);
        }
        Log("show hwnd=" + hwnd + " ok=" + ok + " hr=" + hr + " size=" + SizeLevel + " sound=" + SoundPreset);
        if (!ok) return 1;

        DateTime deadline = DateTime.UtcNow.AddMinutes(hold ? 60 : 30);
        while (DateTime.UtcNow < deadline)
        {
            if (hold) { Thread.Sleep(1000); continue; }
            Thread.Sleep(400);
            RECT probe;
            if (!GetWindowRect(hwnd, out probe)) break;
            if (GetForegroundWindow() == hwnd)
            {
                Thread.Sleep(1200);
                Apply(taskbar, hwnd, false, out hr);
                Log("auto-clear (focused) hr=" + hr);
                return 0;
            }
        }
        // 超时（30 分钟没人回来）也要把点撤掉，不能把 overlay 永久留在任务栏上。
        Apply(taskbar, hwnd, false, out hr);
        Log("auto-clear (timeout) hr=" + hr);
        return 0;
    }
}
