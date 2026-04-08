using ComicViewer.ImageProcessing;


//using ImageMagick;
using NetVips;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using static NetVips.Enums;
using static System.Resources.ResXFileRef;

namespace ComicViewer;

public static class VipsImageFactory
{
    // -------------------------------------------------------------------------
    // Format loaders
    // -------------------------------------------------------------------------

    /// <summary>Decodes JPEG, PNG, or WebP directly via libvips.</summary>
    public static Image FromBuffer(byte[] data)
        => Image.NewFromBuffer(data, access: Enums.Access.Sequential);

    /// <summary>
    /// Decodes a JPEG XL file via the native wrapper and wraps the raw pixels
    /// in a NetVips image. Band count is inferred from the actual buffer size
    /// to guard against the wrapper reporting the wrong channel count.
    /// </summary>
    public static Image FromJxl(byte[] data)
    {
        var (pixels, w, h, _) = JxlDecoder.DecodeInternalRaw(data, new JxlDecodeOptions { Threads = Environment.ProcessorCount });

        int width = (int)w;
        int height = (int)h;
        int total = pixels.Length;

        // Detect silent failure — wrapper returned encoded data unchanged.
        if (total == data.Length)
            throw new InvalidOperationException(
                "JXL decode returned the same byte count as the input. " +
                "The native wrapper likely failed silently — check that libjxl.dll is present.");

        int bands = total / (width * height);

        if (bands is not (1 or 3 or 4) || bands * width * height != total)
            throw new InvalidOperationException(
                $"JXL decode produced an unexpected buffer size: {total} bytes for " +
                $"{width}×{height} (implies {(double)total / (width * height):F2} bands).");

        return Image.NewFromMemory(pixels, width, height, bands, Enums.BandFormat.Uchar);
    }

