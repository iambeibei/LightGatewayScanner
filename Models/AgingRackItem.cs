namespace LightGatewayScanner.Models;

/// <summary>
/// 老化架（DeviceServiceCallback/RackPageList 返回的 data 元素）。
/// </summary>
public sealed class AgingRackItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Remark { get; set; }

    /// <summary>主控 ID（接口返回留着备用，暂不参与逻辑）</summary>
    public string? MasterControlId { get; set; }

    /// <summary>下拉框显示文本：名称（备注非空时附带）</summary>
    public string Display => string.IsNullOrWhiteSpace(Remark) ? Name : $"{Name}（{Remark}）";
}
