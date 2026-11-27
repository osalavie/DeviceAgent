# Sim.DeviceAgent

Sim.DeviceAgent 是运行在 Windows 上位机上的多设备采集转发 Agent。单个进程可同时承载多台串口、TCP 与文件设备，按设备独立排队、编号并持久化 spool；内置 delimiter / 正则、passthrough、CSV 编解码，数据可实时推送到本机 WebSocket，或带发布确认与断线补发地投递到中心网关 / RabbitMQ / Kafka。附带 WinUI 3 托盘宿主、配置热加载和管理接口，自包含单文件发布即用。

采集运行时 P0 MVP 已实现，设计基线见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)，文档索引与阅读顺序见 [`docs/README.md`](docs/README.md)。

本仓库由通用平台基座仓的 `tool/` 目录独立而来，作为独立项目维护。

桌面端 WinUI3 一体化托盘宿主 P0 已实施（2026-11-20）：工程拆分为 `Sim.DeviceAgent.Core` 类库与 `Sim.DeviceAgent.App` WinUI3 应用，原生 Win32 `Shell_NotifyIcon` 托盘、单实例和概览页可用。设备、出口与 Agent 的结构化配置编辑已可用；JS 解析脚本、实时数据与日志页面按 [`docs/UI_DESIGN.md`](docs/UI_DESIGN.md) 设计，尚未实施。

> 状态：采集运行时 P0 MVP、中心上报和配置热加载已实现（2026-11-16）；WinUI3 桌面壳 P0 已实施（2026-11-20）。**2026-11-26 起 WebSocket 监听地址、令牌与 Origin 白名单归属到各 `wsServer` 出口，可同时监听多个地址；新增 RabbitMQ 与 Kafka 生产出口；主窗口改为自定义标题栏，并实现开机自启与进程守护。** 当前宿主面向单台上位机本机使用：本机出口仅回环监听，令牌可留空，不需要 TLS。解析插件、Windows 服务形态和自动升级待后续阶段。平台侧中心网关尚未实现。
>
> 拓扑关系：一个 Agent 进程对应一台上位机，可同时承载多台设备；每台设备就是一条接入定义（`serial`/`tcp`/`file`），使用独立的串口、TCP 连接或文件目录，不共用链路，因此不需要 demux。

## 已实现

- 多设备接入：每台设备独立 `deviceId`、接入方式（`serial`/`tcp`/`file`）、解析器、有界队列和序号。
- 接入适配器：`serial`、`tcp`、`file`；串口/TCP 自动重连，串口重新打开时先丢弃积压的旧数据并重置拆帧缓冲，避免重连后一次性读入历史帧；文件设备支持写入稳定判定、SHA-256 去重和归档。
- 内置编解码：`delimiter` + 正则命名组、`passthrough`、`csv`（表头映射、引号处理、数值/布尔转换）。
- `wsServer` 出口：每个出口独立配置监听地址/端口、可选令牌与 Origin 白名单；多个出口可同时监听不同地址。每个监听地址都提供 `/ws`、`/health`、`/api/status`、`/api/config/reload`。
- 本机 WebSocket 数据面：按设备订阅、状态快照、命令/响应事务。
- 中心 `wsClient` 出口：可选 Bearer 令牌、`hello`、30 秒心跳、指数退避重连、每设备有序补传。
- `rabbitmq` 出口：声明交换机后按路由键模板投递 JSON 信封，发布确认成功后才推进 spool；断线或进程重启后按设备补发。
- `kafka` 出口：按主题/消息键模板生产 JSON 信封，`acks=all` 幂等生产者确认后才推进 spool；支持 SASL/SSL 连接参数，断线或进程重启后按设备补发。
- 按设备 spool：追加式 JSONL 分段、发送前落盘、确认后推进水位并删除分段；`wsClient`、`rabbitmq` 与 `kafka` 共用。
- 跨重启设备序号：按设备预留 100000 个序号块，重启跳到下一个块，避免复用序号（允许出现序号空洞）。
- 配置热加载：保存配置文件后自动校验并重建设备/出口；出口监听地址、令牌与 Origin 同样按新配置重建。
- WinUI3 桌面壳：`Sim.DeviceAgent.App` 承载原生 Win32 `Shell_NotifyIcon` 托盘、单实例（命名 Mutex + 命名管道）和概览页；主窗口使用自定义标题栏（仅显示应用名，标签页紧贴标题栏），关闭窗口默认最小化到托盘，`--autostart` 启动时仅驻留托盘。
- 开机自启与进程守护：默认写入当前用户 Run 项（值名 `Sim.DeviceAgent`，命令为 exe `--watchdog --config …`），登录后由守护进程拉起主程序并驻留托盘；主程序异常退出按 2–60 秒退避自动重启，托盘“退出”同时结束守护进程。配置页“Agent → 启动与守护”可关闭，选择持久化到配置文件同目录的 `ui-settings.json`。
- Windows 托盘菜单：打开主界面、打开配置文件、重新加载配置、退出；托盘初始化失败时 Agent 继续运行。
- 管理接口：`/health` 匿名存活检查，`/api/status` 匿名返回设备、出口和最近一次配置重载状态。

