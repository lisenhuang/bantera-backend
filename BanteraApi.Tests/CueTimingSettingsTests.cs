using BanteraApi.Videos;
using Xunit;

namespace BanteraApi.Tests;

public class CueTimingSettingsTests
{
    [Fact]
    public void StartAtPreviousCueEnd_StartsEachCueWherePreviousEnds()
    {
        VideoTranscriptCue[] cues =
        [
            new(0, 120, 3150, "Hey! Did you manage to get tickets for Saturday's concert?"),
            new(1, 3600, 7050, "I did, two seats in row 12."),
            new(2, 7400, 9000, "That's not bad at all."),
        ];

        var adjusted = CueTimingSettingsService.StartAtPreviousCueEnd(cues);

        Assert.Equal([(0, 3150), (3150, 7050), (7050, 9000)], adjusted.Select(c => (c.StartMs, c.EndMs)));
        Assert.Equal(cues.Select(c => (c.Index, c.Text)), adjusted.Select(c => (c.Index, c.Text)));
    }

    [Fact]
    public void StartAtPreviousCueEnd_NeverMovesAStartLaterOrBeforeThePreviousStart()
    {
        VideoTranscriptCue[] cues =
        [
            new(0, 0, 2000, "a"),
            new(1, 1800, 3000, "b"),  // overlaps the previous cue: keep its start
            new(2, 900, 4000, "c"),   // out of order: previous end would move it later
        ];

        var adjusted = CueTimingSettingsService.StartAtPreviousCueEnd(cues);

        Assert.Equal([0, 1800, 900], adjusted.Select(c => c.StartMs));
    }
}
