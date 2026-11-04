using System.Net;
using Polly;
using Polly.Retry;

namespace PoVoiceHud.Core.Models;

/// <summary>One retry policy for every outbound HTTP call: 3 attempts, exponential from 500 ms, on 429/5xx/transport errors.</summary>
public static class Resilience
{
    public static readonly PredicateBuilder<object> Transient = new PredicateBuilder()
        .Handle<HttpRequestException>(e => e.StatusCode is null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError);

    public static readonly ResiliencePipeline Default = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(500),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = Transient,
        })
        .Build();
}