## 尚未实现

- 平台侧中心网关：设备注册、消息去重入库、按设备/类型转发、在线状态与原始帧留存策略。Agent 侧只定义了连接与 ack 协议。
- 解析器插件与厂商 SDK 插件。
- 中心设备凭据换短期令牌；当前中心出口使用可选静态 Bearer 令牌。
- spool 按天滚动、保留期清理与全局配额；当前只按设备大小滚动和限流。
- Windows 服务形态与自动升级；登录自启已按当前用户（HKCU Run）实现。
- WinUI3 解析脚本、实时数据与日志页面（P1）；当前桌面壳提供概览、托盘与配置编辑。
- 跨平台宿主与 Linux 服务；当前宿主为 Windows 专用。
- `tcpServer`、`udp`、HTTP/MQTT 出口与 MQTT 采样聚合；RabbitMQ / Kafka 出口已支持。
- 文件处理状态持久化：当前使用归档移动 + 进程内去重；重启后若文件未归档可能在极端窗口内重复处理。

## 构建与测试

```bash
dotnet build Sim.DeviceAgent.slnx
dotnet test Sim.DeviceAgent.slnx
```

需要 Windows 10 1809 及以上与 .NET 10 SDK；首次构建会还原 Windows App SDK（`Microsoft.WindowsAppSDK`）包。Core 与测试目标为 `net10.0-windows`，桌面宿主为 `net10.0-windows10.0.19041.0`（x64）。

## 运行

1. 构建：

```bash
dotnet build Sim.DeviceAgent.slnx
```

2. 复制示例配置到应用输出目录并按现场修改：

```bash
cp src/Sim.DeviceAgent.App/agent.example.json src/Sim.DeviceAgent.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/agent.json
```

3. 启动桌面应用：

```bash
./src/Sim.DeviceAgent.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/Sim.DeviceAgent.App.exe
```

配置路径按以下顺序确定：命令行 `--config <path>`（或 `-c`、`--config=<path>`）→ 环境变量 `SIM_DEVICE_AGENT_CONFIG` → **程序所在目录下的 `agent.json`**（即 exe 所在目录；单文件发布时同样取实际 exe 所在目录，而不是 `%TEMP%\.net` 自解压目录）。配置中的相对目录按配置文件所在目录解析。带 `--autostart` 启动时只创建托盘，不显示主窗口。界面级设置（开机自启开关）保存在配置文件同目录的 `ui-settings.json`。

`--watchdog` 为内部守护模式：不显示界面，监测主实例并在其异常退出后以 `--autostart` 重新拉起；由开机自启项或运行中的主实例自动启动，一般无需手工执行。正常退出（托盘“退出”）会通知守护进程一并结束。

启动后主窗口显示设备/出口状态，Windows 托盘出现 `Device Agent` 图标；每个 `wsServer` 出口的监听地址都提供同样的接口（示例为默认出口 `local`）：

- `GET http://127.0.0.1:18080/health`
- `GET http://127.0.0.1:18080/api/status`
- `POST http://127.0.0.1:18080/api/config/reload`
- `ws://127.0.0.1:18080/ws`（WebSocket）

如果配置中没有任何 `wsServer` 出口，设备与中心/RabbitMQ/Kafka 出口仍可运行，但不再监听本机 HTTP/WebSocket 管理接口。

主窗口字段：

