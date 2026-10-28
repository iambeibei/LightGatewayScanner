using System.Text.Json;
using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>网关回执解析结果</summary>
public readonly record struct SetPnReply(
    long CmdSeq,
    int Code,
    string? Msg,
    string? GetPn,
    string TopicGatewayId,
    long? TopicSeq,
    string RawPayload);

/// <summary>
/// MQTT 报文构造与回执解析（纯函数，无 I/O，便于单独验证）。
/// 命令：{"Seq":1782373476,"Data":{"PN":"PN123"}}
/// 回执：{"Seq":1,"Data":{"CmdSeq":1782373476,"Code":1,"Msg":null,"CmdData":{"GetPN":"PN123"}}}
/// </summary>
public static class MqttReplyParser
{
    public const string ReplyTopicFilter = "device/+/cmd/reply/pn";

    /// <summary>
    /// 拼命令 payload。mac 为 null（或格式非法）时输出 {"Seq":..,"Data":{"PN":".."}}，
    /// 即 MAC / AddrType 两个键都不出现；有 MAC 时 AddrType 固定 0。
    /// </summary>
    public static string BuildCommandPayload(long seq, string pn, string? mac = null)
    {
        var normalized = NormalizeMac(mac);

        return normalized is null
            ? JsonSerializer.Serialize(new { Seq = seq, Data = new { PN = pn } })
            : JsonSerializer.Serialize(new { Seq = seq, Data = new { PN = pn, MAC = normalized, AddrType = 0 } });
    }

    /// <summary>
    /// MAC 归一化："C7:2A:18:32:34:D3" / "c7-2a-18-32-34-d3" / "c72a183234d3"
    /// → "c7:2a:18:32:34:d3"；非法（不足 12 位十六进制）返回 null。
    /// </summary>
    public static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12) return null;

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>拼发布主题 server/{网关ID}/cmd/set_pn</summary>
    public static string BuildCommandTopic(string gatewayId) => $"server/{gatewayId}/cmd/set_pn";

    /// <summary>拼回执主题 device/{网关ID}/cmd/reply/pn（用于错误提示与订阅校验）</summary>
    public static string BuildReplyTopic(string gatewayId) => $"device/{gatewayId}/cmd/reply/pn";

    /// <summary>网关 ID 会拼进 MQTT 主题，含 / + # 会导致主题错乱，直接拒绝</summary>
    public static bool IsValidGatewayIdForTopic(string? gatewayId)
        => !string.IsNullOrWhiteSpace(gatewayId)
           && gatewayId.IndexOfAny(new[] { '/', '+', '#' }) < 0;

    public static bool TryParse(string? topic, string? payload, out SetPnReply reply, out string? error)
    {
        reply = default;
        error = null;

        if (string.IsNullOrWhiteSpace(topic))
        {
            error = "主题为空";
            return false;
        }

        var segments = topic.Split('/');
        if (segments.Length != 5
            || !string.Equals(segments[0], "device", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "cmd", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[3], "reply", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[4], "pn", StringComparison.OrdinalIgnoreCase))
        {
            error = $"主题格式不符：{topic}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "回执内容为空";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "回执不是 JSON 对象";
                return false;
            }

            // 顶层 Seq 只用于日志对照，缺失不影响匹配
            var topicSeq = TryGet(root, "Seq", out var seqNode) ? ReadInt64(seqNode) : null;

            if (!TryGet(root, "Data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                error = "回执缺少 Data 对象";
                return false;
            }

            if (!TryGet(data, "CmdSeq", out var cmdSeqNode) || ReadInt64(cmdSeqNode) is not long cmdSeq)
            {
                error = "回执缺少 Data.CmdSeq";
                return false;
            }

            var code = TryGet(data, "Code", out var codeNode) && ReadInt32(codeNode) is int c ? c : 0;
            var msg = TryGet(data, "Msg", out var msgNode) ? ReadString(msgNode) : null;

            string? getPn = null;
            if (TryGet(data, "CmdData", out var cmdData) && cmdData.ValueKind == JsonValueKind.Object
                && TryGet(cmdData, "GetPN", out var pnNode))
            {
                getPn = ReadString(pnNode);
            }

            reply = new SetPnReply(cmdSeq, code, msg, getPn, segments[1], topicSeq, payload);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"回执 JSON 解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 回执 → 结果。Code 必须为 1；若回执带回 GetPN，则与提交值比对，不一致视为失败（防止写错）。
    /// </summary>
    public static SetPnResult ToResult(SetPnReply reply, string expectedPn)
    {
        var raw = Truncate(reply.RawPayload, 300);

        if (reply.Code != 1)
        {
            var text = string.IsNullOrWhiteSpace(reply.Msg)
                ? $"网关返回失败（Code={reply.Code}）"
                : $"网关返回失败（Code={reply.Code}）：{reply.Msg}";
            return SetPnResult.Fail(text, reply.CmdSeq, reply.Code, reply.Msg, raw);
        }

        if (!string.IsNullOrWhiteSpace(reply.GetPn)
            && !string.Equals(reply.GetPn.Trim(), expectedPn.Trim(), StringComparison.Ordinal))
        {
            return SetPnResult.Fail(
                $"回执 PN 与提交不一致（提交 {expectedPn}，回执 {reply.GetPn}）",
                reply.CmdSeq, reply.Code, reply.Msg, raw);
        }

        var message = string.IsNullOrWhiteSpace(reply.GetPn)
            ? "网关已确认写入（回执未包含 PN）"
            : "网关已确认写入";

        return SetPnResult.Ok(message, reply.CmdSeq, reply.Code, reply.Msg, raw, 0);
    }

    // ---------- JSON 读取辅助（属性名忽略大小写：网关可能发 cmdSeq / code） ----------

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.String => node.GetString()?.Trim(),
        JsonValueKind.Number => node.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static long? ReadInt64(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Number => node.TryGetInt64(out var value) ? value : null,
        JsonValueKind.String => long.TryParse(node.GetString(), out var parsed) ? parsed : null,
        _ => null
    };

    private static int? ReadInt32(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Number => node.TryGetInt32(out var value) ? value : null,
        JsonValueKind.String => int.TryParse(node.GetString(), out var parsed) ? parsed : null,
        _ => null
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
