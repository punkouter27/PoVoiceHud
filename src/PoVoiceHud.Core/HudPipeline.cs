using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PoVoiceHud.Core.Asr;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Repos;
using PoVoiceHud.Core.Screen;
using PoVoiceHud.Core.Vad;

namespace PoVoiceHud.Core;

/// <summary>One sentence heard: who said it, the clock time they started saying it, and what was said.</summary>
public sealed record TranscriptLine(SourceHint Source, DateTimeOffset At, string Text);

/// <summary>The verdict on one transcript line.</summary>
public sealed record Checked(TranscriptLine Line, Insight Verdict);

/// <summary>What the code repository says about one transcript line: an answer, or a verdict on a statement about the code.</summary>
public sealed record CodeChecked(TranscriptLine Line, CodeFact Fact);

/// <summary>
/// Audio frames → VAD → utterances → Whisper → fact checker, or <see cref="Code"/> for a sentence about the code repository
/// (questions too). Utterances are transcribed one at a time; up to <see cref="ParallelChecks"/> sentences are checked at once. Muting bumps a generation counter so anything already in
/// flight is discarded when it completes. With <see cref="Screen"/>, each check also gets what is on the screen.
/// </summary>
public sealed partial class HudPipeline : IDisposable
{
    private const int ContextLines = 2;
    /// <summary>Checks in flight at once, so a slow answer (up to 8 s) does not hold up the claims said after it.</summary>
    public const int ParallelChecks = 3;

    private readonly IAudioSource _audio;
    private readonly SileroVad _vad;
    private readonly ITranscriber _transcriber;
    private volatile IFactChecker _checker;
    private volatile CodeAnswerer? _code;
    private ScreenWatcher? _screen;
    private bool _started;
    private readonly ILogger<HudPipeline> _log;
    private readonly UtteranceSegmenter _segmenter = new();
    // Not SingleReader: the mute setter drains it from the UI thread while the worker is the normal consumer.
    private readonly Channel<(Utterance Utterance, DateTimeOffset At)> _utterances = Channel.CreateUnbounded<(Utterance, DateTimeOffset)>();
    // Two stages so a slow model call never delays the next transcription.
    private readonly Channel<(TranscriptLine Line, string[] Context, int Generation, bool Code)> _checks = Channel.CreateUnbounded<(TranscriptLine, string[], int, bool)>();
    private readonly Channel<TranscriptLine> _transcript = Channel.CreateUnbounded<TranscriptLine>();
    private readonly Channel<Checked> _verdicts = Channel.CreateUnbounded<Checked>();
    private readonly Channel<CodeChecked> _codeFacts = Channel.CreateUnbounded<CodeChecked>();
    private readonly Channel<string> _status = Channel.CreateUnbounded<string>();
    private readonly Channel<ScreenSeen> _screens = Channel.CreateUnbounded<ScreenSeen>();
    private readonly Channel<(TranscriptLine Line, bool Busy)> _checking = Channel.CreateUnbounded<(TranscriptLine, bool)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _vadGate = new();
    private int _generation;
    private TimeSpan _frameAt;      // the latest frame's position in the audio, to date an utterance by the clock
    private string? _problem;       // why checks are failing, shown once in the header until one succeeds again

    public HudPipeline(IAudioSource audio, SileroVad vad, ITranscriber transcriber, IFactChecker checker, ILogger<HudPipeline> log)
    {
        (_audio, _vad, _transcriber, _checker, _log) = (audio, vad, transcriber, checker, log);
        // Raised while a frame is pushed, so the clock time it started is now less the audio since its start.
        _segmenter.UtteranceReady += u => _utterances.Writer.TryWrite((u, DateTimeOffset.Now - (_frameAt - u.Start)));
        _audio.FrameAvailable += OnFrame;
        _audio.Status += s => _status.Writer.TryWrite(s);
    }

    public ChannelReader<TranscriptLine> Transcript => _transcript.Reader;
    public ChannelReader<Checked> Verdicts => _verdicts.Reader;
    public ChannelReader<CodeChecked> CodeFacts => _codeFacts.Reader;
    public ChannelReader<string> Status => _status.Reader;
    /// <summary>Each new description of the screen, for the session log.</summary>
    public ChannelReader<ScreenSeen> Screens => _screens.Reader;

