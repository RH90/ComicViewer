//using ImageMagick;
//using NetVips;
//using PhotoSauce.MagicScaler;
//using System;
//using System.Drawing;
//using System.Windows.Controls;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;
//using Image = NetVips.Image;
//using WpfPixelFormats = System.Windows.Media.PixelFormats;

//namespace ComicViewer;

//public static class MagicScalerImageFactory
//{
//    // Revert to the correct, well-known BGR/BGRA GUIDs
//    private static readonly Guid WicGuid_Gray8 = new("6FDDC324-4E03-4BFE-B185-3D77768DC908");
//    private static readonly Guid WicGuid_Bgr24 = new("6FDDC324-4E03-4BFE-B185-3D77768DC90C");
//    private static readonly Guid WicGuid_Bgra32 = new("6FDDC324-4E03-4BFE-B185-3D77768DC90F");

//    // -------------------------------------------------------------------------
//    // Public API
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// Resizes a NetVips image using the MagicScaler pipeline and returns a
//    /// frozen WPF BitmapSource alongside the original image dimensions.
//    /// NetVips exports raw pixels into a <see cref="RawPixelSource"/> which
//    /// MagicScaler consumes directly — no encode/decode round-trip occurs.
//    /// </summary>
//    /// <param name="vipsImage">Decoded source image from any NetVips loader.</param>
//    /// <param name="targetWidth">Target width in pixels; 0 = preserve source width.</param>
//    /// <param name="targetHeight">Target height in pixels; 0 = preserve source height.</param>
//    /// <param name="keepAspectRatio">
//    /// If true, fits within the target box preserving aspect ratio.
//    /// If false, stretches to exact dimensions.
//    /// </param>
//    public static (BitmapSource Image, int OriginalWidth, int OriginalHeight) Scale(
//    Image vipsImage,
//    InterpolationSettings InterpolationSettings,
//    int targetWidth = 0,
//    int targetHeight = 0,
//    bool keepAspectRatio = true)
//    {
//        ArgumentNullException.ThrowIfNull(vipsImage);

//        int originalWidth = vipsImage.Width;
//        int originalHeight = vipsImage.Height;

//        Image? normalised = null;
//        Image? premul = null;
//        Image? casted = null;

//        try
//        {
//            normalised = NormaliseForExport(vipsImage);

//            int bands = normalised.Bands;
//            Guid wicFmt = BandsToWicGuid(bands);
//            bool hasAlpha = bands == 4; // use band count directly — more reliable than HasAlpha()

//            byte[] pixels;

//            if (hasAlpha)
//            {
//                premul = normalised.Premultiply();
//                casted = premul.Cast(Enums.BandFormat.Uchar);
//                pixels = casted.WriteToMemory();
//            }
//            else
//            {
//                pixels = normalised.WriteToMemory();
//            }

//            using var src = new RawPixelSource(pixels, normalised.Width, normalised.Height, bands, wicFmt);
//            var settings = BuildSettings(targetWidth, targetHeight, keepAspectRatio, InterpolationSettings);

//            using var pipeline = MagicImageProcessor.BuildPipeline(src, settings);
//            return (PipelineToBitmapSource(pipeline.PixelSource), originalWidth, originalHeight);
//        }
//        finally
//        {
//            // Disposed in reverse creation order — casted depends on premul,
//            // premul depends on normalised, so innermost is freed first.
//            casted?.Dispose();
//            premul?.Dispose();
//            normalised?.Dispose();
//        }
//    }

//    /// <summary>
//    /// Normalises a NetVips image to 1, 3, or 4 bands with BGR(A) channel ordering
//    /// for export to MagicScaler. Caller is responsible for disposing the result.
//    /// </summary>
//    private static Image NormaliseForExport(Image img)
//    {
//        // Flatten exotic formats (CMYK, 5+ bands) first.
//        // The flattened intermediate is owned here and disposed before returning.
//        if (img.Bands > 4)
//        {
//            using Image flattened = img.Flatten();
//            return NormaliseForExport(flattened); // re-enter with a clean 3/4 band image
//        }

//        return img.Bands switch
//        {
//            1 => img.Copy(),

//            // Grey + alpha — flatten onto white → 1 band.
//            2 => img.Flatten(background: new double[] { 255 }),

//            // Swap R↔B so bytes match the declared WicGuid_Bgr24.
//            3 => ReorderBands(img, 2, 1, 0),

//            // Swap R↔B so bytes match the declared WicGuid_Bgra32.
//            4 => ReorderBands(img, 2, 1, 0, 3),

