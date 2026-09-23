namespace BanteraApi.Database.Entities;

/// <summary>A runtime-editable setting (e.g. the AI models), changed by admins without a deploy.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
