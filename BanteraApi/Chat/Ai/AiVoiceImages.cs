using System.Text.Json;
using SixLabors.ImageSharp;

namespace BanteraApi.Chat.Ai;

// Validated transient image frames. Never stored in the database, logs or R2.
public sealed record AiVoiceImage(string MimeType, string Data);
public static class AiVoiceImages
{
    public const int MaxCount = 4;
    public const int MaxBytes = 1024 * 1024;
    public static IReadOnlyList<AiVoiceImage> Read(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images)) return [];
        if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() > MaxCount)
            throw new InvalidDataException("Invalid image attachments.");
        var result = new List<AiVoiceImage>();
        foreach (var image in images.EnumerateArray()) {
            if (image.ValueKind != JsonValueKind.Object ||
                !image.TryGetProperty("mimeType", out var mimeValue) || mimeValue.ValueKind != JsonValueKind.String ||
                !image.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid image attachment.");
            var mime = mimeValue.GetString()!;
            var data = dataValue.GetString()!;
            if (mime is not ("image/jpeg" or "image/png") || data.Length > ((MaxBytes + 2) / 3) * 4)
                throw new InvalidDataException("Invalid image attachment.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { throw new InvalidDataException("Invalid image attachment."); }
            if (bytes.Length is 0 or > MaxBytes) throw new InvalidDataException("Invalid image attachment.");
            try {
                var info = Image.Identify(bytes);
                if (info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > 40000000 ||
                    info.Metadata.DecodedImageFormat?.DefaultMimeType != mime)
                    throw new InvalidDataException("Invalid image attachment.");
            } catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException) {
                throw new InvalidDataException("Invalid image attachment.");
            }
            result.Add(new(mime, data));
        }
        return result;
    }
}
