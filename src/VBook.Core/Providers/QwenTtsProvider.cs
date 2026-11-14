using VBook.Core.Generation;

namespace VBook.Core.Providers;

public interface IQwenTtsProcessRunner : IDisposable
{
    Task<byte[]> GenerateVoiceDesignAsync(
        VoiceDesignRequest request,
        CancellationToken cancellationToken);

    Task<byte[]> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken);
}

public sealed class QwenTtsProvider : ITtsProvider, IVoiceDesignProvider, IDisposable
{
    public const string DesignedVoiceId = "Designed";

    private static readonly string[] SupportedVoices = [DesignedVoiceId];

    private static readonly string[] SupportedLanguages =
    [
        "Chinese", "English", "French", "German", "Italian", "Japanese",
        "Korean", "Portuguese", "Russian", "Spanish"
    ];

    private readonly IQwenTtsProcessRunner _runner;

    public QwenTtsProvider(IQwenTtsProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public IReadOnlyList<string> VoiceIds { get; } = SupportedVoices;

    public IReadOnlyList<string> Languages { get; } = SupportedLanguages;

    public string? GetVoiceNativeLanguage(string voiceId) => null;

    public bool SupportsVoiceInstructions => true;

    public bool SupportsVoiceDesign => true;

    public ProviderCapabilities Capabilities { get; } =
        new("Qwen3-TTS VoiceDesign + Base 1.7B (local CUDA)", IsMock: false, MaxContextCharacters: 700);

    public async Task<SpeechResult> GenerateVoiceDesignAsync(
        VoiceDesignRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Text);
        ValidateLanguage(request.Language);
        ValidateInstruction(request.Instruction);

        var language = GetSupportedLanguage(request.Language);
        var wavBytes = await _runner.GenerateVoiceDesignAsync(
            request with { Language = language },
            cancellationToken);
        return CreateSpeechResult(wavBytes);
    }

    public async Task<SpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
        ValidateLanguage(request.Language);
        ArgumentNullException.ThrowIfNull(request.Instruction);
        ValidateInstruction(request.Instruction);

        if (!VoiceIds.Contains(request.VoiceId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Qwen3-TTS voice '{request.VoiceId}' is not available.", nameof(request));
        }

        if (request.VoiceDesignReferenceAudio is not { Length: >= 44 } ||
            string.IsNullOrWhiteSpace(request.VoiceDesignReferenceText))
        {
            throw new InvalidOperationException(
                "Generate and apply a Qwen VoiceDesign reference before synthesizing this speaker.");
        }

        var canonicalRequest = request with
        {
            VoiceId = DesignedVoiceId,
            Language = GetSupportedLanguage(request.Language)
        };
        var wavBytes = await _runner.SynthesizeAsync(canonicalRequest, cancellationToken);
        return CreateSpeechResult(wavBytes);
    }

    public void Dispose() => _runner.Dispose();

    private void ValidateText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds Qwen3-TTS's request limit.", nameof(text));
        }
    }

    private static void ValidateLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Qwen3-TTS language '{language}' is not available.", nameof(language));
        }
    }

    private static void ValidateInstruction(string instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        if (instruction.Length > 500)
        {
            throw new ArgumentException("A Qwen voice description cannot exceed 500 characters.", nameof(instruction));
        }
    }

    private static string GetSupportedLanguage(string language) =>
        SupportedLanguages.First(supported =>
            string.Equals(supported, language, StringComparison.OrdinalIgnoreCase));

    private static SpeechResult CreateSpeechResult(byte[] wavBytes)
    {
        var audio = WavPcm.ReadPcmAudio(wavBytes);
        var duration = audio.Data.Length / (double)(audio.SampleRate * audio.BlockAlign);
        if (duration <= 0)
        {
            throw new InvalidDataException("Qwen3-TTS returned an empty WAV recording.");
        }

        return new SpeechResult(wavBytes, TimeSpan.FromSeconds(duration));
    }
}
