using System.Globalization;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Repos;
using PoVoiceHud.Core.ViewModels;

namespace PoVoiceHud.Core.Tests.ViewModels;

public class HudViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 15, 4, 0, TimeSpan.Zero);
    private readonly List<Uri> _opened = [];
    private readonly HudViewModel _vm;

    public HudViewModelTests()
    {
        _vm = new HudViewModel(openUri: u => { _opened.Add(u); return Task.CompletedTask; });
    }

    private static TranscriptLine Line(string text, int second = 1, SourceHint source = SourceHint.Mic) => new(source, T0.AddSeconds(second), text);

    private static Insight Judged(string claim, Verdict verdict, string reason = "Because.") => new(claim, verdict, reason, 0.9);

    [Fact]
    public void A_verdict_read_off_the_screen_goes_to_Screen_facts_under_what_the_screen_shows_and_not_to_Facts()
    {
        Assert.False(_vm.HasScreenNow);
        _vm.WatchesScreen = true;
        _vm.ShowScreen(new PoVoiceHud.Core.Screen.ScreenSeen(T0, "JSA form: start date 09/28/2026."));
        Assert.True(_vm.HasScreenNow);
        Assert.Equal("JSA form: start date 09/28/2026.", _vm.ScreenNow);
        Assert.Equal($"On screen since {T0.ToString("t", CultureInfo.CurrentCulture)}", _vm.ScreenSince);

        var line = Line("The work starts on October 1st.");
        _vm.AddTranscript(line);
        _vm.Push(new Checked(line, new Insight("The work starts on October 1st", Verdict.Contradicted, "On screen: The form says 09/28/2026.", 0.9, OnScreen: true)));
        _vm.Push(new Checked(Line("Paris is in France.", 2), Judged("Paris is in France", Verdict.Supported)));

        var onScreen = Assert.Single(_vm.ScreenFacts);
        Assert.Equal("The work starts on October 1st", onScreen.Claim);
        Assert.Equal("Paris is in France", Assert.Single(_vm.Facts).Claim);
        Assert.Equal("0 correct · 1 incorrect", _vm.ScreenSummary);
        Assert.Equal(LineState.Incorrect, _vm.Live[0].State);   // the Live line is marked as any other

        _vm.Page = HudPage.Screen;
        Assert.True(_vm.CanCopy);
        Assert.Equal("❌ Incorrect: The work starts on October 1st — On screen: The form says 09/28/2026.", _vm.CopyText);
    }

    [Fact]
    public void A_line_shows_checking_while_it_is_with_the_checker_then_its_verdict_in_plain_words()
    {
        var line = Line("Python was created by Dennis Ritchie.");
        _vm.AddTranscript(line);
        var live = Assert.Single(_vm.Live);
        Assert.Equal(LineState.Heard, live.State);
        Assert.False(live.HasNote);

        _vm.MarkChecking(line, busy: true);
        Assert.Equal("Checking…", live.Note);
        Assert.True(_vm.IsChecking);

        _vm.Push(new Checked(line, Judged("Python was created by Dennis Ritchie", Verdict.Contradicted, "Guido van Rossum created Python.")));
        _vm.MarkChecking(line, busy: false);
        Assert.Equal(LineState.Incorrect, live.State);
        Assert.Equal("Incorrect — Guido van Rossum created Python.", live.Note);
        Assert.False(_vm.IsChecking);
        Assert.Equal("❌ Python was created by Dennis Ritchie", _vm.Headline);   // the pill shows the latest verdict

        var chatter = Line("I think we should keep going with this.", 5);
        _vm.AddTranscript(chatter);
        _vm.MarkChecking(chatter, busy: true);
        _vm.MarkChecking(chatter, busy: false);   // the checker said it is not a claim: no mark at all
        Assert.Equal(LineState.Heard, _vm.Live[^1].State);

        var vague = Line("Octopuses dream in colour.", 9);
        _vm.AddTranscript(vague);
        _vm.Push(new Checked(vague, Judged("Octopuses dream in colour", Verdict.Unverified, "Unknown.")));
        Assert.Equal("Can't tell — Unknown.", _vm.Live[^1].Note);
        Assert.Equal("❌ Python was created by Dennis Ritchie", _vm.Headline);   // "can't tell" does not take over the headline
    }

    [Fact]
    public void Facts_keep_only_correct_and_incorrect_claims_newest_first_with_repeats_counted()
    {
        void Say(string claim, Verdict verdict, int minute)
        {
            var line = Line(claim, minute * 60);
            _vm.AddTranscript(line);
            _vm.Push(new Checked(line, Judged(claim, verdict)));
        }

        Say("The Eiffel Tower was completed in 1889", Verdict.Supported, 1);
        Say("Octopuses dream in colour", Verdict.Unverified, 2);
        Say("Python was created by Dennis Ritchie", Verdict.Contradicted, 3);
        Say("the eiffel tower was completed in 1889", Verdict.Supported, 4);

        Assert.Equal(["The Eiffel Tower was completed in 1889 ×2", "Python was created by Dennis Ritchie"], _vm.Facts.Select(f => f.Title));
        Assert.Equal("1 correct · 1 incorrect", _vm.FactsSummary);
        Assert.Equal(LineState.CantTell, _vm.Live[1].State);   // "can't tell" shows on the Live page only
        Assert.Equal((2, 1, 1), (_vm.Supported, _vm.Contradicted, _vm.Unverified));
        Assert.Equal("2 correct · 1 incorrect · 1 can't tell", _vm.TallySummary);
        Assert.Equal(T0.AddMinutes(4).ToString("t", CultureInfo.CurrentCulture), _vm.Facts[0].Time);   // a repeat shows when it was said again
    }

    [Fact]
    public async Task Copy_facts_writes_one_line_per_fact_and_a_fact_links_its_source()
    {
        var wiki = new Uri("https://en.wikipedia.org/wiki/Eiffel_Tower");
        var line = Line("The Eiffel Tower was completed in 1889.");
        _vm.AddTranscript(line);
        _vm.Push(new Checked(line, new Insight("The Eiffel Tower was completed in 1889", Verdict.Supported, "Completed in March 1889.", 0.9, wiki, LinkChecked: true)));

        var fact = Assert.Single(_vm.Facts);
        Assert.Equal("en.wikipedia.org ↗ (looked up)", fact.LinkLabel);
        _vm.Page = HudPage.Facts;
        Assert.True(_vm.CanCopy);
        Assert.Equal("✅ Correct: The Eiffel Tower was completed in 1889 — Completed in March 1889. (https://en.wikipedia.org/wiki/Eiffel_Tower)", _vm.CopyText);

        await fact.OpenLinkCommand.ExecuteAsync(null);
        Assert.Equal(wiki, Assert.Single(_opened));

        // A file of the code repository reads as its name, wherever it opens.
        foreach (var file in new[] { "https://github.com/acme/pmis/blob/HEAD/src/InvoiceService.cs", "https://dev.azure.com/acme/PMIS/_git/pmis?path=/src/InvoiceService.cs", "file:///C:/src/pmis/src/InvoiceService.cs" })
        {
            var said = Line("The invoice service retries three times.", 5);
            _vm.AddTranscript(said);
            _vm.Push(new Checked(said, new Insight($"The invoice service retries three times ({file})", Verdict.Contradicted, "It does not retry.", 0.9, new Uri(file), LinkChecked: true)));
            Assert.Equal("InvoiceService.cs ↗ (looked up)", _vm.Facts[0].LinkLabel);
        }
    }

    [Fact]
    public async Task The_code_answers_a_question_and_judges_a_statement_on_the_line_and_on_code_facts()
    {
        var web = new Uri("https://github.com/acme/site/blob/HEAD/docs/index.html");
        var editor = new Uri("vscode://file/C:/src/site/docs/index.html:1");
        var question = Line("How many pages are on our website?", 1);
        _vm.AddTranscript(question);
        _vm.MarkChecking(question, busy: true);
        _vm.PushCode(new CodeChecked(question, new CodeFact("How many pages are on the website", null, "Nine.", [new CodeSource("docs/index.html", web, editor)])));
        _vm.MarkChecking(question, busy: false);
        var statement = Line("The invoice service retries three times.", 2);
        _vm.AddTranscript(statement);
        _vm.PushCode(new CodeChecked(statement, new CodeFact("The invoice service retries three times", Verdict.Contradicted, "It does not retry.", [])));

        Assert.Equal((LineState.Answered, "Answer — Nine."), (_vm.Live[0].State, _vm.Live[0].Note));
        Assert.Equal((LineState.Incorrect, "Incorrect — It does not retry."), (_vm.Live[1].State, _vm.Live[1].Note));
        Assert.Equal(["The invoice service retries three times", "How many pages are on the website"], _vm.CodeFacts.Select(f => f.Topic));   // newest first
        Assert.Empty(_vm.Facts);   // Facts keeps the claims checked against Wikipedia
        Assert.Equal("1 answer · 1 checked", _vm.CodeSummary);
        Assert.Equal((0, 1, 0), (_vm.Supported, _vm.Contradicted, _vm.Unverified));   // an answer is not a verdict
        Assert.Equal("❌ The invoice service retries three times", _vm.Headline);

        var source = Assert.Single(_vm.CodeFacts[1].Sources);
        Assert.Equal(("index.html ↗", "docs"), (source.Label, source.Folder));
        await source.OpenCommand.ExecuteAsync(null);
        await source.OpenInEditorCommand.ExecuteAsync(null);
        Assert.Equal([web, editor], _opened);

        var unsure = Line("The invoice service logs every payment.", 3);
        _vm.AddTranscript(unsure);
        _vm.PushCode(new CodeChecked(unsure, new CodeFact("The invoice service logs every payment", Verdict.Unverified, "The excerpts do not show it.", [])));
        Assert.Equal(LineState.CantTell, _vm.Live[2].State);
        Assert.Equal(2, _vm.CodeFacts.Count);   // "can't tell" is a note on its line, not a code fact

        _vm.Page = HudPage.Code;
        Assert.Equal("❌ Incorrect: The invoice service retries three times — It does not retry.\n💬 Answer: How many pages are on the website — Nine. (docs/index.html)".Replace("\n", Environment.NewLine), _vm.CopyText);
    }

    [Fact]
    public void The_live_page_shows_first_settings_replace_the_pages_and_setup_keeps_them_shown()
    {
        Assert.True(_vm.ShowsLive);
        _vm.Page = HudPage.Facts;
        Assert.False(_vm.ShowsLive);
        Assert.True(_vm.ShowsFacts && _vm.ShowsList);
        Assert.False(_vm.CanCopy);   // nothing to copy yet
        _vm.Page = HudPage.Code;
        Assert.True(_vm.ShowsCode);
        Assert.Contains("Connect a code repository", _vm.NoCodeHint);
        _vm.HasRepository = true;
        Assert.StartsWith("No code facts yet", _vm.NoCodeHint);
        _vm.Page = HudPage.Facts;

        _vm.ToggleSettingsCommand.Execute(null);
        Assert.True(_vm.ShowsSettings);
        Assert.False(_vm.ShowsPages || _vm.ShowsFacts);
        _vm.ToggleSettingsCommand.Execute(null);
        Assert.True(_vm.ShowsFacts);

        _vm.NeedsSetup = true;
        _vm.ToggleSettingsCommand.Execute(null);   // ⚙ cannot leave the settings until setup is done
        Assert.True(_vm.ShowsSettings);
        Assert.False(_vm.IsSettingsOpen);
        _vm.NeedsSetup = false;

        for (var i = 1; i <= HudViewModel.LiveSize + 5; i++) _vm.AddTranscript(Line($"line {i}", i));
        Assert.Equal(HudViewModel.LiveSize, _vm.Live.Count);
        Assert.Equal("line 6", _vm.Live[0].Text);
    }

    [Fact]
    public void Lines_say_who_spoke_and_a_new_pipeline_clears_checking()
    {
        _vm.AddTranscript(Line("first line here", 1));
        Assert.Equal("You:", _vm.Live[0].Speaker);

        var theirs = Line("second line here", 62, SourceHint.Teams);
        _vm.AddTranscript(theirs);
        Assert.Equal("Them:", _vm.Live[1].Speaker);
        Assert.Equal(T0.AddSeconds(62).ToString("t", CultureInfo.CurrentCulture), _vm.Live[1].Stamp);   // the clock time, as on Facts

        _vm.MarkChecking(theirs, busy: true);
        _vm.ResetChecking();
        Assert.Equal(LineState.Heard, _vm.Live[1].State);
        Assert.False(_vm.IsChecking);

        // Named for this call: every row says the new name at once; a blank name is "Them" again.
        _vm.Push(new Checked(theirs, Judged("second line here", Verdict.Supported)));
        var renamed = new List<string?>();
        _vm.Live[1].PropertyChanged += (_, e) => renamed.Add(e.PropertyName);
        _vm.Names.Them = "Alex";
        Assert.Equal(("Alex:", "Alex"), (_vm.Live[1].Speaker, _vm.Facts[0].Speaker));
        Assert.Contains(nameof(LiveLineViewModel.Speaker), renamed);
        _vm.Names.Them = " ";
        Assert.Equal("Them:", _vm.Live[1].Speaker);
    }

    [Fact]
    public void The_lists_filter_by_words_kind_or_speaker_and_the_header_follows_each_verdict()
    {
        var cues = new List<Cue>();
        _vm.Arrived += cues.Add;
        var said = new[]
        {
            (Line("Paris is in France.", 1), Verdict.Supported),
            (Line("The Moon is bigger than Earth.", 2, SourceHint.Teams), Verdict.Contradicted),
            (Line("Water boils at 100 degrees.", 3), Verdict.Supported),
        };
        foreach (var (line, verdict) in said)
        {
            _vm.AddTranscript(line);
            _vm.Push(new Checked(line, Judged(line.Text.TrimEnd('.'), verdict)));
        }
        var question = Line("How many pages are on our website?", 4);
        _vm.AddTranscript(question);
        _vm.PushCode(new CodeChecked(question, new CodeFact("How many pages are on the website", null, "Nine.", [], "site", 0.7)));

        Assert.Equal([Cue.Supported, Cue.Contradicted, Cue.Supported, Cue.Answer], cues);   // an answer has its own sound
        Assert.Equal([1, 0.5, 2 / 3.0], _vm.Trend);                                           // share correct after each verdict
        Assert.Equal(3, _vm.ShownFacts.Count);

        _vm.FilterText = "moon";
        Assert.Equal("The Moon is bigger than Earth", Assert.Single(_vm.ShownFacts).Claim);
        _vm.FilterText = "";
        _vm.FilterKind = 1;   // correct
        Assert.Equal(["Water boils at 100 degrees", "Paris is in France"], _vm.ShownFacts.Select(f => f.Claim));
        Assert.Empty(_vm.ShownCodeFacts);
        _vm.FilterKind = 5;   // said by them
        Assert.Equal("The Moon is bigger than Earth", Assert.Single(_vm.ShownFacts).Claim);
        _vm.FilterKind = 3;   // answers
        Assert.Equal(0.7, Assert.Single(_vm.ShownCodeFacts).Confidence);
        Assert.Empty(_vm.ShownFacts);
        Assert.Equal("Nothing matches the filter.", _vm.NoFactsHint);
        _vm.FilterKind = 0;
        Assert.Equal(3, _vm.ShownFacts.Count);

        _vm.ToggleCompactCommand.Execute(null);
        Assert.True(_vm.IsCompact);
    }

    [Theory]
    [InlineData("Close your border. It'll be the end! Was it?", new[] { "Close your border.", "It'll be the end!", "Was it?" })]
    [InlineData("The U.S. President met Dr. Smith in 2019. Then he left.", new[] { "The U.S. President met Dr. Smith in 2019.", "Then he left." })]
    [InlineData("  we ask for…  ", new[] { "we ask for…" })]
    public void A_transcription_is_split_into_sentences_but_not_at_initials_or_titles(string text, string[] expected)
    {
        Assert.Equal(expected, HudPipeline.Sentences(text));
    }

    [Theory]
    [InlineData(0, 0.0)]          // silence
    [InlineData(1036, 0.5)]       // -30 dBFS ≈ normal speech → mid-scale
    public void Mic_level_maps_rms_to_a_0_to_1_db_scale(short amplitude, double expected)
    {
        var frame = new short[160];
        Array.Fill(frame, amplitude);
        Assert.Equal(expected, HudPipeline.LevelOf(frame), 2);
    }

    [Fact]
    public void Mute_toggle_flips_the_flag_and_the_pill_shows_the_latest_status()
    {
        _vm.SetStatus("Listening");
        Assert.Equal("Listening", _vm.Headline);

        _vm.ToggleMuteCommand.Execute(null);
        Assert.True(_vm.IsMuted);
        Assert.Equal("Muted", _vm.Headline);

        _vm.ToggleMuteCommand.Execute(null);
        Assert.Equal("Listening", _vm.Headline);

        // "Listen only during calls" knows a call app by the process recording; this app and a music player are not calls.
        Assert.Equal("Teams", CallDetector.Find(["PoVoiceHud.App", "ms-teams"]));
        Assert.Equal("Zoom", CallDetector.Find(["Zoom"]));
        Assert.Null(CallDetector.Find(["Spotify", "PoVoiceHud.App"]));
    }
}
