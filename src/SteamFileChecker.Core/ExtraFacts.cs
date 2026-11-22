namespace SteamFileChecker.Core;

/// <summary>
/// Verifiable facts about a file that is not in the Steam manifest. These are observations, not a judgement: the program
/// does not decide whether a file is safe, harmful or affects the game.
/// </summary>
public static class ExtraFacts
{
    /// <summary>File's last-write time is earlier than the install/update time Steam recorded for the game (ACF "lastupdated").</summary>
    public const string OlderThanUpdate = "olderThanUpdate";
    /// <summary>File is in the same folder as an official .exe of the game.</summary>
    public const string BesideExecutable = "besideExecutable";

    private static readonly HashSet<string> Program = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".asi", ".so", ".dylib", ".bat", ".cmd", ".ps1", ".lua", ".py", ".js" };
    private static readonly HashSet<string> Package = new(StringComparer.OrdinalIgnoreCase) { ".pak", ".ucas", ".utoc", ".sig", ".bundle", ".assets", ".resource", ".resS", ".vpk", ".pck", ".bdt", ".bhd", ".arc", ".rpf", ".big", ".forge" };
    private static readonly HashSet<string> Config = new(StringComparer.OrdinalIgnoreCase) { ".ini", ".cfg", ".config", ".json", ".xml", ".toml", ".yaml", ".yml", ".vdf" };
    private static readonly HashSet<string> Log = new(StringComparer.OrdinalIgnoreCase) { ".log", ".dmp", ".mdmp", ".etl" };

    /// <summary>File type by extension only: program, package, config, log or other.</summary>
    public static string Kind(string path)
    {
        var ext = Path.GetExtension(path);
        return Program.Contains(ext) ? "program" : Package.Contains(ext) ? "package" : Config.Contains(ext) ? "config" : Log.Contains(ext) ? "log" : "other";
    }

    /// <summary>Folders (relative, '/'-separated, "" for the game root) that contain an official .exe.</summary>
    public static HashSet<string> ExecutableFolders(IEnumerable<OfficialFile> official) =>
        official.Where(f => f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Select(f => Folder(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Folder(string relative) { var i = relative.LastIndexOf('/'); return i < 0 ? "" : relative[..i]; }

    public static List<string> For(string relative, DateTime lastWriteUtc, long lastUpdatedUnix, HashSet<string> executableFolders)
    {
        var facts = new List<string>();
        if (lastUpdatedUnix > 0 && lastWriteUtc < DateTimeOffset.FromUnixTimeSeconds(lastUpdatedUnix).UtcDateTime) facts.Add(OlderThanUpdate);
        if (executableFolders.Contains(Folder(relative))) facts.Add(BesideExecutable);
        return facts;
    }
}
