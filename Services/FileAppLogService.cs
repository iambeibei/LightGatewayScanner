using Microsoft.Maui.Storage;

namespace LightGatewayScanner.Services;

/// <summary>
/// 本地文件日志。写入失败绝不允许打断扫码主流程（全部异常吞掉）。
/// </summary>
public sealed class FileAppLogService : IAppLogService
{
    private const int KeepDays = 7;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string LogDirectory { get; } = Path.Combine(FileSystem.AppDataDirectory, "logs");

    public async Task WriteAsync(string message, Exception? exception = null)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var file = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} | {message}";
            if (exception is not null)
                line += $" | EX: {exception.GetType().Name}: {exception.Message}";

            await File.AppendAllTextAsync(file, line + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响业务
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> ReadTodayAsync()
    {
        try
        {
            var file = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            return File.Exists(file) ? await File.ReadAllTextAsync(file) : "（今日暂无日志）";
        }
        catch
        {
            return "（日志读取失败）";
        }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            foreach (var file in Directory.GetFiles(LogDirectory, "app-*.log"))
            {
                try { File.Delete(file); } catch { /* 忽略 */ }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>清理超过保留天数的日志，在启动时调用一次</summary>
    public void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            var deadline = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < deadline) File.Delete(file);
                }
                catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }
}