    /// <summary>
    /// Decodes a JPEG XR file via WIC (Windows Imaging Component).
    /// Uses BitmapCacheOption.OnDemand to avoid retaining a full decoded
    /// copy in WIC's unmanaged cache.
    /// </summary>
    public static Image FromJxr(byte[] data)
    {
        int width, height;
        byte[] rgba;

        using (var stream = new MemoryStream(data, writable: false))
        {
            var decoder = BitmapDecoder.Create(stream,
                               BitmapCreateOptions.PreservePixelFormat,
                               BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            width = converted.PixelWidth;
            height = converted.PixelHeight;
            int stride = width * 4;

            rgba = new byte[height * stride];
            converted.CopyPixels(rgba, stride, 0);
        }

        // WIC outputs BGRA; libvips expects RGB-ordered data → swap R↔B.
        SwapRedBlue(rgba);
        return RawToVips(rgba, width, height, hasAlpha: true);
    }

    // -------------------------------------------------------------------------
    // Scale
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resizes a NetVips image and returns a frozen WPF BitmapSource.
    /// Kernel is chosen based on the operation:
    ///   - Upscale or minor resize  → Lanczos3  (sharpest detail)
    ///   - Moderate downscale       → Mitchell  (balanced sharpness/ringing)
    ///   - Heavy downscale (≤50%)   → Linear    (fastest, avoids aliasing)
    /// </summary>
    public static BitmapSource Scale(Image vipsImage, Enums.Kernel scalingAlgo, int targetWidth = 0, int targetHeight = 0, int sharpenLevel = 0)
    {
        Image? temp = null;
        Image resized = null!;
        bool didResize = targetWidth > 0 || targetHeight > 0;

        try
        {
            //using Image temp = ToBgra(vipsImage);
            temp = ApplyIccProfile(vipsImage);
            int outW = targetWidth > 0 ? targetWidth : temp.Width;
            int outH = targetHeight > 0 ? targetHeight : temp.Height;

            double hScale = (double)outW / temp.Width;
            double vScale = (double)outH / temp.Height;
            double minScale = Math.Min(hScale, vScale);

            didResize = (vScale != 0);


            if (didResize)
            {
                if (scalingAlgo == (Enums.Kernel)12)
                {
                    resized = temp.ThumbnailImage(targetWidth, targetHeight);
                }
                else if (scalingAlgo == (Enums.Kernel)13)
                {
                    resized = temp.ThumbnailImage(targetWidth, targetHeight, linear: true);
                }
                else
                {

                    if (temp.Width < System.Windows.SystemParameters.PrimaryScreenWidth &&
                        (scalingAlgo == (Enums.Kernel)20 ||
                        scalingAlgo == (Enums.Kernel)21 ||
                        scalingAlgo == (Enums.Kernel)22 ||
                        scalingAlgo == (Enums.Kernel)23))
                    {
                        int AiScale = 2;

                        if (vScale > 2.7)
                        {
                            AiScale = 4;
                        }
                        int newWidth = (int)Math.Round(temp.Width * vScale);
                        Debug.WriteLine("AiScale:" + AiScale);


                        Waifu2xNative.Waifu2xNoiseLevel noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.None;
                        if (scalingAlgo == (Enums.Kernel)21)
                        {
                            noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.Low;
                        }
                        else if (scalingAlgo == (Enums.Kernel)22)
                        {
                            noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.Medium;
                        }
                        else if (scalingAlgo == (Enums.Kernel)23)
                        {
                            noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.Highest;
                        }
                        using Image waifuIMG = Waifu2xNative.UpscalePng(temp,
                             Waifu2xNative.Waifu2xModel.AnimeStyleArt,
                             noiseLevel,
                             AiScale);


                        //Waifu2xNativeOld.Waifu2xNoiseLevel noiseLevel = Waifu2xNativeOld.Waifu2xNoiseLevel.None;
                        //if (scalingAlgo == (Enums.Kernel)21)
                        //{
                        //    noiseLevel = Waifu2xNativeOld.Waifu2xNoiseLevel.Low;
                        //}
                        //else if (scalingAlgo == (Enums.Kernel)22)
                        //{
                        //    noiseLevel = Waifu2xNativeOld.Waifu2xNoiseLevel.Medium;
                        //}
                        //else if (scalingAlgo == (Enums.Kernel)23)
                        //{
                        //    noiseLevel = Waifu2xNativeOld.Waifu2xNoiseLevel.Highest;
                        //}

                        //vipsImage = Waifu2xNativeOld.UpscalePng(vipsImage,
                        //    Waifu2xNativeOld.Waifu2xModel.AnimeStyleArt,
                        //    noiseLevel,
                        //    AiScale);

                        int width2 = waifuIMG.Width;

                        double newRatio = (double)newWidth / (double)width2;
                        //Debug.WriteLine("newRatio: " + newRatio);
                        scalingAlgo = Enums.Kernel.Lanczos3;

                        vScale = newRatio;
                        hScale = newRatio;



                        resized = waifuIMG.Resize(hScale, vscale: vScale, kernel: scalingAlgo);


                    }
                    else
                    {
                        //Sharpen: sigma=radius, m2=amount, x1=threshold
                        //Debug.WriteLine(temp.HasAlpha());
                        if (sharpenLevel > 0 && didResize && vScale != 1)
                        {
                            //Debug.WriteLine("Bands: " + temp.Interpretation
                            double sigma = 1.5; // default 0.5, radius
                            double m2 = 0.4; // default 3 , amount (3)
                            double y2 = 3; // default 10 , maximum amount of brightening (2.5)
                            double x1 = 2; // default 2 threshold , higher = sharpen only stronger edges

                            double m1 = 0; // default 0 
                            double y3 = 20; // default 20


                            //double sigma = 1.5; // default 0.5, radius
                            //double m2 = 0.5; // default 3 , amount
                            //double y2 = 4; // default 10 , maximum amount of brightening
                            //double x1 = 0; // default 2 threshold

                            //double m1 = 0; // default 0 
                            //double y3 = 20; // default 20
                            //MainWindow.Log.add("Bands: " + temp.Interpretation, false);
                            scalingAlgo = Enums.Kernel.Lanczos2;

                            if (temp.Interpretation == Interpretation.Multiband)
                            {
                                using Image labs = temp.Colourspace(Interpretation.Labs);
                                //using Image sharpenImg = labs.ThumbnailImage(targetWidth, targetHeight, linear: true).Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2);
                                using Image sharpenImg = labs.Resize(hScale, vscale: vScale, kernel: scalingAlgo).Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2);
                                resized = sharpenImg.Colourspace(Interpretation.Srgb);
                            }
                            else
                            {
                                resized = temp.Resize(hScale, vscale: vScale, kernel: scalingAlgo).Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2);
                            }

                        }
                        else
                        {
                            resized = temp.Resize(hScale, vscale: vScale, kernel: scalingAlgo);
                        }

                    }

                    //if (vipsImage.HasAlpha())
                    //{
                    //    using Image premul = vipsImage.Premultiply();
                    //    using Image resizedPremul = premul.Resize(hScale, vscale: vScale, kernel: scalingAlgo);
                    //    using Image unpremul = resizedPremul.Unpremultiply();
                    //    resized = unpremul.Cast(Enums.BandFormat.Uchar);
                    //}
                    //else
                    //{

                    //}
                }



                //Debug.WriteLine(resized.Width);
                //Debug.WriteLine(vipsImage.Width);
                //resized = vipsImage.Resize(hScale, vscale: vScale, kernel: scalingAlgo);
                //vipsImage = vipsImage.Cast(Enums.BandFormat.Uchar);
                //resized = vipsImage.Resize(hScale, vscale: vScale, kernel: scalingAlgo);
                //}
                //}



                // Resize in linear light — correct blending, better highlight preservation
                //Image linear = vipsImage.Colourspace(Enums.Interpretation.Lab);
                //resized = vipsImage.Resize(hScale, vscale: vScale, kernel: scalingAlgo);
                //resized = resized.Colourspace(Enums.Interpretation.Srgb);


                // Cast back to uchar — Unpremultiply outputs float, WriteToMemory expects uchar.

                // Convert back to sRGB for display

            }

            //using Image workImage = didResize ? resized : vipsImage;

            //byte[] pixels = workImage.WriteToMemory<byte>();
            //int stride = workImage.Width * workImage.Bands;

            //PixelFormat pixelFormat = workImage.Bands switch
            //{
            //    1 => PixelFormats.Gray8,
            //    3 => PixelFormats.Bgr24,
            //    4 => PixelFormats.Bgra32,
            //    _ => throw new NotSupportedException($"Unexpected band count: {bgra.Bands}")
            //};

            //var result = BitmapSource.Create(
            //    workImage.Width, workImage.Height,
            //    96, 96,
            //    pixelFormat, null,
            //    pixels, stride);
            //result.Freeze();

            using Image workImage = didResize ? resized : vipsImage;

            using Image bgra = ToBgraNoIcc(workImage);

            byte[] pixels = bgra.WriteToMemory<byte>();
            int stride = bgra.Width * bgra.Bands;

            PixelFormat pixelFormat = bgra.Bands switch
            {
                1 => PixelFormats.Gray8,
                3 => PixelFormats.Bgr24,
                4 => PixelFormats.Bgra32,
                _ => throw new NotSupportedException($"Unexpected band count: {bgra.Bands}")
            };

            var result = BitmapSource.Create(
                bgra.Width, bgra.Height,
                96, 96,
                pixelFormat, null,
                pixels, stride);
            result.Freeze();

            return result;
        }
        finally
        {
            // Only dispose the resized intermediate — the caller owns vipsImage.

            vipsImage?.Dispose();
            resized?.Dispose();
            if (temp != null && temp != vipsImage)
                temp.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts any NetVips image to a WPF-compatible band order,
    /// applying any embedded ICC profile to convert to sRGB first.
    /// Without this, wide-gamut (P3, AdobeRGB) images appear washed out
    /// and images with unusual profiles show wrong colours entirely.
    /// </summary>
    public static Image ToBgra(Image img)
    {
        // ── 1. Apply embedded ICC profile → convert to sRGB ──────────────────
        // If the image has an embedded profile (e.g. AdobeRGB, P3, CMYK),
        // IccTransform converts it to sRGB so WPF displays it correctly.
        // If there is no profile, libvips assumes sRGB and this is a no-op.

        Image src = ApplyIccProfile(img);
        bool ownsSrc = src != img;

        //Image src = (img);
        //bool ownsSrc = src != img;
        // ── 2. Collapse exotic band counts ───────────────────────────────────
        if (src.Bands > 4)
        {
            Image flat = src.Flatten();
            if (ownsSrc) src.Dispose();
            src = flat;
            ownsSrc = true;
        }

        try
        {
            return src.Bands switch
            {
                1 => src.Copy(),
                2 => src.Flatten(background: new double[] { 255 }),
                3 => ReorderBands(src, 2, 1, 0),
                4 => ReorderBands(src, 2, 1, 0, 3),
                _ => throw new NotSupportedException($"Unexpected band count: {src.Bands}")
            };
        }
        finally
        {
            if (ownsSrc) src.Dispose();
        }
    }
    /// <summary>
    /// Converts to WPF-compatible band order WITHOUT applying ICC profile.
    /// Call this only after ApplyIccProfile has already been called.
    /// </summary>
    private static Image ToBgraNoIcc(Image img)
    {
        bool didFlatten = img.Bands > 4;
        Image src = didFlatten ? img.Flatten() : img;

        try
        {
            return src.Bands switch
            {
                1 => src.Copy(),
                2 => src.Flatten(background: new double[] { 255 }),
                3 => ReorderBands(src, 2, 1, 0),
                4 => ReorderBands(src, 2, 1, 0, 3),
                _ => throw new NotSupportedException($"Unexpected band count: {src.Bands}")
            };
        }
        finally
        {
            if (didFlatten) src.Dispose();
        }
    }


    /// <summary>
    /// Applies the embedded ICC profile to convert the image to sRGB.
    /// Returns the original image unchanged if no profile is present.
    /// </summary>
    private static Image ApplyIccProfile(Image img)
    {
        try
        {
            // Check if the image has an embedded ICC profile.
            // get_typeof returns 0 if the field does not exist.
            if (img.GetTypeOf("icc-profile-data") == 0)
            {
                Debug.WriteLine("No embedded ICC profile found; skipping colour transform.");
                return img;
            }


            // IccTransform converts from the embedded profile to the target.
            // "srgb" = standard sRGB, which is what BitmapSource/WPF expects.
            // PCS = perceptual rendering intent — best for display/photos.
            // embedded = true means use the profile attached to the image.
            return img.IccTransform(
                "srgb",
                pcs: Enums.PCS.Xyz,
                intent: Enums.Intent.Perceptual,
                embedded: true);
        }
        catch
        {
            // If the profile is malformed or the transform fails,
            // fall back to treating the image as sRGB unchanged.
            return img;
        }
    }

    /// <summary>
    /// Extracts bands in the given order and joins them into a new image.
    /// Every extracted band image is disposed after the join.
    /// </summary>
    private static Image ReorderBands(Image src, params int[] order)
    {
        var bands = new Image[order.Length];
        try
        {
            for (int i = 0; i < order.Length; i++)
                bands[i] = src[order[i]];

            // Bandjoin takes ownership of bands[1..]; dispose bands[0] ourselves.
            Image head = bands[0];
            bands[0] = null!;
            using (head)
                return head.Bandjoin(bands[1..]);
        }
        finally
        {
            foreach (var b in bands)
                b?.Dispose();
        }
    }

    public static Image RawToVips(byte[] pixels, int width, int height, bool hasAlpha)
        => Image.NewFromMemory(pixels, width, height,
               bands: hasAlpha ? 4 : 3,
               format: Enums.BandFormat.Uchar);

    /// <summary>Swaps R and B channels of an interleaved BGRA buffer in-place.</summary>
    public static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    public static BitmapSource ConvertToBgra32(BitmapSource src)
        => src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);


