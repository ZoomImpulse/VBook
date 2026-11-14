using System.Security.Cryptography;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers;

namespace VBook.Core.Providers;

public sealed record KokoroModelFiles(
    string ModelPath,
    string VoicesPath,
    string TokensPath,
    string LexiconPath);

public sealed record KokoroModelDownloadProgress(long BytesReceived, long TotalBytes);

public sealed record KokoroModelArchiveDescriptor(
    string ArchiveName,
    string InstallName,
    Uri DownloadUri,
    long Size,
    string Sha256)
{
    public static KokoroModelArchiveDescriptor Default { get; } = new(
        "kokoro-int8-multi-lang-v1_0.tar.bz2",
        "kokoro-multi-lang-v1_0-int8",
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_0.tar.bz2"),
        132_353_410,
        "72c347230107ef8a31098ec4d468a605244beef5f1ed00db97efa13518800653");
}

public interface IKokoroModelArchiveExtractor
{
    Task ExtractAsync(string archivePath, string destinationPath, CancellationToken cancellationToken);
}

public sealed class KokoroModelArchiveExtractor : IKokoroModelArchiveExtractor
{
    private const long MaximumExpandedSize = 1_000_000_000;

    public async Task ExtractAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        Directory.CreateDirectory(destinationPath);
        var rootPath = Path.GetFullPath(destinationPath);
        var rootPrefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        await using var compressedStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var decompressedStream = new BZip2Stream(
            compressedStream,
            CompressionMode.Decompress,
            true);
        using var reader = ReaderFactory.Open(decompressedStream);
        long expandedSize = 0;
        while (await reader.MoveToNextEntryAsync(cancellationToken))
        {
            var entry = reader.Entry;
            var entryKey = entry.Key;
            if (string.IsNullOrWhiteSpace(entryKey))
            {
                throw new InvalidDataException("The Kokoro archive contains an empty entry path.");
            }

            var relativePath = entryKey.Replace('\\', '/');
            if (
                Path.IsPathRooted(relativePath) ||
                relativePath.Split('/').Any(part => part is "." or ".."))
            {
                throw new InvalidDataException($"The Kokoro archive contains an unsafe path: '{entryKey}'.");
            }

            var outputPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            if (!outputPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The Kokoro archive contains an unsafe path: '{entryKey}'.");
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            expandedSize = checked(expandedSize + entry.Size);
            if (expandedSize > MaximumExpandedSize)
            {
                throw new InvalidDataException("The Kokoro archive expands beyond the supported size limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);
            await reader.WriteEntryToAsync(output, cancellationToken);
        }
    }
}

public sealed class KokoroModelAssetManager
{
    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly IKokoroModelArchiveExtractor _extractor;
    private readonly KokoroModelArchiveDescriptor _descriptor;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public KokoroModelAssetManager(
        HttpClient httpClient,
        string modelsDirectory,
        IKokoroModelArchiveExtractor? extractor = null,
        KokoroModelArchiveDescriptor? descriptor = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
        _extractor = extractor ?? new KokoroModelArchiveExtractor();
        _descriptor = descriptor ?? KokoroModelArchiveDescriptor.Default;
        if (!_descriptor.DownloadUri.IsAbsoluteUri ||
            _descriptor.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(_descriptor.ArchiveName) ||
            Path.GetFileName(_descriptor.ArchiveName) != _descriptor.ArchiveName ||
            string.IsNullOrWhiteSpace(_descriptor.InstallName) ||
            Path.GetFileName(_descriptor.InstallName) != _descriptor.InstallName ||
            _descriptor.Size <= 0 ||
            _descriptor.Sha256.Length != 64 ||
            !_descriptor.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The Kokoro model archive descriptor is invalid.", nameof(descriptor));
        }
    }

    public string InstallDirectory => Path.Combine(_modelsDirectory, _descriptor.InstallName);

    public bool IsInstalled => TryGetInstalledFiles() is not null;

    public KokoroModelFiles? TryGetInstalledFiles()
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return null;
        }

        var modelPath = FindRequiredFile("model.int8.onnx");
        var voicesPath = FindRequiredFile("voices.bin");
        var tokensPath = FindRequiredFile("tokens.txt");
        var lexiconPath = FindRequiredFile("lexicon-us-en.txt");
        var licensePath = FindRequiredFile("LICENSE");
        return modelPath is not null &&
               voicesPath is not null &&
               tokensPath is not null &&
               lexiconPath is not null &&
               licensePath is not null
            ? new KokoroModelFiles(modelPath, voicesPath, tokensPath, lexiconPath)
            : null;
    }

    public async Task<KokoroModelFiles> InstallAsync(
        IProgress<KokoroModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken);
        var archivePath = Path.Combine(_modelsDirectory, $"{_descriptor.ArchiveName}.{Guid.NewGuid():N}.partial");
        var stagingPath = Path.Combine(_modelsDirectory, $"kokoro-staging-{Guid.NewGuid():N}");
        try
        {
            if (TryGetInstalledFiles() is { } installed)
            {
                return installed;
            }

            Directory.CreateDirectory(_modelsDirectory);
            await DownloadAndVerifyAsync(archivePath, progress, cancellationToken);
            await _extractor.ExtractAsync(archivePath, stagingPath, cancellationToken);
            var files = FindModelFiles(stagingPath);
            if (files is null)
            {
                throw new InvalidDataException(
                    "The downloaded Kokoro model is missing required model, voice, token, lexicon, or license files.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            Directory.Move(stagingPath, InstallDirectory);
            return TryGetInstalledFiles()
                ?? throw new InvalidDataException("Kokoro model validation failed after installation.");
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            _installLock.Release();
        }
    }

    private async Task DownloadAndVerifyAsync(
        string destinationPath,
        IProgress<KokoroModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            _descriptor.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != _descriptor.Size)
        {
            throw new InvalidDataException(
                $"The Kokoro model download size was unexpected ({contentLength} bytes).");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long bytesReceived = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            bytesReceived += read;
            if (bytesReceived > _descriptor.Size)
            {
                throw new InvalidDataException("The Kokoro model download exceeded its expected size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new KokoroModelDownloadProgress(bytesReceived, _descriptor.Size));
        }

        await destination.FlushAsync(cancellationToken);
        if (bytesReceived != _descriptor.Size)
        {
            throw new InvalidDataException(
                $"The Kokoro model download was incomplete ({bytesReceived} of {_descriptor.Size} bytes).");
        }

        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualSha256, _descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Kokoro model download failed its SHA-256 integrity check.");
        }
    }

    private string? FindRequiredFile(string fileName) =>
        Directory.EnumerateFiles(InstallDirectory, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();

    private static KokoroModelFiles? FindModelFiles(string rootDirectory)
    {
        var modelPath = FindFile(rootDirectory, "model.int8.onnx");
        var voicesPath = FindFile(rootDirectory, "voices.bin");
        var tokensPath = FindFile(rootDirectory, "tokens.txt");
        var lexiconPath = FindFile(rootDirectory, "lexicon-us-en.txt");
        var licensePath = FindFile(rootDirectory, "LICENSE");
        return modelPath is not null &&
               voicesPath is not null &&
               tokensPath is not null &&
               lexiconPath is not null &&
               licensePath is not null
            ? new KokoroModelFiles(modelPath, voicesPath, tokensPath, lexiconPath)
            : null;
    }

    private static string? FindFile(string directory, string fileName) =>
        Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();
}
