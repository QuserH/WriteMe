# WriteME 网页、共享工作区与自建同步服务

服务由 ASP.NET Core + SQLite 构成，同一个 Docker 容器提供 `/team` 笔记网页、独立 `/admin` 账号后台、WebSocket 协作和个人 M6 同步。`/data` 保存账号、成员、共享文档、评论、个人版本前沿和附件。网页与 C# 原生程序使用同一套共享账号，个人本地库不会自动上传到共享工作区。

## 使用 Docker Compose

需要 Docker Engine 和 Compose 插件。从仓库根目录操作：

```sh
cp sync/.env.example sync/.env
chmod 600 sync/.env
```

Windows PowerShell 可以用 `Copy-Item sync/.env.example sync/.env`。编辑 `sync/.env`，填写自己的 `WRITEME_SETUP_USER` 和至少 6 个字符的独立密码；不要复用示例密码。`.env` 已被 Git 和 Docker 构建上下文排除。

`WRITEME_SYNC_PORT` 默认为 `8787`，已有服务占用时可以改成其他空闲端口。`WRITEME_SYNC_BIND` 默认 `127.0.0.1`，供同机反向代理或 SSH 隧道连接。供同一局域网使用时，显式设为 `0.0.0.0` 或服务器的局域网 IP，然后访问 `http://服务器IP:端口/team` 和 `http://服务器IP:端口/admin`。

```sh
docker compose -f sync/compose.yaml --env-file sync/.env config --quiet
docker compose -f sync/compose.yaml --env-file sync/.env up -d --build
docker compose -f sync/compose.yaml --env-file sync/.env ps
curl http://127.0.0.1:8787/health
```

健康检查返回 `{"status":"ok","protocol":1}`。若修改端口，也相应修改检查地址。镜像使用非 root 的 `app` 用户；命名卷保留数据库与附件，正常升级保留该卷。

初始化变量只在数据库没有账号时创建首个管理员，修改 `.env` 不会重置已有密码。旧 M6 库迁移时首个账号成为管理员。管理员通过 `/admin` 开通账号、停用和重置临时密码。容器环境中仍有启动配置，因此限制服务器及 `.env` 的访问权限。

生产网页资源在 Docker 的 Node 构建阶段生成，最终运行镜像只包含 ASP.NET 与构建结果。桌面端仍是 Avalonia 原生编辑器，不使用 WebView。

Linux 主机若能访问 NuGet，而默认构建网络无法解析或连接，可以仅对构建阶段使用宿主网络：

```sh
docker compose -f sync/compose.yaml -f sync/compose.host-build.yaml --env-file sync/.env up -d --build
```

该覆盖只影响构建网络；运行服务仍使用 Compose 中配置的网络和端口绑定。镜像拉取使用 Docker 守护进程的网络/代理，需要能够访问 Node 与 `mcr.microsoft.com` 官方镜像。`NODE_IMAGE` 构建参数可选择可达的官方镜像源。依赖恢复保留锁文件验证与 NuGet 安全检查。

## 独立后台与共享工作区

1. 打开 `/admin`，使用启动账号登录，首次设置自己的显示名、个人 ID 与新密码。
2. 在“账号与同步”中创建伙伴的登录账号和临时密码。伙伴在 `/team` 登录，完成自己的个人 ID 与密码设置。创建、首次设置和更改密码的最低长度均为 6 个字符。
3. 在笔记页创建工作区，用“工作区成员”按对方 ID 添加成员，分配所有者、可编辑或仅阅读权限。账号管理页面不嵌入笔记界面或 EXE。
4. 原生程序打开顶栏共享工作区入口，填写相同服务地址（不带 `/admin` 或 `/team`）、账号和密码。两台设备打开同一文档，即可共同输入和逐条回复。

“所有更改已保存”表示服务器已确认当前修订。已打开的文档断线后可继续修改，原生草稿存入独立 SQLite，网页存入 IndexedDB，恢复连接后合并。浏览器离线冷启动和首次离线打开尚未提供。权限变化或无效更新会停止写入并保留草稿，可以导出后重新打开服务器版本。

共享评论支持段落留言、回复及回复的回复。只可编辑自己的消息，工作区所有者可以删除消息；删除回复保留位置与后续回复，没有“已解决/未解决”。读者可以展开折叠、复制文字和查看完整回复，展开状态只作用于自己的视图。

临时密码首次设置会撤销其他临时登录；停用账号、重置密码或移除工作区成员会撤销对应访问。原生共享令牌用 Windows DPAPI 保存，与个人 M6 令牌分开。

管理员可以在“编辑账号”中修改自己的登录名、名字和新密码，稳定账号身份和已有数据保留；本人改密码保留当前登录，其他设备重新登录。所有用户均可在“个人设置”设计文字/图片头像、修改个人 ID 和显示名，或核对当前密码后修改密码。

“同步详情”展示个人同步文档/版本、附件大小、可访问的共享工作区和回收站、有效登录设备及在线编辑连接。暂停写入保留阅读与草稿，恢复后共享端继续上传；个人 M6 下次同步时重试。退出单设备、批量退出、停用账号均不删除文档。最近交换时间不等于所有设备都已完成同步；列表最多展示最近 200 条个人文档和登录记录。

安卓浏览器采用抽屉导航、纵排分栏和表格独立横滚。段落下方的评论摘要在手机打开底部弹层，评论和回复可滚动，输入区根据键盘造成的可见视口变化调整；电脑和原生使用段落附近的浮层。

## 当前局域网部署

