
namespace PoVoiceHud.Core.Transcription;

public interface ITranscriber : IDisposable
{
    /// <summary>16 kHz mono PCM16 → text. Empty string when nothing intelligible was said.</summary>
    Task<string> TranscribeAsync(short[] pcm, CancellationToken ct);
}
