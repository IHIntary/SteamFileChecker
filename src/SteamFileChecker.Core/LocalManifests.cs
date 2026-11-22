using SteamKit2;

namespace SteamFileChecker.Core;

/// <summary>
/// No-login mode: uses the depot manifests the Steam client already keeps on this computer instead of downloading them.
/// The Steam client stores the manifest of every installed depot as steam/depotcache/&lt;depot&gt;_&lt;manifest&gt;.manifest and the
/// depot decryption keys (needed when file names are encrypted) in steam/config/config.vdf. Nothing is sent over the network.
/// </summary>
public static class LocalManifests
{
    public static string ManifestPath(string steamRoot, uint depotId, string manifestId) =>
        Path.Combine(Path.GetFullPath(steamRoot), "depotcache", $"{depotId}_{manifestId}.manifest");

    /// <summary>Depot decryption keys from config.vdf (InstallConfigStore/Software/Valve/Steam/depots/&lt;id&gt;/DecryptionKey).</summary>
    public static Dictionary<uint, byte[]> ReadDepotKeys(string steamRoot)
    {
        var keys = new Dictionary<uint, byte[]>();
        var config = Path.Combine(Path.GetFullPath(steamRoot), "config", "config.vdf");
        if (!File.Exists(config)) return keys;
        var kv = new KeyValue();
        using (var stream = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (!kv.ReadAsText(stream)) return keys;
        foreach (var depot in kv["Software"]["Valve"]["Steam"]["depots"].Children)
        {
            if (!uint.TryParse(depot.Name, out var id) || depot["DecryptionKey"].Value is not { Length: 64 } hex) continue;
            try { keys[id] = Convert.FromHexString(hex); } catch (FormatException) { }
        }
        return keys;
    }

    /// <summary>Official file list from a local manifest file; verifies it is the expected depot and version.</summary>
    public static List<OfficialFile> Load(string path, uint depotId, ulong manifestId, byte[]? key)
    {
        DepotManifest manifest;
        // Shared read: the Steam client may have the file open.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            manifest = DepotManifest.Deserialize(stream);
        if (manifest.DepotID != depotId || manifest.ManifestGID != manifestId) throw new InvalidDataException("本地清单与安装记录的 Depot 或版本不符");
        if (manifest.FilenamesEncrypted)
        {
            if (key == null) throw new InvalidDataException("本地清单的文件名已加密，但本机 Steam 配置中没有该 Depot 的解密密钥");
            if (!manifest.DecryptFilenames(key)) throw new InvalidDataException("本地清单的文件名解密失败");
        }
        return ToOfficialFiles(manifest, depotId);
    }

    /// <summary>Files of a decrypted manifest (directories excluded), shared by the local and online sources.</summary>
    public static List<OfficialFile> ToOfficialFiles(DepotManifest manifest, uint depotId) =>
        (manifest.Files ?? throw new InvalidDataException("清单没有文件列表"))
            .Where(f => (f.Flags & EDepotFileFlag.Directory) == 0)
            .Select(f => new OfficialFile(Verifier.Normalize(f.FileName), checked((long)f.TotalSize), Convert.ToHexString(f.FileHash), $"Depot {depotId}", string.IsNullOrEmpty(f.LinkTarget) && f.FileHash.Length == 20))
            .ToList();

    /// <summary>
    /// Builds the comparison snapshot from local manifests only. A depot whose manifest is not on this computer is reported as
    /// unavailable and makes the scope incomplete (its absence does not mean the content does not exist).
    /// </summary>
    public static Snapshot Resolve(Game game, string steamRoot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var files = new List<OfficialFile>(); var scopes = new List<DepotScope>(); var warnings = new List<string>();
        var complete = game.Depots.Count > 0;
        if (game.Depots.Count == 0) warnings.Add("没有已安装 Depot 记录，所有未匹配文件均待确认。");
        Dictionary<uint, byte[]> keys;
        try { keys = ReadDepotKeys(steamRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { keys = []; warnings.Add($"无法读取本机 Steam 配置中的 Depot 密钥：{ex.Message}"); }
        foreach (var depot in game.Depots)
        {
            ct.ThrowIfCancellationRequested();
            var dlc = depot.DlcAppId is { } d && d != game.AppId ? d : 0;
            var name = dlc > 0 ? $"DLC {dlc} · Depot {depot.Id}" : $"Depot {depot.Id}";
            progress?.Report(new("读取本地清单", scopes.Count, game.Depots.Count, name));
            try
            {
                if (!ulong.TryParse(depot.ManifestId, out var manifestId) || manifestId == 0) throw new InvalidDataException("缺少已安装 Manifest ID");
                var path = ManifestPath(steamRoot, depot.Id, depot.ManifestId);
                if (!File.Exists(path)) throw new FileNotFoundException($"本机 Steam 没有保存该版本的清单（depotcache\\{Path.GetFileName(path)}），可登录 Steam 后在线获取");
                files.AddRange(Load(path, depot.Id, manifestId, keys.GetValueOrDefault(depot.Id)).Select(f => f with { Source = name }));
                scopes.Add(new(depot.Id, depot.ManifestId, name, dlc == 0 ? null : dlc, true, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
            {
                complete = false; scopes.Add(new(depot.Id, depot.ManifestId, name, dlc == 0 ? null : dlc, false, ex.Message)); warnings.Add($"{name}：{ex.Message}");
            }
        }
        var installed = game.Depots.Select(d => d.Id).ToHashSet();
        foreach (var group in (game.SharedDepots ?? []).Where(s => !installed.Contains(s.Id)).GroupBy(s => s.AppId))
            warnings.Add(SteamSession.SharedDepotNote(group.Key, group.Select(s => s.Id)));
        return new(files, scopes, complete, warnings, "local");
    }
}