    // ... all existing loaders and helpers stay unchanged ...

    /// <summary>
    /// Resizes a NetVips image using ImageMagick and returns a frozen WPF BitmapSource.
    /// NetVips handles decoding (shrink-on-load, format support).
    /// ImageMagick handles resampling.
    /// </summary>
    /// <param name="vipsImage">Decoded source image from any NetVips loader.</param>
    /// <param name="filter">ImageMagick resampling filter. Defaults to Lanczos.</param>
    /// <param name="targetWidth">Target width in pixels; 0 = preserve source width.</param>
    /// <param name="targetHeight">Target height in pixels; 0 = preserve source height.</param>
    /// <param name="keepAspectRatio">
    /// If true, fits within the target box preserving aspect ratio.
    /// If false, stretches to exact dimensions.
    /// </param>
    //public static BitmapSource Scale(
    //    Image vipsImage,
    //    FilterType filter = FilterType.Lanczos,
    //    int targetWidth = 0,
    //    int targetHeight = 0,
    //    bool keepAspectRatio = true)
    //{
    //    // ── 1. Export raw pixels from NetVips ─────────────────────────────
    //    // Flatten exotic band counts before export so ImageMagick always
    //    // receives a known layout (Gray8, Bgr24, or Bgra32).
    //    using Image normalised = NormaliseForExport(vipsImage);

