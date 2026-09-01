using Another_Mirai_Native.Abstractions.Models;
using SimStock.Models;
using SqlSugar;
using System.Collections.Concurrent;

namespace SimStock;

public static class TradingService
{
    private static SqlSugarScope Db => Entry.Db!;

    private static readonly ConcurrentDictionary<long, SemaphoreSlim> AccountLocks = new();

    private static SemaphoreSlim GetLock(long accountId)
        => AccountLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

    /// <summary>获取账户并发锁（供授信等需要跨命令串行化的操作复用）</summary>
    internal static SemaphoreSlim GetAccountLock(long accountId)
        => GetLock(accountId);

    /// <summary>
    /// 计算梭哈最大可买数量：余额扣除预估手续费后，向下取整到 100 股的倍数。
    /// </summary>
    public static int CalcAllInQuantity(decimal balance, decimal price)
    {
        if (price <= 0 || balance <= 0) return 0;

        var quantity = (int)(balance / (price * 1.0003m) / 100) * 100;
        while (quantity >= 100)
        {
            var amount = price * quantity;
            if (amount + SafetyChecker.CalcFee(amount) <= balance)
            {
                break;
            }
            quantity -= 100;
        }

        return quantity;
    }

    // === 市价买入 ===
    public static async Task<(Order? order, string? error, decimal? fee)> MarketBuyAsync(
        long qq, string normalizedCode, int quantity, long? sourceGroupId = null)
    {
        var (account, err) = await SafetyChecker.RequireAccountAsync(Db, qq);
        if (account == null)
        {
            return (null, err, null);
        }

        var parsed = StockCodeParser.ParseNormalized(normalizedCode);
        if (!parsed.HasValue)
        {
            return (null, "股票代码格式错误", null);
        }

        var (market, code) = parsed.Value;

        var check = SafetyChecker.CheckAStock(market, code);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        check = SafetyChecker.CheckOrderParams(quantity);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, code);
        if (quote == null)
        {
            return (null, "行情获取失败", null);
        }

        check = SafetyChecker.CheckSuspension(quote.Bid1, quote.Ask1);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        if (quote.Ask1 <= 0)
        {
            return (null, "该股票当前无卖盘，无法买入", null);
        }

        var price = (decimal)quote.Ask1;
        var amount = price * quantity;
        var fee = SafetyChecker.CalcFee(amount);
        var totalCost = amount + fee;

        var sem = GetLock(account.Id);
        await sem.WaitAsync();
        try
        {
            account = await Db.Queryable<Account>().FirstAsync(a => a.Id == account.Id);
            if (account == null)
            {
                return (null, "账户异常", null);
            }

            check = SafetyChecker.CheckFunds(account, totalCost);
            if (!check.passed)
            {
                return (null, check.error, null);
            }

            var order = new Order
            {
                AccountId = account.Id,
                SourceGroupId = sourceGroupId,
                StockCode = normalizedCode,
                OrderType = 0,
                Quantity = quantity,
                Price = 0,
                FilledQuantity = quantity,
                Status = 2,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await Db.UseTranAsync(async () =>
            {
                order.Id = await Db.Insertable(order).ExecuteReturnBigIdentityAsync();
                await UpsertPositionAsync(account.Id, normalizedCode, quantity, price);
                account.Balance -= totalCost;
                await AccountService.UpdateTotalAssetAsync(account.Id);
                account.UpdatedAt = DateTime.Now;
                await Db.Updateable(account).ExecuteCommandAsync();
                await Db.Insertable(new TradeRecord
                {
                    AccountId = account.Id,
                    OrderId = order.Id,
                    StockCode = normalizedCode,
                    TradeType = 0,
                    Quantity = quantity,
                    Price = price,
                    Amount = totalCost,
                    TradedAt = DateTime.Now
                }).ExecuteCommandAsync();
            });
        }
        finally { sem.Release(); }

        return (null, null, fee);
    }

    // === 限价买入 ===
    public static async Task<(Order? order, string? error, decimal? fee, long? pendingOrderId)> LimitBuyAsync(
        long qq, string normalizedCode, int quantity, decimal price, long? sourceGroupId = null, int? sourceMessageId = null)
    {
        var (account, err) = await SafetyChecker.RequireAccountAsync(Db, qq);
        if (account == null)
        {
            return (null, err, null, null);
        }

        var parsed = StockCodeParser.ParseNormalized(normalizedCode);
        if (!parsed.HasValue)
        {
            return (null, "股票代码格式错误", null, null);
        }

        var (market, code) = parsed.Value;

        var check = SafetyChecker.CheckAStock(market, code);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        check = SafetyChecker.CheckOrderParams(quantity, price);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        check = await SafetyChecker.CheckPendingOrderLimitAsync(Db, account.Id);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        var amount = price * quantity;
        var estimatedFee = SafetyChecker.CalcFee(amount);
        check = SafetyChecker.CheckFunds(account, amount + estimatedFee);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, code);
        if (quote == null)
        {
            return (null, "行情获取失败", null, null);
        }

