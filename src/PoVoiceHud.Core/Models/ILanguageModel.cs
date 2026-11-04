
namespace PoVoiceHud.Core.Models;

public interface ILanguageModel
{
    /// <summary>One chat turn: system + user prompt → assistant text. Throws HttpRequestException on transport/HTTP failure, TimeoutException when no reply arrives in time.</summary>
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

/// <summary>A chat model as the app builds them: text, pictures, and a name to show.</summary>
public interface IChatModel : ILanguageModel, IVisionModel
{
    /// <summary>"gpt-4.1-mini", or the local model's name.</summary>
    string Name { get; }
}

/// <summary>A model that can also be shown a picture.</summary>
public interface IVisionModel
{
    /// <summary>One chat turn with a JPEG beside the user prompt. Throws as <see cref="ILanguageModel.CompleteAsync"/> does.</summary>
    Task<string> CompleteAsync(string system, string user, ReadOnlyMemory<byte> jpeg, CancellationToken ct);
}
