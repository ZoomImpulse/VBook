using System.Net;
using System.Security.Cryptography;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class KokoroModelAssetManagerTests
{
    [Fact]
    public async Task InstallAsync_VerifiesDownloadAndActivatesValidatedFiles()
    {
        using var directory = new TemporaryDirectory();
        var archiveBytes = new byte[] { 1, 2, 3, 4, 5 };
        var descriptor = CreateDescriptor(archiveBytes);
        using var client = CreateClient(archiveBytes);
        var extractor = new TestExtractor();
        var manager = new KokoroModelAssetManager(client, directory.Path, extractor, descriptor);
        var reports = new List<KokoroModelDownloadProgress>();

        var files = await manager.InstallAsync(
            new InlineProgress<KokoroModelDownloadProgress>(reports.Add),
            CancellationToken.None);

        Assert.True(File.Exists(files.ModelPath));
        Assert.True(File.Exists(files.VoicesPath));
        Assert.True(File.Exists(files.TokensPath));
        Assert.True(File.Exists(files.LexiconPath));
        Assert.True(manager.IsInstalled);
        Assert.True(extractor.WasCalled);
        Assert.Contains(reports, report => report.BytesReceived == archiveBytes.Length);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task InstallAsync_RejectsHashMismatchAndDoesNotExtract()
    {
        using var directory = new TemporaryDirectory();
        var archiveBytes = new byte[] { 1, 2, 3, 4, 5 };
        var descriptor = CreateDescriptor(archiveBytes) with
        {
            Sha256 = new string('0', 64)
        };
        using var client = CreateClient(archiveBytes);
        var extractor = new TestExtractor();
        var manager = new KokoroModelAssetManager(client, directory.Path, extractor, descriptor);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.InstallAsync(progress: null, CancellationToken.None));

        Assert.False(extractor.WasCalled);
        Assert.False(manager.IsInstalled);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task InstallAsync_DoesNotExtractWhenRequiredModelFilesAreMissing()
    {
        using var directory = new TemporaryDirectory();
        var archiveBytes = new byte[] { 8, 7, 6 };
        var descriptor = CreateDescriptor(archiveBytes);
        using var client = CreateClient(archiveBytes);
        var manager = new KokoroModelAssetManager(client, directory.Path, new IncompleteExtractor(), descriptor);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.InstallAsync(progress: null, CancellationToken.None));

        Assert.False(manager.IsInstalled);
    }

    private static KokoroModelArchiveDescriptor CreateDescriptor(byte[] bytes) =>
        new(
            "test-model.tar.bz2",
            "kokoro-test",
            new Uri("https://models.test/kokoro.tar.bz2"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static HttpClient CreateClient(byte[] bytes) =>
        new(new TestHttpMessageHandler(bytes));

    private sealed class TestHttpMessageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
    }

    private class TestExtractor : IKokoroModelArchiveExtractor
    {
        public bool WasCalled { get; private set; }

        public virtual Task ExtractAsync(
            string archivePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            var modelDirectory = Path.Combine(destinationPath, "kokoro");
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllText(Path.Combine(modelDirectory, "model.int8.onnx"), "model");
            File.WriteAllText(Path.Combine(modelDirectory, "voices.bin"), "voices");
            File.WriteAllText(Path.Combine(modelDirectory, "tokens.txt"), "tokens");
            File.WriteAllText(Path.Combine(modelDirectory, "lexicon-us-en.txt"), "lexicon");
            File.WriteAllText(Path.Combine(modelDirectory, "LICENSE"), "license");
            return Task.CompletedTask;
        }
    }

    private sealed class IncompleteExtractor : TestExtractor
    {
        public override Task ExtractAsync(
            string archivePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "model.onnx"), "model");
            return Task.CompletedTask;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"vbook-kokoro-assets-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
