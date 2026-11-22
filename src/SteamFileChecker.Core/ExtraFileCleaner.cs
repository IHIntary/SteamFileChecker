namespace SteamFileChecker.Core;

public record DeleteCandidate(string Relative, string FullPath, long Size);
public record DeleteSkip(string Relative, string Reason);
public record DeletePlan(List<DeleteCandidate> Files, List<DeleteSkip> Skipped)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>
/// Decides which requested files may be deleted. Only files that the given report classified as "extra" qualify, and each one
/// must still be the same regular file inside the game folder (no links, no path escape, unchanged size).
/// </summary>
public static class ExtraFileCleaner
{
    /// <summary>Windows shell delete (SHFileOperation) does not support paths of MAX_PATH or longer.</summary>
    public const int MaxShellPath = 259;

    public static DeletePlan Plan(Game game, ScanReport report, IEnumerable<string> requested)
    {
        var files = new List<DeleteCandidate>(); var skipped = new List<DeleteSkip>();
        if (report.AppId != game.AppId) throw new InvalidOperationException("报告与游戏不匹配。");
        if (!report.ScopeComplete) throw new InvalidOperationException("检测范围不完整，无法确定额外文件，不允许删除。");
        var root = Path.GetFullPath(game.Path).TrimEnd(Path.DirectorySeparatorChar);
        var extras = report.Files.Where(f => f.Status == "extra").GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var relative in requested.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? reason = null; string full = "";
            try
            {
                if (!extras.TryGetValue(relative, out var row)) reason = "不是本次报告中的额外文件";
                else
                {
                    full = Path.GetFullPath(Path.Combine(root, Verifier.Normalize(relative).Replace('/', Path.DirectorySeparatorChar)));
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) reason = "路径不在游戏目录内";
                    else if (full.Length > MaxShellPath) reason = "路径过长，无法移到回收站";
                    else if (!File.Exists(full)) reason = "文件已不存在";
                    else if (UnderLink(root, full)) reason = "位于符号链接或目录联接中";
                    else if (new FileInfo(full).Length != row.LocalSize) reason = "检测后文件大小已变化，请重新检测";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException) { reason = ex.Message; }
            if (reason != null) skipped.Add(new(relative, reason));
            else files.Add(new(relative, full, new FileInfo(full).Length));
        }
        return new(files, skipped);
    }

    /// <summary>True when the file itself or any folder between it and the game root is a reparse point.</summary>
    private static bool UnderLink(string root, string full)
    {
        for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    /// <summary>Report without the rows of files that are gone after deletion.</summary>
    public static ScanReport WithoutDeleted(ScanReport report, IEnumerable<string> deleted)
    {
        var gone = deleted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return report with { Files = report.Files.Where(f => !(f.Status == "extra" && gone.Contains(f.Path))).ToList() };
    }
}
