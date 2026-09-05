using Another_Mirai_Native.Abstractions;
using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Context;
using Another_Mirai_Native.Abstractions.Enums;
using Another_Mirai_Native.Abstractions.Models.MessageItem;
using SimStock.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace SimStock;

public class StockCommands : CommandHandlerBase
{
    private const long PrivateChatGroupId = 0L;

    /// <summary>昵称缓存：QQ → (昵称, 过期时间)，过期后重新拉取</summary>
    private static readonly ConcurrentDictionary<long, (string Name, DateTime Expiry)> NicknameCache = new();

    private static readonly TimeSpan NicknameCacheTtl = TimeSpan.FromHours(12);

    /// <summary>好友列表缓存</summary>
    private static Dictionary<long, string>? _friendListCache;
    private static DateTime _friendListCacheTime;
    private static readonly SemaphoreSlim _friendListLock = new(1, 1);

    public string AccountCmd => Entry.Config.GetCommandTemplate("Account");

    public string AdminAddCmd => Entry.Config.GetCommandTemplate("AdminAdd");

    public string AdminListCmd => Entry.Config.GetCommandTemplate("AdminList");

    public string AdminRemoveCmd => Entry.Config.GetCommandTemplate("AdminRemove");

    public string AllInCmd => Entry.Config.GetCommandTemplate("AllIn");

    public string BuyCmd => Entry.Config.GetCommandTemplate("Buy");

    public string CancelCmd => Entry.Config.GetCommandTemplate("Cancel");

    public string ClearAllCmd => Entry.Config.GetCommandTemplate("ClearAll");

    public string ClearOneCmd => Entry.Config.GetCommandTemplate("ClearOne");

    public string TomorrowClearCmd => Entry.Config.GetCommandTemplate("TomorrowClear");

    public string TomorrowClearCancelCmd => Entry.Config.GetCommandTemplate("TomorrowClearCancel");

    public string TomorrowAllInCmd => Entry.Config.GetCommandTemplate("TomorrowAllIn");

    public string TomorrowAllInCancelCmd => Entry.Config.GetCommandTemplate("TomorrowAllInCancel");

    public string CreditCmd => Entry.Config.GetCommandTemplate("Credit");

    public string CreditUseCmd => Entry.Config.GetCommandTemplate("CreditUse");

    public string CreditRepayCmd => Entry.Config.GetCommandTemplate("CreditRepay");

    public string DepositCmd => Entry.Config.GetCommandTemplate("Deposit");

    public string GlobalRankCmd => Entry.Config.GetCommandTemplate("GlobalRank");

    public string HelpCmd => Entry.Config.GetCommandTemplate("Help");

    public string HistoryCmd => Entry.Config.GetCommandTemplate("History");

    public string LimitAllInCmd => Entry.Config.GetCommandTemplate("LimitAllIn");

    public string LimitBuyCmd => Entry.Config.GetCommandTemplate("LimitBuy");

    public string LimitSellCmd => Entry.Config.GetCommandTemplate("LimitSell");

    public string OrderQueryCmd => Entry.Config.GetCommandTemplate("OrderQuery");

    public string PriceCmd => Entry.Config.GetCommandTemplate("Price");

    public string RankCmd => Entry.Config.GetCommandTemplate("Rank");

    public string RegisterCmd => Entry.Config.GetCommandTemplate("Register");

    public string ResetCmd => Entry.Config.GetCommandTemplate("Reset");

    public string SellCmd => Entry.Config.GetCommandTemplate("Sell");

    public string WithdrawCmd => Entry.Config.GetCommandTemplate("Withdraw");