本项目的 Linux ARM64 实例使用 `writeme-sync` Compose 项目，保留原卷 `writeme-sync_writeme-data` 和端口 `18789`：

- 笔记网页：`http://192.168.1.109:18789/team`
- 独立管理后台：`http://192.168.1.109:18789/admin`
- 原生共享服务器地址：`http://192.168.1.109:18789`

部署目录为 `/home/quser/writeme-shared-20260912`，环境配置与升级前备份留在服务器；本机连接信息与备份位于被忽略的 `artifacts/`，不提交登录密码。

升级或迁移部署目录时必须保留已有 Compose 项目名，例如为命令添加 `--project-name writeme-sync`。改变目录后使用新的默认项目名会连接到另一份空卷。

## 个人 M6 客户端连接与 HTTPS

原生程序打开“设置 → 同步”，填写地址、账号及密码，点击“登录并同步此资料库”。默认每 30 秒交换一次，顶栏也可以立即同步。登录把当前资料库的所有空间、文档与附件和该账号合并；需要独立账号时使用不同的 `--data-dir`。

个人 M6 的非回环服务器必须使用 HTTPS；共享模式允许局域网私有 IP 的 HTTP。个人 M6 可以在宿主机的 Caddy 中配置，例如：

```caddyfile
notes.example.com {
    request_body {
        max_size 64MB
    }
    reverse_proxy 127.0.0.1:8787
}
```

将示例域名替换成自己解析到服务器的域名，并确保代理能取得可信证书。Nginx/OpenResty 使用相同的上游，上传体积上限至少 50 MiB；代理若运行在另一容器，`127.0.0.1` 指向代理自己，需要专用网络或宿主可达地址。客户端不跟随重定向。共享网页通过 HTTPS 反代部署还需要配置受信任代理、正确的转发协议及 WebSocket，以通过同源检查；本次交付验证的是局域网直连。

没有域名时可以先使用 SSH 隧道；在运行客户端的电脑上保持以下命令运行：

```sh
ssh -N -L 18789:127.0.0.1:8787 -p 22 user@server
```

客户端地址填写 `http://127.0.0.1:18789`。如果服务端端口也改为 18789，将命令右侧的 `8787` 改为 `18789`。SSH 负责远程链路加密，应用的回环 HTTP 限制保持不变。

## 维护命令与登录状态

新增账号从标准输入读取一行密码，不把密码放进命令参数：

```sh
docker compose -f sync/compose.yaml --env-file sync/.env exec writeme-sync dotnet WriteME.SyncServer.dll --add-user second-user
```

输入至少 6 个字符的密码并回车。通常使用 `/admin` 的账号管理即可。个人 M6 数据和附件按账号隔离，共享文档按工作区成员权限访问；登录名不区分大小写，现有账号不会被同名创建覆盖。

密码用独立盐和 PBKDF2-SHA256 保存，会话令牌只存哈希，30 天过期。客户端退出登录会清除本机凭据并尝试撤销服务端会话；服务器不可达时旧会话自然过期。Windows 客户端使用当前用户 DPAPI 保护令牌，不保存登录密码；其他系统当前不持久化令牌。

服务器可以读取同步正文和附件，当前没有端到端加密。资料库 ZIP 导出不包含登录凭据或服务端账号。

## 个人 M6 的冲突和恢复

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
| 共享文档 | CRDT 状态最多 16 MiB、50,000 块、80 层、4,000,000 UTF-16 字符 |
| 共享评论 | 每文档最多 1,000 条讨论、10,000 条消息、每消息 20,000 字 |

完整原生回归共 299 项，包含 M6 真实 HTTP、共享 WebSocket、账号权限、消息作者校验、临时会话撤销、两原生窗口和 Chromium/原生联动。运行前先 `npm run build` 及 `npx playwright install chromium`。`npm run test:shared` 另验证 `/admin`、两个浏览器账号、评论、表格、离线恢复和只读展开，使用临时数据目录。

Linux ARM64、Docker 29.7.1 / Compose 5.3.1 已完成新镜像的非 root 运行、双账号网页协作、安卓浏览器模拟与重启持久化验证；生产服务保留旧资料卷并开放局域网 18789。手机与账号版本的当前运行目录为 `/home/quser/writeme-mobile-accounts-9093b78`，Compose 项目仍为 `writeme-sync`。升级前停止本项目容器复制完整资料卷，保留服务器和本机两份备份并核对 SHA-256；账号身份/密码字段、个人同步状态、共享正文和评论数据在升级前后保持一致。

3 组共享网页端到端测试在隔离容器验证评论、头像、六位密码、管理员改名和设备管理，重启后再次读取头像和账号数据。生产只验证入口、资源和数据一致性，不修改用户现行账号。旧 M6 的两 Windows C# 客户端、附件、冲突与空库恢复结果继续保留。构建使用 `compose.host-build.yaml` 的主机网络设置；Docker Hub 不可达时通过 `NODE_IMAGE` 参数使用已有的 `public.ecr.aws/docker/library/node:24-bookworm-slim`。其他服务不重启，验证产物在被忽略的 `artifacts/shared-qa/` 和 `artifacts/native/qa-remote-sync/`。

公网 HTTPS 代理与证书部署未包含在这次隧道验收中。

同步前沿、CRDT 历史、墓碑及未引用附件当前没有自动垃圾回收。共享附件上传、个人资料库迁入、@提醒和通知尚未实现；大资料库性能、跨实例高可用和公网长期运行仍需负载与运维验证。协议机制见 [共享架构](../.agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md) 和 [M6 决策记录](../.agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md)。
