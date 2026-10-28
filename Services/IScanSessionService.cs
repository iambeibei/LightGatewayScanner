using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

public interface IScanSessionService
{
    ScanSession Current { get; }
    bool IsBusy { get; }

    /// <summary>手动指定下一个扫码要填入的槽位（null = 自动分类）</summary>
    ScanCodeType? ArmedSlot { get; }

    /// <summary>提交前用于"与上一条相同"的提示判断</summary>
    string LastSubmittedKey { get; }

    ScanAcceptOutcome Accept(string rawCode);

    /// <summary>
    /// API 模式：直接把网关 ID 写入会话（例如从点位下拉框选中的 deviceId）。
    /// 返回 false 表示内容为空、未写入。
    /// </summary>
    bool SetGatewayId(string gatewayId);

    /// <summary>API 模式：切换老化架后旧网关 ID 失效，清掉它</summary>
    void ClearGatewayId();

    /// <summary>
    /// 提交：Redis 在线校验 → （可选）扫描 BLE 取 MAC → MQTT 发布并等回执。
    /// <paramref name="progress"/> 用于把"正在扫描蓝牙…"等阶段状态推给界面。
    /// </summary>
    Task<SubmitOutcome> SubmitAsync(bool force = false, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    void Arm(ScanCodeType? type);

    void Reset();
}