- 设备行：`帧` 为已接收并投递的帧数（解析失败的帧也计入）；`丢弃` 为设备队列满时丢弃的帧数；`解析错误` 为解码/解析失败计数，例如正则不匹配 `regex_no_match`、CSV 列数不符、帧超长。
- `wsServer` 出口：状态为 `listening`（监听失败为 `faulted`），并显示监听地址与已认证客户端数；本机出口是实时广播，不落盘、不排队，没有客户端时数据直接丢弃，因此不显示“待发送”。
- `wsClient` / `rabbitmq` / `kafka` 出口：显示 `connected`/`disconnected` 与“待发送”，即该出口 spool 中已写入但对端尚未确认的帧数；中心不回 ack、RabbitMQ 或 Kafka 不可达时会持续增长。

`/api/status` 的 `sinks[]` 同步返回 `state`、`endpoint`、`pendingEntries`（`wsClient`/`rabbitmq`/`kafka` 有意义）、`clients`（`wsServer` 为已认证客户端数，其他为连接标记 0/1）和 `lastError`；`wsServers[]` 列出全部 `wsServer` 监听地址与令牌/Origin 配置。

本机默认不需要令牌。`wsServer` 出口显式配置 `token` 时才要求 `hello.token`；`SIM_DEVICE_AGENT_TOKEN` 会覆盖全部 `wsServer` 出口的令牌。中心出口的 `SIM_DEVICE_AGENT_CENTER_TOKEN` 同样只在配置了 `wsClient` 且需要鉴权时使用。

## 发布

发布脚本默认生成自包含单文件 `win-x64` exe，并启用裁剪（`PublishTrimmed=true`）与单文件压缩（`EnableCompressionInSingleFile=true`），产物约 49 MB（未精简时约 268 MB），输出到 `artifacts/publish/win-x64/`，并附带 `agent.example.json`：

```powershell
powershell -File scripts/publish.ps1
```

```bash
bash scripts/publish.sh
```

脚本参数两版一致：位置参数或 `-Output` / `-o` 指定发布目录，例如 `powershell -File scripts/publish.ps1 D:\release\Agent`、`bash scripts/publish.sh /mnt/d/release/Agent`；`-NoSingleFile` / `--no-single-file` 改为目录发布；`-FrameworkDependent` / `--framework-dependent` 改为框架依赖发布（目标机需预装 .NET 10 Desktop Runtime，Windows App SDK 仍随包携带，裁剪自动关闭）；`-NoTrim` / `--no-trim` 关闭裁剪；`-NoCompress` / `--no-compress` 关闭单文件压缩；另有 `-Configuration` / `-c`、`-RuntimeIdentifier` / `-r`。默认目录 `artifacts/publish/<rid>` 每次发布前清空；自定义目录默认保留既有文件（避免误删 `agent.json`、`data/`），需要清空时加 `-Clean` / `--clean`。脚本拒绝发布到工具目录及其上级目录。单文件 exe 启动时自解压到 `%TEMP%\.net`，语言资源随包内嵌、不产生语言文件夹，配置仍按 exe 所在目录解析。

体积优化要点：

- Windows App SDK 按组件引用 `Microsoft.WindowsAppSDK.WinUI`，不再携带 AI / ML / Search / Widgets 负载（约 60 MB）。
- 裁剪移除未使用的 .NET / ASP.NET Core 代码与 Windows SDK 投影（`Microsoft.Windows.SDK.NET.dll` 由 23.7 MB 降至 0.1 MB）；WinUI / WinRT 投影与本应用程序集显式保留，避免 XAML 与 JSON 反射元数据被裁剪。
- 配置文件启用 `JsonSerializerIsReflectionEnabledByDefault`，裁剪后仍使用 System.Text.Json 反射序列化读写 `agent.json`。
- WinUI 3 自 Windows App SDK 1.2 起支持 .NET 裁剪；修改 XAML 或序列化代码后应重新做一次发布冒烟测试。

WinUI3 官方推荐目录发布（含 Windows App SDK 运行时，目标机无需预装）：

```bash
dotnet publish src/Sim.DeviceAgent.App -c Release --self-contained true
```

发布时会裁剪本地化资源，避免生成几十个语言文件夹：

- .NET 卫星资源由 `SatelliteResourceLanguages` 控制，当前保留 `en`、`zh-Hans`。
- WinUI 原生 MUI 语言目录由 `KeepWinUIMuiCultures` 控制，当前只保留 `zh-CN`、`en-us`，其余目录在发布后自动删除。

