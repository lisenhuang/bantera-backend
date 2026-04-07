using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

public record UserProfileResponse(
    [property: SwaggerSchema("Bantera user ID.")]
    Guid Id,
    [property: SwaggerSchema("Display name shown in the app.")]
    string Name,
    [property: SwaggerSchema("Absolute URL for the user's profile image. Null when no custom image is set.")]
    string? AvatarUrl,
    [property: SwaggerSchema("Preferred translation target locale stored as a BCP-47 identifier. Null when the user has not chosen one yet.")]
    string? TranslationLanguage,
    [property: SwaggerSchema("User's first/native language as a BCP-47 identifier. Null when not set.")]
    string? NativeLanguage,
    [property: SwaggerSchema("Language the user is learning as a BCP-47 identifier. Null when not set.")]
    string? LearningLanguage
);
