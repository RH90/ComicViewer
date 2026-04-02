using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Upscales a WPF BitmapSource using a Catmull-Rom cubic spline resampling filter
/// (pure managed C#, parallelised with Parallel.For).
///
/// Catmull-Rom is the standard "cubic spline" resampler used in image processing.
/// It is equivalent to the Mitchell-Netravali filter with B=0, C=0.5.
/// It uses 4 taps (radius 2), making it faster than Lanczos3 (6 taps, radius 3)
/// while producing similarly sharp, high-quality output.
///
/// Kernel:
///   |x| in [0, 1):  1.5|x|³ - 2.5|x|² + 1
///   |x| in [1, 2):  -0.5|x|³ + 2.5|x|² - 4|x| + 2
///   |x| >= 2:       0
/// </summary>
///
namespace ComicViewer.NotUsed;

public static class CubicSplineUpscaler
{
    // ── Mitchell-Netravali cubic kernel (B=0, C=0.5 → Catmull-Rom) ───────────

    /// <summary>
    /// Change B and C to select different cubic variants:
    ///   B=0,   C=0.5  → Catmull-Rom      (sharp, recommended)
    ///   B=1/3, C=1/3  → Mitchell          (balanced blur/ring tradeoff)
    ///   B=1,   C=0    → Cubic B-Spline    (very smooth, no ringing)
    ///   B=0,   C=0.75 → Sharper cubic     (more ringing)
    /// </summary>
    private const double B = 0.0;
    private const double C = 0.5;

    /// <summary>Support radius — always 2 for Mitchell-Netravali.</summary>
    private const int Radius = 2;

