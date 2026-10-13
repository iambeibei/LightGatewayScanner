using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

public interface ISetPnApiService
{
    /// <summary>调用 SetPn 写入接口</summary>
    Task<ApiOperationResult> SetPnAsync(string gatewayId, string pn, CancellationToken cancellationToken = default);

    /// <summary>设置页"测试连接"：只做主机解析与端口连通探测，不会触发写入</summary>
    Task<string> ProbeAsync(CancellationToken cancellationToken = default);
}
