using System.Runtime.InteropServices;
using DesktopIconsToggle.Native;

namespace DesktopIconsToggle;

/// <summary>
/// Draws the two tray icon states the notification area shows: the exact same solid toggle
/// switch in both states — filled blue with its white knob on the right while the desktop
/// icons are visible, and filled neutral slate with the same white knob slid to the left while
/// they are hidden — so the tray icon reads as one switch toggling on and off at a glance.
/// Built the way <see cref="MenuIcons"/> builds its bitmaps — a 32-bit top-down
/// <c>CreateDIBSection</c> written directly as premultiplied BGRA — then turned into an
/// <c>HICON</c> via <c>CreateIconIndirect</c>. Sized from <c>SM_CXSMICON</c> so the art tracks
/// DPI instead of being fixed to 16 px, and coloured from <see cref="Theme.IsSystemDark"/> —
/// the taskbar/notification-area theme, which is independent of the app theme
/// <see cref="MenuIcons"/> uses for the popup menu.
/// </summary>
internal sealed class TrayIconArt : IDisposable
{
    // Inactive track fills (COLORREF 0x00BBGGRR) chosen so both the pill against the taskbar
    // and the white knob against the pill stay clearly legible in dark and light modes.
    private const uint DarkTaskbarOffTrackColor  = 0x0060514Au; // RGB(74, 81, 96)  (#4A5160)
    private const uint LightTaskbarOffTrackColor = 0x0090827Bu; // RGB(123, 130, 144) (#7B8290)

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

    /// <summary>Filled blue toggle pill with the white knob on the right — desktop icons are visible.</summary>
    public IntPtr Visible => _visible;

    /// <summary>Same solid toggle pill in neutral slate with the white knob on the left — desktop icons are hidden.</summary>
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

        uint activeTrackColor = IconPalette.EnsureContrast(TileColor, _theme.IsSystemDark);
        uint inactiveTrackColor = _theme.IsSystemDark ? DarkTaskbarOffTrackColor : LightTaskbarOffTrackColor;

        IntPtr newVisible = Render(size, activeTrackColor, knobRight: true);
        IntPtr newHidden = Render(size, inactiveTrackColor, knobRight: false);

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
    private static IntPtr Render(int size, uint trackColorRef, bool knobRight)
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

                Paint(bits, size, trackColorRef, knobRight);

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
    /// Writes an anti-aliased horizontal toggle pill and white circular knob directly into the
    /// DIB's premultiplied BGRA buffer using signed-distance fields on a 16-unit reference
    /// grid scaled to <paramref name="size"/>. Both states share the exact same pill and knob
    /// geometry; only <paramref name="trackColorRef"/> and <paramref name="knobRight"/> change.
    /// </summary>
    private static unsafe void Paint(IntPtr bits, int size, uint trackColorRef, bool knobRight)
    {
        byte trackR = (byte)(trackColorRef & 0xFF);
        byte trackG = (byte)((trackColorRef >> 8) & 0xFF);
        byte trackB = (byte)((trackColorRef >> 16) & 0xFF);

        double s = size / 16.0;
        double cy = 8.0 * s;
        double ax = 5.25 * s;
        double bx = 10.75 * s;
        double rTrack = 4.25 * s;
        double rKnob = 2.65 * s;
        double knobX = knobRight ? bx : ax;

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

                double alphaTrack = Math.Clamp(0.5 - (distTrack - rTrack), 0.0, 1.0);
                if (alphaTrack <= 0.0)
                {
                    pixel[0] = 0;
                    pixel[1] = 0;
                    pixel[2] = 0;
                    pixel[3] = 0;
                    continue;
                }

                double dxKnob = px - knobX;
                double distKnob = Math.Sqrt(dxKnob * dxKnob + dy * dy);
                double alphaKnob = Math.Clamp(0.5 - (distKnob - rKnob), 0.0, 1.0);

                double r = trackR + (255.0 - trackR) * alphaKnob;
                double g = trackG + (255.0 - trackG) * alphaKnob;
                double b = trackB + (255.0 - trackB) * alphaKnob;

                byte a = (byte)Math.Round(alphaTrack * 255.0);
                pixel[0] = (byte)Math.Round(b * a / 255.0);
                pixel[1] = (byte)Math.Round(g * a / 255.0);
                pixel[2] = (byte)Math.Round(r * a / 255.0);
                pixel[3] = a;
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