    private static double CubicKernel(double x)
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

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Upscales <paramref name="source"/> to the specified pixel dimensions using
    /// a Catmull-Rom cubic spline resampling filter.
    /// </summary>
    /// <param name="source">Input bitmap (any PixelFormat).</param>
    /// <param name="destWidth">Desired output width in pixels.</param>
    /// <param name="destHeight">Desired output height in pixels.</param>
    /// <returns>A frozen <see cref="BitmapSource"/> at the requested size.</returns>
    public static BitmapSource Upscale(BitmapSource source, int destWidth, int destHeight)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destWidth <= 0) throw new ArgumentOutOfRangeException(nameof(destWidth));
        if (destHeight <= 0) throw new ArgumentOutOfRangeException(nameof(destHeight));

        BitmapSource src = ConvertToBgra32(source);
        int srcW = src.PixelWidth;
        int srcH = src.PixelHeight;

        byte[] srcPixels = GetPixels(src, srcW, srcH);

        float[] hPass = HorizontalPass(srcPixels, srcW, srcH, destWidth);
        float[] vPass = VerticalPass(hPass, destWidth, srcH, destHeight);

        byte[] dstPixels = FloatToBytes(vPass, destWidth, destHeight);

        var bmp = BitmapSource.Create(
            destWidth, destHeight,
            source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null,
            dstPixels, destWidth * 4);

        bmp.Freeze();
        return bmp;
    }

    // ── Two-pass separable resampling ─────────────────────────────────────────

    private static float[] HorizontalPass(byte[] src, int srcW, int srcH, int dstW)
    {
        float[] dst = new float[dstW * srcH * 4];
        double scale = (double)srcW / dstW;
        double filterRadius = Math.Max(Radius, Radius * scale);

        // Pre-compute per output-column taps (shared across all rows)
        int[][] tapX = new int[dstW][];
        double[][] tapW = new double[dstW][];

        for (int x = 0; x < dstW; x++)
        {
            double center = (x + 0.5) * scale - 0.5;
            int x0 = (int)Math.Floor(center - filterRadius) + 1;
            int x1 = (int)Math.Floor(center + filterRadius);
            int taps = x1 - x0 + 1;

            tapX[x] = new int[taps];
            tapW[x] = new double[taps];
            double wSum = 0;

            for (int i = 0; i < taps; i++)
            {
                double w = CubicKernel((center - (x0 + i)) / Math.Max(1.0, scale));
                tapX[x][i] = Clamp(x0 + i, 0, srcW - 1);
                tapW[x][i] = w;
                wSum += w;
            }
            if (wSum == 0.0) wSum = 1.0;
            for (int i = 0; i < taps; i++) tapW[x][i] /= wSum;
        }

        Parallel.For(0, srcH, y =>
        {
            int srcBase = y * srcW * 4;
            int dstBase = y * dstW * 4;

            for (int x = 0; x < dstW; x++)
            {
                double sB = 0, sG = 0, sR = 0, sA = 0;
                int[] tx = tapX[x];
                double[] tw = tapW[x];

                for (int i = 0; i < tx.Length; i++)
                {
                    double w = tw[i];
                    int idx = srcBase + tx[i] * 4;
                    sB += w * src[idx];
                    sG += w * src[idx + 1];
                    sR += w * src[idx + 2];
                    sA += w * src[idx + 3];
                }

                int dstIdx = dstBase + x * 4;
                dst[dstIdx] = (float)sB;
                dst[dstIdx + 1] = (float)sG;
                dst[dstIdx + 2] = (float)sR;
                dst[dstIdx + 3] = (float)sA;
            }
        });

        return dst;
    }

    private static float[] VerticalPass(float[] src, int dstW, int srcH, int dstH)
    {
        float[] dst = new float[dstW * dstH * 4];
        double scale = (double)srcH / dstH;
        double filterRadius = Math.Max(Radius, Radius * scale);

        // Pre-compute per output-row taps
        int[][] tapY = new int[dstH][];
        double[][] tapW = new double[dstH][];

        for (int y = 0; y < dstH; y++)
        {
            double center = (y + 0.5) * scale - 0.5;
            int y0 = (int)Math.Floor(center - filterRadius) + 1;
            int y1 = (int)Math.Floor(center + filterRadius);
            int taps = y1 - y0 + 1;

            tapY[y] = new int[taps];
            tapW[y] = new double[taps];
            double wSum = 0;

            for (int i = 0; i < taps; i++)
            {
                double w = CubicKernel((center - (y0 + i)) / Math.Max(1.0, scale));
                tapY[y][i] = Clamp(y0 + i, 0, srcH - 1) * dstW * 4;
                tapW[y][i] = w;
                wSum += w;
            }
            if (wSum == 0.0) wSum = 1.0;
            for (int i = 0; i < taps; i++) tapW[y][i] /= wSum;
        }

        Parallel.For(0, dstH, y =>
        {
            int[] ty = tapY[y];
            double[] tw = tapW[y];
            int dstBase = y * dstW * 4;

            for (int x = 0; x < dstW; x++)
            {
                double sB = 0, sG = 0, sR = 0, sA = 0;
                int xOff = x * 4;

                for (int i = 0; i < ty.Length; i++)
                {
                    double w = tw[i];
                    int idx = ty[i] + xOff;
                    sB += w * src[idx];
                    sG += w * src[idx + 1];
                    sR += w * src[idx + 2];
                    sA += w * src[idx + 3];
                }

                int dstIdx = dstBase + xOff;
                dst[dstIdx] = (float)sB;
                dst[dstIdx + 1] = (float)sG;
                dst[dstIdx + 2] = (float)sR;
                dst[dstIdx + 3] = (float)sA;
            }
        });

        return dst;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BitmapSource ConvertToBgra32(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32) return src;
        return new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
    }

    private static byte[] GetPixels(BitmapSource src, int w, int h)
    {
        int stride = w * 4;
        byte[] px = new byte[h * stride];
        src.CopyPixels(px, stride, 0);
        return px;
    }

    private static byte[] FloatToBytes(float[] data, int w, int h)
    {
        byte[] result = new byte[w * h * 4];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(data[i])));
        return result;
    }

    private static int Clamp(int val, int min, int max)
        => val < min ? min : val > max ? max : val;
}