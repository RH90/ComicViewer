using LibHeifSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace ComicViewer.ImageProcessing
{
    public static class AVIF
    {

        public static void Decode(byte[] avifData, out byte[] pixelData, out int width, out int height, out bool hasAlpha, out int bands, out bool IsPremultipliedAlpha)
        {
            using var context = new HeifContext(avifData);
            using var imageHandle = context.GetPrimaryImageHandle();

            var decodingOptions = new HeifDecodingOptions
            {
                DecoderId = GetDav1dDecoderId()
            };

            hasAlpha = imageHandle.HasAlphaChannel;
            int bitDepth = imageHandle.BitDepth;


            // Keep this to the common 8-bit case; HDR (10/12-bit) AVIFs would need
            // the 16-bit interleaved chromas (Rgba64/Rgb48) and a separate copy path.
            var chroma = hasAlpha ? HeifChroma.InterleavedRgba32 : HeifChroma.InterleavedRgb24;
            if (bitDepth != 8)
            {
                decodingOptions.ConvertHdrToEightBit = true;
            }

            using var heifImage = imageHandle.Decode(HeifColorspace.Rgb, chroma, decodingOptions);

            width = heifImage.Width;
            height = heifImage.Height;
            bands = hasAlpha ? 4 : 3;

            var plane = heifImage.GetPlane(HeifChannel.Interleaved);
            int srcStride = plane.Stride;
            int rowBytes = width * bands;


            //MainWindow.Log.add($"AVIF: hasAlpha={hasAlpha}, bitDepth={bitDepth}, width={imageHandle.Width}, height={imageHandle.Height}", false);
            // libheif rows can be padded to a larger stride than width * bands,
            // so pack into a tight buffer before handing it to vips.
            pixelData = new byte[rowBytes * height];

            unsafe
            {
                byte* src = (byte*)plane.Scan0;
                fixed (byte* destBase = pixelData)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + (long)y * srcStride,
                            destBase + (long)y * rowBytes,
                            rowBytes,
                            rowBytes);
                    }
                }
            }
            IsPremultipliedAlpha = imageHandle.IsPremultipliedAlpha;
        }

        private static string GetDav1dDecoderId()
        {
            if (LibHeifInfo.HaveVersion(1, 15, 0))
            {
                foreach (var descriptor in LibHeifInfo.GetDecoderDescriptors(HeifCompressionFormat.Av1))
                {
                    if (descriptor.IdName.Equals("dav1d", StringComparison.OrdinalIgnoreCase))
                    {
                        return descriptor.IdName;
                    }
                }
            }

            // Ignored on libheif < 1.15.0; on newer versions this still works if
            // the dav1d plugin is registered but wasn't listed for some reason.
            return "dav1d";
        }

    }
}
