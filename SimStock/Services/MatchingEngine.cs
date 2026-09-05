using Another_Mirai_Native.Abstractions.Models;

namespace SimStock;

/// <summary>
/// 后台撮合引擎。交易时段内轮询所有待成交限价单，自动撮合。
/// 无挂单时断连休眠；非交易时段断连休眠。
/// </summary>
public class MatchingEngine : IDisposable
{
    private readonly ConnectionManager _connMgr;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>上次执行隔夜遗留清理的日期（每天进入交易时段时清理一次）</summary>
    private DateTime _lastStaleClean;

    public MatchingEngine(ConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public void Start(CancellationToken externalCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _cts = cts;
        _loopTask = Task.Run(() => RunLoopAsync(cts.Token), cts.Token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loopTask = _loopTask;
        _cts = null;
        _loopTask = null;

        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        if (loopTask is not null)
        {
            try { await loopTask; }
            catch (OperationCanceledException) { }
        }

        cts?.Dispose();
    }

    /// <summary>
    /// 插件重启后的挂单恢复：交易日未收盘时按当前行情结算可成交订单，
    /// 非交易日或收盘后撤销遗留订单。
    /// </summary>
    public async Task RecoverPendingOrdersOnStartupAsync()
    {
        try
        {
            var pending = await Entry.Db!.Queryable<Models.Order>()
                .Where(o => o.Status == 0)
                .ToListAsync();
            if (pending.Count == 0)
            {
                return;
            }

            var isTradingDay = await _connMgr.IsTradingDayAsync();
            if (isTradingDay && !TradingHoursChecker.IsAfterClose())
            {
                // 当日委托当日有效：前一日遗留的挂单直接撤销，不参与结算
                var stale = pending.Where(o => o.CreatedAt.Date < DateTime.Today).ToList();
                if (stale.Count > 0)
                {
                    pending = pending.Where(o => o.CreatedAt.Date >= DateTime.Today).ToList();
                    Entry.Api.Logger.Info("撮合引擎", $"启动时发现 {stale.Count} 个隔夜遗留挂单，自动撤销（当日委托当日有效）");
                    await CancelOrdersWithNotifyAsync(stale, "🌙 隔夜遗留挂单已自动取消（当日委托当日有效）：");
                }

                if (pending.Count == 0)
                {
                    return;
                }

                Entry.Api.Logger.Info("撮合引擎", $"启动时发现 {pending.Count} 个遗留挂单，当前为交易时段，尝试结算");
                if (await _connMgr.EnsureConnectedAsync() is null)
                {
                    Entry.Api.Logger.Warn("撮合引擎", "无法连接行情源，遗留挂单保留待撮合引擎处理");
                    return;
                }

                var uniqueStocks = pending
                    .Select(o => o.StockCode).Distinct()
                    .Select(StockCodeParser.ParseNormalized)
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
                    .ToList();
                var quotes = await Entry.Quotes!.GetQuotesBatchAsync(uniqueStocks);
                if (quotes is null)
                {
                    Entry.Api.Logger.Warn("撮合引擎", "获取行情失败，遗留挂单保留待撮合引擎处理");
                    return;
                }

                foreach (var order in pending)
                {
                    if (!quotes.TryGetValue(order.StockCode, out var quote))
                    {
                        continue;
                    }

                    var shouldExecute = order.OrderType == 1
                        ? quote.Ask1 > 0 && order.Price >= (decimal)quote.Ask1
                        : order.OrderType == 3 && quote.Bid1 > 0 && order.Price <= (decimal)quote.Bid1;
                    if (!shouldExecute)
                    {
                        continue;
                    }

                    var executionPrice = order.OrderType == 1 ? (decimal)quote.Ask1 : (decimal)quote.Bid1;
                    var started = await TradingService.ExecuteOrderAsync(order, executionPrice);
                    Entry.Api.Logger.Info("撮合引擎", $"启动结算：订单 {order.Id} {order.StockCode} {(started ? "已成交" : "未成交")}");
                }
            }
            else
            {
                Entry.Api.Logger.Info("撮合引擎", $"启动时发现 {pending.Count} 个遗留挂单，非交易时段，全部撤销");
                foreach (var order in pending)
                {
                    order.Status = 3;
                    order.UpdatedAt = DateTime.Now;
                    await Entry.Db!.Updateable(order).ExecuteCommandAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Error("撮合引擎", $"清理遗留挂单异常: {ex.Message}");
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var wasInSession = false;

        // 日志去重信号：每个状态只打印一次，所有检查通过后重置
        bool loggedAuction = false;
        bool loggedOffHours = false;
        bool loggedLunchBreak = false;
        bool loggedHoliday = false;
        bool loggedNoOrders = false;
        bool loggedConnFail = false;
        bool loggedQuoteFail = false;

        void ResetLogSignals()
        {
            loggedAuction = loggedOffHours = loggedLunchBreak = loggedHoliday = false;
            loggedNoOrders = loggedConnFail = loggedQuoteFail = false;
        }

        void LogOnce(ref bool flag, string msg)
        {
            if (!flag)
            {
                Entry.Api.Logger.Info("撮合引擎", msg);
                flag = true;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 竞价时段跳过
                if (TradingHoursChecker.IsInAuctionPeriod())
                {
                    wasInSession = false;
                    LogOnce(ref loggedAuction, "当前是竞价时段，不处理挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // 非交易时段
                if (!TradingHoursChecker.IsInTradingSession())
                {
                    if (wasInSession)
                    {
                        wasInSession = false;
                        if (TradingHoursChecker.IsAfterClose())
                        {
                            await CancelAllPendingOrdersAtCloseAsync();
                        }
                        else
                        {
                            // 午间休市（11:30-13:00）不是收盘：挂单保留至下午盘继续撮合
                            LogOnce(ref loggedLunchBreak, "午间休市，挂单保留，下午盘继续撮合");
                        }
                    }

                    LogOnce(ref loggedOffHours, "当前是非交易时段，不处理挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // 检查是否交易日
                if (!await _connMgr.IsInTradingSessionAsync())
                {
                    // 防御性处理：仅在 15:00 收盘后触发收盘自动撤单
                    // （交易日不会在盘中变节假日，此分支实际几乎不会命中）
                    if (wasInSession && TradingHoursChecker.IsAfterClose())
                    {
                        wasInSession = false;
                        await CancelAllPendingOrdersAtCloseAsync();
                    }

                    LogOnce(ref loggedHoliday, "当前是节假日，不处理挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                    continue;
                }

                wasInSession = true;

                // 当日委托当日有效：每天进入交易时段的第一轮清理隔夜遗留挂单
                if (_lastStaleClean.Date != DateTime.Today)
                {
                    _lastStaleClean = DateTime.Now;
                    await CancelStaleOrdersAsync();
                }

                // 获取待成交限价单
                var pendingOrders = await TradingService.GetPendingLimitOrdersAsync();
                if (pendingOrders.Count == 0)
                {
                    LogOnce(ref loggedNoOrders, "当前无待成交挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // 收集唯一股票代码
                var uniqueStocks = pendingOrders
                    .Select(o => o.StockCode)
                    .Distinct()
                    .Select(code =>
                    {
                        var parsed = StockCodeParser.ParseNormalized(code);
                        return parsed.HasValue ? (parsed.Value.market, parsed.Value.code) : ((byte)0, "");
                    })
                    .Where(s => s.Item2.Length > 0)
                    .ToList();

                if (uniqueStocks.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                // 确保连接
                var client = await _connMgr.EnsureConnectedAsync(ct);
                if (client == null)
                {
                    LogOnce(ref loggedConnFail, "无法连接行情服务器");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                // 批量获取行情
                var quotesDict = await Entry.Quotes!.GetQuotesBatchAsync(uniqueStocks);
                if (quotesDict == null || quotesDict.Count == 0)
                {
                    LogOnce(ref loggedQuoteFail, "获取行情数据失败");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                // 所有检查通过，重置日志信号
                ResetLogSignals();

                // 逐单撮合
                foreach (var order in pendingOrders)
                {
                    if (!quotesDict.TryGetValue(order.StockCode, out var quote))
                    {
                        continue;
                    }

                    bool shouldExecute = false;

                    if (order.OrderType == 1) // 限价买
                    {
                        if (quote.Ask1 > 0 && order.Price >= (decimal)quote.Ask1)
                        {
                            shouldExecute = true;
                        }
                    }
                    else if (order.OrderType == 3) // 限价卖
                    {
                        if (quote.Bid1 > 0 && order.Price <= (decimal)quote.Bid1)
                        {
                            shouldExecute = true;
                        }
                    }

                    if (shouldExecute)
                    {
                        var execPrice = order.OrderType == 1 ? (decimal)quote.Ask1 : (decimal)quote.Bid1;
                        var executed = await TradingService.ExecuteOrderAsync(order, execPrice);
                        if (!executed)
                        {
                            // 未成交（撒单/余额不足/订单状态变更），ExecuteOrderAsync 内部已处理通知，跳过成交通知
                            continue;
                        }

                        // 发送成交通知：群聊来源发群，私聊来源发私聊
                        try
                        {
                            var account = await Entry.Db!.Queryable<Models.Account>()
                                .FirstAsync(a => a.Id == order.AccountId);
                            if (account != null)
                            {
                                var fee = SafetyChecker.CalcFee(execPrice * order.Quantity);
                                var dir = order.OrderType == 1 ? "🔴买入" : "🟢卖出";
                                var stockName = await Entry.StockNames.GetNameAsync(order.StockCode);
                                var msg = $"🎯 [限价单成交通知]\n" +
                                          $"📋 股票: {StockCodeParser.ToDisplayStock(stockName, order.StockCode)}\n" +
                                          $"📌 方向: {dir}\n" +
                                          $"📦 数量: {order.Quantity} 股\n" +
                                          $"💲 成交价: {execPrice:F2} 元\n" +
                                          $"🧾 手续费: {fee:F2} 元\n" +
                                          $"💰 金额: {execPrice * order.Quantity:N2} 元\n" +
                                          $"⏰ 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                                if (order.SourceGroupId.HasValue)
                                {
                                    await SendGroupNotificationAsync(order, msg, account.QQ);
                                }
                                else
                                {
                                    await Entry.Api.MessageApi.SendPrivateMessageAsync(account.QQ, msg);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Entry.Api.Logger.Warn("撮合引擎", $"成交通知发送失败: {ex.Message}");
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(Entry.Config.QuotePollingIntervalSec), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Entry.Api.Logger.Warn("撮合引擎", $"主循环异常: {ex.Message}");
                _connMgr.Disconnect();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <summary>当日委托当日有效：撤销前一日创建的挂单（防御错过收盘后的重启/停机场景）</summary>
    private async Task CancelStaleOrdersAsync()
    {
        var stale = (await TradingService.GetPendingLimitOrdersAsync())
            .Where(o => o.CreatedAt.Date < DateTime.Today)
            .ToList();
        if (stale.Count == 0)
        {
            return;
        }

        Entry.Api.Logger.Info("撮合引擎", $"清理 {stale.Count} 个隔夜遗留挂单（当日委托当日有效）");
        await CancelOrdersWithNotifyAsync(stale, "🌙 隔夜遗留挂单已自动取消（当日委托当日有效）：");
    }

    /// <summary>收盘后自动撤销所有未成交挂单，并在对应群内发送汇总通知</summary>
    private async Task CancelAllPendingOrdersAtCloseAsync()
    {
        var pendingOrders = await TradingService.GetPendingLimitOrdersAsync();
        if (pendingOrders.Count == 0)
        {
            return;
        }

        await CancelOrdersWithNotifyAsync(pendingOrders, "🌙 本日已休市，未成交挂单自动取消：");
    }

    /// <summary>撤销指定订单，并按来源（群聊/私聊）发送汇总通知</summary>
    private async Task CancelOrdersWithNotifyAsync(List<Models.Order> orders, string heading)
    {
        try
        {
            // 收集订单信息并按来源分组
            var accountIds = orders.Select(o => o.AccountId).Distinct().ToList();
            var accounts = await Entry.Db!.Queryable<Models.Account>()
                .Where(a => accountIds.Contains(a.Id))
                .ToListAsync();
            var accountDict = accounts.ToDictionary(a => a.Id);

            // groupId → orders,    0 = 私聊来源
            var groupOrders = new Dictionary<long, List<(Models.Order Order, long QQ)>>();
            var privateOrders = new List<(Models.Order Order, long QQ)>();

            foreach (var order in orders)
            {
                order.Status = 3;
                order.UpdatedAt = DateTime.Now;
                await Entry.Db!.Updateable(order).ExecuteCommandAsync();

                if (!accountDict.TryGetValue(order.AccountId, out var acc))
                {
                    continue;
                }

                if (order.SourceGroupId.HasValue)
                {
                    var gid = order.SourceGroupId.Value;
                    if (!groupOrders.ContainsKey(gid))
                    {
                        groupOrders[gid] = [];
                    }

                    groupOrders[gid].Add((order, acc.QQ));
                }
                else
                {
                    privateOrders.Add((order, acc.QQ));
                }
            }

            // 群聊来源：按群发汇总
            foreach (var (sourceGroupId, groupOrderList) in groupOrders)
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(heading);

                    // 获取昵称后发群
                    var nameCache = new Dictionary<long, string>();
                    foreach (var (_, qq) in groupOrderList)
                    {
                        if (!nameCache.ContainsKey(qq))
                        {
                            try
                            {
                                var member = Entry.Api.GroupApi.GetGroupMemberInfo(sourceGroupId, qq);
                                nameCache[qq] = member != null
                                    ? (!string.IsNullOrEmpty(member.Card) ? member.Card
                                        : !string.IsNullOrEmpty(member.Nick) ? member.Nick
                                        : qq.ToString())
                                    : qq.ToString();
                            }
                            catch { nameCache[qq] = qq.ToString(); }
                        }
                    }

                    foreach (var (order, qq) in groupOrderList)
                    {
                        var name = nameCache.TryGetValue(qq, out var n) ? n : qq.ToString();
                        var dir = order.OrderType switch { 1 => "买入", 3 => "卖出", _ => "?" };
                        var stockName = await Entry.StockNames.GetNameAsync(order.StockCode);
                        sb.AppendLine($"  · {name}");
                        sb.AppendLine($"    📋 {StockCodeParser.ToDisplayStock(stockName, order.StockCode)}");
                        sb.AppendLine($"    📌 {dir} {order.Quantity} 股");
                        sb.AppendLine($"    💲 委托价: {order.Price:F2}");
                    }

                    await Entry.Api.MessageApi.SendGroupMessageAsync(sourceGroupId, sb.ToString());
                }
                catch (Exception ex)
                {
                    Entry.Api.Logger.Warn("撮合引擎", $"收盘撤单通知发送失败: {ex.Message}");
                }
            }

            // 私聊来源：逐人发私聊
            foreach (var (order, qq) in privateOrders)
            {
                try
                {
                    var dir = order.OrderType switch { 1 => "买入", 3 => "卖出", _ => "?" };
                    var stockName = await Entry.StockNames.GetNameAsync(order.StockCode);
                    var msg = $"{heading}\n" +
                              $"📋 {StockCodeParser.ToDisplayStock(stockName, order.StockCode)}\n" +
                              $"📌 {dir} {order.Quantity} 股\n" +
                              $"💲 委托价: {order.Price:F2}";
                    await Entry.Api.MessageApi.SendPrivateMessageAsync(qq, msg);
                }
                catch (Exception ex)
                {
                    Entry.Api.Logger.Warn("撮合引擎", $"收盘撤单通知发送失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("撮合引擎", $"撤单处理异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 群聊成交通知：优先引用原始挂单消息回复，找不到时 @用户
    /// </summary>
    private static async Task SendGroupNotificationAsync(Models.Order order, string msg, long qq)
    {
        try
        {
            if (order.SourceMessageId.HasValue)
            {
                var mb = new MessageBuilder();
                mb.Items.Add(new Another_Mirai_Native.Abstractions.Models.MessageItem.Reply(order.SourceMessageId.Value));
                mb.Text(msg);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId!.Value, mb.Build());
            }
            else
            {
                // 没有原始消息ID，@用户
                var mb = new MessageBuilder();
                mb.At(qq);
                mb.Text(msg);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId!.Value, mb.Build());
            }
        }
        catch
        {
            // 引用回复失败（消息可能被删除），回退到 @用户
            try
            {
                var mb = new MessageBuilder();
                mb.At(qq);
                mb.Text(msg);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId!.Value, mb.Build());
            }
            catch (Exception ex)
            {
                Entry.Api.Logger.Warn("撮合引擎", $"成交通知最终发送失败: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _loopTask?.Dispose();
    }
}
