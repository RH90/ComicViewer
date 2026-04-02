using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using static System.Resources.ResXFileRef;

namespace ComicViewer.Imaging;

/// <summary>
/// Reads pixel dimensions from JXL, JXR, JPEG, and PNG images by parsing
/// only their binary headers — no full decode required.
/// </summary>
public static class ImageDimensionReader
{
    /// <summary>
    /// Returns (Width, Height) by reading from <paramref name="stream"/>.
    /// Supports JPEG, PNG, JXL (bare codestream and ISOBMFF container), and JXR.
    /// </summary>
    /// 
    private static readonly byte[] IsobmffMagic =
       { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
    public static (int Width, int Height) Read(MemoryStream stream)
    {
        Span<byte> magic = stackalloc byte[12];
        int n = stream.Read(magic);
        stream.Position = 0;

        if (n < 4)
            throw new InvalidDataException("Stream too short to identify format.");

        // ── PNG: 89 50 4E 47 ──────────────────────────────────────────────────
        if (magic[0] == 0x89 && magic[1] == 0x50 && magic[2] == 0x4E && magic[3] == 0x47)
            return ReadPng(stream);

        // ── GIF87a / GIF89a: 47 49 46 38 ─────────────────────────────────────
        if (magic[0] == 0x47 && magic[1] == 0x49 && magic[2] == 0x46 && magic[3] == 0x38)
            return ReadGif(stream);

        // ── JPEG: FF D8 ──────────────────────────────────────────────────────
        if (magic[0] == 0xFF && magic[1] == 0xD8)
            return ReadJpeg(stream);

        //// ── WebP: RIFF????WEBP ───────────────────────────────────────────────
        if (n >= 12
            && magic[0] == 0x52 && magic[1] == 0x49 && magic[2] == 0x46 && magic[3] == 0x46  // "RIFF"
            && magic[8] == 0x57 && magic[9] == 0x45 && magic[10] == 0x42 && magic[11] == 0x50) // "WEBP"
            return ReadWebP(stream);

        // ── JXL bare codestream: FF 0A ───────────────────────────────────────
        if (magic[0] == 0xFF && magic[1] == 0x0A)
            return ReadJxlContainer(stream);

        // ── JXL ISOBMFF container: 00 00 00 0C 4A 58 4C 20 0D 0A 87 0A ──────
        if (n >= 12
            && magic[0] == 0x00 && magic[1] == 0x00 && magic[2] == 0x00 && magic[3] == 0x0C
            && magic[4] == 0x4A && magic[5] == 0x58 && magic[6] == 0x4C && magic[7] == 0x20
            && magic[8] == 0x0D && magic[9] == 0x0A && magic[10] == 0x87 && magic[11] == 0x0A)
            return ReadJxlContainer(stream);

        // ── JXR little-endian: 49 49 BC 01 ──────────────────────────────────
        if (magic[0] == 0x49 && magic[1] == 0x49 && magic[2] == 0xBC && magic[3] == 0x01)
            return ReadJxr(stream, bigEndian: false);

        // ── JXR big-endian: 4D 4D 01 BC ─────────────────────────────────────
        if (magic[0] == 0x4D && magic[1] == 0x4D && magic[2] == 0x01 && magic[3] == 0xBC)
            return ReadJxr(stream, bigEndian: true);

        throw new NotSupportedException("Unrecognized or unsupported image format.");
    }

    // =========================================================================
    // PNG
    //
    // File layout:
    //   [8 B signature]
    //   [4 B IHDR chunk length] [4 B "IHDR"]
    //   [4 B width, BE] [4 B height, BE] …
    // =========================================================================
    private static (int, int) ReadPng(MemoryStream s)
    {
        Span<byte> buf = stackalloc byte[4];
        s.Seek(16, SeekOrigin.Begin); // skip: sig(8) + chunk-len(4) + "IHDR"(4)
        ReadExact(s, buf);
        int w = BinaryPrimitives.ReadInt32BigEndian(buf);
        ReadExact(s, buf);
        int h = BinaryPrimitives.ReadInt32BigEndian(buf);
        return (w, h);
    }
    // =========================================================================
    // GIF (87a and 89a)
    //
    // Header layout — all fields little-endian:
    //   [6 B signature: "GIF87a" or "GIF89a"]
    //   [2 B logical screen width  LE]
    //   [2 B logical screen height LE]
    //   …
    // Width and height sit at fixed offsets 6 and 8 — no scanning required.
    // =========================================================================
    private static (int, int) ReadGif(MemoryStream s)
    {
        Span<byte> buf = stackalloc byte[2];
        s.Seek(6, SeekOrigin.Begin);
        ReadExact(s, buf);
        int w = BinaryPrimitives.ReadUInt16LittleEndian(buf);
        ReadExact(s, buf);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(buf);
        return (w, h);
    }

    // =========================================================================
    // JPEG
    //
    // Scans application/table markers (each preceded by FF xx + 2-byte length)
    // until an SOF (Start-of-Frame) segment is found.
    //
    // SOF markers: FFC0-FFC3, FFC5-FFC7, FFC9-FFCB, FFCD-FFCF
    // SOF payload: [1 B precision][2 B height, BE][2 B width, BE] …
    // =========================================================================
    private static (int, int) ReadJpeg(MemoryStream s)
    {
        s.Seek(2, SeekOrigin.Begin); // skip SOI (FF D8)
        Span<byte> buf = stackalloc byte[4];

        while (s.Position < s.Length)
        {
            if (s.ReadByte() != 0xFF)
                throw new InvalidDataException("JPEG: lost marker sync.");

            // The spec allows multiple 0xFF fill bytes before the actual marker byte.
            int marker;
            while ((marker = s.ReadByte()) == 0xFF) { }
            if (marker < 0) break;

            byte m = (byte)marker;

            // Standalone markers carry no length field (RST0-7, SOI, EOI, TEM).
            if (m is (>= 0xD0 and <= 0xD9) or 0x01)
                continue;

            // All other markers: 2-byte big-endian segment length (inclusive).
            ReadExact(s, buf[..2]);
            int segLen = (buf[0] << 8) | buf[1];
            if (segLen < 2) throw new InvalidDataException("JPEG: invalid segment length.");

            // SOF markers encode image dimensions.
            bool isSof = m is
                (>= 0xC0 and <= 0xC3) or
                (>= 0xC5 and <= 0xC7) or
                (>= 0xC9 and <= 0xCB) or
                (>= 0xCD and <= 0xCF);

            if (isSof)
            {
                s.ReadByte();      // sample precision (1 byte)
                ReadExact(s, buf); // [2 B height][2 B width]
                int h = (buf[0] << 8) | buf[1];
                int w = (buf[2] << 8) | buf[3];
                return (w, h);
            }

            s.Seek(segLen - 2, SeekOrigin.Current);
        }

        throw new InvalidDataException("JPEG: no SOF marker found.");
    }

    // =========================================================================
    // WebP
    //
    // RIFF container layout:
    //   [4 B "RIFF"][4 B file-size LE][4 B "WEBP"]
    //   [4 B chunk-FourCC][4 B chunk-size LE][chunk data …]
    //
    // Three sub-formats, identified by the first chunk FourCC:
    //
    // "VP8 " — simple lossy
    //   data: [3 B frame-tag][3 B sync 9D 01 2A]
    //         [2 B LE: bits 0-13 = width-1,  bits 14-15 = h-scale]
    //         [2 B LE: bits 0-13 = height-1, bits 14-15 = v-scale]
    //
    // "VP8L" — simple lossless
    //   data: [1 B signature 0x2F]
    //         [4 B LE packed: bits 0-13 = width-1, bits 14-27 = height-1]
    //
    // "VP8X" — extended (animated WebP, alpha, ICC, …)
    //   data: [4 B flags]
    //         [3 B LE: canvas-width-1]
    //         [3 B LE: canvas-height-1]
    // =========================================================================
    private static (int, int) ReadWebP(MemoryStream s)
    {
        // Seek to byte 12 — right after "RIFF" + file-size + "WEBP"
        s.Seek(12, SeekOrigin.Begin);

        Span<byte> buf4 = stackalloc byte[4];
        ReadExact(s, buf4);  // chunk FourCC
        uint fourCC = BinaryPrimitives.ReadUInt32BigEndian(buf4);

        ReadExact(s, buf4);  // chunk size (LE) — not needed but must be consumed

        const uint FourCC_VP8 = 0x56503820u; // "VP8 "
        const uint FourCC_VP8L = 0x5650384Cu; // "VP8L"
        const uint FourCC_VP8X = 0x56503858u; // "VP8X"

        switch (fourCC)
        {
            case FourCC_VP8:
                {
                    // Skip 3-byte frame tag, then verify 3-byte sync code.
                    Span<byte> tmp = stackalloc byte[3];
                    ReadExact(s, tmp); // frame tag
                    ReadExact(s, tmp); // sync: must be 9D 01 2A
                    if (tmp[0] != 0x9D || tmp[1] != 0x01 || tmp[2] != 0x2A)
                        throw new InvalidDataException("WebP VP8: invalid sync code.");

                    Span<byte> wb = stackalloc byte[2];
                    ReadExact(s, wb);
                    int w = BinaryPrimitives.ReadUInt16LittleEndian(wb) & 0x3FFF;
                    ReadExact(s, wb);
                    int h = BinaryPrimitives.ReadUInt16LittleEndian(wb) & 0x3FFF;
                    return (w, h);
                }

            case FourCC_VP8L:
                {
                    if (s.ReadByte() != 0x2F)
                        throw new InvalidDataException("WebP VP8L: missing lossless signature.");

                    ReadExact(s, buf4);
                    uint packed = BinaryPrimitives.ReadUInt32LittleEndian(buf4);
                    int w = (int)(packed & 0x3FFFu) + 1;         // bits 0–13
                    int h = (int)((packed >> 14) & 0x3FFFu) + 1; // bits 14–27
                    return (w, h);
                }

            case FourCC_VP8X:
                {
                    // Skip 4-byte flags.
                    ReadExact(s, buf4);

                    // Canvas width and height are each stored as a 24-bit LE integer (value − 1).
                    Span<byte> tmp3 = stackalloc byte[3];
                    ReadExact(s, tmp3);
                    int w = (tmp3[0] | (tmp3[1] << 8) | (tmp3[2] << 16)) + 1;
                    ReadExact(s, tmp3);
                    int h = (tmp3[0] | (tmp3[1] << 8) | (tmp3[2] << 16)) + 1;
                    return (w, h);
                }

            default:
                throw new InvalidDataException(
                    $"WebP: unknown chunk FourCC 0x{fourCC:X8}.");
        }
    }

    // ── Public entry point ─────────────────────────────────────────────────────
    /// <summary>
    /// Extracts the coded width and height from a JPEG XL MemoryStream.
    /// Supports both bare codestream (FF 0A) and ISOBMFF container formats.
    /// Does not modify the stream's Position.
    /// </summary>
    public static (int Width, int Height) ReadJxlContainer(Stream stream)
    {
        // Read just enough bytes to cover any reasonable header
        byte[] data = PeekBytes(stream, 512);

        int csOffset = LocateCodestream(data);

        // Bit reader starts right after the 2-byte FF 0A signature
        int bitPos = (csOffset + 2) * 8;

        return DecodeSizeHeader(data, ref bitPos);
    }

    // ── Codestream location ────────────────────────────────────────────────────
    private static int LocateCodestream(byte[] data)
    {
        // 1) Bare codestream
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x0A)
            return 0;

        // 2) ISOBMFF container — verify 12-byte signature
        if (data.Length >= 12 && StartsWithMagic(data))
            return WalkBoxes(data, startOffset: 12);

        throw new InvalidDataException("Stream does not contain a valid JXL file.");
    }

