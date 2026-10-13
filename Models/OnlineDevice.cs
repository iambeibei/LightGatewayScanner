using System.Text.Json.Serialization;

namespace LightGatewayScanner.Models;

/// <summary>
/// Redis 在线列表元素模型。
/// lgw:devices:online 是 List，每个元素是 JSON 字符串：{"id":"100000010","type":"1"}
/// 必须先反序列化取 id，不能拿元素字符串直接比对网关 ID。
/// </summary>
public sealed class OnlineDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