    [DynamicCommand(nameof(AccountCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdAccount(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, isPrivate) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(qq);
        if (account == null)
        {
            await SendAsync(g, p, $"⚠️ 您还没有交易账户，请使用 {Entry.Config.GetTrigger("Register")} 创建");
            return EventHandleResult.Block;
        }

        await AccountService.UpdateTotalAssetAsync(account.Id);
        account = await AccountService.GetAccountAsync(qq); // 重新读取更新后的总资产

        var positions = await AccountService.GetPositionsAsync(account.Id);
        var pendingOrders = await Entry.Db!.Queryable<Models.Order>().CountAsync(o => o.AccountId == account.Id && o.Status == 0);

        // 批量获取持仓股票行情（用于计算市值和占比）
        Dictionary<string, TdxProtocol.Models.QuoteResult>? quotes = null;
        if (positions.Count > 0)
        {
            var stockList = positions
                .Select(p => StockCodeParser.ParseNormalized(p.StockCode))
                .Where(p => p.HasValue)
                .Select(p => (p.Value.market, p.Value.code))
                .Distinct()
                .ToList();
            await Entry.ConnMgr!.EnsureConnectedAsync();
            quotes = await Entry.Quotes!.GetQuotesBatchAsync(stockList);
        }

        var names = positions.Count > 0
            ? await Entry.StockNames.GetNamesAsync(positions.Select(p => p.StockCode))
            : [];

        // 计算持仓市值
        decimal totalMarketValue = 0;
        var posMarketValues = new Dictionary<long, decimal>();
        foreach (var pos in positions)
        {
            var mv = 0m;
            if (quotes != null && quotes.TryGetValue(pos.StockCode, out var q) && q.Price > 0)
            {
                mv = (decimal)q.Price * pos.Quantity;
            }
            else
            {
                mv = pos.AvgCost * pos.Quantity;
            }
            posMarketValues[pos.Id] = mv;
            totalMarketValue += mv;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("💰 账户信息");
        sb.AppendLine();
        sb.AppendLine($"💵 可用余额: ");
        sb.AppendLine($"{account.Balance:N2} 元");
        sb.AppendLine($"📦 持仓市值: ");
        sb.AppendLine($"{totalMarketValue:N2} 元");
        sb.AppendLine($"📊 总资产: ");
        sb.AppendLine($"{account.Balance + totalMarketValue:N2} 元");
        if (positions.Count > 0)
        {
            // 按市值倒序排列
            positions.Sort((a, b) => posMarketValues[b.Id].CompareTo(posMarketValues[a.Id]));

            sb.AppendLine();
            sb.AppendLine("📦 --- 持仓 ---");
            foreach (var pos in positions)
            {
                names.TryGetValue(pos.StockCode, out var stockName);
                var cost = pos.AvgCost * pos.Quantity;
                var mv = posMarketValues[pos.Id];
                var pct = totalMarketValue > 0 ? mv / totalMarketValue * 100 : 0;
                TdxProtocol.Models.QuoteResult? quote = null;
                if (quotes != null && quotes.TryGetValue(pos.StockCode, out var fetchedQuote) && fetchedQuote.Price > 0)
                {
                    quote = fetchedQuote;
                }

                var hasQuote = quote is not null;
                var currentPrice = quote is not null ? (decimal)quote.Price : pos.AvgCost;
                var lastClose = quote is not null && quote.LastClose > 0 ? (decimal)quote.LastClose : 0;

                // 持仓盈亏（相对均价）
                var gainPct = pos.AvgCost > 0 ? (currentPrice - pos.AvgCost) / pos.AvgCost * 100 : 0;
                var gainSign = gainPct >= 0 ? "+" : "";
                var gainEmoji = gainPct > 0 ? "🔴" : gainPct < 0 ? "🟢" : "⚪";

                // 当日涨跌（相对昨收）
                var dayChangePct = lastClose > 0 ? (currentPrice - lastClose) / lastClose * 100 : 0;
                var daySign = dayChangePct >= 0 ? "+" : "";
                var dayEmoji = dayChangePct > 0 ? "🔴" : dayChangePct < 0 ? "🟢" : "⚪";

                sb.AppendLine($"📋 {StockCodeParser.ToDisplayStock(stockName, pos.StockCode)}");
                sb.AppendLine($"   数量: {pos.Quantity} 股");
                sb.AppendLine($"   均价: {pos.AvgCost:F2}");
                if (hasQuote)
                {
                    sb.AppendLine($"   现价: {currentPrice:F2}  {dayEmoji} {daySign}{dayChangePct:F2}%");
                    sb.AppendLine($"   持仓盈亏: {gainEmoji} {gainSign}{gainPct:F2}%");
                }
                else
                {
                    sb.AppendLine("   现价: ⚠️ 行情获取失败");
                    sb.AppendLine("   持仓盈亏: ⚠️ 行情获取失败");
                }
                sb.AppendLine($"   成本: {cost:N2}");
                sb.AppendLine($"   市值: {mv:N2}");
                sb.AppendLine($"   (占持仓 {pct:F1}%)");
                sb.AppendLine("—————");
            }
            if (positions.Count > 0)
            {
                int lastNewLine = sb.ToString().LastIndexOf(Environment.NewLine);
                if (lastNewLine >= 0)
                {
                    sb.Remove(lastNewLine, sb.Length - lastNewLine);
                }
            }
        }
        else
        {
            sb.AppendLine("📭 当前无持仓");
        }

        if (pendingOrders > 0)
        {
            sb.AppendLine($"\n📋 当前挂单: {pendingOrders} 单");
        }

        await SendAsync(g, p, sb.ToString(), true);
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(AdminAddCmd), MatchMode.Regex, MessageScope.Group)]
    public async Task<EventHandleResult> CmdAdminAdd(GroupMessageContext e, long qq)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }
        if (!await AdminService.IsAdminAsync(e.FromGroup.Id, e.FromQQ.Id)) { await e.SendMessageAsync("仅本群插件管理员可使用此命令"); return EventHandleResult.Block; }

        var (success, err3) = await AdminService.AddAdminAsync(e.FromGroup.Id, qq);
        if (!success) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        e.Reply($"已将 QQ({qq}) 设为本群插件管理员");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(AdminListCmd), MatchMode.FullMatch, MessageScope.Group)]
    public async Task<EventHandleResult> CmdAdminList(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        var admins = await AdminService.GetAdminsAsync(e.FromGroup.Id);
        if (admins.Count == 0) { await e.SendMessageAsync("本群尚未配置插件管理员。请在管理面板中设定。"); return EventHandleResult.Block; }

        var names = new List<string>();
        foreach (var admin in admins)
        {
            try
            {
                var member = Entry.Api.GroupApi.GetGroupMemberInfo(e.FromGroup.Id, admin.QQ);
                var name = member != null ? (!string.IsNullOrEmpty(member.Card) ? member.Card : !string.IsNullOrEmpty(member.Nick) ? member.Nick : admin.QQ.ToString()) : admin.QQ.ToString();
                names.Add($"  {name} (QQ:{admin.QQ})");
            }
            catch { names.Add($"  QQ:{admin.QQ}"); }
        }
        await e.SendMessageAsync($"本群插件管理员:\n{string.Join("\n", names)}");
        return EventHandleResult.Block;
    }

    // ==================== 管理员管理（仅群聊） ====================
    [DynamicCommand(nameof(AdminRemoveCmd), MatchMode.Regex, MessageScope.Group)]
    public async Task<EventHandleResult> CmdAdminRemove(GroupMessageContext e, long qq)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }
        if (!await AdminService.IsAdminAsync(e.FromGroup.Id, e.FromQQ.Id)) { await e.SendMessageAsync("仅本群插件管理员可使用此命令"); return EventHandleResult.Block; }

        var (success, err3) = await AdminService.RemoveAdminAsync(e.FromGroup.Id, qq);
        if (!success) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        e.Reply($"已移除 QQ({qq}) 的本群插件管理员权限");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(BuyCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdBuy(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var (order, err3, fee) = await TradingService.MarketBuyAsync(qq, normalized, qty, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var stockName = await Entry.StockNames.GetNameAsync(normalized);
        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        if (quote is null)
        {
            await SendAsync(g, p, $" ✅ 市价买入成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n⚠️ 行情获取失败，无法显示成交价和金额\n手续费: {fee:F2} 元");
            return EventHandleResult.Block;
        }

        var price = (decimal)quote.Ask1;
        await SendAsync(g, p, $" ✅ 市价买入成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:N2} 元\n手续费: {fee:F2} 元");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(AllInCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdAllIn(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        if (quote == null) { await SendAsync(g, p, "行情获取失败"); return EventHandleResult.Block; }

        var check = SafetyChecker.CheckSuspension(quote.Bid1, quote.Ask1);
        if (!check.passed) { await SendAsync(g, p, check.error!); return EventHandleResult.Block; }

        if (quote.Ask1 <= 0) { await SendAsync(g, p, "该股票当前无卖盘，无法买入"); return EventHandleResult.Block; }

        var price = (decimal)quote.Ask1;
        var qty = TradingService.CalcAllInQuantity(account.Balance, price);
        if (qty < 100) { await SendAsync(g, p, $"可用余额 {account.Balance:N2} 不足以购买 1 手（需 ≈{price * 100 * 1.0003m:N2} 元）"); return EventHandleResult.Block; }

        var (order, err3, fee) = await TradingService.MarketBuyAsync(qq, normalized, qty, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var stockName = await Entry.StockNames.GetNameAsync(normalized);
        await SendAsync(g, p, $" 🥳 梭哈买入成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:N2} 元\n手续费: {fee:F2} 元");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(CancelCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdCancel(GroupMessageContext? g, PrivateMessageContext? p, long orderId)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await TradingService.CancelOrderAsync(qq, orderId);
        if (!success) { await SendAsync(g, p, err3!); return EventHandleResult.Block; }

        await SendAsync(g, p, $" ❌ 订单 {orderId} 已撤销");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(DepositCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdDeposit(GroupMessageContext? g, PrivateMessageContext? p, long qq, decimal amount)
    {
        amount = Math.Round(amount, 2);
        var callerQq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, callerQq))
        {
            return EventHandleResult.Block;
        }

        if (!await AdminService.IsAdminAsync(groupId, callerQq)) { await SendAsync(g, p, "仅本群插件管理员可执行此操作"); return EventHandleResult.Block; }

        var (success, err3) = await AccountService.DepositAsync(qq, amount);
        if (!success) { await SendAsync(g, p, err3!); return EventHandleResult.Block; }

        var account = await AccountService.GetAccountAsync(qq);
        await SendAsync(g, p, $" 💵 已向 QQ({qq}) 入金 {amount:N2} 元，当前余额: {account!.Balance:N2} 元");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(GlobalRankCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdGlobalRank(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var leaderboard = await AccountService.GetGlobalLeaderboardAsync(20);
        if (leaderboard.Count == 0) { await SendAsync(g, p, "还没有人注册交易账户"); return EventHandleResult.Block; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🌍 === 全局排行 TOP 20 ===");
        await BuildGlobalLeaderboardAsync(sb, leaderboard);

        await SendAsync(g, p, sb.ToString(), true);
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(HelpCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdHelp(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var custom = Entry.Config.CustomHelpText;
        await SendAsync(g, p, !string.IsNullOrWhiteSpace(custom) ? custom : BuildDefaultHelpText(), Entry.Config.HelpForwardSend);
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(HistoryCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdHistory(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(qq);
        if (account == null) { await SendAsync(g, p, $"⚠️ 请先使用 {Entry.Config.GetTrigger("Register")} 创建账户"); return EventHandleResult.Block; }

        var trades = await TradingService.GetTradeHistoryAsync(account.Id, 20);
        if (trades.Count == 0) { await SendAsync(g, p, "📭 暂无交易记录"); return EventHandleResult.Block; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📜 === 最近交易记录 ===");
        foreach (var t in trades)
        {
            var dir = t.TradeType == 0 ? "🔴买入" : "🟢卖出";
            var stockName = await Entry.StockNames.GetNameAsync(t.StockCode);
            sb.AppendLine($"⏰ {t.TradedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"   {dir} {StockCodeParser.ToDisplayStock(stockName, t.StockCode)}");
            sb.AppendLine($"   数量: {t.Quantity} 股");
            sb.AppendLine($"   价格: {t.Price:F2}");
            sb.AppendLine($"   金额: {t.Amount:N2}");
            sb.AppendLine();
        }
        await SendAsync(g, p, sb.ToString(), true);
        return EventHandleResult.Block;
    }

    // ==================== 订单查询 ====================
    [DynamicCommand(nameof(OrderQueryCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdOrderQuery(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(qq);
        if (account == null) { await SendAsync(g, p, $"⚠️ 请先使用 {Entry.Config.GetTrigger("Register")} 创建账户"); return EventHandleResult.Block; }

        // 限价挂单中订单（1=限价买 3=限价卖），按挂出先后排序
        var orders = await Entry.Db!.Queryable<Order>()
            .Where(o => o.AccountId == account.Id && o.Status == 0 && (o.OrderType == 1 || o.OrderType == 3))
            .OrderBy(o => o.Id)
            .ToListAsync();

        // 待执行的开盘预约（0=清仓 1=梭哈）
        var reservations = await Entry.Db!.Queryable<TomorrowOrder>()
            .Where(o => o.QQ == qq && o.Status == 0)
            .OrderBy(o => o.Id)
            .ToListAsync();

        if (orders.Count == 0 && reservations.Count == 0)
        {
            await SendAsync(g, p, "📋 当前无挂单，也无开盘预约");
            return EventHandleResult.Block;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📋 当前订单（挂单 {orders.Count} 笔 / 开盘预约 {reservations.Count} 笔）");
        sb.AppendLine();

        // --- 限价挂单 ---
        if (orders.Count == 0)
        {
            sb.AppendLine("【限价挂单】无");
        }
        else
        {
            sb.AppendLine("【限价挂单】");
            var names = await Entry.StockNames.GetNamesAsync(orders.Select(o => o.StockCode));
            for (var i = 0; i < orders.Count; i++)
            {
                var o = orders[i];
                var dir = o.OrderType == 1 ? "限价买入" : "限价卖出";
                names.TryGetValue(o.StockCode, out var stockName);
                sb.AppendLine($"{i + 1}. {dir} {StockCodeParser.ToDisplayStock(stockName, o.StockCode)}");
                sb.AppendLine($"   限价 {o.Price:F2} | 数量 {o.Quantity} 股 | 约 {o.Price * o.Quantity:N2} 元");
                sb.AppendLine($"   单号 #{o.Id} | {o.CreatedAt:M/d HH:mm} 挂出");
            }
        }

        sb.AppendLine();

        // --- 开盘预约 ---
        if (reservations.Count == 0)
        {
            sb.AppendLine("【开盘预约】无");
        }
        else
        {
            // 所有待执行预约共享同一个执行点，只在小节标题写一次
            var execTime = TomorrowOrderEngine.FormatExecutionTime(TomorrowOrderEngine.CalculateNextExecutionTime(DateTime.Now));
            sb.AppendLine($"【开盘预约】执行点：{execTime}");
            var codes = reservations.Where(r => r.StockCode != "ALL").Select(r => r.StockCode).ToList();
            var names = codes.Count > 0 ? await Entry.StockNames.GetNamesAsync(codes) : [];
            for (var i = 0; i < reservations.Count; i++)
            {
                var r = reservations[i];
                var title = r.OrderType == 1 ? "开盘梭哈" : "开盘清仓";
                var stockDesc = r.StockCode == "ALL"
                    ? "全仓"
                    : StockCodeParser.ToDisplayStock(names.TryGetValue(r.StockCode, out var n) ? n : null, r.StockCode);
                sb.AppendLine($"{i + 1}. {title} {stockDesc}");
                sb.AppendLine($"   {r.CreatedAt:M/d HH:mm} 预约");
            }
        }

        // --- 提示行（按需出现） ---
        sb.AppendLine();
        if (orders.Count > 0)
        {
            sb.AppendLine($"💡 撤单：{Entry.Config.GetTrigger("Cancel")} 单号");
        }
        if (reservations.Count > 0)
        {
            var hasClear = reservations.Any(r => r.OrderType == 0);
            var hasAllIn = reservations.Any(r => r.OrderType == 1);
            var cancelHint = hasClear && hasAllIn
                ? $"{Entry.Config.GetTrigger("TomorrowClearCancel")} 代码/全仓 或 {Entry.Config.GetTrigger("TomorrowAllInCancel")} 代码"
                : hasClear
                    ? $"{Entry.Config.GetTrigger("TomorrowClearCancel")} 代码/全仓"
                    : $"{Entry.Config.GetTrigger("TomorrowAllInCancel")} 代码";
            sb.AppendLine($"{(orders.Count > 0 ? "" : "💡 ")}取消预约：{cancelHint}");
        }

        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    // ==================== 交易操作 ====================
    [DynamicCommand(nameof(LimitBuyCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdLimitBuy(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty, decimal price)
    {
        price = Math.Round(price, 2);
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var sourceMsgId = g?.Message.Id ?? p?.Message.Id;
        var (order, err3, fee, pendingId) = await TradingService.LimitBuyAsync(qq, normalized, qty, price, sourceGroupId, sourceMsgId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentAsk = quote?.Ask1 ?? 0;
        var stockName = await Entry.StockNames.GetNameAsync(normalized);

        if (fee.HasValue)
        {
            await SendAsync(g, p, $" 🎯 限价买入已立即成交！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n成交价: {currentAsk:F2} 元\n手续费: {fee:F2} 元");
        }
        else
        {
            await SendAsync(g, p, $" 📝 限价买单已挂出！\n订单号: {pendingId ?? 0}\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前卖一: {currentAsk:F2} 元\n⏳ 当卖一价 ≤ {price:F2} 时自动成交");
        }

        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(LimitAllInCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdLimitAllIn(GroupMessageContext? g, PrivateMessageContext? p, string code, decimal price)
    {
        price = Math.Round(price, 2);
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        // 计算最大可买数量
        var qty = TradingService.CalcAllInQuantity(account.Balance, price);
        if (qty < 100) { await SendAsync(g, p, $"可用余额 {account.Balance:N2} 不足以购买 1 手（委托价 {price:F2}，需 ≈{price * 100 * 1.0003m:N2} 元）"); return EventHandleResult.Block; }

        var sourceMsgId = g?.Message.Id ?? p?.Message.Id;
        var (order, err3, fee, pendingId) = await TradingService.LimitBuyAsync(qq, normalized, qty, price, sourceGroupId, sourceMsgId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentAsk = quote?.Ask1 ?? 0;
        var stockName = await Entry.StockNames.GetNameAsync(normalized);

        if (fee.HasValue)
        {
            await SendAsync(g, p, $" 🥳 限价梭哈已立即成交！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n成交价: {currentAsk:F2} 元\n手续费: {fee:F2} 元");
        }
        else
        {
            await SendAsync(g, p, $" 🤯 限价梭哈单已挂出！\n订单号: {pendingId ?? 0}\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前卖一: {currentAsk:F2} 元\n⏳ 当卖一价 ≤ {price:F2} 时自动成交");
        }

        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(LimitSellCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdLimitSell(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty, decimal price)
    {
        price = Math.Round(price, 2);
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var sourceMsgId = g?.Message.Id ?? p?.Message.Id;
        var (order, err3, fee, pendingId) = await TradingService.LimitSellAsync(qq, normalized, qty, price, sourceGroupId, sourceMsgId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentBid = quote?.Bid1 ?? 0;
        var stockName = await Entry.StockNames.GetNameAsync(normalized);

        if (fee.HasValue)
        {
            await SendAsync(g, p, $" 🎯 限价卖出已立即成交！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n成交价: {currentBid:F2} 元\n手续费: {fee:F2} 元");
        }
        else
        {
            await SendAsync(g, p, $" 📝 限价卖单已挂出！\n订单号: {pendingId ?? 0}\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前买一: {currentBid:F2} 元\n⏳ 当买一价 ≥ {price:F2} 时自动成交");
        }

        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(PriceCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdPrice(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        var qq = GetQQ(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var name = await Entry.StockNames.GetNameAsync(normalized);
        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        if (quote == null || quote.Price <= 0) { await SendAsync(g, p, $"⚠️ 未获取到 {StockCodeParser.ToDisplayStock(name, normalized)} 的行情数据，可能不在交易时段"); return EventHandleResult.Block; }
        var isAStock = QuoteService.IsAStock(market, resolvedCode);
        var typeTag = isAStock ? "" : $" [{TdxProtocol.TdxConstants.GetSecurityTypeName(market, resolvedCode)}]";
        var changePct = quote.LastClose > 0 ? (quote.Price - quote.LastClose) / quote.LastClose * 100 : 0;
        var changeSign = changePct >= 0 ? "+" : "";
        var changeEmoji = changePct > 0 ? "🔴" : changePct < 0 ? "🟢" : "⚪";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📈 {StockCodeParser.ToDisplayStock(name, normalized)}{typeTag} 实时行情");
        sb.AppendLine($"💹 现价: {quote.Price:F2}");
        sb.AppendLine($"📅 昨收: {quote.LastClose:F2}");
        sb.AppendLine($"📊 涨跌: {changeEmoji} {changeSign}{changePct:F2}%");
        sb.AppendLine($"🔴 买一: {quote.Bid1:F2}");
        sb.AppendLine($"🟢 卖一: {quote.Ask1:F2}");
        sb.AppendLine($"📈 最高: {quote.High:F2}");
        sb.AppendLine($"📉 最低: {quote.Low:F2}");
        sb.AppendLine($"📦 成交量: {quote.Vol:N0}");
        sb.AppendLine($"💰 成交额: {quote.Amount:N0}");
        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(RankCmd), MatchMode.FullMatch, MessageScope.Group)]
    public async Task<EventHandleResult> CmdRank(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        var leaderboard = await AccountService.GetLeaderboardAsync(e.FromGroup.Id, 20);
        if (leaderboard.Count == 0) { await SendAsync(e, null, "本群还没有人注册交易账户"); return EventHandleResult.Block; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🏆 === 本群排行 TOP 20 ===");
        await BuildLeaderboardAsync(sb, leaderboard, e.FromGroup.Id);
        await SendAsync(e, null, sb.ToString(), true);
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(RegisterCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdRegister(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (account, err3) = await AccountService.CreateAccountAsync(qq);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var msg = groupId != PrivateChatGroupId
            ? $"🎉 账户注册成功！初始资金: {Entry.Config.InitialCapital:N0} 元\n输入 {Entry.Config.GetTrigger("Help")} 查看完整命令列表"
            : $"🎉 账户注册成功！初始资金: {Entry.Config.InitialCapital:N0} 元";
        await SendAsync(g, p, msg);
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(ResetCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdReset(GroupMessageContext? g, PrivateMessageContext? p, long qq)
    {
        var callerQq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, callerQq))
        {
            return EventHandleResult.Block;
        }

        if (!await AdminService.IsAdminAsync(groupId, callerQq)) { await SendAsync(g, p, "仅本群插件管理员可执行此操作"); return EventHandleResult.Block; }

        var account = await AccountService.GetAccountAsync(qq);
        if (account == null) { await SendAsync(g, p, $"QQ({qq}) 在本群没有交易账户"); return EventHandleResult.Block; }

        await AccountService.ResetAccountAsync(qq);
        await SendAsync(g, p, $" 🔄 QQ({qq}) 的账户已重置，所有数据已清空");
        return EventHandleResult.Block;
    }

    // ==================== 行情查询 ====================
    [DynamicCommand(nameof(SellCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdSell(GroupMessageContext? g, PrivateMessageContext? p, string code, string? qty)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var stockName = await Entry.StockNames.GetNameAsync(normalized);

        if (string.IsNullOrWhiteSpace(qty))
        {
            // 未指定数量：清仓
            var positions = await AccountService.GetPositionsAsync(account.Id);
            var pos = positions.FirstOrDefault(p => p.StockCode == normalized);
            if (pos == null || pos.Quantity <= 0)
            {
                await SendAsync(g, p, $"⚠️ 您没有持有 {StockCodeParser.ToDisplayStock(stockName, normalized)}，无法清仓");
                return EventHandleResult.Block;
            }

            int sellQty = pos.Quantity;

            var (order, err3, fee) = await TradingService.MarketSellAsync(qq, normalized, sellQty, sourceGroupId);
            if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

            var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
            if (quote is null)
            {
                await SendAsync(g, p, $" ✅ 清仓成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {sellQty} 股\n⚠️ 行情获取失败，无法显示成交价和金额\n手续费: {fee:F2} 元");
                return EventHandleResult.Block;
            }

            var price = (decimal)quote.Bid1;
            await SendAsync(g, p, $" ✅ 清仓成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {sellQty} 股\n成交价: {price:F2} 元\n金额: {price * sellQty:N2} 元\n手续费: {fee:F2} 元");
        }
        else if (int.TryParse(qty, out var parsedQty) && parsedQty > 0)
        {
            // 指定数量：市价卖出
            var (order, err3, fee) = await TradingService.MarketSellAsync(qq, normalized, parsedQty, sourceGroupId);
            if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

            var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
            if (quote is null)
            {
                await SendAsync(g, p, $" ✅ 市价卖出成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {parsedQty} 股\n⚠️ 行情获取失败，无法显示成交价和金额\n手续费: {fee:F2} 元");
                return EventHandleResult.Block;
            }

            var price = (decimal)quote.Bid1;
            await SendAsync(g, p, $" ✅ 市价卖出成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {parsedQty} 股\n成交价: {price:F2} 元\n金额: {price * parsedQty:N2} 元\n手续费: {fee:F2} 元");
        }
        else
        {
            await SendAsync(g, p, $"⚠️ 数量格式错误：{qty}，请输入正整数");
        }
        return EventHandleResult.Block;
    }

    // ==================== 清仓操作 ====================
    [DynamicCommand(nameof(ClearOneCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdClearOne(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var stockName = await Entry.StockNames.GetNameAsync(normalized);

        // 获取持仓数量
        var positions = await AccountService.GetPositionsAsync(account.Id);
        var pos = positions.FirstOrDefault(p => p.StockCode == normalized);
        if (pos == null || pos.Quantity <= 0)
        {
            await SendAsync(g, p, $"⚠️ 您没有持有 {StockCodeParser.ToDisplayStock(stockName, normalized)}，无法清仓");
            return EventHandleResult.Block;
        }

        int qty = pos.Quantity;

        var (order, err3, fee) = await TradingService.MarketSellAsync(qq, normalized, qty, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        if (quote is null)
        {
            await SendAsync(g, p, $" ✅ 清仓成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n⚠️ 行情获取失败，无法显示成交价和金额\n手续费: {fee:F2} 元");
            return EventHandleResult.Block;
        }

        var price = (decimal)quote.Bid1;
        await SendAsync(g, p, $" ✅ 清仓成功！\n股票: {StockCodeParser.ToDisplayStock(stockName, normalized)}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:N2} 元\n手续费: {fee:F2} 元");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(ClearAllCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdClearAll(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        // 获取所有持仓
        var positions = await AccountService.GetPositionsAsync(account.Id);
        if (positions.Count == 0)
        {
            await SendAsync(g, p, "⚠️ 您当前无持仓，无需清仓");
            return EventHandleResult.Block;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🔄 开始清仓...");

        int successCount = 0;
        int skipCount = 0;

        foreach (var pos in positions)
        {
            var parsed = StockCodeParser.ParseNormalized(pos.StockCode);
            if (!parsed.HasValue)
            {
                sb.AppendLine($"⚠️ {pos.StockCode}: 股票代码格式错误");
                skipCount++;
                continue;
            }

            var (market, code) = parsed.Value;
            var displayName = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(pos.StockCode), pos.StockCode);

            // 检查 T+1
            var t1Check = await SafetyChecker.CheckT1RuleAsync(Entry.Db!, account.Id, pos.StockCode);
            if (!t1Check.passed)
            {
                sb.AppendLine($"⚠️ {displayName}: {t1Check.error}");
                skipCount++;
                continue;
            }

            // 获取行情
            var quote = await Entry.Quotes!.GetQuoteAsync(market, code);
            if (quote == null)
            {
                sb.AppendLine($"⚠️ {displayName}: 行情获取失败");
                skipCount++;
                continue;
            }

            // 检查停牌
            var suspensionCheck = SafetyChecker.CheckSuspension(quote.Bid1, quote.Ask1);
            if (!suspensionCheck.passed)
            {
                sb.AppendLine($"⚠️ {displayName}: {suspensionCheck.error}");
                skipCount++;
                continue;
            }

            if (quote.Bid1 <= 0)
            {
                sb.AppendLine($"⚠️ {displayName}: 无买盘，无法卖出");
                skipCount++;
                continue;
            }

            // 执行卖出
            var (order, sellErr, fee) = await TradingService.MarketSellAsync(qq, pos.StockCode, pos.Quantity, sourceGroupId);
            if (sellErr != null)
            {
                sb.AppendLine($"⚠️ {displayName}: {sellErr}");
                skipCount++;
            }
            else
            {
                var sellPrice = (decimal)quote.Bid1;
                sb.AppendLine($"✅ {displayName} 卖出 {pos.Quantity} 股 @ {sellPrice:F2} 元");
                successCount++;
            }
        }

        sb.AppendLine($"\n📊 清仓完成: 成功 {successCount} 只, 跳过 {skipCount} 只");

        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    // ==================== 开盘清仓 ====================
    [DynamicCommand(nameof(TomorrowClearCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdTomorrowClear(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            await SendAsync(g, p, "⚠️ 请指定股票代码或全仓，如 #开盘清仓 000001 或 #开盘清仓 全仓");
            return EventHandleResult.Block;
        }

        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        // 检查是否在交易时段——在交易时段应该直接清仓，不要预约
        var (th, err) = SafetyChecker.CheckTradingHours();
        if (th)
        {
            await SendAsync(g, p, "⚠️ 当前为交易时段，请直接使用 /全部清仓 或 /清仓 代码");
            return EventHandleResult.Block;
        }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        string normalizedCode;
        string displayStock;

        if (code == "全仓")
        {
            normalizedCode = "ALL";
            displayStock = "全仓";
        }
        else
        {
            if (Entry.Quotes == null) { await SendAsync(g, p, "⚠️ 行情服务未就绪，请稍后重试"); return EventHandleResult.Block; }
            var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes.ResolveCodeAsync(code);
            if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }
            normalizedCode = normalized;
            displayStock = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(normalized), normalized);
        }

        // 检查是否有持仓
        var positions = await AccountService.GetPositionsAsync(account.Id);
        if (normalizedCode == "ALL")
        {
            if (positions.Count == 0)
            {
                await SendAsync(g, p, "⚠️ 您当前无持仓，无需清仓");
                return EventHandleResult.Block;
            }
        }
        else
        {
            var pos = positions.FirstOrDefault(x => x.StockCode == normalizedCode);
            if (pos == null || pos.Quantity <= 0)
            {
                await SendAsync(g, p, $"⚠️ 您未持有 {displayStock}，无需清仓");
                return EventHandleResult.Block;
            }
        }

        // 检查是否已有待执行的同股票代码订单
        var existing = await Entry.Db!.Queryable<TomorrowOrder>()
            .AnyAsync(o => o.QQ == qq && o.StockCode == normalizedCode && o.Status == 0);
        if (existing)
        {
            await SendAsync(g, p, $"⚠️ 已存在 {displayStock} 的待执行清仓订单");
            return EventHandleResult.Block;
        }

        // 创建订单
        var order = new TomorrowOrder
        {
            QQ = qq,
            GroupId = sourceGroupId ?? 0,
            StockCode = normalizedCode,
            Status = 0,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        await Entry.Db.Insertable(order).ExecuteCommandAsync();

        var execTime = TomorrowOrderEngine.FormatExecutionTime(TomorrowOrderEngine.CalculateNextExecutionTime(DateTime.Now));
        await SendAsync(g, p, $"✅ 已预约开盘清仓：{displayStock}，将在 {execTime} 执行\nℹ️ 若执行日逢节假日将自动顺延至下一交易日");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(TomorrowClearCancelCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdTomorrowClearCancel(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            await SendAsync(g, p, "⚠️ 请指定股票代码或全仓，如 #取消开盘清仓 000001 或 #取消开盘清仓 全仓");
            return EventHandleResult.Block;
        }

        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        string normalizedCode;
        string displayStock;

        if (code == "全仓")
        {
            normalizedCode = "ALL";
            displayStock = "全仓";
        }
        else
        {
            if (Entry.Quotes == null) { await SendAsync(g, p, "⚠️ 行情服务未就绪，请稍后重试"); return EventHandleResult.Block; }
            var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes.ResolveCodeAsync(code);
            if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }
            normalizedCode = normalized;
            displayStock = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(normalized), normalized);
        }

        var pendingOrders = await Entry.Db!.Queryable<TomorrowOrder>()
            .Where(o => o.QQ == qq && o.StockCode == normalizedCode && o.Status == 0)
            .ToListAsync();

        if (pendingOrders.Count == 0)
        {
            await SendAsync(g, p, $"⚠️ 未找到 {displayStock} 的待执行清仓订单");
            return EventHandleResult.Block;
        }

        foreach (var order in pendingOrders)
        {
            order.Status = 2;
            order.UpdatedAt = DateTime.Now;
            await Entry.Db.Updateable(order).ExecuteCommandAsync();
        }

        await SendAsync(g, p, $"✅ 已取消 {displayStock} 的开盘清仓预约（{pendingOrders.Count} 单）");
        return EventHandleResult.Block;
    }

    // ==================== 开盘梭哈 ====================
    [DynamicCommand(nameof(TomorrowAllInCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdTomorrowAllIn(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            await SendAsync(g, p, "⚠️ 请指定股票代码，如 #开盘梭哈 000001");
            return EventHandleResult.Block;
        }

        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        // 检查是否在交易时段——在交易时段应该直接梭哈，不要预约
        var (th, err) = SafetyChecker.CheckTradingHours();
        if (th)
        {
            await SendAsync(g, p, $"⚠️ 当前为交易时段，请直接使用 {Entry.Config.GetTrigger("AllIn")} 代码");
            return EventHandleResult.Block;
        }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        if (Entry.Quotes == null) { await SendAsync(g, p, "⚠️ 行情服务未就绪，请稍后重试"); return EventHandleResult.Block; }
        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }
        var displayStock = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(normalized), normalized);

        // 检查是否已有待执行的同代码开盘订单（清仓或梭哈都不能重复预约，避免开盘先卖后买自相矛盾）
        var existing = await Entry.Db!.Queryable<TomorrowOrder>()
            .AnyAsync(o => o.QQ == qq && o.StockCode == normalized && o.Status == 0);
        if (existing)
        {
            await SendAsync(g, p, $"⚠️ 已存在 {displayStock} 的待执行开盘订单，请先取消后再预约");
            return EventHandleResult.Block;
        }

        // 创建订单
        var order = new TomorrowOrder
        {
            QQ = qq,
            GroupId = sourceGroupId ?? 0,
            StockCode = normalized,
            OrderType = 1,
            Status = 0,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        await Entry.Db.Insertable(order).ExecuteCommandAsync();

        var execTime = TomorrowOrderEngine.FormatExecutionTime(TomorrowOrderEngine.CalculateNextExecutionTime(DateTime.Now));
        await SendAsync(g, p, $"✅ 已预约开盘梭哈：{displayStock}，将在 {execTime} 用全部可用资金买入\nℹ️ 若执行日逢节假日将自动顺延至下一交易日");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(TomorrowAllInCancelCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdTomorrowAllInCancel(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            await SendAsync(g, p, "⚠️ 请指定股票代码，如 #取消开盘梭哈 000001");
            return EventHandleResult.Block;
        }

        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        if (Entry.Quotes == null) { await SendAsync(g, p, "⚠️ 行情服务未就绪，请稍后重试"); return EventHandleResult.Block; }
        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }
        var displayStock = StockCodeParser.ToDisplayStock(await Entry.StockNames.GetNameAsync(normalized), normalized);

        var pendingOrders = await Entry.Db!.Queryable<TomorrowOrder>()
            .Where(o => o.QQ == qq && o.StockCode == normalized && o.OrderType == 1 && o.Status == 0)
            .ToListAsync();

        if (pendingOrders.Count == 0)
        {
            await SendAsync(g, p, $"⚠️ 未找到 {displayStock} 的待执行开盘梭哈订单");
            return EventHandleResult.Block;
        }

        foreach (var order in pendingOrders)
        {
            order.Status = 2;
            order.UpdatedAt = DateTime.Now;
            await Entry.Db.Updateable(order).ExecuteCommandAsync();
        }

        await SendAsync(g, p, $"✅ 已取消 {displayStock} 的开盘梭哈预约（{pendingOrders.Count} 单）");
        return EventHandleResult.Block;
    }

    // ==================== 授信额度 ====================
    [DynamicCommand(nameof(CreditCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdCredit(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (account, err) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        // 存量账号兼容：建号早于授信功能、CreditLimit 未初始化的账号，查询时兜底同步为配置额度
        if (account.CreditLimit <= 0)
        {
            account.CreditLimit = Entry.Config.EffectiveCreditAmount;
            account.UpdatedAt = DateTime.Now;
            await Entry.Db!.Updateable(account).ExecuteCommandAsync();
        }

        // 计算待还利息
        var interest = SafetyChecker.CalculateInterest(account, Entry.Config);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("💰 授信额度");
        sb.AppendLine();
        sb.AppendLine($"💵 可用余额: {account.Balance:N2} 元");
        sb.AppendLine($"🏦 授信额度: {account.CreditLimit:N2} 元");
        sb.AppendLine($"📉 当前费率: {Entry.Config.CreditInterestRate * 100m:0.#####}");
        sb.AppendLine($"📊 待还额度: {account.DebtBalance:N2} 元");
        sb.AppendLine($"💸 待还利息: {interest:N2} 元");

        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(CreditUseCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdCreditUse(GroupMessageContext? g, PrivateMessageContext? p, string amount)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (account, err) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        // 加账户并发锁：避免并发借款同时通过额度检查导致超额
        var sem = TradingService.GetAccountLock(account.Id);
        await sem.WaitAsync();
        try
        {
            account = await Entry.Db!.Queryable<Account>().FirstAsync(a => a.Id == account.Id);
            if (account == null) { await SendAsync(g, p, "账户异常"); return EventHandleResult.Block; }

            // 存量账号兼容：CreditLimit 未初始化的账号，借款前兑底同步为配置额度
            if (account.CreditLimit <= 0)
            {
                account.CreditLimit = Entry.Config.EffectiveCreditAmount;
                account.UpdatedAt = DateTime.Now;
                await Entry.Db.Updateable(account).ExecuteCommandAsync();
            }

            // 解析金额（支持 '梭哈' 表示用满剩余额度）
            decimal requestedAmount;
            bool isMax = amount == "梭哈";
            if (isMax)
            {
                var remainingLimit = account.CreditLimit - account.DebtBalance;
                if (remainingLimit <= 0)
                {
                    await SendAsync(g, p, "⚠️ 当前无剩余额度可用，请先偿还部分授信");
                    return EventHandleResult.Block;
                }
                requestedAmount = remainingLimit;
            }
            else
            {
                if (!decimal.TryParse(amount, out var parsed) || parsed <= 0)
                {
                    await SendAsync(g, p, "⚠️ 借款金额格式错误，请输入数字或 梭哈");
                    return EventHandleResult.Block;
                }

                decimal usableLimit = account.CreditLimit - account.DebtBalance;
                if (parsed > usableLimit)
                {
                    await SendAsync(g, p, $"⚠️ 借款金额超过剩余额度，剩余额度为 {usableLimit:N2} 元");
                    return EventHandleResult.Block;
                }
                requestedAmount = parsed;
            }

            // 执行借入
            await Entry.Db.UseTranAsync(async () =>
            {
                account.Balance += requestedAmount;
                account.DebtBalance += requestedAmount;
                account.LastInterestCalculated = DateTime.Now;
                account.UpdatedAt = DateTime.Now;
                await Entry.Db.Updateable(account).ExecuteCommandAsync();

                await Entry.Db.Insertable(new CreditRecord
                {
                    AccountId = account.Id,
                    Type = 1,
                    Amount = requestedAmount,
                    Interest = 0,
                    CreatedAt = DateTime.Now,
                    SourceMessageId = sourceGroupId
                }).ExecuteCommandAsync();
            });

            // 刷新总资产（扣除负债后），保持排行榜账目一致
            await AccountService.UpdateTotalAssetAsync(account.Id);

            await SendAsync(g, p, $"✅ 借款成功！借款金额: {requestedAmount:N2} 元\n当前授信额度: {account.CreditLimit:N2} 元\n当前待还本金: {account.DebtBalance:N2} 元");
            return EventHandleResult.Block;
        }
        finally { sem.Release(); }
    }

    [DynamicCommand(nameof(CreditRepayCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdCreditRepay(GroupMessageContext? g, PrivateMessageContext? p, string amount)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (account, err) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq);
        if (account == null) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        // 加账户并发锁：避免并发还款/借款交错导致账目不一致
        var sem = TradingService.GetAccountLock(account.Id);
        await sem.WaitAsync();
        try
        {
            account = await Entry.Db!.Queryable<Account>().FirstAsync(a => a.Id == account.Id);
            if (account == null) { await SendAsync(g, p, "账户异常"); return EventHandleResult.Block; }

            if (!decimal.TryParse(amount, out var requestedAmount) || requestedAmount <= 0)
            {
                await SendAsync(g, p, "⚠️ 还款金额格式错误");
                return EventHandleResult.Block;
            }

            // 拒绝超额还款
            if (requestedAmount > account.DebtBalance)
            {
                await SendAsync(g, p, $"⚠️ 还款金额超过待还本金，当前待还 {account.DebtBalance:N2} 元");
                return EventHandleResult.Block;
            }

            // 计算当前待还利息
            var interest = SafetyChecker.CalculateInterest(account, Entry.Config);
            var totalRepay = requestedAmount + interest;

            if (account.Balance < totalRepay)
            {
                await SendAsync(g, p, $"⚠️ 余额不足，需要 {totalRepay:N2} 元（含利息 {interest:N2} 元）");
                return EventHandleResult.Block;
            }

            // 执行偿还
            await Entry.Db!.UseTranAsync(async () =>
            {
                account.Balance -= totalRepay;
                account.DebtBalance -= requestedAmount;
                account.LastInterestCalculated = DateTime.Now;
                account.UpdatedAt = DateTime.Now;
                await Entry.Db.Updateable(account).ExecuteCommandAsync();

                await Entry.Db.Insertable(new CreditRecord
                {
                    AccountId = account.Id,
                    Type = 2,
                    Amount = requestedAmount,
                    Interest = interest,
                    CreatedAt = DateTime.Now,
                    SourceMessageId = sourceGroupId
                }).ExecuteCommandAsync();
            });

            // 刷新总资产（扣除负债后），保持排行榜账目一致
            await AccountService.UpdateTotalAssetAsync(account.Id);

            await SendAsync(g, p, $"✅ 还款成功！偿还本金: {requestedAmount:N2} 元\n支付利息: {interest:N2} 元\n当前待还本金: {account.DebtBalance:N2} 元");
            return EventHandleResult.Block;
        }
        finally { sem.Release(); }
    }

    // ==================== 账户管理 ====================
    [DynamicCommand(nameof(WithdrawCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdWithdraw(GroupMessageContext? g, PrivateMessageContext? p, decimal amount)
    {
        amount = Math.Round(amount, 2);
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await AccountService.WithdrawAsync(qq, amount);
        if (!success) { await SendAsync(g, p, err3!); return EventHandleResult.Block; }

        var account = await AccountService.GetAccountAsync(qq);
        await SendAsync(g, p, $" 💸 出金 {amount:N2} 元成功！当前余额: {account!.Balance:N2} 元");
        return EventHandleResult.Block;
    }

    private static void AppendRankRows(System.Text.StringBuilder sb, List<Account> accounts, Dictionary<long, string> nameCache)
    {
        for (int i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            var marketValue = a.TotalAsset - a.Balance;
            var medal = i == 0 ? "🥇" : i == 1 ? "🥈" : i == 2 ? "🥉" : $"{i + 1}.";
            var name = nameCache.TryGetValue(a.QQ, out var n) ? n : a.QQ.ToString();
            sb.AppendLine($"{medal} {name}");
            sb.AppendLine($"   📊 总资产: {a.TotalAsset:N0}");
            sb.AppendLine($"   💵 可用余额: {a.Balance:N0}");
            sb.AppendLine($"   💹 持仓数量: {AccountService.GetPositionsAsync(a.Id).Result.Count}");
            sb.AppendLine($"   📦 持仓市值: {marketValue:N0}");
            sb.AppendLine();
        }
    }

    private static string BuildDefaultHelpText()
    {
        var t = (string name) => Entry.Config.GetTrigger(name);
        return $"""
            🌿 === 水银韭菜机 帮助 ===

            💰 【账户管理】
            {t("Register")}          创建账户，获得初始资金
            {t("Account")}          查看余额、持仓、挂单
            {t("Withdraw")} 金额     取出账户资金

            🔧 【管理员命令】
            {t("Deposit")} QQ 金额  为指定用户增加资金
            {t("Reset")} QQ       重置指定用户的账户
            {t("AdminAdd")} QQ  添加插件管理员
            {t("AdminRemove")} QQ  移除插件管理员
            {t("AdminList")}     查看本群管理员

            📈 【行情查询】
            {t("Price")} 代码     查询实时股价
              示例: {t("Price")} sz000001
              前缀: sz深市 sh沪市 bj北交所

            💹 【交易操作】
            {t("Buy")} 代码 数量 市价买入
            {t("Sell")} 代码 [数量]  市价卖出（无数量=清仓）
            {t("AllIn")} 代码     梭哈买入（余额全仓）
            {t("LimitAllIn")} 代码 价格  限价梭哈（余额全仓）
            {t("LimitBuy")} 代码 数量 价格  挂限价买单
            {t("LimitSell")} 代码 数量 价格  挂限价卖单
            {t("Cancel")} 订单号   撤销挂单
            {t("ClearOne")} 代码     清仓指定股票（全仓卖出）
            {t("ClearAll")}          清仓全部持仓
            {t("TomorrowClear")} 代码/全仓  预约下一开盘（9:31/13:01）清仓
            {t("TomorrowClearCancel")} 代码/全仓  取消预约开盘清仓
            {t("TomorrowAllIn")} 代码   预约下一开盘（9:31/13:01）全仓买入
            {t("TomorrowAllInCancel")} 代码  取消预约开盘梭哈
            {t("Credit")}            查询授信额度与欠款
            {t("CreditUse")} 金额   使用授信借款（梭哈表示用满剩余额度）
            {t("CreditRepay")} 金额 偿还授信本金

            🔍 【信息查询】
            {t("Rank")}          本群交易排行榜
            {t("GlobalRank")}          全局交易排行榜
            {t("History")}          个人交易历史
            {t("OrderQuery")}          查看挂单与开盘预约
            {t("Help")}          显示本帮助

            ⚠️ 【交易规则】
            - T+1制度: 当日买入的股票次日方可卖出
            - 盘口限制: 买入需要卖一报价，卖出需要买一报价；无对手盘时无法成交
            - 停牌股票无法交易
            - 手续费: 成交金额的0.03%，最低5元
            - 仅支持A股交易（不含指数/基金/债券）
            - 交易单位: 100股（1手）的整数倍
            - 交易时段: 工作日 9:30-11:30 13:00-15:00
            - 限价单在满足条件时自动成交
            - 不明确交易所时请加前缀，如 {t("Buy")} sz000001 100
            """;
    }

    // ==================== 信息查询 ====================
    private static async Task BuildLeaderboardAsync(System.Text.StringBuilder sb, List<Account> accounts, long groupId)
    {
        var nameCache = await ResolveNicknamesAsync(accounts, groupId);
        AppendRankRows(sb, accounts, nameCache);
    }

    /// <summary>全局排行：从 UserGroups 查每个用户所在群来获取昵称</summary>
    private static async Task BuildGlobalLeaderboardAsync(System.Text.StringBuilder sb, List<Account> accounts)
    {
        var result = new Dictionary<long, string>();
        var now = DateTime.Now;

        // 先检查缓存，收集未命中的 QQ
        var uncached = new List<long>();
        foreach (var a in accounts)
        {
            if (result.ContainsKey(a.QQ)) continue;
            if (NicknameCache.TryGetValue(a.QQ, out var cached) && cached.Expiry > now)
            {
                result[a.QQ] = cached.Name;
            }
            else
            {
                uncached.Add(a.QQ);
            }
        }

        if (uncached.Count > 0)
        {
            // 查出这些用户的 UserGroup
            var userGroups = await Entry.Db!.Queryable<Models.UserGroup>()
                .Where(ug => uncached.Contains(ug.QQ))
                .ToListAsync();
            var groupLookup = userGroups
                .GroupBy(ug => ug.QQ)
                .ToDictionary(g => g.Key, g => g.First().GroupId);

            foreach (var qq in uncached)
            {
                if (result.ContainsKey(qq)) continue;

                string name;
                if (groupLookup.TryGetValue(qq, out var gid))
                {
                    try
                    {
                        var member = Entry.Api.GroupApi.GetGroupMemberInfo(gid, qq);
                        name = member != null
                            ? (!string.IsNullOrEmpty(member.Card) ? member.Card : !string.IsNullOrEmpty(member.Nick) ? member.Nick : qq.ToString())
                            : qq.ToString();
                    }
                    catch { name = qq.ToString(); }
                }
                else
                {
                    // 纯私聊用户，尝试好友列表
                    var friendNick = await GetFriendNicknameAsync(qq);
                    name = friendNick ?? qq.ToString();
                }

                result[qq] = name;
                NicknameCache[qq] = (name, now + NicknameCacheTtl);
            }
        }
        AppendRankRows(sb, accounts, result);
    }

    /// <summary>从好友列表获取昵称（带缓存），未找到返回 null</summary>
    private static async Task<string?> GetFriendNicknameAsync(long qq)
    {
        await _friendListLock.WaitAsync();
        try
        {
            if (_friendListCache == null || (DateTime.Now - _friendListCacheTime) > NicknameCacheTtl)
            {
                try
                {
                    var friends = await Entry.Api.FriendApi.GetFriendInfosAsync();
                    _friendListCache = friends.ToDictionary(f => f.QQ, f =>
                        !string.IsNullOrEmpty(f.Nick) ? f.Nick : f.QQ.ToString());
                    _friendListCacheTime = DateTime.Now;
                }
                catch
                {
                    _friendListCache ??= [];
                    _friendListCacheTime = DateTime.Now;
                }
            }
            return _friendListCache.TryGetValue(qq, out var nick) ? nick : null;
        }
        finally { _friendListLock.Release(); }
    }

    private static async Task<Dictionary<long, string>> ResolveNicknamesAsync(List<Account> accounts, long groupId)
    {
        var result = new Dictionary<long, string>();
        var now = DateTime.Now;

        foreach (var a in accounts)
        {
            if (result.ContainsKey(a.QQ)) continue;

            // 先查缓存
            if (NicknameCache.TryGetValue(a.QQ, out var cached) && cached.Expiry > now)
            {
                result[a.QQ] = cached.Name;
                continue;
            }

            // API 拉取
            string name;
            try
            {
                var member = Entry.Api.GroupApi.GetGroupMemberInfo(groupId, a.QQ);
                name = member != null
                    ? (!string.IsNullOrEmpty(member.Card) ? member.Card : !string.IsNullOrEmpty(member.Nick) ? member.Nick : a.QQ.ToString())
                    : a.QQ.ToString();
            }
            catch { name = a.QQ.ToString(); }

            result[a.QQ] = name;
            NicknameCache[a.QQ] = (name, now + NicknameCacheTtl);
        }
        return result;
    }

    /// <summary>
    /// 访问检查：私聊跳过白名单，群聊检查白名单；统一检查黑名单
    /// </summary>
    private static async Task<bool> CheckAccess(GroupMessageContext? g, PrivateMessageContext? p, long qq)
    {
        if (g != null)
        {
            var (w, _) = SafetyChecker.CheckGroupWhitelist(g.FromGroup.Id);
            if (!w)
            {
                return false;
            }
            await AccountService.RecordGroupInteractionAsync(qq, g.FromGroup.Id);
        }
        var (b, _) = SafetyChecker.CheckUserBlacklist(qq);
        return b;
    }

    private static long GetQQ(GroupMessageContext? g, PrivateMessageContext? p)
    {
        return (g ?? (object?)p) switch
        {
            GroupMessageContext gc => gc.FromQQ.Id,
            PrivateMessageContext pc => pc.FromQQ.Id,
            _ => 0
        };
    }

    /// <summary>
    /// 从上下文解析来源：群聊返回 (groupId, sourceGroupId)，私聊返回 (0, null)
    /// </summary>
    private static (long groupId, long? sourceGroupId, bool isPrivate) ResolveCtx(GroupMessageContext? g, PrivateMessageContext? p)
    {
        if (g != null)
        {
            return (g.FromGroup.Id, g.FromGroup.Id, false);
        }

        return (PrivateChatGroupId, null, true);
    }

    /// <summary>
    /// 向来源回复消息：群聊和私聊均引用原消息回复
    /// </summary>
    private static async Task SendAsync(GroupMessageContext? g, PrivateMessageContext? p, string msg, bool forward = false)
    {
        if (!forward)
        {
            if (g != null)
            {
                await g.ReplyAsync(msg);
            }
            else
            {
                await p!.ReplyAsync(msg);
            }
        }
        else
        {
            if (g != null)
            {
                await Entry.Api.MessageApi.SendGroupForwardMessageAsync(g.FromGroup.Id, [msg]);
            }
            else
            {
                await Entry.Api.MessageApi.SendPrivateForwardMessageAsync(p!.FromQQ.Id, [msg]);
            }
        }
    }

    // ==================== 辅助方法 ====================

}
