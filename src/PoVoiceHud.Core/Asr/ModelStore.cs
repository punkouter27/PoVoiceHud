using System.Security.Cryptography;

namespace PoVoiceHud.Core.Asr;

/// <param name="Voices">The speaker model that tells the call's voices apart; null when that is switched off.</param>
public readonly record struct ModelPaths(string Whisper, string Silero, string? Voices = null);

/// <summary>
/// Downloads the chosen Whisper model (GGML), the Silero VAD model and, to tell voices apart, the WeSpeaker model once into
/// %LOCALAPPDATA%\PoVoiceHud\models. Every download is checked against a pinned SHA-256 before it is put in place.
/// </summary>
public static class ModelStore
{
    // Pinned to a commit, not a branch, so the file cannot change underneath the hash.
    private const string SileroUrl = "https://github.com/snakers4/silero-vad/raw/1e261b036686cd0017d500ee96acd1c4ba572a9d/src/silero_vad/data/silero_vad.onnx";
    private const string SileroSha256 = "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3";
    // WeSpeaker's ResNet34 trained on VoxCeleb (Apache-2.0), as sherpa-onnx publishes it: 80 log-mel bins in, a 256-number voice print out.
    private const string VoicesUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx";
    private const string VoicesSha256 = "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012";

    public static string Directory { get; } = Path.Combine(Config.AppPaths.DataDir, "models");

    /// <summary>base.en and the VAD model: what the tests and a first run without settings need.</summary>
    public static Task<ModelPaths> EnsureAsync(IProgress<double>? progress, CancellationToken ct) => EnsureAsync(WhisperModels.Default, voices: false, progress, ct);

    /// <param name="whisper">A <see cref="WhisperModels"/> id; an unknown one means the default.</param>
    /// <param name="voices">Also the speaker model (26 MB).</param>
    public static async Task<ModelPaths> EnsureAsync(string? whisper, bool voices, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var model = WhisperModels.Find(whisper);
        var whisperPath = Path.Combine(Directory, model.File);
        var silero = Path.Combine(Directory, "silero_vad.onnx");
        var voicesPath = voices ? Path.Combine(Directory, "wespeaker_resnet34.onnx") : null;

        // Only the Whisper download reports progress; the small models would just make the gauge jump.
        await Task.WhenAll(
            Present(silero) ? Task.CompletedTask : DownloadAsync(new Uri(SileroUrl), silero, SileroSha256, 0, null, ct),
            voicesPath is null || Present(voicesPath) ? Task.CompletedTask : DownloadAsync(new Uri(VoicesUrl), voicesPath, VoicesSha256, 0, null, ct),
            Present(whisperPath) ? Task.CompletedTask : DownloadAsync(WhisperModels.Url(model), whisperPath, model.Sha256, model.Bytes, progress, ct));
        progress?.Report(1);
        return new ModelPaths(whisperPath, silero, voicesPath);
    }

    private static async Task DownloadAsync(Uri url, string path, string sha256, long size, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // a large model on a slow line takes minutes
        await using var src = await http.GetStreamAsync(url, ct);
        await SaveAsync(src, path, sha256, size, progress, ct);
    }

    private static bool Present(string path) => File.Exists(path) && new FileInfo(path).Length > 1_000_000;

    private static async Task SaveAsync(Stream src, string path, string sha256, long size, IProgress<double>? progress, CancellationToken ct)
    {
        var tmp = path + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                if (size > 0) progress?.Report(Math.Min(0.99, (double)total / size));
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (actual != sha256)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"{Path.GetFileName(path)} failed its integrity check (SHA-256 {actual[..12]}…): the download was discarded.");
        }
        File.Move(tmp, path, overwrite: true);
    }
}
