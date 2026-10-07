using System.Text.Json;

namespace BanteraApi.Chat.Ai;

public sealed record AiClock(string TimeZone, int UtcOffsetMinutes);
public sealed record AiClientMetadata(AiClock Clock, string? PushToken, bool? HasMetBanteraAi = null, string? AlertPushToken = null, string? LearningLevel = null)
{
    public static AiClientMetadata Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(new("UTC", 0), null);
        if (json.Length > 2000) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(json);
        var clock = doc.RootElement.TryGetProperty("clock", out var c) ? ReadClock(c) : new AiClock("UTC", 0);
        var token = doc.RootElement.TryGetProperty("pushToken", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (token?.Length > 512) throw new InvalidDataException();
        bool? hasMet = null;
        if (doc.RootElement.TryGetProperty("hasMetBanteraAi", out var met)) {
            if (met.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new InvalidDataException();
            if (met.ValueKind != JsonValueKind.Null) hasMet = met.GetBoolean();
        }
        var alert = doc.RootElement.TryGetProperty("alertPushToken", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (alert?.Length > 512) throw new InvalidDataException();
        var level = doc.RootElement.TryGetProperty("learningLevel", out var l) && l.ValueKind == JsonValueKind.String
            ? NormalizeLevel(l.GetString()) : null;
        return new(clock, token, hasMet, alert, level);
    }
    private static string? NormalizeLevel(string? level) => level?.Trim().ToLowerInvariant() switch {
        "beginner" => "beginner", "intermediate" => "intermediate", "advanced" => "advanced", _ => null
    };
    public string LevelPrompt => NormalizeLevel(LearningLevel) switch {
        "beginner" => " Current Discover practice level: beginner. Use common everyday words, short simple sentences, a gentle clear pace and one concrete question at a time. Model short phrases they can repeat; explain unfamiliar words simply. ",
        "intermediate" => " Current Discover practice level: intermediate. Use everyday vocabulary with some useful new expressions, moderately complex sentences and a natural but clear pace. Encourage fuller answers and short explanations of opinions. ",
        "advanced" => " Current Discover practice level: advanced. Use natural conversational pace, nuanced vocabulary, idiomatic expressions and more complex discussion. Offer subtle corrections and invite detailed opinions without long monologues. ",
        _ => " No specific Discover practice level is selected (All levels or an older app). Do not assume a proficiency level; adapt gradually to the learner's demonstrated ability. "
    };
    public static AiClock ReadClock(JsonElement c)
    {
        var zone = c.GetProperty("timeZone").GetString() ?? "UTC";
        var offset = c.GetProperty("utcOffsetMinutes").GetInt32();
        if (zone.Length > 100 || offset is < -840 or > 840) throw new InvalidDataException();
        return new(zone, offset);
    }
    public static object CurrentTime(AiClock clock)
    {
        var utc = DateTimeOffset.UtcNow;
        DateTimeOffset local;
        try { local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(clock.TimeZone)); }
        catch (TimeZoneNotFoundException) { local = utc.ToOffset(TimeSpan.FromMinutes(clock.UtcOffsetMinutes)); }
        return new { utc = utc.ToString("O"), local = local.ToString("O"), timeZone = clock.TimeZone, utcOffsetMinutes = local.Offset.TotalMinutes };
    }
    public string TimePrompt => " Current trusted server time and learner device timezone (data): " + JsonSerializer.Serialize(CurrentTime(Clock));
}
