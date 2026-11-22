using System.Security.Cryptography;
using SteamKit2;

namespace SteamFileChecker.Core;

public static class Library
{
    public static List<Game> Discover(string steamRoot)
    {
        static string Canonical(string path) => System.IO.Path.GetFullPath(path.Replace('/', System.IO.Path.DirectorySeparatorChar)).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        steamRoot = Canonical(steamRoot);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var libraries = System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraries))
            foreach (var entry in Read(libraries).Children)
                if (entry["path"].Value is { Length: > 0 } path) roots.Add(Canonical(path));
        var games = new List<Game>();
        foreach (var root in roots)
        {
            var steamapps = System.IO.Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                games.Add(ReadGame(acf));
        }
        // Steam normally installs one copy per AppID. Prefer a fully installed copy if stale records remain in another library.
        return games.OrderBy(g => g.Updating).DistinctBy(g => g.AppId).OrderBy(g => g.Name).ToList();
    }

    public static KeyValue Read(string path)
    {
        var value = new KeyValue();
        using var stream = File.OpenRead(path);
        if (!value.ReadAsText(stream)) throw new InvalidDataException($"无法解析安装记录：{path}");
        return value;
    }

    public static Game ReadGame(string acf)
    {
        var bytes = File.ReadAllBytes(acf);
        var kv = new KeyValue();
        using var stream = new MemoryStream(bytes);
        if (!kv.ReadAsText(stream)) throw new InvalidDataException("无效 ACF");
        var basePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(acf)!, "common"));
        var install = kv["installdir"].Value ?? throw new InvalidDataException("安装目录缺失");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, install));
        if (!path.StartsWith(basePath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装路径越界");
        var depots = kv["InstalledDepots"].Children.Select(d =>
        {
            // Steam records the owning DLC AppID for DLC depots; it is needed to request the depot key and manifest.
            var dlc = d["dlcappid"].AsUnsignedInteger();
            return new InstalledDepot(uint.Parse(d.Name!), d["manifest"].Value ?? "0", dlc > 0 ? dlc : null);
        }).ToList();
        // "SharedDepots" { "<depot>" "<owning app>" }: content from another app installed into this game's folder (e.g. _CommonRedist).
        var shared = kv["SharedDepots"].Children
            .Where(d => uint.TryParse(d.Name, out _) && d.AsUnsignedInteger() > 0)
            .Select(d => new SharedDepot(uint.Parse(d.Name!), d.AsUnsignedInteger()))
            .ToList();
        var flags = kv["StateFlags"].AsUnsignedInteger();
        return new Game(kv["appid"].AsUnsignedInteger(), kv["name"].Value ?? install, path, kv["buildid"].Value ?? "未知", kv["UserConfig"]["BetaKey"].Value ?? "public", acf, Convert.ToHexString(SHA256.HashData(bytes)), depots, flags != 4, shared);
    }
}
