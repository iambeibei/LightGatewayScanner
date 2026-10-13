namespace LightGatewayScanner.Services;

public interface IAppLogService
{
    Task WriteAsync(string message, Exception? exception = null);
    string LogDirectory { get; }
    Task<string> ReadTodayAsync();
    Task ClearAsync();
}
