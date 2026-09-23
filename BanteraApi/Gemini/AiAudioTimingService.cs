using System.Diagnostics;
using BanteraApi.Diagnostics;
using BanteraApi.Videos;

namespace BanteraApi.Gemini;

public sealed record AiAudioTimingResult(
    IReadOnlyList<WordTimingRecord> WordTiming,
    IReadOnlyList<VideoTranscriptCueRecord> Cues,
    IReadOnlyList<VideoTranscriptCueRecord>? ShortCues,
    string Mode,
    WordAlignment Alignment);

/// <summary>
/// Word timing for AI-generated dialogue audio:
/// Gemini Transcribe (word timestamps) → exact character matches → AI fixes only the
/// words heard differently → timings for every word of the ORIGINAL script.
/// A transcript that misses part of the dialogue is retried once. Returns null when
/// anything fails, so callers fall back to Rev.ai or estimated timing.
/// </summary>
public sealed class AiAudioTimingService(GeminiService gemini, AiPipelineEventRecorder events, ILogger<AiAudioTimingService> logger)
{
    /// <summary>More estimated words than this means the transcript did not fit the script.</summary>
    private const double MaxEstimatedRatio = 0.35;
    /// <summary>Retry the transcription when more than this share of words was not heard.</summary>
    private const double RetryEstimatedRatio = 0.10;
    /// <summary>Speech missing although the audio ends this soon after the last heard word: TTS likely never spoke it.</summary>
    private const int TtsDropMaxTailMs = 1200;

    private sealed record Attempt(IReadOnlyList<TranscribedWord> Words, WordAlignment Alignment, string Mode, long TranscribeMs, long AlignMs);

    private sealed record CoverageIssue(int[] MissingLines, int TrailingEstimated, double EstimatedRatio, int AudioAfterLastWordMs);

    public async Task<AiAudioTimingResult?> TryBuildAsync(
        DialogueLine[] lines,
        GeneratedAudio audio,
        string languageCode,
        bool buildShortCues,
        CancellationToken cancellationToken)
    {
        if (lines.Length == 0) return null;
        var lineTexts = lines.Select(l => l.Text).ToArray();
        var tokens = WordTimingAligner.Tokenize(lineTexts);
        if (tokens.Count == 0) return null;

        try
        {
            var best = await RunAttemptAsync(lines, tokens, audio, languageCode, cancellationToken);
            var retried = false;

            if (FindCoverageIssue(lines, best, audio.DurationMs) is { } issue)
            {
                await events.RecordAsync(
                    AiPipelineSeverity.Warning, "transcription", "transcription_incomplete",
                    $"{best.Alignment.Estimated} of {best.Alignment.Tokens.Count} words not heard; retrying the transcription once.",
                    Describe(issue, best, audio.DurationMs));

                retried = true;
                try
                {
                    var second = await RunAttemptAsync(lines, tokens, audio, languageCode, cancellationToken);
                    var improved = second.Alignment.Estimated < best.Alignment.Estimated;
                    await events.RecordAsync(
                        AiPipelineSeverity.Info, "transcription", improved ? "transcription_retry_improved" : "transcription_retry_not_improved",
                        $"Retry: {second.Alignment.Estimated} words not heard (first attempt: {best.Alignment.Estimated}).",
                        new { firstEstimated = best.Alignment.Estimated, retryEstimated = second.Alignment.Estimated, total = tokens.Count });
                    if (improved) best = second;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await events.RecordAsync(AiPipelineSeverity.Warning, "transcription", "transcription_retry_failed", ex.Message);
                }

                if (FindCoverageIssue(lines, best, audio.DurationMs) is { MissingLines.Length: > 0 } remaining)
                {
                    // Tell apart "the TTS never spoke it" from "the transcriber missed it".
                    var ttsLikely = remaining.TrailingEstimated > 0 && remaining.AudioAfterLastWordMs < TtsDropMaxTailMs;
                    await events.RecordAsync(
                        AiPipelineSeverity.Warning, ttsLikely ? "tts" : "transcription",
                        ttsLikely ? "tts_speech_possibly_missing" : "lines_missing_after_retry",
                        ttsLikely
                            ? "The audio ends right after the last heard word: the TTS may have dropped the final line(s)."
                            : "Whole lines are still missing from the transcript after one retry.",
                        Describe(remaining, best, audio.DurationMs));
                }
            }

            var alignment = best.Alignment;
            if (alignment.EstimatedRatio > MaxEstimatedRatio)
            {
                logger.LogWarning(
                    "Word timing for {LanguageCode} rejected: {Estimated} of {Total} words could not be located in the transcript.",
                    languageCode, alignment.Estimated, alignment.Tokens.Count);
                await events.RecordAsync(
                    AiPipelineSeverity.Warning, "timing", "timing_rejected",
                    $"{alignment.Estimated} of {alignment.Tokens.Count} words could not be located; falling back.",
                    Summary(best, retried));
                return null;
            }

            var cues = WordTimingAligner.BuildLineCues(lineTexts, alignment, audio.DurationMs);
            if (cues is null) return null;
            var shortCues = buildShortCues ? WordTimingAligner.BuildShortCues(lines, alignment, audio.DurationMs) : null;
            if (buildShortCues && shortCues is null)
                await events.RecordAsync(AiPipelineSeverity.Warning, "timing", "short_cues_unmatched", "Short cue texts did not line up with the line's words.");

            logger.LogInformation(
                "Word timing for {LanguageCode} via {Mode}: {Exact} exact, {Corrected} corrected, {Estimated} estimated of {Total} words; retried={Retried}.",
                languageCode, best.Mode, alignment.Exact, alignment.Corrected, alignment.Estimated, alignment.Tokens.Count, retried);
            await events.RecordAsync(
                AiPipelineSeverity.Info, "timing", "timing_completed",
                $"{alignment.Exact} exact, {alignment.Corrected} corrected, {alignment.Estimated} estimated of {alignment.Tokens.Count} words.",
                Summary(best, retried),
                durationMs: (int)(best.TranscribeMs + best.AlignMs));

            return new AiAudioTimingResult(WordTimingAligner.ToWordTiming(alignment), cues, shortCues, best.Mode, alignment);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Gemini word timing failed for {LanguageCode}; falling back.", languageCode);
            await events.RecordAsync(AiPipelineSeverity.Warning, "timing", "timing_failed", ex.Message, new { exception = ex.GetType().Name });
            return null;
        }
    }

