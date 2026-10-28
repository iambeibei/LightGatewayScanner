using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LightGatewayScanner.Models;
using OperationCanceledException = System.OperationCanceledException;
#if ANDROID
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.Runtime;
using Microsoft.Maui.ApplicationModel;
#endif

namespace LightGatewayScanner.Services;

/// <summary>
/// BLE 扫描实现（Android 原生 BluetoothLeScanner，不依赖第三方包）。
/// 约定：储能设备的 BLE 广播名就是它的 PN（与网关固件 ble_connect_by_name 一致）。
/// 说明：Android 的 ScanFilter 不支持 RSSI，且本项目的设备名可能出现在扫描响应里，
///       因此不做硬件过滤，改为软件 RSSI 门限 + MAC 缓存 + 扫描节流。
/// </summary>
public sealed class BleScanner : IBleScanner
{
    /// <summary>首次命中后再等一小会儿：只有 1 台就提前返回，≥2 台则继续扫到窗口结束取最强</summary>
    private const int StabilizeMs = 600;

    /// <summary>Android 7+ 每 App 30 秒最多 5 次 startScan，本地提前拦一道</summary>
    private const int ScanWindowMs = 30_000;
    private const int ScanWindowLimit = 5;

    private const int MacCacheMaxEntries = 100;

    /// <summary>ScanFailure 中 API 33 才加入的错误码，用数值比较避免 CA1416（minSdk 23）</summary>
    private const int ScanFailedOutOfHardwareResources = 5;
    private const int ScanFailedScanningTooFrequently = 6;

    private readonly IAppLogService _logService;
    private readonly IAppConfigService _configService;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private readonly object _slotLock = new();
    private readonly Queue<long> _scanStarts = new();

    private readonly ConcurrentDictionary<string, CacheEntry> _macCache =
        new(StringComparer.OrdinalIgnoreCase);

    private int _permissionAsked;

#if ANDROID
    /// <summary>必须用字段持有回调，否则 Java 侧对象被 GC 后不再回调（甚至原生崩溃）</summary>
    private ScanCallback? _activeCallback;
#endif

    public BleScanner(IAppLogService logService, IAppConfigService configService)
    {
        _logService = logService;
        _configService = configService;
    }

    // ---------- 权限 ----------

    public async Task<string?> EnsurePermissionsAsync(bool forceRequest = false)
    {
#if ANDROID
        try
        {
            var status = await MainThread.InvokeOnMainThreadAsync(
                () => Permissions.CheckStatusAsync<BleScanPermission>());

            var shouldRequest = forceRequest || Interlocked.CompareExchange(ref _permissionAsked, 1, 0) == 0;
            if (status != PermissionStatus.Granted && shouldRequest)
            {
                status = await MainThread.InvokeOnMainThreadAsync(
                    () => Permissions.RequestAsync<BleScanPermission>());
            }

            return status == PermissionStatus.Granted
                ? null
                : "未获得蓝牙扫描权限，本次未读取 MAC（可在系统设置 → 权限 → 附近的设备 中开启）";
        }
        catch (Exception ex)
        {
            return $"申请蓝牙权限失败：{ex.Message}";
        }
#else
        await Task.CompletedTask;
        return "当前平台不支持蓝牙扫描";
#endif
    }

    // ---------- 按 PN 找 MAC ----------

    public async Task<BleScanResult> FindByAdvertisedNameAsync(
        string targetName,
        int timeoutSeconds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        targetName = (targetName ?? string.Empty).Trim();
        if (targetName.Length == 0) return BleScanResult.Skipped("PN 为空，未扫描蓝牙");

        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 15);

#if ANDROID
        var config = _configService.Current;
        var minRssi = config.BleMinRssi;
        var cacheSeconds = Math.Max(0, config.BleMacCacheSeconds);

        // 缓存优先：命中时不消耗扫描配额，也不要求蓝牙可用（网关侧不依赖手机蓝牙）
        if (cacheSeconds > 0 && TryGetCached(targetName, cacheSeconds, out var cached))
        {
            await _logService.WriteAsync($"BLE 命中缓存 pn={targetName} mac={cached!.Mac} ttl={cacheSeconds}s");
            return cached;
        }

