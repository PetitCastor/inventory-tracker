using System.Text.RegularExpressions;

namespace InventoryTracker.Naming;

/// <summary>
/// The game's own display text, read from the loose <c>global.ini</c> the client loads in
/// place of the packed one: <c>&lt;install&gt;\data\Localization\&lt;g_language&gt;\global.ini</c>,
/// with the language taken from <c>user.cfg</c>. When it is there it is literally what the
/// game shows the player, so it is the best source for a place's name. Most installs do not
/// have one; then this is empty and names come from the logs and the wiki alone.
/// </summary>
public sealed partial class GameLocalization
{
    private readonly Dictionary<string, string> _text;

    public GameLocalization(Dictionary<string, string> text) => _text = text;

    public static readonly GameLocalization Empty = new(new Dictionary<string, string>());

    public int Count => _text.Count;

    /// <summary>The text under <paramref name="key"/>, or null when there is none.</summary>
    public string? Get(string key) =>
        _text.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>
    /// The text of the one key directly below <paramref name="prefix"/> that is a name
    /// rather than a description: "Stanton2_Transfer" finds "Stanton2_Transfer_Seraphim" and
    /// skips its "_Desc" and "_clinic" siblings. Null when there is none, or more than one.
    /// </summary>
    public string? GetOnlyChild(string prefix)
    {
        string? found = null;
        var stem = prefix + "_";

        foreach (var (key, _) in _text)
        {
            if (!key.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = key[stem.Length..];
            if (rest.Length == 0 || rest.Contains('_') || NotNames.Contains(rest)) continue;
            if (found is not null) return null;
            found = key;
        }

        return found is null ? null : Get(found);
    }

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Desc", "Description", "clinic", "Hospital",
    };

    /// <summary>Where the loose file would be for an install, in the language the client is
    /// set to.</summary>
    public static string PathFor(string installDir) =>
        Path.Combine(installDir, "data", "Localization", LanguageOf(installDir), "global.ini");

    // Language folders carry a region: "french_(france)", "chinese_(simplified)".
    [GeneratedRegex(@"^\s*g_language\s*=\s*(?<lang>[^\s;]+)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex LanguageSetting();

    private static string LanguageOf(string installDir)
    {
        try
        {
            var cfg = Path.Combine(installDir, "user.cfg");
            if (File.Exists(cfg) && LanguageSetting().Match(File.ReadAllText(cfg)) is { Success: true } m)
            {
                return m.Groups["lang"].Value;
            }
        }
        catch (IOException)
        {
            // The game can hold user.cfg open while writing it; English is its default.
        }

        return "english";
    }

    /// <summary>Reads the loose file for an install; <see cref="Empty"/> when there is none.</summary>
    public static GameLocalization Load(string installDir)
    {
        var path = PathFor(installDir);
        return File.Exists(path) ? Parse(File.ReadLines(path)) : Empty;
    }

    /// <summary>
    /// One <c>key=value</c> per line. A key can carry a ",P" flag ("Nyx_Keeger_Segment_Social_001,P"),
    /// which is not part of the key.
    /// </summary>
    public static GameLocalization Parse(IEnumerable<string> lines)
    {
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].TrimStart('﻿').Trim();
            var comma = key.IndexOf(',');
            if (comma >= 0) key = key[..comma];
            if (key.Length == 0) continue;

            text[key] = line[(eq + 1)..];
        }

        return new GameLocalization(text);
    }
}
