using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VBook.Core.Generation;

public interface IM4bExporter
{
    Task ExportAsync(
        GeneratedBookAudio book,
        string outputPath,
        CancellationToken cancellationToken);
}

public interface IFfmpegProcessRunner
{
    Task RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class FfmpegM4bExporter : IM4bExporter
{
    private readonly string _executablePath;
    private readonly IFfmpegProcessRunner _processRunner;

    public FfmpegM4bExporter(string executablePath, IFfmpegProcessRunner? processRunner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _processRunner = processRunner ?? new FfmpegProcessRunner();
    }

    public async Task ExportAsync(
        GeneratedBookAudio book,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(book.Title);
        if (book.Chapters.Count == 0)
        {
            throw new InvalidOperationException("Generate at least one chapter before exporting an M4B audiobook.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullOutputPath), ".m4b", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The audiobook output path must use the .m4b extension.", nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The M4B output path has no parent directory.", nameof(outputPath));
        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException($"The M4B output directory does not exist: {outputDirectory}");
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), $"vbook-m4b-{Guid.NewGuid():N}");
        var temporaryOutput = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(fullOutputPath)}-{Guid.NewGuid():N}.m4b");
        Directory.CreateDirectory(workDirectory);
        try
        {
            var metadataFile = Path.Combine(workDirectory, "chapters.ffmeta");
            var filterFile = Path.Combine(workDirectory, "audio.filter");
            var filter = new StringBuilder();
            var metadata = new StringBuilder(";FFMETADATA1\n");
            metadata.Append("title=").AppendLine(EscapeMetadata(book.Title));
            long chapterStartMilliseconds = 0;
            var arguments = new List<string> { "-hide_banner", "-y" };
            for (var index = 0; index < book.Chapters.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = book.Chapters[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(chapter.Title);
                var wavPath = Path.GetFullPath(chapter.WavPath);
                var wavBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);
                var audio = WavPcm.ReadPcmAudio(wavBytes);
                var byteRate = checked(audio.SampleRate * audio.BlockAlign);
                if (audio.Data.Length == 0 || audio.Data.Length % audio.BlockAlign != 0)
                {
                    throw new InvalidDataException($"Chapter '{chapter.Title}' contains empty or incomplete PCM audio.");
                }

                var durationTicks = checked((long)audio.Data.Length * TimeSpan.TicksPerSecond / byteRate);
                var durationMilliseconds = Math.Max(1L, (long)Math.Ceiling(durationTicks / (double)TimeSpan.TicksPerMillisecond));
                var chapterEndMilliseconds = checked(chapterStartMilliseconds + durationMilliseconds);
                arguments.Add("-i");
                arguments.Add(wavPath);
                filter.Append('[').Append(index).Append(":a:0]")
                    .Append("aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo,")
                    .Append("asetpts=PTS-STARTPTS[a").Append(index).AppendLine("];");
                metadata.AppendLine("[CHAPTER]");
                metadata.AppendLine("TIMEBASE=1/1000");
                metadata.Append("START=").AppendLine(chapterStartMilliseconds.ToString(CultureInfo.InvariantCulture));
                metadata.Append("END=").AppendLine(chapterEndMilliseconds.ToString(CultureInfo.InvariantCulture));
                metadata.Append("title=").AppendLine(EscapeMetadata(chapter.Title));
                chapterStartMilliseconds = chapterEndMilliseconds;
            }

            filter.Append(string.Concat(Enumerable.Range(0, book.Chapters.Count).Select(index => $"[a{index}]")))
                .Append("concat=n=").Append(book.Chapters.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine(":v=0:a=1[aout]");
            await File.WriteAllTextAsync(filterFile, filter.ToString(), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(metadataFile, metadata.ToString(), new UTF8Encoding(false), cancellationToken);
            var metadataInputIndex = book.Chapters.Count;
            arguments.AddRange([
                "-f", "ffmetadata", "-i", metadataFile,
                "-filter_complex_script", filterFile,
                "-map", "[aout]",
                "-map_metadata", metadataInputIndex.ToString(CultureInfo.InvariantCulture),
                "-map_chapters", metadataInputIndex.ToString(CultureInfo.InvariantCulture),
                "-vn", "-c:a", "aac", "-b:a", "64k", "-f", "ipod",
                temporaryOutput
            ]);
            await _processRunner.RunAsync(_executablePath, arguments, cancellationToken);
            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
            {
                throw new InvalidDataException("FFmpeg completed without creating a non-empty M4B output.");
            }

            File.Move(temporaryOutput, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }

            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }
        }
    }

    private static string EscapeMetadata(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                case '=':
                case ';':
                case '#':
                    escaped.Append('\\').Append(character);
                    break;
                case '\r':
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }
}

public sealed class FfmpegProcessRunner : IFfmpegProcessRunner
{
    public async Task RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start FFmpeg executable '{executablePath}'.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start FFmpeg executable '{executablePath}'. Install FFmpeg or configure VBOOK_FFMPEG_EXECUTABLE.",
                exception);
        }

        var errorOutput = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await errorOutput;
            throw;
        }

        var error = await errorOutput;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}: {error.Trim()}");
        }
    }
}
