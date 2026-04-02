//using ImageMagick;
//using NetVips;
//using PhotoSauce.MagicScaler;
//using SixLabors.ImageSharp;
//using SixLabors.ImageSharp.PixelFormats;
//using SixLabors.ImageSharp.Processing;
//using SixLabors.ImageSharp.Processing.Processors.Transforms;
//using System;
//using System.IO.Packaging;
//using System.Runtime.CompilerServices;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;
//using static System.Windows.Forms.VisualStyles.VisualStyleElement.Rebar;
//using WpfPixelFormats = System.Windows.Media.PixelFormats;

//namespace ComicViewer;

//public static class ImageSharpImageFactory
//{
//    // -------------------------------------------------------------------------
//    // Public API
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// Resizes a NetVips image using ImageSharp and returns a frozen WPF
//    /// BitmapSource alongside the original image dimensions.
//    /// NetVips exports raw pixels; ImageSharp handles resampling.
//    /// No encode/decode round-trip occurs.
//    /// </summary>
//    /// <param name="vipsImage">Decoded source image from any NetVips loader.</param>
//    /// <param name="resampler">ImageSharp resampler. Defaults to Lanczos3.</param>
//    /// <param name="targetWidth">Target width in pixels; 0 = preserve source width.</param>
//    /// <param name="targetHeight">Target height in pixels; 0 = preserve source height.</param>
//    /// <param name="keepAspectRatio">
//    /// If true, fits within the target box preserving aspect ratio.
//    /// If false, stretches to exact dimensions.
//    /// </param>
//    public static (BitmapSource Image, int OriginalWidth, int OriginalHeight) Scale(
//        NetVips.Image vipsImage,
//        IResampler resampler = null!,
//        int targetWidth = 0,
//        int targetHeight = 0,
//        bool keepAspectRatio = true)
//    {
//        ArgumentNullException.ThrowIfNull(vipsImage);
//        resampler ??= KnownResamplers.Lanczos3;

//        int originalWidth = vipsImage.Width;
//        int originalHeight = vipsImage.Height;

//        int bands = NormalisedBandCount(vipsImage);
//        bool hasAlpha = bands == 4;

//        BitmapSource result = bands switch
//        {
//            1 => ScaleGray(vipsImage, resampler, targetWidth, targetHeight, keepAspectRatio),
//            3 => ScaleRgb(vipsImage, resampler, targetWidth, targetHeight, keepAspectRatio),
//            4 => ScaleRgba(vipsImage, resampler, targetWidth, targetHeight, keepAspectRatio),
//            _ => throw new NotSupportedException($"Unexpected band count: {bands}")
//        };

//        return (result, originalWidth, originalHeight);
//    }

//    // -------------------------------------------------------------------------
//    // Per-format scale paths
//    // -------------------------------------------------------------------------

//    /// <summary>Greyscale (L8) path.</summary>
//    private static BitmapSource ScaleGray(
//        NetVips.Image vipsImage,
//        IResampler resampler,
//        int targetWidth, int targetHeight, bool keepAspectRatio)
//    {
//        // Flatten Grey+Alpha (2-band) onto white → 1-band before export.
//        NetVips.Image? flat = null;

//        try
//        {
//            NetVips.Image src = vipsImage.Bands == 2
//                ? (flat = vipsImage.Flatten(background: new double[] { 255 }))
//                : vipsImage;

//            byte[] pixels = src.WriteToMemory();

//            using var img = SixLabors.ImageSharp.Image.LoadPixelData<L8>(pixels, src.Width, src.Height);
//            ApplyResize(img, resampler, targetWidth, targetHeight, keepAspectRatio);
//            return ImageSharpToBitmapSource(img);
//        }
//        finally
//        {
//            flat?.Dispose();
//        }
//    }

//    /// <summary>RGB (Rgb24) path — outputs Bgr24 for WPF.</summary>
//    private static BitmapSource ScaleRgb(
//        NetVips.Image vipsImage,
//        IResampler resampler,
//        int targetWidth, int targetHeight, bool keepAspectRatio)
//    {
//        // NetVips outputs RGB — ImageSharp's Rgb24 matches this layout directly.
//        byte[] pixels = vipsImage.WriteToMemory();

//        using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgb24>(pixels, vipsImage.Width, vipsImage.Height);
//        ApplyResize(img, resampler, targetWidth, targetHeight, keepAspectRatio);
//        return ImageSharpToBitmapSource(img);
//    }

//    /// <summary>RGBA (Rgba32) path — outputs Bgra32 for WPF.</summary>
//    private static BitmapSource ScaleRgba(
//        NetVips.Image vipsImage,
//        IResampler resampler,
//        int targetWidth, int targetHeight, bool keepAspectRatio)
//    {
//        // Premultiply before resize to prevent colour fringing on transparent edges.
//        // Cast back to uchar — NetVips Premultiply outputs float.
//        NetVips.Image? premul = null;
//        NetVips.Image? casted = null;

