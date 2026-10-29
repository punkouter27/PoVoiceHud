using Microsoft.Extensions.Logging.Abstractions;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Screen;

namespace PoVoiceHud.Core.Tests.Screen;

public class ScreenWatcherTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FakeVision(Func<int, string> reply) : IVisionModel
    {
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string system, string user, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
        {
            Assert.True(jpeg.Length > 0);
            return Task.FromResult(reply(++Calls));
        }
    }

    /// <summary>A 320×180 grey screen with a white box of the given size in its top-left corner.</summary>
    private static ScreenShot Shot(int box)
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels.AsSpan((y * width + x) * 4, 4).Fill(x < box && y < box ? (byte)255 : (byte)60);
        return new ScreenShot(width, height, pixels);
    }

    [Fact]
    public async Task The_model_is_asked_only_when_the_screen_has_changed_and_its_description_becomes_current()
    {
        var screens = new Queue<ScreenShot>([Shot(0), Shot(0), Shot(6), Shot(120)]);   // the same, the same, a caret's worth, a new page
        var model = new FakeVision(n => $$"""{"screen":"Page {{n}}: JSA form, start date 09/28/2026"}""");
        using var watcher = new ScreenWatcher(screens.Dequeue, model, NullLogger.Instance);
        var seen = new List<ScreenSeen>();
        watcher.Seen += seen.Add;

        for (var i = 0; i < 4; i++) await watcher.LookAsync(_ct);

        Assert.Equal(2, model.Calls);
        Assert.Equal("Page 2: JSA form, start date 09/28/2026", watcher.Current);
        Assert.Equal(["Page 1: JSA form, start date 09/28/2026", "Page 2: JSA form, start date 09/28/2026"], seen.Select(s => s.Summary));
    }

    [Fact]
    public async Task A_failed_read_forgets_the_old_description_and_tries_again_on_the_next_capture()
    {
        var model = new FakeVision(n => n == 2 ? throw new HttpRequestException("down") : """{"screen":"A form"}""");
        var screens = new Queue<ScreenShot>([Shot(0), Shot(120), Shot(120)]);
        using var watcher = new ScreenWatcher(screens.Dequeue, model, NullLogger.Instance);

        await watcher.LookAsync(_ct);
        Assert.Equal("A form", watcher.Current);
        await watcher.LookAsync(_ct);
        Assert.Null(watcher.Current);   // what was described is no longer on the screen
        await watcher.LookAsync(_ct);
        Assert.Equal("A form", watcher.Current);
        Assert.Equal(3, model.Calls);
    }
}