//            _ => throw new NotSupportedException($"Unexpected band count: {img.Bands}")
//        };
//    }

//    private static Image ReorderBands(Image src, params int[] order)
//    {
//        var bands = new Image[order.Length];
//        try
//        {
//            for (int i = 0; i < order.Length; i++)
//                bands[i] = src[order[i]];

//            Image head = bands[0];
//            bands[0] = null!; // ownership transferred to using block below
//            using (head)
//                return head.Bandjoin(bands[1..]);
//        }
//        finally
//        {
//            // Dispose all band extractions that are still alive.
//            // bands[0] is null at this point — disposed via using (head) above.
//            foreach (var b in bands)
//                b?.Dispose();
//        }
//    }

//    // -------------------------------------------------------------------------
//    // Core: pull pixels directly from the pipeline into a WPF BitmapSource
//    // -------------------------------------------------------------------------

//    private static BitmapSource PipelineToBitmapSource(IPixelSource src)
//    {
//        PixelFormat wpfFormat = WicGuidToWpfFormat(src.Format);
//        int bytesPerPixel = (wpfFormat.BitsPerPixel + 7) / 8;
//        int width = src.Width;
//        int height = src.Height;
//        int stride = width * bytesPerPixel;

//        byte[] pixels = new byte[height * stride];
//        src.CopyPixels(new Rectangle(0, 0, width, height), stride, pixels.AsSpan());

//        var result = BitmapSource.Create(width, height, 96, 96, wpfFormat, null, pixels, stride);
//        result.Freeze();
//        return result;
//    }

//    // -------------------------------------------------------------------------
//    // Helpers
//    // -------------------------------------------------------------------------

//    private static ProcessImageSettings BuildSettings(
//        int targetWidth, int targetHeight, bool keepAspectRatio, InterpolationSettings interpolation)
//    {
//        var settings = new ProcessImageSettings
//        {
//            BlendingMode = GammaMode.Companded,
//            HybridMode = HybridScaleMode.FavorQuality,
//            Interpolation = interpolation,
//        };

//        if (targetWidth > 0) settings.Width = targetWidth;
//        if (targetHeight > 0) settings.Height = targetHeight;

//        if (targetWidth > 0 && targetHeight > 0)
//        {
//            settings.ResizeMode = keepAspectRatio
//                ? CropScaleMode.Pad
//                : CropScaleMode.Stretch;
//        }

//        return settings;
//    }

//    /// <summary>
//    /// Normalises a NetVips image to 1, 3, or 4 bands for export.
//    /// NetVips outputs RGB(A) — RawPixelSource uses WIC GUIDs to declare
//    /// the layout, so no manual band reorder is needed here.
//    /// </summary>
//    private static Guid BandsToWicGuid(int bands) => bands switch
//    {
//        1 => WicGuid_Gray8,
//        3 => WicGuid_Bgr24,   // bytes are now actually BGR after ReorderBands
//        4 => WicGuid_Bgra32,  // bytes are now actually BGRA after ReorderBands
//        _ => throw new NotSupportedException($"No WIC GUID for {bands} bands.")
//    };

//    private static PixelFormat WicGuidToWpfFormat(Guid guid)
//    {
//        if (guid == WicGuid_Bgra32) return WpfPixelFormats.Bgra32;
//        if (guid == WicGuid_Bgr24) return WpfPixelFormats.Bgr24;
//        if (guid == WicGuid_Gray8) return WpfPixelFormats.Gray8;
//        return WpfPixelFormats.Bgra32;
//    }

//    // In NormaliseForExport — remove the comment about BGR, it's RGB:


//    // RawPixelSource
//    // -------------------------------------------------------------------------

//    internal sealed class RawPixelSource : IPixelSource, IDisposable
//    {
//        private readonly byte[] _pixels;
//        private readonly int _bands;
//        private readonly int _rowStride;

//        public Guid Format { get; }
//        public int Width { get; }
//        public int Height { get; }

//        public RawPixelSource(byte[] pixels, int width, int height, int bands, Guid wicFormat)
//        {
//            _pixels = pixels;
//            _bands = bands;
//            _rowStride = width * bands;
//            Width = width;
//            Height = height;
//            Format = wicFormat;
//        }

//        public void CopyPixels(Rectangle sourceArea, int cbStride, Span<byte> buffer)
//        {
//            int rowBytes = sourceArea.Width * _bands;

