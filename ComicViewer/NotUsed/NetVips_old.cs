//using NetVips;
//using System;
//using System.IO;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;

///// <summary>
///// Supported image formats for <see cref="VipsImageFactory"/>.
///// </summary>
///// C
///// 
//namespace ComicViewer.ImageProcessing;

///// <summary>
///// Creates <see cref="NetVips.Image"/> instances from raw byte data.
///// Natively supported formats (JPEG, PNG, WebP) go straight through libvips.
///// JXL is decoded via a native wrapper then handed to libvips as raw pixels.
///// JXR is decoded via WIC (Windows Imaging Component) then handed to libvips as raw pixels.
///// </summary>
//public static class VipsImageFactory
//{
//    public static Image FromJxl(byte[] data)
//    {
//        var b = JxlDecoder.DecodeInternalRaw(data, new JxlDecodeOptions() { Threads = Environment.ProcessorCount });

//        int width = (int)b.Item2;
//        int height = (int)b.Item3;
//        int pixels = b.Item1.Length;
//        int expectedPixels = width * height;

//        // Infer band count from actual buffer size rather than trusting HasAlpha,
//        // which prevents the "memory area too small" error when the wrapper
//        // reports the wrong channel count.
//        int bands = pixels / expectedPixels;

//        return Image.NewFromMemory(
//            b.Item1,
//            width,
//            height,
//            bands,
//            Enums.BandFormat.Uchar);
//    }
//    public static Image FromJxr(byte[] data)
//    {
//        int width, height;
//        byte[] rgba;

//        // Stream must stay open for the lifetime of the decoder when using
//        // OnDemand — WIC reads directly from it rather than caching a full
//        // decoded copy in unmanaged memory like OnLoad does.
//        using (var stream = new MemoryStream(data, writable: false))
//        {
//            BitmapDecoder decoder = BitmapDecoder.Create(
//                stream,
//                BitmapCreateOptions.PreservePixelFormat,
//                BitmapCacheOption.OnDemand); // ← no unmanaged cache retained

//            BitmapFrame frame = decoder.Frames[0];

//            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

//            width = converted.PixelWidth;
//            height = converted.PixelHeight;
//            int stride = width * 4;

//            rgba = new byte[height * stride];
//            converted.CopyPixels(rgba, stride, 0);

//            // Stream disposed here — after CopyPixels, safe to close.
//        }

//        // Swap BGRA → RGBA in-place before handing off to libvips.
//        SwapRedBlue(rgba);

//        // NewFromMemory copies rgba into libvips-managed memory, so the
//        // managed array becomes eligible for GC as soon as this returns.
//        return RawToVips(rgba, width, height, hasAlpha: true);
//    }
//    public static Image RawToVips(byte[] pixels, int width, int height, bool hasAlpha)
//    => Image.NewFromMemory(
//        pixels,
//        width,
//        height,
//        bands: hasAlpha ? 4 : 3,
//        format: Enums.BandFormat.Uchar);
//    public static void SwapRedBlue(byte[] pixels)
//    {
//        for (int i = 0; i < pixels.Length; i += 4)
//            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
//    }
//    public static BitmapSource ConvertToBgra32(BitmapSource src)
//    => src.Format == PixelFormats.Bgra32
//        ? src
//        : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
//    public static Image LoadPngToVips(MemoryStream stream)
//    {
//        byte[] buffer = stream.ToArray();
//        return Image.NewFromBuffer(buffer, access: Enums.Access.Sequential);
//    }

//    /// <summary>
//    /// Converts any NetVips image to a WPF-compatible pixel format.
//    /// - 1 band  → Gray8   (no reordering needed)
//    /// - 3 bands → Bgr24   (R↔B swap)
//    /// - 4 bands → Bgra32  (R↔B swap, alpha preserved)
//    /// - 5+ bands → flattened to 4 bands first, then Bgra32
//    /// </summary>
//    public static Image ToBgra(Image img)
//    {
//        // Collapse anything exotic (CMYK, extra alpha, etc.) down to RGB/RGBA first.
//        Image src = img.Bands > 4 ? img.Flatten() : img;

//        return src.Bands switch
//        {
//            // Grayscale — libvips already outputs a single contiguous byte per pixel,
//            // which maps directly to WPF's Gray8 format. No band manipulation needed.
//            1 => src.Copy(),

//            // RGB → BGR
//            3 => src[2].Bandjoin(new[] { src[1], src[0] }),

//            // RGBA → BGRA
//            4 => src[2].Bandjoin(new[] { src[1], src[0], src[3] }),

//            _ => throw new NotSupportedException($"Unexpected band count after flatten: {src.Bands}")
//        };
//    }
//    public static BitmapSource Scale(NetVips.Image vipsImage, int targetWidth = 0, int targetHeight = 0)
//    {
//        //Image vipsImage = Image.NewFromBuffer(buffer, access: Enums.Access.Sequential);

//        // Optional resize
//        if (targetWidth > 0 || targetHeight > 0)
//        {
//            int outW = targetWidth > 0 ? targetWidth : vipsImage.Width;
//            int outH = targetHeight > 0 ? targetHeight : vipsImage.Height;

//            double hScale = (double)outW / vipsImage.Width;
//            double vScale = (double)outH / vipsImage.Height;

//            if (vScale > 0.8)
//            {
//                vipsImage = vipsImage.Resize(hScale, vscale: vScale, kernel: Enums.Kernel.Lanczos3);
//            }
//            else
//            {
//                vipsImage = vipsImage.Resize(hScale, vscale: vScale, kernel: Enums.Kernel.Lanczos2);
//            }

//        }

//        // Ensure we have BGRA (WPF's native format)
//        //using Image bgra = vipsImage.Bands == 4
//        //    ? vipsImage.Copy()                                // already has alpha
//        //    : vipsImage.Bandjoin(255);

//        // add opaque alpha if missing
//        using Image bgra = ToBgra(vipsImage);
//        byte[] pixels = bgra.WriteToMemory();
//        int stride = bgra.Width * bgra.Bands;

//        System.Windows.Media.PixelFormat pixelFormat = bgra.Bands switch
//        {
//            1 => PixelFormats.Gray8,
//            3 => PixelFormats.Bgr24,
//            4 => PixelFormats.Bgra32,
//            _ => throw new NotSupportedException($"Unexpected band count: {bgra.Bands}")
//        };

//        var bitmapSource = BitmapSource.Create(
//            bgra.Width, bgra.Height,
//            96, 96,
//            pixelFormat,
//            null,
//            pixels,
//            stride);

//        bitmapSource.Freeze();
//        return bitmapSource;
//    }

//}