        check = SafetyChecker.CheckSuspension(quote.Bid1, quote.Ask1);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        var canExecuteNow = quote.Ask1 > 0 && price >= (decimal)quote.Ask1;
        var execPrice = canExecuteNow ? (decimal)quote.Ask1 : 0;

        var sem = GetLock(account.Id);
        await sem.WaitAsync();
        try
        {
            account = await Db.Queryable<Account>().FirstAsync(a => a.Id == account.Id);
            if (account == null)
            {
                return (null, "账户异常", null, null);
            }

            if (canExecuteNow)
            {
                var execAmount = execPrice * quantity;
                var fee = SafetyChecker.CalcFee(execAmount);
                var totalCost = execAmount + fee;

                if (account.Balance < totalCost)
                {
                    return (null, "资金不足", null, null);
                }

                var order = new Order
                {
                    AccountId = account.Id,
                    SourceGroupId = sourceGroupId,
                    SourceMessageId = sourceMessageId,
                    StockCode = normalizedCode,
                    OrderType = 1,
                    Quantity = quantity,
                    Price = price,
                    FilledQuantity = quantity,
                    Status = 2,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };

                await Db.UseTranAsync(async () =>
                {
                    order.Id = await Db.Insertable(order).ExecuteReturnBigIdentityAsync();
                    await UpsertPositionAsync(account.Id, normalizedCode, quantity, execPrice);
                    account.Balance -= totalCost;
                    await AccountService.UpdateTotalAssetAsync(account.Id);
                    account.UpdatedAt = DateTime.Now;
                    await Db.Updateable(account).ExecuteCommandAsync();
                    await Db.Insertable(new TradeRecord
                    {
                        AccountId = account.Id,
                        OrderId = order.Id,
                        StockCode = normalizedCode,
                        TradeType = 0,
                        Quantity = quantity,
                        Price = execPrice,
                        Amount = totalCost,
                        TradedAt = DateTime.Now
                    }).ExecuteCommandAsync();
                });

                return (null, null, fee, null);
            }
            else
            {
                var order = new Order
                {
                    AccountId = account.Id,
                    SourceGroupId = sourceGroupId,
                    SourceMessageId = sourceMessageId,
                    StockCode = normalizedCode,
                    OrderType = 1,
                    Quantity = quantity,
                    Price = price,
                    FilledQuantity = 0,
                    Status = 0,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                order.Id = await Db.Insertable(order).ExecuteReturnBigIdentityAsync();
                return (null, null, null, order.Id);
            }
        }
        finally { sem.Release(); }
    }

    // === 市价卖出 ===
    public static async Task<(Order? order, string? error, decimal? fee)> MarketSellAsync(
        long qq, string normalizedCode, int quantity, long? sourceGroupId = null)
    {
        var (account, err) = await SafetyChecker.RequireAccountAsync(Db, qq);
        if (account == null)
        {
            return (null, err, null);
        }

        var parsed = StockCodeParser.ParseNormalized(normalizedCode);
        if (!parsed.HasValue)
        {
            return (null, "股票代码格式错误", null);
        }

        var (market, code) = parsed.Value;

        var check = SafetyChecker.CheckAStock(market, code);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        check = SafetyChecker.CheckOrderParams(quantity);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        check = await SafetyChecker.CheckT1RuleAsync(Db, account.Id, normalizedCode);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, code);
        if (quote == null)
        {
            return (null, "行情获取失败", null);
        }

        check = SafetyChecker.CheckSuspension(quote.Bid1, quote.Ask1);
        if (!check.passed)
        {
            return (null, check.error, null);
        }

        if (quote.Bid1 <= 0)
        {
            return (null, "该股票当前无买盘，无法卖出", null);
        }

        var price = (decimal)quote.Bid1;
        var amount = price * quantity;
        var fee = SafetyChecker.CalcFee(amount);
        var totalCredit = amount - fee;