    private static bool StartsWithMagic(byte[] data)
    {
        for (int i = 0; i < IsobmffMagic.Length; i++)
            if (data[i] != IsobmffMagic[i]) return false;
        return true;
    }

    // ── Box walker ─────────────────────────────────────────────────────────────
    private static int WalkBoxes(byte[] data, int startOffset)
    {
        int offset = startOffset;

        while (offset + 8 <= data.Length)
        {
            // First 4 bytes: box size (big-endian)
            ulong boxSize = ReadUInt32BE(data, offset);
            // Next 4 bytes: box type as ASCII
            string boxType = Encoding.ASCII.GetString(data, offset + 4, 4);

            int headerLen = 8;

            if (boxSize == 1)
            {
                // Extended 64-bit size in the next 8 bytes
                if (offset + 16 > data.Length)
                    throw new InvalidDataException("Truncated extended-size box.");
                boxSize = ReadUInt64BE(data, offset + 8);
                headerLen = 16;
            }
            else if (boxSize == 0)
            {
                // Box extends to end of file
                boxSize = (ulong)(data.Length - offset);
            }

            switch (boxType)
            {
                case "jxlc":
                    // Full codestream — payload starts right after the box header
                    return offset + headerLen;

                case "jxlp":
                    // Partial codestream — skip the 4-byte sequence number
                    return offset + headerLen + 4;
            }

            if (boxSize < (ulong)headerLen)
                throw new InvalidDataException($"Invalid box size {boxSize} for box '{boxType}'.");

            offset += (int)boxSize;
        }

        throw new InvalidDataException("No jxlc or jxlp box found in the JXL container.");
    }

