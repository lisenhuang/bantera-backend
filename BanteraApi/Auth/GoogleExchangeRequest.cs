using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Auth;

public record GoogleExchangeRequest(
    [property: Required]
    [property: SwaggerSchema("One-time code delivered to the app via the callback deep link.")]
    string Code
);
