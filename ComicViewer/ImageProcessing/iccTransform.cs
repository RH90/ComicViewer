using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// Applies an ICC "matrix/TRC" RGB profile transform to sRGB, in pure managed
/// code with no image library dependency (no NetVips, no MagicScaler, no WIC).
///
/// SCOPE / LIMITATIONS (read before using):
/// ─────────────────────────────────────────
/// This supports the common case: RGB profiles built from a 3x3 colorant
/// matrix (rXYZ/gXYZ/bXYZ tags) plus three per-channel tone curves
/// (rTRC/gTRC/bTRC tags). This covers the overwhelming majority of profiles
/// you will actually encounter: sRGB, Adobe RGB (1998), Display P3,
/// ProPhoto RGB, and most camera/monitor-generated ICC profiles.
///
/// It does NOT support LUT-based profiles (AToB0/AToB1/BToA* tags), which
/// are common in CMYK workflows and some high-end monitor profiles. Those
/// require full N-dimensional CLUT interpolation — a much larger undertaking
/// than a matrix/TRC transform — and this class will throw a clear
/// NotSupportedException if it encounters one rather than silently producing
/// wrong colors.
///
/// There is no rendering-intent handling or black-point compensation; this
/// always performs a direct relative-colorimetric-style matrix transform,
/// which is the standard (and only meaningful) approach for matrix/TRC
/// profiles — rendering intent only meaningfully affects LUT-based profiles.
///
/// The Bradford D50→D65 adaptation matrix and sRGB reference matrices below
/// are the standard published color-science constants used throughout the
/// industry (matching Bruce Lindbloom's reference tables and lcms2's
/// internal constants) — not anything specific to this codebase.
///
/// PERFORMANCE
/// ─────────────
///   • Per-pixel math runs in float, not double (parsing/setup still uses
///     double for precision; only the hot loop is narrowed).
///   • Rows are processed in parallel via Parallel.For for images above a
///     small size threshold.
///   • Parsed profiles (LUTs + combined matrix) are cached by content hash,
///     so repeated calls with the same embedded ICC profile (e.g. batch
///     processing many images from the same camera/pipeline) skip re-parsing
///     entirely after the first call.
/// </summary>
public static class IccSrgbTransform
{
    // Number of samples in the sRGB linear->encoded lookup table.
    // 4096 gives sub-quarter-level accuracy after rounding to 8-bit; the
    // table is built once per unique profile and reused for every pixel.
    private const int EncodeLutSize = 4096;

    // Below this pixel count, Parallel.For's scheduling overhead isn't worth
    // it — a single thread finishes small images faster than spinning up
    // worker tasks. ~256x256.
    private const long ParallelThresholdPixels = 65536;

    // ── Profile cache ─────────────────────────────────────────────────────
    //
    // Keyed by a fast content hash of the raw ICC bytes. Collisions are
    // vanishingly unlikely with a 64-bit hash, but we still verify the
    // cached entry's original bytes match before trusting it, so a
    // collision degrades to "re-parse" rather than "silently wrong colors".
    private static readonly ConcurrentDictionary<ulong, (byte[] Bytes, ParsedProfile Profile)> s_profileCache = new();

    // ── Public entry points ─────────────────────────────────────────────

