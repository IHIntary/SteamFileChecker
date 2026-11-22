using System.Security.Cryptography;

namespace SteamFileChecker.Core;

public static class Verifier
{
    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or "" || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException($"不安全的清单路径：{path}");
        return path;
    }

    /// <summary>Steam manifests record an all-zero hash for empty files instead of the SHA-1 of empty input (DA39A3EE…).</summary>
    public static bool IsEmptyFileMatch(long localSize, OfficialFile target) =>
        localSize == 0 && target.Size == 0 && target.Sha1.Length > 0 && target.Sha1.All(c => c == '0');

    public static async Task<ScanReport> Run(Game game, Snapshot snapshot, IProgress<ScanProgress>? progress, CancellationToken ct, VerifyMode mode = VerifyMode.Full)
    {
        if (game.Updating) throw new IOException("Steam 安装状态不是已完成，请等待更新结束后重新扫描游戏库。");
        if (!Directory.Exists(game.Path)) throw new DirectoryNotFoundException(game.Path);
        if ((File.GetAttributes(game.Path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不扫描链接形式的游戏根目录。");
        var started = DateTimeOffset.UtcNow;
        var results = new List<FileResult>();
        var warnings = new List<string>(snapshot.Warnings);
        var expected = snapshot.Files.GroupBy(f => Normalize(f.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new List<string>();
        var count = 0;
        void Unreadable(string relative, string reason) { blocked.Add(relative.TrimEnd('/') + "/"); results.Add(new(relative, "unreadable", null, null, null, null, "本地", reason)); }
        async Task Walk(string dir)
        {
            ct.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Unreadable(System.IO.Path.GetRelativePath(game.Path, dir).Replace('\\', '/'), ex.Message); return; }
            foreach (var full in entries)
            {
                ct.ThrowIfCancellationRequested();
                var relative = System.IO.Path.GetRelativePath(game.Path, full).Replace('\\', '/');
                try
                {
                    var attributes = File.GetAttributes(full);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { seen.Add(relative); Unreadable(relative, "跳过符号链接或目录联接"); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { await Walk(full); continue; }
                    seen.Add(relative);
                    var info = new FileInfo(full);
                    if (!expected.TryGetValue(relative, out var candidates))
                    {
                        results.Add(new(relative, snapshot.Complete ? "extra" : "uncertain", info.Length, null, null, null, "未匹配", snapshot.Complete ? "可能是配置、MOD 或运行时生成文件，请人工确认" : "官方内容范围不完整"));
                    }
                    else
                    {
                        var target = candidates[0];
                        var source = string.Join(" · ", candidates.Select(f => f.Source).Distinct());
                        if (candidates.Any(f => !f.Supported) || candidates.Select(f => (f.Size, f.Sha1)).Distinct().Count() != 1)
                            results.Add(new(relative, "uncertain", info.Length, target.Size, null, target.Sha1, source, "清单路径存在覆盖冲突或特殊文件，未推测覆盖顺序"));
                        else if (mode == VerifyMode.Quick)
                        {
                            // Quick mode: name and size only; contents are not read, so equal size counts as a match.
                            var matches = info.Length == target.Size;
                            results.Add(new(relative, matches ? "match" : snapshot.Complete ? "changed" : "uncertain", info.Length, target.Size, null, target.Sha1, source, !matches && !snapshot.Complete ? "清单不完整，无法排除其他 Depot 覆盖此文件" : null));
                        }
                        else
                        {
                            var size = info.Length; var modified = info.LastWriteTimeUtc;
                            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            var hash = size == target.Size ? Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)) : null;
                            info.Refresh();
                            if (info.Length != size || info.LastWriteTimeUtc != modified) throw new IOException("读取期间文件发生变化");
                            var matches = size == target.Size && (string.Equals(hash, target.Sha1, StringComparison.OrdinalIgnoreCase) || IsEmptyFileMatch(size, target));
                            results.Add(new(relative, matches ? "match" : snapshot.Complete ? "changed" : "uncertain", size, target.Size, hash, target.Sha1, source, !matches && !snapshot.Complete ? "清单不完整，无法排除其他 Depot 覆盖此文件" : null));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { seen.Add(relative); results.Add(new(relative, "unreadable", null, null, null, null, "本地", ex.Message)); }
                progress?.Report(new(mode == VerifyMode.Quick ? "比对文件名与大小" : "校验本地文件", ++count, Math.Max(expected.Count, count), relative));
            }
        }
        await Walk(game.Path);
        foreach (var (path, candidates) in expected.Where(e => !seen.Contains(e.Key)))
        {
            var unknown = !snapshot.Complete || blocked.Any(p => p == "./" || path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || candidates.Any(f => !f.Supported) || candidates.Select(f => (f.Size, f.Sha1)).Distinct().Count() != 1;
            results.Add(new(path, unknown ? "uncertain" : "missing", null, candidates[0].Size, null, candidates[0].Sha1, string.Join(" · ", candidates.Select(f => f.Source)), unknown ? "目录不可读取或清单冲突" : null));
        }
        if (!string.IsNullOrEmpty(game.AcfHash) && (!File.Exists(game.AcfPath) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(game.AcfPath, ct))) != game.AcfHash))
            throw new IOException("检测期间 Steam 安装记录变化，结果已丢弃，请重新检测。");
        return new(game.AppId, game.Name, game.BuildId, game.Branch, started, snapshot.Complete, snapshot.Depots, warnings, results, mode == VerifyMode.Quick ? "quick" : "full", snapshot.Source);
    }
}
