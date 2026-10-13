using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LightGatewayScanner.Models;

namespace LightGatewayScanner.Services;

/// <summary>
/// SetPn 接口调用。写接口不做自动重试（避免重复写入），失败由人工点"重试"。
/// </summary>
public sealed class SetPnApiService : ISetPnApiService, IDisposable
{
    private readonly IAppConfigService _configService;
    private readonly IAppLogService _logService;

    private HttpClient? _httpClient;
    private string _urlInUse = string.Empty;
    private int _timeoutInUse;
    private bool _allowInvalidCertInUse;

    public SetPnApiService(IAppConfigService configService, IAppLogService logService)
    {
        _configService = configService;
        _logService = logService;
        _configService.ConfigChanged += (_, _) => RebuildIfNeeded();
    }

    public async Task<ApiOperationResult> SetPnAsync(string gatewayId, string pn, CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;

        if (string.IsNullOrWhiteSpace(gatewayId) || string.IsNullOrWhiteSpace(pn))
            return ApiOperationResult.Fail("网关 ID 或储能 PN 为空，未发起请求");
        if (!Uri.TryCreate(config.SetPnUrl?.Trim(), UriKind.Absolute, out var uri))
            return ApiOperationResult.Fail($"接口地址不合法：{config.SetPnUrl}");

        var client = GetClient(config);
        var request = new SetPnRequest { Pn = pn.Trim(), PointId = gatewayId.Trim() };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.PostAsJsonAsync(uri, request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            var status = (int)response.StatusCode;
            var detail = string.IsNullOrWhiteSpace(body) ? "服务未返回内容" : Truncate(body, 300);

            // 先解析响应体，取服务端自己的业务结果与提示
            var reading = ReadResponse(body, config);

            if (!response.IsSuccessStatusCode)
            {
                var reason = status >= 500 ? "服务端错误" : "请求被拒绝";
                var text = reading.Message is not null
                    ? $"{reason} HTTP {status}：{reading.Message}"
                    : $"{reason} HTTP {status}：{detail}";

                var failed = ApiOperationResult.Fail(text, status, detail, stopwatch.ElapsedMilliseconds, reading.Message);
                await _logService.WriteAsync($"SetPn 失败 gateway={gatewayId} pn={pn} status={status} body={detail}");
                return failed;
            }

            // HTTP 2xx，但业务结果可能仍表示失败
            if (reading.Success == false)
            {
                var text = reading.Message is not null
                    ? $"写入失败：{reading.Message}"
                    : $"写入失败：业务返回 {reading.CodeText}";

                var businessFailed = ApiOperationResult.Fail(text, status, detail, stopwatch.ElapsedMilliseconds, reading.Message);
                await _logService.WriteAsync($"SetPn 业务失败 gateway={gatewayId} pn={pn} body={detail}");
                return businessFailed;
            }

            var okText = reading.Message is not null ? $"写入成功：{reading.Message}" : "写入成功";
            await _logService.WriteAsync($"SetPn 成功 gateway={gatewayId} pn={pn} 耗时={stopwatch.ElapsedMilliseconds}ms body={detail}");
            return ApiOperationResult.Ok(okText, status, detail, stopwatch.ElapsedMilliseconds, reading.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var msg = $"请求超时（>{config.TimeoutSeconds}s）";
            await _logService.WriteAsync($"SetPn 超时 gateway={gatewayId} pn={pn}");
            return ApiOperationResult.Fail(msg);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var msg = TranslateHttpException(ex, uri);
            await _logService.WriteAsync($"SetPn 网络失败 gateway={gatewayId} pn={pn}：{msg}", ex);
            return ApiOperationResult.Fail(msg);
        }
        catch (OperationCanceledException)
        {
            return ApiOperationResult.Fail("请求已取消");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _logService.WriteAsync($"SetPn 异常 gateway={gatewayId} pn={pn}", ex);
            return ApiOperationResult.Fail($"请求异常：{ex.Message}");
        }
    }

    public async Task<string> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Current;
        if (!Uri.TryCreate(config.SetPnUrl?.Trim(), UriKind.Absolute, out var uri))
            return $"接口地址不合法：{config.SetPnUrl}";

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80);

        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, port);
            await connectTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return $"可连通 {host}:{port}（{uri.Scheme}）。注意：仅做端口探测，未触发写入。";
        }
        catch (SocketException ex)
        {
            return $"无法连通 {host}:{port}，请确认已连接内网：{ex.SocketErrorCode}";
        }
        catch (TimeoutException)
        {
            return $"连接 {host}:{port} 超时，请检查网络";
        }
        catch (Exception ex)
        {
            return $"探测失败：{ex.Message}";
        }
    }

    // ---------- 响应解析 ----------

    private readonly record struct ResponseReading(bool? Success, string? Message, string? CodeText);

    /// <summary>
    /// 解析响应体里的业务结果。只依据明确信号判定，避免误判：
    ///  - success / ok 显式为 false（布尔或字符串）→ 失败；
    ///  - code / errCode 数值不在"成功业务码"集合内 → 失败；
    ///  其余一律不做负面判定（HTTP 状态码才是主判据），这样 code=200 不会被误判成失败。
    /// </summary>
    private static ResponseReading ReadResponse(string? body, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(body)) return new ResponseReading(null, null, null);

        var text = body.TrimStart();
        if (!text.StartsWith('{')) return new ResponseReading(null, null, null);

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var message = ReadMessage(root);
            var codeText = ReadCodeText(root);

            // 1) 先抓明确**正向**信号：success / ok / data 显式 true（任何一项为 true 即成功）
            //    这覆盖了"code 是步骤号不能当失败判据"这种接口
            var positive = ReadPositiveFlag(root);
            var negative = ReadNegativeFlag(root);

            if (negative) return new ResponseReading(false, message, codeText);
            if (positive == true) return new ResponseReading(true, message, codeText);

            // 2) 没有显式布尔信号时按 code 数值判定
            if (codeText is not null && int.TryParse(codeText, out var code))
            {
                return new ResponseReading(IsSuccessCode(code, config), message, codeText);
            }

            return new ResponseReading(null, message, codeText);
        }
        catch (JsonException)
        {
            return new ResponseReading(null, null, null);
        }
    }

    /// <summary>success / ok / data 显式 true（布尔或字符串）</summary>
    private static bool? ReadPositiveFlag(JsonElement root)
    {
        foreach (var name in new[] { "success", "ok", "succeeded", "data" })
        {
            if (!root.TryGetProperty(name, out var node)) continue;

            if (node.ValueKind == JsonValueKind.True) return true;
            if (node.ValueKind == JsonValueKind.False) continue; // 不能据此判成功
            if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return null;
    }

    /// <summary>success / ok 显式 false（布尔或字符串）</summary>
    private static bool ReadNegativeFlag(JsonElement root)
    {
        foreach (var name in new[] { "success", "ok", "succeeded" })
        {
            if (!root.TryGetProperty(name, out var node)) continue;

            if (node.ValueKind == JsonValueKind.False) return true;
            if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "fail", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSuccessCode(int code, AppConfig config)
    {
        var configured = (config.ApiSuccessCodes ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToArray();

        foreach (var item in configured)
        {
            if (int.TryParse(item, out var success) && code == success) return true;
        }

        // 未配置时按最常见约定：0 / 1 / 200 视为成功（覆盖 {code:0}、{code:1,"message":"操作成功"} 等国内常见接口）
        return configured.Length == 0 && (code == 0 || code == 1 || code == 200);
    }

    private static string? ReadCodeText(JsonElement root)
    {
        foreach (var name in new[] { "code", "errcode", "errCode", "errorCode", "statusCode", "retCode" })
        {
            if (!root.TryGetProperty(name, out var node)) continue;

            return node.ValueKind switch
            {
                JsonValueKind.Number => node.TryGetInt32(out var number) ? number.ToString() : node.GetRawText(),
                JsonValueKind.String => node.GetString(),
                _ => null
            };
        }
        return null;
    }

    private static string? ReadMessage(JsonElement root)
    {
        foreach (var name in new[] { "message", "msg", "error", "errorMessage", "resultMsg", "info" })
        {
            if (root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
            }
        }
        return null;
    }

    // ---------- 内部实现 ----------

    private static string TranslateHttpException(HttpRequestException ex, Uri uri)
    {
        var text = $"{ex.Message} {ex.InnerException?.Message}";

        if (text.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("trust", StringComparison.OrdinalIgnoreCase))
            return "证书校验失败，请在设置中开启『信任自签证书』或在手机上安装服务器证书";

        if (text.Contains("NameResolution", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return $"无法解析主机 {uri.Host}，请确认已连接内网 WiFi";

        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return $"连接被拒绝（{uri.Host}:{uri.Port}），服务可能未启动";

        if (text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
            return "网络不可达，请检查手机网络与内网路由";

        return $"网络请求失败：{ex.Message}";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private HttpClient GetClient(AppConfig config)
    {
        RebuildIfNeeded();
        return _httpClient!;
    }

    private void RebuildIfNeeded()
    {
        var config = _configService.Current;
        var url = config.SetPnUrl?.Trim() ?? string.Empty;
        var timeout = Math.Clamp(config.TimeoutSeconds, 1, 120);

        if (_httpClient is not null
            && _urlInUse == url
            && _timeoutInUse == timeout
            && _allowInvalidCertInUse == config.AllowInvalidCertificate)
            return;

        _httpClient?.Dispose();

        var handler = new HttpClientHandler();
        if (config.AllowInvalidCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
        _urlInUse = url;
        _timeoutInUse = timeout;
        _allowInvalidCertInUse = config.AllowInvalidCertificate;
    }

    public void Dispose() => _httpClient?.Dispose();
}
