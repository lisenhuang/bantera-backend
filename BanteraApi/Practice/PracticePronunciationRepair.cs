namespace BanteraApi.Practice;

public static class PracticePronunciationRepair
{
    // Called under the pronunciation worker's advisory transaction lock. Only
    // pronunciation fields change; the original practice and progress are untouched.
    public static bool Inspect(PracticeDialogue row, DateTime now)
    {
        if (row.PronunciationAlignmentVersion >= PracticePronunciation.AlignmentVersion) return false;
        row.PronunciationAlignmentVersion = PracticePronunciation.AlignmentVersion;
        if (row.PronunciationJson == null || PracticePronunciation.Read(row) != null) return false;
        row.PronunciationJson = null;
        row.PronunciationAttempts = 0;
        row.PronunciationRetryAt = null;
        row.PronunciationRequestedAt ??= now;
        return true;
    }
}