        var sem = GetLock(account.Id);
        await sem.WaitAsync();
        try
        {
            account = await Db.Queryable<Account>().FirstAsync(a => a.Id == account.Id);
            if (account == null)
            {
                return (null, "账户异常", null);
            }

            check = await SafetyChecker.CheckHoldingsAsync(Db, account.Id, normalizedCode, quantity);
            if (!check.passed)
            {
                return (null, check.error, null);
            }

            var order = new Order
            {
                AccountId = account.Id,
                SourceGroupId = sourceGroupId,
                StockCode = normalizedCode,
                OrderType = 2,
                Quantity = quantity,
                Price = 0,
                FilledQuantity = quantity,
                Status = 2,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await Db.UseTranAsync(async () =>
            {
                order.Id = await Db.Insertable(order).ExecuteReturnBigIdentityAsync();
                await DeductPositionAsync(account.Id, normalizedCode, quantity);
                account.Balance += totalCredit;
                await AccountService.UpdateTotalAssetAsync(account.Id);
                account.UpdatedAt = DateTime.Now;
                await Db.Updateable(account).ExecuteCommandAsync();
                await Db.Insertable(new TradeRecord
                {
                    AccountId = account.Id,
                    OrderId = order.Id,
                    StockCode = normalizedCode,
                    TradeType = 1,
                    Quantity = quantity,
                    Price = price,
                    Amount = totalCredit,
                    TradedAt = DateTime.Now
                }).ExecuteCommandAsync();
            });
        }
        finally { sem.Release(); }

        return (null, null, fee);
    }

