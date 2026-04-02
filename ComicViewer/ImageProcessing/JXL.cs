using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicViewer
{
    public sealed class JxlDecodeOptions
    {
        /// <summary>
        /// Number of threads. 0 = Environment.ProcessorCount. 1 = single-threaded.
        /// </summary>
        public int Threads { get; set; } = 0;

        /// <summary>
        /// Skip EXIF orientation correction. Slightly faster; only safe if you
        /// don't need correct orientation or handle it yourself.
        /// </summary>
        public bool SkipOrientation { get; set; } = false;

        public static readonly JxlDecodeOptions Default = new();
    }

    /// <summary>
    /// P/Invoke wrapper around libjxl (jxl.dll / jxl_threads.dll).
    ///
    /// FIX: s_threadRunnerFnPtr is now obtained via GetProcAddress so the CLR
    /// never wraps it in a managed thunk. Passing a managed delegate to
    /// JxlDecoderSetParallelRunner caused ExecutionEngineException because
    /// libjxl calls the runner from native worker threads where the CLR has no
    /// frame set up.
    /// </summary>
    public static class JxlDecoder
    {
        // ── Native constants ──────────────────────────────────────────────────

        private const int JXL_DEC_SUCCESS = 0;
        private const int JXL_DEC_ERROR = 1;
        private const int JXL_DEC_NEED_MORE_INPUT = 2;
        private const int JXL_DEC_NEED_IMAGE_OUT_BUFFER = 5;
        private const int JXL_DEC_BASIC_INFO = 0x40;
        private const int JXL_DEC_FULL_IMAGE = 0x1000;

        private const int JXL_TYPE_UINT8 = 2;
        private const int JXL_NATIVE_ENDIAN = 0;
        private const int JXL_TRUE = 1;

        // ── Native structs ────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct JxlBasicInfo
        {
            public int have_container;
            public uint xsize;
            public uint ysize;
            public int bits_per_sample;
            public int exponent_bits_per_sample;
            public float intensity_target;
            public float min_nits;
            public int relative_to_max_display;
            public float linear_below;
            public int uses_original_profile;
            public int have_preview;
            public int have_animation;
            public int orientation;
            public uint num_color_channels;
            public uint num_extra_channels;
            public uint alpha_bits;
            public uint alpha_exponent_bits;
            public int alpha_premultiplied;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] _padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JxlPixelFormat
        {
            public uint num_channels;
            public int data_type;
            public int endianness;
            public nuint align;
        }

        // ── P/Invoke – jxl.dll ────────────────────────────────────────────────

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr JxlDecoderCreate(IntPtr memory_manager);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern void JxlDecoderDestroy(IntPtr dec);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderSubscribeEvents(IntPtr dec, int events_wanted);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderSetInput(IntPtr dec, byte[] data, nuint size);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern void JxlDecoderCloseInput(IntPtr dec);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderProcessInput(IntPtr dec);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderGetBasicInfo(IntPtr dec, out JxlBasicInfo info);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderImageOutBufferSize(
            IntPtr dec, ref JxlPixelFormat format, out nuint size);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderSetImageOutBuffer(
            IntPtr dec, ref JxlPixelFormat format, byte[] buffer, nuint size);

        // parallel_runner must be a raw native function pointer, NOT a managed delegate.
        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderSetParallelRunner(
            IntPtr dec,
            IntPtr parallel_runner,
            IntPtr parallel_runner_opaque);

        [DllImport(MainWindow.libJxlMain, CallingConvention = CallingConvention.Cdecl)]
        private static extern int JxlDecoderSetKeepOrientation(
            IntPtr dec, int keepOrientation);

        // ── P/Invoke – jxl_threads.dll ────────────────────────────────────────

        [DllImport(MainWindow.libJxlThreads, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr JxlThreadParallelRunnerCreate(
            IntPtr memory_manager, nuint num_worker_threads);

        [DllImport(MainWindow.libJxlThreads, CallingConvention = CallingConvention.Cdecl)]
        private static extern void JxlThreadParallelRunnerDestroy(IntPtr runner);

        // ── Win32 helpers ─────────────────────────────────────────────────────

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        // ── Cached raw native function pointer ───────────────────────────────
        //
        // KEY FIX: We use GetProcAddress to get the raw native address of
        // JxlThreadParallelRunner from jxl_threads.dll. This is a plain C
        // function pointer with no CLR involvement. Passing a managed delegate
        // (as the previous version did) causes ExecutionEngineException because
        // libjxl calls the runner from unmanaged worker threads, and the CLR
        // thunk for a managed delegate requires a managed thread context that
        // doesn't exist on those threads.

        private static readonly Lazy<IntPtr> s_threadRunnerFnPtr = new(() =>
        {
            IntPtr hModule = GetModuleHandle(MainWindow.libJxlThreads);
            if (hModule == IntPtr.Zero)
                hModule = LoadLibrary(MainWindow.libJxlThreads);
            if (hModule == IntPtr.Zero)
                throw new DllNotFoundException(
                    "Could not load jxl_threads.dll. " +
                    "Ensure it is present next to the executable.");

            IntPtr fn = GetProcAddress(hModule, "JxlThreadParallelRunner");
            if (fn == IntPtr.Zero)
                throw new EntryPointNotFoundException(
                    "JxlThreadParallelRunner not found in jxl_threads.dll.");
            return fn;
        });

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a JPEG XL byte array and returns a freeze-able WPF BitmapImage.
        /// </summary>
        public static BitmapImage Decode(
            byte[] jxlData,
            JxlDecodeOptions? options = null)
        {
            if (jxlData is null || jxlData.Length == 0)
                throw new ArgumentNullException(nameof(jxlData));

            return ConvertToBitmapImage(
                DecodeInternal(jxlData, options ?? JxlDecodeOptions.Default));
        }

        /// <summary>
        /// Decodes a JPEG XL byte array and returns a BitmapSource.
        /// More efficient when BitmapSource suffices for display.
        /// </summary>
        public static BitmapSource DecodeAsBitmapSource(
            byte[] jxlData,
            JxlDecodeOptions? options = null)
        {
            if (jxlData is null || jxlData.Length == 0)
                throw new ArgumentNullException(nameof(jxlData));

            return DecodeInternal(jxlData, options ?? JxlDecodeOptions.Default);
        }

        // ── Core decode ───────────────────────────────────────────────────────

        private static BitmapSource DecodeInternal(byte[] jxlData, JxlDecodeOptions opts)
        {
            IntPtr dec = JxlDecoderCreate(IntPtr.Zero);
            IntPtr runner = IntPtr.Zero;

            if (dec == IntPtr.Zero)
                throw new InvalidOperationException("JxlDecoderCreate returned null.");

            try
            {
                // 1. Parallel runner (raw native fn ptr via GetProcAddress)
                int threadCount = opts.Threads <= 0
                    ? Environment.ProcessorCount
                    : opts.Threads;

                if (threadCount > 1)
                {
                    runner = JxlThreadParallelRunnerCreate(IntPtr.Zero, (nuint)threadCount);
                    if (runner == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "JxlThreadParallelRunnerCreate returned null.");

                    if (JxlDecoderSetParallelRunner(dec, s_threadRunnerFnPtr.Value, runner)
                            != JXL_DEC_SUCCESS)
                        throw new InvalidOperationException(
                            "JxlDecoderSetParallelRunner failed.");
                }

                // 2. Skip orientation (optional)
                if (opts.SkipOrientation)
                    if (JxlDecoderSetKeepOrientation(dec, JXL_TRUE) != JXL_DEC_SUCCESS)
                        throw new InvalidOperationException(
                            "JxlDecoderSetKeepOrientation failed.");

                // 3. Subscribe to events
                if (JxlDecoderSubscribeEvents(dec, JXL_DEC_BASIC_INFO | JXL_DEC_FULL_IMAGE)
                        != JXL_DEC_SUCCESS)
                    throw new InvalidOperationException("JxlDecoderSubscribeEvents failed.");

                // 4. Feed data
                if (JxlDecoderSetInput(dec, jxlData, (nuint)jxlData.Length) != JXL_DEC_SUCCESS)
                    throw new InvalidOperationException("JxlDecoderSetInput failed.");
                JxlDecoderCloseInput(dec);

                // 5. Event loop
                JxlBasicInfo info = default;
                bool hasInfo = false;
                byte[]? pixels = null;
                uint width = 0;
                uint height = 0;
                bool hasAlpha = false;

                while (true)
                {
                    int status = JxlDecoderProcessInput(dec);
                    switch (status)
                    {
                        case JXL_DEC_BASIC_INFO:
                            if (JxlDecoderGetBasicInfo(dec, out info) != JXL_DEC_SUCCESS)
                                throw new InvalidOperationException("JxlDecoderGetBasicInfo failed.");
                            hasInfo = true;
                            width = info.xsize;
                            height = info.ysize;
                            hasAlpha = info.alpha_bits > 0;
                            break;

                        case JXL_DEC_NEED_IMAGE_OUT_BUFFER:
                            if (!hasInfo)
                                throw new InvalidOperationException(
                                    "NEED_IMAGE_OUT_BUFFER received before BASIC_INFO.");
                            {
                                uint ch = hasAlpha ? 4u : 3u;
                                var fmt = MakePixelFormat(ch);
                                nuint sz;
                                if (JxlDecoderImageOutBufferSize(dec, ref fmt, out sz) != JXL_DEC_SUCCESS)
                                    throw new InvalidOperationException("JxlDecoderImageOutBufferSize failed.");
                                pixels = new byte[(int)sz];
                                if (JxlDecoderSetImageOutBuffer(dec, ref fmt, pixels, sz) != JXL_DEC_SUCCESS)
                                    throw new InvalidOperationException("JxlDecoderSetImageOutBuffer failed.");
                            }
                            break;

                        case JXL_DEC_FULL_IMAGE:
                            break;

                        case JXL_DEC_SUCCESS:
                            goto Done;

                        case JXL_DEC_NEED_MORE_INPUT:
                            throw new InvalidOperationException(
                                "Decoder needs more input even though the entire file was supplied.");

                        case JXL_DEC_ERROR:
                            throw new InvalidOperationException(
                                "libjxl reported JXL_DEC_ERROR.");

                        default:
                            break;
                    }
                }

            Done:
                if (pixels is null)
                    throw new InvalidOperationException("No pixel data captured.");

                // 6. Build WPF BitmapSource  (libjxl: RGB(A)  →  WPF: BGR(A))
                int stride;
                PixelFormat fmt2;
                if (hasAlpha) { stride = (int)width * 4; fmt2 = PixelFormats.Bgra32; SwapRedBlue(pixels, 4); }
                else { stride = (int)width * 3; fmt2 = PixelFormats.Bgr24; SwapRedBlue(pixels, 3); }



                return BitmapSource.Create((int)width, (int)height, 96, 96, fmt2, null, pixels, stride);
            }
            finally
            {
                JxlDecoderDestroy(dec);
                if (runner != IntPtr.Zero)
                    JxlThreadParallelRunnerDestroy(runner);
            }
        }
        public static (byte[], uint, uint, bool) DecodeInternalRaw(byte[] jxlData, JxlDecodeOptions opts)
        {
            IntPtr dec = JxlDecoderCreate(IntPtr.Zero);
            IntPtr runner = IntPtr.Zero;

            if (dec == IntPtr.Zero)
                throw new InvalidOperationException("JxlDecoderCreate returned null.");

            try
            {
                // 1. Parallel runner (raw native fn ptr via GetProcAddress)
                int threadCount = opts.Threads <= 0
                    ? Environment.ProcessorCount
                    : opts.Threads;

                if (threadCount > 1)
                {
                    runner = JxlThreadParallelRunnerCreate(IntPtr.Zero, (nuint)threadCount);
                    if (runner == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "JxlThreadParallelRunnerCreate returned null.");

                    if (JxlDecoderSetParallelRunner(dec, s_threadRunnerFnPtr.Value, runner)
                            != JXL_DEC_SUCCESS)
                        throw new InvalidOperationException(
                            "JxlDecoderSetParallelRunner failed.");
                }

                // 2. Skip orientation (optional)
                if (opts.SkipOrientation)
                    if (JxlDecoderSetKeepOrientation(dec, JXL_TRUE) != JXL_DEC_SUCCESS)
                        throw new InvalidOperationException(
                            "JxlDecoderSetKeepOrientation failed.");

                // 3. Subscribe to events
                if (JxlDecoderSubscribeEvents(dec, JXL_DEC_BASIC_INFO | JXL_DEC_FULL_IMAGE)
                        != JXL_DEC_SUCCESS)
                    throw new InvalidOperationException("JxlDecoderSubscribeEvents failed.");

                // 4. Feed data
                if (JxlDecoderSetInput(dec, jxlData, (nuint)jxlData.Length) != JXL_DEC_SUCCESS)
                    throw new InvalidOperationException("JxlDecoderSetInput failed.");
                JxlDecoderCloseInput(dec);

                // 5. Event loop
                JxlBasicInfo info = default;
                bool hasInfo = false;
                byte[]? pixels = null;
                uint width = 0;
                uint height = 0;
                bool hasAlpha = false;

                while (true)
                {
                    int status = JxlDecoderProcessInput(dec);
                    switch (status)
                    {
                        case JXL_DEC_BASIC_INFO:
                            if (JxlDecoderGetBasicInfo(dec, out info) != JXL_DEC_SUCCESS)
                                throw new InvalidOperationException("JxlDecoderGetBasicInfo failed.");
                            hasInfo = true;
                            width = info.xsize;
                            height = info.ysize;
                            hasAlpha = info.alpha_bits > 0;
                            break;

                        case JXL_DEC_NEED_IMAGE_OUT_BUFFER:
                            if (!hasInfo)
                                throw new InvalidOperationException(
                                    "NEED_IMAGE_OUT_BUFFER received before BASIC_INFO.");
                            {
                                uint ch = hasAlpha ? 4u : 3u;
                                var fmt = MakePixelFormat(ch);
                                nuint sz;
                                if (JxlDecoderImageOutBufferSize(dec, ref fmt, out sz) != JXL_DEC_SUCCESS)
                                    throw new InvalidOperationException("JxlDecoderImageOutBufferSize failed.");
                                pixels = new byte[(int)sz];
                                if (JxlDecoderSetImageOutBuffer(dec, ref fmt, pixels, sz) != JXL_DEC_SUCCESS)
                                    throw new InvalidOperationException("JxlDecoderSetImageOutBuffer failed.");
                            }
                            break;

                        case JXL_DEC_FULL_IMAGE:
                            break;

                        case JXL_DEC_SUCCESS:
                            goto Done;

                        case JXL_DEC_NEED_MORE_INPUT:
                            throw new InvalidOperationException(
                                "Decoder needs more input even though the entire file was supplied.");

                        case JXL_DEC_ERROR:
                            throw new InvalidOperationException(
                                "libjxl reported JXL_DEC_ERROR.");

                        default:
                            break;
                    }
                }

            Done:
                if (pixels is null)
                    throw new InvalidOperationException("No pixel data captured.");

                // 6. Build WPF BitmapSource  (libjxl: RGB(A)  →  WPF: BGR(A))
                //int stride;
                //PixelFormat fmt2;
                //if (hasAlpha) { stride = (int)width * 4; fmt2 = PixelFormats.Bgra32; SwapRedBlue(pixels, 4); }
                //else { stride = (int)width * 3; fmt2 = PixelFormats.Bgr24; SwapRedBlue(pixels, 3); }



                return (pixels, width, height, hasAlpha);
            }
            finally
            {
                JxlDecoderDestroy(dec);
                if (runner != IntPtr.Zero)
                    JxlThreadParallelRunnerDestroy(runner);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static JxlPixelFormat MakePixelFormat(uint channels) =>
            new JxlPixelFormat
            {
                num_channels = channels,
                data_type = JXL_TYPE_UINT8,
                endianness = JXL_NATIVE_ENDIAN,
                align = 0
            };

        private static void SwapRedBlue(byte[] pixels, int bpp)
        {
            for (int i = 0; i < pixels.Length; i += bpp)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }

        private static BitmapImage ConvertToBitmapImage(BitmapSource source)
        {
            using var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(source));
            enc.Save(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }


    // ═════════════════════════════════════════════════════════════════════════
    // Optional reusable thread pool for batch decoding
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the JxlThreadParallelRunner thread pool once and reuses it
    /// across many decode calls, avoiding per-image create/destroy overhead.
    ///
    /// <code>
    ///   using var pool = new JxlThreadPool();
    ///   foreach (var file in files)
    ///       JxlDecoder.Decode(File.ReadAllBytes(file), pool.Options);
    /// </code>
    /// </summary>
    public sealed class JxlThreadPool : IDisposable
    {
        [DllImport(MainWindow.libJxlThreads, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr JxlThreadParallelRunnerCreate(
            IntPtr memory_manager, nuint num_worker_threads);

        [DllImport(MainWindow.libJxlThreads, CallingConvention = CallingConvention.Cdecl)]
        private static extern void JxlThreadParallelRunnerDestroy(IntPtr runner);

        private readonly IntPtr _runner;
        private bool _disposed;

        public int ThreadCount { get; }
        public JxlDecodeOptions Options { get; }

        public JxlThreadPool(int threadCount = 0, bool skipOrientation = false)
        {
            ThreadCount = threadCount <= 0 ? Environment.ProcessorCount : threadCount;
            _runner = JxlThreadParallelRunnerCreate(IntPtr.Zero, (nuint)ThreadCount);
            if (_runner == IntPtr.Zero)
                throw new InvalidOperationException(
                    "JxlThreadParallelRunnerCreate returned null.");
            Options = new JxlDecodeOptions
            { Threads = ThreadCount, SkipOrientation = skipOrientation };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                JxlThreadParallelRunnerDestroy(_runner);
                _disposed = true;
            }
        }
    }
}