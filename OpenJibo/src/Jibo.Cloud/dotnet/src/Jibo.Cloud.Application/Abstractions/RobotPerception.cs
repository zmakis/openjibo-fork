namespace Jibo.Cloud.Application.Abstractions;

/// <summary>
/// Who Jibo's own on-robot face/voice ID says is speaking and present this turn
/// (from the turn's context.runtime.perception, mapped to loop first names).
/// </summary>
public sealed record RobotPerceptionSnapshot(string? SpeakerName, IReadOnlyList<string> PeoplePresentNames)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SpeakerName) && PeoplePresentNames.Count == 0;
}

/// <summary>
/// Carries the current turn's <see cref="RobotPerceptionSnapshot"/> down to the AI provider
/// without changing the search interfaces. Flows with the async call chain of one turn.
/// </summary>
public static class RobotPerception
{
    private static readonly AsyncLocal<RobotPerceptionSnapshot?> CurrentValue = new();

    public static RobotPerceptionSnapshot? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }
}
