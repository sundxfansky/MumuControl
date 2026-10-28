# 控制接口

默认本机地址 `http://127.0.0.1:17830`，免登录。创建远程访问账户后可以从本机启用独立远程 HTTP，默认端口 `17831`，可由用户反向代理提供 HTTPS。仅本机入口允许创建账户和管理设置；通过远程端口的回环连接也不享有本机权限。Host、实际连接地址与同源校验不信任 `Forwarded`、`X-Forwarded-*`。

先 `GET /api/bootstrap` 读取认证状态。本机入口始终 `authenticated: true`，返回本次运行有效的 `token`，不使用 Cookie；远程入口通过 `POST /api/auth/login` 登录，服务器写 HttpOnly 会话 Cookie 并返回会话 `token`。写请求（登录除外）都要附带 `X-Mumu-Token` 和 `Content-Type: application/json`；远程读取设备、截图等也必须携带有效 Cookie。错误格式为 `{ "error": "中文说明" }`，远程未登录 401，本机权限或 CSRF 失败 403，登录限速 429（`Retry-After`）。

| 方法 | 路径 | 内容 |
|---|---|---|
| GET | `/api/bootstrap` 或 `/api/auth/status` | initialized（是否已创建远程访问账户）、authenticated、local、token、远程状态；仅本机返回 publicOrigin、remoteUrls |
| POST | `/api/auth/setup` | 仅本机；创建远程访问账户 `{username,password,remoteEnabled:false}`，只允许一次 |
| POST | `/api/auth/login` | 远程入口；`{username,password}`，账户/密码错误统一提示（本机入口直接返回已认证状态） |
| POST | `/api/auth/logout` | 远程入口；`{}`，撤销当前会话 |
| PUT | `/api/auth/credentials` | 仅本机；`{username,password,currentPassword}`，更换凭据使全部远程会话失效 |
| GET/PUT | `/api/access` 或 `/api/auth/settings` | 仅本机；启用前须已创建账户；`{remoteEnabled,remotePort,publicOrigin}`，立即生效；publicOrigin 省略保留，空字符串清除 |
| GET/PUT | `/api/startup` | 仅本机已登录；读取 enabled/supported/reason，保存 `{enabled}` |

| 方法 | 路径 | 内容 |
|---|---|---|
| GET | `/api/state` | 实例、任务、设置、连接状态、进程内存 |
| GET | `/api/health` | 服务健康状态 |
| POST | `/api/refresh` | 立即刷新官方实例状态 |
| POST | `/api/emulators/{index}/start` | `{ "durationMinutes": 30 }`；可省略 durationMinutes |
| POST | `/api/emulators/{index}/stop` | `{}` |
| POST | `/api/emulators/{index}/auto-stop` | `{ "durationMinutes": 60 }`；null 取消倒计时 |
| POST | `/api/emulators/{index}/action` | `{ "action": "show" }`；show/hide/home/back |
| GET | `/api/emulators/{index}/thumbnail?size=320` | 实时画面 JPEG，经 MuMu 渲染器接口读取；size 为长边像素 64–1280，默认 320 |
| GET | `/api/emulators/{index}/capture` | 原始分辨率 PNG，用于保存画面；渲染器接口优先，失败时回退 ADB 截图 |
| GET | `/api/emulators/{index}/screenshot` | 真实 PNG，按需获取；全局最多一个抓取 |
| POST | `/api/tasks` | 创建计划 |
| PUT | `/api/tasks/{id}` | 更新计划 |
| DELETE | `/api/tasks/{id}` | 删除计划 |
| GET/PUT | `/api/settings` | 仅本机已登录，端口变更下次启动生效 |
| GET | `/api/logs` | 最多 200 条运行记录 |
| POST | `/api/apk/upload` | 请求体为 APK 原始字节，`Content-Type: application/vnd.android.package-archive` 或 `application/octet-stream`，文件名放在 URL 编码的 `X-Apk-Name`；最大 4 GB，校验 ZIP 头；返回 `{uploadId,fileName,size}`，30 分钟内未使用即失效 |
| POST | `/api/apk/install` | `{indexes:[0,1],source:"upload"|"url"|"path",uploadId?,url?,path?}`；目标须为运行中实例；`path` 仅本机；`url` 由本机下载，拒绝回环、链路本地与本机网卡地址。同一时间只运行一批，返回任务对象 |
| GET | `/api/apk/jobs` | 最近 10 个安装任务：`phase` 为 preparing/installing/done，`items[].status` 为 pending/installing/success/failed，附失败原因 |
| POST | `/api/apk/browse` | 仅本机；在本机弹出文件选择框，返回 `{path}`（取消时为 null） |

计划格式：

```json
{
  "vmIndex": 0,
  "action": "start",
  "trigger": "once",
  "at": "2026-10-21T09:00:00+08:00",
  "durationMinutes": 30,
  "enabled": true
}
```

`trigger: daily` 时 `at: HH:mm`，按运行电脑本地时区执行。单次任务使用带时区的 ISO 时间。运行记录、nextRunAt、autoStopAt 使用 ISO 时间，浏览器按本地时区显示。

设置字段：`managerPath`、`port`、`pollIntervalSeconds`（3–60）、`previewIntervalSeconds`（1–30）、`closeToTray`。

认证文件与普通状态文件分离，不通过设置或状态接口返回。远程状态不返回本机 MuMu 安装路径。会话绑定本机/远程入口和实际 socket 来源 IP；服务重启、退出登录、密码修改都会按范围撤销会话。修改远程设置撤销全部远程会话。

`publicOrigin` 接受 HTTP / HTTPS origin，例如 `https://mumu.example.com`，不允许路径、参数、用户信息或片段。非空时只接受该 origin 对应的 Host，存在 Origin 时也必须匹配它；为空时只接受本机 IP/电脑名加远程端口，Origin 必须为 `http://` 加该 Host。不开放 CORS。HTTPS 公开地址的远程会话使用 `__Host-MumuSession` Secure Cookie；HTTP 远程会话使用 `MumuRemoteSession`，本机会话使用 `MumuLocalSession`。代理必须保留外部 Host，连接远程 HTTP 端口。详见 [反向代理说明](REVERSE-PROXY.md)。

官方接口依据：[MuMuManager 文档](https://mumu.163.com/help/20240726/35047_1170006.html)。使用现行 `info -v all`、`control -v N launch/shutdown/show_window/hide_window`、`tool func`。官方 #a14 是可能废弃的旧兼容命令，未作为首选。实时预览使用 MuMu 同目录随附 ADB 对本机端口执行 `exec-out screencap -p`，是定期截图而非视频流。截图连接使用 `127.0.0.1:adb_port` 宿主转发端口；官方 `adb_host_ip` 可能返回 Android 内部地址 `10.0.2.15`，不用于宿主连接。
