using System.Runtime.InteropServices;
using DesktopIconsToggle.Native;

namespace DesktopIconsToggle;

/// <summary>
/// Draws the two tray icon states the notification area shows: a filled blue toggle switch
/// with its white knob on the right while the desktop icons are visible, and an outlined
/// toggle switch with its knob on the left while they are hidden — so the tray icon itself
/// answers "are my icons on" at a glance. Built the way <see cref="MenuIcons"/> builds its
/// bitmaps — a 32-bit top-down <c>CreateDIBSection</c> written directly as premultiplied
/// BGRA — then turned into an <c>HICON</c> via <c>CreateIconIndirect</c>. Sized from
/// <c>SM_CXSMICON</c> so the art tracks DPI instead of being fixed to 16 px, and coloured
/// from <see cref="Theme.IsSystemDark"/> — the taskbar/notification-area theme, which is
/// independent of the app theme <see cref="MenuIcons"/> uses for the popup menu.
/// </summary>
internal sealed class TrayIconArt : IDisposable
{
    // COLORREF-style values (0x00BBGGRR), but grayscale so channel order is moot.
    private const uint LightTaskbarColor = 0x00202020u; // near-black, for a light taskbar
    private const uint DarkTaskbarColor  = 0x00EBEBEBu; // near-white, for a dark taskbar

    // The active toggle blue, RGB(56, 138, 240) (#388AF0, shared with FolderTags),
    // written as a COLORREF (0x00BBGGRR): blue=0xF0, green=0x8A, red=0x38. Run through
    // IconPalette.EnsureContrast before painting so it stays visible on light/dark taskbars.
    private const uint TileColor = 0x00F08A38u;

    private readonly Theme _theme;
    private IntPtr _visible;
    private IntPtr _hidden;

    public TrayIconArt(Theme theme)
    {
        _theme = theme;
        Refresh();
    }

    /// <summary>Filled blue toggle pill with the knob on the right — desktop icons are visible.</summary>
    public IntPtr Visible => _visible;

    /// <summary>Outlined toggle pill with the knob on the left — desktop icons are hidden.</summary>
    public IntPtr Hidden => _hidden;

    /// <summary>
    /// Redraws both icons for the current system theme and icon metrics. Builds the two
    /// replacement icons first, then destroys whatever handles were previously held, and
    /// only then publishes the new ones — so a reader can never observe a destroyed handle
    /// through <see cref="Visible"/>/<see cref="Hidden"/>, and nothing leaks even if this
    /// runs back-to-back.
    /// </summary>
    public void Refresh()
    {
        int size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        if (size <= 0)
        {
            size = 16;
        }

        uint frameColor = _theme.IsSystemDark ? DarkTaskbarColor : LightTaskbarColor;
        uint tileColor = IconPalette.EnsureContrast(TileColor, _theme.IsSystemDark);

        IntPtr newVisible = Render(size, frameColor, tileColor, isVisible: true);
        IntPtr newHidden = Render(size, frameColor, tileColor, isVisible: false);

        DestroyIfPresent(_visible);
        DestroyIfPresent(_hidden);

        _visible = newVisible;
        _hidden = newHidden;
    }

