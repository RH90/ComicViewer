using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Upscales a WPF BitmapSource using a Lanczos resampling filter.
/// Pure managed C# — parallelised with Parallel.For for maximum throughput.
/// </summary>
/// 
namespace ComicViewer.NotUsed;

public static class LanczosUpscaler
{
    // ── Lanczos kernel ────────────────────────────────────────────────────────

    /// <summary>Lobes of the Lanczos kernel (2 = Lanczos2, 3 = Lanczos3).</summary>
    private const int Lobes = 3;

    private static double LanczosKernel(double x)
    {
        if (x == 0.0) return 1.0;
        if (Math.Abs(x) >= Lobes) return 0.0;
        double px = Math.PI * x;
        return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Upscales <paramref name="source"/> to the specified pixel dimensions using
    /// a Lanczos resampling filter. Internally parallelised across CPU cores.
    /// </summary>
    public static BitmapSource Upscale(BitmapSource source, int destWidth, int destHeight)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destWidth <= 0) throw new ArgumentOutOfRangeException(nameof(destWidth));
        if (destHeight <= 0) throw new ArgumentOutOfRangeException(nameof(destHeight));

        BitmapSource src = ConvertToBgra32(source);
        int srcW = src.PixelWidth;
        int srcH = src.PixelHeight;


        byte[] srcPixels = GetPixels(src, srcW, srcH);

        // Two-pass separable Lanczos: horizontal then vertical
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
        double filterRadius = Math.Max(Lobes, Lobes * scale);

        // Pre-compute per output-column tap positions and weights (reused for every row)
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
                double w = LanczosKernel((center - (x0 + i)) / Math.Max(1.0, scale));
                tapX[x][i] = Clamp(x0 + i, 0, srcW - 1);
                tapW[x][i] = w;
                wSum += w;
            }
            // Normalise in place
            if (wSum == 0.0) wSum = 1.0;
            for (int i = 0; i < taps; i++) tapW[x][i] /= wSum;
        }

        // Each row is independent → parallel
        Parallel.For(0, srcH, y =>
        {
            int srcRowBase = y * srcW * 4;
            int dstRowBase = y * dstW * 4;

            for (int x = 0; x < dstW; x++)
            {
                double sB = 0, sG = 0, sR = 0, sA = 0;
                int[] tx = tapX[x];
                double[] tw = tapW[x];

                for (int i = 0; i < tx.Length; i++)
                {
                    double w = tw[i];
                    int idx = srcRowBase + tx[i] * 4;
                    sB += w * src[idx];
                    sG += w * src[idx + 1];
                    sR += w * src[idx + 2];
                    sA += w * src[idx + 3];
                }

                int dstIdx = dstRowBase + x * 4;
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
        double filterRadius = Math.Max(Lobes, Lobes * scale);

        // Pre-compute per output-row tap positions and normalised weights
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
                double w = LanczosKernel((center - (y0 + i)) / Math.Max(1.0, scale));
                tapY[y][i] = Clamp(y0 + i, 0, srcH - 1) * dstW * 4;
                tapW[y][i] = w;
                wSum += w;
            }
            if (wSum == 0.0) wSum = 1.0;
            for (int i = 0; i < taps; i++) tapW[y][i] /= wSum;
        }

        // Each row is independent → parallel
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