    // === 限价卖出 ===
    public static async Task<(Order? order, string? error, decimal? fee, long? pendingOrderId)> LimitSellAsync(
        long qq, string normalizedCode, int quantity, decimal price, long? sourceGroupId = null, int? sourceMessageId = null)
    {
        var (account, err) = await SafetyChecker.RequireAccountAsync(Db, qq);
        if (account == null)
        {
            return (null, err, null, null);
        }

        var parsed = StockCodeParser.ParseNormalized(normalizedCode);
        if (!parsed.HasValue)
        {
            return (null, "股票代码格式错误", null, null);
        }

        var (market, code) = parsed.Value;

        var check = SafetyChecker.CheckAStock(market, code);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        check = SafetyChecker.CheckOrderParams(quantity, price);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        check = await SafetyChecker.CheckT1RuleAsync(Db, account.Id, normalizedCode);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        check = await SafetyChecker.CheckHoldingsAsync(Db, account.Id, normalizedCode, quantity);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        check = await SafetyChecker.CheckPendingOrderLimitAsync(Db, account.Id);
        if (!check.passed)
        {
            return (null, check.error, null, null);
        }

        var sem = GetLock(account.Id);
        await sem.WaitAsync();
        try
        {
            account = await Db.Queryable<Account>().FirstAsync(a => a.Id == account.Id);
            if (account == null)
            {
                return (null, "账户异常", null, null);
            }

            var quote = await Entry.Quotes!.GetQuoteAsync(market, code);
            if (quote == null)
            {
                return (null, "行情获取失败", null, null);
            }

            check = SafetyChecker.CheckSuspension(quote.Bid1, quote.Ask1);
            if (!check.passed)
            {
                return (null, check.error, null, null);
            }

            var canExecuteNow = quote.Bid1 > 0 && price <= (decimal)quote.Bid1;
            var execPrice = canExecuteNow ? (decimal)quote.Bid1 : 0;

            if (canExecuteNow)
            {
                var execAmount = execPrice * quantity;
                var fee = SafetyChecker.CalcFee(execAmount);
                var totalCredit = execAmount - fee;

                var order = new Order
                {
                    AccountId = account.Id,
                    SourceGroupId = sourceGroupId,
                    SourceMessageId = sourceMessageId,
                    StockCode = normalizedCode,
                    OrderType = 3,
                    Quantity = quantity,
                    Price = price,
                    FilledQuantity = quantity,
                    Status = 2,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };

                await Db.UseTranAsync(async () =>
                {
                    order.Id = await Db.Insertable(order).ExecuteReturnBigIdentityAsync();
                    await DeductPositionAsync(account.Id, normalizedCode, quantity);
                    account.Balance += totalCredit;
                    await AccountService.UpdateTotalAssetAsync(account.Id);
                    account.UpdatedAt = DateTime.Now;
                    await Db.Updateable(account).ExecuteCommandAsync();
                    await Db.Insertable(new TradeRecord
                    {
                        AccountId = account.Id,
                        OrderId = order.Id,
                        StockCode = normalizedCode,
                        TradeType = 1,
                        Quantity = quantity,
                        Price = execPrice,
                        Amount = totalCredit,
                        TradedAt = DateTime.Now
                    }).ExecuteCommandAsync();
                });

                return (null, null, fee, null);
            }
            else
            {
                var order = new Order
                {
                    AccountId = account.Id,
                    SourceGroupId = sourceGroupId,
                    SourceMessageId = sourceMessageId,
                    StockCode = normalizedCode,
                    OrderType = 3,
                    Quantity = quantity,
                    Price = price,
                    FilledQuantity = 0,
                    Status = 0,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                order.Id = await Db.Insertable(order).ExecuteReturnBigIdentityAsync();
                return (null, null, null, order.Id);
            }
        }
        finally { sem.Release(); }
    }

    // === 撤单 ===
    public static async Task<(bool success, string? error)> CancelOrderAsync(long qq, long orderId)
    {
        var (account, err) = await SafetyChecker.RequireAccountAsync(Db, qq);
        if (account == null)
        {
            return (false, err);
        }

        var (order, err2) = await SafetyChecker.RequireOwnOrderAsync(Db, orderId, account.Id);
        if (order == null)
        {
            return (false, err2);
        }

        var sem = GetLock(account.Id);
        await sem.WaitAsync();
        try
        {
            order = await Db.Queryable<Order>().FirstAsync(o => o.Id == orderId);
            if (order == null || order.Status != 0)
            {
                return (false, "该订单已成交或已撤销");
            }

            order.Status = 3;
            order.UpdatedAt = DateTime.Now;
            await Db.Updateable(order).ExecuteCommandAsync();
            return (true, null);
        }
        finally { sem.Release(); }
    }

    /// <summary>撮合引擎调用：以指定价格执行限价单（含手续费）</summary>
    public static async Task ExecuteOrderAsync(Order order, decimal executionPrice)
    {
        var sem = GetLock(order.AccountId);
        await sem.WaitAsync();
        try
        {
            var freshOrder = await Db.Queryable<Order>().FirstAsync(o => o.Id == order.Id);
            if (freshOrder == null || freshOrder.Status != 0)
            {
                return;
            }

            var account = await Db.Queryable<Account>().FirstAsync(a => a.Id == order.AccountId);
            if (account == null)
            {
                return;
            }

            var amount = executionPrice * freshOrder.Quantity;
            var fee = SafetyChecker.CalcFee(amount);

            if (freshOrder.OrderType == 1) // 限价买
            {
                var totalCost = amount + fee;
                if (account.Balance < totalCost)
                {
                    return;
                }

                await Db.UseTranAsync(async () =>
                {
                    freshOrder.Status = 2;
                    freshOrder.FilledQuantity = freshOrder.Quantity;
                    freshOrder.UpdatedAt = DateTime.Now;
                    await Db.Updateable(freshOrder).ExecuteCommandAsync();

                    await UpsertPositionAsync(account.Id, freshOrder.StockCode, freshOrder.Quantity, executionPrice);

                    account.Balance -= totalCost;
                    await AccountService.UpdateTotalAssetAsync(account.Id);
                    account.UpdatedAt = DateTime.Now;
                    await Db.Updateable(account).ExecuteCommandAsync();

                    await Db.Insertable(new TradeRecord
                    {
                        AccountId = account.Id,
                        OrderId = freshOrder.Id,
                        StockCode = freshOrder.StockCode,
                        TradeType = 0,
                        Quantity = freshOrder.Quantity,
                        Price = executionPrice,
                        Amount = totalCost,
                        TradedAt = DateTime.Now
                    }).ExecuteCommandAsync();
                });
            }
            else if (freshOrder.OrderType == 3) // 限价卖
            {
                // 持仓二次校验：挂单期间持仓可能被市价卖出，防止超额卖出。
                // 持仓不足时直接撤单并通知玩家，避免订单悬置到收盘才消失
                var holdingCheck = await SafetyChecker.CheckHoldingsAsync(Db, account.Id, freshOrder.StockCode, freshOrder.Quantity);
                if (!holdingCheck.passed)
                {
                    freshOrder.Status = 3;
                    freshOrder.UpdatedAt = DateTime.Now;
                    await Db.Updateable(freshOrder).ExecuteCommandAsync();
                    Entry.Api.Logger.Info("撮合引擎", $"限价卖单 {freshOrder.Id} {freshOrder.StockCode} 持仓不足，自动撤销");
                    await NotifyOrderCancelledAsync(freshOrder, account, "持仓已被卖出，该挂单已自动取消");
                    return;
                }

                var totalCredit = amount - fee;

                await Db.UseTranAsync(async () =>
                {
                    freshOrder.Status = 2;
                    freshOrder.FilledQuantity = freshOrder.Quantity;
                    freshOrder.UpdatedAt = DateTime.Now;
                    await Db.Updateable(freshOrder).ExecuteCommandAsync();

                    await DeductPositionAsync(account.Id, freshOrder.StockCode, freshOrder.Quantity);

                    account.Balance += totalCredit;
                    await AccountService.UpdateTotalAssetAsync(account.Id);
                    account.UpdatedAt = DateTime.Now;
                    await Db.Updateable(account).ExecuteCommandAsync();

                    await Db.Insertable(new TradeRecord
                    {
                        AccountId = account.Id,
                        OrderId = freshOrder.Id,
                        StockCode = freshOrder.StockCode,
                        TradeType = 1,
                        Quantity = freshOrder.Quantity,
                        Price = executionPrice,
                        Amount = totalCredit,
                        TradedAt = DateTime.Now
                    }).ExecuteCommandAsync();
                });
            }
        }
        finally { sem.Release(); }
    }

    /// <summary>通知订单来源（群聊/私聊）：挂单已被自动撤销</summary>
    internal static async Task NotifyOrderCancelledAsync(Order order, Account account, string reason)
    {
        try
        {
            var stockName = await Entry.StockNames.GetNameAsync(order.StockCode);
            var dir = order.OrderType == 1 ? "买入" : "卖出";
            var text = "🌙 挂单已自动取消：\n" +
                       $"📋 {StockCodeParser.ToDisplayStock(stockName, order.StockCode)}\n" +
                       $"📌 {dir} {order.Quantity} 股\n" +
                       $"💲 委托价: {order.Price:F2}\n" +
                       $"⚠️ {reason}";

            if (order.SourceGroupId.HasValue)
            {
                var mb = new MessageBuilder();
                if (order.SourceMessageId.HasValue)
                {
                    mb.Items.Add(new Another_Mirai_Native.Abstractions.Models.MessageItem.Reply(order.SourceMessageId.Value));
                }
                else
                {
                    mb.At(account.QQ);
                }
                mb.Text(text);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId.Value, mb.Build());
            }
            else
            {
                await Entry.Api.MessageApi.SendPrivateMessageAsync(account.QQ, text);
            }
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("撮合引擎", $"挂单撤单通知发送失败: {ex.Message}");
        }
    }

