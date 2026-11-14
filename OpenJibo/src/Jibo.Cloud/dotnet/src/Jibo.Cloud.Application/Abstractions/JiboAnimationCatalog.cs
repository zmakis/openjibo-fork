namespace Jibo.Cloud.Application.Abstractions;

/// <summary>
/// Animations the AI may ask Jibo to show while speaking, written as [anim:name].
/// Every name was checked against the robot's own animation index
/// (jibo-anim-db-animations/animdb.json): emoji pictures and animated "Jibojis" are matched by
/// their metaTerm inside the emoji / jiboji category; gestures are emotion categories.
/// </summary>
public static class JiboAnimationCatalog
{
    /// <summary>Animated scenes (category "jiboji"), e.g. a coin flip or a UFO.</summary>
    public static readonly IReadOnlyList<string> Jibojis =
    [
        "coin-flip", "butterfly", "ufo", "telescope", "kite", "trampoline", "hula-hoop", "blowing-bubbles",
        "fireplace", "sunflower", "rose", "bouquet", "ghost-boo", "pumpkin-carving", "snowglobe", "maze",
        "drawing", "fidget-spinner", "winner", "shoot-basketball"
    ];

    /// <summary>Pictures shown on Jibo's face (category "emoji").</summary>
    public static readonly IReadOnlyList<string> Emojis =
    [
        "heart", "love", "star", "sun", "sunny", "rain", "snow", "snowflake", "rainbow", "cloudy",
        "lightning", "moon", "fire", "pizza", "burger", "taco", "sushi", "cake", "icecream", "coffee", "tea",
        "popcorn", "apple", "carrot", "cheese", "chocolate", "candy", "watermelon", "egg", "sandwich",
        "hotdog", "dog", "cat", "puppy", "kitten", "bird", "penguin", "whale", "fish", "turtle", "rabbit",
        "pig", "cow", "sheep", "chicken", "mouse", "bug", "flower", "tulip", "daisy", "tree", "plant",
        "earth", "rocket", "robot", "car", "airplane", "boat", "bike", "truck", "taxi", "soccer",
        "basketball", "football", "baseball", "tennis", "golf", "hockey", "bowling", "boxing", "music",
        "book", "camera", "phone", "computer", "laptop", "tv", "gift", "party", "birthday", "christmas",
        "christmas-tree", "santa", "pumpkin", "halloween", "easter", "valentines", "fireworks", "money",
        "clock", "lightbulb", "idea", "magic", "question-mark", "exclamation", "checkmark", "brain",
        "applause", "fingers-crossed", "lucky", "umbrella", "beach", "summer", "winter", "mountain", "ocean",
        "home", "school", "sleep", "thermometer"
    ];

    /// <summary>Body-language gestures (emotion categories).</summary>
    public static readonly IReadOnlyList<string> Gestures =
    [
        "happy", "laughing", "surprised", "affection", "proud", "sad", "confused", "thinking", "yes", "no",
        "greeting", "goodbye", "worried", "embarrassed", "scared"
    ];

    private static readonly HashSet<string> JibojiSet = new(Jibojis, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> EmojiSet = new(Emojis, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GestureSet = new(Gestures, StringComparer.OrdinalIgnoreCase);

    /// <summary>The menu as text for the AI's instructions.</summary>
    public static string BuildPromptMenu() =>
        "animated scenes: " + string.Join(", ", Jibojis) + ". " +
        "pictures: " + string.Join(", ", Emojis) + ". " +
        "gestures: " + string.Join(", ", Gestures) + ".";

    /// <summary>
    /// Turns a catalog name into a non-blocking ESML anim tag (plays while Jibo speaks).
    /// Unknown names return false so a made-up name can never reach the robot.
    /// </summary>
    public static bool TryBuildAnimTag(string? name, out string esmlTag)
    {
        esmlTag = string.Empty;
        var key = name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return false;

        if (JibojiSet.Contains(key))
            esmlTag = $"<anim cat='jiboji' filter='{key}' nonBlocking='true' />";
        else if (EmojiSet.Contains(key))
            esmlTag = $"<anim cat='emoji' filter='{key}' nonBlocking='true' />";
        else if (GestureSet.Contains(key))
            esmlTag = $"<anim cat='{key}' filter='!audio-only, !ssa-only' nonBlocking='true' />";
        else
            return false;

        return true;
    }
}
