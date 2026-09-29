using BanteraApi.Database.Entities;

namespace BanteraApi.Videos;

/// <summary>Optional browsing groups; exact locale filtering remains available to released clients.</summary>
public static class AudioLanguageGroupFilter
{
    public static IQueryable<UserVideo> Apply(IQueryable<UserVideo> query, string group)
    {
        var key = group.Trim().Replace('_', '-').ToLowerInvariant();
        return key switch
        {
            "yue" => query.Where(v => v.TranscriptLanguageCode.ToLower() == "zh-hk"
                || v.TranscriptLanguageCode.ToLower() == "zh-hant-hk"
                || v.TranscriptLanguageCode.ToLower() == "yue"
                || v.TranscriptLanguageCode.ToLower().StartsWith("yue-")),
            "zh-cn" => query.Where(v => v.TranscriptLanguageCode.ToLower() == "zh"
                || v.TranscriptLanguageCode.ToLower() == "zh-cn"
                || v.TranscriptLanguageCode.ToLower() == "zh-hans"
                || v.TranscriptLanguageCode.ToLower().StartsWith("zh-hans-")),
            "zh-tw" => query.Where(v => v.TranscriptLanguageCode.ToLower() == "zh-tw"
                || v.TranscriptLanguageCode.ToLower() == "zh-hant-tw"),
            _ => query.Where(v => v.TranscriptLanguageCode.ToLower() == key
                || v.TranscriptLanguageCode.ToLower().StartsWith(key + "-")),
        };
    }
}
