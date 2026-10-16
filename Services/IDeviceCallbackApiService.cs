using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// 上位机设备服务回调接口（API 模式下选老化架 / 点位用）。
/// </summary>
public interface IDeviceCallbackApiService
{
    /// <summary>
    /// 拉取老化架列表。请求体固定 {"pageSize":111110,"pageIndex":1}。
    /// </summary>
    Task<ApiListResult<AgingRackItem>> GetRackListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 拉取指定老化架下的可用点位列表，只保留 deviceId != "0" 的项。
    /// </summary>
    Task<ApiListResult<AgingPointItem>> GetAvailablePointListAsync(string agingRackId, CancellationToken cancellationToken = default);
}
