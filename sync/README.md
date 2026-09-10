# WriteME 自建同步服务

同步服务由 ASP.NET Core + SQLite 构成，Docker 中的 `/data` 保存账号、版本前沿和附件。客户端离线编辑，在连接后交换变化；并发编辑保留整篇文档的多个版本，用户选择后继续。

## 使用 Docker Compose

需要 Docker Engine 和 Compose 插件。从仓库根目录操作：

```sh
cp sync/.env.example sync/.env
chmod 600 sync/.env
```

Windows PowerShell 可以用 `Copy-Item sync/.env.example sync/.env`。编辑 `sync/.env`，填写自己的 `WRITEME_SETUP_USER` 和至少 12 个字符的独立密码；不要复用示例密码。`.env` 已被 Git 和 Docker 构建上下文排除。

`WRITEME_SYNC_PORT` 默认为 `8787`，已有服务占用时可以改成其他空闲端口。Compose 默认绑定宿主 `127.0.0.1`，供同机反向代理或 SSH 隧道连接。

```sh
docker compose -f sync/compose.yaml --env-file sync/.env config --quiet
docker compose -f sync/compose.yaml --env-file sync/.env up -d --build
docker compose -f sync/compose.yaml --env-file sync/.env ps
curl http://127.0.0.1:8787/health
```

健康检查返回 `{"status":"ok","protocol":1}`。若修改端口，也相应修改检查地址。镜像使用非 root 的 `app` 用户；命名卷保留数据库与附件，正常升级保留该卷。

初始化变量只在数据库没有账号时创建首个账号，修改 `.env` 不会重置已有密码。当前没有账号恢复或密码重置界面。容器环境中仍有启动配置，因此限制服务器及 `.env` 的访问权限。

Linux 主机若能访问 NuGet，而默认构建网络无法解析或连接，可以仅对构建阶段使用宿主网络：

```sh
docker compose -f sync/compose.yaml -f sync/compose.host-build.yaml --env-file sync/.env up -d --build
```

该覆盖不改变运行服务的网络和回环端口绑定。镜像拉取仍使用 Docker 守护进程的网络/代理；需要先确认它能访问 `mcr.microsoft.com`。依赖恢复保留锁文件验证与 NuGet 安全检查，不通过关闭校验来绕过网络问题。

## 客户端连接与 HTTPS

原生程序打开“设置 → 同步”，填写地址、账号及密码，点击“登录并同步此资料库”。默认每 30 秒交换一次，顶栏也可以立即同步。登录把当前资料库的所有空间、文档与附件和该账号合并；需要独立账号时使用不同的 `--data-dir`。

非回环服务器必须使用 HTTPS。可以在宿主机的 Caddy 中配置，例如：

```caddyfile
notes.example.com {
    request_body {
        max_size 64MB
    }
    reverse_proxy 127.0.0.1:8787
}
```

将示例域名替换成自己解析到服务器的域名，并确保代理能取得可信证书。Nginx/OpenResty 使用相同的上游，上传体积上限至少 50 MiB；代理若运行在另一容器，`127.0.0.1` 指向代理自己，需要给两者配置专用网络或宿主可达地址。不要把登录地址配置成会跳转到另一主机的 URL，客户端不跟随重定向。

没有域名时可以先使用 SSH 隧道；在运行客户端的电脑上保持以下命令运行：

```sh
ssh -N -L 18789:127.0.0.1:8787 -p 22 user@server
```

客户端地址填写 `http://127.0.0.1:18789`。如果服务端端口也改为 18789，将命令右侧的 `8787` 改为 `18789`。SSH 负责远程链路加密，应用的回环 HTTP 限制保持不变。

## 账号与登录状态

新增账号从标准输入读取一行密码，不把密码放进命令参数：

```sh
docker compose -f sync/compose.yaml --env-file sync/.env exec writeme-sync dotnet WriteME.SyncServer.dll --add-user second-user
```

输入至少 12 个字符的密码并回车。每个账号的数据和附件独立，登录名不区分大小写。现有账号不会被同名创建覆盖。

