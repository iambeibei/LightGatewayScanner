namespace LightGatewayScanner.Models;

/// <summary>
/// 一次"网关 ID + 储能 PN"配对会话。两个字段都非空时才允许提交。
/// </summary>
public sealed class ScanSession
{
    public string? GatewayId { get; set; }
    public string? Pn { get; set; }

    public DateTime? GatewayScannedAt { get; set; }
    public DateTime? PnScannedAt { get; set; }

    public bool HasGateway => !string.IsNullOrWhiteSpace(GatewayId);
    public bool HasPn => !string.IsNullOrWhiteSpace(Pn);
    public bool IsReady => HasGateway && HasPn;

    public void Clear()
    {
        GatewayId = null;
        Pn = null;
        GatewayScannedAt = null;
        PnScannedAt = null;
    }
}
