namespace BanteraApi.Practice;

public static class PracticeAudioPreparation
{
    public static async Task CompleteAsync(PracticeDialogue row, Func<int, Task<string>> create, Func<Task> save)
    {
        var keys = new[] { row.CueAudio, row.ReplyAudio, row.DistractorAudio };
        for (var part = 0; part < keys.Length; part++)
        {
            if (!string.IsNullOrEmpty(keys[part])) continue;
            keys[part] = await create(part);
            row.CueAudio = keys[0]; row.ReplyAudio = keys[1]; row.DistractorAudio = keys[2];
            await save();
        }
        row.Ready = true;
        await save();
    }
}