输出目录中的 `Sim.DeviceAgent.App.exe` 可直接运行；现场配置 `agent.json` 放在 exe 旁即可。手工执行时追加 `-p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true`，即得到脚本默认的裁剪压缩单文件形态。

## 配置要点

`agent.example.json` 即首次启动自动生成的默认配置结构（示例使用显式 `agent.id`）：本机 `wsServer` 出口 `local` + 一个 `file-001` 文件设备，监视配置目录下的 `data/in`，直通解析，归档到 `data/archive`、错误文件移入 `data/error`。串口/TCP 设备与中心 `wsClient`、`rabbitmq`、`kafka` 出口的字段见下文表格，完整结构见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

配置结构：

| 节点 | 说明 |
| --- | --- |
| `agent` | Agent 标识、状态目录、Windows 托盘；WebSocket 监听已归属到 `wsServer` 出口 |
| `sinks` | 出口注册表；支持 `wsServer`（本机页面/命令）、`wsClient`（中心网关）、`rabbitmq` 与 `kafka`（消息中间件生产） |
| `devices` | 每台设备一条定义：`deviceId`（唯一键）、`type`（`serial`/`tcp`/`file`）、`deviceName` 及对应接入字段，另含 `codec` 与 `sinks` |

`wsServer` 出口字段：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `bind` | — | 必填；仅回环地址（`127.0.0.1`、`localhost`、`::1`） |
| `port` | — | 必填；1–65535；同一 Agent 内不同 `wsServer` 出口不得使用相同 `bind:port` |
| `token` | 空 | 可选 `hello.token` 校验；可用 `SIM_DEVICE_AGENT_TOKEN` 覆盖全部 `wsServer` 出口 |
| `allowedOrigins` | `[]` | 浏览器 Origin 白名单；为空时浏览器连接被拒绝，非浏览器客户端可不带 Origin |

需要多个监听地址时，创建多个 `wsServer` 出口即可；每个出口独立监听、独立令牌/Origin，设备在 `sinks` 数组中引用需要投递的出口。

`rabbitmq` 出口字段：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `host` / `port` | — / 5672 | 必填主机与端口 |
| `virtualHost` | `/` | Virtual Host |
| `userName` / `password` | `guest` / 空 | AMQP 凭据；以明文存放在 `agent.json`（与现有令牌一致），需限制配置文件 ACL |
| `exchange` | — | 必填；启动连接后按 `durable` 声明交换机 |
| `exchangeType` | `topic` | `topic`/`direct`/`fanout`/`headers` |
| `routingKey` | `device.{deviceId}` | 支持 `{agentId}`、`{deviceId}` 占位符 |
| `durable` / `persistent` | `true` / `true` | 交换机持久化 / 消息 `deliveryMode=2` |
| `reconnectMinSeconds` / `reconnectMaxSeconds` | 1 / 30 | 指数退避区间 |
| `spoolSegmentMegabytes` / `spoolMaxMegabytes` | 64 / 1024 | 单设备 spool 分段与配额 |
| `spoolDirectory` | `<stateDirectory>/spool/<agentId>/<sinkId>` | 可选，覆盖 spool 根目录 |

RabbitMQ 投递行为：`data`/`event` 等信封先写 spool，再按设备顺序发布；发布确认（publisher confirm）成功后才推进水位，连接中断或进程重启后重放未确认数据，消费端应容忍重复并按 `messageId`（`agentId:deviceId:seq`）去重。`status` 信封不落盘，仅在连接可用时直接投递。

`kafka` 出口字段：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `bootstrapServers` | — | 必填；逗号分隔的 `host:port` 列表 |
| `topic` | — | 必填；支持 `{agentId}`、`{deviceId}` 占位符 |
| `messageKey` | `{agentId}:{deviceId}` | 消息键模板；同键进入同一分区，保证设备内顺序 |
| `securityProtocol` | `plaintext` | `plaintext`/`ssl`/`saslPlaintext`/`saslSsl` |
| `saslMechanism` | `plain` | `plain`/`scramSha256`/`scramSha512`，仅 SASL 协议生效 |
| `saslUsername` / `saslPassword` | 空 | SASL 凭据；SASL 协议下用户名必填；以明文存放在 `agent.json` |
| `sslCaLocation` | 空 | `ssl`/`saslSsl` 时可选 CA 证书路径 |
| `reconnectMinSeconds` / `reconnectMaxSeconds` | 1 / 30 | 指数退避区间 |
| `spoolSegmentMegabytes` / `spoolMaxMegabytes` | 64 / 1024 | 单设备 spool 分段与配额 |
| `spoolDirectory` | `<stateDirectory>/spool/<agentId>/<sinkId>` | 可选，覆盖 spool 根目录 |

