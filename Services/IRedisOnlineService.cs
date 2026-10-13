using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

public interface IRedisOnlineService
{
    /// <summary>判断网关是否在线</summary>
    Task<OnlineCheckResult> CheckAsync(string gatewayId, CancellationToken cancellationToken = default);

    /// <summary>设置页"测试连接"：连通性 + 在线数量 + 当前前缀匹配数量</summary>
    Task<RedisTestResult> TestAsync(CancellationToken cancellationToken = default);

    void InvalidateCache();

    /// <summary>配置变更后重建连接</summary>
    void ResetConnection();
}
