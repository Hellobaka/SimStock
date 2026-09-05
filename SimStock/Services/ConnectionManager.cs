using System.Text.Json;
using TdxProtocol;

namespace SimStock;

/// <summary>
/// TDX 连接管理。负责最佳服务器选择（每天一次缓存）、连接建立/断开。
/// </summary>
public class ConnectionManager : IDisposable
{
    private TdxClient? _client;
    private string? _bestIp;
    private int _bestPort;
    private DateTime _lastBestIpCheck;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _bestIpCachePath;
    private readonly HolidayClient _holidayClient;
    private string _holidayCacheDir;

    public ConnectionManager(string appDir)
    {
        _bestIpCachePath = Path.Combine(appDir, "bestip.json");
        _holidayCacheDir = appDir;
        _holidayClient = new HolidayClient { CacheDirectory = appDir };
    }

    public void SetHolidayCacheDirectory(string dir)
    {
        _holidayCacheDir = dir;
        _holidayClient.CacheDirectory = dir;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    /// <summary>
    /// 确保 TDX 已连接。如果断连会自动重连。
    /// 每天只检查一次最佳服务器。
    /// </summary>
    public async Task<TdxClient?> EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_client?.IsConnected == true)
            {
                return _client;
            }

            // 每天检查一次最佳服务器
            await RefreshBestIpIfNeededAsync();

            if (_bestIp == null)
            {
                Entry.Api.Logger.Warn("连接管理", "无法获取最佳服务器IP，行情服务不可用");
                return null;
            }

            // 重新连接
            _client?.Dispose();
            _client = new TdxClient();
            TdxClient.Logger = msg => Entry.Api.Logger.Debug("TDX", msg);
            _client.Connect(_bestIp, _bestPort);
            Entry.Api.Logger.Info("连接管理", $"已连接 TDX {_bestIp}:{_bestPort}");
            return _client;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("连接管理", $"TDX连接失败: {ex.Message}");
            _client?.Dispose();
            _client = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 行情请求失败后，丢弃失败连接并从其他候选服务器中重新选择、连接。
    /// 若其他请求已经完成了切换，则直接复用新的连接。
    /// </summary>
    public async Task<TdxClient?> ReconnectAfterQuoteFailureAsync(TdxClient failedClient, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // 并发请求中可能已有请求完成了服务器切换，无需再次测速或影响新连接。
            if (!ReferenceEquals(_client, failedClient))
            {
                return _client?.IsConnected == true ? _client : null;
            }

            var failedIp = failedClient.ConnectedIp ?? _bestIp;
            var failedPort = failedClient.ConnectedPort != 0 ? failedClient.ConnectedPort : _bestPort;

            _client?.Dispose();
            _client = null;
            _bestIp = null;
            _bestPort = 0;

            var candidates = BestIpFinder.HqServers
                .Where(server => !string.Equals(server.Ip, failedIp, StringComparison.OrdinalIgnoreCase)
                              || server.Port != failedPort)
                .ToArray();

            if (candidates.Length == 0)
            {
                Entry.Api.Logger.Warn("连接管理", "行情请求失败后没有可供切换的服务器");
                return null;
            }

            Entry.Api.Logger.Warn("连接管理", $"行情请求失败，忽略服务器 {failedIp}:{failedPort} 并重新选择服务器");
            TdxClient.Logger = msg => Entry.Api.Logger.Debug("TDX", msg);
            BestIpFinder.Log = msg => Entry.Api.Logger.Info("寻找最佳服务器", msg);

            var results = await BestIpFinder.BestIpAsync(top: 1, savePath: _bestIpCachePath, servers: candidates);
            if (results.Length == 0)
            {
                Entry.Api.Logger.Warn("连接管理", "重新选择服务器失败，未找到可用服务器");
                return null;
            }

            _bestIp = results[0].Server.Ip;
            _bestPort = results[0].Server.Port;
            _lastBestIpCheck = DateTime.Now;

            _client = new TdxClient();
            _client.Connect(_bestIp, _bestPort);
            Entry.Api.Logger.Info("连接管理", $"已切换 TDX 服务器至 {_bestIp}:{_bestPort}");
            return _client;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("连接管理", $"行情失败后的服务器切换失败: {ex.Message}");
            _client?.Dispose();
            _client = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 断开 TDX 连接。
    /// </summary>
    public void Disconnect()
    {
        _lock.Wait();
        try
        {
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsInTradingSessionAsync()
    {
        if (!TradingHoursChecker.IsInTradingSession())
        {
            return false;
        }

        return await _holidayClient.IsTradingDayAsync(DateTime.Now);
    }

    /// <summary>判断今天是否为交易日（不考虑具体时段，节假日返回 false）</summary>
    public async Task<bool> IsTradingDayAsync()
    {
        return await _holidayClient.IsTradingDayAsync(DateTime.Now);
    }

    public async Task RefreshBestIpAsync()
    {
        try
        {
            TdxClient.Logger = msg => Entry.Api.Logger.Debug("TDX", msg);
            BestIpFinder.Log = msg => Entry.Api.Logger.Info("寻找最佳服务器", msg);

            var results = await BestIpFinder.BestIpAsync(top: 1, savePath: _bestIpCachePath);
            if (results.Length > 0)
            {
                _bestIp = results[0].Server.Ip;
                _bestPort = results[0].Server.Port;
                _lastBestIpCheck = DateTime.Now;
            }
            else
            {
                Entry.Api.Logger.Warn("连接管理", "最佳服务器发现完成，但未找到可用服务器");
            }
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("连接管理", $"最佳服务器发现失败: {ex.Message}");
        }
    }

    private async Task RefreshBestIpIfNeededAsync()
    {
        // 先尝试从缓存文件加载
        if (_bestIp == null && File.Exists(_bestIpCachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<BestIpConfig>(File.ReadAllText(_bestIpCachePath));
                if (cached != null)
                {
                    _bestIp = cached.BestIp.Ip;
                    _bestPort = cached.BestIp.Port;
                    _lastBestIpCheck = cached.UpdatedAt;
                }
            }
            catch (Exception ex)
            {
                Entry.Api.Logger.Warn("连接管理", $"最佳服务器缓存读取失败: {ex.Message}");
            }
        }

        // 检查是否需要刷新（每天一次，或者还没有IP）
        if (_bestIp == null || (DateTime.Now - _lastBestIpCheck).TotalHours >= 24)
        {
            await RefreshBestIpAsync();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _lock.Dispose();
    }
}
