# MuMu Control

一个轻量的 MuMu 模拟器控制台。Windows 桌面窗口、系统浏览器共享同一套自适应 Web 界面；本地服务、桌面宿主、页面和 .NET 运行时打包在一个 `MuMuControl.exe` 中。

## 功能

- 显示模拟器名称、编号、当前状态、运行时长。
- 对指定模拟器启动、停止，以及启动后延时停止。
- 一键启动 30 分钟或 60 分钟，倒计时结束后自动停止。
- 创建一次性或每日定时启动、停止任务；可为定时启动附加运行时长。
- 控制台卡片实时显示每个运行中模拟器的画面缩略图（低分辨率、低频刷新），点击查看大图。
- 托盘常驻、浏览器访问、关闭窗口后继续执行任务，以及纯后台模式。
- 配置和任务保存在本机，页面提供运行记录和错误反馈。
- 账户登录、仅本机初始化与修改凭据、可选的远程 HTTP 控制与 HTTPS 反向代理。
- M 字母应用图标、可选的 Windows 登录后自动驻留托盘。

## 开始使用

1. 安装支持 `MuMuManager.exe` 官方接口的 MuMu Windows 版本。
2. 启动 `dist\MuMuControl.exe`。默认服务地址为 `http://127.0.0.1:17830/`，同时打开桌面窗口。
3. 首次访问在这台电脑上创建账户。账户名 3～32 位，密码 12～128 位并包含字母与数字；账户初始化只能通过本机 `127.0.0.1` 入口完成。
4. 在“偏好设置”中确认 `MuMuManager.exe` 路径。程序会尝试自动发现安装位置，也可以手动指定。
5. 在控制台选择模拟器，使用“启动”“停止”“运行 30 分钟”等快捷操作；“定时任务”中可以添加计划。

默认数据目录为 `%LOCALAPPDATA%\MumuControl`，其中 `state.json` 保存偏好、任务及自动停止记录。复制此目录即可备份配置。桌面页面缓存单独保存在其 `WebView2` 子目录中。

