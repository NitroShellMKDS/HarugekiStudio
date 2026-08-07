using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Harugeki.Formats;
using System.Runtime.InteropServices;

namespace HarugekiStudio.Services;

/// <summary>
/// PNG in and out, using Avalonia's Skia-backed bitmaps so the format library
/// stays free of any image dependency.
///
/// The stored pixels are RGBA8 and Avalonia's universally supported layout is
/// Bgra8888, so every conversion goes through <see cref="RingTexture.SwapRedBlue"/>,
/// which serves both directions. Alpha is kept unpremultiplied: premultiplying and
/// dividing back is lossy, and it would silently corrupt any texture with real alpha.
/// </summary>
public static class ImageIO
{
    public static WriteableBitmap ToBitmap(RingTexture tex)
    {
        WriteableBitmap bmp = new(
            new PixelSize(tex.Width, tex.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        byte[] bgra = tex.ToBgra();
        using ILockedFramebuffer fb = bmp.Lock();
        int rowBytes = tex.Width * 4;
        for (int y = 0; y < tex.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                bgra, y * rowBytes, fb.Address + (y * fb.RowBytes), rowBytes);
        }

        return bmp;
    }

    public static void SavePng(RingTexture tex, string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using WriteableBitmap bmp = ToBitmap(tex);
        bmp.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// Loads a PNG as RGBA8 in the game's channel order. Throws if the image
    /// does not match the texture it is replacing, rather than silently
    /// resampling and producing a subtly wrong asset.
    /// </summary>
    public static byte[] LoadRgba(string path, int expectWidth, int expectHeight)
    {
        using Bitmap bitmap = new(path);
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
        if (w != expectWidth || h != expectHeight)
        {
            throw new InvalidDataException(
                $"image is {w}x{h} but the texture is {expectWidth}x{expectHeight}");
        }

        byte[] bgra = new byte[w * h * 4];
        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bgra.Length, w * 4);
        }
        finally
        {
            handle.Free();
        }
        return RingTexture.SwapRedBlue(bgra);
    }
}
