using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ZXing;
using ZXing.Common;

namespace Vouch.App.Platform;

/// <summary>
/// Grabs a QR code either from an image already on the clipboard or from a full-screen capture, and
/// decodes it. Capture is Windows-only (GDI); decoding (ZXing) is portable. Split so the capture runs
/// on the UI thread (clipboard access needs it) and the heavier decode can run on a background thread.
/// </summary>
public static class QrScanner
{
    public readonly record struct Capture(byte[] Bgra, int Width, int Height);

    /// <summary>Full-screen (all monitors) capture. Null if unsupported or it failed.</summary>
    public static Capture? CaptureScreen()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return CaptureScreenWin(); } catch { return null; }
    }

    /// <summary>The bitmap currently on the clipboard, if any. Null when the clipboard holds no image.</summary>
    public static Capture? CaptureClipboard()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return CaptureClipboardWin(); } catch { return null; }
    }

    /// <summary>Decodes a QR code from a BGRA pixel buffer. Null if no QR is found.</summary>
    public static string? Decode(Capture cap)
    {
        try
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                    TryHarder = true,
                },
            };
            var source = new RGBLuminanceSource(cap.Bgra, cap.Width, cap.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            return reader.Decode(source)?.Text;
        }
        catch { return null; }
    }

    // ---- Windows capture (GDI) ----

    [SupportedOSPlatform("windows")]
    private static Capture? CaptureScreenWin()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) return null;

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)) return null;
            var bits = ReadBits(screen, bmp, w, h);
            return bits is null ? null : new Capture(bits, w, h);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Capture? CaptureClipboardWin()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr hbmp = GetClipboardData(CF_BITMAP);
            if (hbmp == IntPtr.Zero) return null;
            var bm = new BITMAP();
            if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
                return null;

            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                var bits = ReadBits(dc, hbmp, bm.bmWidth, bm.bmHeight);
                return bits is null ? null : new Capture(bits, bm.bmWidth, bm.bmHeight);
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        finally { CloseClipboard(); }
    }

    // Pulls a device bitmap's pixels as top-down 32bpp BGRA via GetDIBits.
    [SupportedOSPlatform("windows")]
    private static byte[]? ReadBits(IntPtr dc, IntPtr hbmp, int w, int h)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative = top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        var buffer = new byte[w * h * 4];
        int scanned = GetDIBits(dc, hbmp, 0, (uint)h, buffer, ref bmi, DIB_RGB_COLORS);
        return scanned == 0 ? null : buffer;
    }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int SRCCOPY = 0x00CC0020, BI_RGB = 0, DIB_RGB_COLORS = 0;
    private const uint CF_BITMAP = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
}