        var (scanner, failure) = await PrepareScannerAsync(forcePermission: false);
        if (failure is not null) return failure;
        if (scanner is null) return BleScanResult.Miss(BleScanStatus.NotSupported, "蓝牙扫描器不可用，未读取 MAC");

        if (!await _scanGate.WaitAsync(0, cancellationToken))
            return BleScanResult.Miss(BleScanStatus.Busy, "上一次蓝牙扫描尚未结束，本次跳过");

        var stopwatch = Stopwatch.StartNew();
        var callback = new NameMatchCallback(targetName, minRssi, progress);
        _activeCallback = callback;

        try
        {
            if (!TryReserveScanSlot(out var retryAfterMs))
            {
                var waitSeconds = Math.Max(1, retryAfterMs / 1000);
                return BleScanResult.Miss(
                    BleScanStatus.Throttled,
                    $"蓝牙扫描太频繁（30 秒内已扫 {ScanWindowLimit} 次），本次跳过，请 {waitSeconds} 秒后再试",
                    stopwatch.ElapsedMilliseconds);
            }

            progress?.Report($"正在扫描蓝牙（最多 {timeoutSeconds} 秒）…");
            StartScan(scanner, callback);

            try
            {
                var hit = await callback.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
                var result = BleScanResult.Hit(hit.Mac, hit.Name, hit.Rssi, callback.Count, stopwatch.ElapsedMilliseconds);
                StoreCache(targetName, result, cacheSeconds);
                return result;
            }
            catch (TimeoutException)
            {
                // 命中 ≥2 台时不会提前完成，走到这里取最强；若窗内已命中但被稳定窗推过超时，同样取最强
                var best = callback.Best;
                if (best is not null)
                {
                    var result = BleScanResult.Hit(best.Mac, best.Name, best.Rssi, callback.Count, stopwatch.ElapsedMilliseconds);
                    StoreCache(targetName, result, cacheSeconds);
                    return result;
                }

                if (callback.RejectedCount > 0)
                {
                    return BleScanResult.Miss(
                        BleScanStatus.BelowRssiThreshold,
                        $"发现 {callback.RejectedCount} 台同名设备，信号最强 {callback.RejectedBest}dBm 低于门限 {minRssi}dBm，未下发 MAC",
                        stopwatch.ElapsedMilliseconds);
                }

                return BleScanResult.Miss(
                    BleScanStatus.Timeout,
                    $"扫描 {timeoutSeconds}s 未发现广播名称为 {targetName} 的设备",
                    stopwatch.ElapsedMilliseconds,
                    callback.Count);
            }
            catch (BleScanFailedException ex)
            {
                await _logService.WriteAsync($"BLE 扫描失败 pn={targetName} code={ex.Code}");
                var status = (int)ex.Code == ScanFailedScanningTooFrequently
                    ? BleScanStatus.Throttled
                    : BleScanStatus.Error;
                return BleScanResult.Miss(status, ex.Message, stopwatch.ElapsedMilliseconds, callback.Count);
            }
            catch (OperationCanceledException)
            {
                return BleScanResult.Miss(BleScanStatus.Canceled, "蓝牙扫描已取消", stopwatch.ElapsedMilliseconds, callback.Count);
            }
            catch (Exception ex)
            {
                await _logService.WriteAsync($"BLE 扫描异常 pn={targetName}", ex);
                return BleScanResult.Miss(BleScanStatus.Error, $"蓝牙扫描失败：{ex.Message}", stopwatch.ElapsedMilliseconds, callback.Count);
            }
        }
        finally
        {
            try { scanner.StopScan(callback); } catch { /* 已停止 / 权限被撤销 */ }
            _activeCallback = null;
            _scanGate.Release();
        }
#else
        await Task.CompletedTask;
        return BleScanResult.Miss(BleScanStatus.NotSupported, "当前平台不支持蓝牙扫描");
#endif
    }

    // ---------- 设置页：列出附近设备（取证） ----------

    public async Task<BleNearbyResult> ScanNearbyAsync(int durationSeconds, CancellationToken cancellationToken = default)
    {
        durationSeconds = Math.Clamp(durationSeconds, 1, 15);

#if ANDROID
        var (scanner, failure) = await PrepareScannerAsync(forcePermission: true);
        if (failure is not null) return BleNearbyResult.Fail(failure.Reason ?? "蓝牙不可用");
        if (scanner is null) return BleNearbyResult.Fail("蓝牙扫描器不可用");

        if (!await _scanGate.WaitAsync(0, cancellationToken))
            return BleNearbyResult.Fail("上一次蓝牙扫描尚未结束，请稍后再试");

        var callback = new CollectCallback();
        _activeCallback = callback;

        try
        {
            if (!TryReserveScanSlot(out var retryAfterMs))
                return BleNearbyResult.Fail($"扫描太频繁（30 秒内已扫 {ScanWindowLimit} 次），请 {Math.Max(1, retryAfterMs / 1000)} 秒后再试");

            StartScan(scanner, callback);
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellationToken);
            return BleNearbyResult.Ok(callback.Snapshot());
        }
        catch (OperationCanceledException)
        {
            return BleNearbyResult.Ok(callback.Snapshot());
        }
        catch (Exception ex)
        {
            await _logService.WriteAsync("BLE 附近设备扫描失败", ex);
            return BleNearbyResult.Fail($"蓝牙扫描失败：{ex.Message}");
        }
        finally
        {
            try { scanner.StopScan(callback); } catch { /* 忽略 */ }
            _activeCallback = null;
            _scanGate.Release();
        }
