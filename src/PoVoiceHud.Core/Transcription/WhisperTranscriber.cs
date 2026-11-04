using System.Text;
using PoVoiceHud.Core.Audio;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace PoVoiceHud.Core.Transcription;

/// <summary>
/// Whisper.net on the CPU runtime, or on the graphics card through Vulkan after <see cref="UseGpu"/>. The encoder cost is set by audio_ctx (50 per second, 1500 = 30 s), so
/// utterances up to 10 s use a 512-context processor (~3x faster, same text on the fixture) and longer ones the full window.
/// An optional prompt (a glossary of names) steers spelling; changing it rebuilds the processors before the next utterance.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    private const int ShortContext = 512;
    private const int ShortMaxSamples = ShortContext / 50 * Audio.PcmFrame.SampleRate;

    private readonly WhisperFactory _factory;
    private readonly int _threads;
    private readonly string _language;
    private readonly SemaphoreSlim _one = new(1, 1);
    private WhisperProcessor _short;
    private WhisperProcessor _long;
    private string? _builtWith;
    private volatile string? _prompt;

    static WhisperTranscriber() => RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];

    /// <summary>
    /// Vulkan first, then the CPU: Whisper.net loads the first runtime that works, so a PC without a Vulkan driver still
    /// transcribes. The native library is loaded once per process, so this counts only before the first transcriber is built.
    /// </summary>
    public static void UseGpu(bool gpu) => RuntimeOptions.RuntimeLibraryOrder = gpu ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu] : [RuntimeLibrary.Cpu];

    /// <summary>"graphics card (Vulkan)" or "CPU": the runtime that was loaded, once a transcriber exists.</summary>
    public static string Runtime => RuntimeOptions.LoadedLibrary switch
    {
        RuntimeLibrary.Vulkan => "graphics card (Vulkan)",
        RuntimeLibrary.Cuda => "graphics card (CUDA)",
        RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => "CPU",
        null => "not loaded",
        var other => other.ToString()!,
    };

    /// <param name="language">"en", another ISO code, or "auto" to detect it (for a multilingual model only).</param>
    public WhisperTranscriber(string modelPath, int threads, string? prompt = null, string language = "en")
    {
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = RuntimeOptions.RuntimeLibraryOrder.FirstOrDefault() != RuntimeLibrary.Cpu });
        _threads = threads;
        _language = language;
        _prompt = _builtWith = prompt;
        (_short, _long) = Build(prompt);
    }

    /// <summary>
    /// "Glossary: Inspection, TenantId." from the first words that fit in <paramref name="maxChars"/>, or null when there are none.
    /// Any prompt costs ~80 ms per utterance and every 100 more characters ~50 ms; beyond ~120 Whisper also starts
    /// capitalising ordinary words ("Inspection Table"), so the default keeps it short (measured: about 80–100 ms per sentence; see README).
    /// </summary>
    public static string? PromptFor(IEnumerable<string> words, int maxChars = 120)
    {
        var kept = new List<string>();
        var length = 0;
        foreach (var word in words)
        {
            if (length + word.Length + 2 > maxChars) break;
            kept.Add(word);
            length += word.Length + 2;
        }
        return kept.Count == 0 ? null : $"Glossary: {string.Join(", ", kept)}.";
    }

    /// <summary>Takes effect from the next utterance; safe to set from any thread (the workspace watcher sets it).</summary>
    public string? Prompt
    {
        get => _prompt;
        set => _prompt = value;
    }

    private (WhisperProcessor Short, WhisperProcessor Long) Build(string? prompt)
    {
        WhisperProcessorBuilder Builder()
        {
            var builder = _factory.CreateBuilder().WithThreads(_threads);
            builder = _language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(_language);
            return prompt is null ? builder : builder.WithPrompt(prompt);
        }
        return (Builder().WithAudioContextSize(ShortContext).Build(), Builder().Build());
    }

    public async Task<string> TranscribeAsync(short[] pcm, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            if (_prompt is var prompt && prompt != _builtWith)
            {
                _short.Dispose();
                _long.Dispose();
                (_short, _long) = Build(prompt);
                _builtWith = prompt;
            }
            var processor = pcm.Length <= ShortMaxSamples ? _short : _long;
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(SampleConvert.ToFloat(pcm), ct))
                text.Append(segment.Text);
            var result = text.ToString().Trim();
            return result.StartsWith('[') && result.EndsWith(']') ? "" : result; // "[BLANK_AUDIO]" etc.
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        // A transcription cancelled by the pipeline may still be unwinding inside the native processor.
        _one.Wait(TimeSpan.FromSeconds(5));
        _short.Dispose();
        _long.Dispose();
        _factory.Dispose();
        _one.Dispose();
    }
}