//            for (int row = 0; row < sourceArea.Height; row++)
//            {
//                int srcOffset = (sourceArea.Y + row) * _rowStride + sourceArea.X * _bands;
//                int dstOffset = row * cbStride;
//                _pixels.AsSpan(srcOffset, rowBytes).CopyTo(buffer.Slice(dstOffset));
//            }
//        }

//        public void Dispose() { }
//    }
//}

using PhotoSauce.MagicScaler;
using PhotoSauce.MagicScaler.Transforms;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace ComicViewer;

public enum MagicImageFormat { Jpeg, Png, WebP, Jxl, Jxr }

public static class MagicScalerImageFactory
{
    // WIC pixel format GUIDs — the stable constants MagicScaler uses in IPixelSource.Format.
    private static readonly Guid WicGuid_Gray8 = new("6FDDC324-4E03-4BFE-B185-3D77768DC908");
    private static readonly Guid WicGuid_Bgr24 = new("6FDDC324-4E03-4BFE-B185-3D77768DC90C");
    private static readonly Guid WicGuid_Bgra32 = new("6FDDC324-4E03-4BFE-B185-3D77768DC90F");

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decodes and optionally resizes an image, returning a frozen WPF BitmapSource
    /// alongside the original image dimensions.
    /// </summary>
    /// 
    private static InterpolationSettings defaultInterpolation = InterpolationSettings.Lanczos;
    public static (BitmapSource Image, int OriginalWidth, int OriginalHeight) Scale(
        InterpolationSettings interpolation,
        byte[] data,
        MagicImageFormat format,
        int targetWidth = 0,
        int targetHeight = 0,
        bool keepAspectRatio = true,
        bool linear = false)
    {
        ArgumentNullException.ThrowIfNull(data);

        return format switch
        {
            MagicImageFormat.Jpeg => ScaleEncoded(data, targetWidth, targetHeight, keepAspectRatio, interpolation, linear),
            MagicImageFormat.Png => ScaleEncoded(data, targetWidth, targetHeight, keepAspectRatio, interpolation, linear),
            MagicImageFormat.WebP => ScaleEncoded(data, targetWidth, targetHeight, keepAspectRatio, interpolation, linear),
            MagicImageFormat.Jxl => ScaleJxl(data, targetWidth, targetHeight, keepAspectRatio, interpolation, linear),
            MagicImageFormat.Jxr => ScaleJxr(data, targetWidth, targetHeight, keepAspectRatio, interpolation, linear),
            _ => throw new NotSupportedException($"Format '{format}' is not supported.")
        };
    }

    // -------------------------------------------------------------------------
    // Encoded paths (JPEG, PNG, WebP)
    // -------------------------------------------------------------------------

    private static (BitmapSource, int, int) ScaleEncoded(
     byte[] data, int targetWidth, int targetHeight, bool keepAspectRatio, InterpolationSettings interpolation, bool linear)
    {
        // ImageFileInfo.Load reads only the compressed header — no pixel decode.
        // Frames[0] contains the corrected (post-EXIF-rotation) Width and Height.
        var frameInfo = ImageFileInfo.Load(data.AsSpan()).Frames[0];

        var settings = BuildSettings(targetWidth, targetHeight, keepAspectRatio, interpolation, linear, (double)frameInfo.Width / (double)targetWidth);

        using var pipeline = MagicImageProcessor.BuildPipeline(
                                 new MemoryStream(data, writable: false), settings);
        Debug.WriteLine($"MagicScaler settings: Unsharp: Threshold={pipeline.Settings.UnsharpMask.Threshold},Amount={pipeline.Settings.UnsharpMask.Amount},Radius={pipeline.Settings.UnsharpMask.Radius}");
        return (PipelineToBitmapSource(pipeline.PixelSource), frameInfo.Width, frameInfo.Height);
    }

    // -------------------------------------------------------------------------
    // JXL — native wrapper → RawPixelSource → MagicScaler pipeline
    // -------------------------------------------------------------------------