    private static void DestroyIfPresent(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(icon);
        }
    }

    /// <summary>
    /// Builds one HICON: a 32-bit top-down DIB painted directly as premultiplied BGRA
    /// (see <see cref="Paint"/>), paired with an all-zero monochrome mask — required by
    /// <c>ICONINFO</c>, but its content is ignored for a 32-bit colour bitmap that already
    /// carries real alpha. <c>CreateIconIndirect</c> copies both bitmaps into the icon it
    /// returns, so both are deleted here immediately afterwards on every path, success or
    /// failure: keeping them would leak, and the leak would repeat on every
    /// <see cref="Refresh"/>.
    /// </summary>
    private static IntPtr Render(int size, uint frameColorRef, uint tileColorRef, bool isVisible)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                var header = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size, // negative: top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB,
                };

                IntPtr colorBitmap = Win32.CreateDIBSection(screenDc, ref header, Win32.DIB_RGB_COLORS,
                                                            out IntPtr bits, IntPtr.Zero, 0);
                if (colorBitmap == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                if (bits == IntPtr.Zero)
                {
                    Win32.DeleteObject(colorBitmap);
                    return IntPtr.Zero;
                }

                // As in MenuIcons.Render: the DIB must be deselected before its bits are
                // touched directly or the bitmap is deleted, while selected the DC still
                // owns it.
                IntPtr previousBitmap = Win32.SelectObject(memDc, colorBitmap);
                Win32.SelectObject(memDc, previousBitmap);

                Paint(bits, size, frameColorRef, tileColorRef, isVisible);

                IntPtr maskBitmap = Win32.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
                if (maskBitmap == IntPtr.Zero)
                {
                    Win32.DeleteObject(colorBitmap);
                    return IntPtr.Zero;
                }

                var iconInfo = new Win32.ICONINFO
                {
                    fIcon = true,
                    xHotspot = 0,
                    yHotspot = 0,
                    hbmMask = maskBitmap,
                    hbmColor = colorBitmap,
                };

                IntPtr icon = Win32.CreateIconIndirect(ref iconInfo);

                Win32.DeleteObject(colorBitmap);
                Win32.DeleteObject(maskBitmap);

                return icon;
            }
            finally
            {
                Win32.DeleteDC(memDc);
            }
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Writes an anti-aliased horizontal toggle pill and circular knob directly into the
    /// DIB's premultiplied BGRA buffer using signed-distance fields on a 16-unit reference
    /// grid scaled to <paramref name="size"/>. When <paramref name="isVisible"/> is true,
    /// the pill is filled with <paramref name="tileColorRef"/> and a white knob sits on the
    /// right; when false, the pill is stroked with <paramref name="frameColorRef"/> and the
    /// knob sits on the left.
    /// </summary>
    private static unsafe void Paint(IntPtr bits, int size, uint frameColorRef, uint tileColorRef, bool isVisible)
    {
        byte frameR = (byte)(frameColorRef & 0xFF);
        byte frameG = (byte)((frameColorRef >> 8) & 0xFF);
        byte frameB = (byte)((frameColorRef >> 16) & 0xFF);

        byte tileR = (byte)(tileColorRef & 0xFF);
        byte tileG = (byte)((tileColorRef >> 8) & 0xFF);
        byte tileB = (byte)((tileColorRef >> 16) & 0xFF);

        double s = size / 16.0;
        double cy = 8.0 * s;
        double ax = 5.25 * s;
        double bx = 10.75 * s;
        double rTrackOuter = 4.25 * s;
        double stroke = Math.Max(1.35, 1.45 * s);
        double rTrackInner = rTrackOuter - stroke;
        double rKnobOn = 2.65 * s;
        double rKnobOff = 1.85 * s;

        byte* pixels = (byte*)bits;
        for (int y = 0; y < size; y++)
        {
            double py = y + 0.5;
            double dy = py - cy;

            for (int x = 0; x < size; x++)
            {
                double px = x + 0.5;
                double clampedX = Math.Clamp(px, ax, bx);
                double dxTrack = px - clampedX;
                double distTrack = Math.Sqrt(dxTrack * dxTrack + dy * dy);

                byte* pixel = pixels + (y * size + x) * 4;

                if (isVisible)
                {
                    double alphaTrack = Math.Clamp(0.5 - (distTrack - rTrackOuter), 0.0, 1.0);
                    if (alphaTrack <= 0.0)
                    {
                        pixel[0] = 0;
                        pixel[1] = 0;
                        pixel[2] = 0;
                        pixel[3] = 0;
                        continue;
                    }

                    double dxKnob = px - bx;
                    double distKnob = Math.Sqrt(dxKnob * dxKnob + dy * dy);
                    double alphaKnob = Math.Clamp(0.5 - (distKnob - rKnobOn), 0.0, 1.0);

                    double r = tileR + (255.0 - tileR) * alphaKnob;
                    double g = tileG + (255.0 - tileG) * alphaKnob;
                    double b = tileB + (255.0 - tileB) * alphaKnob;

                    byte a = (byte)Math.Round(alphaTrack * 255.0);
                    pixel[0] = (byte)Math.Round(b * a / 255.0);
                    pixel[1] = (byte)Math.Round(g * a / 255.0);
                    pixel[2] = (byte)Math.Round(r * a / 255.0);
                    pixel[3] = a;
                }
                else
                {
                    double sdfOuter = distTrack - rTrackOuter;
                    double sdfInner = rTrackInner - distTrack;
                    double alphaRing = Math.Clamp(0.5 - Math.Max(sdfOuter, sdfInner), 0.0, 1.0);

                    double dxKnob = px - ax;
                    double distKnob = Math.Sqrt(dxKnob * dxKnob + dy * dy);
                    double alphaKnob = Math.Clamp(0.5 - (distKnob - rKnobOff), 0.0, 1.0);

                    double alpha = Math.Max(alphaRing, alphaKnob);
                    if (alpha <= 0.0)
                    {
                        pixel[0] = 0;
                        pixel[1] = 0;
                        pixel[2] = 0;
                        pixel[3] = 0;
                        continue;
                    }

                    byte a = (byte)Math.Round(alpha * 255.0);
                    pixel[0] = (byte)((frameB * a + 127) / 255);
                    pixel[1] = (byte)((frameG * a + 127) / 255);
                    pixel[2] = (byte)((frameR * a + 127) / 255);
                    pixel[3] = a;
                }
            }
        }
    }

    public void Dispose()
    {
        DestroyIfPresent(_visible);
        DestroyIfPresent(_hidden);
        _visible = IntPtr.Zero;
        _hidden = IntPtr.Zero;
    }
}
