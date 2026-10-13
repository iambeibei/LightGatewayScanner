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

    Task<SubmitOutcome> SubmitAsync(bool force = false, CancellationToken cancellationToken = default);

    void Arm(ScanCodeType? type);

    void Reset();
}
