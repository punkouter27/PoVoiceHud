namespace PoVoiceHud.Core.Asr;

/// <summary>A Whisper model the app can download: what Settings calls it, its file, and the SHA-256 it is checked against.</summary>
public sealed record WhisperModel(string Id, string Label, string File, string Sha256, long Bytes, bool English);

/// <summary>
/// The Whisper models on offer, all from ggerganov/whisper.cpp on Hugging Face at one pinned commit, so a file cannot change
/// underneath its hash. base.en is the default: on a CPU it keeps up with speech at about 0.1 s a sentence.
/// </summary>
public static class WhisperModels
{
    public const string Default = "base.en";

    private const string Commit = "5359861c739e955e79d9a303bcbc70fb988958b1";

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new(Default, "Fast — base.en (148 MB)", "ggml-base.en.bin", "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", 147_964_211, English: true),
        new("small.en", "More accurate — small.en (264 MB)", "ggml-small.en-q8_0.bin", "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067", 264_477_561, English: true),
        new("large-v3-turbo", "Most accurate — large-v3 turbo (574 MB, best with a graphics card)", "ggml-large-v3-turbo-q5_0.bin",
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574_041_195, English: false),
    ];

    /// <summary>The model of that id, or the default for an id this version does not know.</summary>
    public static WhisperModel Find(string? id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];

    public static Uri Url(WhisperModel model) => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Commit}/{model.File}");
}