    //    byte[] pixels = normalised.WriteToMemory();
    //    int width = normalised.Width;
    //    int height = normalised.Height;
    //    int bands = normalised.Bands;

    //    var (magickStorage, magickColorspace, hasAlpha) = BandsToMagickFormat(bands);

    //    // ── 2. Wrap raw pixels in a MagickImage ───────────────────────────
    //    // ReadPixels ingests the raw interleaved buffer directly — no encode/
    //    // decode round-trip, no temp file.
    //    var readSettings = new PixelReadSettings(
    //         (uint)width, (uint)height,
    //         magickStorage,
    //         bands == 1 ? "R" : (hasAlpha ? "BGRA" : "BGR"));

    //    using var magick = new MagickImage();
    //    magick.ReadPixels(pixels, readSettings);
    //    magick.ColorSpace = magickColorspace;
    //    magick.HasAlpha = hasAlpha;

    //    // ── 3. Resize with ImageMagick ────────────────────────────────────
    //    if (targetWidth > 0 || targetHeight > 0)
    //    {
    //        uint outW = targetWidth > 0 ? (uint)targetWidth : (uint)width;
    //        uint outH = targetHeight > 0 ? (uint)targetHeight : (uint)height;

    //        var geometry = new MagickGeometry(outW, outH)
    //        {
    //            // IgnoreAspectRatio=false preserves ratio; true stretches to exact size.
    //            IgnoreAspectRatio = !keepAspectRatio
    //        };

