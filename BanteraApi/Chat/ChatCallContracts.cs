namespace BanteraApi.Chat;

public static class ChatCallMediaKinds
{
    public const string Audio = "audio";
    public const string Video = "video";

    public static bool IsSupported(string? value)
    {
        return string.Equals(value, Audio, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Video, StringComparison.OrdinalIgnoreCase);
    }
}

public static class ChatCallStateKinds
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Ended = "ended";
}

public record ChatIceServerEntryResponse(
    IReadOnlyList<string> Urls,
    string? Username,
    string? Credential
);

public record ChatIceServersResponse(
    IReadOnlyList<ChatIceServerEntryResponse> IceServers
);
