namespace BanteraApi.Chat.Ai;

public sealed record BanteraAiVoice(string Name, string Style, string Gender);

public static class BanteraAiVoices
{
    public const string Default = "Puck";
    // Google's Live API catalogue and voice presentation labels:
    // https://firebase.google.com/docs/ai-logic/live-api/configuration#response-voice
    public static readonly IReadOnlyList<BanteraAiVoice> All = [
        new("Achernar", "Soft", "Female"),
        new("Achird", "Friendly", "Male"),
        new("Algenib", "Gravelly", "Male"),
        new("Algieba", "Smooth", "Male"),
        new("Alnilam", "Firm", "Male"),
        new("Aoede", "Breezy", "Female"),
        new("Autonoe", "Bright", "Female"),
        new("Callirrhoe", "Easy-going", "Female"),
        new("Charon", "Informative", "Male"),
        new("Despina", "Smooth", "Female"),
        new("Enceladus", "Breathy", "Male"),
        new("Erinome", "Clear", "Female"),
        new("Fenrir", "Excitable", "Male"),
        new("Gacrux", "Mature", "Female"),
        new("Iapetus", "Clear", "Male"),
        new("Kore", "Firm", "Female"),
        new("Laomedeia", "Upbeat", "Female"),
        new("Leda", "Youthful", "Female"),
        new("Orus", "Firm", "Male"),
        new("Puck", "Upbeat", "Male"),
        new("Pulcherrima", "Forward", "Female"),
        new("Rasalgethi", "Informative", "Male"),
        new("Sadachbia", "Lively", "Male"),
        new("Sadaltager", "Knowledgeable", "Male"),
        new("Schedar", "Even", "Male"),
        new("Sulafat", "Warm", "Female"),
        new("Umbriel", "Easy-going", "Male"),
        new("Vindemiatrix", "Gentle", "Female"),
        new("Zephyr", "Bright", "Female"),
        new("Zubenelgenubi", "Casual", "Male"),
    ];
    public static bool IsSupported(string? voice) => All.Any(v => v.Name == voice);
}
