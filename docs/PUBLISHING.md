# 发布打包与体积优化

> 适用版本：Sim.DeviceAgent 1.0.1（2026-11-27）。本文记录发布包的体积构成、精简措施、踩坑与冒烟清单；构建、运行与配置说明见根目录 [README.md](../README.md)。

## 1. 结论

- 发布默认产物为自包含单文件 `win-x64`，**目标机无需安装 .NET 或 Windows App SDK**。
- 单文件 exe 约 **49.0 MiB**（51,383,160 字节），GitHub Release zip 约 **43.3 MiB**（含 exe 与 `agent.example.json`）。
- 精简手段：Windows App SDK 按组件引用 + IL 裁剪 + 单文件压缩 + 保留必要的反射元数据。
- 入口：`scripts/publish.ps1` / `scripts/publish.sh`，默认开启裁剪与压缩，`--no-trim` / `--no-compress` 可关闭。

## 2. 体积构成与方案对比

| # | 方案 | 目录发布 | 单文件 | 结果 |
| --- | --- | --- | --- | --- |
| 1 | WindowsAppSDK 元包，自包含，无裁剪无压缩 | 272 MiB | 268 MiB | 可用 |
| 2 | 元包 + 单文件压缩 | — | 109 MiB | 可用 |
| 3 | WinUI 子包（去 AI / ML / Search / Widgets），无裁剪无压缩 | 209 MiB | 84.1 MiB | 可用 |
| 4 | 方案 3 + 裁剪（未保留元数据） | 96 MiB | 45.8 MiB | 启动失败，见 4.1、4.2 |
| 5 | 方案 4 + 保留应用三程序集 + STJ 反射开关 | — | 41.3 MiB | 配置可读，XAML 仍崩溃 |
| 6 | 方案 5 + 保留 `Microsoft.WinUI` | — | 43.1 MiB | 仍崩溃 |
| 7 | 方案 6 + 保留 `Microsoft.Windows.SDK.NET` / `WinRT.Runtime` | — | 49.0 MiB | 冒烟通过（当前方案） |

说明：“目录发布”指 `--no-single-file`；“单文件”为压缩后 exe 大小；方案 4–7 为定位问题过程的中间态，仅方案 7 可发布。

未精简时的主要体积来源：

| 来源 | 说明 |
| --- | --- |
| Windows App SDK 元包 AI / ML 负载 | `onnxruntime.dll` 22.5 MB、`DirectML.dll` 17.8 MB、`Microsoft.Windows.Search.dll` 4.0 MB、SemanticIndex / AiFabric 3.8 MB 等，本项目完全不用 |
| Windows SDK / WinUI 托管投影 | `Microsoft.Windows.SDK.NET.dll` 23.7 MB、`Microsoft.WinUI.dll` 7.0 MB，绝大多数类型未使用 |
| .NET 运行时与 ASP.NET Core | `wsServer` 依赖 Kestrel（Core 项目 `FrameworkReference=Microsoft.AspNetCore.App`），但 MVC / Identity 等组件未使用 |
| WinAppSDK 原生组件 | `Microsoft.ui.xaml.dll` 14.5 MB 等，随包携带，不参与托管裁剪 |

## 3. 精简措施

### 3.1 Windows App SDK 按组件引用

- 将 `Microsoft.WindowsAppSDK` 元包替换为 `Microsoft.WindowsAppSDK.WinUI`（`Directory.Packages.props` 与 App csproj 同步修改）；WinUI 会自动带入 Foundation / InteractiveExperiences / Base。
- 效果：不再携带 AI / ML / Search / Widgets 负载，目录发布 272 → 209 MiB（约 -63 MiB）。
- 检查：输出目录不应出现 `onnxruntime.dll`、`DirectML.dll`、`Microsoft.Windows.AI.*`、`Microsoft.Windows.Search.*`、`Microsoft.Windows.Widgets.*`。
- 若后续使用 Windows ML / AI API，需按对应包的 README 额外引用 `Microsoft.WindowsAppSDK.Runtime`，体积会相应回升。

### 3.2 IL 裁剪（PublishTrimmed）

- 仅自包含发布启用；`--framework-dependent` 下脚本自动关闭。
- 实测裁剪幅度：`Microsoft.Windows.SDK.NET.dll` 23.7 → 0.11 MB，`Microsoft.WinUI.dll` 7.0 → 0.33 MB，`System.Private.CoreLib.dll` 15.3 → 3.1 MB；未使用的 ASP.NET Core 组件被移除。
- Windows App SDK 自 1.2 起支持 .NET 裁剪，但本项目的反射用法必须显式保留（见 3.3）。

### 3.3 保留反射元数据（关键配置）

App csproj 固定以下配置，缺一不可：

```xml
<JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>
<ItemGroup>
  <TrimmerRootAssembly Include="Sim.DeviceAgent.App" />
  <TrimmerRootAssembly Include="Sim.DeviceAgent.Core" />
  <TrimmerRootAssembly Include="Sim.DeviceAgent.Contracts" />
  <TrimmerRootAssembly Include="Microsoft.WinUI" />
  <TrimmerRootAssembly Include="Microsoft.Windows.SDK.NET" />
  <TrimmerRootAssembly Include="WinRT.Runtime" />
</ItemGroup>
```

- `JsonSerializerIsReflectionEnabledByDefault`：裁剪默认关闭 System.Text.Json 反射序列化，配置加载会直接失败（见 4.1）。
- 应用三程序集保留：`agent.json`、spool 记录与 WebSocket 消息都依赖 STJ 反射读写这些类型；实测保留后体积几乎不变。
- WinUI / SDK.NET / WinRT 投影保留：XAML 通过托管投影激活控件类型，裁掉任意一层都会崩溃（见 4.2）。三项保留在压缩后仅增加约 7.7 MiB。

