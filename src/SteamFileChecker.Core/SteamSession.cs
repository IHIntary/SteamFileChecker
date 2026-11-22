using SteamKit2;
using SteamKit2.Authentication;

namespace SteamFileChecker.Core;

public sealed class SteamSession : IDisposable
{
    private readonly SteamClient client = new();
    private readonly CallbackManager callbacks;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsLoggedOn { get; private set; }
    public string? AccountName { get; private set; }
    /// <summary>Result of the last logon attempt, e.g. to tell an expired saved token (AccessDenied etc.) from a network failure.</summary>
    public EResult? LogOnResult { get; private set; }
    public SteamSession()
    {
        callbacks = new(client);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => { IsLoggedOn = false; connected.TrySetException(new IOException("Steam 连接断开")); loggedOn.TrySetException(new IOException("Steam 连接断开")); });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => { LogOnResult = c.Result; if (c.Result == EResult.OK) { IsLoggedOn = true; loggedOn.TrySetResult(); } else loggedOn.TrySetException(new IOException($"Steam 登录失败：{c.Result}")); });
        _ = Task.Run(() => { while (!lifetime.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); });
    }

    /// <summary>A completed sign-in: the account and the refresh token Steam issued for it (used to log on, and to remember the login if asked).</summary>
    public sealed record SignIn(string AccountName, string RefreshToken);

    private async Task Connect(CancellationToken ct)
    {
        client.Connect();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(40), ct);
    }

    private async Task LogOn(SignIn signIn, CancellationToken ct)
    {
        client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails { Username = signIn.AccountName, AccessToken = signIn.RefreshToken, ShouldRememberPassword = true });
        await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(40), ct);
        AccountName = signIn.AccountName;
    }

    /// <summary>QR sign-in with the Steam mobile app. <paramref name="remember"/> asks Steam for a long-lived (persistent) token.</summary>
    public async Task<SignIn> LoginWithQr(bool remember, Action<string> challenge, CancellationToken ct)
    {
        await Connect(ct);
        var session = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails { IsPersistentSession = remember }).WaitAsync(ct);
        session.ChallengeURLChanged = () => challenge(session.ChallengeURL);
        challenge(session.ChallengeURL);
        var result = await session.PollingWaitForResultAsync(ct);
        var signIn = new SignIn(result.AccountName, result.RefreshToken);
        await LogOn(signIn, ct);
        return signIn;
    }

    /// <summary>
    /// Account name and password sign-in. Steam Guard is handled by <paramref name="guard"/>: approval in the mobile app,
    /// a mobile authenticator code, or an email code. The password is only passed to Steam and not kept.
    /// </summary>
    public async Task<SignIn> LoginWithPassword(string username, string password, bool remember, IAuthenticator guard, CancellationToken ct)
    {
        await Connect(ct);
        var session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username, Password = password, IsPersistentSession = remember, Authenticator = guard,
        }).WaitAsync(ct);
        var result = await session.PollingWaitForResultAsync(ct);
        var signIn = new SignIn(result.AccountName, result.RefreshToken);
        await LogOn(signIn, ct);
        return signIn;
    }

    /// <summary>Logs on with a previously saved refresh token (remembered login).</summary>
    public async Task LoginWithToken(SignIn saved, CancellationToken ct)
    {
        await Connect(ct);
        await LogOn(saved, ct);
    }

    private async Task<KeyValue> AppInfo(uint appId, CancellationToken ct)
    {
        var apps = client.GetHandler<SteamApps>()!;
        var tokens = await apps.PICSGetAccessTokens(appId, null).ToTask().WaitAsync(ct);
        tokens.AppTokens.TryGetValue(appId, out var token);
        var result = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(appId, token), null).ToTask().WaitAsync(ct);
        return result.Results?.SelectMany(r => r.Apps.Values).FirstOrDefault(a => a.ID == appId)?.KeyValues ?? throw new IOException($"无法获取 App {appId} 官方配置");
    }

    private async Task<KeyValue?> TryAppInfo(uint appId, Dictionary<uint, KeyValue?> known, List<string> warnings, CancellationToken ct)
    {
        if (known.TryGetValue(appId, out var cached)) return cached;
        KeyValue? info = null;
        try { info = await AppInfo(appId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(ex.Message); }
        known[appId] = info;
        return info;
    }

    private static KeyValue? Listed(KeyValue? info, uint depotId)
    {
        var metadata = info?["depots"][depotId.ToString()];
        return metadata == null || metadata.Children.Count == 0 ? null : metadata;
    }

    public async Task<Snapshot> Resolve(Game game, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        if (!IsLoggedOn) throw new InvalidOperationException("请先连接 Steam。");
        var files = new List<OfficialFile>(); var scopes = new List<DepotScope>(); var warnings = new List<string>();
        var appInfos = new Dictionary<uint, KeyValue?>();
        var info = await TryAppInfo(game.AppId, appInfos, warnings, ct);
        var complete = info != null && game.Depots.Count > 0;
        if (game.Depots.Count == 0) warnings.Add("没有已安装 Depot 记录，所有未匹配文件均待确认。");
        var installed = game.Depots.Select(d => d.Id).ToHashSet();
        if (info?["depots"].Children.Any(d => uint.TryParse(d.Name, out var id) && !installed.Contains(id) && d["dlcappid"].AsUnsignedInteger() > 0) == true)
        {
            complete = false;
            warnings.Add("官方配置包含未记录为已安装的 DLC Depot；其未匹配本地文件保留为待确认，不推测 DLC 已安装。");
        }
        using var cdn = new SteamKit2.CDN.Client(client);
        foreach (var depot in game.Depots)
        {
            ct.ThrowIfCancellationRequested();
            var baseMeta = Listed(info, depot.Id);
            var baseDlc = baseMeta?["dlcappid"].AsUnsignedInteger() ?? 0;
            var localDlc = depot.DlcAppId ?? 0;
            var dlc = localDlc > 0 ? localDlc : baseDlc;
            if (dlc == game.AppId) dlc = 0;
            var name = dlc > 0 ? $"DLC {dlc} · Depot {depot.Id}" : $"Depot {depot.Id}";
            progress.Report(new("获取官方清单", scopes.Count, game.Depots.Count, name));
            try
            {
                if (localDlc > 0 && baseDlc > 0 && localDlc != baseDlc)
                {
                    complete = false;
                    warnings.Add($"Depot {depot.Id} 的 DLC 归属不一致：本地记录为 {localDlc}，本体官方配置为 {baseDlc}；按本地记录优先尝试。");
                }
                var dlcInfo = dlc > 0 ? await TryAppInfo(dlc, appInfos, warnings, ct) : null;
                var dlcMeta = Listed(dlcInfo, depot.Id);
                var origin = new DepotOrigin(game.AppId, dlc, baseMeta != null, baseMeta?["depotfromapp"].AsUnsignedInteger() ?? 0, dlcMeta != null, dlcMeta?["depotfromapp"].AsUnsignedInteger() ?? 0);
                if (!origin.Confirmed)
                {
                    complete = false;
                    warnings.Add(dlc > 0
                        ? $"{name} 未出现在本体或 DLC {dlc} 的官方配置中，归属未确认；仍按本地记录尝试获取。"
                        : $"Depot {depot.Id} 的官方内容归属未确认。");
                }
                if (!ulong.TryParse(depot.ManifestId, out var manifestId) || manifestId == 0) throw new InvalidDataException("缺少已安装 Manifest ID，不自动切换到最新版。");
                // Beta branches belong to the base app; a DLC's own config usually only has "public".
                string[] BranchesFor(uint owner) => owner == game.AppId || game.Branch == "public" ? [game.Branch] : [game.Branch, "public"];
                var fetched = await FetchFiles(depot.Id, depot.ManifestId, origin.Candidates(), BranchesFor, cdn, ct);
                files.AddRange(fetched.Select(f => f with { Source = name }));
                scopes.Add(new(depot.Id, depot.ManifestId, name, dlc == 0 ? null : dlc, true, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                complete = false; scopes.Add(new(depot.Id, depot.ManifestId, name, dlc == 0 ? null : dlc, false, ex.Message)); warnings.Add($"{name}：{ex.Message}");
            }
        }

        // Shared depots (ACF "SharedDepots", e.g. 228980 Steamworks Common Redistributables) are installed into the owning app's
        // own folder (steamapps/common/Steamworks Shared), not into this game's folder. They are not compared here; the owning app
        // has its own install record with pinned Manifest IDs and can be checked as its own library entry.
        foreach (var group in (game.SharedDepots ?? []).Where(s => !installed.Contains(s.Id)).GroupBy(s => s.AppId))
            warnings.Add(SharedDepotNote(group.Key, group.Select(s => s.Id)));
        return new(files, scopes, complete, warnings);
    }

    public static string SharedDepotNote(uint appId, IEnumerable<uint> depotIds) =>
        $"共享 Depot {string.Join("、", depotIds)} 属于 App {appId}，Steam 将其安装在该应用自己的目录（通常为 steamapps/common/Steamworks Shared），不在本游戏目录中，因此不纳入本次比较；可在游戏库中单独检测该应用。";

    /// <summary>
    /// Official file list for one depot manifest, downloaded from Steam on every scan (nothing is stored). The key is requested with each candidate owner AppID in order;
    /// the AppID Steam accepts is used for the manifest request code and CDN authorization.
    /// </summary>
    private async Task<List<OfficialFile>> FetchFiles(uint depotId, string manifestText, IReadOnlyList<uint> candidates, Func<uint, string[]> branchesFor, SteamKit2.CDN.Client cdn, CancellationToken ct)
    {
        var manifestId = ulong.Parse(manifestText);
        var apps = client.GetHandler<SteamApps>()!; var content = client.GetHandler<SteamContent>()!;

        var attempts = new List<(uint App, EResult Result)>();
        byte[]? depotKey = null; uint owner = 0;
        foreach (var app in candidates)
        {
            var key = await apps.GetDepotDecryptionKey(depotId, app).ToTask().WaitAsync(ct);
            attempts.Add((app, key.Result));
            if (key.Result == EResult.OK) { depotKey = key.DepotKey; owner = app; break; }
        }
        if (depotKey == null) throw new IOException(DepotOrigin.DescribeKeyFailure(depotId, attempts));

        var branches = branchesFor(owner);
        ulong code = 0;
        foreach (var branch in branches)
        {
            code = await content.GetManifestRequestCode(depotId, owner, manifestId, branch).WaitAsync(ct);
            if (code != 0) break;
        }
        if (code == 0) throw new IOException($"无法获取该版本的清单请求码（App {owner}，分支 {string.Join("/", branches)}）");
        var servers = await content.GetServersForSteamPipe().WaitAsync(ct);
        DepotManifest? manifest = null; Exception? last = null;
        foreach (var server in servers.Where(s => s.Type is "CDN" or "SteamCache").Take(4))
        {
            try
            {
                try { manifest = await cdn.DownloadManifestAsync(depotId, manifestId, code, server, depotKey).WaitAsync(ct); }
                catch (SteamKitWebRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var auth = await content.GetCDNAuthToken(owner, depotId, server.Host ?? throw new IOException("CDN 缺少主机名")).WaitAsync(ct);
                    if (auth.Result != EResult.OK) throw new IOException($"CDN 授权失败（App {owner}）：{auth.Result}");
                    manifest = await cdn.DownloadManifestAsync(depotId, manifestId, code, server, depotKey, cdnAuthToken: auth.Token).WaitAsync(ct);
                }
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        if (manifest == null) throw new IOException("官方 CDN 清单获取失败", last);
        if (manifest.DepotID != depotId || manifest.ManifestGID != manifestId || manifest.FilenamesEncrypted || manifest.Files == null) throw new InvalidDataException("清单身份不符或尚未解密");
        return LocalManifests.ToOfficialFiles(manifest, depotId);
    }
    public void Dispose() { lifetime.Cancel(); client.Disconnect(); }
}