Kafka 投递行为：信封先写 spool，再按设备顺序生产；生产者固定 `acks=all` 并启用幂等，Broker 确认（消息状态 `Persisted`）后才推进水位，连接中断或进程重启后重放未确认数据。消息键默认按设备区分分区，消费端应容忍重复并按消息头 `messageId`（`agentId:deviceId:seq`）去重。主题需预先创建，或在 Broker 上开启自动建主题。Agent 只作为生产者，不创建或管理主题与分区。`status` 信封不落盘，仅在连接可用时直接投递。

`agent.localWs` 旧字段仍会被读取并迁移到 `local`（或第一个）`wsServer` 出口，保存后会按新结构重写，不再输出 `agent.localWs`。

接入字段：

- `serial`：`port`、`baudRate`、`dataBits`、`parity`、`stopBits`、`dtrEnable`、`rtsEnable`、`readTimeoutMs`。
- `tcp`：`host`、`port`、`reconnectMinSeconds`、`reconnectMaxSeconds`。
- `file`：`directory`、`pattern`、`stableSeconds`、`pollIntervalSeconds`、`archiveDirectory`、`errorDirectory`。

编解码字段：

- `delimiter`：`delimiter`、`encoding`、`parse.pattern`（正则命名组会转为 JSON 字段）、`parse.ignoreCase`、`filter.pattern`（准入过滤正则，不匹配的帧不入队；留空时全部放行）、`filter.ignoreCase`、`maxFrameBytes`。`ignoreCase` 默认 `false`，由界面"忽略大小写"开关控制，无需在模式里写 `(?i)` 等引擎相关内联选项。
- `passthrough`：`encoding`。
- `csv`：`csvDelimiter`、`hasHeader`（MVP 必须为 `true`）、`columns`（JSON 键 → CSV 表头名）、`encoding`。

`deviceId` 是唯一键，同时用于本机订阅、命令路由、状态与序号；每台设备只描述一条链路。若现场确有一条物理链路承载多台设备（RS-485 多从站、聚合器多工位），先由设备侧网关或前置采集器拆分后再接入 Agent。

本机与托盘字段：

- `agent.tray.enabled`：默认 `true`；Windows 托盘不可用时只记录警告，不阻止 Agent 运行。
- `wsServer.token`：可留空；留空时该出口的 `/ws` 不要求令牌。同一监听地址上的 `/health`、`/api/status` 始终匿名。
- `wsServer.allowedOrigins`：浏览器页面 Origin 白名单；为空时浏览器连接会被拒绝，非浏览器客户端可不带 Origin。配置热加载会重建出口并更新新连接的 Origin/令牌校验。

## 配置热加载

Agent 监听启动时加载的配置文件，保存后约 0.4 秒自动重载；也可以点击托盘“重新加载配置”或调用任一 `wsServer` 监听地址上的：

```bash
curl -X POST http://127.0.0.1:18080/api/config/reload
```

重载流程：重新读取并校验配置 → 停止现有设备和出口 → 按新配置重建（`wsServer` 会按新的地址/端口重新监听）→ 向已连接页面补发状态快照。设备序号、`sequences.json` 和 spool 数据保留。

即时生效：

- `devices`（含各设备 `codec`）与 `sinks` 的出口引用。
- 全部出口字段：`wsServer` 的 `bind`/`port`/`token`/`allowedOrigins`、`wsClient`/`rabbitmq`/`kafka` 的连接参数与 spool 参数。
- `agent.displayName`。

需要重启 Agent：

- `agent.id`、`agent.stateDirectory`。
- `agent.tray.enabled`（托盘只在进程启动时决定是否创建）。

配置文件语法错误、校验失败或修改了需重启字段时，当前运行配置保持不变，`/api/status` 的 `config.lastError` 给出原因。重载期间设备会短暂停止再启动；文件设备依赖归档移动防止重复处理，进程内 SHA-256 去重集合会重建。

## Windows 托盘与主窗口

托盘菜单（左键双击托盘图标同样打开主界面）：

