---
name: scan-result-banner-compact
overview: 把扫码页提交结果提示改成单行三态文案（发送成功带 MAC / 发送成功无 MAC / 发送失败），并以浮层形式叠在相机预览底部，不再占用布局行，从而彻底消除提示框出现时压缩视频预览的问题；Seq、耗时、MAC、蓝牙诊断、原始回执 JSON 全部从界面移除，仅保留在日志中。
todos:
  - id: vm-result-compact
    content: 重构 ScanViewModel 结果三态单行文案，删除 ResultDetail 与 BuildResultDetail，新增浮层/行内可见性属性及成功提示自动隐藏
    status: completed
  - id: xaml-result-overlay
    content: 将结果提示改为相机预览内浮层并保留行内兜底，删除详情 Label，相机加最小高度，样式复用
    status: completed
    dependencies:
      - vm-result-compact
  - id: build-and-verify
    content: 更新 BuildTag 为 2026-10-29.1，执行 Debug 构建校验绑定与警告
    status: completed
    dependencies:
      - xaml-result-overlay
---

## 用户需求
修复扫码页「提交结果提示框出现后把相机预览挤扁」的缺陷，并把提示内容精简为三种单行状态。

## 产品概述
现场手机扫「网关 ID + 储能设备 PN」提交后，界面只给一条简短结果提示，不遮挡、不压缩相机画面，方便连续扫码作业。

## 核心功能
- 结果提示精简为三态单行文案：
  - 发送成功且带 MAC 地址 → 「发送成功（带 MAC 地址）」
  - 发送成功但无 MAC 地址 → 「发送成功（无 MAC 地址）」
  - 发送失败（含异常）→ 「发送失败」
- 提示条改为浮层覆盖在相机预览底部，不再占用页面布局行，相机预览高度保持不变，且不拦截「照明 / 关闭相机 / 手动输入」按钮点击。
- 相机未显示时（关闭相机、无权限、未配置）改在状态区行内显示同一条提示，保证提示不丢失。
- 成功提示浮层停留数秒后自动消失，避免长期遮挡预览；失败提示保留到下次提交或清空。
- 详细诊断信息（Seq、耗时、MAC、蓝牙耗时、同名设备数、原始回执 JSON）不再在界面展示，仅保留在本地日志中，排查时照常拉日志。
- 失败原因（网关不在线、Redis 不可用、回执超时等）不在界面显示，但必须能在日志中查到；「Redis 不可用是否强制提交」的确认弹窗保持原有完整说明不变。


## 技术栈
- 平台：.NET MAUI 单工程（`net10.0-android`），包名 `com.lightgw.scanner`
- MVVM：CommunityToolkit.Mvvm（`[ObservableProperty]` / `[RelayCommand]`）
- UI：XAML，`MauiXamlInflator=SourceGen` + `MauiEnableXamlCBindingWithStaticCompilation=true`（绑定路径编译期校验）
- 校验：`dotnet build LightGatewayScanner.csproj -f net10.0-android -c Debug`

## 实现方案
### 总体思路
缺陷根因是结果条位于根 Grid 的 `Auto` 行（Row 3），一旦显示就把 `*` 行（Row 1 采集区）压小，相机 Border 内部 `*` 行（预览）先塌陷，只剩工具条。解决分两步：**内容瘦身**（提示只留一行，自然变矮）+ **位置改造**（提示改为相机预览区内的浮层，完全不参与布局测量），双管齐下，任何内容长度都不会再压缩画面。

### 关键技术决策
1. **只改 ViewModel 展示层，不动 `IScanSessionService` 契约**：`SubmitOutcome.Message` 仍保留完整原因文本，因为 `CanForce` 分支的 `DisplayAlertAsync("Redis 不可用", outcome.Message, ...)` 需要它；界面是否显示原因由 VM 决定，避免影响服务层与日志语义。
2. **三态文案由 VM 依据 `SubmitOutcome` 派生**：`outcome.Success && !string.IsNullOrWhiteSpace(outcome.DeviceMac)` → 带 MAC 成功；`Success && 无 MAC` → 无 MAC 成功；`!Success` 或 catch 异常 → 「发送失败」。文案只保留结论，不带网关 ID / PN（上方两张卡片已展示），单行 `LineBreakMode="TailTruncation"`。
3. **删除 `ResultDetail` 与 `BuildResultDetail()`**：诊断信息在界面彻底移除。回执原文已由 `MqttSetPnService`（`回执={reply.RawResponse}`）与 BLE 详情由 `ScanSessionService` 写入日志，信息不丢失；VM 侧只补一条精简结果日志（success / gateway / pn / 是否带 MAC / 原因），不重复打回执 JSON，避免日志膨胀。
4. **浮层与行内两套可见性派生属性**：新增 `ResultOverlayVisible => ResultVisible && CameraVisible`、`ResultInlineVisible => ResultVisible && !CameraVisible`，在 `resultVisible`、`cameraVisible` 字段上加 `[NotifyPropertyChangedFor]`，保证任一状态变化都刷新。相机不可见时仍有一条行内提示，不会出现"提交了但没有任何反馈"。
5. **样式复用**：在 `ScanPage.Resources` 定义 `ResultBannerBorder` / `ResultBannerLabel` 两个 `Style`，浮层与行内共用，避免复制粘贴（DRY），后续改配色只改一处。
6. **自动隐藏用代际计数**：成功提示 4 秒后自动消失，用 `_resultGeneration` 自增令牌，`Reset`、新提交、失败分支均 `++` 作废旧任务，避免旧延时把新提示误隐藏（`Task.Delay` + 令牌比较，无定时器泄漏、无额外线程常驻）。
7. **相机最小高度兜底**：相机 Border 加 `MinimumHeightRequest`，即使极端小屏 / 键盘弹出也不至于把预览压成一条工具条。