    private static (BitmapSource, int, int) ScaleJxl(
        byte[] data, int targetWidth, int targetHeight, bool keepAspectRatio, InterpolationSettings interpolation, bool linear)
    {
        var (pixels, w, h, _) = JxlDecoder.DecodeInternalRaw(
            data, new JxlDecodeOptions { Threads = Environment.ProcessorCount });

        int originalWidth = (int)w;
        int originalHeight = (int)h;

        if (pixels.Length == data.Length)
            throw new InvalidOperationException(
                "JXL decode returned the same byte count as the input. " +
                "The native wrapper likely failed silently — check that libjxl.dll is present.");

        int bands = pixels.Length / (originalWidth * originalHeight);

        if (bands is not (1 or 3 or 4) || bands * originalWidth * originalHeight != pixels.Length)
            throw new InvalidOperationException(
                $"Unexpected JXL buffer size: {pixels.Length} bytes for {originalWidth}×{originalHeight} " +
                $"(implies {(double)pixels.Length / (originalWidth * originalHeight):F2} bands).");

        // libjxl outputs RGB(A); MagicScaler's IPixelSource expects BGR(A).
        if (bands >= 3) SwapRedBlue(pixels, bands);

        using var src = new RawPixelSource(pixels, originalWidth, originalHeight, bands, BandsToWicGuid(bands));
        var settings = BuildSettings(targetWidth, targetHeight, keepAspectRatio, interpolation, linear, (double)originalWidth / (double)targetWidth);

        using var pipeline = MagicImageProcessor.BuildPipeline(src, settings);
        return (PipelineToBitmapSource(pipeline.PixelSource), originalWidth, originalHeight);
    }

    // -------------------------------------------------------------------------
    // JXR — WIC decoder → RawPixelSource → MagicScaler pipeline
    // -------------------------------------------------------------------------

