using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

public record LoginRequest(
    [property: Required, EmailAddress]
    [property: SwaggerSchema("User's email address.", Format = "email")]
    string Email,
    [property: Required, MinLength(6)]
    [property: SwaggerSchema("User's password. Min 6 characters.")]
    string Password
);
