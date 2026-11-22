using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SteamFileChecker.Core;
using System.Text.Json;

namespace SteamFileChecker.Desktop;

internal static class Program
{
    [STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainWindow()); }
}

public sealed class MainWindow : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(17, 16, 28) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SteamSession? steam;
    private CancellationTokenSource? operation;
    private List<Game> games = [];
    private ScanReport? report;
    /// <summary>Per game: artwork paths relative to the Steam library cache, served at https://steam-art.local/.</summary>
    private Dictionary<string, object> artwork = [];
    /// <summary>The install record snapshot the current report was produced from.</summary>
    private Game? reportGame;
    private ScanProgress? currentProgress;
    private string? operationKind;
    private uint? activeAppId;
    private bool libraryLoaded;
    private long operationVersion;
    private readonly string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamFileChecker");
    private readonly LoginStore loginStore;
    /// <summary>Steam root of the current library: depotcache and config.vdf there are the no-login manifest source.</summary>
    private string? steamRoot;
    /// <summary>Pending Steam Guard code request during password sign-in, answered by the page.</summary>
    private TaskCompletionSource<string>? guardCode;
    public MainWindow()
    {
        loginStore = new LoginStore(data);
        Text = "Steam File Checker"; Width = 1440; Height = 940; MinimumSize = new Size(1080, 720);
        StartPosition = FormStartPosition.CenterScreen; Controls.Add(web);
        // Window and taskbar icon (the .exe icon itself comes from <ApplicationIcon> in the project file).
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico")) if (icon != null) Icon = new Icon(icon);
        Load += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
                web.CoreWebView2.SetVirtualHostNameToFolderMapping("checker.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
                // Folder mappings only apply to pages loaded afterwards, so map the Steam artwork cache before navigating.
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steamPath) MapArtworkFolder(steamPath);

                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://checker.local/", StringComparison.Ordinal)) e.Cancel = true; };
                web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                web.CoreWebView2.WebMessageReceived += Receive;
                web.Source = new Uri("https://checker.local/index.html");
            }
            catch (Exception ex) { MessageBox.Show("启动失败，请确认已安装 WebView2 Runtime。\n" + ex.Message); }
        };
        FormClosed += (_, _) => { operation?.Cancel(); steam?.Dispose(); };
        HandleCreated += (_, _) => WindowChrome.Apply(Handle);
        Resize += (_, _) => SendWindowState();
    }

    protected override void WndProc(ref Message m)
    {
        // Remove the native title bar; keep the sizable frame on the other edges (see WindowChrome).
        if (m.Msg == WindowChrome.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var top = WindowChrome.ProposedTop(m.LParam);
            base.WndProc(ref m);
            WindowChrome.AdjustClientArea(ref m, top);
            return;
        }
        base.WndProc(ref m);
    }

    private bool lastMaximized;
    private void SendWindowState()
    {
        var maximized = WindowState == FormWindowState.Maximized;
        if (maximized == lastMaximized || web.CoreWebView2 == null) return;
        lastMaximized = maximized;
        Send("window", new { maximized });
    }

    /// <summary>Title-bar actions requested by the page's own window controls and drag areas.</summary>
    private void HandleWindowAction(string? action)
    {
        switch (action)
        {
            case "drag": WindowChrome.BeginNonClientDrag(Handle, WindowChrome.HTCAPTION); break;
            case "resize-top": if (WindowState == FormWindowState.Normal) WindowChrome.BeginNonClientDrag(Handle, WindowChrome.HTTOP); break;
            case "resize-topleft": if (WindowState == FormWindowState.Normal) WindowChrome.BeginNonClientDrag(Handle, WindowChrome.HTTOPLEFT); break;
            case "resize-topright": if (WindowState == FormWindowState.Normal) WindowChrome.BeginNonClientDrag(Handle, WindowChrome.HTTOPRIGHT); break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "close": Close(); break;
        }
    }
    private void Send(string type, object? payload)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => Send(type, payload)); return; }
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, payload }, Json));
    }
    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith("https://checker.local/", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var message = doc.RootElement; var type = message.GetProperty("type").GetString();
            // Read-only navigation must be able to recover a running task, without starting discovery.
            if (type == "sync")
            {
                if (libraryLoaded || operation != null) return;
                type = "discover";
            }
            if (type == "window") { HandleWindowAction(message.TryGetProperty("action", out var a) ? a.GetString() : null); return; }
            if (type == "cancel") { operation?.Cancel(); return; }
            // Steam Guard code typed in the page while a password sign-in is waiting for it.
            if (type == "guardCode") { guardCode?.TrySetResult(message.TryGetProperty("code", out var c) ? c.GetString()?.Trim() ?? "" : ""); return; }
            if (type == "export")
            {
                if (report == null) throw new InvalidOperationException("暂无报告");
                using var dialog = new SaveFileDialog { Filter = "JSON 报告|*.json", FileName = $"steam-{report.AppId}-{DateTime.Now:yyyyMMdd-HHmm}.json" };
                if (dialog.ShowDialog(this) == DialogResult.OK) { await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions(Json) { WriteIndented = true })); Send("notice", "报告已导出"); }
                return;
            }
            if (operation != null)
            {
                Send("notice", "当前任务仍在进行，已恢复任务状态；如需重新检测，请先取消。");
                return;
            }
            if (type == "deleteExtra")
            {
                await DeleteExtra(message);
                return;
            }
            if (type is "discover" or "folder")
            {
                var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (type == "folder" || string.IsNullOrEmpty(root))
                {
                    using var dialog = new FolderBrowserDialog { Description = "选择 Steam 根目录（包含 steamapps）", UseDescriptionForTitle = true };
                    if (dialog.ShowDialog(this) != DialogResult.OK) { Send("idle", null); return; }
                    root = dialog.SelectedPath;
                }
                operation = new CancellationTokenSource(); operationKind = "discover";
                try
                {
                    SendState();
                    var discovered = await Task.Run(() => Library.Discover(root!), operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    games = discovered; libraryLoaded = true; steamRoot = root;
                    // A library under a different Steam root needs a new mapping, which only takes effect after a reload.
                    if (MapArtwork(root!)) web.CoreWebView2.Reload();
                    if (report != null && !games.Any(g => g.AppId == report.AppId)) report = null;
                    Send("notice", games.Count > 0 ? "已读取本地 Steam 安装记录" : "未发现游戏，请选择正确的 Steam 根目录。");
                }
                finally { operation.Dispose(); operation = null; operationKind = null; }
                // Remembered login: sign in automatically once the library is known.
                if (steam == null && loginStore.Exists) { SendState(); await LoginWithSaved(); }
                return;
            }
            if (type == "logout")
            {
                steam?.Dispose(); steam = null; loginStore.Delete();
                Send("notice", "已退出登录，本机保存的登录信息已清除。");
                return;
            }
            if (type == "login")
            {
                var method = message.TryGetProperty("method", out var mt) ? mt.GetString() : "qr";
                var remember = message.TryGetProperty("remember", out var rm) && rm.GetBoolean();
                operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                operationKind = "login";
                SendState();
                var ct = operation.Token;
                try
                {
                    steam?.Dispose(); steam = new SteamSession();
                    SteamSession.SignIn signIn;
                    if (method == "password")
                    {
                        var username = message.GetProperty("username").GetString() ?? "";
                        var password = message.GetProperty("password").GetString() ?? "";
                        if (username.Length == 0 || password.Length == 0) throw new InvalidOperationException("请输入账号和密码。");
                        var guard = new PageAuthenticator(async (kind, email, retry) =>
                        {
                            guardCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                            Send("guard", new { kind, email, retry });
                            try { return await guardCode.Task.WaitAsync(ct); } finally { guardCode = null; }
                        }, text => Send("notice", text));
                        signIn = await steam.LoginWithPassword(username, password, remember, guard, ct);
                    }
                    else signIn = await steam.LoginWithQr(remember, url => Send("qr", url), ct);
                    // Keep the token only when asked to; a login without "remember" also removes an older saved one.
                    if (remember) loginStore.Save(signIn); else loginStore.Delete();
                    Send("connected", true);
                }
                catch { steam?.Dispose(); steam = null; Send("connected", false); throw; }
                finally { operation.Dispose(); operation = null; operationKind = null; Send("guard", null); }
                return;
            }
            if (type == "scan")
            {
                // Signed in: download the manifests from Steam. Not signed in: only the manifests the Steam client keeps locally.
                var online = steam?.IsLoggedOn == true;
                if (!online && steamRoot == null) throw new InvalidOperationException("尚未读取 Steam 游戏库。");
                var selected = games.Single(g => g.AppId == message.GetProperty("appId").GetUInt32());
                var game = Library.ReadGame(selected.AcfPath);
                var mode = message.TryGetProperty("mode", out var m) && m.GetString() == "full" ? VerifyMode.Full : VerifyMode.Quick;
                if (game.Updating) throw new IOException("Steam 正在更新或安装未完成，请稍后重试。");
                operation = new CancellationTokenSource(); report = null;
                operationKind = "scan"; activeAppId = game.AppId;
                currentProgress = new("准备检测", 0, 0, "固定已安装版本快照");
                var version = ++operationVersion;
                SendState();
                var ct = operation.Token;
                var progress = new Progress<ScanProgress>(p =>
                {
                    if (operationKind != "scan" || version != operationVersion) return;
                    currentProgress = p; Send("progress", p);
                });
                try
                {
                    var result = await Task.Run(async () => { var snapshot = online ? await steam!.Resolve(game, progress, ct) : LocalManifests.Resolve(game, steamRoot!, progress, ct); return await Verifier.Run(game, snapshot, progress, ct, mode); }, ct);
                    report = result; reportGame = game; Send("report", report);
                }
                finally { operation.Dispose(); operation = null; operationKind = null; currentProgress = null; }
            }
        }
        catch (OperationCanceledException) { Send("cancelled", "任务已取消，未生成完整报告。"); }
        catch (Exception ex) { Send("error", ex.Message); }
        finally { SendState(); }
    }
    /// <summary>
    /// Moves user-selected extra files of the current report to the Recycle Bin, after host-side validation and a native confirmation.
    /// The page only names paths; which files qualify is decided here from the stored report.
    /// </summary>
    private async Task DeleteExtra(JsonElement message)
    {
        if (report == null || reportGame == null) throw new InvalidOperationException("暂无报告，请先检测。");
        var current = Library.ReadGame(reportGame.AcfPath);
        if (current.Updating) throw new IOException("Steam 正在更新或安装未完成，请稍后再删除。");
        if (current.AcfHash != reportGame.AcfHash) throw new IOException("检测后 Steam 安装记录已变化（例如游戏更新或启动过），请重新检测后再删除。");
        var paths = message.GetProperty("paths").EnumerateArray().Select(p => p.GetString() ?? "").ToList();
        var plan = ExtraFileCleaner.Plan(reportGame, report, paths);
        if (plan.Files.Count == 0)
        {
            Send("notice", plan.Skipped.Count > 0 ? $"没有可删除的文件。{plan.Skipped[0].Relative}：{plan.Skipped[0].Reason}" : "未选择文件。");
            return;
        }
        var text = $"将 {plan.Files.Count:N0} 个额外文件（{FormatSize(plan.TotalSize)}）从\n{reportGame.Path}\n移到回收站。\n\n"
            + "“额外”只表示不在官方清单中。其中可能有游戏配置、存档、缓存、启动器或反作弊组件，删除后游戏可能需要重新生成文件或通过 Steam 修复。\n\n"
            + (plan.Skipped.Count > 0 ? $"另有 {plan.Skipped.Count} 个文件不符合删除条件，将保留。\n\n" : "")
            + "文件可从回收站恢复；如果某些文件过大无法放入回收站，Windows 会先询问是否永久删除。\n\n是否继续？";
        if (MessageBox.Show(this, text, "删除额外文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            Send("notice", "已取消删除，文件未改动。");
            return;
        }
        operation = new CancellationTokenSource(); operationKind = "delete";
        currentProgress = new("移到回收站", 0, plan.Files.Count, $"{plan.Files.Count:N0} 个文件");
        SendState();
        try
        {
            await RecycleBin.SendAsync(Handle, plan.Files.Select(f => f.FullPath).ToList());
            var deleted = plan.Files.Where(f => !File.Exists(f.FullPath)).Select(f => f.Relative).ToList();
            report = ExtraFileCleaner.WithoutDeleted(report, deleted);
            var kept = plan.Files.Count - deleted.Count;
            var summary = $"已将 {deleted.Count:N0} 个额外文件移到回收站。";
            if (kept > 0) summary += $" {kept:N0} 个未删除（可能正在被占用或操作被取消）。";
            if (plan.Skipped.Count > 0) summary += $" {plan.Skipped.Count:N0} 个不符合条件已保留，例如 {plan.Skipped[0].Relative}：{plan.Skipped[0].Reason}。";
            Send("report", report);
            Send("notice", summary);
        }
        finally { operation.Dispose(); operation = null; operationKind = null; currentProgress = null; }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>
    /// The Steam client keeps every library game's artwork in steam/appcache/librarycache. That folder is mapped read-only
    /// to https://steam-art.local/, and the page is told each game's hero and icon file (found by <see cref="SteamArtwork"/>).
    /// Nothing is downloaded.
    /// </summary>
    private bool MapArtwork(string steamRoot)
    {
        var remapped = MapArtworkFolder(steamRoot);
        artwork = [];
        if (mappedArtFolder == null) return remapped;
        var cache = mappedArtFolder;
        string? Url(string? file) => file == null ? null
            : string.Join('/', Path.GetRelativePath(cache, file).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        foreach (var game in games)
            artwork[game.AppId.ToString()] = new
            {
                hero = Url(SteamArtwork.Find(steamRoot, game.AppId, SteamArtwork.Kind.Hero)),
                icon = Url(SteamArtwork.Find(steamRoot, game.AppId, SteamArtwork.Kind.Icon)),
            };
        return remapped;
    }

    private string? mappedArtFolder;
    /// <summary>Maps steam/appcache/librarycache read-only to https://steam-art.local/. Returns true when the mapping changed.</summary>
    private bool MapArtworkFolder(string steamRoot)
    {
        var cache = Path.GetFullPath(Path.Combine(steamRoot, "appcache", "librarycache"));
        if (!Directory.Exists(cache) || string.Equals(cache, mappedArtFolder, StringComparison.OrdinalIgnoreCase)) return false;
        web.CoreWebView2.SetVirtualHostNameToFolderMapping("steam-art.local", cache, CoreWebView2HostResourceAccessKind.DenyCors);
        mappedArtFolder = cache;
        return true;
    }

    /// <summary>Signs in with the remembered token. A token Steam rejects is deleted; a network failure keeps it for next time.</summary>
    private async Task LoginWithSaved()
    {
        if (loginStore.Load() is not { } saved) return;
        operation = new CancellationTokenSource(TimeSpan.FromMinutes(1)); operationKind = "login";
        SendState();
        try
        {
            steam = new SteamSession();
            await steam.LoginWithToken(saved, operation.Token);
            Send("connected", true);
        }
        catch (Exception ex)
        {
            var rejected = steam?.LogOnResult is { } result && result != SteamKit2.EResult.OK;
            steam?.Dispose(); steam = null;
            if (rejected) { loginStore.Delete(); Send("notice", "保存的登录已失效，请重新登录。"); }
            else Send("notice", $"自动登录失败：{ex.Message}");
        }
        finally { operation.Dispose(); operation = null; operationKind = null; SendState(); }
    }

    private void SendState() => Send("state", new
    {
        busy = operation != null, operationKind, activeAppId,
        progress = currentProgress, connected = steam?.IsLoggedOn == true, account = steam?.IsLoggedOn == true ? steam.AccountName : null, remembered = loginStore.Exists, maximized = WindowState == FormWindowState.Maximized,
        games, report, artwork
    });
}