    private static (BitmapSource, int, int) ScaleJxr(
        byte[] data, int targetWidth, int targetHeight, bool keepAspectRatio, InterpolationSettings interpolation, bool linear)
    {
        int originalWidth, originalHeight;
        byte[] bgra;

        using (var stream = new MemoryStream(data, writable: false))
        {
            var decoder = BitmapDecoder.Create(
                                stream,
                                BitmapCreateOptions.PreservePixelFormat,
                                BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(frame, WpfPixelFormats.Bgra32, null, 0);

            originalWidth = converted.PixelWidth;
            originalHeight = converted.PixelHeight;
            bgra = new byte[originalHeight * originalWidth * 4];
            converted.CopyPixels(bgra, originalWidth * 4, 0);
        }

        // WIC outputs BGRA — already in the correct order for MagicScaler.
        using var src = new RawPixelSource(bgra, originalWidth, originalHeight, 4, WicGuid_Bgra32);
        var settings = BuildSettings(targetWidth, targetHeight, keepAspectRatio, interpolation, linear, (double)originalWidth / (double)targetWidth);

        using var pipeline = MagicImageProcessor.BuildPipeline(src, settings);
        Debug.WriteLine($"MagicScaler settings: Unsharp: Threshold={pipeline.Settings.UnsharpMask.Threshold},Amount={pipeline.Settings.UnsharpMask.Amount},Radius={pipeline.Settings.UnsharpMask.Radius}");
        return (PipelineToBitmapSource(pipeline.PixelSource), originalWidth, originalHeight);
    }

    // -------------------------------------------------------------------------
    // Core: pull pixels directly from the pipeline into a WPF BitmapSource
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pulls pixels from the MagicScaler pipeline output directly into a managed
    /// byte array, then wraps it in a frozen <see cref="BitmapSource"/>.
    /// No intermediate encode/decode occurs — this is the critical path optimisation
    /// over the previous ProcessImage → PNG → BitmapDecoder approach.
    /// </summary>
    private static BitmapSource PipelineToBitmapSource(IPixelSource src)
    {
        PixelFormat wpfFormat = WicGuidToWpfFormat(src.Format);

        int bytesPerPixel = (wpfFormat.BitsPerPixel + 7) / 8;
        int width = src.Width;
        int height = src.Height;
        int stride = width * bytesPerPixel;

        byte[] pixels = new byte[height * stride];

        // Pull all rows in one call. MagicScaler processes lazily row-by-row
        // as CopyPixels streams through the pipeline — memory usage stays low.
        src.CopyPixels(new Rectangle(0, 0, width, height), stride, pixels.AsSpan());

        var result = BitmapSource.Create(width, height, 96, 96, wpfFormat, null, pixels, stride);
        result.Freeze();
        return result;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ProcessImageSettings BuildSettings(
      int targetWidth, int targetHeight, bool keepAspectRatio, InterpolationSettings interpolation, bool linear, double ratio)
    {

        UnsharpMaskSettings us = new UnsharpMaskSettings(40, 1.5, 0x55);

        // SaveFormat is not a property of ProcessImageSettings — it was removed.
        // Since we use BuildPipeline + CopyPixels, no encoder is involved at all.
        // MagicScaler settings: Unsharp: Threshold=0,Amount=40,Radius=1,5

        var settings = new ProcessImageSettings
        {
            Anchor = CropAnchor.Top | CropAnchor.Left,
            BlendingMode = GammaMode.Companded,
            //BlendingMode = GammaMode.Linear,
            //UnsharpMask = us,
            //Sharpen = false,
            //ResizeMode = CropScaleMode.Stretch,
            HybridMode = HybridScaleMode.FavorQuality,
            Interpolation = interpolation,
            //ColorProfileMode = ColorProfileMode.Normalize,
        };

        if (interpolation == InterpolationSettings.CatmullRom)
        {
            //settings.Interpolation = InterpolationSettings.Lanczos;
            //settings.UnsharpMask = new UnsharpMaskSettings(30, 1.5, 0x45);
        }
        else if (interpolation == InterpolationSettings.Lanczos)
        {
            //settings.UnsharpMask = new UnsharpMaskSettings(40, 1.5, 0x30);
        }
        else if (interpolation == InterpolationSettings.Hermite)
        {
            settings.Interpolation = InterpolationSettings.Lanczos;
            settings.UnsharpMask = new UnsharpMaskSettings(30, 1.5, 0x80);
            //settings.UnsharpMask = new UnsharpMaskSettings(25, 1.5, 0x70);
        }


        if (linear)
        {
            settings.BlendingMode = GammaMode.Linear;
            settings.ColorProfileMode = ColorProfileMode.Normalize;
        }


        if (targetWidth > 0) settings.Width = targetWidth;
        if (targetHeight > 0) settings.Height = targetHeight;


        if (targetWidth > 0 && targetHeight > 0)
        {
            settings.ResizeMode = keepAspectRatio
                //? CropScaleMode.Pad
                ? CropScaleMode.Crop
                : CropScaleMode.Stretch;
        }
        if (ratio == 1)
        {
            settings.Sharpen = false;
        }
        //Debug.WriteLine($"MagicScaler settings: Unsharp: Threshold={settings.UnsharpMask.Threshold},Amount={settings.UnsharpMask.Amount},Radius={settings.UnsharpMask.Radius}");

        return settings;
    }

    /// <summary>
    /// Maps MagicScaler's WIC pixel format GUIDs to WPF PixelFormat instances.
    /// Only the formats that MagicScaler realistically outputs are handled.
    /// </summary>
    private static PixelFormat WicGuidToWpfFormat(Guid guid)
    {
        if (guid == WicGuid_Bgra32) return WpfPixelFormats.Bgra32;
        if (guid == WicGuid_Bgr24) return WpfPixelFormats.Bgr24;
        if (guid == WicGuid_Gray8) return WpfPixelFormats.Gray8;

        // Fallback: force to Bgra32 to avoid throwing on an unusual pipeline output.
        return WpfPixelFormats.Bgra32;
    }

    private static Guid BandsToWicGuid(int bands) => bands switch
    {
        1 => WicGuid_Gray8,
        3 => WicGuid_Bgr24,
        4 => WicGuid_Bgra32,
        _ => throw new NotSupportedException($"No WIC GUID for {bands} bands.")
    };

    /// <summary>Swaps R and B channels of an interleaved BGR(A) buffer in-place.</summary>
    private static void SwapRedBlue(byte[] pixels, int bands)
    {
        for (int i = 0; i < pixels.Length; i += bands)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}

// -------------------------------------------------------------------------
// RawPixelSource
// -------------------------------------------------------------------------

/// <summary>
/// Adapts a raw interleaved BGR(A) pixel buffer to MagicScaler's
/// <see cref="IPixelSource"/> interface.
/// </summary>
internal sealed class RawPixelSource : IPixelSource, IDisposable
{
    private readonly byte[] _pixels;
    private readonly int _bands;
    private readonly int _rowStride;

    public Guid Format { get; }
    public int Width { get; }
    public int Height { get; }

    public RawPixelSource(byte[] pixels, int width, int height, int bands, Guid wicFormat)
    {
        _pixels = pixels;
        _bands = bands;
        _rowStride = width * bands;
        Width = width;
        Height = height;
        Format = wicFormat;
    }

    public void CopyPixels(Rectangle sourceArea, int cbStride, Span<byte> buffer)
    {
        int rowBytes = sourceArea.Width * _bands;

        for (int row = 0; row < sourceArea.Height; row++)
        {
            int srcOffset = (sourceArea.Y + row) * _rowStride + sourceArea.X * _bands;
            int dstOffset = row * cbStride;

            _pixels.AsSpan(srcOffset, rowBytes).CopyTo(buffer.Slice(dstOffset));
        }
    }

    public void Dispose() { }
}