- **打开主界面**：显示并激活 WinUI3 主窗口（运行状态页）。
- **打开配置文件**：用系统默认程序打开当前 `agent.json`。
- **重新加载配置**：与文件监听使用同一重载入口；成功/失败通过托盘气泡提示。
- **退出**：立即关闭主窗口，运行时最多收尾 1 秒后结束进程（未确认数据由 spool 在下次启动重放）。

关闭主窗口默认最小化到托盘，不退出进程；再次启动 exe 时由单实例机制唤醒已运行实例的窗口。进程退出只能通过托盘菜单“退出”。托盘初始化失败时 Agent 继续采集并提供 WebSocket 与状态接口。

### 日志

主程序与守护进程都会写入 `<stateDirectory>/logs/agent-yyyyMMdd.log`（默认 `data/logs/`，按天切分，保留 7 天，UTF-8，级别 Information 及以上）。关键记录包括：

- 设备传输状态变化（`connecting` / `connected` / `waiting` 等）及失败原因；
- 串口/TCP 打开、读取异常（含异常堆栈），用于定位 `资源在使用中` 一类驱动问题；
- WebSocket 客户端连接、订阅、断开与 Origin 拒绝；
- 配置热加载结果、设备/出口启动与停止、启动失败；
- 守护进程的启动与拉起主程序记录。

### 界面配置

主窗口分为“运行状态 / 配置”两个页签；配置页内再分“设备 / 出口 / Agent”：

- 设备：新增、删除与编辑 `serial`/`tcp`/`file` 全部接入字段，`delimiter`/`passthrough`/`csv` 解析器，以及设备引用的出口；`deviceId` 保存后不可修改。
- 出口：可新增、删除任意出口，编号和类型创建后不可修改。“新增出口”选择 `wsServer` 时必须填写监听地址与端口（默认 `127.0.0.1:18080`，自动避开已用端口）；`wsServer` 可编辑监听地址/端口、本机令牌与 Origin 白名单；`wsClient` 可编辑 URL、令牌、重连区间与 spool 参数；`rabbitmq` 可编辑主机、端口、Virtual Host、用户名/密码、交换机/类型、路由键、持久化开关与 spool 参数；`kafka` 可编辑 Bootstrap Servers、主题与消息键模板、安全协议/SASL 机制与凭据、SSL CA 路径、重连区间与 spool 参数。
- Agent：显示名称可改；`agent.id`、`stateDirectory`、托盘与状态目录只读。WebSocket 监听已从 Agent 页移入出口页。

“保存并应用”先执行与启动时相同的整份配置校验，失败时不写盘；通过后以 UTF-8（无 BOM）、CRLF、临时文件替换的方式原子写入当前生效配置文件（默认即程序目录下的 `agent.json`），随后立即触发热加载。文件监听器对同一内容只应用一次，不会重复重载。令牌按配置文件原值写回，环境变量提供的令牌不会落盘。

保存、从磁盘重新载入与运行期热加载（含托盘“重新加载配置”）的结果统一通过窗口顶部悬浮的消息条提示；消息条 3 秒内未手动关闭会自动消失。

配置文件不存在时，启动阶段会按当前配置路径自动创建默认配置：`agent.id` 取主机名，包含本机出口 `local` 和一个示例文件设备 `file-001`（监视 `<配置目录>/data/in`，直通解析），可在界面中修改或删除。配置文件存在但无法解析时，配置页可点击“新建默认配置”用同样的内容覆盖并重新编辑；Agent 未运行或运行时启动失败时，保存后提示重启生效。旧版 `sources`/`channels` 结构不在编辑器模型中，保存后会按当前 `devices` 结构重写。

## WebSocket 协议

消息使用 JSON，字段为 camelCase。完整信封与示例见设计文档第 6 节。

握手（该 `wsServer` 出口的 `token` 为空时不需要 `token`；非空时必须提供）：

```json
{ "v": 1, "type": "hello",
  "subscribe": { "devices": ["PT-001"] } }
```

订阅为空数组表示全部；握手成功返回 `helloAck`，`payload.devices[]` 包含 `deviceId`、`deviceName`、`type`、`state`，可供页面动态发现设备；随后收到订阅范围内的 `status` 快照。浏览器页面必须把自身 Origin 配置到对应 `wsServer` 出口的 `allowedOrigins`。

数据：

