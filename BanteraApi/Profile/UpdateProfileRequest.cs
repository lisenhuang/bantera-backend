using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

/// <summary>
/// PATCH-style body: only properties present in JSON are updated.
/// Use a property bag (class) instead of a positional record so partial JSON
/// ({ "learningLanguage": "en" }) always binds reliably across STJ versions.
/// </summary>
public sealed class UpdateProfileRequest
{
    [MaxLength(80)]
    [SwaggerSchema("Display name shown in the app. Omit this key to leave unchanged.")]
    public string? Name { get; set; }

    [MaxLength(35)]
    [SwaggerSchema(
        "Preferred translation target locale stored as a BCP-47 identifier such as en, en-NZ, or zh-Hans.")]
    public string? TranslationLanguage { get; set; }

    [MaxLength(35)]
    [SwaggerSchema("User's first/native language as a BCP-47 identifier such as en-US or zh-Hans.")]
    public string? NativeLanguage { get; set; }

    [MaxLength(35)]
    [SwaggerSchema(
        "Language the user is learning as a BCP-47 identifier such as ja-JP or fr-FR.")]
    public string? LearningLanguage { get; set; }
}
