using System.ComponentModel.DataAnnotations;

namespace BanteraApi.Auth;

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password
);