    // ── SizeHeader decoder ─────────────────────────────────────────────────────
    private static (int Width, int Height) DecodeSizeHeader(byte[] data, ref int bitPos)
    {
        // 'small' flag — when set, dimensions are multiples of 8 encoded in 5 bits
        bool small = ReadBit(data, ref bitPos) == 1;

        int height = DecodeU32OrSmall(data, ref bitPos, small);

        // 3-bit aspect ratio selector
        int ratio = ReadBits(data, ref bitPos, 3);

        int width = ratio switch
        {
            0 => DecodeU32OrSmall(data, ref bitPos, small),  // explicit
            1 => height,
            2 => height * 12 / 10,
            3 => height * 4 / 3,
            4 => height * 3 / 2,
            5 => height * 16 / 9,
            6 => height * 5 / 4,
            7 => height * 2,
            _ => throw new InvalidDataException($"Unexpected ratio value: {ratio}")
        };

        return (width, height);
    }

    /// <summary>
    /// Decodes one dimension.
    /// small=true  → 5-bit value: (v + 1) * 8
    /// small=false → 2-bit tier selects bit-width, then value + 1
    /// </summary>
    private static int DecodeU32OrSmall(byte[] data, ref int bitPos, bool small)
    {
        if (small)
            return (ReadBits(data, ref bitPos, 5) + 1) * 8;

        int tier = ReadBits(data, ref bitPos, 2);
        int[] bitsPerTier = { 9, 13, 18, 30 };
        return ReadBits(data, ref bitPos, bitsPerTier[tier]) + 1;
    }

