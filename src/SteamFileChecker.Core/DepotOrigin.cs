using SteamKit2;

namespace SteamFileChecker.Core;

/// <summary>
/// Where a depot belongs: the base app config, the owning DLC's own config, or neither.
/// Steam requires the AppID that actually owns the depot when requesting its key, manifest request code and CDN token.
/// </summary>
public sealed record DepotOrigin(uint BaseAppId, uint DlcAppId, bool InBaseConfig, uint BaseDepotFromApp, bool InDlcConfig, uint DlcDepotFromApp)
{
    /// <summary>The depot is listed in at least one official config (base app or its DLC).</summary>
    public bool Confirmed => InBaseConfig || InDlcConfig;

    /// <summary>AppIDs to try for the depot key, most authoritative first. Unconfirmed fallbacks come last.</summary>
    public List<uint> Candidates()
    {
        var list = new List<uint>();
        void Add(uint app) { if (app != 0 && !list.Contains(app)) list.Add(app); }
        if (InDlcConfig) { Add(DlcDepotFromApp); Add(DlcAppId); }
        if (InBaseConfig) { Add(BaseDepotFromApp); Add(BaseAppId); }
        // Not confirmed by any config: prefer the DLC recorded locally by Steam, then the base app.
        Add(DlcAppId);
        Add(BaseAppId);
        return list;
    }

    public static string DescribeKeyFailure(uint depotId, IReadOnlyList<(uint App, EResult Result)> attempts)
    {
        var apps = string.Join("、", attempts.Select(a => $"App {a.App}"));
        if (attempts.Count == 0) return $"Depot {depotId} 没有可用于请求密钥的 AppID。";
        if (attempts.Any(a => a.Result == EResult.AccessDenied))
            return $"账号无权获取 Depot {depotId} 的密钥（AccessDenied；已尝试 {apps}）。请确认当前登录账号拥有该内容。";
        if (attempts.All(a => a.Result == EResult.FileNotFound))
            return $"Steam 未找到 Depot {depotId} 与 {apps} 的对应关系（FileNotFound）。这不表示账号无权，也不表示本地文件缺失。";
        return $"Depot {depotId} 密钥请求失败（{string.Join("；", attempts.Select(a => $"App {a.App}：{a.Result}"))}）。";
    }
}