    /// <summary>The fact checker; replaced when only its settings change, so Whisper and the audio keep running.</summary>
    public IFactChecker Checker
    {
        get => _checker;
        set
        {
            _checker = value;
            Volatile.Write(ref _problem, null);   // the new one gets a fresh chance to report what is wrong
        }
    }

    /// <summary>Answers and checks sentences about the code repository; null without one. Replaced along with <see cref="Checker"/>.</summary>
    public CodeAnswerer? Code
    {
        get => _code;
        set => _code = value;
    }

    /// <summary>
    /// Watches the screen so checks can be judged against it; null when that is switched off. Replaced along with
    /// <see cref="Checker"/>: the old one stops, and the new one starts if the pipeline is running.
    /// </summary>
    public ScreenWatcher? Screen
    {
        get => Volatile.Read(ref _screen);
        set
        {
            var old = Interlocked.Exchange(ref _screen, value);
            old?.Dispose();
            if (value is null) return;
            value.IsPaused = IsMuted;
            value.Seen += seen => _screens.Writer.TryWrite(seen);
            if (Volatile.Read(ref _started)) value.Start();
        }
    }

    /// <summary>A line is queued for the fact checker (Busy) and done with it, with or without a verdict, or dropped by mute.</summary>
    public ChannelReader<(TranscriptLine Line, bool Busy)> Checking => _checking.Reader;

    public bool IsMuted
    {
        get => _audio.IsMuted;
        set
        {
            _audio.IsMuted = value;
            if (Screen is { } screen) screen.IsPaused = value;
            if (value)
            {
                Interlocked.Increment(ref _generation);
                lock (_vadGate)
                {
                    _segmenter.Flush();
                    _vad.Reset();
                    Level = 0;   // no frames arrive while muted, so the meter would otherwise keep the last level
                }
                while (_utterances.Reader.TryRead(out _)) { }
                while (_checks.Reader.TryRead(out var dropped)) _checking.Writer.TryWrite((dropped.Line, false));
            }
            _status.Writer.TryWrite(value ? "Muted" : "Listening");
        }
    }

    public void Start()
    {
        _ = Task.Run(WorkAsync);
        _ = Task.Run(CheckAsync);
        _audio.Start();
        Volatile.Write(ref _started, true);
        Screen?.Start();
    }

    /// <summary>Latest frame loudness on a 0–1 scale (−60 dBFS → 0, 0 dBFS → 1); 0 while muted. For the mic meter.</summary>
    public float Level { get; private set; }

    /// <summary>Who the latest frame most likely came from, for colouring the meter.</summary>
    public SourceHint Speaker { get; private set; }

    public static float LevelOf(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        var rms = Math.Sqrt(sum / Math.Max(1, samples.Length)) / short.MaxValue;
        return rms <= 0 ? 0 : (float)Math.Clamp((20 * Math.Log10(rms) + 60) / 60, 0, 1);
    }

    /// <summary>
    /// <paramref name="text"/> cut after each . ! or ? that is followed by a new sentence; initials ("U.S.") and titles
    /// ("Dr.") do not end one.
    /// </summary>
    public static IEnumerable<string> Sentences(string text) =>
        SentenceBreak().Split(text.Trim()).Select(s => s.Trim()).Where(s => s.Length > 0);

    [GeneratedRegex(@"(?<![A-Z]\.)(?<!\b(?:Mr|Mrs|Ms|Dr|St|Jr|Sr|vs|Prof)\.)(?<=[.!?…])\s+(?=[A-Z0-9""'“(])")]
    private static partial Regex SentenceBreak();

    private void OnFrame(PcmFrame frame)
    {
        lock (_vadGate)
        {
            if (_audio.IsMuted)
            {
                Level = 0;
                return;
            }
            Level = LevelOf(frame.Samples.Span);
            Speaker = frame.Source;
            _frameAt = frame.Timestamp;
            _segmenter.Push(frame, _vad.Process(frame.Samples.Span));
        }
    }