    // ── LSB-first bit reader ───────────────────────────────────────────────────
    private static int ReadBit(byte[] data, ref int bitPos)
    {
        int b = bitPos / 8;
        int shift = bitPos % 8;

        if (b >= data.Length)
            throw new InvalidDataException("Unexpected end of data while reading JXL header bits.");

        bitPos++;
        return (data[b] >> shift) & 1;
    }

    private static int ReadBits(byte[] data, ref int bitPos, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
            value |= ReadBit(data, ref bitPos) << i;
        return value;
    }

    // ── Stream / byte helpers ──────────────────────────────────────────────────

    /// <summary>Reads up to <paramref name="count"/> bytes without changing Position.</summary>
    private static byte[] PeekBytes(Stream stream, int count)
    {
        long saved = stream.Position;
        try
        {
            stream.Position = 0;
            int toRead = (int)Math.Min(count, stream.Length);
            byte[] buf = new byte[toRead];
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int n = stream.Read(buf, totalRead, toRead - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            return buf;
        }
        finally
        {
            stream.Position = saved;
        }
    }

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
         (uint)data[offset + 3];

    private static ulong ReadUInt64BE(byte[] data, int offset)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
            v = (v << 8) | data[offset + i];
        return v;
    }

    // =========================================================================
    // JXR (JPEG XR)
    //
    // TIFF-compatible structure:
    //   [2 B byte-order: "II"=LE / "MM"=BE]
    //   [2 B magic: 0xBC01 LE or 0x01BC BE]
    //   [4 B IFD offset]
    //
    // IFD entry (12 bytes): [2 B tag][2 B type][4 B count][4 B value/offset]
    //   Tag 0x0080 = IMAGE_WIDTH  (ULONG)
    //   Tag 0x0081 = IMAGE_HEIGHT (ULONG)
    // =========================================================================
    private static (int, int) ReadJxr(MemoryStream s, bool bigEndian)
    {
        //Span<byte> buf4 = stackalloc byte[4];
        //Span<byte> buf2 = stackalloc byte[2];

        //s.Seek(4, SeekOrigin.Begin); // skip byte-order mark (2) + magic (2)
        //ReadExact(s, buf4);
        //long ifdOffset = bigEndian
        //    ? BinaryPrimitives.ReadUInt32BigEndian(buf4)
        //    : BinaryPrimitives.ReadUInt32LittleEndian(buf4);

        //s.Seek(ifdOffset, SeekOrigin.Begin);
        //ReadExact(s, buf2);
        //int entryCount = bigEndian
        //    ? BinaryPrimitives.ReadUInt16BigEndian(buf2)
        //    : BinaryPrimitives.ReadUInt16LittleEndian(buf2);

        int w = 0, h = 0;

        byte[] arr = new byte[500];
        s.Read(arr, 0, 500);
        //Span<byte> entry = stackalloc byte[12];
        for (int i = 7; i < arr.Length; i++)
        {

            if (arr[i - 7] == 0x57 &&
                arr[i - 6] == 0x4D &&
                arr[i - 5] == 0x50 &&
                arr[i - 4] == 0x48 &&
                arr[i - 3] == 0x4F &&
                arr[i - 2] == 0x54 &&
                arr[i - 1] == 0x4F &&
                arr[i] == 0x00)
            {
                int shortNumber = arr[i + 3] >> 7;

                byte[] width, height;
                if (shortNumber == 1)
                {
                    width = new byte[] { arr[i + 5], arr[i + 6] };
                    height = new byte[] { arr[i + 7], arr[i + 8] };
                    short num = BitConverter.ToInt16(width, 0);
                    w = BinaryPrimitives.ReverseEndianness(num) + 1;

                    num = BitConverter.ToInt16(height, 0);
                    h = BinaryPrimitives.ReverseEndianness(num) + 1;
                }
                else
                {
                    width = new byte[] { arr[i + 5], arr[i + 6], arr[i + 7], arr[i + 8] };
                    height = new byte[] { arr[i + 9], arr[i + 10], arr[i + 11], arr[i + 12] };
                    int num = BitConverter.ToInt32(width, 0);
                    w = BinaryPrimitives.ReverseEndianness(num) + 1;
                    num = BitConverter.ToInt32(height, 0);
                    h = BinaryPrimitives.ReverseEndianness(num) + 1;
                }

                break;
            }
        }

        return (w, h);
    }





