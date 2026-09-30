using BanteraApi.Chat;
using Xunit;
using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BanteraApi.Tests;

public class ChatGroupImageTests
{
    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public async Task AcceptsRealImage(string contentType)
    {
        using var image = new Image<Rgb24>(540, 960);
        using var stream = new MemoryStream();
        if (contentType == "image/jpeg") await image.SaveAsJpegAsync(stream);
        else await image.SaveAsPngAsync(stream);
        Assert.True(await ChatService.IsValidGroupImageAsync(File(stream.ToArray(), contentType)));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("audio/mp4")]
    public async Task RejectsCorruptAndWrongMedia(string contentType)
    {
        Assert.False(await ChatService.IsValidGroupImageAsync(File(new byte[] {255,216,255,0,0,0,0,0}, contentType)));
        using var image = new Image<Rgb24>(10, 10);
        using var stream = new MemoryStream();
        await image.SaveAsJpegAsync(stream);
        Assert.False(await ChatService.IsValidGroupImageAsync(File(stream.ToArray(), "image/png")));
        Assert.False(await ChatService.IsValidGroupImageAsync(File(stream.ToArray(), "audio/mp4")));
    }

    [Fact]
    public async Task RejectsEmptyOversizedAndExcessiveDimensions()
    {
        Assert.False(await ChatService.IsValidGroupImageAsync(null));
        Assert.False(await ChatService.IsValidGroupImageAsync(File([], "image/jpeg")));
        Assert.False(await ChatService.IsValidGroupImageAsync(File(new byte[5 * 1024 * 1024 + 1], "image/jpeg")));
        using var image = new Image<Rgb24>(4097, 1);
        using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        Assert.False(await ChatService.IsValidGroupImageAsync(File(stream.ToArray(), "image/png")));
    }

    private static IFormFile File(byte[] bytes, string contentType) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "achievement.jpg")
        { Headers = new HeaderDictionary(), ContentType = contentType };
}
