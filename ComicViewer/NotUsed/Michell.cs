using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Provides high-quality image downscaling using the Mitchell-Netravali cubic filter.
/// Supports BGRA32, BGR32, and Gray8 pixel formats.
/// </summary>
/// 
namespace ComicViewer.NotUsed;

public static class MitchellDownscaler
{
    // Standard Mitchell-Netravali parameters (B=1/3, C=1/3).
    // Adjust for different looks: (0, 0.5) = Catmull-Rom, (1, 0) = cubic B-spline.
    private const double B = 1.0 / 3.0;
    private const double C = 1.0 / 3.0;

    /// <summary>
    /// Downscales a <see cref="BitmapSource"/> to the specified dimensions using the
    /// Mitchell-Netravali cubic filter.
    /// </summary>
    /// <param name="source">The source bitmap. Must be BGRA32, BGR32, or Gray8.</param>
    /// <param name="destWidth">Target width in pixels.</param>
    /// <param name="destHeight">Target height in pixels.</param>
    /// <returns>
    /// A <see cref="WriteableBitmap"/> in the same pixel format as the source,
    /// resampled to the requested size.
    /// </returns>
    public static WriteableBitmap Downscale(BitmapSource source, int destWidth, int destHeight)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destWidth <= 0) throw new ArgumentOutOfRangeException(nameof(destWidth));
        if (destHeight <= 0) throw new ArgumentOutOfRangeException(nameof(destHeight));

        // Normalise to a known pixel format so we can work with fixed channel counts.
        PixelFormat fmt = source.Format;
        int channels;

        if (fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32)
        {
            channels = 4;
            fmt = PixelFormats.Bgra32;
        }
        else if (fmt == PixelFormats.Bgr32 || fmt == PixelFormats.Bgr24)
        {
            channels = 4; // convert to Bgr32 (4-byte aligned) for simplicity
            fmt = PixelFormats.Bgr32;
        }
        else if (fmt == PixelFormats.Gray8 || fmt == PixelFormats.Indexed8)
        {
            channels = 1;
            fmt = PixelFormats.Gray8;
        }
        else
        {
            // Fall back to Bgra32 for anything else (HDR, palette, etc.)
            channels = 4;
            fmt = PixelFormats.Bgra32;
        }

        // Convert source to the normalised format if needed.
        BitmapSource src = source.Format == fmt
            ? source
            : new FormatConvertedBitmap(source, fmt, null, 0);

        int srcW = src.PixelWidth;
        int srcH = src.PixelHeight;
        int srcStride = srcW * channels;

        byte[] srcPixels = new byte[srcH * srcStride];
        src.CopyPixels(srcPixels, srcStride, 0);

        // --- Horizontal pass: srcW × srcH  →  destWidth × srcH ---
        byte[] hPass = ResizeHorizontal(srcPixels, srcW, srcH, destWidth, channels);

        // --- Vertical pass:   destWidth × srcH  →  destWidth × destHeight ---
        byte[] vPass = ResizeVertical(hPass, destWidth, srcH, destHeight, channels);

        // Write result into a WriteableBitmap.
        var result = new WriteableBitmap(destWidth, destHeight, 96, 96, fmt, null);
        int dstStride = destWidth * channels;
        result.WritePixels(new System.Windows.Int32Rect(0, 0, destWidth, destHeight),
                           vPass, dstStride, 0);
        return result;
    }

    // -------------------------------------------------------------------------
    //  Separable passes
    // -------------------------------------------------------------------------

    private static unsafe byte[] ResizeHorizontal(
        byte[] src, int srcW, int srcH, int dstW, int channels)
    {
        byte[] dst = new byte[dstW * srcH * channels];
        double scale = (double)srcW / dstW;         // > 1 when downscaling
        double support = scale * 2.0;                 // Mitchell kernel radius in src space

        // Pre-compute per-output-column: source indices and weights.
        (int start, float[] weights)[] cols = BuildContributions(srcW, dstW, scale, support);

        int srcStride = srcW * channels;
        int dstStride = dstW * channels;

        System.Threading.Tasks.Parallel.For(0, srcH, y =>
        {
            int srcRowOff = y * srcStride;
            int dstRowOff = y * dstStride;

            for (int x = 0; x < dstW; x++)
            {
                var (start, weights) = cols[x];
                int dstOff = dstRowOff + x * channels;

                for (int c = 0; c < channels; c++)
                {
                    double acc = 0.0;
                    for (int k = 0; k < weights.Length; k++)
                        acc += weights[k] * src[srcRowOff + (start + k) * channels + c];
                    dst[dstOff + c] = ClampToByte(acc);
                }
            }
        });

        return dst;
    }

    private static byte[] ResizeVertical(
        byte[] src, int srcW, int srcH, int dstH, int channels)
    {
        byte[] dst = new byte[srcW * dstH * channels];
        double scale = (double)srcH / dstH;
        double support = scale * 2.0;

        (int start, float[] weights)[] rows = BuildContributions(srcH, dstH, scale, support);

        int stride = srcW * channels;

        System.Threading.Tasks.Parallel.For(0, dstH, y =>
        {
            var (start, weights) = rows[y];
            int dstRowOff = y * stride;

            for (int x = 0; x < srcW; x++)
            {
                int dstOff = dstRowOff + x * channels;

                for (int c = 0; c < channels; c++)
                {
                    double acc = 0.0;
                    for (int k = 0; k < weights.Length; k++)
                        acc += weights[k] * src[(start + k) * stride + x * channels + c];
                    dst[dstOff + c] = ClampToByte(acc);
                }
            }
        });

        return dst;
    }

    // -------------------------------------------------------------------------
    //  Contribution table
    // -------------------------------------------------------------------------

    /// <summary>
    /// For each destination pixel, computes the range of source pixels that
    /// contribute and their normalised Mitchell weights.
    /// </summary>
    private static (int start, float[] weights)[] BuildContributions(
        int srcLen, int dstLen, double scale, double support)
    {
        var table = new (int start, float[] weights)[dstLen];

        for (int i = 0; i < dstLen; i++)
        {
            // Centre of the destination pixel mapped back to source space.
            double centre = (i + 0.5) * scale - 0.5;

            int left = (int)Math.Floor(centre - support + 0.5);
            int right = (int)Math.Floor(centre + support + 0.5);

            left = Math.Max(left, 0);
            right = Math.Min(right, srcLen - 1);

            int count = right - left + 1;
            float[] w = new float[count];
            double sum = 0.0;

            for (int k = 0; k < count; k++)
            {
                double dist = (left + k - centre) / scale; // normalised distance
                double weight = Mitchell(dist);
                w[k] = (float)weight;
                sum += weight;
            }

            // Normalise so weights sum to 1 (avoids brightness drift).
            if (Math.Abs(sum) > 1e-10)
                for (int k = 0; k < count; k++) w[k] = (float)(w[k] / sum);

            table[i] = (left, w);
        }

        return table;
    }

    // -------------------------------------------------------------------------
    //  Mitchell-Netravali kernel
    // -------------------------------------------------------------------------

    /// <summary>Evaluates the Mitchell-Netravali cubic kernel at distance <paramref name="x"/>.</summary>
    private static double Mitchell(double x)
    {
        x = Math.Abs(x);

        if (x < 1.0)
            return ((12 - 9 * B - 6 * C) * x * x * x
                  + (-18 + 12 * B + 6 * C) * x * x
                  + (6 - 2 * B)) / 6.0;

        if (x < 2.0)
            return ((-B - 6 * C) * x * x * x
                  + (6 * B + 30 * C) * x * x
                  + (-12 * B - 48 * C) * x
                  + (8 * B + 24 * C)) / 6.0;

        return 0.0;
    }

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    private static byte ClampToByte(double v)
        => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)(v + 0.5);
}