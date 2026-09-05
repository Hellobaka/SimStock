using Another_Mirai_Native.Abstractions;
using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Services;
using SqlSugar;
using SimStock.Models;

namespace SimStock;

[PluginInfo(
    appId: "me.cqp.luohuaming.SimStock",
    name: "水银韭菜机",
    version: "1.18.6",
    description: "群聊模拟炒股插件",
    author: "落花茗"
)]
public class Entry : PluginBase
{
    public static SqlSugarScope? Db { get; private set; }

    public static ConfigService Config { get; private set; } = null!;

    public static MatchingEngine? Matcher { get; private set; }

    public static TomorrowOrderEngine? TomorrowOrders { get; private set; }

    public static ConnectionManager? ConnMgr { get; private set; }

    public static QuoteService Quotes { get; private set; } = null!;

    public static StockNameService StockNames { get; private set; } = null!;

    public static IPluginApi Api { get; private set; } = null!;

    public override async Task OnEnableAsync(CancellationToken ct)
    {
        Api = API;
        var appDir = API.AppApi.GetAppDirectory();
        API.Logger.Info("水银韭菜机", $"插件目录: {appDir}");

        var dbPath = Path.Combine(appDir, "core.db");
        Db = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = $"Data Source={dbPath};Pooling=true;",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        });

        Db.DbMaintenance.CreateDatabase();
        Db.CodeFirst.InitTables(
            typeof(Account),
            typeof(Position),
            typeof(Order),
            typeof(TradeRecord),
            typeof(Setting),
            typeof(GroupAdmin),
            typeof(UserGroup),
            typeof(CreditRecord),
            typeof(TomorrowOrder));

        Config = new ConfigService();
        await Config.LoadAsync(Db);

        Quotes = new QuoteService();
        ConnMgr = new ConnectionManager(appDir);
        ConnMgr.SetHolidayCacheDirectory(appDir);
        StockNames = new StockNameService(appDir, ConnMgr);

        Matcher = new MatchingEngine(ConnMgr);
        await Matcher.RecoverPendingOrdersOnStartupAsync();
        Matcher.Start(ct);

        TomorrowOrders = new TomorrowOrderEngine(ConnMgr);
        TomorrowOrders.Start(ct);

        API.Logger.Info("水银韭菜机", "插件已启用");
    }

    public override async Task OnDisableAsync(CancellationToken ct)
    {
        API.Logger.Info("水银韭菜机", "正在停止...");

        if (TomorrowOrders is not null)
        {
            await TomorrowOrders.StopAsync();
            TomorrowOrders.Dispose();
            TomorrowOrders = null;
        }

        if (Matcher is not null)
        {
            await Matcher.StopAsync();
            Matcher.Dispose();
            Matcher = null;
        }

        ConnMgr?.Disconnect();
        ConnMgr?.Dispose();
        ConnMgr = null;
        Db?.Close();
        Db = null;

        API.Logger.Info("水银韭菜机", "插件已禁用");
    }
}
