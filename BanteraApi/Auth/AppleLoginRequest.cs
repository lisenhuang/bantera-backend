using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

public record AppleLoginRequest(
    [property: Required]
    [property: SwaggerSchema("Apple identity token returned by the native Apple login flow.")]
    string IdentityToken,
    [property: SwaggerSchema("Apple user identifier returned by the native Apple login flow.")]
    string? UserIdentifier,
    [property: EmailAddress]
    [property: SwaggerSchema("Apple email address if the native SDK returned it. May be null after the first sign-in.", Format = "email")]
    string? Email,
    [property: SwaggerSchema("Apple given name if the native SDK returned it on first sign-in.")]
    string? GivenName,
    [property: SwaggerSchema("Apple family name if the native SDK returned it on first sign-in.")]
    string? FamilyName
);
