using System.Collections.Frozen;

namespace BanteraApi.Gemini;

/// <summary>Provider hints only. Never rewrite the lesson locale or TTS accent.</summary>
public static class GeminiTranscriptionLanguages
{
    // Gemini 3.5 Transcribe's published language table, checked 2026-09-29:
    // https://ai.google.dev/gemini-api/docs/transcribe?hl=en#supported-languages
    private static readonly FrozenDictionary<string, string> Supported = """
        af-ZA am-ET ar-EG hy-AM as-IN az-AZ be-BY bn-BD bn-IN bs-BA bg-BG rup-BG
        my-MM yue-Hant-HK ca-ES cmn-Hans-CN ceb km-KH hr-HR cs-CZ da-DK nl-NL
        en-GB en-IN en-US et-EE fa-IR fil-PH fi-FI fr-FR gl-ES ka-GE de-DE el-GR
        gu-IN ha-NG he-IL hi-IN hu-HU is-IS id-ID it-IT ja-JP jv-ID kea-CV kn-IN
        kk-KZ ko-KR ky-KG lv-LV ln-CD lt-LT mk-MK ms-MY ml-IN mt-MT mr-IN mn-MN
        ne-NP nb-NO or-IN pl-PL pt-BR pt-PT pa-IN pa-Guru-IN ro-RO ru-RU sr-RS
        sd-Arab-IN sk-SK sl-SI es-419 es-US sw-KE sv-SE tg-TJ te-IN
        th-TH tr-TR uk-UA uz-UZ vi-VN
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .ToFrozenDictionary(code => code, code => code, StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string? lessonLocale)
    {
        if (string.IsNullOrWhiteSpace(lessonLocale)) return null;
        var locale = lessonLocale.Trim();
        var mapped = locale.ToLowerInvariant() switch
        {
            "zh-hk" or "yue-cn" => "yue-Hant-HK",
            "zh-cn" => "cmn-Hans-CN",
            _ => locale,
        };
        // An unlisted regional dialect/script must not be mislabeled as another one.
        // Null becomes language_codes: [], Google's documented automatic detection mode.
        return Supported.GetValueOrDefault(mapped);
    }
}
