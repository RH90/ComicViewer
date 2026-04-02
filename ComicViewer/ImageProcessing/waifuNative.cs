using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.IO;
using NetVips;
using System.Diagnostics;

namespace ComicViewer.ImageProcessing;

public static class Waifu2xNative
{
    private const string DllName = "waifu2x_dll";

    public enum GpuId
    {
        Auto = -2,
        Cpu = -1,
        Gpu0 = 0,
        Gpu1 = 1,
        Gpu2 = 2,
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int waifu2x_upscale_pixels(
        byte[] inputPixels,
        int width,
        int height,
        int channels,
        out IntPtr outputPixels,
        out int outputWidth,
        out int outputHeight,
        string modelPath,
        int noise,
        int scale,
        int tileSize,
        int gpuid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void waifu2x_free(IntPtr data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int waifu2x_get_gpu_count();


    public enum Waifu2xModel { CUnet, AnimeStyleArt, Photo }
    public enum Waifu2xNoiseLevel { None = -1, Low = 0, Medium = 1, High = 2, Highest = 3 }
    /// <summary>
    /// Takes a PNG byte array, returns an upscaled PNG byte array.
    /// All PNG encode/decode happens in .NET — no stb_image involved.
    /// </summary>
    public static Image UpscalePng(
        Image image,
        Waifu2xModel model,
        Waifu2xNoiseLevel noise = Waifu2xNoiseLevel.Medium,
        int scale = 2,
        int tileSize = 0,
        GpuId gpu = GpuId.Auto)
    {
        // --- Ensure image is RGB or RGBA (uchar) ---
        // Flatten to remove any alpha-premultiplication, cast to uchar
        bool hasAlpha = image.HasAlpha();
        int channels = hasAlpha ? 4 : 3;

        // Convert to sRGB + correct number of bands, 8-bit uchar
        Image prepared = image
            .Colourspace(Enums.Interpretation.Srgb)
            .Cast(Enums.BandFormat.Uchar);

        // If alpha state doesn't match, add or remove it
        if (hasAlpha && prepared.Bands == 3)
            prepared = prepared.Bandjoin(255); // add opaque alpha
        else if (!hasAlpha && prepared.Bands == 4)
            prepared = prepared.Flatten();     // remove alpha

        int width = prepared.Width;
        int height = prepared.Height;

        // --- Extract raw pixel bytes from NetVips ---
        byte[] pixels = prepared.WriteToMemory<byte>();

        string modelPath = Path.Join(AppContext.BaseDirectory, "waifu2x", ModelToDirectory(model));

        // --- Call native upscaler ---
        int result = waifu2x_upscale_pixels(
            pixels, width, height, channels,
            out IntPtr outPtr, out int outW, out int outH,
            modelPath, (int)noise, scale, tileSize, (int)gpu);

        if (result != 0)
            throw new Exception($"waifu2x failed — code {result}: " +
                result switch
                {
                    -1 => "null argument",
                    -3 => "model load failed — check modelPath and noise",
                    -4 => "process failed — check GPU/Vulkan",
                    -6 => "out of memory",
                    _ => "unknown error"
                });

        // --- Wrap output pointer into a NetVips image ---
        int outSize = outW * outH * channels;
        byte[] outPixels = new byte[outSize];
        Marshal.Copy(outPtr, outPixels, 0, outSize);
        waifu2x_free(outPtr);

        // Reconstruct NetVips image from raw bytes
        // NewFromMemory(data, width, height, bands, format)
        Image outImage = Image.NewFromMemory(
            outPixels,
            outW, outH,
            channels,
            Enums.BandFormat.Uchar);

        // Restore the original interpretation
        outImage = outImage.Copy(interpretation: image.Interpretation);
        image.Close();
        prepared.Close();

        return outImage;
    }

    private static string ModelToDirectory(Waifu2xModel model) => model switch
    {
        Waifu2xModel.CUnet => "models-cunet",
        Waifu2xModel.AnimeStyleArt => "models-upconv_7_anime_style_art_rgb",
        Waifu2xModel.Photo => "models-upconv_7_photo",
        _ => "models-cunet"
    };
}