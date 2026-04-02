using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

public record UpdateProfileRequest(
    [property: Required, MinLength(1), MaxLength(80)]
    [property: SwaggerSchema("Display name shown in the app. Does not need to be a real name.")]
    string Name
);
