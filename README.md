# Codex Quota Local

一个极简 Windows 悬浮窗，用来显示 Codex 剩余额度，并可选显示 public reset radar 预测。

它的设计目标不是功能最全，而是安全边界清楚、源码容易审计、本地日志优先。

## 功能

- 显示 Codex 5 小时额度窗口剩余百分比和自然重置时间。
- 显示 Codex weekly 额度窗口剩余百分比和自然重置时间。
- 可选显示 reset radar：未来 24/48 小时出现额外 reset-like 事件的公开预测概率。
- 悬浮窗跟随 ChatGPT/Codex 桌面窗口。
- 系统托盘菜单提供手动刷新和退出。
- 附带 CLI 快照模式，方便排查和自动化。
- 默认先从本地日志读取额度；如果日志没有 quota headers，再读取 Codex 登录态并请求 OpenAI usage endpoint。
- 支持严格离线模式 `--offline-only`，只读本地日志，不读取 `auth.json`，不联网。
- 托盘右键可直接切换 quota mode、开关 reset radar、修改 quota/radar 刷新频率。
- 默认额度每 10 秒刷新一次，radar 每 10 分钟刷新一次。

示例显示：

```text
5h 56% -> 15:57 | W 88% -> 9/15 12:22 | Radar 24h 25% / 48h 45%
```

其中：

- `5h 56% -> 15:57` 表示 5 小时窗口剩余 56%，今天 15:57 自然重置。
- `W 88% -> 9/15 12:22` 表示 weekly 窗口剩余 88%，9 月 15 日 12:22 自然重置。
- `Radar 24h 25% / 48h 45%` 表示 public reset radar 预测未来 24/48 小时内出现额外 reset-like 事件的概率。它不是你个人额度窗口的自然重置时间。

## 托盘菜单

右键系统托盘图标可以直接调整运行状态，不需要重启：

- `Quota mode`：切换 `Auto: logs, then live`、`Offline only`、`Live first`。
- `Reset radar`：开关 public reset radar。
- `Quota refresh`：切换 5 秒、10 秒、30 秒、1 分钟。
- `Radar refresh`：切换 1 分钟、5 分钟、10 分钟、30 分钟。

切换 quota mode、打开 radar、修改刷新频率后，程序会立即触发一次刷新。

## 和现有项目的差异

目前已有一些优秀项目覆盖了相邻需求，例如：

- `zoeyliew192/codex-usage-remaining`：侧重 Codex 剩余额度悬浮显示。
- `whmc76/codex-reset-radar`：侧重浏览器扩展形式的 reset radar、quota、通知和建议。
- `Ronanism/codex-radar`：侧重 Windows dashboard/floating monitor，会读取 Codex auth 并请求 usage endpoint。
- `VictorZakharov/codex-usage`：侧重 tray 监控、历史记录和本地使用趋势。

Codex Quota Local 的取舍：

- 默认 auto：先只读 `~/.codex/logs_2.sqlite`；如果本地日志没有可解析 quota，再读取 `auth.json` 并请求 OpenAI usage endpoint。
- 严格离线：`--offline-only` 只读本地日志，不读 `auth.json`，不联网。
- Radar 独立开关：只有 `--radar` 才访问 `https://codex-reset.com/api/forecast`，且不发送任何 Codex token。
- Live-first 独立开关：`--live` 会优先读取 usage endpoint，再回退到本地日志。
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
- 如果本地日志没有 quota data，再读取 `~/.codex/auth.json` 中的 Codex access token 和 account id。
- 兜底请求 `https://chatgpt.com/backend-api/wham/usage`。
- token 只在内存中使用，不写入磁盘，不写入日志。
- 不创建开机启动项。
- 不保存额度历史。

如果需要恢复旧的严格离线行为：

```powershell
.\CodexQuotaLocal.exe --offline-only
```

严格离线模式只读 `~/.codex/logs_2.sqlite`，不读取 `auth.json`，不发起网络请求。

Radar 运行：

```powershell
.\CodexQuotaLocal.exe --radar
```

额外行为：

- 请求 `https://codex-reset.com/api/forecast`。
- 请求不带 Authorization header，不带 Codex account id，不带本地日志内容。

Live-first 运行：

```powershell
.\CodexQuotaLocal.exe --live
```

行为：

- 先读取 `~/.codex/auth.json` 中的 Codex access token 和 account id。
- 优先请求 `https://chatgpt.com/backend-api/wham/usage`。
- 如果 live 请求失败，再回退到本地日志。
- token 只在内存中使用，不写入磁盘，不写入日志。

## 下载后直接使用

从 GitHub Release 下载 `CodexQuotaLocal-v0.2.2-win-x64-portable.zip`，解压后运行：

```powershell
.\CodexQuotaLocal.exe
```

也可以直接双击严格离线入口：

```text
Run-Offline.cmd
```

如果希望显示 reset radar，并保留默认 auto quota 读取，也可以双击：

```text
Run-With-Radar.cmd
```

如果希望显示 reset radar，并保留默认 auto quota 读取：

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
- `Run-Offline.cmd` / `Snapshot-Offline.cmd`：严格离线双击脚本。
- `Run-With-Radar.cmd` / `Snapshot-With-Radar.cmd`：auto quota + radar 双击脚本。
- `dist/CodexQuotaLocal-v0.2.2-win-x64-portable.zip`：可上传到 GitHub Release 的 portable 包。

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

- 默认 auto 模式优先依赖 Codex 本地日志；日志缺失或格式变化时会回退到 live usage endpoint。
- `--offline-only` 模式依赖 Codex 本地日志，因此可能比实时 usage endpoint 稍有滞后，也可能在日志没有 quota headers 时显示 `NO_DATA`。
- Codex 本地日志格式和 usage endpoint 都不是稳定公开 API，未来 Codex 更新可能导致解析失效。
- Reset radar 是第三方公开预测，不是 OpenAI 官方承诺。
- 默认 radar 轮询频率是 10 分钟；如果请求失败，overlay 会 60 秒后重试。
- Windows 可执行文件如果未签名，下载后可能触发 SmartScreen 提示。

## 许可证

MIT
