
//using NetVips;
//using System;
//using System.Diagnostics;
//using System.IO;
//using System.Threading;
//using System.Threading.Tasks;

//namespace ComicViewer.ImageProcessing;

///// <summary>
///// Waifu2x noise reduction models. Higher levels remove more noise
///// but may soften fine detail.
///// </summary>
//public enum Waifu2xNoiseLevel
//{
//    None = -1,
//    Low = 0,
//    Medium = 1,
//    High = 2,
//    Highest = 3
//}

///// <summary>
///// Waifu2x model variants. CUnet gives the best quality for anime/manga.
///// </summary>
//public enum Waifu2xModel
//{
//    /// <summary>Best quality for anime/manga line art. Slower.</summary>
//    CUnet,

//    /// <summary>Faster anime model. Good for flat-colour art.</summary>
//    AnimeStyleArt,

//    /// <summary>Trained on photos. Use for real-world images.</summary>
//    Photo
//}

///// <summary>
///// AI upscaler wrapping waifu2x-ncnn-vulkan via CLI.
///// Accepts and returns NetVips images — no temp files visible to the caller.
///// The caller is responsible for disposing the returned image.
///// </summary>
//public static class Waifu2xUpscaler
//{
//    // Path to waifu2x-ncnn-vulkan.exe — default = same directory as the app.
//    private static string _executablePath = Path.Join(
//        AppContext.BaseDirectory, "waifu2x", "waifu2x-ncnn-vulkan____.exe");

//    /// <summary>
//    /// Override the path to waifu2x-ncnn-vulkan.exe if it is not
//    /// in the application directory.
//    /// </summary>
//    public static void SetExecutablePath(string path) => _executablePath = path;

//    // -------------------------------------------------------------------------
//    // Public API
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// AI upscales a NetVips image using waifu2x-ncnn-vulkan.
//    /// </summary>
//    /// <param name="vipsImage">Source image. Not disposed by this method.</param>
//    /// <param name="scale">
//    /// Upscale factor. waifu2x supports 1, 2, 4, 8, 16, 32.
//    /// Use 1 for denoise-only with no upscale.
//    /// </param>
//    /// <param name="noiseLevel">Denoise strength. Use None to skip denoising.</param>
//    /// <param name="model">Model variant. CUnet for best manga/anime quality.</param>
//    /// <param name="gpuId">
//    /// GPU to use. -1 = CPU only (slow). 0 = first GPU (default).
//    /// </param>
//    /// <param name="tileSize">
//    /// Tile size in pixels. 0 = auto. Reduce (e.g. 128) if you get GPU OOM errors.
//    /// </param>
//    /// <param name="cancellationToken">Optional cancellation token.</param>
//    /// <returns>A new upscaled NetVips image. Caller must dispose.</returns>
//    public static async Task<Image> UpscaleAsync(
//        Image vipsImage,
//        int scale = 2,
//        Waifu2xNoiseLevel noiseLevel = Waifu2xNoiseLevel.Medium,
//        Waifu2xModel model = Waifu2xModel.CUnet,
//        int gpuId = 0,
//        int tileSize = 0,
//        CancellationToken cancellationToken = default)
//    {
//        ArgumentNullException.ThrowIfNull(vipsImage);

//        if (!File.Exists(_executablePath))
//            throw new FileNotFoundException(
//                $"waifu2x-ncnn-vulkan.exe not found at: {_executablePath}\n" +
//                $"Download from https://github.com/nihui/waifu2x-ncnn-vulkan/releases " +
//                $"and place it next to your application, or call SetExecutablePath().");

//        // Use temp PNG files — waifu2x-ncnn-vulkan only accepts file paths.
//        // PNG is lossless so no quality loss occurs in the round-trip.
//        string inputPath = Path.Combine(Path.GetTempPath(), $"w2x_in_{Guid.NewGuid():N}.png");
//        string outputPath = Path.Combine(Path.GetTempPath(), $"w2x_out_{Guid.NewGuid():N}.png");

//        try
//        {
//            // ── 1. Export NetVips image to a temp PNG ─────────────────────
//            // Normalise to RGB/RGBA first — waifu2x does not handle Grey or
//            // exotic colour spaces reliably.
//            using Image normalised = NormaliseForExport(vipsImage);
//            normalised.Pngsave(inputPath);

//            // ── 2. Run waifu2x-ncnn-vulkan ────────────────────────────────
//            string args = BuildArguments(
//                inputPath, outputPath, scale, noiseLevel, model, gpuId, tileSize);


//            await RunProcessAsync(_executablePath, args, cancellationToken)
//                .ConfigureAwait(false);

//            if (!File.Exists(outputPath))
//                throw new InvalidOperationException(
//                    "waifu2x-ncnn-vulkan did not produce an output file. " +
//                    "Check that the models folder is present next to the executable.");

//            // ── 3. Load the output PNG back into NetVips ──────────────────
//            // Access.Random because the caller may need random pixel access.