### 3.4 单文件压缩

- `EnableCompressionInSingleFile=true`，仅自包含单文件可用；框架依赖单文件会报 NETSDK1176。
- 托管程序集启动时从包内解压加载；原生库仍按原机制自解压到 `%TEMP%\.net`。

### 3.5 资源语言裁剪（既有）

- .NET 卫星资源：`SatelliteResourceLanguages=en;zh-Hans`。
- WinUI MUI 目录：`KeepWinUIMuiCultures=zh-CN;en-us`，发布后删除其余语言目录。

## 4. 踩坑记录

### 4.1 配置加载失败：STJ 反射被禁用

- 症状：应用日志出现 `Configuration error: Reflection-based serialization has been disabled for this application...`，健康接口不监听。
- 原因：`PublishTrimmed=true` 时 .NET SDK 将 `JsonSerializer.IsReflectionEnabledByDefault` 设为 false。
- 处理：csproj 设置 `JsonSerializerIsReflectionEnabledByDefault=true`；长期方案是迁移到 STJ 源生成器。

### 4.2 XAML 启动崩溃 0xC000027B

- 症状：应用启动后立即退出（退出码 -1073741189 / 0xC000027B）；事件查看器显示故障模块 `Microsoft.UI.Xaml.dll`，WER 签名 `combase.dll 80004003`；若守护进程在运行，会反复拉起并重复崩溃。
- 原因：托管 WinRT 投影被裁剪，XAML 运行时激活控件类型失败。
- 处理：按 3.3 保留 `Microsoft.WinUI`、`Microsoft.Windows.SDK.NET`、`WinRT.Runtime`。实测只保留应用程序集、或再保留 `Microsoft.WinUI` 均不足，仍会崩溃。

### 4.3 框架依赖模式不是精简路径

- `--framework-dependent` 下裁剪与压缩都不可用（压缩报 NETSDK1176），且 Windows App SDK 仍随包携带。
- 实测单文件 105.6 MiB，反而大于自包含精简版；目标机还需额外安装 .NET 10 Desktop Runtime。
- 除调试或特殊分发场景外，发布包应使用默认自包含模式。

### 4.4 调整裁剪属性后先清理再测量

- 观察到切换 `TrimMode=partial` / `full` 时可能得到字节一致的产物（增量构建复用链接结果）。
- 调整裁剪或发布属性后，先清空发布目录（默认目录会自动清理）或清理 `obj` / `bin`，再比较体积与行为。

## 5. 发布冒烟清单（每次发布必做）

1. `dotnet test Sim.DeviceAgent.slnx` 全绿（当前 63 个用例）。
2. 用脚本发布，确认 exe 体积与预期一致（当前约 49 MiB）。
3. 隔离运行：
   - 复制 `agent.example.json` 为 `agent.json`，使用唯一 `agent.id` 与空闲端口；
   - 同目录写 `ui-settings.json`：`{"startWithWindows": false}`，避免写入 HKCU Run；
   - 使用独立数据目录。
4. 断言：
   - `GET /health` 返回 `{"status":"ok"}`；
   - `GET /api/status` 中 `wsServers` 监听、设备 `connected`、sink `listening`；
   - WebSocket `ws://127.0.0.1:<port>/ws` 发送 `hello` 后收到 `helloAck` 与 `status`；
   - 向文件设备输入目录写入文件，数秒后归档到 `data/archive/<yyyyMMdd>/`；
   - 主窗口标题与 UI Automation 树可读（验证 XAML 正常加载）；
   - exe 版本信息为 `1.0.1`，`ExtractAssociatedIcon` 能取到图标（验证托盘与资源嵌入）。
5. 清理：
   - 先结束 `--watchdog` 守护进程，再结束主进程，避免被自动拉起；
   - 确认无残留进程与 HKCU Run 项（仅删除指向临时目录的测试项）；
   - 删除临时配置、日志与数据目录。
6. 排障：
   - 应用日志位于状态目录（`agent.stateDirectory`）下的 `logs/agent-<date>.log`；
   - 启动即退时查看事件查看器 Application 日志（Id 1000）；
   - 与未裁剪基线对照，区分既有问题与裁剪回归。例如 `POST /api/config/reload` 目前在未裁剪构建上同样会中断监听，不属于裁剪引入。

## 6. 维护约定

- 修改 XAML、System.Text.Json 序列化或新增 Windows App SDK 能力后，必须重新发布并执行第 5 节冒烟。
- 不要把 `dotnet publish` 的原始输出当作发布包：未传裁剪 / 压缩参数时单文件仍约 268 MiB。
- 发布体积监控线：单文件超过 60 MiB 时，优先排查是否回退了元包、裁剪或压缩开关。
- ASP.NET Core `FrameworkReference` 是 `wsServer`（Kestrel）的必需依赖，不能删除。
- 后续优化空间：迁移 STJ 源生成器后可移除反射开关；用更轻量 WebSocket / HTTP 实现替换 Kestrel 可进一步缩小；WinUI 3 不支持 NativeAOT，暂不考虑。

## 附录：常用命令

```bash
# 默认发布（自包含 + 裁剪 + 压缩）
bash scripts/publish.sh

# 对比未精简体积
bash scripts/publish.sh artifacts/publish/full --no-trim --no-compress

# 手工等价命令
dotnet publish src/Sim.DeviceAgent.App -c Release -r win-x64 -o artifacts/publish/win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true
```