#else
        await Task.CompletedTask;
        return BleNearbyResult.Fail("当前平台不支持蓝牙扫描");
#endif
    }

    // ---------- MAC 缓存 ----------

    public void InvalidateCache(string pn)
    {
        if (string.IsNullOrWhiteSpace(pn)) return;
        _macCache.TryRemove(pn.Trim(), out _);
    }

    private bool TryGetCached(string pn, int ttlSeconds, out BleScanResult? result)
    {
        result = null;
        if (!_macCache.TryGetValue(pn, out var entry)) return false;

        if (Environment.TickCount64 - entry.AtTick > ttlSeconds * 1000L)
        {
            _macCache.TryRemove(pn, out _);
            return false;
        }

        result = BleScanResult.Hit(entry.Mac, entry.Name, entry.Rssi, entry.Count, 0, fromCache: true);
        return true;
    }

    private void StoreCache(string pn, BleScanResult hit, int cacheSeconds)
    {
        if (cacheSeconds <= 0 || hit.Mac is null) return;

        if (_macCache.Count >= MacCacheMaxEntries) PruneCache();

        _macCache[pn] = new CacheEntry(hit.Mac, hit.DeviceName ?? pn, hit.Rssi, hit.DeviceCount, Environment.TickCount64);
    }

    private void PruneCache()
    {
        var ttlMs = Math.Max(0, _configService.Current.BleMacCacheSeconds) * 1000L;
        var now = Environment.TickCount64;

        foreach (var pair in _macCache)
        {
            if (now - pair.Value.AtTick > ttlMs) _macCache.TryRemove(pair.Key, out _);
        }

        if (_macCache.Count < MacCacheMaxEntries) return;

        foreach (var pair in _macCache.OrderBy(x => x.Value.AtTick).Take(_macCache.Count - MacCacheMaxEntries + 1))
            _macCache.TryRemove(pair.Key, out _);
    }

    // ---------- 扫描节流 ----------

    /// <summary>滑动窗口预占一个扫描名额；失败时返回还需等待的毫秒数</summary>
    private bool TryReserveScanSlot(out int retryAfterMs)
    {
        retryAfterMs = 0;

        lock (_slotLock)
        {
            var now = Environment.TickCount64;
            while (_scanStarts.Count > 0 && now - _scanStarts.Peek() >= ScanWindowMs)
                _scanStarts.Dequeue();

            if (_scanStarts.Count >= ScanWindowLimit)
            {
                retryAfterMs = (int)(ScanWindowMs - (now - _scanStarts.Peek()));
                return false;
            }

            _scanStarts.Enqueue(now);
            return true;
        }
    }

    // ---------- Android 细节 ----------

