using System.Runtime.InteropServices;

namespace PoVoiceHud.Core.Screen;

/// <summary>
/// The monitor the person is working on, captured with GDI: the one holding the foreground window, or the last one when
/// that window is the HUD itself. The HUD hides itself from captures while the screen is watched, so it never reads its
/// own verdicts back.
/// </summary>
public static partial class ScreenCapture
{
    /// <summary>The longest side sent to the model: enough to read a form's labels, small enough to send every few seconds.</summary>
    public const int MaxSide = 1600;

    private const int MonitorDefaultToPrimary = 1;
    private const int Halftone = 4;
    private const int SrcCopy = 0x00CC0020;

    private static nint _monitor;

    /// <summary>The monitor scaled to fit <see cref="MaxSide"/>, or null when Windows will not hand it over (a locked desktop).</summary>
    public static ScreenShot? Capture()
    {
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var process);
        if (process != Environment.ProcessId || _monitor == 0) _monitor = MonitorFromWindow(foreground, MonitorDefaultToPrimary);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(_monitor, ref info)) return null;
        var (left, top) = (info.Left, info.Top);
        var (width, height) = (info.Right - info.Left, info.Bottom - info.Top);
        if (width <= 0 || height <= 0) return null;
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        var (w, h) = ((int)(width * scale), (int)(height * scale));

        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var previous = SelectObject(memory, bitmap);
        try
        {
            _ = SetStretchBltMode(memory, Halftone);
            if (!StretchBlt(memory, 0, 0, w, h, screen, left, top, width, height, SrcCopy)) return null;
            _ = SelectObject(memory, previous);   // GetDIBits wants the bitmap out of every DC
            previous = 0;
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var pixels = new byte[w * h * 4];
            return GetDIBits(memory, bitmap, 0, (uint)h, pixels, ref header, 0) == h ? new ScreenShot(w, h, pixels) : null;
        }
        finally
        {
            if (previous != 0) _ = SelectObject(memory, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int process);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, int sw, int sh, int rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}
