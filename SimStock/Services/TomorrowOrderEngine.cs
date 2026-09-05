using SimStock.Models;

namespace SimStock;

/// <summary>
/// 管理开盘预约订单（清仓与梭哈）的定时执行、状态转换与通知。
/// </summary>
public sealed class TomorrowOrderEngine : IDisposable
{
    private readonly ConnectionManager _connMgr;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public TomorrowOrderEngine(ConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public void Start(CancellationToken externalCt)
    {
        if (_loopTask is not null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _cts = cts;
        _loopTask = Task.Run(() => RunAsync(cts.Token), cts.Token);
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

    private async Task RunAsync(CancellationToken ct)
    {
        // 插件在交易时段内启用时，不必等到下一次定时触发；先补执行当天已有的预约订单。
        if (TradingHoursChecker.IsInTradingSession())
        {
            LogInfo("开盘订单引擎在交易时段启动，立即检查当天待执行订单");
            await ExecutePendingOrdersAsync();
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var nextTime = CalculateNextExecutionTime(now);
                var delay = nextTime - now;
                LogInfo($"开盘订单引擎下次执行时间: {nextTime:yyyy-MM-dd HH:mm:ss}，等待 {delay.TotalMinutes:F0} 分钟");
                await Task.Delay(delay, ct);

                await ExecutePendingOrdersAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogError($"开盘订单引擎异常: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <summary>
    /// 计算下一次候选执行时间：盘前在 9:31 执行，9:31 过后的工作日内（含午间休市）在 13:01 执行，
    /// 盘后则顺延至下一个工作日的 9:31。
    /// </summary>
    public static DateTime CalculateNextExecutionTime(DateTime now)
    {
        var morningOpen = now.Date.AddHours(9).AddMinutes(31);
        if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && morningOpen > now)
        {
            return morningOpen;
        }

        var afternoonOpen = now.Date.AddHours(13).AddMinutes(1);
        if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && morningOpen <= now && afternoonOpen > now)
        {
            return afternoonOpen;
        }

        var nextTradingDay = now.Date.AddDays(1);
        while (nextTradingDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            nextTradingDay = nextTradingDay.AddDays(1);
        }

        return nextTradingDay.AddHours(9).AddMinutes(31);
    }

    /// <summary>
    /// 将执行时间格式化为对用户友好的表述：当天为"今天 13:01"，
    /// 次日为"明天 9:31"，更远的日期为"8/20 周三 9:31"。
    /// </summary>
    public static string FormatExecutionTime(DateTime time)
    {
        var today = DateTime.Today;
        var prefix = time.Date == today
            ? "今天"
            : time.Date == today.AddDays(1)
                ? "明天"
                : $"{time:M/d} 周{ChineseWeekday(time.DayOfWeek)}";
        return $"{prefix} {time:H:mm}";
    }

    private static string ChineseWeekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    private async Task ExecutePendingOrdersAsync()
    {
        try
        {
            var pendingOrders = await Entry.Db!.Queryable<TomorrowOrder>()
                .Where(o => o.Status == 0)
                .OrderBy(o => o.OrderType) // 清仓(0)优先，避免同一股票先买后卖
                .ToListAsync();
            if (pendingOrders.Count == 0)
            {
                return;
            }

            LogInfo($"开盘订单：发现 {pendingOrders.Count} 个待执行订单，开始执行");
            if (await _connMgr.EnsureConnectedAsync() is null)
            {
                LogWarning("开盘订单：无法连接行情源，跳过本次执行");
                return;
            }

            if (!TradingHoursChecker.IsInTradingSession() || !await _connMgr.IsInTradingSessionAsync())
            {
                LogInfo("开盘订单：当前非交易时段或非交易日，跳过执行");
                return;
            }

            var reports = new Dictionary<long, GroupReport>();
            foreach (var order in pendingOrders)
            {
                if (!reports.TryGetValue(order.GroupId, out var report))
                {
                    report = new GroupReport(order.GroupId);
                    reports[order.GroupId] = report;
                }

                try
                {
                    var account = await Entry.Db.Queryable<Account>().FirstAsync(a => a.QQ == order.QQ);
                    if (account is null)
                    {
                        await MarkFailedAsync(order, "账户不存在", report);
                        continue;
                    }

                    if (order.OrderType == 1)
                    {
                        await ExecuteAllInAsync(order, account, report);
                        continue;
                    }

                    await ExecuteClearAsync(order, account, report);
                }
                catch (Exception ex)
                {
                    await MarkFailedAsync(order, $"执行异常：{ex.Message}", report);
                }
            }

            foreach (var report in reports.Values)
            {
                if (!report.HasLines)
                {
                    continue;
                }

                LogInfo($"开盘订单：群 {report.GroupId} 结算 成功 {report.SuccessCount} / 跳过 {report.SkipCount} / 失败 {report.FailCount}");
                await SendGroupMessageAsync(report.GroupId, report.BuildMessage());
            }
        }
        catch (Exception ex)
        {
            LogError($"开盘订单执行异常: {ex.Message}");
        }
    }

    private async Task ExecuteClearAsync(TomorrowOrder order, Account account, GroupReport report)
    {
        var positions = await AccountService.GetPositionsAsync(account.Id);
        if (order.StockCode == "ALL")
        {
            if (positions.Count == 0)
            {
                await MarkFailedAsync(order, "无持仓", report);
                return;
            }

            var successCount = 0;
            var skipCount = 0;
            foreach (var pos in positions)
            {
                var parsed = StockCodeParser.ParseNormalized(pos.StockCode);
                if (!parsed.HasValue)
                {
                    report.Skip(order.QQ, $"{pos.StockCode}: 股票代码格式错误");
                    skipCount++;
                    LogInfo($"开盘清仓跳过：{order.QQ} {pos.StockCode} 股票代码格式错误");
                    continue;
                }

                var displayName = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(pos.StockCode), pos.StockCode);

                var quote = await Entry.Quotes!.GetQuoteAsync(parsed.Value.market, parsed.Value.code);
                if (quote is null || quote.Bid1 <= 0)
                {
                    var skipReason = quote is null ? "行情获取失败" : "无买盘，无法卖出";
                    report.Skip(order.QQ, $"{displayName}: {skipReason}");
                    skipCount++;
                    LogInfo($"开盘清仓跳过：{order.QQ} {pos.StockCode} {skipReason}");
                    continue;
                }

                var (clearSoldOrder, error, _) = await TradingService.MarketSellAsync(order.QQ, pos.StockCode, pos.Quantity, order.GroupId);
                if (error is null)
                {
                    var price = clearSoldOrder!.Price;
                    report.Success(order.QQ, $"{displayName} 卖出 {pos.Quantity} 股 @ {price:F2} 元");
                    successCount++;
                    LogInfo($"开盘清仓成功：{order.QQ} {pos.StockCode} 卖出 {pos.Quantity} 股 @ {price:F2} 元");
                }
                else
                {
                    report.Skip(order.QQ, $"{displayName}: {error}");
                    skipCount++;
                    LogInfo($"开盘清仓跳过：{order.QQ} {pos.StockCode} {error}");
                }
            }

            order.UpdatedAt = DateTime.Now;
            if (skipCount > 0)
            {
                order.Status = 0; // 留待下一交易日继续处理
                await Entry.Db!.Updateable(order).ExecuteCommandAsync();
                LogInfo($"开盘清仓部分完成：{order.QQ} 全仓清仓，成功 {successCount} 只, 跳过 {skipCount} 只");
                var nextRetryStr = FormatExecutionTime(CalculateNextExecutionTime(DateTime.Now));
                report.Info($"{skipCount} 只跳过（T+1/停牌/行情不可用），将在 {nextRetryStr} 自动继续，或使用 {Entry.Config.GetTrigger("TomorrowClearCancel")} 全仓 取消");
                return;
            }

            order.Status = 1;
            await Entry.Db!.Updateable(order).ExecuteCommandAsync();
            LogInfo($"开盘清仓成功：{order.QQ} 全仓清仓，成功 {successCount} 只");
            return;
        }

        var holding = positions.FirstOrDefault(x => x.StockCode == order.StockCode);
        if (holding is null || holding.Quantity <= 0)
        {
            await MarkFailedAsync(order, "持仓不足", report);
            return;
        }

        var stock = StockCodeParser.ParseNormalized(order.StockCode);
        if (!stock.HasValue)
        {
            await MarkFailedAsync(order, "股票代码格式错误", report);
            return;
        }

        var singleQuote = await Entry.Quotes!.GetQuoteAsync(stock.Value.market, stock.Value.code);
        if (singleQuote is null || singleQuote.Bid1 <= 0)
        {
            await MarkFailedAsync(order, "行情获取失败或无买盘", report);
            return;
        }

        var (soldOrder, sellError, _) = await TradingService.MarketSellAsync(order.QQ, order.StockCode, holding.Quantity, order.GroupId);
        if (sellError is not null)
        {
            await MarkFailedAsync(order, sellError, report);
            return;
        }

        order.Status = 1;
        order.UpdatedAt = DateTime.Now;
        await Entry.Db!.Updateable(order).ExecuteCommandAsync();
        var stockDisplay = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(order.StockCode), order.StockCode);
        var sellPrice = soldOrder!.Price;
        LogInfo($"开盘清仓成功：{order.QQ} {order.StockCode} 卖出 {holding.Quantity} 股 @ {sellPrice:F2} 元");
        report.Success(order.QQ, $"{stockDisplay} 卖出 {holding.Quantity} 股 @ {sellPrice:F2} 元");
    }

    private async Task ExecuteAllInAsync(TomorrowOrder order, Account account, GroupReport report)
    {
        try
        {
            var parsed = StockCodeParser.ParseNormalized(order.StockCode);
            if (!parsed.HasValue)
            {
                await MarkFailedAsync(order, "股票代码格式错误", report);
                return;
            }

            var quote = await Entry.Quotes!.GetQuoteAsync(parsed.Value.market, parsed.Value.code);
            if (quote is null || quote.Ask1 <= 0)
            {
                await MarkFailedAsync(order, "行情获取失败或无卖盘", report);
                return;
            }

            var price = (decimal)quote.Ask1;
            var qty = TradingService.CalcAllInQuantity(account.Balance, price);
            if (qty < 100)
            {
                await MarkFailedAsync(order, $"可用余额不足以买入 1 手（现价 {price:F2}，需 ≈{price * 100 * 1.0003m:N2} 元）", report);
                return;
            }

            var (boughtOrder, buyError, _) = await TradingService.MarketBuyAsync(order.QQ, order.StockCode, qty, order.GroupId);
            if (buyError is not null)
            {
                await MarkFailedAsync(order, buyError, report);
                return;
            }

            order.Status = 1;
            order.UpdatedAt = DateTime.Now;
            await Entry.Db!.Updateable(order).ExecuteCommandAsync();
            var displayName = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(order.StockCode), order.StockCode);
            var execPrice = boughtOrder!.Price;
            LogInfo($"开盘梭哈成功：{order.QQ} {order.StockCode} 买入 {qty} 股 @ {execPrice:F2} 元");
            report.Success(order.QQ, $"{displayName} 买入 {qty} 股 @ {execPrice:F2} 元");
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(order, $"执行异常：{ex.Message}", report);
        }
    }

    private static async Task MarkFailedAsync(TomorrowOrder order, string reason, GroupReport report)
    {
        order.Status = 3;
        order.FailureReason = reason;
        order.UpdatedAt = DateTime.Now;
        await Entry.Db!.Updateable(order).ExecuteCommandAsync();

        var action = order.OrderType == 1 ? "开盘梭哈" : "开盘清仓";
        var target = order.StockCode == "ALL"
            ? "全仓"
            : StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(order.StockCode), order.StockCode);
        LogWarning($"开盘订单失败：{order.QQ} {action} {target} {reason}");
        report.Fail(order.QQ, $"{action} {target}：{reason}");
    }

    private static async Task SendGroupMessageAsync(long groupId, string message)
    {
        try { await Entry.Api.MessageApi.SendGroupMessageAsync(groupId, message); }
        catch (Exception ex) { LogWarning($"发送群消息失败：{ex.Message}"); }
    }

    private static void LogInfo(string message) => Entry.Api.Logger.Info("开盘订单", message);
    private static void LogWarning(string message) => Entry.Api.Logger.Warn("开盘订单", message);
    private static void LogError(string message) => Entry.Api.Logger.Error("开盘订单", message);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _loopTask?.Dispose();
    }
}