### 性能与可靠性
- 不新增任何网络、I/O 或后台轮询；自动隐藏为一次 `Task.Delay`，开销可忽略。
- 浮层 `InputTransparent="True"`，不改变相机手势与工具条按钮的命中测试，避免引入"点不到照明"的新问题。
- 页面与 VM 为 Singleton，属性变更需 `MainThread` 上下文：现有提交链路已在 UI 线程 await 回来，自动隐藏回到 UI 线程后仅改绑定属性，安全。
- 影响面收敛在扫码页结果展示，设置页、MQTT、BLE、Redis 逻辑零改动。

## 目录结构
```
LightGatewayScanner/
├── ViewModels/
│   └── ScanViewModel.cs     # [MODIFY] 结果展示重构：三态单行文案；删除 resultDetail 与 BuildResultDetail；
│                            #          新增 ResultOverlayVisible / ResultInlineVisible 派生属性（配 NotifyPropertyChangedFor）；
│                            #          新增成功提示 4 秒自动隐藏（_resultGeneration 令牌作废机制）；
│                            #          catch 分支文案改为「发送失败」并记录异常日志；
│                            #          CanForce 弹窗分支先隐藏结果条再弹确认，保持 outcome.Message 原文案；
│                            #          Reset 中清理新字段并作废旧令牌；
│                            #          提交结束后补一条精简结果日志（success/gateway/pn/mac 有无/原因）
├── Views/
│   └── ScanPage.xaml        # [MODIFY] 相机 Border 内预览 Grid 增加结果浮层 Border
│                            #          （Grid.Row=0、VerticalOptions=End、Margin 让出工具条、InputTransparent=True、
│                            #           IsVisible 绑 ResultOverlayVisible、单行 TailTruncation Label）；
│                            #          Row 3 原结果条改为行内兜底（IsVisible 绑 ResultInlineVisible），删除 ResultDetail Label；
│                            #          相机 Border 增加 MinimumHeightRequest；Page.Resources 增加结果条 Border/Label 样式
└── Models/
    └── AppConfig.cs         # [MODIFY] BuildTag 由 2026-10-28.2 升为 2026-10-29.1，便于现场确认装的是哪一版
```

## 实现注意
- **SourceGen 绑定强校验**：删除 `ResultDetail` 属性必须同步删除 XAML 中该绑定，新增 `ResultOverlayVisible` / `ResultInlineVisible` 必须是 public 只读属性，否则构建直接失败。
- **不要动 `ScanSessionService` 的 `Message` 拼装**：`写入失败：xxx` 前缀仍被强制提交弹窗使用，改了会导致弹窗说明残缺。
- **不要动 `DoSubmitAsync` 前置守卫**（`!session.IsReady`、`IsBusy`、重复提交确认）：这些走 `StatusText` 与弹窗，不经结果条，保持原行为。
- **成功自动清空与自动隐藏共存**：`AutoResetAfterSuccess` 的 600ms 清槽位逻辑保留，结果浮层独立于清空动作，避免"成功提示一闪而过"的回归。
- **配色沿用既有色板**：成功 `#3B6D11`、失败 `#A32D2D`，不引入新色。
- 完成后执行 Debug 构建确认无 XAML 绑定与 CA 警告新增，并按需 `adb install -r bin/Debug/net10.0-android/com.lightgw.scanner-Signed.apk` 现场回归：带 MAC 成功、无 MAC 成功、失败（网关离线 / Redis 不可用强制提交）三种路径下预览高度不变。

