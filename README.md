# Codex Quota Local

一个极简 Windows 悬浮窗，用来显示 Codex 剩余额度，并可选显示 public reset radar 预测。

它的设计目标不是功能最全，而是安全边界清楚、源码容易审计、本地日志优先。

## 功能

- 显示 Codex 5 小时额度窗口剩余百分比和自然重置时间。
- 显示 Codex weekly 额度窗口剩余百分比和自然重置时间。
- 可选显示 Codex credits 余额。
- 可选显示多来源 reset radar：未来 24 小时出现额外 reset-like 事件的多个公开预测/信号值。
- 悬浮窗跟随 ChatGPT/Codex 桌面窗口。
- 系统托盘菜单提供手动刷新和退出。
- 附带 CLI 快照模式，方便排查和自动化。
- 默认先从本地日志读取额度；如果日志缺失、超过 60 秒或重置时间已过，再读取 Codex 登录态并请求 OpenAI usage endpoint。
- 支持严格离线模式 `--offline-only`，只读本地日志，不读取 `auth.json`，不联网。
- 托盘右键可直接切换 quota mode、开关 reset radar、修改 quota/radar 刷新频率。
- 默认额度每 10 秒刷新一次，radar 每 10 分钟刷新一次。
- 可选 `--follow-codex` watcher：Codex/ChatGPT 桌面端打开时启动悬浮窗，关闭时退出悬浮窗。

示例显示：

```text
5h 56% -> 15:57 | W 88% -> 9/15 12:22 | Credits 769.65 | Radar 24h 25%/67%/86%
```

其中：

- `5h 56% -> 15:57` 表示 5 小时窗口剩余 56%，今天 15:57 自然重置。
- `W 88% -> 9/15 12:22` 表示 weekly 窗口剩余 88%，9 月 15 日 12:22 自然重置。
- `Credits 769.65` 表示 Codex usage 响应中的 credits balance，保留两位小数。它不是 5 小时或 weekly 额度百分比。
- `Radar 24h 25%/67%/86%` 表示多个公开 reset radar 来源对未来 24 小时的预测/信号读数，顺序是 `oracle / signal / watch`。如果某个来源暂时不可用，对应位置会显示 `--`。这些数值口径不同，适合并列参考，不适合取平均。它不是你个人额度窗口的自然重置时间。

## 托盘菜单

右键系统托盘图标可以直接调整运行状态，不需要重启：

- `Quota mode`：切换 `Auto: logs, then live`、`Offline only`、`Live first`。
- `Reset radar`：开关 public reset radar。
- `Credit balance`：开关 credits 余额显示。开启后会读取 Codex 登录态并请求官方 usage endpoint；严格离线模式下不可用。
- `Quota refresh`：切换 5 秒、10 秒、30 秒、1 分钟。
- `Radar refresh`：切换 1 分钟、5 分钟、10 分钟、30 分钟。

切换 quota mode、打开 radar、修改刷新频率后，程序会立即触发一次刷新。

## 跟随 Codex 开关

如果希望悬浮窗跟随 Codex/ChatGPT 桌面端打开和关闭，可以运行：

```powershell
.\CodexQuotaLocal.exe --follow-codex --radar --balance
```

或者双击全功能入口（跟随 + Radar + Credits）：

```text
Run-Follow-Codex.cmd
```

说明：

- `--follow-codex` 会启动一个轻量 watcher。ChatGPT/Codex 桌面进程存在时，它启动悬浮窗；桌面进程关闭后，它关闭悬浮窗。
- 随包提供的 `Run-Follow-Codex.cmd` 默认附加 `--radar --balance`；如果不需要其中某项，可直接使用自定义命令行。
- watcher 本身需要保持运行，才能感知下一次 Codex 打开。
- watcher 会显示一个系统托盘图标，右键 `Exit follower` 可以退出 watcher，并关闭它启动的悬浮窗。
- 这个模式不写开机启动项、不创建服务、不写注册表。如果希望开机自动跟随，可以自行把 `Run-Follow-Codex.cmd` 放进 Windows 启动文件夹。
- 如果只想让当前悬浮窗在 Codex 关闭后自动退出，可以运行 `.\CodexQuotaLocal.exe --radar --exit-with-codex`。

## 和现有项目的差异

目前已有一些优秀项目覆盖了相邻需求，例如：

- `zoeyliew192/codex-usage-remaining`：侧重 Codex 剩余额度悬浮显示。
- `whmc76/codex-reset-radar`：侧重浏览器扩展形式的 reset radar、quota、通知和建议。
- `Ronanism/codex-radar`：侧重 Windows dashboard/floating monitor，会读取 Codex auth 并请求 usage endpoint。
- `VictorZakharov/codex-usage`：侧重 tray 监控、历史记录和本地使用趋势。

Codex Quota Local 的取舍：

- 默认 auto：先只读 `~/.codex/logs_2.sqlite`；如果本地日志没有可用的新鲜额度记录，再读取 `auth.json` 并请求 OpenAI usage endpoint。
- 严格离线：`--offline-only` 只读本地日志，不读 `auth.json`，不联网。
- Radar 独立开关：只有 `--radar` 才访问公开 radar endpoints，且不发送任何 Codex token。
- Live-first 独立开关：`--live` 会优先读取 usage endpoint，再回退到本地日志。
- Credits 独立开关：`--balance` 才会持续从 usage endpoint 获取余额；数值不写入磁盘。
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
- 如果本地日志没有 quota data、记录超过 60 秒或重置时间已过，再读取 `~/.codex/auth.json` 中的 Codex access token 和 account id。
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

