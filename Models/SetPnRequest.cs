using System.Text.Json.Serialization;

namespace LightGatewayScanner.Models;

/// <summary>SetPn 接口请求体：字段名严格小写 pn / pointId</summary>
public sealed class SetPnRequest
{
    [JsonPropertyName("pn")]
    public string Pn { get; set; } = string.Empty;

    [JsonPropertyName("pointId")]
    public string PointId { get; set; } = string.Empty;
}
