using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;
public class PracticeAudioPreparationTests
{
    [Fact]
    public async Task ProviderFailureRetainsCompletedClipAndRetryOnlyGeneratesMissingAudio()
    {
        var row = new PracticeDialogue();
        var calls = new List<int>();
        var saves = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PracticeAudioPreparation.CompleteAsync(row, part =>
        {
            calls.Add(part);
            return part == 1 ? Task.FromException<string>(new InvalidOperationException()) : Task.FromResult("cue.mp3");
        }, () => { saves++; return Task.CompletedTask; }));
        Assert.False(row.Ready);
        Assert.Equal("cue.mp3", row.CueAudio);
        Assert.Equal(1, saves);
        await PracticeAudioPreparation.CompleteAsync(row, part =>
        {
            calls.Add(part); return Task.FromResult($"{part}.mp3");
        }, () => { saves++; return Task.CompletedTask; });
        Assert.Equal(new[] { 0, 1, 1, 2 }, calls);
        Assert.True(row.Ready);
        Assert.Equal(4, saves);
    }

    [Fact]
    public async Task CancellationDoesNotPublishPartialAudio()
    {
        var row = new PracticeDialogue { CueAudio = "saved.mp3" };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PracticeAudioPreparation.CompleteAsync(row,
            _ => Task.FromException<string>(new OperationCanceledException()), () => Task.CompletedTask));
        Assert.Equal("saved.mp3", row.CueAudio);
        Assert.False(row.Ready);
    }
}