桌面窗口依赖 **Microsoft Edge WebView2 运行时**，现代 Windows 通常已经安装。如果运行时不可用，程序会显示原因并尝试打开系统浏览器。运行时可从 [Microsoft 官方页面](https://developer.microsoft.com/microsoft-edge/webview2/) 获取。无需另行安装 .NET 运行时。

默认关闭窗口后收起到系统托盘，任务继续运行。双击托盘图标可重新打开窗口；托盘菜单提供“显示”“在浏览器打开”和“退出”。也可以在“偏好设置”中关闭“关闭窗口时最小化到托盘”。退出整个程序后，计划停止执行。

### 远程访问

在本机登录后，打开“偏好设置 → 远程访问与账户”，启用远程访问并保存。程序提供独立的 **HTTP** 端口，默认 `17831`（自定义本机端口首次使用时默认加一）。公开地址留空时，可使用页面列出的本机 IP/电脑名和远程端口直接访问，以同一账户密码登录。

使用 HTTPS 反向代理时，将“公开访问地址”设为外部地址，例如 `https://mumu.example.com`，只填协议、域名及可选端口，不含路径。代理上游指向 `http://127.0.0.1:17831`，并保留外部 `Host`。配置公开地址后，远程入口只接受该地址的 Host，校验浏览器 Origin，并为 HTTPS 公开地址设置 Secure 会话 Cookie；无需在程序中配置或信任自签证书。见 [Nginx / Caddy 示例](docs/REVERSE-PROXY.md)。

本机管理入口 `http://127.0.0.1:17830` 只用于这台电脑的账户初始化和管理；**反向代理必须指向远程端口**。即使同机代理通过回环连接远程端口，也不能初始化账户、修改密码、修改服务设置或开机启动。程序忽略 `Forwarded` / `X-Forwarded-*`，不会依据这些头提升权限或改变来源 IP；经同一代理的登录限速按代理地址合并。

默认远程关闭，未初始化账户时不监听远程端口。关闭远程开关立即关闭监听；修改远程开关、端口或公开地址都会撤销已有远程会话。HTTP 上游的访问范围由用户的防火墙和代理配置控制；程序不会自动修改防火墙、路由器或系统证书信任。本机和远程均需登录；退出页面或登录过期不会中断后台定时任务。

### 跟随 Windows 启动

本机登录后，在“偏好设置 → 开机启动”中启用并保存。该开关写入当前 Windows 用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MumuControl`，无需管理员权限；登录 Windows 后静默驻留托盘，不创建 WebView2，双击托盘图标再打开界面。

启动项指向当前发布版 EXE 的完整路径；移动程序后应在新位置重新配置启动项。程序会检测启动项外部修改与 Windows 禁用状态，不会静默覆盖。默认关闭；这是用户登录后启动，不是开机前运行的系统服务。

### 纯后台模式

```powershell
.\dist\MuMuControl.exe --headless
```

纯后台模式只运行本地服务，不创建桌面窗口或 WebView2 进程。浏览器中打开 `http://127.0.0.1:17830/` 即可访问相同界面。

可选命令行参数：

| 参数 | 用途 |
| --- | --- |
| `--headless` | 只运行本地 HTTP 服务 |
| `--start-minimized` | 桌面程序直接驻留托盘，按需创建页面进程 |
| `--port 17840` | 指定本次本地端口，覆盖保存的设置 |
| `--manager "D:\MuMu\nx_main\MuMuManager.exe"` | 指定官方接口程序路径 |
| `--data-dir "D:\MuMuControlData"` | 指定配置和任务数据目录 |

例如：

```powershell
.\dist\MuMuControl.exe --headless --port 17831 --data-dir "D:\MuMuControlData"
```

此时浏览器访问 `http://127.0.0.1:17831/`。同一个数据目录应只由一个运行实例使用。

## 官方接口与实时画面

启动、停止、实例发现通过 [MuMu 官方命令行接口](https://mumu.163.com/help/20240726/35047_1170006.html#a14) 实现，程序直接执行 `MuMuManager.exe`，使用独立参数而非拼接 Shell 命令。

### 卡片实时缩略图

与 MuMu 多开器列表的缩略图类似，控制台每张运行中模拟器卡片都会显示实时画面：

- 画面来源：MuMu 安装目录随附的渲染器接口 `nx_main\sdk\external_renderer_ipc.dll`（`nemu_connect` / `nemu_capture_display`），通过共享内存直接读取渲染帧，不经过 Android 与 ADB。本机实测单帧约 10 ms，ADB `screencap` 约 500 ms。
- 服务端翻正、缩小到长边 320 px 并编码为 JPEG：`GET /api/emulators/{index}/thumbnail?size=320`。同一实例 0.7 秒内的请求共享同一帧，多个浏览器同时查看不会成倍截图。
- 刷新频率沿用“偏好设置 → 画面刷新间隔”（默认 3 秒）；只刷新屏幕上可见的卡片，切换到其他页面或浏览器隐藏时停止。
- 点击缩略图或“查看实际画面”打开大图预览（长边 1280 px）；渲染器接口不可用时自动回退到 MuMu 自带 ADB 的 `screencap` PNG 截图。

`external_renderer_ipc.dll` 是 MuMu 随附但未公开文档的接口，MuMu 升级可能改变其行为；失效时卡片保留原占位样式，大图使用 ADB 截图。当前版本不提供音频、视频推流或画面触控。如果画面获取失败，请确认实例已完成 Android 启动、MuMu 安装完整，且 ADB 连接没有被禁用。画面只在请求时读取，不录制到磁盘。远程访问时缩略图同样走登录会话校验。

实例编号沿用官方接口返回值。外部已经启动的实例会尝试从宿主进程获取起始时间；无法确定时界面显示未知，避免伪造运行时长。

## 定时行为

- 定时任务依赖 MuMu Control 持续运行，不会安装 Windows 服务或计划任务，也不会自动唤醒电脑。
- 电脑休眠期间无法执行任务。恢复后，错过时间不超过 5 分钟的任务补执行一次；超过 5 分钟则跳过并记录结果。每日任务按本机时区安排下一次执行。
- 程序退出时不会代替用户关闭所有模拟器。若需要按时停止，请让程序在后台持续运行。
- 自动停止只绑定模拟器编号，不依赖进程 PID 或运行会话：启动较慢、MuMu 更换进程、本程序重启都不会取消倒计时。到期时只要该编号的实例未处于停止状态，就会执行停止；到期前实例已经停止，倒计时随之结束。手动停止或重新设置倒计时会替换旧倒计时。
- 应用关闭、系统休眠、MuMu 未响应都会影响执行时刻；界面会展示任务结果，不能将它用作需要实时保证的计时器。

## 架构与性能

```text
MuMuControl.exe
  ├─ .NET 8 本机 HTTP 管理入口（127.0.0.1）＋可选远程 HTTP
  ├─ 账户哈希、内存会话、CSRF 与登录限速
  ├─ Core：MuMu 官方接口、状态、任务调度、原子配置存储
  ├─ 内嵌 HTML / CSS / JavaScript
  └─ Windows WinForms + WebView2 桌面窗口
                  │
                  └─ 与浏览器访问相同本地页面
```

界面不需要 Node.js、前端运行时或独立后端进程。响应式布局支持窗口缩放和窄屏浏览器；桌面默认大小为 1280 × 840，最小大小为 860 × 600，并支持 Windows 每显示器 DPI 缩放。

性能策略：

- 后台状态同步默认每 5 秒执行，避免高频运行 MuMu 命令；命令并发数和超时时间受限。
- 画面默认每 3 秒更新，按需请求；隐藏页面停止前端画面更新，避免空耗截图和网络流量。
- 关闭到托盘或最小化桌面窗口会释放 WebView2 控件，重新显示时再创建。WebView2 使用系统运行时，不在发布包中捆绑 Chromium。
- 纯后台模式没有浏览器进程；需要最小常驻占用时，建议使用此模式并按需打开浏览器。
- 图片请求和日志均有上限；配置通过临时文件替换并保留备份，避免频繁全量写入和中断损坏。

实际内存和 CPU 取决于 WebView2 版本、是否显示画面、模拟器数量及 MuMu 本身的响应。默认关闭单文件内部压缩，以减少程序集解压映像的常驻内存。自包含单文件会增大发布包，这是免装 .NET 运行时的取舍；首次运行会由 .NET 将必要的原生库解压到用户临时目录。

Web 页面可以在其他平台浏览器复用；当前可执行程序与 MuMu 官方 Windows 接口针对 Windows。后续若有 macOS / Linux 的控制接口，可以复用页面与调度逻辑并替换系统适配器和桌面宿主。

## 从源码构建

需要 Windows 与 **.NET 8 SDK**。项目可使用系统 SDK，也可将 SDK 解压到仓库的 `.tools\dotnet\`，脚本会优先使用后者。

```powershell
.\build.ps1
```

默认生成 `dist\MuMuControl.exe`，发布目标为 `win-x64`，自包含、单文件发布。首次构建需从 NuGet 获取 Microsoft WebView2 SDK 与 .NET 发布资源。

发布包约 166 MiB。可在发布时设置 `-p:EnableCompressionInSingleFile=true` 缩小到约 75 MiB，但本机对比发现其纯后台占用更高，默认优先常驻内存。

```powershell
# 指定配置和输出位置
.\build.ps1 -Configuration Release -OutputDirectory .\dist
```

目录：

```text
src/MumuControl/          桌面宿主、本地 HTTP 服务、启动入口
src/MumuControl/wwwroot/  无框架的自适应 Web 页面
src/MumuControl.Core/     官方接口适配、任务调度、状态存储
tests/                   核心行为验证
docs/API.md              本地接口说明
docs/VALIDATION.md       检查结果、内存对比与验证范围
build.ps1                单文件发布脚本
```

## 账户存储

`security/access.json` 存储账户名、随机盐和 PBKDF2-SHA256 密码哈希（600,000 次），不保存明文密码。安全目录只授予当前 Windows 用户与 SYSTEM 权限，`security/security.log` 记录有限大小的安全事件，不记录密码和会话 token。1.1.1 不再生成或加载证书；旧版本留下的 `security/certificate.dpapi` 不再使用。

会话仅在内存中，重启后重新登录；空闲 30 分钟或总时长 12 小时后过期。Cookie 为 HttpOnly、SameSite=Strict，公开地址为 HTTPS 时远程 Cookie 另带 Secure 和 `__Host-` 前缀；HTTP 直连使用独立远程 Cookie。本机与远程会话隔离，写操作需要会话 CSRF token。每个来源连续 5 次失败会暂时限速，远程还设全局失败预算；并发、连接、请求体与日志均有限额。修改账户凭据会撤销全部旧会话。

忘记密码时，必须在本机退出程序，备份数据目录后将 `security/access.json` 移至备份位置，再启动并重新初始化；远程监听在初始化前关闭。旧版账户、任务与偏好保持兼容；从旧版启用的远程 HTTPS 升级后，同一端口改为 HTTP，请同步修改代理上游。

## 测试

`tests/MumuControl.Tests` 为核心调度回归，`tests/MumuControl.SecurityTests` 为隔离 HTTP 与 HTTPS 反代认证安全检查，`tests/MumuControl.StartupTests` 为启动项隔离检查。后两者不会控制真实模拟器或修改生产启动项。

```powershell
dotnet run --project tests\MumuControl.Tests
dotnet run --project tests\MumuControl.SecurityTests
# 可选：只读访问本机已安装的 MuMu，需至少一个运行中的实例
dotnet run --project tests\MumuControl.Tests -- --live --live-thumbnails --live-screenshots
```

实机检查只读取实例列表与画面，不发送启动、停止等控制命令。

## 免责声明

本项目为第三方工具，与网易 MuMu 无关联。MuMu、MuMuManager 及其随附组件归其权利人所有，本仓库不包含也不分发这些文件，运行时使用用户本机已安装的 MuMu。