#if ANDROID
    /// <summary>权限 → BLE 硬件 → 蓝牙开关 → 定位服务 → 扫描器，逐项检查</summary>
    private async Task<(BluetoothLeScanner? Scanner, BleScanResult? Failure)> PrepareScannerAsync(bool forcePermission)
    {
        var permissionError = await EnsurePermissionsAsync(forcePermission);
        if (permissionError is not null)
            return (null, BleScanResult.Miss(BleScanStatus.PermissionDenied, permissionError));

        var context = Platform.AppContext;

        if (context.PackageManager?.HasSystemFeature(PackageManager.FeatureBluetoothLe) != true)
            return (null, BleScanResult.Miss(BleScanStatus.NotSupported, "本机不支持蓝牙 BLE，未读取 MAC"));

        var adapter = ((BluetoothManager?)context.GetSystemService(Context.BluetoothService))?.Adapter;
        if (adapter is null)
            return (null, BleScanResult.Miss(BleScanStatus.NotSupported, "未找到蓝牙适配器，未读取 MAC"));

        bool enabled;
        try
        {
            enabled = adapter.IsEnabled;   // Android 12+ 缺 BLUETOOTH_CONNECT 会抛 SecurityException
        }
        catch (Exception)
        {
            return (null, BleScanResult.Miss(BleScanStatus.PermissionDenied, "缺少蓝牙权限，未读取 MAC"));
        }

        if (!enabled)
            return (null, BleScanResult.Miss(BleScanStatus.BluetoothOff, "蓝牙未开启，未读取 MAC"));

        // Android 11 及以下：扫描结果强制要求定位权限 + 系统定位服务开启
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) && !IsLocationServiceOn())
            return (null, BleScanResult.Miss(
                BleScanStatus.LocationDisabled,
                "Android 11 及以下扫描蓝牙需开启系统定位服务（设置 → 位置信息）"));

        var scanner = adapter.BluetoothLeScanner;
        return scanner is null
            ? (null, BleScanResult.Miss(BleScanStatus.NotSupported, "蓝牙扫描器不可用，未读取 MAC"))
            : (scanner, null);
    }

    private static void StartScan(BluetoothLeScanner scanner, ScanCallback callback)
    {
        // 逐个赋值而不是链式调用：绑定里的 SetXxx 返回值可空，链式会触发 CS8602
        var builder = new ScanSettings.Builder();
        builder.SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency);   // 只扫几秒，用低延迟换命中率
        builder.SetCallbackType(ScanCallbackType.AllMatches);            // 不用"仅首个匹配"，避免被 ROM 吞掉
        builder.SetReportDelay(0);                                       // 0 = 逐条回调

        scanner.StartScan(null, builder.Build(), callback);
    }

    /// <summary>优先取 AD type 0x09（Complete Local Name），回退 ScanRecord.DeviceName</summary>
    private static string? ReadAdvertisedName(ScanResult result)
    {
        foreach (var structure in ParseAdStructures(result.ScanRecord?.GetBytes()))
        {
            if (structure.Type != 0x09 || structure.Data.Length == 0) continue;

            var text = Encoding.UTF8.GetString(structure.Data).Trim();
            if (text.Length > 0) return text;
        }

        var fallback = result.ScanRecord?.DeviceName;   // Android 已合并 adv + scan response
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private readonly record struct AdStructure(byte Type, byte[] Data);

    private static List<AdStructure> ParseAdStructures(byte[]? data)
    {
        var list = new List<AdStructure>();
        if (data is null || data.Length == 0) return list;

        for (var i = 0; i + 1 < data.Length;)
        {
            int length = data[i];
            if (length == 0 || i + 1 + length > data.Length) break;

            var payload = new byte[length - 1];
            Array.Copy(data, i + 2, payload, 0, length - 1);
            list.Add(new AdStructure(data[i + 1], payload));
            i += length + 1;
        }

        return list;
    }

    /// <summary>把原始广播包解析成可读摘要，用于现场取证（判断能否上 Service UUID / 厂商数据过滤）</summary>
    private static string BuildAdText(ScanResult result)
    {
        var bytes = result.ScanRecord?.GetBytes();
        var structures = ParseAdStructures(bytes);

        var lines = new List<string>();
        foreach (var structure in structures)
            lines.Add($"0x{structure.Type:X2} {DescribeAd(structure)}");

        if (lines.Count == 0) lines.Add("（无原始广播数据）");

        // GetBytes() 只回传最近一包（广播包或扫描响应之一），必须同时展示合并名，否则会误判
        lines.Add($"合并名：{result.ScanRecord?.DeviceName ?? "—"}");
        lines.Add($"原始 {bytes?.Length ?? 0}B：{Convert.ToHexString(bytes ?? Array.Empty<byte>())}");

        return string.Join("\n", lines);
    }

    private static string DescribeAd(AdStructure structure) => structure.Type switch
    {
        0x01 => $"Flags 0x{(structure.Data.Length > 0 ? structure.Data[0] : 0):X2}{DescribeFlags(structure.Data)}",
        0x02 => $"UUID16(部分) {FormatUuid16(structure.Data)}",
        0x03 => $"UUID16(完整) {FormatUuid16(structure.Data)}",
        0x06 => $"UUID128(部分) {FormatUuid128(structure.Data)}",
        0x07 => $"UUID128(完整) {FormatUuid128(structure.Data)}",
        0x08 => $"短名 \"{Encoding.UTF8.GetString(structure.Data)}\"",
        0x09 => $"名称 \"{Encoding.UTF8.GetString(structure.Data)}\"",
        0x0A => $"TxPower {(structure.Data.Length > 0 ? (sbyte)structure.Data[0] : 0)}dBm",
        0x16 => $"SvcData16 {FormatUuid16(structure.Data)}: {HexTail(structure.Data, 2)}",
        0xFF => $"厂商 0x{(structure.Data.Length >= 2 ? (ushort)(structure.Data[0] | (structure.Data[1] << 8)) : 0):X4} {HexTail(structure.Data, 2)}",
        _ => Convert.ToHexString(structure.Data)
    };

    private static string DescribeFlags(byte[] data)
    {
        if (data.Length == 0) return string.Empty;

        var flags = data[0];
        var notes = new List<string>();
        if ((flags & 0x02) != 0) notes.Add("通用可发现");
        if ((flags & 0x04) != 0) notes.Add("不支持经典蓝牙");
        if ((flags & 0x10) != 0) notes.Add("仅 BLE");

        return notes.Count == 0 ? string.Empty : "（" + string.Join(" / ", notes) + "）";
    }

    private static string FormatUuid16(byte[] data)
    {
        var parts = new List<string>();
        for (var i = 0; i + 1 < data.Length; i += 2)
            parts.Add($"0x{(ushort)(data[i] | (data[i + 1] << 8)):X4}");

        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    private static string FormatUuid128(byte[] data)
    {
        if (data.Length < 16) return Convert.ToHexString(data);

        var hex = Convert.ToHexString(data.Take(16).Reverse().ToArray());
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    private static string HexTail(byte[] data, int skip)
    {
        if (data.Length <= skip) return string.Empty;

        var hex = Convert.ToHexString(data.AsSpan(skip));
        return hex.Length <= 40 ? hex : hex[..40] + "…";
    }

    private static bool IsLocationServiceOn()
    {
        try
        {
            var manager = (LocationManager?)Platform.AppContext.GetSystemService(Context.LocationService);
            if (manager is null) return false;

            return OperatingSystem.IsAndroidVersionAtLeast(28)
                ? manager.IsLocationEnabled
                : manager.IsProviderEnabled(LocationManager.GpsProvider)
                  || manager.IsProviderEnabled(LocationManager.NetworkProvider);
        }
        catch
        {
            return true;   // 判断失败不阻断流程，让扫描自己给结果
        }
    }

    /// <summary>按广播名精确匹配（忽略大小写）；RSSI 门限只影响候选集</summary>
    private sealed class NameMatchCallback : ScanCallback
    {
        private readonly string _target;
        private readonly int _minRssi;
        private readonly IProgress<string>? _progress;
        private readonly ConcurrentDictionary<string, BleDeviceInfo> _hits = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _rejected = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<BleDeviceInfo> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _stabilizeStarted;

        public NameMatchCallback(string target, int minRssi, IProgress<string>? progress)
        {
            _target = target;
            _minRssi = minRssi;
            _progress = progress;
        }

        public Task<BleDeviceInfo> Task => _tcs.Task;

        public int Count => _hits.Count;

        public int RejectedCount => _rejected.Count;

        public int RejectedBest => _rejected.IsEmpty ? 0 : _rejected.Values.Max();

        public BleDeviceInfo? Best
        {
            get
            {
                BleDeviceInfo? best = null;
                foreach (var device in _hits.Values)
                {
                    if (best is null || device.Rssi > best.Rssi) best = device;
                }
                return best;
            }
        }

        public override void OnScanResult([GeneratedEnum] ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is null) return;

            var name = ReadAdvertisedName(result);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!string.Equals(name, _target, StringComparison.OrdinalIgnoreCase)) return;

            var mac = MqttReplyParser.NormalizeMac(result.Device.Address);
            if (mac is null) return;   // MAC 格式异常直接丢弃，绝不发出空 MAC

            // RSSI 门限：低于门限的同名设备不进候选，单独记账用于提示
            if (_minRssi != 0 && result.Rssi < _minRssi)
            {
                _rejected[mac] = result.Rssi;
                return;
            }

            var before = _hits.Count;
            _hits[mac] = new BleDeviceInfo(name, mac, result.Rssi);

            if (_hits.Count != before)
            {
                _progress?.Report(_hits.Count == 1
                    ? $"已发现设备 {name}，正在确认…"
                    : $"已发现 {_hits.Count} 台同名设备，正在选信号最强的…");
            }

            ArmStabilize();
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            if (results is null) return;
            foreach (var item in results) OnScanResult(ScanCallbackType.AllMatches, item);
        }

        public override void OnScanFailed([GeneratedEnum] ScanFailure errorCode)
            => _tcs.TrySetException(new BleScanFailedException(errorCode));

        /// <summary>
        /// 首次命中后 600ms 检查一次：只有 1 台才提前完成；
        /// ≥2 台不完成，交给外层超时分支取最强。
        /// </summary>
        private void ArmStabilize()
        {
            if (Interlocked.CompareExchange(ref _stabilizeStarted, 1, 0) != 0) return;

            _ = System.Threading.Tasks.Task.Delay(StabilizeMs).ContinueWith(_ =>
            {
                if (_hits.Count != 1) return;

                var best = Best;
                if (best is not null) _tcs.TrySetResult(best);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>收集所有带名称的广播设备（设置页排查/取证用，不做任何过滤）</summary>
    private sealed class CollectCallback : ScanCallback
    {
        private readonly ConcurrentDictionary<string, BleDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);

        public override void OnScanResult([GeneratedEnum] ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is null) return;

            var name = ReadAdvertisedName(result);
            if (string.IsNullOrWhiteSpace(name)) return;

            var mac = MqttReplyParser.NormalizeMac(result.Device.Address);
            if (mac is null) return;

            var connectable = OperatingSystem.IsAndroidVersionAtLeast(26) ? result.IsConnectable : (bool?)null;
            _devices[mac] = new BleDeviceInfo(name, mac, result.Rssi, BuildAdText(result), connectable);
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            if (results is null) return;
            foreach (var item in results) OnScanResult(ScanCallbackType.AllMatches, item);
        }

        public IReadOnlyList<BleDeviceInfo> Snapshot()
            => _devices.Values.OrderByDescending(d => d.Rssi).ToArray();
    }

    /// <summary>把 ScanFailure 映射成中文原因</summary>
    private sealed class BleScanFailedException : Exception
    {
        public BleScanFailedException(ScanFailure code) : base(Describe(code)) => Code = code;

        public ScanFailure Code { get; }

        private static string Describe(ScanFailure code) => code switch
        {
            ScanFailure.AlreadyStarted => "蓝牙扫描已在运行（系统认为上一次未停止），请稍后重试",
            ScanFailure.ApplicationRegistrationFailed => "系统拒绝注册蓝牙扫描回调，请关闭再打开蓝牙后重试",
            ScanFailure.InternalError => "蓝牙协议栈内部错误，请关闭再打开蓝牙后重试",
            ScanFailure.FeatureUnsupported => "本机蓝牙不支持 BLE 扫描",
            // 下面两个错误码是 API 33 才加入的枚举成员，直接引用会触发 CA1416，这里按数值比较
            _ when (int)code == ScanFailedOutOfHardwareResources =>
                "蓝牙芯片资源不足，请稍后重试（可关闭其他占用蓝牙的 APP）",
            _ when (int)code == ScanFailedScanningTooFrequently =>
                "扫描太频繁（系统限制每 30 秒最多 5 次），请等几秒再提交",
            _ => $"蓝牙扫描失败（错误码 {(int)code}）"
        };
    }
#endif

    private readonly record struct CacheEntry(string Mac, string Name, int Rssi, int Count, long AtTick);
}
