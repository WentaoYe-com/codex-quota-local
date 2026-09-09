# Codex Quota Local

一个极简 Windows 悬浮窗，用来显示 Codex 剩余额度，并可选显示 public reset radar 预测。

它的设计目标不是功能最全，而是安全边界清楚、源码容易审计、默认不碰敏感凭据。

## 功能

- 显示 Codex 5 小时额度窗口剩余百分比和自然重置时间。
- 显示 Codex weekly 额度窗口剩余百分比和自然重置时间。
- 可选显示 reset radar：未来 24/48 小时出现额外 reset-like 事件的公开预测概率。
- 悬浮窗跟随 ChatGPT/Codex 桌面窗口。
- 系统托盘菜单提供手动刷新和退出。
- 附带 CLI 快照模式，方便排查和自动化。
- 默认本地额度每 10 秒刷新一次，radar 每 10 分钟刷新一次。

示例显示：

```text
5h 56% -> 15:57 | W 88% -> 9/15 12:22 | Radar 24h 25% / 48h 45%
```

其中：

- `5h 56% -> 15:57` 表示 5 小时窗口剩余 56%，今天 15:57 自然重置。
- `W 88% -> 9/15 12:22` 表示 weekly 窗口剩余 88%，9 月 15 日 12:22 自然重置。
- `Radar 24h 25% / 48h 45%` 表示 public reset radar 预测未来 24/48 小时内出现额外 reset-like 事件的概率。它不是你个人额度窗口的自然重置时间。

## 和现有项目的差异

目前已有一些优秀项目覆盖了相邻需求，例如：

- `zoeyliew192/codex-usage-remaining`：侧重 Codex 剩余额度悬浮显示。
- `whmc76/codex-reset-radar`：侧重浏览器扩展形式的 reset radar、quota、通知和建议。
- `Ronanism/codex-radar`：侧重 Windows dashboard/floating monitor，会读取 Codex auth 并请求 usage endpoint。
- `VictorZakharov/codex-usage`：侧重 tray 监控、历史记录和本地使用趋势。

Codex Quota Local 的取舍：

- 默认离线：只读 `~/.codex/logs_2.sqlite`，不读 `auth.json`，不联网。
- Radar 独立开关：只有 `--radar` 才访问 `https://codex-reset.com/api/forecast`，且不发送任何 Codex token。
- Live 独立开关：只有 `--live` 才读取 `auth.json` 并请求 OpenAI usage endpoint。
- 无依赖：Windows 自带 .NET Framework 编译器即可构建，不使用 npm、pip、Electron 或第三方 SDK。
- 无持久化：不保存凭据、不保存历史、不写启动项、不写注册表。
- 源码短：核心逻辑集中在一个 C# 文件，便于逐行审计。

## 安全模型

默认运行：

```powershell
.\CodexQuotaLocal.exe
```

行为：

- 读取 `~/.codex/logs_2.sqlite`，SQLite read-only 打开。
- 解析 Codex 已经写入本地日志的 rate-limit header。
- 不读取 `~/.codex/auth.json`。
- 不发起网络请求。
- 不创建开机启动项。
- 不保存额度历史。

Radar 运行：

```powershell
.\CodexQuotaLocal.exe --radar
```

额外行为：

- 请求 `https://codex-reset.com/api/forecast`。
- 请求不带 Authorization header，不带 Codex account id，不带本地日志内容。

Live 运行：

```powershell
.\CodexQuotaLocal.exe --live
```

额外行为：

- 读取 `~/.codex/auth.json` 中的 Codex access token 和 account id。
- 请求 `https://chatgpt.com/backend-api/wham/usage`。
- token 只在内存中使用，不写入磁盘，不写入日志。

## 下载后直接使用

从 GitHub Release 下载 `CodexQuotaLocal-v0.2.0-win-x64-portable.zip`，解压后运行：

```powershell
.\CodexQuotaLocal.exe
```

也可以直接双击：

```text
Run-Offline.cmd
Run-With-Radar.cmd
```

如果希望显示 reset radar：

```powershell
.\CodexQuotaLocal.exe --radar
```

刷新频率可以用命令行调节，不增加悬浮窗 UI 复杂度：

```powershell
.\CodexQuotaLocal.exe --radar --quota-interval-seconds 10 --radar-interval-minutes 10
```

说明：

- `--quota-interval-seconds` 控制本地额度刷新间隔，默认 `10` 秒，最小 `2` 秒。
- `--radar-interval-minutes` 控制 radar 刷新间隔，默认 `10` 分钟，最小 `1` 分钟。
- 托盘菜单里的 `Refresh now` 会立即刷新额度和 radar。

如果只是想看一次命令行快照：

```powershell
.\CodexQuotaLocalCli.exe --snapshot
.\CodexQuotaLocalCli.exe --snapshot --radar
```

## 从源码构建

在项目目录运行：

```powershell
.\build.ps1
```

构建产物：

- `CodexQuotaLocal.exe`：GUI 悬浮窗版本。
- `CodexQuotaLocalCli.exe`：命令行快照版本。
- `Run-Offline.cmd` / `Run-With-Radar.cmd`：双击启动脚本。
- `Snapshot-Offline.cmd` / `Snapshot-With-Radar.cmd`：双击查看一次 CLI 快照。
- `dist/CodexQuotaLocal-v0.2.0-win-x64-portable.zip`：可上传到 GitHub Release 的 portable 包。

## 环境变量

默认读取：

```text
%USERPROFILE%\.codex
```

也可以用环境变量覆盖：

```powershell
$env:CODEX_HOME="D:\path\to\.codex"
```

测试隔离数据时可使用优先级更高的：

```powershell
$env:CODEX_QUOTA_DATA_DIR="D:\path\to\fixture-codex-home"
```

## 已知限制

- Offline 模式依赖 Codex 本地日志，因此可能比实时 usage endpoint 稍有滞后。
- Codex 本地日志格式和 usage endpoint 都不是稳定公开 API，未来 Codex 更新可能导致解析失效。
- Reset radar 是第三方公开预测，不是 OpenAI 官方承诺。
- 默认 radar 轮询频率是 10 分钟；如果请求失败，overlay 会 60 秒后重试。
- Windows 可执行文件如果未签名，下载后可能触发 SmartScreen 提示。

## 许可证

MIT