    /// <summary>获取所有待成交限价单</summary>
    public static async Task<List<Order>> GetPendingLimitOrdersAsync()
    {
        return await Db.Queryable<Order>()
            .Where(o => o.Status == 0 && (o.OrderType == 1 || o.OrderType == 3))
            .ToListAsync();
    }

    /// <summary>获取用户交易历史</summary>
    public static async Task<List<TradeRecord>> GetTradeHistoryAsync(long accountId, int count = 20)
    {
        return await Db.Queryable<TradeRecord>()
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.TradedAt, OrderByType.Desc)
            .Take(count)
            .ToListAsync();
    }

    // === 辅助方法 ===

    private static async Task UpsertPositionAsync(long accountId, string stockCode, int qty, decimal price)
    {
        var pos = await Db.Queryable<Position>()
            .FirstAsync(p => p.AccountId == accountId && p.StockCode == stockCode);
        if (pos != null)
        {
            var newAvg = ((pos.Quantity * pos.AvgCost) + (qty * price)) / (pos.Quantity + qty);
            pos.Quantity += qty;
            pos.AvgCost = newAvg;
            pos.UpdatedAt = DateTime.Now;
            await Db.Updateable(pos).ExecuteCommandAsync();
        }
        else
        {
            await Db.Insertable(new Position
            {
                AccountId = accountId,
                StockCode = stockCode,
                Quantity = qty,
                AvgCost = price,
                UpdatedAt = DateTime.Now
            }).ExecuteCommandAsync();
        }
    }

    private static async Task DeductPositionAsync(long accountId, string stockCode, int qty)
    {
        var positions = await Db.Queryable<Position>()
            .Where(p => p.AccountId == accountId && p.StockCode == stockCode)
            .OrderBy(p => p.Id)
            .ToListAsync();

        var remaining = qty;
        foreach (var pos in positions)
        {
            if (remaining <= 0)
            {
                break;
            }

            var deduct = Math.Min(pos.Quantity, remaining);
            pos.Quantity -= deduct;
            remaining -= deduct;
            pos.UpdatedAt = DateTime.Now;
            if (pos.Quantity > 0)
            {
                await Db.Updateable(pos).ExecuteCommandAsync();
            }
            else
            {
                await Db.Deleteable(pos).ExecuteCommandAsync();
            }
        }
    }
}
