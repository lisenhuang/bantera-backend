using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

public record RefreshRequest(
    [property: Required]
    [property: SwaggerSchema("The refresh token received from login or a previous refresh call.")]
    string RefreshToken
);
