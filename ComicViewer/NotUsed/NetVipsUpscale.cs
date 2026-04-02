// NuGet packages required:
//   NetVips                      (core binding)
//   NetVips.Native.win-x64       (or the package matching your target platform)
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;

/// <summary>
/// Upscales a WPF BitmapSource using libvips' Lanczos3 resampling kernel via NetVips.
///
/// Why NetVips?
///   - libvips processes images as streaming pipelines, so it never needs the
///     full image in memory at once.
///   - It uses SIMD + threading internally, making it typically 4× faster than
///     ImageSharp and ~26× faster than Magick.NET on equivalent workloads.
///   - Correct alpha handling: premultiply before resize, unpremultiply after,
///     which prevents colour fringing around transparent edges.
/// </summary>
public static class NetVipsLanczosUpscaler
{
    /// <summary>
    /// Upscales <paramref name="source"/> to the given dimensions using Lanczos3.
    /// </summary>
    /// <param name="source">Input bitmap (any WPF PixelFormat).</param>
    /// <param name="destWidth">Desired output width in pixels.</param>
    /// <param name="destHeight">Desired output height in pixels.</param>
    /// <returns>A frozen <see cref="BitmapSource"/> at the requested size.</returns>
    public static BitmapSource Upscale(BitmapSource source, int destWidth, int destHeight)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destWidth <= 0) throw new ArgumentOutOfRangeException(nameof(destWidth));
        if (destHeight <= 0) throw new ArgumentOutOfRangeException(nameof(destHeight));

        // ── 1. Normalise to Bgra32 ────────────────────────────────────────────
        BitmapSource bgra = ConvertToBgra32(source);
        int srcW = bgra.PixelWidth;
        int srcH = bgra.PixelHeight;
        byte[] imageData = GetPixels(bgra, srcW, srcH);

        // ── 2. Wrap raw bytes in a NetVips image ──────────────────────────────
        using var vipsImage = Image.NewFromMemory(
            imageData,
            srcW, srcH,
            bands: 4,
            format: Enums.BandFormat.Uchar);

        // ── 3. Premultiply alpha ──────────────────────────────────────────────
        using var premul = vipsImage.Premultiply();

        // ── 4. Resize — Lanczos3 for upscale, Mitchell for downscale ─────────
        double hScale = (double)destWidth / srcW;
        double vScale = (double)destHeight / srcH;
        bool isDownscale = vScale < 1.0;

        using var resized = premul.Resize(
            hScale,
            vscale: vScale,
            kernel: isDownscale ? Enums.Kernel.Mitchell : Enums.Kernel.Lanczos3);

        // ── 5. Unpremultiply and cast back to uchar ───────────────────────────
        using var unpremul = resized.Unpremultiply();
        using var output = unpremul.Cast(Enums.BandFormat.Uchar);

        // ── 6. Build a frozen WPF BitmapSource ───────────────────────────────
        byte[] dstBytes = output.WriteToMemory();

        var result = BitmapSource.Create(
            destWidth, destHeight,
            96, 96,
            PixelFormats.Bgra32, null,
            dstBytes,
            destWidth * 4);

        result.Freeze();
        return result;
    }

    private static BitmapSource ConvertToBgra32(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32) return src;
        return new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
    }

    private static byte[] GetPixels(BitmapSource src, int w, int h)
    {
        byte[] px = new byte[h * w * 4];
        src.CopyPixels(px, w * 4, 0);
        return px;
    }
}