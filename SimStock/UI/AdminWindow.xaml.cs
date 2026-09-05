using Another_Mirai_Native.Abstractions.Models;
using SimStock.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SimStock;

public class ValueConverter(Func<object, string> formatter) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => formatter(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class CollectionCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is System.Collections.ICollection col)
        {
            return col.Count.ToString();
        }

        return "0";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class AdminWindow : Window
{
    private static readonly Dictionary<string, string> ColumnNames = new()
    {
        // Account
        ["Id"] = "ID",
        ["QQ"] = "QQ号",
        ["Balance"] = "可用余额",
        ["TotalAsset"] = "总资产",
        ["CreatedAt"] = "注册时间",
        ["UpdatedAt"] = "更新时间",
        ["Positions"] = "持仓",
        ["Orders"] = "订单",
        // Order
        ["AccountId"] = "账户ID",
        ["StockCode"] = "股票代码",
        ["OrderType"] = "订单类型",
        ["Quantity"] = "数量",
        ["Price"] = "价格",
        ["FilledQuantity"] = "已成交",
        ["Status"] = "状态",
        // TradeRecord
        ["OrderId"] = "订单号",
        ["TradeType"] = "方向",
        ["Amount"] = "金额",
        ["TradedAt"] = "成交时间",
        // CreditRecord
        ["Type"] = "类型",
        ["Interest"] = "利息",
        ["Time"] = "时间",
        // Shared
        ["AvgCost"] = "均价",
        ["Value"] = "值",
        ["Key"] = "键",
    };

    private static readonly Dictionary<string, Func<object, string>> ColumnFormatters = new()
    {
        ["OrderType"] = v => (int)v switch { 0 => "市价买", 1 => "限价买", 2 => "市价卖", 3 => "限价卖", _ => v.ToString()! },
        ["Status"] = v => (int)v switch { 0 => "挂单中", 1 => "部分成交", 2 => "已成交", 3 => "已撤销", _ => v.ToString()! },
        ["TradeType"] = v => (int)v == 0 ? "买入" : "卖出",
        ["Type"] = v => (int)v == 1 ? "借入" : "偿还",
    };

    public AdminWindow()
    {
        InitializeComponent();
        Loaded += async (s, e) => await LoadAllData();
    }

    private static readonly HashSet<string> ReadOnlyColumns = ["Id", "AccountId", "UpdatedAt", "CreatedAt", "TradedAt"];

    private void DataGrid_AutoGeneratingColumn(object sender, System.Windows.Controls.DataGridAutoGeneratingColumnEventArgs e)
    {
        // 保护列只读（按属性名判断，不受翻译影响）
        if (ReadOnlyColumns.Contains(e.PropertyName))
        {
            e.Column.IsReadOnly = true;
        }

        if (ColumnNames.TryGetValue(e.PropertyName, out var chineseName))
        {
            e.Column.Header = chineseName;
        }

        if (e.PropertyType == typeof(DateTime))
        {
            ((System.Windows.Controls.DataGridTextColumn)e.Column).Binding.StringFormat = "yyyy-MM-dd HH:mm:ss";
        }

        // 导航属性显示集合数量，而不是 "(Collection)"
        if (e.PropertyType.IsGenericType && e.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var originalBinding = ((System.Windows.Controls.DataGridTextColumn)e.Column).Binding as System.Windows.Data.Binding;
            if (originalBinding != null)
            {
                originalBinding.Converter = new CollectionCountConverter();
            }
        }

        if (ColumnFormatters.TryGetValue(e.PropertyName, out var formatter))
        {
            var originalBinding = ((System.Windows.Controls.DataGridTextColumn)e.Column).Binding as System.Windows.Data.Binding;
            if (originalBinding != null)
            {
                originalBinding.Converter = new ValueConverter(formatter);
            }
        }
    }

    private async Task LoadAllData()
    {
        await LoadUsers();
        await LoadPositions();
        await LoadOrders();
        await LoadTrades();
        await LoadCreditRecords();
        LoadSettings();
        await LoadGroupList();
        LoadCommandTemplates();
    }

    private async Task LoadUsers()
    {
        try
        {
            var users = await Entry.Db!.Queryable<Account>()
                .OrderBy(a => a.Id, SqlSugar.OrderByType.Desc).Take(200).ToListAsync();

            // 用数据库直接查持仓数和订单数，比导航属性可靠
            var accountIds = users.Select(u => u.Id).ToList();
            var posCounts = await Entry.Db!.Queryable<Position>()
                .Where(p => accountIds.Contains(p.AccountId) && p.Quantity > 0)
                .GroupBy(p => p.AccountId)
                .Select(p => new { p.AccountId, Count = SqlSugar.SqlFunc.AggregateCount(p.Id) })
                .ToListAsync();
            var orderCounts = await Entry.Db!.Queryable<Order>()
                .Where(o => accountIds.Contains(o.AccountId) && o.Status == 0)
                .GroupBy(o => o.AccountId)
                .Select(o => new { o.AccountId, Count = SqlSugar.SqlFunc.AggregateCount(o.Id) })
                .ToListAsync();

            var posDict = posCounts.ToDictionary(x => x.AccountId, x => x.Count);
            var ordDict = orderCounts.ToDictionary(x => x.AccountId, x => x.Count);

            foreach (var user in users)
            {
                user.Positions = new List<Position>(new Position[posDict.GetValueOrDefault(user.Id)]);
                user.Orders = new List<Order>(new Order[ordDict.GetValueOrDefault(user.Id)]);
            }

            UsersGrid.ItemsSource = users;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载用户数据失败: {ex.Message}");
            MessageBox.Show($"加载用户数据失败: {ex.Message}");
        }
    }

    private async Task LoadPositions()
    {
        try
        {
            var positions = await Entry.Db!.Queryable<Position>()
                .Where(p => p.Quantity > 0)
                .OrderBy(p => p.AccountId)
                .ToListAsync();
            PositionsGrid.ItemsSource = positions;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载持仓数据失败: {ex.Message}");
            MessageBox.Show($"加载持仓数据失败: {ex.Message}");
        }
    }

    private async Task LoadOrders()
    {
        try
        {
            var orders = await Entry.Db!.Queryable<Order>()
                .OrderBy(o => o.Id, SqlSugar.OrderByType.Desc).Take(200).ToListAsync();
            OrdersGrid.ItemsSource = orders;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载挂单数据失败: {ex.Message}");
            MessageBox.Show($"加载挂单数据失败: {ex.Message}");
        }
    }

    private async Task LoadCreditRecords()
    {
        try
        {
            var records = await Entry.Db!.Queryable<CreditRecord>()
                .InnerJoin<Account>((c, a) => c.AccountId == a.Id)
                .OrderBy(c => c.Id, SqlSugar.OrderByType.Desc)
                .Take(200)
                .Select((c, a) => new CreditRecordView
                {
                    QQ = a.QQ,
                    Type = c.Type,
                    Amount = c.Amount,
                    Interest = c.Interest,
                    Time = c.CreatedAt
                })
                .ToListAsync();
            CreditRecordsGrid.ItemsSource = records;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载授信记录失败: {ex.Message}");
            MessageBox.Show($"加载授信记录失败: {ex.Message}");
        }
    }

    private async void RefreshCreditRecords_Click(object sender, RoutedEventArgs e) => await LoadCreditRecords();

    private async Task LoadTrades()
    {
        try
        {
            var trades = await Entry.Db!.Queryable<TradeRecord>()
                .OrderBy(t => t.Id, SqlSugar.OrderByType.Desc).Take(200).ToListAsync();
            TradesGrid.ItemsSource = trades;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载交易记录失败: {ex.Message}");
            MessageBox.Show($"加载交易记录失败: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        MaxOrdersInput.Text = Entry.Config.MaxPendingOrdersPerUser.ToString();
        PollingIntervalInput.Text = Entry.Config.QuotePollingIntervalSec.ToString();
        InitialCapitalInput.Text = Entry.Config.InitialCapital.ToString("F0");
        GroupWhitelistInput.Text = string.Join(", ", Entry.Config.GroupWhitelist);
        UserBlacklistInput.Text = string.Join(", ", Entry.Config.UserBlacklist);
        CustomHelpTextInput.Text = Entry.Config.CustomHelpText;
        HelpForwardSend.IsChecked = Entry.Config.HelpForwardSend;
        LoadCommandTemplates();
        LoadCreditSettings();
    }

    private void LoadCreditSettings()
    {
        var effective = Entry.Config.EffectiveCreditAmount;
        var capital = Entry.Config.InitialCapital;
        _syncingCredit = true;
        CreditAmountInput.Text = effective.ToString("F0");
        if (capital > 0)
        {
            CreditPctInput.IsEnabled = true;
            CreditPctInput.Text = (effective / capital * 100m).ToString("0.##");
        }
        else
        {
            CreditPctInput.IsEnabled = false;
            CreditPctInput.Text = "";
        }
        CreditRateInput.Text = (Entry.Config.CreditInterestRate * 10000).ToString("F0"); // 万分之几
        _syncingCredit = false;
    }

    /// <summary>联动同步标记：程序化更新另一个输入框时防止 TextChanged 递归</summary>
    private bool _syncingCredit;

    /// <summary>百分比 → 金额：金额 = 百分比/100 × 初始资金设置</summary>
    private void CreditPctInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingCredit) return;
        if (CreditAmountInput.IsKeyboardFocusWithin) return;
        if (Entry.Config.InitialCapital <= 0) return;
        if (!decimal.TryParse(CreditPctInput.Text.Trim(), out var pct)) return;
        _syncingCredit = true;
        CreditAmountInput.Text = (pct / 100m * Entry.Config.InitialCapital).ToString("0.##");
        _syncingCredit = false;
    }

    /// <summary>金额 → 百分比：百分比 = 金额/初始资金设置 × 100</summary>
    private void CreditAmountInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingCredit) return;
        if (CreditPctInput.IsKeyboardFocusWithin) return;
        if (Entry.Config.InitialCapital <= 0) return;
        if (!decimal.TryParse(CreditAmountInput.Text.Trim(), out var amount)) return;
        _syncingCredit = true;
        CreditPctInput.Text = (amount / Entry.Config.InitialCapital * 100m).ToString("0.##");
        _syncingCredit = false;
    }

    private async void SaveCreditSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var db = Entry.Db!;

            // 解析最终额度：优先取金额输入；金额为空/非法时按百分比反算
            decimal creditAmount;
            if (decimal.TryParse(CreditAmountInput.Text.Trim(), out var amount) && amount >= 0)
            {
                creditAmount = amount;
            }
            else if (Entry.Config.InitialCapital > 0
                     && decimal.TryParse(CreditPctInput.Text.Trim(), out var pct) && pct >= 0)
            {
                creditAmount = pct / 100m * Entry.Config.InitialCapital;
            }
            else
            {
                MessageBox.Show("请填写有效的授信额度金额或百分比", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await Entry.Config.SetAsync(db, "CreditAmount", creditAmount.ToString("F0"));

            if (decimal.TryParse(CreditRateInput.Text.Trim(), out var ratePerWan) && ratePerWan >= 0)
            {
                await Entry.Config.SetAsync(db, "CreditInterestRate", (ratePerWan / 10000m).ToString("F6"));
            }

            MessageBox.Show("授信设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadCreditSettings();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"保存授信设置失败: {ex.Message}");
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshUsers_Click(object sender, RoutedEventArgs e) => await LoadUsers();

    private async void RefreshPositions_Click(object sender, RoutedEventArgs e) => await LoadPositions();

    private async void DeletePosition_Click(object sender, RoutedEventArgs e)
    {
        if (PositionsGrid.SelectedItem is not Position pos)
        {
            return;
        }

        var stockName = await Entry.StockNames.GetNameAsync(pos.StockCode);
        var result = MessageBox.Show(
            $"确认删除持仓？\n股票: {StockCodeParser.ToDisplayStock(stockName, pos.StockCode)}\n数量: {pos.Quantity}\n均价: {pos.AvgCost:F2}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await Entry.Db!.Deleteable(pos).ExecuteCommandAsync();
            MessageBox.Show("持仓已删除");
            await LoadPositions();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"删除持仓失败: {ex.Message}");
            MessageBox.Show($"删除失败: {ex.Message}");
        }
    }

    private async void SavePositions_Click(object sender, RoutedEventArgs e)
    {
        if (PositionsGrid.ItemsSource is not List<Position> positions)
        {
            return;
        }

        var modified = positions.Where(p =>
        {
            // 找出有变化的持仓：数量变更或均价变更
            var original = Entry.Db!.Queryable<Position>().First(pp => pp.Id == p.Id);
            return original != null && (original.Quantity != p.Quantity || original.AvgCost != p.AvgCost);
        }).ToList();

        if (modified.Count == 0)
        {
            MessageBox.Show("没有需要保存的修改");
            return;
        }

        var result = MessageBox.Show($"确认保存 {modified.Count} 条持仓修改？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var pos in modified)
            {
                pos.UpdatedAt = DateTime.Now;
                if (pos.Quantity <= 0)
                {
                    await Entry.Db!.Deleteable(pos).ExecuteCommandAsync();
                }
                else
                {
                    await Entry.Db!.Updateable(pos).ExecuteCommandAsync();
                }
            }

            MessageBox.Show("保存成功");
            await LoadPositions();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"保存持仓修改失败: {ex.Message}");
            MessageBox.Show($"保存失败: {ex.Message}");
        }
    }

    private async void RefreshOrders_Click(object sender, RoutedEventArgs e) => await LoadOrders();

    private async void RefreshTrades_Click(object sender, RoutedEventArgs e) => await LoadTrades();

    private async void ResetUser_Click(object sender, RoutedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not Account account)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确认重置 QQ={account.QQ} 的账户？\n所有数据将被清空且不可恢复。",
            "确认操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await AccountService.ResetAccountAsync(account.QQ);
            MessageBox.Show("账户已重置");
            await LoadUsers();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"重置账户失败: {ex.Message}");
            MessageBox.Show($"操作失败: {ex.Message}");
        }
    }

    private async void ForceCancelOrder_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not Order order)
        {
            return;
        }

        if (order.Status != 0)
        {
            MessageBox.Show("该订单已成交或已撤销");
            return;
        }

        var stockName = await Entry.StockNames.GetNameAsync(order.StockCode);
        var result = MessageBox.Show(
            $"确认强制撤销订单 {order.Id}？\n股票: {StockCodeParser.ToDisplayStock(stockName, order.StockCode)} 数量: {order.Quantity}",
            "确认操作", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            order.Status = 3;
            order.UpdatedAt = DateTime.Now;
            await Entry.Db!.Updateable(order).ExecuteCommandAsync();
            MessageBox.Show("订单已撤销");
            await LoadOrders();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"强制撤单失败: {ex.Message}");
            MessageBox.Show($"操作失败: {ex.Message}");
        }
    }

    private async void SearchUser_Click(object sender, RoutedEventArgs e)
    {
        if (long.TryParse(UserSearchBox.Text.Trim(), out var qq))
        {
            var users = await Entry.Db!.Queryable<Account>()
                .Where(a => a.QQ == qq)
                .OrderBy(a => a.Id, SqlSugar.OrderByType.Desc)
                .ToListAsync();

            var accountIds = users.Select(u => u.Id).ToList();
            if (accountIds.Count > 0)
            {
                var posCounts = await Entry.Db!.Queryable<Position>()
                    .Where(p => accountIds.Contains(p.AccountId) && p.Quantity > 0)
                    .GroupBy(p => p.AccountId)
                    .Select(p => new { p.AccountId, Count = SqlSugar.SqlFunc.AggregateCount(p.Id) })
                    .ToListAsync();
                var orderCounts = await Entry.Db!.Queryable<Order>()
                    .Where(o => accountIds.Contains(o.AccountId) && o.Status == 0)
                    .GroupBy(o => o.AccountId)
                    .Select(o => new { o.AccountId, Count = SqlSugar.SqlFunc.AggregateCount(o.Id) })
                    .ToListAsync();

                var posDict = posCounts.ToDictionary(x => x.AccountId, x => x.Count);
                var ordDict = orderCounts.ToDictionary(x => x.AccountId, x => x.Count);

                foreach (var user in users)
                {
                    user.Positions = new List<Position>(new Position[posDict.GetValueOrDefault(user.Id)]);
                    user.Orders = new List<Order>(new Order[ordDict.GetValueOrDefault(user.Id)]);
                }
            }

            UsersGrid.ItemsSource = users;
        }
        else
        {
            await LoadUsers();
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var db = Entry.Db!;

            if (int.TryParse(MaxOrdersInput.Text.Trim(), out var maxOrders) && maxOrders > 0)
            {
                await Entry.Config.SetAsync(db, "MaxPendingOrdersPerUser", maxOrders.ToString());
            }

            if (int.TryParse(PollingIntervalInput.Text.Trim(), out var interval) && interval >= 1)
            {
                await Entry.Config.SetAsync(db, "QuotePollingIntervalSec", interval.ToString());
            }

            if (decimal.TryParse(InitialCapitalInput.Text.Trim(), out var capital) && capital > 0)
            {
                await Entry.Config.SetAsync(db, "InitialCapital", capital.ToString("F0"));
            }

            var whitelistRaw = GroupWhitelistInput.Text.Trim();
            if (!string.IsNullOrEmpty(whitelistRaw))
            {
                var parsed = ConfigService.ParseIdList(whitelistRaw);
                var rawParts = whitelistRaw.Split(',', '，').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (parsed.Count < rawParts.Count)
                {
                    var invalid = rawParts.Where(s => !long.TryParse(s, out _)).ToList();
                    MessageBox.Show($"以下群号格式无效，已跳过:\n{string.Join("\n", invalid)}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                await Entry.Config.SetAsync(db, "GroupWhitelist", ConfigService.FormatIdList(parsed));
            }
            else
            {
                await Entry.Config.SetAsync(db, "GroupWhitelist", "");
            }

            var blacklistRaw = UserBlacklistInput.Text.Trim();
            if (!string.IsNullOrEmpty(blacklistRaw))
            {
                var parsed = ConfigService.ParseIdList(blacklistRaw);
                var rawParts = blacklistRaw.Split(',', '，').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (parsed.Count < rawParts.Count)
                {
                    var invalid = rawParts.Where(s => !long.TryParse(s, out _)).ToList();
                    MessageBox.Show($"以下QQ号格式无效，已跳过:\n{string.Join("\n", invalid)}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                await Entry.Config.SetAsync(db, "UserBlacklist", ConfigService.FormatIdList(parsed));
            }
            else
            {
                await Entry.Config.SetAsync(db, "UserBlacklist", "");
            }

            await Entry.Config.SetAsync(db, "CustomHelpText", CustomHelpTextInput.Text.Trim());

            if (CmdTemplateGrid.ItemsSource is List<CmdTemplateRow> rows)
            {
                var triggers = rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Template) && r.Template != ConfigService.DefaultTriggers.GetValueOrDefault(r.Name))
                    .ToDictionary(r => r.Name, r => r.Template);
                await Entry.Config.SaveTriggersAsync(db, triggers);
            }

            await Entry.Config.SetAsync(db, "HelpForwardSend", (HelpForwardSend.IsChecked ?? false).ToString());

            MessageBox.Show("设置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadSettings();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"保存设置失败: {ex.Message}");
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== 插件管理员 Tab ====================

    private async void RefreshGroupList_Click(object sender, RoutedEventArgs e)
    {
        await LoadGroupList();
    }

    private void LoadCommandTemplates()
    {
        var items = ConfigService.DefaultTriggers.Select(kv => new CmdTemplateRow
        {
            Name = kv.Key,
            Template = Entry.Config.GetTrigger(kv.Key)
        }).OrderBy(r => r.Name).ToList();

        CmdTemplateGrid.ItemsSource = items;
    }

    private async Task LoadGroupList()
    {
        try
        {
            var groups = await Task.Run(() => Entry.Api.GroupApi.GetGroupList());
            GroupSelector.ItemsSource = null;
            GroupSelector.DisplayMemberPath = "Name";
            GroupSelector.ItemsSource = groups;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载群列表失败: {ex.Message}");
            MessageBox.Show($"加载群列表失败: {ex.Message}");
        }
    }

    private async void GroupSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        await LoadAdminsForSelectedGroup();
    }

    private async Task LoadAdminsForSelectedGroup()
    {
        if (GroupSelector.SelectedItem == null)
        {
            return;
        }

        var groupInfo = (GroupInfo)GroupSelector.SelectedItem;
        var groupId = groupInfo.Group;

        try
        {
            var admins = await AdminService.GetAdminsAsync(groupId);
            var displayItems = new List<string>();
            foreach (var admin in admins)
            {
                try
                {
                    var member = Entry.Api.GroupApi.GetGroupMemberInfo(groupId, admin.QQ);
                    var name = member != null
                        ? (!string.IsNullOrEmpty(member.Card) ? member.Card
                            : !string.IsNullOrEmpty(member.Nick) ? member.Nick
                            : admin.QQ.ToString())
                        : admin.QQ.ToString();
                    displayItems.Add($"{name} (QQ:{admin.QQ})");
                }
                catch (Exception ex)
                {
                    Entry.Api.Logger.Warn("管理界面", $"获取群成员信息失败: {ex.Message}");
                    displayItems.Add($"QQ:{admin.QQ}");
                }
            }

            AdminListBox.ItemsSource = displayItems;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"加载管理员列表失败: {ex.Message}");
            MessageBox.Show($"加载管理员列表失败: {ex.Message}");
        }
    }

    private async void AddAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (GroupSelector.SelectedItem == null)
        {
            MessageBox.Show("请先选择一个群");
            return;
        }

        if (!long.TryParse(NewAdminQQInput.Text.Trim(), out var qq) || qq <= 0)
        {
            MessageBox.Show("请输入有效的QQ号");
            return;
        }

        var groupInfo = (GroupInfo)GroupSelector.SelectedItem;
        var groupId = groupInfo.Group;

        try
        {
            var (success, error) = await AdminService.AddAdminAsync(groupId, qq);
            if (!success)
            {
                MessageBox.Show(error!);
                return;
            }

            NewAdminQQInput.Clear();
            await LoadAdminsForSelectedGroup();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"添加管理员失败: {ex.Message}");
            MessageBox.Show($"添加失败: {ex.Message}");
        }
    }

    private async void RemoveAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (GroupSelector.SelectedItem == null)
        {
            MessageBox.Show("请先选择一个群");
            return;
        }

        if (AdminListBox.SelectedItem is not string selectedText)
        {
            MessageBox.Show("请先在列表中选择要移除的管理员");
            return;
        }

        // 从显示文本中提取QQ号: "昵称 (QQ:12345)" 或 "QQ:12345"
        var match = System.Text.RegularExpressions.Regex.Match(selectedText, @"QQ:(\d+)");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var qq))
        {
            return;
        }

        var groupInfo = (GroupInfo)GroupSelector.SelectedItem;
        var groupId = groupInfo.Group;

        try
        {
            var (success, error) = await AdminService.RemoveAdminAsync(groupId, qq);
            if (!success)
            {
                MessageBox.Show(error!);
                return;
            }

            await LoadAdminsForSelectedGroup();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("管理界面", $"移除管理员失败: {ex.Message}");
            MessageBox.Show($"移除失败: {ex.Message}");
        }
    }

}

public class CmdTemplateRow
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
}

public class CreditRecordView
{
    public long QQ { get; set; }
    public int Type { get; set; }
    public decimal Amount { get; set; }
    public decimal Interest { get; set; }
    public DateTime Time { get; set; }
}