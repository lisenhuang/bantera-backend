# Short-Cue Diagnostics Analysis

This document explains how to analyze AI audio items where `transcriptShortCues` was not saved. These cases are recorded in the `ai_audio_short_cue_diagnostics` table.

The table is written only when v2 AI audio generation finishes but `TranscriptShortCues` is `null`. The generated media row still exists in `user_videos`; this diagnostics table stores the reason and enough alignment detail to understand why short cues were dropped.

## Table

`ai_audio_short_cue_diagnostics`

Important columns:

- `VideoId`: generated media id in `user_videos`.
- `UserId`: user who generated the audio.
- `CreatedAt`: when the diagnostic row was written.
- `LanguageCode`: locale used for generation, for example `en-US`.
- `ScenarioId`: scenario id, such as `latest_news`.
- `Reason`: high-level failure reason.
- `LongAlignmentMode`: long-cue alignment path used, such as `revAiStrict`, `revAiTolerant`, `geminiFallback`, or `estimatedFallback`.
- `ShortAlignmentAttempted`: whether short-cue alignment was attempted.
- `LineCount`: number of long dialogue lines.
- `LinesWithSplitCount`: number of dialogue lines where Gemini returned more than one short cue.
- `FlattenedShortCueCount`: total short cue fragments after flattening.
- `LongCueCount`: number of saved long cues.
- `WordTimingCount`: number of word-timing entries available.
- `DetailJson`: JSONB payload with per-line short cues and alignment failure details.

## Common Queries

Reason breakdown:

```sql
SELECT
  "Reason",
  count(*) AS total
FROM ai_audio_short_cue_diagnostics
GROUP BY "Reason"
ORDER BY total DESC;
```

Reason by language:

```sql
SELECT
  "LanguageCode",
  "Reason",
  count(*) AS total
FROM ai_audio_short_cue_diagnostics
GROUP BY "LanguageCode", "Reason"
ORDER BY total DESC, "LanguageCode";
```

Recent failures:

```sql
SELECT
  "CreatedAt",
  "VideoId",
  "LanguageCode",
  "ScenarioId",
  "Reason",
  "LongAlignmentMode",
  "ShortAlignmentAttempted",
  "LineCount",
  "LinesWithSplitCount",
  "FlattenedShortCueCount",
  "LongCueCount",
  "WordTimingCount"
FROM ai_audio_short_cue_diagnostics
ORDER BY "CreatedAt" DESC
LIMIT 50;
```

Find rows where Gemini produced short-cue splits but alignment still failed:

```sql
SELECT
  "CreatedAt",
  "VideoId",
  "LanguageCode",
  "Reason",
  "LinesWithSplitCount",
  "FlattenedShortCueCount",
  "DetailJson"
FROM ai_audio_short_cue_diagnostics
WHERE "LinesWithSplitCount" > 0
ORDER BY "CreatedAt" DESC
LIMIT 50;
```

Find rows where no word timing was available:

```sql
SELECT
  "CreatedAt",
  "VideoId",
  "LanguageCode",
  "Reason",
  "LongAlignmentMode",
  "WordTimingCount"
FROM ai_audio_short_cue_diagnostics
WHERE "WordTimingCount" = 0
ORDER BY "CreatedAt" DESC;
```

## Inspect DetailJson

`DetailJson` contains:

- `lineSummaries`: each dialogue line, its returned `shortCues`, and whether it had a split.
- `shortCueValidationFailures`: Gemini short-cue validation failures before alignment.
- `strictLongAlignmentFailure`: strict Rev.ai long-cue alignment failure, when present.
- `tolerantLongAlignmentFailure`: tolerant Rev.ai long-cue alignment failure, when present.
- `shortCueAlignmentFailure`: short-cue alignment failure, when present.

Show all line summaries for one diagnostic row:

```sql
SELECT
  line_summary
FROM ai_audio_short_cue_diagnostics d
CROSS JOIN LATERAL jsonb_array_elements(d."DetailJson" -> 'lineSummaries') AS line_summary
WHERE d."VideoId" = '00000000-0000-0000-0000-000000000000';
```