- 请求 `https://codex-reset.com/api/forecast`、`https://codexreset.app/api/signal` 和 `https://savemetibo.com/status.json`。
- 请求不带 Authorization header，不带 Codex account id，不带本地日志内容。

Live-first 运行：

```powershell
.\CodexQuotaLocal.exe --live
```

行为：

- 先读取 `~/.codex/auth.json` 中的 Codex access token 和 account id。
- 优先请求 `https://chatgpt.com/backend-api/wham/usage`。
- 如果 live 请求失败，只回退到仍然有效的本地日志；不会继续展示过期百分比。
- token 只在内存中使用，不写入磁盘，不写入日志。

Credits 余额运行：

```powershell
.\CodexQuotaLocal.exe --balance
```

行为：

- 从同一个 `https://chatgpt.com/backend-api/wham/usage` 响应读取 credits balance。
- 悬浮窗显示 `Credits 余额`；无限额度显示 `Credits unlimited`，字段不可用显示 `Credits --`。
- 开启余额时会优先使用 live quota 和余额；请求失败后可回退到新鲜本地额度，但余额显示不可用。
- 不保存余额历史，不向第三方 radar 来源发送余额或 Codex 凭据。

## 下载后直接使用

从 GitHub Release 下载最新的 portable zip，解压后运行：

```powershell
.\CodexQuotaLocal.exe
```

如果希望悬浮窗跟随 Codex/ChatGPT 桌面端打开和关闭，并同时显示 Radar 和 Credits，可以双击：

```text
Run-Follow-Codex.cmd
```

严格离线运行：

```powershell
.\CodexQuotaLocal.exe --offline-only
```

如果希望显示 reset radar，并保留默认 auto quota 读取：

```powershell
.\CodexQuotaLocal.exe --radar
```

刷新频率可以用命令行调节，不增加悬浮窗 UI 复杂度：

```powershell
.\CodexQuotaLocal.exe --radar --quota-interval-seconds 10 --radar-interval-minutes 10
```

余额和 Radar 可以组合：

```powershell
.\CodexQuotaLocal.exe --balance --radar
```

说明：

- `--quota-interval-seconds` 控制本地额度刷新间隔，默认 `10` 秒，最小 `2` 秒。
- 默认 auto 模式下，日志失效时也会按额度刷新间隔查询官方接口；每次重新读取旧日志不会延长记录的有效期。
- `--radar-interval-minutes` 控制 radar 刷新间隔，默认 `10` 分钟，最小 `1` 分钟。
- 托盘菜单里的 `Refresh now` 会立即刷新额度和 radar。

如果只是想看一次命令行快照：

```powershell
.\CodexQuotaLocalCli.exe --snapshot
.\CodexQuotaLocalCli.exe --snapshot --radar
.\CodexQuotaLocalCli.exe --snapshot --balance
```

## 从源码构建

在项目目录运行：

```powershell
.\build.ps1
```

构建产物：

- `CodexQuotaLocal.exe`：GUI 悬浮窗版本。
- `CodexQuotaLocalCli.exe`：命令行快照版本。
- `Run-Follow-Codex.cmd`：auto quota + credits + radar + 跟随 Codex/ChatGPT 打开关闭的全功能入口。
- `dist/CodexQuotaLocal-v0.4.0-win-x64-portable.zip`：可上传到 GitHub Release 的 portable 包。

运行 `./test.ps1` 可执行额度读取回归测试。测试使用临时合成日志，不读取真实登录信息，也不联网。

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

- 默认 auto 模式优先依赖 Codex 本地日志；日志缺失、格式变化、超过 60 秒或任一窗口重置时间已过时会回退到 live usage endpoint。60 秒内的本地读数仍可能滞后；需要优先查询官方接口时可使用 `--live`。
- `--offline-only` 模式无法主动获取新额度。日志失效时显示 `Quota: -- (stale/unavailable)`，CLI 返回 `NO_DATA`；托盘会显示诊断原因。不会假定额度已经恢复到 100%。
- 日志中的相对重置时间按日志产生时间计算，CLI 的 `observed_at` 可用于核对数据时间。
- Codex 本地日志格式和 usage endpoint 都不是稳定公开 API，未来 Codex 更新可能导致解析失效。
- OpenAI 将 credits 定义为符合条件的超额使用所消耗的计量单位；当前 usage endpoint 的响应结构并非稳定公开 API 合约。本工具因此显示 `Credits` 数量，不添加货币符号。
- Reset radar 是第三方公开预测，不是 OpenAI 官方承诺。
- 默认 radar 轮询频率是 10 分钟；如果请求失败，overlay 会 60 秒后重试。
- Windows 可执行文件如果未签名，下载后可能触发 SmartScreen 提示。

## 仓库维护

- `CHANGELOG.md` 是唯一的版本历史文件，不为每个版本保留重复的 release notes。
- GitHub Release 说明由 tag 的提交记录自动生成。
- 发布包只保留两个可执行文件、一个全功能双击入口和必要文档；其他模式通过托盘菜单或命令行参数启用。

## 许可证

MIT
