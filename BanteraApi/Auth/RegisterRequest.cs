using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

public record RegisterRequest(
    [property: Required, EmailAddress]
    [property: SwaggerSchema("User's email address.", Format = "email")]
    string Email,
    [property: Required, MinLength(8)]
    [property: SwaggerSchema("User's password. Min 8 characters.")]
    string Password
);