    /// <summary>
    /// Transforms tightly packed 8-bit RGB or RGBA pixel data from the color
    /// space described by <paramref name="iccProfile"/> into sRGB, in place.
    /// Alpha (if present) is left untouched.
    /// </summary>
    /// <param name="pixels">
    ///   Tightly packed, interleaved 8-bit pixel data (RGB or RGBA).
    ///   Modified in place.
    /// </param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="channels">3 for RGB, 4 for RGBA.</param>
    /// <param name="iccProfile">Raw ICC profile bytes (e.g. from JxlDecoder.TryExtractIccProfile).</param>
    /// <param name="useParallel">
    ///   When true (default), rows are processed across multiple threads for
    ///   images above a small internal size threshold. Set false to force
    ///   single-threaded execution (e.g. if you are already parallelizing
    ///   at a higher level across many images).
    /// </param>
    /// <exception cref="NotSupportedException">
    ///   Thrown if the profile is not a matrix/TRC RGB profile (e.g. it is
    ///   LUT-based, or is a CMYK/Gray/Lab-PCS profile).
    /// </exception>
    public static void TransformToSrgbInPlace(
        byte[] pixels, int width, int height, int channels, byte[] iccProfile,
        bool useParallel = true)
    {
        if (pixels is null) throw new ArgumentNullException(nameof(pixels));
        if (iccProfile is null) throw new ArgumentNullException(nameof(iccProfile));
        if (channels != 3 && channels != 4)
            throw new ArgumentException("channels must be 3 (RGB) or 4 (RGBA).", nameof(channels));

        long expected = (long)width * height * channels;
        if (pixels.LongLength < expected)
            throw new ArgumentException(
                $"Pixel buffer too small: expected at least {expected} bytes, got {pixels.LongLength}.");

        ParsedProfile profile = GetOrBuildProfile(iccProfile);

        int stride = width * channels;
        long pixelCount = (long)width * height;
        bool runParallel = useParallel && pixelCount >= ParallelThresholdPixels && height > 1;

        unsafe
        {
            fixed (byte* basePtr = pixels)
            fixed (float* rLutPtr = profile.DecodeLutR)
            fixed (float* gLutPtr = profile.DecodeLutG)
            fixed (float* bLutPtr = profile.DecodeLutB)
            fixed (float* encLutPtr = profile.EncodeLut)
            {
                // Local copies so the lambda below captures plain pointers,
                // not managed array references (keeps the JIT from
                // re-checking null/bounds on the fixed arrays every access).
                byte* pixelsPtr = basePtr;
                float* rLut = rLutPtr, gLut = gLutPtr, bLut = bLutPtr, encLut = encLutPtr;
                Matrix3x3F m = profile.CombinedMatrix;
                int encLutLen = profile.EncodeLut.Length;

                if (runParallel)
                {
                    Parallel.For(0, height, row =>
                    {
                        ProcessRow(pixelsPtr + (long)row * stride, width, channels,
                                   rLut, gLut, bLut, encLut, encLutLen, in m);
                    });
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        ProcessRow(pixelsPtr + (long)row * stride, width, channels,
                                   rLut, gLut, bLut, encLut, encLutLen, in m);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Convenience overload matching JxlDecoder's (pixels, width, height, hasAlpha)
    /// tuple shape.
    /// <code>
    ///   var (pixels, w, h, hasAlpha) = JxlDecoder.DecodeInternalRaw(jxlBytes, opts);
    ///   byte[]? icc = JxlDecoder.TryExtractIccProfile(jxlBytes);
    ///   if (icc != null)
    ///       IccSrgbTransform.TransformToSrgbInPlace(pixels, w, h, hasAlpha, icc);
    /// </code>
    /// </summary>
    public static void TransformToSrgbInPlace(
        byte[] pixels, uint width, uint height, bool hasAlpha, byte[] iccProfile,
        bool useParallel = true) =>
        TransformToSrgbInPlace(pixels, (int)width, (int)height, hasAlpha ? 4 : 3, iccProfile, useParallel);

    // ── Per-row hot loop ──────────────────────────────────────────────────

    private static unsafe void ProcessRow(
        byte* row, int width, int channels,
        float* rLut, float* gLut, float* bLut,
        float* encLut, int encLutLen,
        in Matrix3x3F m)
    {
        byte* p = row;
        for (int x = 0; x < width; x++)
        {
            float rl = rLut[p[0]];
            float gl = gLut[p[1]];
            float bl = bLut[p[2]];

            m.Transform(rl, gl, bl, out float sr, out float sg, out float sb);

            p[0] = EncodeSrgb(sr, encLut, encLutLen);
            p[1] = EncodeSrgb(sg, encLut, encLutLen);
            p[2] = EncodeSrgb(sb, encLut, encLutLen);

            p += channels; // alpha byte (if present) skipped, left untouched
        }
    }

    // ── Profile cache lookup ──────────────────────────────────────────────

    private static ParsedProfile GetOrBuildProfile(byte[] iccProfile)
    {
        ulong hash = Fnv1a64(iccProfile);

        if (s_profileCache.TryGetValue(hash, out var cached) &&
            BytesEqual(cached.Bytes, iccProfile))
        {
            return cached.Profile;
        }

        ParsedProfile parsed = ParseMatrixTrcProfile(iccProfile);
        s_profileCache[hash] = (iccProfile, parsed);
        return parsed;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static ulong Fnv1a64(byte[] data)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    // ── Internal representation ──────────────────────────────────────────

    // Double-precision matrix, used only while building the profile (once
    // per unique ICC profile). Kept separate from the float runtime matrix
    // so the one-time setup math stays fully precise.
    private readonly struct Matrix3x3D
    {
        public readonly double M00, M01, M02;
        public readonly double M10, M11, M12;
        public readonly double M20, M21, M22;

        public Matrix3x3D(double m00, double m01, double m02,
                          double m10, double m11, double m12,
                          double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static Matrix3x3D Multiply(in Matrix3x3D a, in Matrix3x3D b) => new(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public Matrix3x3F ToFloat() => new(
            (float)M00, (float)M01, (float)M02,
            (float)M10, (float)M11, (float)M12,
            (float)M20, (float)M21, (float)M22);
    }

    // Single-precision matrix used in the per-pixel hot path. Float ops are
    // faster than double on essentially all hardware and give the compiler
    // more room to auto-vectorize; 8-bit output has nowhere near enough
    // precision to need double here.
    private readonly struct Matrix3x3F
    {
        public readonly float M00, M01, M02;
        public readonly float M10, M11, M12;
        public readonly float M20, M21, M22;

        public Matrix3x3F(float m00, float m01, float m02,
                          float m10, float m11, float m12,
                          float m20, float m21, float m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public void Transform(float r, float g, float b, out float x, out float y, out float z)
        {
            x = M00 * r + M01 * g + M02 * b;
            y = M10 * r + M11 * g + M12 * b;
            z = M20 * r + M21 * g + M22 * b;
        }
    }

    private struct ParsedProfile
    {
        public float[] DecodeLutR; // device value (0-255) -> linear tone (0-1), exact per-8-bit-level
        public float[] DecodeLutG;
        public float[] DecodeLutB;
        public float[] EncodeLut;  // linear [0,1] -> 8-bit sRGB, EncodeLutSize samples
        public Matrix3x3F CombinedMatrix; // profile linear RGB -> linear sRGB (includes D50->D65 adaptation)
    }

    // Standard XYZ (D65) -> linear sRGB matrix (Bruce Lindbloom / IEC 61966-2-1 reference).
    private static readonly Matrix3x3D XyzD65ToLinearSrgb = new(
         3.2404542, -1.5371385, -0.4985314,
        -0.9692660, 1.8760108, 0.0415560,
         0.0556434, -0.2040259, 1.0572252);

    // Standard Bradford-adapted D50 -> D65 chromatic adaptation matrix.
    // ICC profile PCS white is always D50; sRGB's reference white is D65,
    // so this adaptation step is required before applying the sRGB matrix.
    private static readonly Matrix3x3D BradfordD50ToD65 = new(
         0.9555766, -0.0230393, 0.0631636,
        -0.0282895, 1.0099416, 0.0210077,
         0.0122982, -0.0204830, 1.3299098);

    // ── ICC profile parsing (matrix/TRC RGB profiles only) ───────────────

    private static ParsedProfile ParseMatrixTrcProfile(byte[] icc)
    {
        if (icc.Length < 132)
            throw new InvalidDataException("ICC profile too small to be valid (need at least 132 bytes).");

        string colorSpace = ReadTagSignature(icc, 16).TrimEnd();
        string pcs = ReadTagSignature(icc, 20).TrimEnd();

        if (colorSpace != "RGB")
            throw new NotSupportedException(
               $"Only RGB ICC profiles are supported by this minimal transform " +
               $"(profile color space is '{colorSpace}'). CMYK/Gray profiles need a " +
               $"different pipeline (CMYK in particular is virtually always LUT-based).");


        if (pcs != "XYZ")
            throw new NotSupportedException(
                $"Only XYZ-PCS profiles are supported (found PCS '{pcs}'). " +
                "Lab-PCS matrix profiles are rare and not handled here.");

        uint tagCount = ReadUInt32BE(icc, 128);
        var tags = new Dictionary<string, (uint offset, uint size)>();

        for (uint t = 0; t < tagCount; t++)
        {
            int entryOffset = 132 + (int)(t * 12);
            if (entryOffset + 12 > icc.Length)
                throw new InvalidDataException("ICC tag table extends past end of profile.");

            string sig = ReadTagSignature(icc, entryOffset);
            uint offset = ReadUInt32BE(icc, entryOffset + 4);
            uint size = ReadUInt32BE(icc, entryOffset + 8);
            tags[sig] = (offset, size);
        }

        bool hasMatrixTrc =
            tags.ContainsKey("rXYZ") && tags.ContainsKey("gXYZ") && tags.ContainsKey("bXYZ") &&
            tags.ContainsKey("rTRC") && tags.ContainsKey("gTRC") && tags.ContainsKey("bTRC");

        if (!hasMatrixTrc)
        {
            bool isLut = tags.ContainsKey("A2B0") || tags.ContainsKey("A2B1") || tags.ContainsKey("A2B2");
            throw new NotSupportedException(isLut
                ? "This is a LUT-based (AToB) ICC profile, not a matrix/TRC profile. " +
                  "LUT-based profiles need full N-dimensional CLUT interpolation, which " +
                  "this minimal transform does not implement."
                : "Required matrix/TRC tags (rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC) were not found in this profile.");
        }

        var (rx, ry, rz) = ReadXyzTag(icc, tags["rXYZ"].offset);
        var (gx, gy, gz) = ReadXyzTag(icc, tags["gXYZ"].offset);
        var (bx, by, bz) = ReadXyzTag(icc, tags["bXYZ"].offset);

        // Columns are the R/G/B colorant XYZ values (already D50-relative,
        // per the ICC spec's PCS convention) — this matrix maps the
        // profile's own linear RGB directly to D50 XYZ.
        var profileToXyzD50 = new Matrix3x3D(
            rx, gx, bx,
            ry, gy, by,
            rz, gz, bz);

        // Combined: profile linear RGB -> D50 XYZ -> D65 XYZ -> linear sRGB.
        // Precomputed once (in double, for accuracy) then narrowed to float
        // for the runtime hot path.
        Matrix3x3D combined = Matrix3x3D.Multiply(
            XyzD65ToLinearSrgb,
            Matrix3x3D.Multiply(BradfordD50ToD65, profileToXyzD50));

        return new ParsedProfile
        {
            DecodeLutR = BuildDecodeLut(icc, tags["rTRC"]),
            DecodeLutG = BuildDecodeLut(icc, tags["gTRC"]),
            DecodeLutB = BuildDecodeLut(icc, tags["bTRC"]),
            EncodeLut = BuildSrgbEncodeLut(),
            CombinedMatrix = combined.ToFloat(),
        };
    }

    /// <summary>
    /// Builds a 256-entry exact lookup table mapping an 8-bit device value
    /// to its linear tone value, per the profile's TRC tag (curveType or
    /// parametricCurveType). Since there are only 256 possible 8-bit input
    /// values, this table is exact — no interpolation error at decode time.
    /// </summary>
    private static float[] BuildDecodeLut(byte[] icc, (uint offset, uint size) tag)
    {
        var lut = new float[256];
        string type = ReadTagSignature(icc, (int)tag.offset);

        if (type == "curv")
        {
            uint count = ReadUInt32BE(icc, (int)tag.offset + 8);

            if (count == 0)
            {
                // Identity curve: linear, gamma = 1.0.
                for (int i = 0; i < 256; i++) lut[i] = i / 255f;
            }
            else if (count == 1)
            {
                // Single gamma value, stored as u8Fixed8Number.
                ushort raw = ReadUInt16BE(icc, (int)tag.offset + 12);
                double gamma = raw / 256.0;
                for (int i = 0; i < 256; i++)
                    lut[i] = (float)Math.Pow(i / 255.0, gamma);
            }
            else
            {
                // Sampled curve: 'count' uint16 samples, uniformly spaced
                // across device input [0,1], each representing linear output.
                var samples = new float[count];
                for (uint i = 0; i < count; i++)
                {
                    ushort raw = ReadUInt16BE(icc, (int)tag.offset + 12 + (int)(i * 2));
                    samples[i] = raw / 65535f;
                }
                for (int i = 0; i < 256; i++)
                {
                    double pos = (i / 255.0) * (count - 1);
                    int idx = (int)pos;
                    double frac = pos - idx;
                    float a = samples[idx];
                    float b = idx + 1 < count ? samples[idx + 1] : samples[idx];
                    lut[i] = (float)(a + (b - a) * frac);
                }
            }
        }
        else if (type == "para")
        {
            ushort funcType = ReadUInt16BE(icc, (int)tag.offset + 8);
            int paramsOffset = (int)tag.offset + 12;

            double g = ReadS15Fixed16(icc, paramsOffset);
            double a = 1, b = 0, c = 0, d = 0, e = 0, f = 0;

            switch (funcType)
            {
                case 0:
                    break;
                case 1:
                    a = ReadS15Fixed16(icc, paramsOffset + 4);
                    b = ReadS15Fixed16(icc, paramsOffset + 8);
                    break;
                case 2:
                    a = ReadS15Fixed16(icc, paramsOffset + 4);
                    b = ReadS15Fixed16(icc, paramsOffset + 8);
                    c = ReadS15Fixed16(icc, paramsOffset + 12);
                    break;
                case 3:
                    a = ReadS15Fixed16(icc, paramsOffset + 4);
                    b = ReadS15Fixed16(icc, paramsOffset + 8);
                    c = ReadS15Fixed16(icc, paramsOffset + 12);
                    d = ReadS15Fixed16(icc, paramsOffset + 16);
                    break;
                case 4:
                    a = ReadS15Fixed16(icc, paramsOffset + 4);
                    b = ReadS15Fixed16(icc, paramsOffset + 8);
                    c = ReadS15Fixed16(icc, paramsOffset + 12);
                    d = ReadS15Fixed16(icc, paramsOffset + 16);
                    e = ReadS15Fixed16(icc, paramsOffset + 20);
                    f = ReadS15Fixed16(icc, paramsOffset + 24);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported parametric curve function type {funcType} (valid range is 0-4).");
            }

            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;
                double y = funcType switch
                {
                    0 => Math.Pow(x, g),
                    1 => x >= -b / a ? Math.Pow(a * x + b, g) : 0.0,
                    2 => x >= -b / a ? Math.Pow(a * x + b, g) + c : c,
                    3 => x >= d ? Math.Pow(a * x + b, g) : c * x,
                    _ => x >= d ? Math.Pow(a * x + b, g) + e : c * x + f, // case 4
                };
                lut[i] = (float)Math.Clamp(y, 0.0, 1.0);
            }
        }
        else
        {
            throw new NotSupportedException(
                $"Unsupported TRC tag type '{type}' (expected 'curv' or 'para').");
        }

        return lut;
    }

    // ── sRGB encode (linear -> 8-bit gamma-encoded) ──────────────────────

    private static unsafe byte EncodeSrgb(float linear, float* lut, int lutLength)
    {
        if (linear <= 0f) return 0;
        if (linear >= 1f) return 255;

        float pos = linear * (lutLength - 1);
        int idx = (int)pos;
        float frac = pos - idx;
        float a = lut[idx];
        float b = idx + 1 < lutLength ? lut[idx + 1] : lut[idx];
        float v = a + (b - a) * frac;

        // Manual round-to-nearest (avoids a Math.Round call per channel per
        // pixel); v is already guaranteed in [0,255] by the LUT's construction.
        int result = (int)(v + 0.5f);
        return (byte)(result > 255 ? 255 : result);
    }

    private static float[] BuildSrgbEncodeLut()
    {
        var lut = new float[EncodeLutSize];
        for (int i = 0; i < EncodeLutSize; i++)
        {
            double linear = (double)i / (EncodeLutSize - 1);
            double srgb = linear <= 0.0031308
                ? linear * 12.92
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            lut[i] = (float)(srgb * 255.0);
        }
        return lut;
    }

    // ── Big-endian binary readers (all ICC numeric fields are big-endian) ─

    private static string ReadTagSignature(byte[] data, int offset) =>
        Encoding.ASCII.GetString(data, offset, 4);

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static ushort ReadUInt16BE(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static int ReadInt32BE(byte[] data, int offset) =>
        (int)ReadUInt32BE(data, offset);

    // s15Fixed16Number: signed 16.16 fixed point, big-endian.
    private static double ReadS15Fixed16(byte[] data, int offset) =>
        ReadInt32BE(data, offset) / 65536.0;

    private static (double x, double y, double z) ReadXyzTag(byte[] icc, uint offset)
    {
        // XYZType: 4-byte signature 'XYZ ', 4 bytes reserved, then 3 x s15Fixed16Number.
        double x = ReadS15Fixed16(icc, (int)offset + 8);
        double y = ReadS15Fixed16(icc, (int)offset + 12);
        double z = ReadS15Fixed16(icc, (int)offset + 16);
        return (x, y, z);
    }
}