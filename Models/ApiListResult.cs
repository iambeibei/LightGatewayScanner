namespace LightGatewayScanner.Models;

/// <summary>
/// 列表类接口的统一返回结果（RackPageList / AvailablePointList 等）。
/// </summary>
public sealed class ApiListResult<T>
{
    private ApiListResult(bool success, string? message, IReadOnlyList<T> items, int total, int statusCode, string? rawResponse)
    {
        Success = success;
        Message = message;
        Items = items;
        Total = total;
        StatusCode = statusCode;
        RawResponse = rawResponse;
    }

    public bool Success { get; }

    public string? Message { get; }

    public IReadOnlyList<T> Items { get; } = Array.Empty<T>();

    /// <summary>服务端返回的 total（没有该字段时等于实际解析到的条数）</summary>
    public int Total { get; }

    public int StatusCode { get; }

    /// <summary>原始响应体（截断到 300 字，用于日志与展示）</summary>
    public string? RawResponse { get; }

    public static ApiListResult<T> Ok(IReadOnlyList<T> items, int total, int statusCode, string? rawResponse, string? message = null)
        => new(true, message, items, total, statusCode, rawResponse);

    public static ApiListResult<T> Fail(string message, int statusCode = 0, string? rawResponse = null)
        => new(false, message, Array.Empty<T>(), 0, statusCode, rawResponse);
}
