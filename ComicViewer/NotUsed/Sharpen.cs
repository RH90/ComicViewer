using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicViewer.NotUsed
{
    public class Sharpen
    {

        public static BitmapSource Sharp(BitmapSource source, double strength = 1.0)
        {
            // Convert to Bgra32 for easy pixel access
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;

            byte[] pixels = new byte[height * stride];
            byte[] output = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            // Sharpening kernel (Laplacian-based):
            // [  0, -1,  0 ]
            // [ -1,  5, -1 ]
            // [  0, -1,  0 ]
            // Strength blends between original (0) and fully sharpened (1)
            double center = 1 + 4 * strength;

            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int i = (y * width + x) * 4;

                    for (int c = 0; c < 3; c++) // B, G, R (skip alpha)
                    {
                        double val = center * pixels[i + c]
                                   - strength * pixels[i + c - 4]          // left
                                   - strength * pixels[i + c + 4]          // right
                                   - strength * pixels[i + c - stride]     // up
                                   - strength * pixels[i + c + stride];    // down

                        output[i + c] = Clamp(val);
                    }

                    output[i + 3] = pixels[i + 3]; // Preserve alpha
                }
            }

            // Copy border pixels unchanged
            CopyBorder(pixels, output, width, height, stride);

            return BitmapSource.Create(width, height,
                source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null,
                output, stride);
        }

        private static byte Clamp(double value)
            => (byte)Math.Max(0, Math.Min(255, (int)value));

        private static void CopyBorder(byte[] src, byte[] dst, int width, int height, int stride)
        {
            // Top and bottom rows
            Array.Copy(src, 0, dst, 0, stride);
            Array.Copy(src, (height - 1) * stride, dst, (height - 1) * stride, stride);

            // Left and right columns
            for (int y = 0; y < height; y++)
            {
                int left = y * stride;
                int right = y * stride + (width - 1) * 4;
                Array.Copy(src, left, dst, left, 4);
                Array.Copy(src, right, dst, right, 4);
            }
        }

        public static BitmapSource UnsharpMask(BitmapSource source,
        double sigma = 1.5,    // Blur radius (higher = affects more detail)
        double amount = 1.2,   // Sharpening strength
        int threshold = 3)  // Min difference to sharpen (avoids noise)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;

            byte[] original = new byte[height * stride];
            converted.CopyPixels(original, stride, 0);

            // Step 1: Gaussian blur
            byte[] blurred = GaussianBlur(original, width, height, stride, sigma);

            // Step 2: output = original + amount * (original - blurred)  [if diff > threshold]
            byte[] output = new byte[height * stride];

            for (int i = 0; i < original.Length - 4; i += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int diff = original[i + c] - blurred[i + c];

                    if (Math.Abs(diff) >= threshold)
                        output[i + c] = Clamp(original[i + c] + amount * diff);
                    else
                        output[i + c] = original[i + c];
                }
                output[i + 3] = original[i + 3]; // Alpha
            }

            return BitmapSource.Create(width, height,
                source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null,
                output, stride);
        }
        private static byte[] GaussianBlur(byte[] pixels, int width, int height, int stride, double sigma)
        {
            int radius = (int)Math.Ceiling(sigma * 3);
            double[] kernel = BuildGaussianKernel(sigma, radius);

            byte[] temp = new byte[pixels.Length];
            byte[] result = new byte[pixels.Length];

            // Horizontal pass
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        double sum = 0, weight = 0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int nx = Math.Clamp(x + k, 0, width - 1);
                            double w = kernel[k + radius];
                            sum += pixels[(y * width + nx) * 4 + c] * w;
                            weight += w;
                        }
                        temp[idx + c] = Clamp(sum / weight);
                    }
                    temp[idx + 3] = pixels[idx + 3];
                }

            // Vertical pass
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        double sum = 0, weight = 0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int ny = Math.Clamp(y + k, 0, height - 1);
                            double w = kernel[k + radius];
                            sum += temp[(ny * width + x) * 4 + c] * w;
                            weight += w;
                        }
                        result[idx + c] = Clamp(sum / weight);
                    }
                    result[idx + 3] = pixels[idx + 3];
                }

            return result;
        }

        private static double[] BuildGaussianKernel(double sigma, int radius)
        {
            int size = radius * 2 + 1;
            double[] kernel = new double[size];
            double sum = 0;

            for (int i = 0; i < size; i++)
            {
                int x = i - radius;
                kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
                sum += kernel[i];
            }

            for (int i = 0; i < size; i++)
                kernel[i] /= sum; // Normalize

            return kernel;
        }
    }
}