```json
{ "v": 1, "type": "data", "agentId": "agent-line-a-01",
  "deviceId": "PT-001",
  "seq": 1, "ts": "2026-11-16T08:15:30.123+08:00",
  "raw": "...", "text": "RESULT,FORCE=12.34N", "parsed": { "force": 12.34 } }
```

命令事务：

```json
{ "v": 1, "type": "command", "deviceId": "PT-001", "requestId": "r-1",
  "payload": { "text": "READ\r\n", "timeoutMs": 2000,
               "match": { "pattern": "^RESULT" } } }
```

Agent 在超时内收到匹配帧后返回 `response`（`ok=true`）；超时返回 `ok=false` 和 `command_timeout`。同一设备同时只允许一个在途命令。

## 中心出口与 spool

在 `sinks` 中增加 `wsClient`，并在设备条目的 `sinks` 数组里同时引用本机与中心出口：

```json
{
  "agent": {
    "stateDirectory": "./data/state",
    "tray": { "enabled": true }
  },
  "sinks": {
    "local": {
      "type": "wsServer",
      "bind": "127.0.0.1",
      "port": 18080,
      "allowedOrigins": [],
      "token": ""
    },
    "center": {
      "type": "wsClient",
      "url": "wss://qms.example.com/device-gateway",
      "token": "",
      "reconnectMinSeconds": 1,
      "reconnectMaxSeconds": 30,
      "spoolSegmentMegabytes": 64,
      "spoolMaxMegabytes": 1024,
      "spoolDirectory": "./data/spool/center"
    }
  },
  "devices": [
    {
      "deviceId": "PT-001",
      "type": "file",
      "directory": "./data/in",
      "pattern": "*.csv",
      "archiveDirectory": "./data/archive",
      "errorDirectory": "./data/error",
      "codec": {
        "type": "csv",
        "columns": { "sampleId": "Sample", "force": "ForceN" }
      },
      "sinks": ["local", "center"]
    }
  ]
}
```

中心出口字段：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `type` | — | 固定为 `wsClient` |
| `url` | — | `ws://` 或 `wss://` 绝对地址；本机/局域网可先用 `ws://`，跨不可信网络时使用 `wss://` |
| `token` | 空 | 可选；非空时写入 `Authorization: Bearer <token>`，可用 `SIM_DEVICE_AGENT_CENTER_TOKEN` 覆盖 |
| `reconnectMinSeconds` / `reconnectMaxSeconds` | 1 / 30 | 指数退避区间 |
| `spoolSegmentMegabytes` | 64 | 单设备分段大小 |
| `spoolMaxMegabytes` | 1024 | 单设备 spool 配额 |
| `spoolDirectory` | `<stateDirectory>/spool/<agentId>/<sinkId>` | 可选，覆盖 spool 根目录 |

`agent.stateDirectory` 默认是配置文件目录下的 `data`，用于 `sequences.json` 和默认 spool 根目录。

连接行为：

1. 配置了令牌时 WebSocket 握手携带 `Authorization: Bearer <token>`；随后发送 `hello`：
   ```json
   { "v": 1, "type": "hello", "agentId": "agent-line-a-01", "displayName": "...", "ts": "..." }
   ```
2. 每条 `data` 先追加到该设备 spool，再按设备顺序发送；连接断开时继续写盘。
3. 中心持久化后回送确认：
   ```json
   { "v": 1, "type": "ack", "agentId": "agent-line-a-01", "deviceId": "PT-001", "seq": 1 }
   ```
   `seq` 按设备累计确认；Agent 收到后推进 `lastDeliveredSeq`，删除已确认的非活动分段。
4. 重连或进程重启后，从 `lastDeliveredSeq + 1` 继续补传；未确认数据会重发，中心必须按 `agentId + deviceId + seq` 去重。
5. Agent 每 30 秒发送 `heartbeat`，包含 `agentId`、待补传数量和发送/确认计数。

可靠性约束：

- spool 布局为 `<spoolDirectory>/<deviceId>/segment-00000001.jsonl`；每行一条消息信封，`state.json` 保存 `lastDeliveredSeq`。
- 设备序号跨重启从预留块继续，允许出现空洞；中心按序处理时不能要求序号连续。
- 单设备 spool 达到配额后，该设备的新数据写入失败并在 `/api/status` 的出口状态中给出错误；本机 `wsServer` 出口继续工作，其他设备不受影响。
- 中心不回 ack 时 spool 不会释放；接入前必须确认中心网关实现 `ack`。
- 中心网关本身（鉴权、去重入库、业务转发、在线状态）尚未在平台侧实现，当前 `wsClient` 只完成了 Agent 侧协议与缓冲。