    //        magick.FilterType = filter;
    //        magick.Resize(geometry);
    //    }

    //    // ── 4. Export pixels from ImageMagick ─────────────────────────────
    //    return MagickToBitmapSource(magick, hasAlpha);
    //}

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Normalises a NetVips image to a known band count (1, 3, or 4) suitable
    /// for direct export to ImageMagick. Does NOT reorder RGB→BGR — ImageMagick
    /// handles the mapping via <see cref="PixelMapping"/>.
    /// </summary>
    private static Image NormaliseForExport(Image img)
    {
        // Collapse CMYK / 5+ bands to RGB/RGBA first.
        Image src = img.Bands > 4 ? img.Flatten() : img;
        bool ownsSrc = img.Bands > 4;

        try
        {
            return src.Bands switch
            {
                // Greyscale — pass through as-is.
                1 => src.Copy(),

                // Greyscale + alpha — flatten onto white, output as 1-band.
                2 => src.Flatten(background: new double[] { 255 }),

                // RGB or RGBA — pass through; PixelMapping tells ImageMagick
                // the channel order so no manual band swap needed.
                3 => src.Copy(),
                4 => src.Copy(),

                _ => throw new NotSupportedException($"Unexpected band count: {src.Bands}")
            };
        }
        finally
        {
            if (ownsSrc) src.Dispose();
        }
    }

    /// <summary>
    /// Maps band count to the ImageMagick storage type, colourspace, and alpha flag.
    /// </summary>
    //private static (StorageType Storage, ColorSpace Colorspace, bool HasAlpha)
    //    BandsToMagickFormat(int bands) => bands switch
    //    {
    //        1 => (StorageType.Char, ColorSpace.Gray, false),
    //        3 => (StorageType.Char, ColorSpace.sRGB, false),
    //        4 => (StorageType.Char, ColorSpace.sRGB, true),
    //        _ => throw new NotSupportedException($"Unexpected band count: {bands}")
    //    };

    ///// <summary>
    ///// Exports an ImageMagick image to a frozen WPF BitmapSource by reading
    ///// its raw pixels directly — no encode/decode round-trip.
    ///// </summary>
    //private static BitmapSource MagickToBitmapSource(MagickImage magick, bool hasAlpha)
    //{
    //    int width = (int)magick.Width;
    //    int height = (int)magick.Height;

    //    PixelFormat wpfFormat;
    //    byte[] pixels;
    //    int stride;

    //    if (magick.ColorSpace == ColorSpace.Gray)
    //    {
    //        using var pixelCollection = magick.GetPixels();
    //        pixels = pixelCollection.ToByteArray("R")
    //                 ?? throw new InvalidOperationException("MagickImage pixel export returned null.");
    //        stride = width;
    //        wpfFormat = PixelFormats.Gray8;
    //    }
    //    else if (hasAlpha)
    //    {
    //        using var pixelCollection = magick.GetPixels();
    //        pixels = pixelCollection.ToByteArray("BGRA")
    //                 ?? throw new InvalidOperationException("MagickImage pixel export returned null.");
    //        stride = width * 4;
    //        wpfFormat = PixelFormats.Bgra32;
    //    }
    //    else
    //    {
    //        using var pixelCollection = magick.GetPixels();
    //        pixels = pixelCollection.ToByteArray("BGR")
    //                 ?? throw new InvalidOperationException("MagickImage pixel export returned null.");
    //        stride = width * 3;
    //        wpfFormat = PixelFormats.Bgr24;
    //    }

    //    var result = BitmapSource.Create(
    //        width, height,
    //        96, 96,
    //        wpfFormat, null,
    //        pixels, stride);

    //    result.Freeze();
    //    return result;
    //}
    //public static Image LoadPngToVips(MemoryStream stream)
    //    => Image.NewFromBuffer(stream.ToArray(), access: Enums.Access.Sequential);
}