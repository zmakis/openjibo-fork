using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Claude-first brain: when the AI decides the user asked for one of Jibo's built-in skills
/// (in any wording or language), it returns a plain-English command that is run once more
/// through the existing skill router, which acts as Jibo's skill library.
/// </summary>
public sealed partial class JiboInteractionService
{
    internal const string AiCommandAttributeKey = "aiCommandRedirect";

    private static bool IsAiCommandTurn(TurnContext turn) =>
        turn.Attributes.TryGetValue(AiCommandAttributeKey, out var value) && value is true;

    private static TurnContext CreateAiCommandTurn(TurnContext turn, string commandPhrase)
    {
        // Same copy as "repeat last command", but with the AI's English phrase and none of the
        // original turn's robot-side NLU (it described the untranslated sentence).
        var redirected = CreateReplayTurn(
            turn,
            new RepeatLastCommandStore.LastCommand(
                commandPhrase,
                commandPhrase,
                new Dictionary<string, object?>()));
        redirected.Attributes.Remove(RepeatReplayAttributeKey);
        redirected.Attributes[AiCommandAttributeKey] = true;
        return redirected;
    }

    private async Task<JiboInteractionDecision> BuildChatFallbackWithAiCommandsAsync(
        TurnContext turn,
        JiboExperienceCatalog catalog,
        string transcript,
        string lowered,
        string? chitchatEmotion,
        string? preferredName,
        CancellationToken cancellationToken)
    {
        // A command the AI rewrote that the skill router still does not recognise:
        // say so instead of asking the AI again (no loops, no second charge).
        if (IsAiCommandTurn(turn))
            return BuildAiCommandUnsupportedDecision();

        var decision = await BuildChatFallbackDecisionAsync(
            catalog,
            transcript,
            lowered,
            chitchatEmotion,
            preferredName,
            cancellationToken);

        if (string.Equals(decision.IntentName, AiCommandIntent, StringComparison.OrdinalIgnoreCase) &&
            decision.SkillPayload?.TryGetValue(AiCommandPayloadKey, out var rawCommand) == true &&
            rawCommand is string commandPhrase &&
            !string.IsNullOrWhiteSpace(commandPhrase))
            return await BuildDecisionCoreAsync(CreateAiCommandTurn(turn, commandPhrase), cancellationToken);

        return decision;
    }

    private static JiboInteractionDecision BuildAiCommandUnsupportedDecision() =>
        new(
            "ai_command_unsupported",
            "I can't do that yet, but I bet I'll be able to do that sometime soon.");
}
