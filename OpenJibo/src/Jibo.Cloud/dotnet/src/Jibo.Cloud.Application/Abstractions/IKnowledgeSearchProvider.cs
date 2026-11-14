namespace Jibo.Cloud.Application.Abstractions;

public enum KnowledgeSearchOutcome
{
    Found,
    NotFound,
    Unavailable
}

public interface IKnowledgeSearchProvider
{
    SearchBackendKind Kind { get; }

    Task<KnowledgeSearchResult?> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <param name="Mood">Optional speaking mood chosen by the AI: "happy", "neutral" or "sad".</param>
/// <param name="CommandPhrase">
/// When the AI decided the user wants one of Jibo's built-in skills, a plain-English command
/// ("set a timer for 5 minutes") to run through the skill router instead of speaking an answer.
/// </param>
/// <param name="Animation">Optional animation name from <see cref="JiboAnimationCatalog"/> to show while speaking.</param>
public sealed record KnowledgeSearchResult(
    string AnswerText,
    SearchBackendKind BackendKind,
    KnowledgeSearchOutcome Outcome = KnowledgeSearchOutcome.Found,
    string? Mood = null,
    string? CommandPhrase = null,
    string? Animation = null)
{
    public static KnowledgeSearchResult NotFound(SearchBackendKind backendKind) =>
        new(string.Empty, backendKind, KnowledgeSearchOutcome.NotFound);

    public static KnowledgeSearchResult Unavailable(SearchBackendKind backendKind) =>
        new(string.Empty, backendKind, KnowledgeSearchOutcome.Unavailable);
}
