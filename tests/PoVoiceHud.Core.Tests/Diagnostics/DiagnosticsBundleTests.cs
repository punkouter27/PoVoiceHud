using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PoVoiceHud.Core.Diagnostics;


namespace PoVoiceHud.Core.Tests.Diagnostics;

public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PoVoiceHud.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_file_log_feeds_a_diagnostics_zip_that_never_holds_transcripts()
    {
        var old = Path.Combine(_dir, "logs", "2020-01-01.log");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "ancient");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-FileLoggerProvider.KeepDays - 1));

        using (var provider = new FileLoggerProvider(Path.Combine(_dir, "logs")))
        {
            var log = provider.CreateLogger("PoVoiceHud.Core.HudPipeline");
            log.LogDebug("too chatty to keep");
            log.LogWarning(new IOException("disk full"), "Utterance at {At} failed", "00:42");
        }
        Assert.False(File.Exists(old));   // logs older than two weeks are tidied away
        var written = File.ReadAllText(Directory.GetFiles(Path.Combine(_dir, "logs")).Single());
        Assert.Contains("[WRN] HudPipeline: Utterance at 00:42 failed", written);
        Assert.Contains("System.IO.IOException: disk full", written);
        Assert.DoesNotContain("too chatty", written);

        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dir, "sessions"));
        File.WriteAllText(Path.Combine(_dir, "sessions", "2026-10-19.jsonl"), "{\"heard\":\"something private\"}");

        var zip = DiagnosticsBundle.Create(_dir, Path.Combine(_dir, "bundle.zip"), "PoVoiceHud 1.1.0");

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["environment.txt", $"logs/{DateTime.Now:yyyy-MM-dd}.log", "settings.json"], archive.Entries.Select(e => e.FullName).Order());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
