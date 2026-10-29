using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;

namespace PoVoiceHud.App;

public sealed partial class MainWindow : WindowEx
{
    private static readonly Brush None = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private bool _follow = true;  // the Live page is scrolled to the end, so new lines keep it there
    private LiveLineViewModel? _slid;   // the newest line that has slid in: a line scrolled back into view does not slide again
    private Storyboard? _pulse;
    private double _fullHeight;   // the window's height before compact mode, in DIPs

    public MainWindow(HudViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AppWindow.SetIcon(IconPath);
        // The header is the title bar: one 48 px row, dragged by the headline, with the caption buttons drawn over its right end.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Root.SizeChanged += (_, _) => KeepClearOfCaptionButtons();
        PageSwitch.SelectedItem = Tab(ViewModel.Page);   // from the view model, not the markup, so a page chosen before load sticks
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HudViewModel.MicLevel)) ShowLevel(ViewModel.MicLevel);
            if (e.PropertyName == nameof(HudViewModel.Page)) PageSwitch.SelectedItem = Tab(ViewModel.Page);
            if (e.PropertyName == nameof(HudViewModel.Trend)) DrawTrend();
            if (e.PropertyName == nameof(HudViewModel.IsChecking)) Pulse(ViewModel.IsChecking);
            if (e.PropertyName == nameof(HudViewModel.IsCompact)) Compact(ViewModel.IsCompact);
        };
        // New lines and verdicts make the list taller; while it is scrolled to the end it stays there. Scrolling up stops that.
        LiveScroll.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) _follow = LiveScroll.VerticalOffset >= LiveScroll.ScrollableHeight - 40;
        };
        LiveList.SizeChanged += (_, _) =>
        {
            if (_follow) LiveScroll.ChangeView(null, LiveScroll.ScrollableHeight, null, disableAnimation: true);
        };
    }

    public HudViewModel ViewModel { get; }

    /// <summary>The highlight fades in rather than appearing; off with "Sounds and animations" or Windows' animation switch.</summary>
    public bool Animations { get; set; } = true;

    /// <summary>The app icon, copied next to the exe: the window, the tray and the installer all use it.</summary>
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "PoVoiceHud.ico");

    /// <summary>The caption buttons' width is in physical pixels and changes with the display's scale.</summary>
    private void KeepClearOfCaptionButtons()
    {
        if (Content.XamlRoot is { RasterizationScale: > 0 } root)
            CaptionSpace.Width = new GridLength(AppWindow.TitleBar.RightInset / root.RasterizationScale);
    }

    private void PageSwitch_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Page = sender.SelectedItem == FactsTab ? HudPage.Facts : sender.SelectedItem == CodeTab ? HudPage.Code
            : sender.SelectedItem == HistoryTab ? HudPage.History : HudPage.Live;

    private SelectorBarItem Tab(HudPage page) => page switch { HudPage.Facts => FactsTab, HudPage.Code => CodeTab, HudPage.History => HistoryTab, _ => LiveTab };

    /// <summary>The highlight colour of a line fades in when its verdict lands, and the newest line slides up into place.</summary>
    private void Live_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Border border) return;
        border.BackgroundTransition = Animations ? new BrushTransition { Duration = TimeSpan.FromMilliseconds(350) } : null;
        if (!Animations || args.Index != ViewModel.Live.Count - 1 || ViewModel.Live[args.Index] == _slid) return;
        _slid = ViewModel.Live[args.Index];
        // On the compositor, so it costs the UI thread nothing: from 10 px lower and transparent, over a fifth of a second.
        ElementCompositionPreview.SetIsTranslationEnabled(border, true);
        var visual = ElementCompositionPreview.GetElementVisual(border);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 10, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = TimeSpan.FromMilliseconds(220);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Translation", rise);
        visual.StartAnimation(nameof(visual.Opacity), fade);
    }

    /// <summary>The level bars breathe while a claim is with the model, so a glance says it is still working.</summary>
    private void Pulse(bool on)
    {
        _pulse?.Stop();
        _pulse = null;
        LevelBars.Opacity = 1;
        if (!on || !Animations) return;
        var breathe = new DoubleAnimation { From = 1, To = 0.45, Duration = TimeSpan.FromMilliseconds(700), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(breathe, LevelBars);
        Storyboard.SetTargetProperty(breathe, nameof(UIElement.Opacity));
        _pulse = new Storyboard { Children = { breathe } };
        _pulse.Begin();
    }

    /// <summary>A brief red wash across the header: a claim was just found incorrect.</summary>
    public void FlashIncorrect()
    {
        if (!Animations) return;
        var wash = new DoubleAnimationUsingKeyFrames();
        wash.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(120), Value = 1 });
        wash.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(700), Value = 0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Storyboard.SetTarget(wash, Flash);
        Storyboard.SetTargetProperty(wash, nameof(UIElement.Opacity));
        new Storyboard { Children = { wash } }.Begin();
    }

    /// <summary>The share found correct after each recent verdict, as a line: top is all correct, bottom none.</summary>
    private void DrawTrend()
    {
        var trend = ViewModel.Trend;
        var points = new PointCollection();
        const double width = 44, height = 18, inset = 2;
        for (var i = 0; i < trend.Count; i++)
            points.Add(new Windows.Foundation.Point(trend.Count == 1 ? width / 2 : i * width / (trend.Count - 1), inset + (1 - trend[i]) * (height - 2 * inset)));
        Spark.Points = points;
        Spark.Stroke = Theme(trend.Count > 0 && trend[^1] < 0.5 ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush");
    }

    /// <summary>
    /// Compact: the window shrinks to its header, a strip to keep beside a call; the full height comes back after. The
    /// minimum height gives way while compact.
    /// </summary>
    private void Compact(bool compact)
    {
        if (compact)
        {
            _fullHeight = Height;
            MinHeight = 48;
            Height = 48;
        }
        else
        {
            MinHeight = 320;
            Height = Math.Max(320, _fullHeight);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(ViewModel.CopyText);
        Clipboard.SetContent(data);
        CopyText.Text = "Copied";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => CopyText.Text = "Copy";
        timer.Start();
    }

    // Level (0 = −60 dBFS, 1 = full scale) at which each of the five bars lights. Room noise sits near 0.15–0.2 and
    // ordinary speech around 0.4–0.6, so silence shows nothing, talking shows three or four bars, and five means loud.
    private static readonly double[] BarThresholds = [0.25, 0.34, 0.43, 0.52, 0.62];

    private void ShowLevel(double level)
    {
        for (var i = 0; i < BarThresholds.Length; i++)
            LevelBars.Children[i].Opacity = level >= BarThresholds[i] ? 1 : 0.2;
    }

    /// <summary>The Settings page, shown in place of Live and Facts while it is open or while setup is needed.</summary>
    public void AttachSettings(SettingsViewModel settings, Action quit) => SettingsHost.Content = new Views.SettingsView(settings) { Quit = quit };

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static string MicName(bool muted) => muted ? "Microphone is muted — unmute" : "Microphone is live — mute";

    public static string MicTip(string status, bool muted) => $"{status}\n{(muted ? "Click to unmute" : "Click to mute")}";

    public static string CompactName(bool compact) => compact ? "Show the pages" : "Compact: just the header";

    public static string CompactGlyph(bool compact) => compact ? "" : "";   // full screen / back to window

    public static Visibility Shown(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>A model confidence (0–1) on a progress bar's 0–100.</summary>
    public static double Percent(double confidence) => Math.Clamp(confidence, 0, 1) * 100;

    /// <summary>The summary's Markdown as plain text to read: headings as lines of their own, bullets as dots.</summary>
    public static string Plain(string? markdown) => string.Join('\n', (markdown ?? "").Split('\n')
        .Where(line => !line.StartsWith("## ", StringComparison.Ordinal))
        .Select(line => line.StartsWith("### ", StringComparison.Ordinal) ? $"\n{line[4..].ToUpperInvariant()}"
            : line.StartsWith("- ", StringComparison.Ordinal) ? $"• {line[2..]}" : line)).Trim();

    /// <summary>A past day's line: a verdict or answer in its colour, what was said plain.</summary>
    public static Brush HistoryBack(LineState kind) => kind == LineState.Heard ? None : kind == LineState.CantTell ? Theme("SubtleFillColorSecondaryBrush") : LineBack(kind);

    /// <summary>One verdict's share of the bar along the header's bottom edge; with no verdicts yet the bar is empty.</summary>
    public static GridLength Share(int count) => new(count, GridUnitType.Star);

    public static Brush LineBack(LineState state) => state switch
    {
        LineState.Correct => Theme("SystemFillColorSuccessBackgroundBrush"),
        LineState.Incorrect => Theme("SystemFillColorCriticalBackgroundBrush"),
        LineState.Checking => Theme("SubtleFillColorSecondaryBrush"),
        LineState.Answered => Theme("SystemFillColorAttentionBackgroundBrush"),
        _ => None,   // "can't tell" gets a note, not a block of colour: it is not a finding
    };

    /// <summary>Claims in the ordinary text colour; everything that is not one, dimmed, so the claims stand out.</summary>
    public static Brush LineText(LineState state) => Theme(state == LineState.Heard ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");

    public static Brush LineFore(LineState state) => state switch
    {
        LineState.Correct => Theme("SystemFillColorSuccessBrush"),
        LineState.Incorrect => Theme("SystemFillColorCriticalBrush"),
        LineState.CantTell => Theme("SystemFillColorCautionBrush"),
        LineState.Answered => Theme("SystemFillColorAttentionBrush"),
        _ => Theme("TextFillColorSecondaryBrush"),
    };

    public static string LineGlyph(LineState state) => state switch
    {
        LineState.Correct => "\uE73E",     // check mark
        LineState.Incorrect => "\uE711",   // cross
        LineState.CantTell => "\uE7BA",    // warning
        LineState.Answered => "\uE943",    // code: answered from the repository
        _ => "\uE895",                     // the sync arrows: being checked
    };

    public static Brush FactBack(bool correct) => LineBack(correct ? LineState.Correct : LineState.Incorrect);

    public static Brush FactFore(bool correct) => LineFore(correct ? LineState.Correct : LineState.Incorrect);

    public static string FactGlyph(bool correct) => LineGlyph(correct ? LineState.Correct : LineState.Incorrect);

    /// <summary>A code fact looks like its line: an answer in the accent colour, a verdict as on Facts ("can't tell" plain).</summary>
    private static LineState CodeState(Core.Providers.Verdict? verdict) => verdict switch
    {
        null => LineState.Answered,
        Core.Providers.Verdict.Supported => LineState.Correct,
        Core.Providers.Verdict.Contradicted => LineState.Incorrect,
        _ => LineState.CantTell,
    };

    public static Brush CodeBack(Core.Providers.Verdict? verdict) => CodeState(verdict) == LineState.CantTell ? Theme("SubtleFillColorSecondaryBrush") : LineBack(CodeState(verdict));

    public static Brush CodeFore(Core.Providers.Verdict? verdict) => LineFore(CodeState(verdict));

    public static string CodeGlyph(Core.Providers.Verdict? verdict) => LineGlyph(CodeState(verdict));

    private static Brush Theme(string key) => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : None;

#if DEBUG
    /// <summary>
    /// A conversation with every kind of line, for layout checks: run a Debug build with --demo (--facts for the Facts page,
    /// --code for Code facts).
    /// </summary>
    public void SeedDemo()
    {
        var now = DateTimeOffset.Now;
        ViewModel.SetStatus("Listening to you and the call");
        ViewModel.HasRepository = true;
        (SourceHint Source, int At, string Text, Core.Providers.Verdict? Verdict, string Reason)[] said =
        [
            (SourceHint.Teams, 241, "Okay, let's get started with the roadmap review.", null, ""),
            (SourceHint.Mic, 246, "The Eiffel Tower was completed in 1889.", Core.Providers.Verdict.Supported, "It was finished in March 1889 for the Exposition Universelle."),
            (SourceHint.Teams, 250, "Right, and I think we should keep that example in the deck.", null, ""),
            (SourceHint.Mic, 255, "Python was created by Dennis Ritchie.", Core.Providers.Verdict.Contradicted, "Guido van Rossum created Python; Dennis Ritchie created C."),
            (SourceHint.Teams, 262, "Octopuses dream in colour, apparently.", Core.Providers.Verdict.Unverified, "They change colour in their sleep, but whether they dream is unknown."),
        ];
        foreach (var (source, at, text, verdict, reason) in said)
        {
            var line = new TranscriptLine(source, now.AddSeconds(at - 270), text);
            ViewModel.AddTranscript(line);
            if (verdict is not { } v) continue;
            ViewModel.MarkChecking(line, busy: true);
            var link = v == Core.Providers.Verdict.Unverified ? null
                : new Uri($"https://en.wikipedia.org/wiki/{(v == Core.Providers.Verdict.Supported ? "Eiffel_Tower" : "Python_(programming_language)")}");
            ViewModel.Push(new Checked(line, new Core.Providers.Insight(text.TrimEnd('.'), v, reason, 0.9, link, LinkChecked: true)));
            ViewModel.MarkChecking(line, busy: false);
        }

        // About the code: a question answered and a statement judged from the repository, then a claim still with the checker.
        static Core.Repos.CodeSource File(string path, int line) => new(path, new Uri($"https://github.com/acme/site/blob/HEAD/{path}"),
            new Uri($"vscode://file/C:/src/site/{path}:{line}"));
        (int At, string Text, Core.Repos.CodeFact Fact)[] code =
        [
            (264, "How many pages are on our website?", new("How many pages are on the website", null,
                "Nine: docs/index.html and the eight privacy pages under docs/privacy.", [File("docs/index.html", 1), File("docs/privacy/pobox.html", 1)])),
            (266, "The invoice service retries failed payments three times.", new("The invoice service retries failed payments three times",
                Core.Providers.Verdict.Contradicted, "InvoiceService has no retry logic: a failed payment is logged and returned.", [File("src/Services/InvoiceService.cs", 3)])),
        ];
        foreach (var (at, text, fact) in code)
        {
            var line = new TranscriptLine(SourceHint.Mic, now.AddSeconds(at - 270), text);
            ViewModel.AddTranscript(line);
            ViewModel.PushCode(new CodeChecked(line, fact));
        }
        var pending = new TranscriptLine(SourceHint.Mic, now.AddSeconds(-2), "Light from the Sun takes about eight minutes to reach Earth.");
        ViewModel.AddTranscript(pending);
        ViewModel.MarkChecking(pending, busy: true);
        ViewModel.MicLevel = 0.5;   // an ordinary speaking voice, so the meter is visible in the sample
    }
#endif
}