//            return Image.NewFromBuffer(File.ReadAllBytes(outputPath), access: Enums.Access.Random); ;
//        }
//        finally
//        {

//            TryDelete(inputPath);
//            TryDelete(outputPath);
//            // Clean up temp files regardless of success or failure.

//        }
//    }

//    /// <summary>
//    /// Synchronous convenience wrapper around <see cref="UpscaleAsync"/>.
//    /// Blocks the calling thread — prefer UpscaleAsync on UI threads.
//    /// </summary>
//    public static Image Upscale(
//        Image vipsImage,
//        int scale = 2,
//        Waifu2xNoiseLevel noiseLevel = Waifu2xNoiseLevel.Medium,
//        Waifu2xModel model = Waifu2xModel.CUnet,
//        int gpuId = 0,
//        int tileSize = 0)
//        => UpscaleAsync(vipsImage, scale, noiseLevel, model, gpuId, tileSize)
//               .GetAwaiter().GetResult();

//    // -------------------------------------------------------------------------
//    // Helpers
//    // -------------------------------------------------------------------------

//    /// <summary>
//    /// Normalises a NetVips image to RGB or RGBA for export to waifu2x.
//    /// waifu2x-ncnn-vulkan handles RGB and RGBA PNG reliably.
//    /// Greyscale, Grey+Alpha, and exotic formats are converted first.
//    /// </summary>
//    private static Image NormaliseForExport(Image img)
//    {
//        // Collapse CMYK / 5+ bands to sRGB first.
//        if (img.Bands > 4)
//        {
//            using Image flat = img.Flatten();
//            return NormaliseForExport(flat);
//        }

//        return img.Bands switch
//        {
//            // Grey → expand to RGB so waifu2x models (trained on colour) work correctly.
//            1 => img.Colourspace(Enums.Interpretation.Srgb),

//            // Grey+Alpha → flatten onto white → RGB.
//            2 => img.Flatten(background: new double[] { 255 })
//                    .Colourspace(Enums.Interpretation.Srgb),

//            // RGB and RGBA — pass through unchanged.
//            3 or 4 => img.Copy(),

//            _ => throw new NotSupportedException($"Unexpected band count: {img.Bands}")
//        };
//    }

//    /// <summary>
//    /// Builds the waifu2x-ncnn-vulkan command line arguments.
//    /// </summary>
//    private static string BuildArguments(
//        string inputPath,
//        string outputPath,
//        int scale,
//        Waifu2xNoiseLevel noiseLevel,
//        Waifu2xModel model,
//        int gpuId,
//        int tileSize)
//    {
//        string modelDir = model switch
//        {
//            Waifu2xModel.CUnet => "models-cunet",
//            Waifu2xModel.AnimeStyleArt => "models-upconv_7_anime_style_art_rgb",
//            Waifu2xModel.Photo => "models-upconv_7_photo",
//            _ => "models-cunet"
//        };

//        // Resolve model directory relative to the executable.
//        string modelPath = Path.Combine(
//            Path.GetDirectoryName(_executablePath)!, modelDir);

//        return string.Join(" ",
//            $"-i \"{inputPath}\"",
//            $"-o \"{outputPath}\"",
//            $"-n {(int)noiseLevel}",
//            $"-s {scale}",
//            $"-m \"{modelPath}\"",
//            $"-g {gpuId}",
//            $"-t {tileSize}",
//            "-f png");   // always output PNG for lossless round-trip
//    }

//    /// <summary>
//    /// Runs an external process asynchronously, capturing stderr for diagnostics.
//    /// Throws <see cref="InvalidOperationException"/> if the process exits non-zero.
//    /// </summary>
//    private static async Task RunProcessAsync(
//        string exe,
//        string args,
//        CancellationToken cancellationToken)
//    {
//        var psi = new ProcessStartInfo(exe, args)
//        {
//            UseShellExecute = false,
//            CreateNoWindow = true,
//            RedirectStandardOutput = true,
//            RedirectStandardError = true,
//        };

//        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

//        var stderr = new System.Text.StringBuilder();
//        process.ErrorDataReceived += (_, e) =>
//        {
//            Debug.WriteLine(e.Data);
//            if (e.Data is not null) stderr.AppendLine(e.Data);
//        };

//        process.Start();
//        process.BeginErrorReadLine();

//        await using (cancellationToken.Register(() =>
//        {
//            try { process.Kill(); } catch { /* process may have already exited */ }
//        }))
//        {
//            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
//        }

//        cancellationToken.ThrowIfCancellationRequested();

//        if (process.ExitCode != 0)
//            throw new InvalidOperationException(
//                $"waifu2x-ncnn-vulkan exited with code {process.ExitCode}.\n" +
//                $"Stderr: {stderr}");
//    }

//    private static void TryDelete(string path)
//    {
//        try { if (File.Exists(path)) File.Delete(path); }
//        catch { /* best-effort cleanup — do not throw from finally */ }
//    }
//}