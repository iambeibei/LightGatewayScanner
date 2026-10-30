# CODEBUDDY.md This file provides guidance to CodeBuddy when working with code in this repository.

## 这是什么

.NET MAUI **安卓单平台**应用（`net10.0-android`，包名 `com.lightgw.scanner`，标题「网关扫码写入」）。现场用手机扫「网关 ID + 储能设备 PN」两个码，校验网关在线后通过 MQTT 把 PN（及可选的 BLE MAC）写入网关。

仓库是**单个 csproj**：没有 `.sln`、没有测试工程、没有 CI。`Platforms/iOS`、`MacCatalyst`、`Windows` 是模板残留，不参与编译。

## 常用命令

在 `LightGatewayScanner/` 目录下执行。需要 .NET SDK 10 + `android` workload（`dotnet workload list` 确认）。

| 目的 | 命令 |
|---|---|
| Debug 构建（同时产出 APK） | `dotnet build LightGatewayScanner.csproj -f net10.0-android -c Debug` |
| 部署并启动到已连接设备 / 模拟器 | `dotnet build LightGatewayScanner.csproj -f net10.0-android -t:Run` |
| 安装 Debug APK | `adb install -r bin/Debug/net10.0-android/com.lightgw.scanner-Signed.apk` |
| Release 打包 | `dotnet publish LightGatewayScanner.csproj -f net10.0-android -c Release`，产物在 `bin/Release/net10.0-android/publish/com.lightgw.scanner-Signed.apk` |
| 格式化 / 应用 analyzer 修复 | `dotnet format LightGatewayScanner.csproj` |
| 拉取设备上的今日日志（`YYYYMMDD` 换成当天日期） | `adb shell run-as com.lightgw.scanner cat files/logs/app-YYYYMMDD.log` |

- **没有测试工程**，`dotnet test` 目前无内容可跑。全仓库唯一无 I/O 的纯逻辑是 `Services/MqttReplyParser.cs`，新增测试时它是最先该覆盖的目标，单测命令形如 `dotnet test --filter FullyQualifiedName~MqttReplyParser`。
- **“lint” 等于编译警告**：没有 `.editorconfig`，靠 `Nullable=enable` 与 `dotnet build` 的 CA 警告兜底。
- Release 下 `PublishTrimmed=false`、`RunAOTCompilation=false` 是刻意的，**不要打开**：JSON 走反射，裁剪会静默丢属性。
- `MauiXamlInflator=SourceGen` + `MauiEnableXamlCBindingWithStaticCompilation=true`：绑定路径编译期校验，改 VM 属性名必须同步改 XAML，否则构建失败。

## 架构总览

### 依赖方向

```
Views (XAML + code-behind) → 构造函数注入 → ViewModels (CommunityToolkit.Mvvm)
                                            ↓
IScanSessionService            ← 唯一编排者 + 会话状态机
  ├── IScanClassifier            纯函数：条码 → 网关 ID / PN
  ├── IRedisOnlineService        在线校验
  ├── IBleScanner                按 PN 找 MAC（Android 原生）
  └── IMqttSetPnService          发布 set_pn 并等回执
横切：IAppConfigService / IAppLogService / IDeviceCallbackApiService / IFeedbackService
```

View 层只做 UI 与相机，不直接发网络请求；业务一律从 `ScanViewModel` 进 `IScanSessionService`。

### 扫码 → 提交主链路

`Accept()` 处理去重窗口（ZXing 连续多帧会重复回调）、分类、槽位约束（`ArmedSlot`）、顺序约束、覆盖策略，用 `ScanAcceptOutcome` 枚举而非异常表达拒绝。两槽位填满即 `Ready`。

`SubmitAsync(force, progress, ct)` 严格按序：Redis 在线校验 → BLE 取 MAC（扫不到不阻断，只带 PN 下发）→ MQTT 发布并等回执。Redis 自身不可用且 `AllowSubmitWhenRedisUnavailable` 打开时返回 `CanForce=true`，UI 确认后以 `force: true` 重入；回执失败且 MAC 来自缓存则 `InvalidateCache(pn)`。

`SubmitOutcome` 是给 UI 的扁平结果（在线状态、Seq、DeviceMac、BLE 诊断、耗时、`CanForce`）。`_busy` 用 `Interlocked.CompareExchange` 互斥；该服务为单例并持有可变会话状态，是「当前这组码」的唯一真相来源。

### MQTT 契约（`MqttReplyParser` 是唯一权威定义）

```
发布  server/{网关ID}/cmd/set_pn
      {"Seq":1782373476,"Data":{"PN":"PN123","MAC":"c7:2a:18:32:34:d3","AddrType":0}}
订阅  device/+/cmd/reply/pn
      {"Seq":1,"Data":{"CmdSeq":1782373476,"Code":1,"Msg":null,"CmdData":{"GetPN":"PN123"}}}
```

