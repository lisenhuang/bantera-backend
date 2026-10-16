namespace BanteraApi.Chat;

/// Online discovery needs a language to practise, not merely a shared native language.
public static class ChatDiscoveryPolicy
{
    public static bool Matches(string? learning, string? native, string? otherLearning, string? otherNative)
    {
        static bool Same(string? a, string? b) =>
            ChatLanguageResolver.Resolve(a) is { } left &&
            ChatLanguageResolver.Resolve(b) is { } right &&
            string.Equals(left.MatchKey, right.MatchKey, StringComparison.OrdinalIgnoreCase);
        return Same(learning, otherLearning) || Same(learning, otherNative) || Same(native, otherLearning);
    }
}
