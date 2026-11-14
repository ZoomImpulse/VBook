using VBook.Core.Generation;

namespace VBook.Core.Providers;

public interface IQwenTtsProcessRunner : IDisposable
{
    Task<byte[]> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken);
}

public sealed class QwenTtsProvider : ITtsProvider, IDisposable
{
    private static readonly string[] SupportedVoices =
    [
        "Aiden", "Dylan", "Eric", "Ono_Anna", "Ryan", "Serena", "Sohee", "Uncle_Fu", "Vivian"
    ];

    private static readonly string[] SupportedLanguages =
    [
        "Chinese", "English", "French", "German", "Italian", "Japanese",
        "Korean", "Portuguese", "Russian", "Spanish"
    ];

    private static readonly IReadOnlyDictionary<string, string> VoiceNativeLanguages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Aiden"] = "English",
            ["Dylan"] = "Chinese (Beijing dialect)",
            ["Eric"] = "Chinese (Sichuan dialect)",
            ["Ono_Anna"] = "Japanese",
            ["Ryan"] = "English",
            ["Serena"] = "Chinese",
            ["Sohee"] = "Korean",
            ["Uncle_Fu"] = "Chinese",
            ["Vivian"] = "Chinese"
        };

    private readonly IQwenTtsProcessRunner _runner;

    public QwenTtsProvider(IQwenTtsProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public IReadOnlyList<string> VoiceIds { get; } = SupportedVoices;

    public IReadOnlyList<string> Languages { get; } = SupportedLanguages;

    public string? GetVoiceNativeLanguage(string voiceId) =>
        VoiceNativeLanguages.GetValueOrDefault(voiceId);

    public bool SupportsVoiceInstructions => true;

    public ProviderCapabilities Capabilities { get; } =
        new("Qwen3-TTS 1.7B CustomVoice (local CUDA)", IsMock: false, MaxContextCharacters: 700);

    public async Task<SpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Language);
        ArgumentNullException.ThrowIfNull(request.Instruction);
        if (request.Text.Length > Capabilities.MaxContextCharacters)
        {
            throw new ArgumentException("The text exceeds Qwen3-TTS's request limit.", nameof(request));
        }

        if (!VoiceIds.Contains(request.VoiceId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Qwen3-TTS voice '{request.VoiceId}' is not available.", nameof(request));
        }

        if (!Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Qwen3-TTS language '{request.Language}' is not available.",
                nameof(request));
        }

        if (request.Instruction.Length > 500)
        {
            throw new ArgumentException("A Qwen voice instruction cannot exceed 500 characters.", nameof(request));
        }

        var wavBytes = await _runner.SynthesizeAsync(
            request with
            {
                VoiceId = VoiceIds.First(voice =>
                    string.Equals(voice, request.VoiceId, StringComparison.OrdinalIgnoreCase)),
                Language = Languages.First(language =>
                    string.Equals(language, request.Language, StringComparison.OrdinalIgnoreCase))
            },
            cancellationToken);
        var audio = WavPcm.ReadPcmAudio(wavBytes);
        var duration = audio.Data.Length / (double)(audio.SampleRate * audio.BlockAlign);
        if (duration <= 0)
        {
            throw new InvalidDataException("Qwen3-TTS returned an empty WAV recording.");
        }

        return new SpeechResult(wavBytes, TimeSpan.FromSeconds(duration));
    }

    public void Dispose() => _runner.Dispose();
}
