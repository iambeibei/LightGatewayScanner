namespace LightGatewayScanner.Models;

/// <summary>
/// 老化架点位（DeviceServiceCallback/AvailablePointList 返回的 data 元素）。
/// <see cref="DeviceId"/> 就是网关 ID：选中某一项时把它填到网关 ID 输入框。
/// </summary>
public sealed class AgingPointItem
{
    public string Id { get; set; } = string.Empty;

    public string? PointSequence { get; set; }

    public string? LayerNumber { get; set; }

    public string? AgingRackId { get; set; }

    /// <summary>设备（网关）ID，"0" 表示该点位没有设备，需要过滤掉</summary>
    public string DeviceId { get; set; } = "0";

    public int DeviceCount { get; set; }

    /// <summary>下拉框显示文本：层号 + 点位序号</summary>
    public string Display => $"层 {LayerNumber ?? "?"} · 点位 {PointSequence ?? "?"}";
}
