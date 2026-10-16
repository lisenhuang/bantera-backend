using System.Text.Json;
using BanteraApi.Chat.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BanteraApi.Tests;
public class AiVoiceImagesTests
{
    private static string Png() {
        using var image = new Image<Rgb24>(4, 4);
        using var stream = new MemoryStream(); image.SaveAsPng(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
    private static JsonDocument Frame(int count, string mime, string data) => JsonDocument.Parse(JsonSerializer.Serialize(new {
        images = Enumerable.Range(0, count).Select(_ => new { mimeType = mime, data }).ToArray()
    }));
    [Fact]
    public void LegacyVoiceAndFourValidImagesAreAccepted() {
        using var legacy = JsonDocument.Parse("{}");
        Assert.Empty(AiVoiceImages.Read(legacy.RootElement));
        using var valid = Frame(4, "image/png", Png());
        Assert.Equal(4, AiVoiceImages.Read(valid.RootElement).Count);
    }
    [Fact]
    public void ExcessCountSpoofedMimeOversizeAndCorruptDataAreRejected() {
        foreach (var frame in new[] { Frame(5, "image/png", Png()), Frame(1, "image/jpeg", Png()), Frame(1, "image/png", "not base64"), Frame(1, "image/png", Convert.ToBase64String(new byte[AiVoiceImages.MaxBytes + 1])) })
            using (frame) Assert.Throws<InvalidDataException>(() => AiVoiceImages.Read(frame.RootElement));
    }
}