密码用独立盐和 PBKDF2-SHA256 保存，会话令牌只存哈希，30 天过期。客户端退出登录会清除本机凭据并尝试撤销服务端会话；服务器不可达时旧会话自然过期。Windows 客户端使用当前用户 DPAPI 保护令牌，不保存登录密码；其他系统当前不持久化令牌。

服务器可以读取同步正文和附件，当前没有端到端加密。资料库 ZIP 导出不包含登录凭据或服务端账号。

## 冲突和恢复

- 离线设备各自修改同一文档后，双方版本都保留。进入“查看冲突”，可先另存副本，再选择要继续编辑的版本。
- 页面样式、收藏和位置属于文档版本，因此不同设备只改不同字段也可能冲突；当前不是逐字符或逐块实时协作。
- 删除与编辑并发时不会只凭时间戳丢弃正文。文件夹循环被确定性地拆开，同日新建碰撞保留双方笔记。
- 接收远端正文前，客户端会保存本机草稿并保留原文的本地备份。仅更新元信息时保留现有编辑历史。
- 断网、取消或附件失败保留待处理状态，可在恢复网络后重试。迁移/恢复服务端后，让客户端退出并重新登录，以重置游标并重新比较完整前沿。

不要把一个已经配置同步的活跃本机数据库直接复制成第二台设备。第二台设备使用空资料库登录下载；资料迁移可使用 ZIP 导入副本，内部链接会重新映射。

## 备份与升级

服务端备份必须包含整个 `/data`，包含 `sync.db`、其 WAL 状态与 `assets/`。以下停机复制方式不依赖数据库工具；选择一个新的备份目录：

```sh
mkdir backup-writeme-sync
docker compose -f sync/compose.yaml --env-file sync/.env stop writeme-sync
docker compose -f sync/compose.yaml --env-file sync/.env cp writeme-sync:/data/. ./backup-writeme-sync/
docker compose -f sync/compose.yaml --env-file sync/.env start writeme-sync
```

需要在线备份时必须使用 SQLite 在线备份接口，不能直接复制正在写入的主数据库文件。备份目录包含私有笔记，应按资料本身管理。

升级前先备份，然后在同一 Compose 项目下更新代码并重建：

```sh
docker compose -f sync/compose.yaml --env-file sync/.env up -d --build
docker compose -f sync/compose.yaml --env-file sync/.env logs --tail 60 writeme-sync
curl http://127.0.0.1:8787/health
```

恢复时先停止服务，把备份完整复制到新建的空数据卷，确保 `app` 用户拥有该目录，再启动并验证。保留原卷直到恢复检查完成；`docker compose down -v` 会删除资料卷，不用于普通升级。

## 运行范围与验证

| 项目 | 当前范围 |
|---|---|
| 单附件 | 50 MiB，SHA-256 内容校验 |
| 同步请求/响应 | 24 MiB，上传最多 100 个对象，分页交换 |
| 单对象并发内容 | payload 合计 16 MiB、最多 32 个版本 |
| 版本向量 | 每个版本最多 256 个设备 |
| 登录限流 | 每个直接连接地址每分钟 12 次；经代理时可能共享来源地址 |
| 本机数据 | SQLite WAL、串行事务、统一编辑历史 |
| 服务端拓扑 | 单实例 SQLite，持久卷；不支持多实例共享库 |

`npm run native:test` 的 153 项回归包括真实 HTTP 主机上的登录、账号隔离、附件、并发修改、冲突选择、分页、重启和取消。

本轮另在 Linux ARM64、Docker 29.7.1 / Compose 5.3.1 完成镜像构建和非 root 容器运行。两个 Windows C# 客户端经 SSH 隧道验证文档、文件夹、外观、封面/附件、双版本冲突和选择、账号隔离及会话撤销；强制重建容器后，第三份空资料库恢复全部验证数据。构建使用 `compose.host-build.yaml`，运行服务仍只绑定回环端口。结果记录在本机 `artifacts/native/qa-remote-sync/`，不包含登录凭据。

公网 HTTPS 代理与证书部署未包含在这次隧道验收中。

同步前沿、墓碑及未引用附件当前没有自动垃圾回收；大资料库性能、跨实例高可用和公网长期运行仍需要相应负载与运维验证。协议机制及取舍见 [M6 决策记录](../.agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md)。