    /// <summary>One transcription plus alignment. The AI runs only when some words were heard differently.</summary>
    private async Task<Attempt> RunAttemptAsync(
        DialogueLine[] lines,
        List<ScriptToken> tokens,
        GeneratedAudio audio,
        string languageCode,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var words = await gemini.TranscribeWordsAsync(audio.Bytes, audio.ContentType, languageCode, cancellationToken);
        var transcribeMs = clock.ElapsedMilliseconds;

        clock.Restart();
        var matches = WordTimingAligner.AlignByCharacters(tokens, words);
        var mode = "geminiTranscribe";
        if (tokens.Where((t, i) => !WordTimingAligner.IsExact(t, matches[i], words)).Any())
        {
            try
            {
                var ai = await gemini.AlignTokensWithAiAsync(tokens, lines, words, cancellationToken);
                matches = WordTimingAligner.Merge(tokens, words, ai, matches);
                mode = "geminiTranscribe+ai";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The character alignment alone is still usable; unmatched words get estimated.
                logger.LogWarning(ex, "AI word alignment failed for {LanguageCode}; using character alignment only.", languageCode);
                await events.RecordAsync(AiPipelineSeverity.Warning, "alignment", "ai_alignment_failed", ex.Message);
            }
        }

        var alignment = WordTimingAligner.Resolve(tokens, words, matches, audio.DurationMs);
        return new Attempt(words, alignment, mode, transcribeMs, clock.ElapsedMilliseconds);
    }

    private static CoverageIssue? FindCoverageIssue(DialogueLine[] lines, Attempt attempt, int durationMs)
    {
        var tokens = attempt.Alignment.Tokens;
        var missingLines = Enumerable.Range(0, lines.Length)
            .Where(line =>
            {
                var lineTokens = tokens.Where(t => t.Line == line).ToList();
                return lineTokens.Count > 0 && lineTokens.All(t => t.Status == TokenTimingStatus.Estimated);
            })
            .ToArray();
        var trailing = 0;
        for (var i = tokens.Count - 1; i >= 0 && tokens[i].Status == TokenTimingStatus.Estimated; i--) trailing++;

        var ratio = attempt.Alignment.EstimatedRatio;
        if (missingLines.Length == 0 && trailing == 0 && ratio <= RetryEstimatedRatio)
            return null;

        var lastWordEnd = attempt.Words.Count > 0 ? attempt.Words[^1].EndMs : 0;
        return new CoverageIssue(missingLines, trailing, ratio, Math.Max(0, durationMs - lastWordEnd));
    }

    private static object Describe(CoverageIssue issue, Attempt attempt, int durationMs) => new
    {
        missingLines = issue.MissingLines,
        trailingEstimatedWords = issue.TrailingEstimated,
        estimatedRatio = Math.Round(issue.EstimatedRatio, 3),
        audioAfterLastWordMs = issue.AudioAfterLastWordMs,
        audioDurationMs = durationMs,
        wordsHeard = attempt.Words.Count,
        scriptWords = attempt.Alignment.Tokens.Count,
        lastWordsHeard = attempt.Words.TakeLast(5).Select(w => w.Text),
    };

    private static object Summary(Attempt attempt, bool retried) => new
    {
        mode = attempt.Mode,
        retried,
        exact = attempt.Alignment.Exact,
        corrected = attempt.Alignment.Corrected,
        estimated = attempt.Alignment.Estimated,
        total = attempt.Alignment.Tokens.Count,
        unusedTranscriptWords = attempt.Alignment.UnusedTranscriptWords,
        transcribeMs = attempt.TranscribeMs,
        alignMs = attempt.AlignMs,
    };
}