//        try
//        {
//            premul = vipsImage.Premultiply();
//            casted = premul.Cast(Enums.BandFormat.Uchar);

//            byte[] pixels = casted.WriteToMemory();

//            // ImageSharp's Rgba32 is RGBA order — matches NetVips' RGBA output exactly.
//            using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(pixels, vipsImage.Width, vipsImage.Height);
//            ApplyResize(img, resampler, targetWidth, targetHeight, keepAspectRatio);
//            return ImageSharpToBitmapSource(img);
//        }
//        finally
//        {
//            casted?.Dispose();
//            premul?.Dispose();
//        }
//    }

//    // -------------------------------------------------------------------------
//    // Resize
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// Applies in-place resize to an ImageSharp image.
//    /// compand: false = resize in gamma-companded sRGB (equivalent to GammaMode.Companded).
//    /// compand: true  = resize in linear light        (equivalent to GammaMode.Linear).
//    /// </summary>
//    private static void ApplyResize<TPixel>(
//        SixLabors.ImageSharp.Image<TPixel> img,
//        IResampler resampler,
//        int targetWidth, int targetHeight, bool keepAspectRatio)
//        where TPixel : unmanaged, SixLabors.ImageSharp.PixelFormats.IPixel<TPixel>
//    {
//        if (targetWidth <= 0 && targetHeight <= 0)
//            return;

//        img.Mutate(ctx => ctx.Resize(new ResizeOptions
//        {
//            Size = new SixLabors.ImageSharp.Size(
//                          targetWidth > 0 ? targetWidth : 0,
//                          targetHeight > 0 ? targetHeight : 0),
//            Sampler = resampler,
//            Mode = keepAspectRatio ? ResizeMode.Pad : ResizeMode.Stretch,

//            // compand: false = gamma-companded sRGB blending (GammaMode.Companded equivalent).
//            // Set to true for linear-light blending (GammaMode.Linear equivalent).
//            Compand = false,
//        }));
//    }

//    // -------------------------------------------------------------------------
//    // Pixel export → WPF BitmapSource
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// Exports an ImageSharp image to a frozen WPF BitmapSource.
//    /// Converts to Bgra32 or Bgr24 via CopyPixelDataTo — no encode/decode round-trip.
//    /// </summary>
//    private static BitmapSource ImageSharpToBitmapSource<TPixel>(
//        SixLabors.ImageSharp.Image<TPixel> img)
//        where TPixel : unmanaged, SixLabors.ImageSharp.PixelFormats.IPixel<TPixel>
//    {
//        int width = img.Width;
//        int height = img.Height;

//        // Convert to the WPF-compatible pixel type before exporting.
//        // ImageSharp handles the pixel format conversion internally.
//        if (typeof(TPixel) == typeof(L8))
//        {
//            // Greyscale — 1 byte per pixel, no reorder needed.
//            int stride = width;
//            byte[] pixels = new byte[height * stride];
//            img.CopyPixelDataTo(pixels);

//            return CreateFrozenBitmapSource(pixels, width, height, stride, WpfPixelFormats.Gray8);
//        }
//        else
//        {
//            // Colour — convert to Bgra32 which WPF consumes directly.
//            // CloneAs handles RGB→BGR and RGBA→BGRA conversion efficiently.
//            using var bgra = img.CloneAs<Bgra32>();
//            int stride = width * Unsafe.SizeOf<Bgra32>();
//            byte[] pixels = new byte[height * stride];
//            bgra.CopyPixelDataTo(pixels);

//            return CreateFrozenBitmapSource(pixels, width, height, stride, WpfPixelFormats.Bgra32);
//        }
//    }

//    private static BitmapSource CreateFrozenBitmapSource(
//        byte[] pixels, int width, int height, int stride, PixelFormat format)
//    {
//        var result = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
//        result.Freeze();
//        return result;
//    }

//    // -------------------------------------------------------------------------
//    // Helpers
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// Returns the normalised band count (1, 3, or 4) that will be used for export.
//    /// Matches the logic in each Scale path so the switch in Scale() is consistent.
//    /// </summary>
//    private static int NormalisedBandCount(NetVips.Image img) => img.Bands switch
//    {
//        1 => 1, // Grey
//        2 => 1, // Grey+Alpha → flattened to 1
//        3 => 3, // RGB
//        4 => 4, // RGBA
//        _ => img.HasAlpha() ? 4 : 3 // CMYK / 5+ bands → normalised by Flatten
//    };
//}