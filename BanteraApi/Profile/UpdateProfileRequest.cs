using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

public record UpdateProfileRequest(
    [property: MinLength(1), MaxLength(80)]
    [property: SwaggerSchema("Display name shown in the app.")]
    string? Name,
    [property: MaxLength(35)]
    [property: SwaggerSchema("Preferred translation target locale stored as a BCP-47 identifier such as en, en-NZ, or zh-Hans.")]
    string? TranslationLanguage
);