    // Ceiling integer division helper.
    private static uint CeilDiv(uint a, uint b) => (a + b - 1) / b;


    // =========================================================================
    // Helpers
    // =========================================================================
    private static void ReadExact(MemoryStream s, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf[total..]);
            if (n == 0) throw new EndOfStreamException("Unexpected end of stream.");
            total += n;
        }
    }

    private static ushort Rd16(ReadOnlySpan<byte> b, bool be) =>
        be ? BinaryPrimitives.ReadUInt16BigEndian(b)
           : BinaryPrimitives.ReadUInt16LittleEndian(b);

    private static uint Rd32(ReadOnlySpan<byte> b, bool be) =>
        be ? BinaryPrimitives.ReadUInt32BigEndian(b)
           : BinaryPrimitives.ReadUInt32LittleEndian(b);

    // -------------------------------------------------------------------------
    // LSB-first bit reader used exclusively for JXL codestream parsing.
    // JXL packs bits from LSB to MSB within each byte.
    // -------------------------------------------------------------------------
    private ref struct JxlBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bytePos;
        private int _bitPos; // 0 = LSB of current byte

        public JxlBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bytePos = 0;
            _bitPos = 0;
        }

        public bool ReadBool() => ReadBits(1) != 0;

        public uint ReadBits(int count)
        {
            uint result = 0;
            for (int i = 0; i < count; i++)
            {
                if (_bytePos >= _data.Length)
                    throw new InvalidDataException("JXL: unexpected end of SizeHeader bits.");

                result |= (uint)((_data[_bytePos] >> _bitPos) & 1) << i;

                if (++_bitPos == 8) { _bitPos = 0; _bytePos++; }
            }
            return result;
        }
    }
}