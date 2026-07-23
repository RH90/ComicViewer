using ComicViewer.ImageProcessing;
using LibHeifSharp;
using Microsoft.VisualBasic.Logging;



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
    public static long imageLoad = 0;
    public static long imageResize = 0;

    // -------------------------------------------------------------------------
    // Format loaders
    // -------------------------------------------------------------------------

    /// <summary>Decodes JPEG, PNG, or WebP directly via libvips.</summary>
    public static Image FromBuffer(byte[] data)
    {
        //return Image.JpegloadBuffer(data);
        Stopwatch sw = Stopwatch.StartNew();

        //Image image = Image.NewFromBuffer(data);
        Image image = Image.NewFromBuffer(data);

        //MainWindow.Log.add($"Vips: Loaded {image.Width}×{image.Height} ({image.Bands} bands), {image.Interpretation}", false);
        sw.Stop();

        imageLoad = sw.ElapsedMilliseconds;

        return image;
        //return Image.NewFromBuffer(data, access: Enums.Access.Sequential);
    }
    //=> 

    /// <summary>
    /// Decodes a JPEG XL file via the native wrapper and wraps the raw pixels
    /// in a NetVips image. Band count is inferred from the actual buffer size
    /// to guard against the wrapper reporting the wrong channel count.
    /// </summary>
    public static Image FromJxl(byte[] data)
    {
        Stopwatch sw = Stopwatch.StartNew();

        var (pixels, w, h, hasAlpha) = JxlDecoder.DecodeInternalRaw(data, new JxlDecodeOptions { Threads = Environment.ProcessorCount });

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

        //using Image imgMem = Image.NewFromMemory(pixels, width, height, bands, Enums.BandFormat.Uchar);
        //Image image = imgMem.Copy(width: width, height: height, bands: bands, interpretation: Enums.Interpretation.Srgb);

        Image image = Image.NewFromMemoryCopy(pixels, width, height, bands, Enums.BandFormat.Uchar).IccTransform("srgb");

        byte[]? iccBytes = JxlDecoder.TryExtractIccProfile(data);
        if (iccBytes != null)
        {
            image = image.Mutate(i => i.Set("icc-profile-data", iccBytes));
        }


        sw.Stop();
        imageLoad = sw.ElapsedMilliseconds;

        return image;
        //return Image.NewFromMemory(pixels, width, height, bands, Enums.BandFormat.Uchar);
    }

    /// <summary>
    /// Decodes the primary image of an AVIF file into a NetVips.Image,
    /// forcing libheif to use the dav1d AV1 decoder plugin when available.
    /// </summary>
    public static Image FromAVIF(byte[] data)
    {
        AVIF.Decode(data, out var pixelData, out int width, out int height, out bool hasAlpha, out int bands, out bool IsPremultipliedAlpha);

        var image = Image.NewFromMemoryCopy(pixelData, width, height, bands, Enums.BandFormat.Uchar);
        image = image.Copy(interpretation: Enums.Interpretation.Srgb);

        // libheif can return premultiplied alpha depending on the source file;
        // NetVips/vips conventions expect straight alpha, so normalize it.
        if (hasAlpha && IsPremultipliedAlpha)
        {
            image = image.Unpremultiply().Cast(Enums.BandFormat.Uchar);
        }

        return image;
    }

    /// <summary>
    /// Finds the dav1d decoder's ID string via libheif's decoder registry,
    /// falling back to the plugin's well-known ID if enumeration isn't available.
    /// </summary>


    /// <summary>
    /// Decodes a JPEG XR file via WIC (Windows Imaging Component).
    /// Uses BitmapCacheOption.OnDemand to avoid retaining a full decoded
    /// copy in WIC's unmanaged cache.
    /// </summary>
    public static Image FromJxr(byte[] data)
    {
        Stopwatch sw = Stopwatch.StartNew();

        int width, height;
        byte[] rgba;
        bool alpha = false;

        using (var stream = new MemoryStream(data))
        {
            WmpBitmapDecoder decoder = new WmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);


            //var decoder = BitmapDecoder.Create(stream,
            //                   BitmapCreateOptions.PreservePixelFormat,
            //                   BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];


            //MainWindow.Log.add("JXR PixelFormat: " + frame.Format, false);

            if (frame.Format == PixelFormats.Rgb24)
            {
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                int stride = width * 3;

                rgba = new byte[height * stride];
                frame.CopyPixels(rgba, stride, 0);
            }
            //else if (frame.Format == PixelFormats.Bgra32)
            //{
            //    width = frame.PixelWidth;
            //    height = frame.PixelHeight;
            //    int stride = width * 4;
            //    rgba = new byte[height * stride];
            //    frame.CopyPixels(rgba, stride, 0);
            //    alpha = true;
            //}
            else
            {
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
                width = converted.PixelWidth;
                height = converted.PixelHeight;
                int stride = width * 3;

                rgba = new byte[height * stride];
                converted.CopyPixels(rgba, stride, 0);
            }
            //var converted = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);



        }

        // WIC outputs BGRA; libvips expects RGB-ordered data → swap R↔B.
        //SwapRedBlue(rgba);
        //return RawToVips(rgba, width, height, hasAlpha: true);

        Image bgra = RawToVips(rgba, width, height, hasAlpha: alpha);
        //Image bgra = ReorderBands(RawToVips(rgba, width, height, hasAlpha: alpha), new[] { 2, 1, 0, 3 });
        //Image reorder = ReorderBands(bgra, new[] { 2, 1, 0, 3 });

        sw.Stop();
        imageLoad = sw.ElapsedMilliseconds;
        return bgra;
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
    public static BitmapSource Scale(Image vipsImage, MainWindow.Scalers scalingAlgo, int aiScale, int targetWidth = 0, int targetHeight = 0, int sharpenLevel = 0)
    {

        Image resized = null!;
        bool didResize = targetWidth > 0 || targetHeight > 0;
        int outW = targetWidth > 0 ? targetWidth : vipsImage.Width;
        int outH = targetHeight > 0 ? targetHeight : vipsImage.Height;

        double hScale = (double)outW / vipsImage.Width;
        double vScale = (double)outH / vipsImage.Height;
        double minScale = Math.Min(hScale, vScale);

        Stopwatch sw = Stopwatch.StartNew();

        //Image temp = vScale < 1 && aiScale == 0 ? vipsImage : ApplyIccProfile(vipsImage);
        Image temp = ApplyIccProfile(vipsImage);

        try
        {

            didResize = (vScale != 0);


            Enums.Kernel kernel = Enums.Kernel.Lanczos3;
            switch (scalingAlgo)
            {
                case MainWindow.Scalers.VipsLanczos3:
                    kernel = Enums.Kernel.Lanczos3;
                    break;
                case MainWindow.Scalers.VipsLanczos2:
                    kernel = Enums.Kernel.Lanczos2;
                    break;
                case MainWindow.Scalers.VipsMitchell:
                    kernel = Enums.Kernel.Mitchell;
                    break;
                case MainWindow.Scalers.VipsCubic:
                    kernel = Enums.Kernel.Cubic;
                    break;
                case MainWindow.Scalers.VipsLinear:
                    kernel = Enums.Kernel.Linear;
                    break;
            }


            if (didResize)
            {
                if ((temp.Bands == 4 || temp.HasAlpha()) && temp.Interpretation != Interpretation.Cmyk)
                {
                    temp = temp.Flatten(background: new double[] { MainWindow.backgroundValue });
                }

                if (scalingAlgo == MainWindow.Scalers.VipsThumb)
                {
                    resized = temp.ThumbnailImage(targetWidth, targetHeight);
                }
                else if (scalingAlgo == MainWindow.Scalers.VipsThumbL)
                {
                    resized = temp.ThumbnailImage(targetWidth, targetHeight, linear: true);
                }
                else
                {

                    if (aiScale > 0)
                    {
                        int newWidth = (int)Math.Round(temp.Width * vScale);
                        Debug.WriteLine("AiScale:" + aiScale);


                        Waifu2xNative.Waifu2xNoiseLevel noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.None;
                        if (scalingAlgo == MainWindow.Scalers.AILow)
                        {
                            noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.Low;
                        }
                        else if (scalingAlgo == MainWindow.Scalers.AIMedium)
                        {
                            noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.Medium;
                        }
                        else if (scalingAlgo == MainWindow.Scalers.AIHigh)
                        {
                            noiseLevel = Waifu2xNative.Waifu2xNoiseLevel.Highest;
                        }
                        using Image waifuIMG = Waifu2xNative.UpscalePng(temp,
                             Waifu2xNative.Waifu2xModel.AnimeStyleArt,
                             noiseLevel,
                             aiScale);


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

                        //vScale = newRatio;
                        //hScale = newRatio;

                        resized = waifuIMG.Resize(newRatio, vscale: newRatio, kernel: Enums.Kernel.Lanczos3);
                    }
                    else
                    {

                        //Sharpen: sigma=radius, m2=amount, x1=threshold
                        //Debug.WriteLine(temp.HasAlpha());
                        if (sharpenLevel > 0 && didResize && vScale != 1)
                        {
                            //Debug.WriteLine("Bands: " + temp.Interpretation
                            double sigma = 1; // default 0.5, radius
                            double m2 = 0.35;
                            if (sharpenLevel == 1)
                            {
                                m2 = 0.28;
                            }
                            else if (sharpenLevel == 2)
                            {
                                m2 = 0.35;
                            }

                            // default 3 , amount (3)
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
                            //MainWindow.Log.add("Bands1: " + temp.Bands + ", " + temp.HasAlpha(), false);
                            kernel = Enums.Kernel.Lanczos2;

                            Debug.WriteLine(temp.Interpretation);
                            Debug.WriteLine(temp.Bands);


                            //if ((temp.Bands == 4 || temp.HasAlpha()) && temp.Interpretation != Interpretation.Cmyk)
                            //{
                            //    temp = temp.Flatten(background: new double[] { MainWindow.backgroundValue });
                            //}

                            //Image img1 = temp.Resize(hScale, vscale: vScale, kernel: kernel);
                            //using Image alpha = img1.ExtractBand(img1.Bands - 1);
                            //img1 = img1.ExtractBand(0, 3);
                            //resized = img1.Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2).Bandjoin(alpha).Cast(Enums.BandFormat.Uchar);

                            //MainWindow.Log.add("Bands2: " + temp.Bands + ", " + temp.HasAlpha() + ", " + temp.Interpretation + ", " + temp.Bands, false);
                            if (temp.Interpretation == Interpretation.Multiband || temp.Interpretation == Interpretation.Rgb16 || temp.Interpretation == Interpretation.Grey16 || temp.Format != Enums.BandFormat.Uchar)
                            {
                                using Image img1 = temp.Resize(hScale, vscale: vScale, kernel: kernel);
                                using Image labs = img1.Colourspace(Interpretation.Lab);
                                using Image sharpenImg = labs.Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2);
                                resized = sharpenImg.Colourspace(Interpretation.Srgb);
                            }
                            else
                            {
                                using Image img1 = temp.Resize(hScale, vscale: vScale, kernel: kernel);
                                resized = img1.Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2);

                                //Image img1 = temp.Resize(hScale, vscale: vScale, kernel: kernel);
                                //using Image alpha = img1.ExtractBand(img1.Bands - 1);
                                //img1 = img1.ExtractBand(0, 3);
                                //resized = img1.Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2).Bandjoin(alpha).Cast(Enums.BandFormat.Uchar);
                            }
                        }
                        else
                        {

                            //MainWindow.Log.add("Bands: " + temp.Interpretation + ", " + temp.Format, false);

                            if (temp.Interpretation == Interpretation.Rgb16 || temp.Interpretation == Interpretation.Grey16 || temp.Format != Enums.BandFormat.Uchar)
                            {
                                //using Image sharpenImg = labs.ThumbnailImage(targetWidth, targetHeight, linear: true).Sharpen(sigma: sigma, m2: m2, x1: x1, y2: y2);
                                using Image sharpenImg = temp.Resize(hScale, vscale: vScale, kernel: kernel);
                                using Image labs = sharpenImg.Colourspace(Interpretation.Lab);
                                resized = labs.Colourspace(Interpretation.Srgb);
                            }
                            else
                            {
                                //resized = temp;
                                resized = temp.Resize(hScale, vscale: vScale, kernel: kernel);
                                //resized = temp.Resize(hScale, vscale: vScale, kernel: Enums.Kernel.Nearest, gap: 999999);
                            }

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
            }

            using Image workImage = didResize ? resized : vipsImage;

            //using Image bgra = vScale < 1 && aiScale == 0 ? ToBgraNoIcc(ApplyIccProfile(workImage)) : ToBgraNoIcc(workImage);
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

            sw.Stop();
            imageResize = sw.ElapsedMilliseconds;

            return result;
        }
        finally
        {
            // Only dispose the resized intermediate — the caller owns vipsImage.

            vipsImage?.Dispose();
            resized?.Dispose();
            resized?.Dispose();
            //System.GC.Collect();
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
            Image flat = src.Flatten(background: new double[] { MainWindow.backgroundValue });
            if (ownsSrc) src.Dispose();
            src = flat;
            ownsSrc = true;
        }

        try
        {
            return src.Bands switch
            {
                1 => src.Copy(),
                2 => src.Flatten(background: new double[] { MainWindow.backgroundValue }),
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
        Image src = didFlatten ? img.Flatten(background: new double[] { MainWindow.backgroundValue }) : img;
        //MainWindow.Log.add("Bands: " + src.Bands, false);
        try
        {
            return src.Bands switch
            {
                1 => src.Copy(),
                2 => src.Flatten(background: new double[] { MainWindow.backgroundValue }),
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
            //MainWindow.Log.add("ICC profile type: " + img.GetTypeOf("icc-profile-data"), false);
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
        => Image.NewFromMemoryCopy(pixels, width, height,
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
        Image src = img.Bands > 4 ? img.Flatten(background: new double[] { MainWindow.backgroundValue }) : img;
        bool ownsSrc = img.Bands > 4;

        try
        {
            return src.Bands switch
            {
                // Greyscale — pass through as-is.
                1 => src.Copy(),

                // Greyscale + alpha — flatten onto white, output as 1-band.
                2 => src.Flatten(background: new double[] { MainWindow.backgroundValue }),

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

}