using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// 通过 MQTT 设置 PN：向 server/{网关ID}/cmd/set_pn 发布命令，等待 device/{网关ID}/cmd/reply/pn 回执。
/// </summary>
public interface IMqttSetPnService
{
    /// <summary>常驻长连接当前是否已连上 Broker</summary>
    bool IsConnected { get; }

    /// <summary>当前配置的 Broker 地址，形如 192.168.1.65:1883</summary>
    string Endpoint { get; }

    /// <summary>连接状态变化（已连接 / 断开 / 重连成功）</summary>
    event EventHandler? StateChanged;

    /// <summary>启动后后台连一次，不阻塞 UI</summary>
    void Start();

    /// <summary>
    /// 发布设置 PN 命令并等待回执（超时由配置的 MqttReplyTimeoutSeconds 控制）。
    /// <paramref name="deviceMac"/> 非空时会随 PN 一起下发（AddrType 固定 0）。
    /// </summary>
    Task<SetPnResult> SetPnAsync(string gatewayId, string pn, string? deviceMac = null,
        CancellationToken cancellationToken = default);

    /// <summary>设置页"测试 MQTT 连接"：用临时连接验证连通性，不会发送写入命令</summary>
    Task<string> ProbeAsync(string host, int port, string? username, string? password, CancellationToken cancellationToken = default);
}