Show only lines where Gemini attempted to split the line:

```sql
SELECT
  d."VideoId",
  d."LanguageCode",
  line_summary ->> 'lineIndex' AS line_index,
  line_summary ->> 'lineText' AS line_text,
  line_summary -> 'shortCues' AS short_cues
FROM ai_audio_short_cue_diagnostics d
CROSS JOIN LATERAL jsonb_array_elements(d."DetailJson" -> 'lineSummaries') AS line_summary
WHERE (line_summary ->> 'hasSplit')::boolean = true
ORDER BY d."CreatedAt" DESC;
```

Show validation failures:

```sql
SELECT
  d."VideoId",
  d."LanguageCode",
  failure ->> 'lineIndex' AS line_index,
  failure ->> 'reason' AS reason,
  failure ->> 'lineText' AS line_text,
  failure -> 'rawShortCues' AS raw_short_cues,
  failure -> 'expectedTokens' AS expected_tokens,
  failure -> 'actualTokens' AS actual_tokens
FROM ai_audio_short_cue_diagnostics d
CROSS JOIN LATERAL jsonb_array_elements(d."DetailJson" -> 'shortCueValidationFailures') AS failure
ORDER BY d."CreatedAt" DESC;
```

Show short-cue alignment failure details:

```sql
SELECT
  "CreatedAt",
  "VideoId",
  "LanguageCode",
  "DetailJson" -> 'shortCueAlignmentFailure' AS short_cue_alignment_failure
FROM ai_audio_short_cue_diagnostics
WHERE "DetailJson" -> 'shortCueAlignmentFailure' IS NOT NULL
ORDER BY "CreatedAt" DESC;
```

## Join With Generated Audio

Join diagnostics back to the media row:

```sql
SELECT
  d."CreatedAt",
  d."VideoId",
  v."OriginalFileName",
  v."TranscriptLanguageCode",
  v."DurationMs",
  v."IsTranscriptionEstimated",
  d."Reason",
  d."LongAlignmentMode",
  d."ShortAlignmentAttempted",
  d."LinesWithSplitCount",
  d."FlattenedShortCueCount",
  d."WordTimingCount"
FROM ai_audio_short_cue_diagnostics d
JOIN user_videos v ON v."Id" = d."VideoId"
ORDER BY d."CreatedAt" DESC
LIMIT 100;
```

Find diagnostics for public generated audio:

```sql
SELECT
  d."CreatedAt",
  d."VideoId",
  d."LanguageCode",
  d."ScenarioId",
  d."Reason",
  v."OriginalFileName"
FROM ai_audio_short_cue_diagnostics d
JOIN user_videos v ON v."Id" = d."VideoId"
WHERE v."IsAiGenerated" = true
  AND v."MediaContentType" LIKE 'audio/%'
  AND v."IsPublic" = true
ORDER BY d."CreatedAt" DESC;
```

## How To Read Reasons

Common values:

- `ShortCueValidationFailed`: Gemini returned short cues, but they did not exactly cover the parent line tokens or had invalid boundaries.
- `NoShortCueSplitsGenerated`: all lines had only one short cue, so there was nothing useful to save as short mode.
- `NoWordTimingForShortCueAlignment`: Rev.ai word timing was unavailable, so short cues could not be aligned.
- `LongCueAlignmentFallbackUsed`: long cue alignment fell back to Gemini or estimated timing, so short cues were intentionally dropped.
- `ShortCueAlignmentFailed`: short-cue fragments existed and word timing existed, but the short-cue alignment builder could not safely map them to audio timing.

## Useful Investigation Flow

1. Start with the reason breakdown query.
2. If one language is dominant, run the reason-by-language query.
3. For `ShortCueValidationFailed`, inspect `shortCueValidationFailures` and compare `expectedTokens` with `actualTokens`.
4. For `ShortCueAlignmentFailed`, inspect `shortCueAlignmentFailure` and the related line summaries.
5. For `NoWordTimingForShortCueAlignment`, check whether the issue is tied to one locale or one alignment mode.
6. Join with `user_videos` to inspect the transcript, duration, and generated audio metadata.

