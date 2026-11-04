namespace PoVoiceHud.Core.Providers.FactCheck;

/// <summary>
/// Whether calls go to the local model for now because Azure failed: after a failure the app stays with the local model for
/// <see cref="Pause"/> before Azure is tried again, so every claim of an outage does not wait out Azure's retries first.
/// </summary>
public sealed class FallbackCircuit(TimeProvider? clock = null)
{
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _retryAt;   // UTC ticks; 0 while Azure is in use

    /// <summary>True when calls switched to the local model, false when back on Azure; raised once per switch.</summary>
    public event Action<bool>? Switched;

    public bool UsesLocal => Interlocked.Read(ref _retryAt) is var at && at != 0 && _clock.GetUtcNow().UtcTicks < at;

    internal void Failed()
    {
        var first = Interlocked.Exchange(ref _retryAt, (_clock.GetUtcNow() + Pause).UtcTicks) == 0;
        if (first) Switched?.Invoke(true);
    }

    internal void Recovered()
    {
        if (Interlocked.Exchange(ref _retryAt, 0) != 0) Switched?.Invoke(false);
    }
}

/// <summary>
/// Azure first, the local model when Azure cannot be reached, is too slow, answers with an error, or its content filter
/// refuses the sentence (the local model has none). The circuit is shared, so one failure moves every client over.
/// </summary>
public sealed class FallbackModel(IChatModel primary, IChatModel local, FallbackCircuit circuit) : IChatModel
{
    public string Name => circuit.UsesLocal ? local.Name : primary.Name;

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct) =>
        RouteAsync(model => model.CompleteAsync(system, user, ct), ct);

    public Task<string> CompleteAsync(string system, string user, ReadOnlyMemory<byte> jpeg, CancellationToken ct) =>
        RouteAsync(model => model.CompleteAsync(system, user, jpeg, ct), ct);

    private async Task<string> RouteAsync(Func<IChatModel, Task<string>> call, CancellationToken ct)
    {
        if (!circuit.UsesLocal)
        {
            try
            {
                var reply = await call(primary);
                circuit.Recovered();
                return reply;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TimeoutException or ContentFilteredException)
            {
                if (e is not ContentFilteredException) circuit.Failed();   // one refused sentence says nothing about Azure being up
            }
        }
        return await call(local);
    }
}
