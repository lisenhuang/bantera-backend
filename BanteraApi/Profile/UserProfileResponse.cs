using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

public record UserProfileResponse(
    [property: SwaggerSchema("Bantera user ID.")]
    Guid Id,
    [property: SwaggerSchema("Display name shown in the app.")]
    string Name,
    [property: SwaggerSchema("Absolute URL for the user's profile image. Null when no custom image is set.")]
    string? AvatarUrl
);
