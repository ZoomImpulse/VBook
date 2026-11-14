using System.Security.Cryptography;

namespace VBook.Core.Providers;

public sealed record QwenTtsModelFiles(string ModelDirectory);

public sealed record QwenTtsSetupProgress(string Phase, long BytesReceived = 0, long TotalBytes = 0);

public sealed class QwenTtsModelAssetManager
{
    public const string ModelRevision = "0c0e3051f131929182e2c023b9537f8b1c68adfe";
    public const long DownloadSize = 4_520_159_586;

    private static readonly ModelAsset[] Assets =
    [
        new("config.json", 4908, "95d74b20b7485f5dfa09d05988d6c606ec226811"),
        new("generation_config.json", 245, "1872b16d4535564abf2db5d6debe6cddd82b7f2e"),
        new("merges.txt", 1_671_839, "20024bfe7c83998e9aeaf98a0cd6a2ce6306c2f0"),
        new("model.safetensors", 3_833_402_552, "38b1d5971bdbd982b561cccec982669a53b0537c3cf5e9bd4778ed07bb2f5137"),
        new("preprocessor_config.json", 127, "0525dd953bb9241912f7147666f0d535165d5d4f"),
        new("speech_tokenizer/config.json", 2336, "06cc8dc4c5ec8a1929086b71b98c313020d9268b"),
        new("speech_tokenizer/configuration.json", 76, "ab58e2eaf53cd14a1a2a7527d9261ceea93a24cd"),
        new("speech_tokenizer/model.safetensors", 682_293_092, "836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258"),
        new("speech_tokenizer/preprocessor_config.json", 234, "ba40914f4f49ab98a8ca545d4892ef7291a39592"),
        new("tokenizer_config.json", 7344, "6ff9fd60cc623bb54bbd603cbd418c97a11528d7"),
        new("vocab.json", 2_776_833, "4783fe10ac3adce15ac8f358ef5462739852c569")
    ];

    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public QwenTtsModelAssetManager(HttpClient httpClient, string modelsDirectory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
    }

    public string InstallDirectory => Path.Combine(_modelsDirectory, "Qwen3-TTS-12Hz-1.7B-CustomVoice");

    public bool IsInstalled => TryGetInstalledFiles() is not null;

    public QwenTtsModelFiles? TryGetInstalledFiles() =>
        Assets.All(asset =>
            File.Exists(Path.Combine(InstallDirectory, asset.Path)) &&
            new FileInfo(Path.Combine(InstallDirectory, asset.Path)).Length == asset.Size)
            ? new QwenTtsModelFiles(InstallDirectory)
            : null;

    public async Task<QwenTtsModelFiles> InstallAsync(
        IProgress<QwenTtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken);
        var staging = Path.Combine(_modelsDirectory, $"qwen-staging-{Guid.NewGuid():N}");
        try
        {
            if (TryGetInstalledFiles() is { } installed)
            {
                return installed;
            }

            Directory.CreateDirectory(_modelsDirectory);
            EnsureFreeSpace();
            Directory.CreateDirectory(staging);
            long totalBytes = 0;
            foreach (var asset in Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new QwenTtsSetupProgress($"Downloading {asset.Path}", totalBytes, DownloadSize));
                var destination = Path.Combine(staging, asset.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAndVerifyAsync(asset, destination, progress, totalBytes, cancellationToken);
                totalBytes += asset.Size;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            Directory.Move(staging, InstallDirectory);
            return TryGetInstalledFiles()
                ?? throw new InvalidDataException("The installed Qwen model failed validation.");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            _installLock.Release();
        }
    }

    private async Task DownloadAndVerifyAsync(
        ModelAsset asset,
        string destination,
        IProgress<QwenTtsSetupProgress>? progress,
        long precedingBytes,
        CancellationToken cancellationToken)
    {
        var encodedPath = string.Join('/', asset.Path.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri(
            $"https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice/resolve/{ModelRevision}/{encodedPath}?download=true");
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
        {
            throw new InvalidDataException($"The Qwen model file '{asset.Path}' has an unexpected size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(
            asset.Hash.Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        var gitBlobHeader = asset.Hash.Length == 40
            ? System.Text.Encoding.ASCII.GetBytes($"blob {asset.Size}\0")
            : [];
        hash.AppendData(gitBlobHeader);
        var buffer = new byte[128 * 1024];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            received += read;
            if (received > asset.Size)
            {
                throw new InvalidDataException($"The Qwen model file '{asset.Path}' exceeds its pinned size.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new QwenTtsSetupProgress(
                $"Downloading {asset.Path}",
                precedingBytes + received,
                DownloadSize));
        }

        if (received != asset.Size)
        {
            throw new InvalidDataException(
                $"The Qwen model file '{asset.Path}' is incomplete ({received} of {asset.Size} bytes).");
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, asset.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The Qwen model file '{asset.Path}' failed its integrity check.");
        }
    }

    private void EnsureFreeSpace()
    {
        var drive = new DriveInfo(Path.GetPathRoot(_modelsDirectory)!);
        if (drive.AvailableFreeSpace < DownloadSize + 1_000_000_000)
        {
            throw new IOException(
                $"Qwen model setup needs at least {DownloadSize + 1_000_000_000:N0} bytes free on {drive.Name}.");
        }
    }

    private sealed record ModelAsset(string Path, long Size, string Hash);
}
