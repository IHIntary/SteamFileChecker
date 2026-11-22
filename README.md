# Steam File Checker

❤ 来自 Claude Code and Codex

找出游戏目录里**不在 Steam 官方清单中的文件**。

Steam 自带的“验证游戏文件的完整性”只检查清单里的文件，不会处理多出来的文件。卸载游戏后残留的 MOD、旧版本留下的资源包、MOD 加载器的 DLL 等，都可能让重新安装或更新后的游戏报错。本程序把这些文件列出来，由你决定如何处理。

- 自动读取本机 Steam 游戏库，按已安装的版本（Manifest）获取官方清单，包含已安装的 DLC
- 额外文件按文件夹归组：整个不在清单中的文件夹（例如 MOD 文件夹）显示为一项，可整体勾选
- 只标注可核实的事实，不做风险判断：文件类型、修改时间是否早于 Steam 记录的最近一次安装或更新、是否与游戏主程序同目录
- 两种校验方式：**快速**（只比对文件名和大小，几秒完成）和**完整**（逐个计算 SHA-1）
- 选中的文件在确认后移到回收站；可导出 JSON 报告
- 缺失或内容不同的文件会一并列出，修复请使用 Steam 自带的“验证游戏文件的完整性”

## 系统要求

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已自带）
- 已安装 Steam 客户端；在线获取付费游戏和 DLC 的清单时，需要用拥有该游戏的账号登录

## 使用

1. 启动后自动读取 Steam 游戏库；找不到时可手动选择 Steam 根目录（包含 `steamapps` 的目录）。
2. （可选）登录 Steam：扫码登录，或账号密码登录（支持手机确认、Steam 令牌和邮箱验证码）。勾选“记住登录”时，登录凭据用 Windows DPAPI 加密保存在本机 `%LOCALAPPDATA%\SteamFileChecker\login.dat`，下次启动自动登录；不保存密码，退出登录即删除。
3. 选择游戏和校验方式，点击“开始检测”。
   - **未登录**：免登录，仅读取本机 Steam 客户端保存的清单（`steam\depotcache`）。本机没有某个版本的清单时，该部分标记为未获取。
   - **已登录**：从 Steam 在线获取官方清单。
4. 查看结果，按需导出报告或处理额外文件。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 Node.js 22 及以上版本。

```powershell
./build.ps1 -Run
```

脚本会安装前端依赖、运行前端和核心测试、构建界面并发布桌面程序到 `artifacts/app`。可用 `-OutputDirectory` 指定其他输出目录。如果项目目录中存在 `.tools/dotnet/dotnet.exe`，脚本优先使用它。

项目结构：

| 目录 | 内容 |
| --- | --- |
| `src/SteamFileChecker.Core` | 游戏库与安装记录解析、Steam 清单获取、文件比对、额外文件删除检查 |
| `src/SteamFileChecker.Desktop` | WinForms + WebView2 桌面宿主、自绘标题栏 |
| `ui` | React + TypeScript 界面 |
| `tests` | 核心逻辑测试（`dotnet run --project tests/SteamFileChecker.Tests`） |

## 致谢

- [SteamKit2](https://github.com/SteamRE/SteamKit)：Steam 登录、应用信息与 Depot 清单获取
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)：桌面界面宿主
- [React](https://react.dev)、[Vite](https://vite.dev)、[TypeScript](https://www.typescriptlang.org)：界面开发与构建
- [Lucide](https://lucide.dev)：界面图标
- [node-qrcode](https://github.com/soldair/node-qrcode)：登录二维码生成
- [Vitest](https://vitest.dev)、[Testing Library](https://testing-library.com)、[jsdom](https://github.com/jsdom/jsdom)：界面测试
- [ingFY](https://github.com/Ingram5656/ingFY)：液态玻璃风格UI设计灵感提供

## 许可证

[MIT](LICENSE)。依赖的 SteamKit2 采用 LGPL-2.1 许可证，以 NuGet 包形式引用。

## 声明

**关于额外文件：** 本程序只列出不在 Steam 官方清单中的文件，并标注文件类型、修改时间等可核实的信息；不判断文件是否安全、是否影响游戏运行，也不判断是否符合游戏规则或反作弊要求。额外文件也可能是游戏自行生成的配置、存档、缓存，或游戏启动器另行下载的内容。是否删除由你自行决定，相关后果由你自行承担。

**程序做什么、不做什么：** 只读取游戏目录和本机 Steam 数据，以及在你确认后把选中的文件移到回收站；不修改游戏文件内容，不注入或附加到游戏进程，也不与反作弊程序交互。

本项目与 Valve Corporation 无关。Steam 及相关标识是 Valve Corporation 的商标。游戏横幅与图标来自用户本机 Steam 客户端的缓存，版权归各自权利人所有。
