using SimStock.Models;
using SqlSugar;

namespace SimStock;

/// <summary>
/// 账户管理服务。每个 QQ 全局唯一账户，跨群共享资金。
/// 通过 UserGroup 表记录用户在哪些群交互过，用于群排行过滤。
/// </summary>
public static class AccountService
{
    private static SqlSugarScope Db => Entry.Db!;

    public static async Task<Account?> GetAccountAsync(long qq)
    {
        return await Db.Queryable<Account>()
            .FirstAsync(a => a.QQ == qq);
    }

    public static async Task<(Account? account, string? error)> CreateAccountAsync(long qq)
    {
        var existing = await GetAccountAsync(qq);
        if (existing != null)
        {
            return (existing, "您已注册过交易账户，请使用 /股票账户 查看");
        }

        var account = new Account
        {
            QQ = qq,
            Balance = Entry.Config.InitialCapital,
            TotalAsset = Entry.Config.InitialCapital,
            CreditLimit = Entry.Config.EffectiveCreditAmount,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var id = await Db.Insertable(account).ExecuteReturnBigIdentityAsync();
        account.Id = id;
        return (account, null);
    }

    /// <summary>记录用户在指定群的交互（幂等，已存在则忽略）</summary>
    public static async Task RecordGroupInteractionAsync(long qq, long groupId)
    {
        if (groupId == 0) return; // 私聊不记录
        try
        {
            await Db.Insertable(new UserGroup { QQ = qq, GroupId = groupId }).ExecuteCommandAsync();
        }
        catch
        {
            // 唯一约束冲突，已存在则忽略
        }
    }

    public static async Task<(bool success, string? error)> DepositAsync(long qq, decimal amount)
    {
        if (amount <= 0)
        {
            return (false, "入金金额必须大于0");
        }

        var account = await GetAccountAsync(qq);
        if (account == null)
        {
            return (false, $"请先使用 {Entry.Config.GetTrigger("Register")} 创建账户");
        }

        account.Balance += amount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
        await UpdateTotalAssetAsync(account.Id);
        return (true, null);
    }

    public static async Task<(bool success, string? error)> WithdrawAsync(long qq, decimal amount)
    {
        if (amount <= 0)
        {
            return (false, "出金金额必须大于0");
        }

        var account = await GetAccountAsync(qq);
        if (account == null)
        {
            return (false, $"请先使用 {Entry.Config.GetTrigger("Register")} 创建账户");
        }

        if (amount > account.Balance)
        {
            return (false, $"出金失败，可用余额 {account.Balance:N2} 元，不足 {amount:N2} 元");
        }

        account.Balance -= amount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
        await UpdateTotalAssetAsync(account.Id);
        return (true, null);
    }

    public static async Task ResetAccountAsync(long qq)
    {
        var account = await GetAccountAsync(qq);
        if (account == null)
        {
            return;
        }

        await Db.UseTranAsync(async () =>
        {
            await Db.Deleteable<Order>().Where(o => o.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<TradeRecord>().Where(t => t.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<Position>().Where(p => p.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<UserGroup>().Where(ug => ug.QQ == qq).ExecuteCommandAsync();
            await Db.Deleteable<CreditRecord>().Where(c => c.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<TomorrowOrder>().Where(t => t.QQ == qq).ExecuteCommandAsync();
            await Db.Deleteable<Account>().Where(a => a.Id == account.Id).ExecuteCommandAsync();
        });
    }

    /// <summary>本群排行：在该群交互过的用户按总资产降序</summary>
    public static async Task<List<Account>> GetLeaderboardAsync(long groupId, int top = 20)
    {
        var accounts = await Db.Queryable<Account>()
            .InnerJoin<UserGroup>((a, ug) => a.QQ == ug.QQ && ug.GroupId == groupId)
            .OrderBy((a) => a.TotalAsset, OrderByType.Desc)
            .Take(top)
            .ToListAsync();
        await RefreshTotalAssetsAsync(accounts);
        accounts.Sort((a, b) => b.TotalAsset.CompareTo(a.TotalAsset));
        return accounts;
    }

    /// <summary>全局排行：所有用户按总资产降序</summary>
    public static async Task<List<Account>> GetGlobalLeaderboardAsync(int top = 20)
    {
        var accounts = await Db.Queryable<Account>()
            .OrderBy(a => a.TotalAsset, OrderByType.Desc)
            .Take(top)
            .ToListAsync();
        await RefreshTotalAssetsAsync(accounts);
        accounts.Sort((a, b) => b.TotalAsset.CompareTo(a.TotalAsset));
        return accounts;
    }

    public static async Task<List<Position>> GetPositionsAsync(long accountId)
    {
        return await Db.Queryable<Position>()
            .Where(p => p.AccountId == accountId && p.Quantity > 0)
            .ToListAsync();
    }

    /// <summary>更新账户总资产 = 现金余额 + 持仓市值（实时行情）</summary>
    public static async Task UpdateTotalAssetAsync(long accountId)
    {
        var account = await Db.Queryable<Account>().FirstAsync(a => a.Id == accountId);
        if (account == null) return;

        var positions = await GetPositionsAsync(accountId);
        if (positions.Count == 0)
        {
            account.TotalAsset = account.Balance - account.DebtBalance;
            account.CreditLimit = Entry.Config.EffectiveCreditAmount;
            account.UpdatedAt = DateTime.Now;
            await Db.Updateable(account).ExecuteCommandAsync();
            return;
        }

        // 批量获取行情
        var stocks = positions
            .Select(p => StockCodeParser.ParseNormalized(p.StockCode))
            .Where(p => p.HasValue)
            .Select(p => (p.Value.market, p.Value.code))
            .ToList();

        var quotes = await Entry.Quotes!.GetQuotesBatchAsync(stocks);
        decimal marketValue = 0;
        foreach (var pos in positions)
        {
            var normalized = pos.StockCode;
            if (quotes != null && quotes.TryGetValue(normalized, out var quote) && quote.Price > 0)
                marketValue += (decimal)quote.Price * pos.Quantity;
        }

        account.TotalAsset = account.Balance + marketValue - account.DebtBalance;
        // 同步更新授信额度（生效额度：显式保存值或初始资金默认值）
        account.CreditLimit = Entry.Config.EffectiveCreditAmount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
    }

    /// <summary>批量刷新账户总资产：一次查询所有持仓的行情后重新计算</summary>
    private static async Task RefreshTotalAssetsAsync(List<Account> accounts)
    {
        if (accounts.Count == 0) return;

        var accountIds = accounts.Select(a => a.Id).ToList();
        var allPositions = await Db.Queryable<Position>()
            .Where(p => accountIds.Contains(p.AccountId) && p.Quantity > 0)
            .ToListAsync();

        if (allPositions.Count == 0)
        {
            // 无持仓时也同步授信额度
            foreach (var account in accounts)
            {
                account.CreditLimit = Entry.Config.EffectiveCreditAmount;
                account.UpdatedAt = DateTime.Now;
                await Db.Updateable(account).ExecuteCommandAsync();
            }
            return;
        }

        var uniqueStocks = allPositions
            .Select(p => StockCodeParser.ParseNormalized(p.StockCode))
            .Where(p => p.HasValue)
            .Select(p => (p.Value.market, p.Value.code))
            .Distinct()
            .ToList();

        Dictionary<string, TdxProtocol.Models.QuoteResult>? quotes = null;
        try { quotes = await Entry.Quotes!.GetQuotesBatchAsync(uniqueStocks); }
        catch (Exception ex) { Entry.Api.Logger.Warn("账户服务", $"批量获取行情失败: {ex.Message}"); }
        if (quotes == null) return;

        // 按账户分组计算市值
        var marketValues = new Dictionary<long, decimal>();
        foreach (var pos in allPositions)
        {
            if (quotes.TryGetValue(pos.StockCode, out var quote) && quote.Price > 0)
            {
                marketValues.TryGetValue(pos.AccountId, out var current);
                marketValues[pos.AccountId] = current + (decimal)quote.Price * pos.Quantity;
            }
        }

        // 更新各账户 TotalAsset
        foreach (var account in accounts)
        {
            var mv = marketValues.GetValueOrDefault(account.Id);
            var newTotal = account.Balance + mv - account.DebtBalance;
            if (account.TotalAsset != newTotal)
            {
                account.TotalAsset = newTotal;
                // 同步更新授信额度（生效额度：显式保存值或初始资金默认值）
                account.CreditLimit = Entry.Config.EffectiveCreditAmount;
                account.UpdatedAt = DateTime.Now;
                await Db.Updateable(account).ExecuteCommandAsync();
            }
        }
    }
}
