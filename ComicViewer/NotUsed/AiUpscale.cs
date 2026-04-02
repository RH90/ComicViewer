using NetVips;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComicViewer.NotUsed;

public enum Waifu2xNoiseLevel { None = -1, Low = 0, Medium = 1, High = 2, Highest = 3 }
public enum Waifu2xModel { CUnet, AnimeStyleArt, Photo }

public static class Waifu2xUpscaler
{
    private static string _executablePath = Path.Join(
        AppContext.BaseDirectory, "waifu2x", "waifu2x-ncnn-vulkan.exe");

    public static void SetExecutablePath(string path) => _executablePath = path;

    /// <summary>
    /// AI upscales a NetVips image using waifu2x-ncnn-vulkan via stdin/stdout.
    /// No temp files are written to disk.
    /// Caller must dispose the returned image.
    /// </summary>
    public static async Task<Image> UpscaleAsync(
        Image vipsImage,
        int scale = 2,
        Waifu2xNoiseLevel noiseLevel = Waifu2xNoiseLevel.Medium,
        Waifu2xModel model = Waifu2xModel.CUnet,
        int gpuId = 0,
        int tileSize = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vipsImage);

        if (!File.Exists(_executablePath))
            throw new FileNotFoundException(
                $"waifu2x-ncnn-vulkan.exe not found at: {_executablePath}");

        string modelPath = Path.Combine(
            Path.GetDirectoryName(_executablePath)!, ModelToDirectory(model));

        // -i - means read PNG from stdin
        // -o - means write PNG to stdout
        string args = string.Join(" ",
            "-i -",
            "-o -",
            $"-n {(int)noiseLevel}",
            $"-s {scale}",
            $"-m \"{modelPath}\"",
            $"-g {gpuId}",
            $"-t {tileSize}",
            "-f png");

        var psi = new ProcessStartInfo(_executablePath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Must set binary mode on stdout — default stream encoding
        // would corrupt the PNG bytes on Windows.
        psi.StandardOutputEncoding = null;
        psi.StandardInputEncoding = null;

        using var process = new Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();

        //process.ErrorDataReceived += (_, e) =>
        //{

        //    if (e.Data is not null) stderr.AppendLine(e.Data);
        //};

        process.Start();
        process.BeginErrorReadLine();

        // Set stdin/stdout to binary mode on Windows to prevent
        // newline translation corrupting the PNG stream.
        process.StandardInput.BaseStream.Flush();
        //process.StandardOutput.BaseStream.ReadTimeout = Timeout.Infinite;

        // ── 1. Encode input image to PNG and write to stdin ───────────────
        // Normalise to RGB/RGBA — waifu2x models expect colour input.
        // PngsaveBuffer is lossless — no quality loss in the round-trip.
        using Image normalised = NormaliseForExport(vipsImage);
        byte[] pngBytes = normalised.PngsaveBuffer();

        // Write stdin and read stdout concurrently — if we write all of
        // stdin before reading stdout, the process output buffer fills up
        // and deadlocks when it tries to write more output than the OS
        // pipe buffer can hold (~64KB on Windows).
        var writeTask = Task.Run(async () =>
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();


                //for (int i = 0; i < pngBytes.Length;)
                //{

                //}
                //process.StandardInput.BaseStream.Write(pngBytes);
                await process.StandardInput.BaseStream
                    .WriteAsync(pngBytes, cancellationToken)
                    .ConfigureAwait(false);

                sw.Stop();
                Debug.WriteLine("Timess: " + sw.ElapsedMilliseconds);

                await process.StandardInput.BaseStream
                    .FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Close stdin so the process gets EOF and starts processing.
                process.StandardInput.Close();
            }
        }, cancellationToken);

        // ── 2. Read stdout into a MemoryStream concurrently ───────────────
        using var outputBuffer = new MemoryStream();

        var readTask = process.StandardOutput.BaseStream
            .CopyToAsync(outputBuffer, cancellationToken);

        // ── 3. Wait for both to complete ──────────────────────────────────
        await Task.WhenAll(writeTask, readTask).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"waifu2x-ncnn-vulkan exited with code {process.ExitCode}.\n" +
                $"Stderr:\n{stderr}");

        byte[] outBytes = outputBuffer.ToArray();

        if (outBytes.Length == 0)
            throw new InvalidOperationException(
                "waifu2x-ncnn-vulkan produced no output.\n" +
                $"Stderr:\n{stderr}");

        // ── 4. Decode output PNG back into a NetVips image ────────────────
        return Image.NewFromBuffer(outBytes, access: Enums.Access.Random);
    }

    /// <summary>
    /// Synchronous convenience wrapper. Prefer UpscaleAsync on UI threads.
    /// </summary>
    public static Image Upscale(
        Image vipsImage,
        int scale = 2,
        Waifu2xNoiseLevel noiseLevel = Waifu2xNoiseLevel.Medium,
        Waifu2xModel model = Waifu2xModel.CUnet,
        int gpuId = 0,
        int tileSize = 0)
        => UpscaleAsync(vipsImage, scale, noiseLevel, model, gpuId, tileSize)
               .GetAwaiter().GetResult();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Image NormaliseForExport(Image img)
    {
        if (img.Bands > 4)
        {
            using Image flat = img.Flatten();
            return NormaliseForExport(flat);
        }

        return img.Bands switch
        {
            // Expand grey to RGB — waifu2x models are trained on colour images.
            1 => img.Colourspace(Enums.Interpretation.Srgb),

            // Grey+Alpha → flatten onto white → RGB.
            2 => img.Flatten(background: new double[] { 255 })
                    .Colourspace(Enums.Interpretation.Srgb),

            // RGB and RGBA — pass through unchanged.
            3 or 4 => img.Copy(),

            _ => throw new NotSupportedException($"Unexpected band count: {img.Bands}")
        };
    }

    private static string ModelToDirectory(Waifu2xModel model) => model switch
    {
        Waifu2xModel.CUnet => "models-cunet",
        Waifu2xModel.AnimeStyleArt => "models-upconv_7_anime_style_art_rgb",
        Waifu2xModel.Photo => "models-upconv_7_photo",
        _ => "models-cunet"
    };
}
