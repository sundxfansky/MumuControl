# HTTPS 反向代理

MuMu Control 1.1.1 提供独立的远程 HTTP 入口，由代理负责 HTTPS。默认端口：本机管理 `17830`，远程 `17831`。

1. 在运行程序的电脑打开 `http://127.0.0.1:17830`，初始化账户并登录。
2. 在“偏好设置 → 远程访问与账户”启用远程 HTTP，端口设为 `17831`。
3. 将公开访问地址设为 `https://mumu.example.com` 并保存，替换成自己的实际域名。若使用非默认 HTTPS 端口，将端口一同填入。不支持路径前缀；中文域名请先转换为 Punycode。
4. 代理上游指向 `http://127.0.0.1:17831`，保留外部 Host，在该域名根路径提供页面与 API。

填写公开地址后，只接受该地址的 Host 和浏览器 Origin。HTTPS 访问使用 Secure Cookie，程序无需部署证书。**不要代理本机管理端口 `17830`**；账户初始化、改密、远程配置和启动项只能在本机管理入口完成。

## Nginx

在已经配置好域名、TLS 证书的 HTTPS `server` 中加入：

```nginx
location / {
    proxy_pass http://127.0.0.1:17831;
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_buffering off;
}
```

`$http_host` 保留非默认外部端口，例如 `mumu.example.com:8443`。应用忽略 `X-Forwarded-*` / `Forwarded`，不需要用这些头告知 HTTPS。

## Caddy

```caddyfile
mumu.example.com {
    reverse_proxy 127.0.0.1:17831 {
        header_up Host {http.request.host}
    }
}
```

Caddy 负责证书与 HTTPS；公开地址相应填写 `https://mumu.example.com`。

## 部署与排查

- 代理与程序不在同一机器时，将上游 `127.0.0.1` 改为程序所在电脑的内网地址；HTTP 上游仅在可信链路内使用，远程端口的防火墙范围限制到代理来源。
- 公开地址留空时，可以通过 `http://电脑IP:17831` 或电脑名直连；配置公开地址后，这些直连 Host 会被拒绝。修改远程设置会退出全部远程会话。
- 返回 403 时，核对公开地址、外部端口、代理传递的 Host 及浏览器 Origin。使用域名根路径，不要添加 `/mumu/` 等路径前缀。
- HTTPS 登录后仍显示未登录时，确认浏览器实际使用 HTTPS，代理没有删除 `Set-Cookie` 或缓存 `/api/`。本机与远程会话互不通用。
- 登录限速依据实际连接 IP；通过同一代理的用户共享该代理的限速预算。程序不会接受客户端提供的 IP 头来绕过限速。
- 从 1.1.0 升级时，账户与任务保持兼容，远程端口改为 HTTP；旧自签证书不再使用，代理上游也需改为 `http://`。
