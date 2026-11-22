// #claude
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// Texts in several languages. The dictionaries are the same JSON files the web page uses (Web/i18n/*.json,
/// embedded into the DLL). German is the source language; missing texts fall back to English, then German.
/// </summary>
public static class Loc
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.Ordinal);
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Gets the supported languages (two-letter codes).</summary>
    public static IReadOnlyList<string> Languages { get; } = new[] { "de", "en", "fr", "es", "it" };

    /// <summary>Gets or sets a function returning Jellyfin's server language (e.g. "de" or "en-US").</summary>
    public static Func<string?>? ServerCulture { get; set; }

    /// <summary>Gets the language for texts written into Jellyfin (setting, otherwise the server language, otherwise English).</summary>
    public static string ConfigLanguage
        => Normalize(Plugin.Instance?.Configuration.TextLanguage)
           ?? Normalize(SafeServerCulture())
           ?? "en";

    /// <summary>Returns the supported two-letter code for a language tag, or null.</summary>
    public static string? Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return null;
        }

        var code = lang.Trim().ToLowerInvariant();
        if (code.Length > 2)
        {
            code = code[..2];
        }

        return Languages.Contains(code) ? code : null;
    }

    /// <summary>Gets the merged dictionary of a language (German, overlaid by English, overlaid by the language).</summary>
    public static IReadOnlyDictionary<string, string> Dictionary(string? lang)
    {
        var code = Normalize(lang) ?? "en";
        return Cache.GetOrAdd(code, Build);
    }

    /// <summary>Translates a text into a language.</summary>
    public static string T(string? lang, string key, params (string Name, object? Value)[] args)
    {
        var text = Dictionary(lang).TryGetValue(key, out var s) ? s : key;
        if (args.Length == 0)
        {
            return text;
        }

        return Placeholder.Replace(text, m =>
        {
            foreach (var (name, value) in args)
            {
                if (string.Equals(name, m.Groups[1].Value, StringComparison.Ordinal))
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            return m.Value;
        });
    }

    /// <summary>Translates a text into the language for texts written into Jellyfin.</summary>
    public static string T(string key, params (string Name, object? Value)[] args) => T(ConfigLanguage, key, args);

    private static string? SafeServerCulture()
    {
        try
        {
            return ServerCulture?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> Build(string code)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in code == "de" ? new[] { "de" } : code == "en" ? new[] { "de", "en" } : new[] { "de", "en", code })
        {
            foreach (var pair in Load(layer))
            {
                result[pair.Key] = pair.Value;
            }
        }

        result.Remove("_comment");
        return result;
    }

    private static Dictionary<string, string> Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".i18n." + code + ".json", StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
            return new Dictionary<string, string>();
        }

        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null)
        {
            return new Dictionary<string, string>();
        }

        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? new Dictionary<string, string>();
    }
}
