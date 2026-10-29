using System.Globalization;

namespace PoVoiceHud.Core.Providers;

/// <summary>
/// How the model has done this session: calls, how long they took, the tokens they used, and the retries and failures on
/// the way. One instance for the whole app, shared by every model client and kept across settings changes; the header's
/// tooltip shows <see cref="Summary"/>.
/// </summary>
public sealed class ModelStats
{
    private readonly Lock _gate = new();
    private int _calls;
    private int _failures;
    private int _retries;
    private long _tokens;
    private double _totalMs;
    private double _lastMs;

    public int Calls { get { lock (_gate) return _calls; } }

    /// <summary>A call that came back, with its time and tokens (0 when the reply did not say).</summary>
    public void Record(TimeSpan took, int tokens)
    {
        lock (_gate)
        {
            _calls++;
            _tokens += tokens;
            _totalMs += took.TotalMilliseconds;
            _lastMs = took.TotalMilliseconds;
        }
    }

    /// <summary>An attempt failed and was tried again.</summary>
    public void Retried()
    {
        lock (_gate) _retries++;
    }

    /// <summary>A call that failed for good: unreachable, too slow, or refused.</summary>
    public void Failed()
    {
        lock (_gate) _failures++;
    }

    /// <summary>"12 model calls · 1.3 s average (last 0.9 s) · 4,210 tokens · 1 retry", or that there were none yet.</summary>
    public string Summary
    {
        get
        {
            lock (_gate)
            {
                if (_calls == 0 && _failures == 0) return "No model calls yet";
                var culture = CultureInfo.CurrentCulture;
                var parts = new List<string>
                {
                    $"{_calls} model {(_calls == 1 ? "call" : "calls")}",
                    string.Format(culture, "{0:0.0} s average (last {1:0.0} s)", _calls == 0 ? 0 : _totalMs / _calls / 1000, _lastMs / 1000),
                    string.Format(culture, "{0:N0} tokens", _tokens),
                };
                if (_retries > 0) parts.Add($"{_retries} {(_retries == 1 ? "retry" : "retries")}");
                if (_failures > 0) parts.Add($"{_failures} failed");
                return string.Join(" · ", parts);
            }
        }
    }
}
