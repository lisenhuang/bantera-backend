using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

public record UpdateProfileRequest(
    [property: MinLength(1), MaxLength(80)]
    [property: SwaggerSchema("Display name shown in the app.")]
    string? Name,
    [property: MaxLength(35)]
    [property: SwaggerSchema("Preferred translation target locale stored as a BCP-47 identifier such as en, en-NZ, or zh-Hans.")]
    string? TranslationLanguage,
    [property: MaxLength(35)]
    [property: SwaggerSchema("User's first/native language as a BCP-47 identifier such as en-US or zh-Hans.")]
    string? NativeLanguage,
    [property: MaxLength(35)]
    [property: SwaggerSchema("Language the user is learning as a BCP-47 identifier such as ja-JP or fr-FR.")]
    string? LearningLanguage
);
