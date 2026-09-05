using SimStock.Models;
using SqlSugar;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimStock;

public class ConfigService
{
    public int MaxPendingOrdersPerUser { get; set; } = 5;

    public int QuotePollingIntervalSec { get; set; } = 3;

    public decimal InitialCapital { get; set; } = 1_000_000m;

    /// <summary>授信额度（用户显式保存的值；null 表示从未设置，默认按初始资金的 100% 生效）</summary>
    public decimal? CreditAmount { get; set; } = null;

    /// <summary>生效授信额度：已显式保存则用保存值，否则默认等于初始资金</summary>
    public decimal EffectiveCreditAmount => CreditAmount ?? InitialCapital;

    /// <summary>日利率（默认万分之五 0.05%）</summary>
    public decimal CreditInterestRate { get; set; } = 0.0005m;

    public string CustomHelpText { get; set; } = "";

    public bool HelpForwardSend { get; set; } = true;

    public HashSet<long> GroupWhitelist { get; set; } = [];

    public HashSet<long> UserBlacklist { get; set; } = [];

    /// <summary>自定义触发词字典: 命令名 → 触发词 (不含正则后缀)</summary>
    public Dictionary<string, string> Triggers { get; set; } = [];

    /// <summary>所有命令的默认触发词</summary>
    public static readonly Dictionary<string, string> DefaultTriggers = new()
    {
        ["Register"] = "/股票开户",
        ["Account"] = "/股票账户",
        ["Deposit"] = "/股票入金",
        ["Withdraw"] = "/股票出金",
        ["Reset"] = "/股票重置",
        ["AdminAdd"] = "/股票管理 添加",
        ["AdminRemove"] = "/股票管理 移除",
        ["AdminList"] = "/股票管理 列表",
        ["Price"] = "/股价查询",
        ["Buy"] = "/买入股票",
        ["LimitBuy"] = "/限价买入",
        ["Sell"] = "/卖出股票",
        ["LimitSell"] = "/限价卖出",
        ["AllIn"] = "/梭哈",
        ["LimitAllIn"] = "/限价梭哈",
        ["Cancel"] = "/股票撤单",
        ["Rank"] = "/股票排行",
        ["GlobalRank"] = "/全局排行",
        ["History"] = "/历史订单",
        ["OrderQuery"] = "/查询订单",
        ["Help"] = "/股票帮助",
        ["ClearOne"] = "/清仓",
        ["ClearAll"] = "/全部清仓",
        ["Credit"] = "/授信额度",
        ["CreditUse"] = "/使用授信",
        ["CreditRepay"] = "/偿还授信",
        ["TomorrowClear"] = "/开盘清仓",
        ["TomorrowClearCancel"] = "/取消开盘清仓",
        ["TomorrowAllIn"] = "/开盘梭哈",
        ["TomorrowAllInCancel"] = "/取消开盘梭哈",
    };

    /// <summary>Regex 型命令的固定参数后缀（不可修改，防止破坏命名组）</summary>
    public static readonly Dictionary<string, string> ParamSuffixes = new()
    {
        ["Deposit"] = @"\s+(?<qq>\d{5,12})\s+(?<amount>\d+(\.\d+)?)",
        ["Withdraw"] = @"\s+(?<amount>\d+(\.\d+)?)",
        ["Reset"] = @"\s+(?<qq>\d{5,12})",
        ["AdminAdd"] = @"\s+(?<qq>\d{5,12})",
        ["AdminRemove"] = @"\s+(?<qq>\d{5,12})",
        ["Price"] = @"\s+(?<code>\w{2,8})",
        ["Buy"] = @"\s+(?<code>\w{2,8})\s+(?<qty>\d+)",
        ["LimitBuy"] = @"\s+(?<code>\w{2,8})\s+(?<qty>\d+)\s+(?<price>\d+(\.\d+)?)",
        ["Sell"] = @"\s+(?<code>\w{2,8})(?:\s+(?<qty>\d+))?",
        ["LimitSell"] = @"\s+(?<code>\w{2,8})\s+(?<qty>\d+)\s+(?<price>\d+(\.\d+)?)",
        ["AllIn"] = @"\s+(?<code>\w{2,8})",
        ["LimitAllIn"] = @"\s+(?<code>\w{2,8})\s+(?<price>\d+(\.\d+)?)",
        ["Cancel"] = @"\s+(?<orderId>\d+)",
        ["ClearOne"] = @"\s+(?<code>\w{2,8})",
        ["CreditUse"] = @"\s+(?<amount>\d+(\.\d+)?|梭哈)",
        ["CreditRepay"] = @"\s+(?<amount>\d+(\.\d+)?)",
        ["TomorrowClear"] = @"\s+(?<code>\w{2,8}|全仓)",
        ["TomorrowClearCancel"] = @"\s+(?<code>\w{2,8}|全仓)",
        ["TomorrowAllIn"] = @"\s+(?<code>\w{2,8})",
        ["TomorrowAllInCancel"] = @"\s+(?<code>\w{2,8})",
    };