- 关联键是 `Data.CmdSeq`，不是顶层 `Seq`（后者仅记日志）。
- 成功 = `Code == 1`，且若回执带 `CmdData.GetPN` 必须与提交值一致（防写错）。
- 回执主题里的网关 ID 必须与登记的一致，否则丢弃。
- `Seq` = Unix 秒级时间戳，同秒内自增保证唯一；无 MAC 时 `MAC` 与 `AddrType` 两个键都不出现。
- 网关 ID 含 `/ + #` 会破坏主题，发布前拒绝；回执属性名忽略大小写解析（网关可能发 `cmdSeq`）。

### 连接与配置生命周期

- MQTTnet 5.x 无 `ManagedMqttClient`，重连在 `MqttSetPnService` 内自行实现（指数退避、最多 10 次、放线程池）。
- 回调必须在 `ConnectAsync` **之前**注册，否则连接瞬间消息丢失；发布前要确认「已连接且已订阅」，只判断 `IsConnected` 不够。`ClientId` 持久化在 `Preferences`。
- 配置变更只在**连接签名**（`host|port|user|password`）变化时重连，只改回执超时不重连；新增 MQTT 配置项先想清楚要不要进签名。
- `AppConfig` 整份 JSON 存 `Preferences` 单 key `app_config`，`CurrentVersion` + `Migrate()` 升级：新增带默认值的属性无需迁移，**改既有默认值或语义必须升版本并显式迁移**（只改代码默认值对已装机用户无效）。
- `ConfigChanged` 是全局扇出点（Redis 清缓存、MQTT 重连、`ScanPage` 重建相机、VM 刷新）；`Save()` 只在实质变化时触发，避免无谓重连。
- `BuildTag` 显示在扫码页副标题，用于现场确认装的是哪一版；改动需现场核对的逻辑时顺手更新。

### BLE 取 MAC

约定：**设备广播名就是 PN**（对应网关固件 `ble_connect_by_name`）。三个非显然约束：

1. 不能用硬件 `ScanFilter`——不支持 RSSI，且设备名可能只在扫描响应里，只能软件过滤 + 合并名匹配。
2. 必须遵守 Android 7+ 的扫描频率限制（每 App 30 秒最多 5 次 `startScan`），`BleScanner` 本地提前拦截。
3. `ScanCallback` 必须用字段持有，否则 Java 侧被 GC 后不再回调甚至原生崩溃。

命中缓存不消耗配额；首次命中后再等 `StabilizeMs`，仅 1 台提前返回，≥2 台扫到窗口结束取最强 RSSI。权限走自定义 `BleScanPermission`（API 31+ 用 `BLUETOOTH_SCAN/CONNECT`，≤30 用定位）。

### UI 与平台约束

- Shell + `TabBar` 两个静态路由（`scan` / `settings`）；新增页面 = `AppShell.xaml` 加 `ShellContent` + `MauiProgram.cs` 注册。
- **页面与 VM 全为 Singleton**（横竖屏切换不丢已扫数据），副作用是 VM 构造只跑一次，`OnAppearing` 不能假设是首次进入。
- 相机生命周期在 `ScanPage.xaml.cs`：`OnDisappearing` 必须 `ReleaseCamera()`，仅设 `IsVisible=false` 不会释放摄像头。
- `RackPicker` / `PointPicker` 的 `ItemDisplayBinding` 不是 `BindableProperty`，只能 code-behind 绑定。
- API 模式用请求代际计数器丢弃过期响应；点位 `DeviceId` 为 `"0"` 表示空点位需过滤。
- 日志写 `AppDataDirectory/logs/app-yyyyMMdd.log`，保留 7 天且**异常全吞**（日志不能打断主流程），格式 `事件 key=value`。
- 回 UI 线程用 `MainThread.BeginInvokeOnMainThread`；阶段进度用 `IProgress<string>`；纯日志 `_ = XxxAsync()` fire-and-forget。

## 代码约定

- 注释与 UI 文案**全中文**，注释解释「为什么」而不是「做了什么」。
- 文件作用域命名空间，与目录同名；服务实现 `sealed`；私有字段 `_camelCase`；异步方法 `XxxAsync` 且接口统一带 `CancellationToken`。
- 平台相关代码用 `#if ANDROID` 隔离在同一文件内（`BleScanner`、`FeedbackService`），不拆平台目录。
- 配色沿用既有硬编码色板：主色 `#512BD4`，信息 `#185FA5`，成功 `#3B6D11`，错误 `#A32D2D`，警告 `#854F0B`，面板底 `#F5F6F8`，边框 `#DCDCE4`。
- 明文 HTTP 只通过 `network_security_config.xml` 放行，且仅服务老化架 / 点位两个接口；设置 PN 走 MQTT 明文 TCP，不经过该配置。
