using System.Net;
using System.Security.Cryptography;
using VBook.App.Services;
using VBook.Core.Providers;
using Xunit;

namespace VBook.App.Tests;

public sealed class LocalTtsProviderServiceTests
{
    [Fact]
    public void Constructor_SelectsQwenByDefaultWithoutMockFallback()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new EmptyHttpMessageHandler());
        var assets = new KokoroModelAssetManager(client, Path.Combine(directory.Path, "models"));
        using var provider = new LocalTtsProviderService(
            assets,
            new FakeSpeechEngineFactory(),
            piperExecutable: null,
            piperVoicesDirectory: null,
            Path.Combine(directory.Path, "tts-settings.json"));

        Assert.Equal(LocalTtsProviderService.QwenProviderId, provider.SelectedProviderId);
        Assert.Empty(provider.VoiceIds);
        Assert.False(provider.Capabilities.IsMock);
        Assert.Contains("Qwen", provider.CurrentProviderDescription);
    }

    [Fact]
    public void Constructor_MigratesUnversionedPreferenceToQwenOnce()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new EmptyHttpMessageHandler());
        var assets = new KokoroModelAssetManager(client, Path.Combine(directory.Path, "models"));
        var settingsPath = Path.Combine(directory.Path, "tts-settings.json");
        File.WriteAllText(settingsPath, """{"SelectedProviderId":"Kokoro"}""");

        using var provider = new LocalTtsProviderService(
            assets,
            new FakeSpeechEngineFactory(),
            piperExecutable: null,
            piperVoicesDirectory: null,
            settingsPath);

        Assert.Equal(LocalTtsProviderService.QwenProviderId, provider.SelectedProviderId);
        Assert.Contains("\"SettingsVersion\":2", File.ReadAllText(settingsPath));

        provider.SelectProvider(LocalTtsProviderService.PiperProviderId);
        using var reopened = new LocalTtsProviderService(
            assets,
            new FakeSpeechEngineFactory(),
            piperExecutable: null,
            piperVoicesDirectory: null,
            settingsPath);
        Assert.Equal(LocalTtsProviderService.PiperProviderId, reopened.SelectedProviderId);
    }

    [Fact]
    public void SelectProvider_PreservesPiperAsExplicitUnavailableOption()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new EmptyHttpMessageHandler());
        var assets = new KokoroModelAssetManager(client, Path.Combine(directory.Path, "models"));
        var settingsPath = Path.Combine(directory.Path, "tts-settings.json");
        using var provider = new LocalTtsProviderService(
            assets,
            new FakeSpeechEngineFactory(),
            piperExecutable: null,
            piperVoicesDirectory: null,
            settingsPath);

        provider.SelectProvider(LocalTtsProviderService.PiperProviderId);

        Assert.Equal(LocalTtsProviderService.PiperProviderId, provider.SelectedProviderId);
        Assert.Empty(provider.VoiceIds);
        Assert.Contains("Configure", provider.CurrentProviderDescription);
        Assert.Contains("\"SelectedProviderId\":\"Piper\"", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void SelectProvider_ActivatesInstalledKokoroAndPersistsSelection()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new EmptyHttpMessageHandler());
        var assets = new KokoroModelAssetManager(client, Path.Combine(directory.Path, "models"));
        var modelFolder = Path.Combine(assets.InstallDirectory, "kokoro");
        Directory.CreateDirectory(modelFolder);
        File.WriteAllText(Path.Combine(modelFolder, "model.int8.onnx"), "model");
        File.WriteAllText(Path.Combine(modelFolder, "voices.bin"), "voices");
        File.WriteAllText(Path.Combine(modelFolder, "tokens.txt"), "tokens");
        File.WriteAllText(Path.Combine(modelFolder, "lexicon-us-en.txt"), "lexicon");
        File.WriteAllText(Path.Combine(modelFolder, "LICENSE"), "license");
        var factory = new FakeSpeechEngineFactory();
        var settingsPath = Path.Combine(directory.Path, "tts-settings.json");
        using var provider = new LocalTtsProviderService(
            assets,
            factory,
            piperExecutable: null,
            piperVoicesDirectory: null,
            settingsPath);
        provider.SelectProvider(LocalTtsProviderService.PiperProviderId);

        provider.SelectProvider(LocalTtsProviderService.KokoroProviderId);

        Assert.True(factory.WasUsed);
        Assert.Equal(53, provider.VoiceIds.Count);
        Assert.Equal("Kokoro-82M v1.0 int8 (local CPU)", provider.Capabilities.Name);
        Assert.Contains("\"SelectedProviderId\":\"Kokoro\"", File.ReadAllText(settingsPath));
    }

    private sealed class FakeSpeechEngineFactory : IKokoroSpeechEngineFactory
    {
        public bool WasUsed { get; private set; }

        public IKokoroSpeechEngine Create(KokoroModelFiles modelFiles)
        {
            WasUsed = true;
            return new FakeSpeechEngine();
        }
    }

    private sealed class FakeSpeechEngine : IKokoroSpeechEngine
    {
        public KokoroAudioSamples Synthesize(string text, int speakerId, CancellationToken cancellationToken) =>
            new([0.1f], 24_000);

        public void Dispose()
        {
        }
    }

    private sealed class EmptyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"vbook-tts-provider-service-test-{Guid.NewGuid():N}");
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
