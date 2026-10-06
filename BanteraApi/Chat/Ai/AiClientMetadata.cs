using System.Text.Json;

namespace BanteraApi.Chat.Ai;

public sealed record AiClock(string TimeZone, int UtcOffsetMinutes);
public sealed record AiClientMetadata(AiClock Clock, string? PushToken)
{
    public static AiClientMetadata Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(new("UTC", 0), null);
        if (json.Length > 2000) throw new InvalidDataException();
        using var doc = JsonDocument.Parse(json);
        var clock = doc.RootElement.TryGetProperty("clock", out var c) ? ReadClock(c) : new AiClock("UTC", 0);
        var token = doc.RootElement.TryGetProperty("pushToken", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (token?.Length > 512) throw new InvalidDataException();
        return new(clock, token);
    }
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