    private async Task WorkAsync()
    {
        var recent = new List<string>();   // the sentences just heard, handed to the checker as context for "it" and "they"
        try
        {
            await foreach (var (utterance, at) in _utterances.Reader.ReadAllAsync(_cts.Token))
            {
                var generation = Volatile.Read(ref _generation);
                try
                {
                    var text = await _transcriber.TranscribeAsync(utterance.Samples, _cts.Token);
                    if (generation != Volatile.Read(ref _generation) || string.IsNullOrWhiteSpace(text)) continue;
                    // Whisper hands over everything said between two pauses; each sentence is its own line with its own verdict.
                    foreach (var sentence in Sentences(text))
                    {
                        var line = new TranscriptLine(utterance.Source, at, sentence);
                        _transcript.Writer.TryWrite(line);
                        // About the code (questions too) goes to the repository; any other claim to the fact checker.
                        var code = Code?.IsAbout(sentence, recent) == true;   // "…in it?" after "…our repo" is about the code too
                        if (code || FactChecker.IsCandidate(sentence))
                        {
                            _checking.Writer.TryWrite((line, true));   // shown as "checking…" from the moment it is queued
                            _checks.Writer.TryWrite((line, recent.ToArray(), generation, code));
                        }
                        recent.Add(sentence);
                        if (recent.Count > ContextLines) recent.RemoveAt(0);
                    }
                }
                catch (Exception e) when (!_cts.IsCancellationRequested)
                {
                    // Includes HttpClient timeouts (TaskCanceledException): the worker must outlive one bad call.
                    _log.LogError(e, "Utterance at {At} failed", utterance.Start);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = ParallelChecks, CancellationToken = _cts.Token };
            await Parallel.ForEachAsync(_checks.Reader.ReadAllAsync(_cts.Token), options, async (item, ct) =>
            {
                var (line, context, generation, code) = item;
                var screen = Screen?.Current;
                try
                {
                    // Not about the code after all (a follow-on that changed the subject): a claim still gets the ordinary check.
                    var fact = code && Code is { } answerer ? await answerer.AskAsync(line.Text, context, screen, ct) : null;
                    if (fact is not null)
                    {
                        Recovered();
                        if (generation == Volatile.Read(ref _generation)) _codeFacts.Writer.TryWrite(new CodeChecked(line, fact));
                    }
                    else if (FactChecker.IsCandidate(line.Text))
                    {
                        var verdict = await Checker.CheckAsync(line.Text, context, screen, ct);
                        Recovered();
                        if (verdict is not null && generation == Volatile.Read(ref _generation)) _verdicts.Writer.TryWrite(new Checked(line, verdict));
                    }
                }
                catch (FactCheckUnavailableException e)
                {
                    // Once in the header, not on every line: the line itself just goes back to plain.
                    if (Interlocked.Exchange(ref _problem, e.Message) != e.Message) _status.Writer.TryWrite($"Fact checks paused: {e.Message}");
                    _log.LogWarning("Checking \"{Text}\" failed: {Problem}", line.Text, e.Message);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    _log.LogError(e, "Checking \"{Text}\" failed", line.Text);
                }
                finally
                {
                    _checking.Writer.TryWrite((line, false));
                }
            });
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    /// <summary>A check went through: if they had been failing, the header says so once.</summary>
    private void Recovered()
    {
        if (Interlocked.Exchange(ref _problem, null) is not null) _status.Writer.TryWrite("Fact checks are working again");
    }

    public void Dispose()
    {
        _cts.Cancel();
        Screen = null;
        _audio.FrameAvailable -= OnFrame;
        _audio.Stop();
        // Readers (the UI's drain loops) end instead of waiting forever on a pipeline that is gone.
        _transcript.Writer.TryComplete();
        _verdicts.Writer.TryComplete();
        _codeFacts.Writer.TryComplete();
        _status.Writer.TryComplete();
        _checking.Writer.TryComplete();
        _screens.Writer.TryComplete();
        _cts.Dispose();
    }
}
