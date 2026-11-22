using System.Text.RegularExpressions;

namespace SteamFileChecker.Core;

/// <summary>
/// Finds a game's artwork in the Steam client's local library cache (steam/appcache/librarycache), the same images the
/// Steam library shows. Nothing is downloaded. Two cache layouts exist:
/// newer: librarycache/&lt;appid&gt;/[&lt;hash&gt;/]library_hero.jpg, library_header.jpg, logo.png, and &lt;hash&gt;.jpg (the small icon);
/// older: librarycache/&lt;appid&gt;_library_hero.jpg, &lt;appid&gt;_header.jpg, &lt;appid&gt;_icon.jpg.
/// </summary>
public static partial class SteamArtwork
{
    public enum Kind { Hero, Icon }

    [GeneratedRegex("^[0-9a-f]{40}\\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex IconName();

    public static string? Find(string steamRoot, uint appId, Kind kind)
    {
        // Normalize: the registry stores the Steam path with forward slashes (e.g. d:/steam), enumeration returns backslashes.
        var cache = Path.GetFullPath(Path.Combine(steamRoot, "appcache", "librarycache"));
        if (!Directory.Exists(cache)) return null;
        var folder = Path.Combine(cache, appId.ToString());
        if (Directory.Exists(folder))
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
            string? Pick(params string[] prefixes)
            {
                foreach (var prefix in prefixes)
                {
                    // Exact name first (e.g. library_hero.jpg), then a localized variant (library_hero_schinese.jpg); never the blurred copy.
                    var match = files.Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("_blur", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).FirstOrDefault();
                    if (match != null) return match;
                }
                return null;
            }
            var found = kind switch
            {
                Kind.Hero => Pick("library_hero", "library_header", "header"),
                // The small library icon is stored directly in the app folder under its content hash.
                _ => files.FirstOrDefault(f => Path.GetDirectoryName(f) == folder && IconName().IsMatch(Path.GetFileName(f))),
            };
            if (found != null) return found;
        }
        var legacy = kind == Kind.Hero ? new[] { "_library_hero.jpg", "_header.jpg" } : new[] { "_icon.jpg" };
        return legacy.Select(suffix => Path.Combine(cache, appId + suffix)).FirstOrDefault(File.Exists);
    }

}
