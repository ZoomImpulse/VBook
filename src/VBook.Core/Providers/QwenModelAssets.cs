using System.Security.Cryptography;

namespace VBook.Core.Providers;

public sealed record QwenTtsModelFiles(string VoiceDesignDirectory, string BaseDirectory);

public sealed record QwenTtsSetupProgress(string Phase, long BytesReceived = 0, long TotalBytes = 0);

public sealed class QwenTtsModelAssetManager
{
    public const string VoiceDesignModelRevision = "5ecdb67327fd37bb2e042aab12ff7391903235d3";
    public const string BaseModelRevision = "fd4b254389122332181a7c3db7f27e918eec64e3";

    private static readonly ModelDefinition[] Models =
    [
        new(
            "VoiceDesign",
            "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign",
            VoiceDesignModelRevision,
            CreateAssets(
                4421,
                "2c2c027c574a85c0d5a5d48e3cfed1a8a051ff9d",
                3_833_402_552,
                "391e8db219f292c515297cdceeb43e4eae67cdde35fa57e79a6a8a532fca0522")),
        new(
            "Base",
            "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            BaseModelRevision,
            CreateAssets(
                4494,
                "81b57e8e790c07e8fa7f82d8bdfd7574d485c396",
                3_857_413_744,
                "38fc7fc51c5e776e840414b6fd443962e9411b9654888fd7913e4da643cb857c"))
    ];

    public static long DownloadSize { get; } =
        Models.Sum(model => model.Assets.Sum(asset => asset.Size));

    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public QwenTtsModelAssetManager(HttpClient httpClient, string modelsDirectory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
    }

    public string InstallDirectory => Path.Combine(_modelsDirectory, "Qwen3-TTS-12Hz-1.7B-VoiceDesign");

    public bool IsInstalled => TryGetInstalledFiles() is not null;

    public QwenTtsModelFiles? TryGetInstalledFiles()
    {
        if (Models.Any(model => model.Assets.Any(asset =>
            !HasExpectedSize(Path.Combine(InstallDirectory, model.DirectoryName, asset.Path), asset.Size))))
        {
            return null;
        }

        return new QwenTtsModelFiles(
            Path.Combine(InstallDirectory, "VoiceDesign"),
            Path.Combine(InstallDirectory, "Base"));
    }

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
            foreach (var model in Models)
            {
                foreach (var asset in model.Assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var phase = $"Downloading {model.DirectoryName}/{asset.Path}";
                    progress?.Report(new QwenTtsSetupProgress(phase, totalBytes, DownloadSize));
                    var destination = Path.Combine(staging, model.DirectoryName, asset.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await DownloadAndVerifyAsync(
                        model,
                        asset,
                        destination,
                        progress,
                        phase,
                        totalBytes,
                        cancellationToken);
                    totalBytes += asset.Size;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            Directory.Move(staging, InstallDirectory);
            return TryGetInstalledFiles()
                ?? throw new InvalidDataException("The installed Qwen VoiceDesign and Base models failed validation.");
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
        ModelDefinition model,
        ModelAsset asset,
        string destination,
        IProgress<QwenTtsSetupProgress>? progress,
        string phase,
        long precedingBytes,
        CancellationToken cancellationToken)
    {
        var encodedPath = string.Join('/', asset.Path.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri(
            $"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{encodedPath}?download=true");
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
                phase,
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

    private static bool HasExpectedSize(string path, long expectedSize) =>
        File.Exists(path) && new FileInfo(path).Length == expectedSize;

    private static ModelAsset[] CreateAssets(
        long configSize,
        string configHash,
        long modelSize,
        string modelHash) =>
    [
        new("config.json", configSize, configHash),
        new("generation_config.json", 245, "1872b16d4535564abf2db5d6debe6cddd82b7f2e"),
        new("merges.txt", 1_671_839, "20024bfe7c83998e9aeaf98a0cd6a2ce6306c2f0"),
        new("model.safetensors", modelSize, modelHash),
        new("preprocessor_config.json", 127, "0525dd953bb9241912f7147666f0d535165d5d4f"),
        new("speech_tokenizer/config.json", 2336, "06cc8dc4c5ec8a1929086b71b98c313020d9268b"),
        new("speech_tokenizer/configuration.json", 76, "ab58e2eaf53cd14a1a2a7527d9261ceea93a24cd"),
        new("speech_tokenizer/model.safetensors", 682_293_092, "836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258"),
        new("speech_tokenizer/preprocessor_config.json", 234, "ba40914f4f49ab98a8ca545d4892ef7291a39592"),
        new("tokenizer_config.json", 7344, "6ff9fd60cc623bb54bbd603cbd418c97a11528d7"),
        new("vocab.json", 2_776_833, "4783fe10ac3adce15ac8f358ef5462739852c569")
    ];

    private sealed record ModelDefinition(
        string DirectoryName,
        string Repository,
        string Revision,
        ModelAsset[] Assets);

    private sealed record ModelAsset(string Path, long Size, string Hash);
}