## 安全与部署

本机模式按“仅上位机内使用”设计：

- 每个 `wsServer` 出口的监听地址强制为回环地址，不监听 `0.0.0.0`；令牌默认留空，此时任何本机进程都可以访问该地址上的 `/api/status` 和 `/ws`（管理接口始终匿名）。
- 浏览器页面仍需命中对应出口的 `allowedOrigins`；这是本机模式下防止其他网页随意连接的主要边界。
- 页面与 Agent 之间使用 `http://` / `ws://`，不启用 TLS。若页面本身通过 HTTPS 提供，浏览器可能阻止 `ws://127.0.0.1` 的混合内容；此时用本机 HTTP 页面、桌面壳或后续增加本机 TLS 支持。
- 中心出口是可选能力。本机使用可不配置 `wsClient`；若启用且跨机器传输，建议配置令牌并使用可信证书的 `wss`。Agent 不提供跳过证书校验的开关，令牌只放在请求头。
- RabbitMQ 出口的 `userName`/`password` 与 Kafka 出口的 `saslUsername`/`saslPassword` 一样以明文存放在 `agent.json`，应限制配置文件 ACL；生产环境建议使用受限发布账号，不要把凭据提交到仓库。当前版本不提供 AMQP TLS 开关，跨不可信网络时应先把 Agent 与 Broker 放在受控网段或由基础设施加密。
- Kafka 出口可选 `securityProtocol`（`ssl`/`saslSsl`）并指定 `sslCaLocation`；Agent 不提供跳过证书校验的开关。`plaintext` 仅应用于受控网段。
- `wsServer` 令牌可用 `SIM_DEVICE_AGENT_TOKEN`，中心令牌可用 `SIM_DEVICE_AGENT_CENTER_TOKEN` 覆盖配置值；不要把生产令牌提交到仓库。
- spool 包含设备原始帧，应限制 `stateDirectory` 和 `spoolDirectory` 的 ACL，只允许运行 Agent 的账号与本机管理员读取。
- 中心 ack、RabbitMQ 发布确认或 Kafka `acks=all` 确认是释放 spool 的唯一条件；对端未确认时不得把该出口作为唯一数据留存链路。
- Agent 进程仍在运行时，断线数据进入 spool；进程停止期间数据不保证补传。
- Windows 服务形态与静默升级尚未实现；开机自启与进程守护已按当前用户实现（HKCU Run + `--watchdog`），当前以 WinUI3 托盘应用运行，关闭窗口后驻留托盘。

## 目录

```text
.
├── README.md
├── docs/                           # 文档索引与阅读顺序见 docs/README.md
│   ├── README.md                   # 文档索引与阅读顺序
│   ├── ARCHITECTURE.md             # 采集运行时设计基线
│   └── UI_DESIGN.md                # WinUI3 桌面端设计基线
├── Sim.DeviceAgent.slnx
├── src/
│   ├── Sim.DeviceAgent.Contracts/  # 消息信封、FileRef、JSON 选项
│   ├── Sim.DeviceAgent.Core/       # 类库：配置、接入适配器、编解码、运行时、spool、出口
│   │   ├── Configuration/          # 配置模型、校验、写盘、热加载与文件监听
│   │   ├── Hosting/                # 可嵌入运行时宿主与管理接口
│   │   ├── Transports/             # serial / tcp / file
│   │   ├── Codecs/                 # delimiter / passthrough / csv
│   │   ├── Runtime/                # 多设备、wsServer/wsClient/rabbitmq/kafka 出口、跨重启序号
│   │   ├── Spool/                  # 按设备 JSONL 分段与确认水位
│   │   └── WebSockets/             # 每个 wsServer 出口的 WebSocket 服务
│   └── Sim.DeviceAgent.App/        # WinUI3 宿主：运行状态/配置页、Shell_NotifyIcon 托盘、单实例
│       └── agent.example.json      # 默认示例配置：本机 wsServer + 文件设备
└── tests/
    └── Sim.DeviceAgent.Tests/      # 56 个单元/集成测试
```

## 许可证

本项目采用 GNU General Public License v3.0，详见 [`LICENSE`](LICENSE)。
