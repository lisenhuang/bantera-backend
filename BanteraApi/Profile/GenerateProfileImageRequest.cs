using System.ComponentModel.DataAnnotations;
using Swashbuckle.AspNetCore.Annotations;

namespace BanteraApi.Profile;

public sealed class GenerateProfileImageRequest
{
    [Required]
    [SwaggerSchema("One-time avatar generation hint. Allowed values: male, female.")]
    public string? AvatarGender { get; set; }
}