    /// <summary>获取当前触发词</summary>
    public string GetTrigger(string name)
        => Triggers.TryGetValue(name, out var t) && !string.IsNullOrWhiteSpace(t) ? t : DefaultTriggers[name];

    /// <summary>获取完整的 DynamicCommand 用模板。Regex 型 = ^触发词 + 固定后缀$；FullMatch 型 = 触发词原文</summary>
    public string GetCommandTemplate(string name)
    {
        var trigger = GetTrigger(name);
        if (ParamSuffixes.TryGetValue(name, out var suffix))
        {
            return $"^{Regex.Escape(trigger)}{suffix}$";
        }

        return trigger;
    }

    public async Task LoadAsync(SqlSugarScope db)
    {
        var settings = await db.Queryable<Setting>().ToListAsync();
        var dict = settings.ToDictionary(s => s.Key, s => s.Value);

        if (dict.TryGetValue("MaxPendingOrdersPerUser", out var maxOrders) && int.TryParse(maxOrders, out var v1))
        {
            MaxPendingOrdersPerUser = v1;
        }

        if (dict.TryGetValue("QuotePollingIntervalSec", out var interval) && int.TryParse(interval, out var v2) && v2 >= 1)
        {
            QuotePollingIntervalSec = v2;
        }

        if (dict.TryGetValue("InitialCapital", out var capital) && decimal.TryParse(capital, out var v3) && v3 > 0)
        {
            InitialCapital = v3;
        }

        if (dict.TryGetValue("CustomHelpText", out var help) && !string.IsNullOrWhiteSpace(help))
        {
            CustomHelpText = help;
        }
        else
        {
            CustomHelpText = "";
        }

        if (dict.TryGetValue("CommandTriggers", out var triggersJson) && !string.IsNullOrWhiteSpace(triggersJson))
        {
            try { Triggers = JsonSerializer.Deserialize<Dictionary<string, string>>(triggersJson) ?? []; }
            catch { Triggers = []; }
        }
        else
        {
            Triggers = [];
        }

        if (dict.TryGetValue("GroupWhitelist", out var wl) && !string.IsNullOrWhiteSpace(wl))
        {
            GroupWhitelist = ParseIdList(wl);
        }

        if (dict.TryGetValue("UserBlacklist", out var bl) && !string.IsNullOrWhiteSpace(bl))
        {
            UserBlacklist = ParseIdList(bl);
        }

        if (dict.TryGetValue("HelpForwardSend", out var hfs))
        {
            HelpForwardSend = hfs.Equals("true", StringComparison.CurrentCultureIgnoreCase);
        }

        if (dict.TryGetValue("CreditAmount", out var ca) && decimal.TryParse(ca, out var vCa))
        {
            CreditAmount = vCa;
        }

        if (dict.TryGetValue("CreditInterestRate", out var ci) && decimal.TryParse(ci, out var vCi))
        {
            CreditInterestRate = vCi;
        }
    }

    /// <summary>解析逗号分隔的ID列表，同时支持英文逗号和中文逗号，无效条目静默跳过</summary>
    public static HashSet<long> ParseIdList(string raw)
    {
        return raw.Split(',', '，')
            .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    /// <summary>将ID集合规范化为逗号分隔的存储字符串</summary>
    public static string FormatIdList(HashSet<long> ids) => string.Join(",", ids);

    /// <summary>保存自定义触发词到数据库（仅保存与默认值不同的）</summary>
    public async Task SaveTriggersAsync(SqlSugarScope db, Dictionary<string, string> triggers)
    {
        var json = JsonSerializer.Serialize(triggers);
        await SetAsync(db, "CommandTriggers", json);
    }

    public async Task SetAsync(SqlSugarScope db, string key, string value)
    {
        var setting = await db.Queryable<Setting>().FirstAsync(s => s.Key == key);
        if (setting == null)
        {
            await db.Insertable(new Setting { Key = key, Value = value }).ExecuteCommandAsync();
        }
        else
        {
            setting.Value = value;
            await db.Updateable(setting).ExecuteCommandAsync();
        }

        // 即时更新内存缓存
        await LoadAsync(db);
    }
}