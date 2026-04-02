using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.IO;
using NetVips;
using System.Diagnostics;

namespace ComicViewer.NotUsed;

public static class Waifu2xNativeOld
{
    public enum Waifu2xModel { CUnet, AnimeStyleArt, Photo }
    public enum Waifu2xNoiseLevel { None = -1, Low = 0, Medium = 1, High = 2, Highest = 3 }

    private const string DllName = "waifu2x_dll_old";

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
        int tileSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void waifu2x_free(IntPtr data);

    /// <summary>
    /// Takes a PNG byte array, returns an upscaled PNG byte array.
    /// All PNG encode/decode happens in .NET — no stb_image involved.
    /// </summary>
    public static Image UpscalePng(
        Image image,
        Waifu2xModel model,
        Waifu2xNoiseLevel noise = Waifu2xNoiseLevel.Medium,
        int scale = 2,
        int tileSize = 0)
    {
        // --- Decode PNG using WPF BitmapDecoder ---
        //BitmapSource src;
        //using (var ms = new MemoryStream(inputPng))
        //{
        //    var decoder = BitmapDecoder.Create(ms,
        //        BitmapCreateOptions.PreservePixelFormat,
        //        BitmapCacheOption.OnLoad);
        //    src = decoder.Frames[0];
        //}

        //// Convert to BGR24 or BGRA32 depending on alpha
        //bool hasAlpha = src.Format == PixelFormats.Bgra32
        //             || src.Format == PixelFormats.Rgba64
        //             || src.Format == PixelFormats.Pbgra32;

        //var fmt = hasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        //int channels = hasAlpha ? 4 : 3;

        //var converted = new FormatConvertedBitmap(src, fmt, null, 0);
        //int width = converted.PixelWidth;
        //int height = converted.PixelHeight;
        //int stride = width * channels;
        //byte[] pixels = new byte[stride * height];
        //converted.CopyPixels(pixels, stride, 0);

        int width = image.Width;
        int height = image.Height;
        byte[] dstBytes = image.WriteToMemory<byte>();
        int bands = image.Bands;

        Enums.BandFormat bandFormat = image.Format;

        //if (image.HasAlpha())
        //{
        //    channels = 4;
        //}
        //Debug.WriteLine("Bands: " + bands + ", Channels: " + channels);

        string modelPath = Path.Join(AppContext.BaseDirectory, "waifu2x", ModelToDirectory(model));

        // --- Call native upscaler ---
        int result = waifu2x_upscale_pixels(
            dstBytes, width, height, bands,
            out IntPtr outPtr, out int outW, out int outH,
            modelPath, (int)noise, scale, tileSize);

        if (result != 0)
            throw new Exception($"waifu2x_upscale_pixels failed: code {result} — " +
                result switch
                {
                    -1 => "null argument",
                    -3 => "model load failed — check modelPath and noise level",
                    -4 => "process failed — check GPU/Vulkan",
                    -6 => "malloc failed",
                    _ => "unknown error"
                });

        // --- Copy output pixels ---
        int outSize = outW * outH * bands;
        byte[] outPixels = new byte[outSize];
        Marshal.Copy(outPtr, outPixels, 0, outSize);
        waifu2x_free(outPtr);

        //// --- Encode back to PNG using WPF ---
        //int outStride = outW * channels;
        //var outBitmap = BitmapSource.Create(
        //    outW, outH, 96, 96, fmt, null, outPixels, outStride);

        //using var outMs = new MemoryStream();
        //var encoder = new PngBitmapEncoder();
        //encoder.Frames.Add(BitmapFrame.Create(outBitmap));
        //encoder.Save(outMs);
        //return outMs.ToArray();
        image.Close();
        return NetVips.Image.NewFromMemory(outPixels, outW, outH, bands, bandFormat);
    }

    private static string ModelToDirectory(Waifu2xModel model) => model switch
    {
        Waifu2xModel.CUnet => "models-cunet",
        Waifu2xModel.AnimeStyleArt => "models-upconv_7_anime_style_art_rgb",
        Waifu2xModel.Photo => "models-upconv_7_photo",
        _ => "models-cunet"
    };
}