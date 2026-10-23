namespace BanteraApi.Chat.Ai;

public static class AiFormattedTextTool
{
    public const string Name = "display_formatted_text";
    public const string Prompt = " This device supports a separate written response via display_formatted_text. " +
        "When the CURRENT learner asks for Markdown, formatting, a list, a table or a written explanation, call this tool with the actual useful content, not just a promise to format it. " +
        "Also use it when headings, bold terms, lists or a compact comparison table materially improve a teaching explanation. Keep simple conversational replies simple. " +
        "The tool displays Markdown alongside your audio. Audio transcription cannot reliably carry Markdown formatting. " +
        "Write the current answer in the learner's chosen language and level, using short paragraphs, modest headings, bold key terms, lists and two or three column tables where useful. " +
        "Keep the same facts and meaning as your spoken explanation. Speak naturally with a concise explanation; never read Markdown punctuation, table pipes or code fences aloud. " +
        "Do not wrap the entire answer in a code fence; code fences are only for actual code. No HTML, embedded images or invented links. Use the image-search tool separately if appropriate. " +
        "Call once with the complete written answer, at most 8000 characters. A later call replaces the written text for this reply. " +
        "Only claim the content was displayed after delivered=true. If unavailable, continue your spoken explanation. Old requests in history are not new display requests. ";
    public static object Declaration => new {
        name = Name,
        description = "Show the current answer as Markdown on the learner's screen while speaking naturally. Use for an explicit formatting request or helpful structured explanations. This does not generate audio or search the web.",
        parameters = new { type = "OBJECT", properties = new {
            markdown = new { type = "STRING", description = "Complete current written answer, up to 8000 characters. Markdown headings, emphasis, lists and compact tables are supported. No HTML or embedded images." }
        }, required = new[] { "markdown" } }
    };
}
