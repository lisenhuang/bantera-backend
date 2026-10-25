using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BanteraApi.Practice;

/// <summary>
/// ICU's locale-specific spelled-number parser. This only identifies a numeric
/// prefix; callers must check word boundaries, units and ambiguous expressions.
/// Native ICU is also the globalization dependency of the Linux .NET runtime.
/// If its C API is unavailable, existing deterministic matching remains usable.
/// </summary>
internal static class PracticeNumberSpellout
{
    private const int MaximumInputLength = 1200;
    // Double cannot distinguish adjacent integers beyond this limit.
    private const double MaximumExactInteger = 9007199254740991d;
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    {
        "en", "es", "fr", "de", "it", "zh", "yue", "ja", "ko", "pt", "ar",
        "ru", "hi", "id", "vi", "th", "tr", "nl", "pl", "sv", "da", "nb",
        "fi", "uk", "el", "cs", "sk", "hu", "ro", "hr", "he", "ms", "ca",
    };
    private static readonly Dictionary<string, string> ProductLocales = LearningLanguageCatalog.Items
        .ToDictionary(x => x.Identifier.ToLowerInvariant(), x => x.Identifier.Replace('-', '_'), StringComparer.Ordinal);
    private static readonly Lazy<NativeApi?> Api = new(NativeApi.Load);
    // Keys are limited to product locales and Languages, so untrusted locale
    // values cannot grow this cache. Regional number words matter, e.g. septante.
    private static readonly ConcurrentDictionary<string, Lazy<Formatter?>> Formatters = new(StringComparer.Ordinal);

    internal static bool Available => Api.Value is not null;

    internal static bool TryParsePrefix(string text, string locale, out decimal value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (string.IsNullOrEmpty(text) || text.Length > MaximumInputLength || string.IsNullOrWhiteSpace(locale))
            return false;
        var normalizedLocale = locale.Trim().Replace('_', '-').ToLowerInvariant();
        var language = normalizedLocale.Split('-')[0];
        if (normalizedLocale == "zh-hk") language = "yue";
        if (!Languages.Contains(language) || Api.Value is not { } api) return false;
        var parserLocale = normalizedLocale == "zh-hk" ? "yue" :
            ProductLocales.GetValueOrDefault(normalizedLocale, language);
        var formatter = Formatters.GetOrAdd(parserLocale,
            name => new Lazy<Formatter?>(() => Formatter.Create(api, name))).Value;
        if (formatter is null) return false;

        // ICU number formatters are not shared concurrently. Input and locale
        // counts are bounded instead of abandoning native work on a timeout.
        lock (formatter.Sync)
        {
            var status = 0;
            var position = 0;
            var parsed = api.Parse(formatter.Handle, text, text.Length, ref position, ref status);
            if (status > 0 || position <= 0 || position > text.Length ||
                !double.IsFinite(parsed) || Math.Abs(parsed) > MaximumExactInteger ||
                !decimal.TryParse(parsed.ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                return false;
            value = result;
            consumed = position;
            return true;
        }
    }

    private sealed class Formatter
    {
        private Formatter(FormatHandle handle) => Handle = handle;
        internal object Sync { get; } = new();
        internal FormatHandle Handle { get; }

        internal static Formatter? Create(NativeApi api, string language)
        {
            var status = 0;
            const int spellout = 5; // UNUM_SPELLOUT
            var pointer = api.Open(spellout, IntPtr.Zero, 0, language, IntPtr.Zero, ref status);
            if (pointer == IntPtr.Zero) return null;
            var handle = new FormatHandle(pointer, api.Close);
            if (status > 0)
            {
                handle.Dispose();
                return null;
            }
            // Natural ASR omits CLDR's discretionary soft hyphens and Thai
            // zero-width spaces. Without this, e.g. "twenty five" can parse as 2005.
            const int lenientParse = 19; // UNUM_LENIENT_PARSE
            api.SetAttribute(handle, lenientParse, 1);
            return new Formatter(handle);
        }
    }

    private sealed class FormatHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly CloseDelegate _close;
        internal FormatHandle(IntPtr pointer, CloseDelegate close) : base(true)
        {
            _close = close;
            SetHandle(pointer);
        }
        protected override bool ReleaseHandle()
        {
            _close(handle);
            return true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenDelegate(int style, IntPtr pattern, int patternLength,
        [MarshalAs(UnmanagedType.LPStr)] string locale, IntPtr parseError, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate double ParseDelegate(FormatHandle format,
        [MarshalAs(UnmanagedType.LPWStr)] string text, int length, ref int position, ref int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetAttributeDelegate(FormatHandle format, int attribute, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate(IntPtr format);

    private sealed record NativeApi(OpenDelegate Open, ParseDelegate Parse,
        SetAttributeDelegate SetAttribute, CloseDelegate Close)
    {
        internal static NativeApi? Load()
        {
            foreach (var library in LibraryNames())
            {
                if (!NativeLibrary.TryLoad(library, out var handle)) continue;
                // Apple exports the unsuffixed C API; Linux ICU normally exports
                // versioned names, e.g. unum_open_76. Keep the successful library
                // loaded for the process lifetime along with the formatter cache.
                foreach (var suffix in ExportSuffixes())
                {
                    if (!NativeLibrary.TryGetExport(handle, "unum_open" + suffix, out var open) ||
                        !NativeLibrary.TryGetExport(handle, "unum_parseDouble" + suffix, out var parse) ||
                        !NativeLibrary.TryGetExport(handle, "unum_setAttribute" + suffix, out var attribute) ||
                        !NativeLibrary.TryGetExport(handle, "unum_close" + suffix, out var close)) continue;
                    return new NativeApi(Marshal.GetDelegateForFunctionPointer<OpenDelegate>(open),
                        Marshal.GetDelegateForFunctionPointer<ParseDelegate>(parse),
                        Marshal.GetDelegateForFunctionPointer<SetAttributeDelegate>(attribute),
                        Marshal.GetDelegateForFunctionPointer<CloseDelegate>(close));
                }
                NativeLibrary.Free(handle);
            }
            return null;
        }

        private static IEnumerable<string> LibraryNames()
        {
            if (OperatingSystem.IsMacOS())
            {
                yield return "/usr/lib/libicucore.A.dylib";
                yield return "libicui18n.dylib";
            }
            else if (OperatingSystem.IsLinux())
            {
                yield return "libicui18n.so";
                // Runtime images need only the versioned shared library, not the
                // development package that creates the unversioned symlink.
                for (var version = 100; version >= 60; version--)
                    yield return "libicui18n.so." + version;
            }
            else if (OperatingSystem.IsWindows())
            {
                yield return "icuin.dll";
                for (var version = 100; version >= 60; version--)
                    yield return "icuin" + version + ".dll";
            }
        }

        private static IEnumerable<string> ExportSuffixes()
        {
            yield return "";
            for (var version = 100; version >= 60; version--) yield return "_" + version;
        }
    }
}
