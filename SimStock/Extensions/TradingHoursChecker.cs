namespace SimStock;

/// <summary>
/// 交易时段判断。A股交易时间: 上午 9:30-11:30，下午 13:00-15:00（北京时间）。
/// 集合竞价: 9:15-9:25。
/// </summary>
public static class TradingHoursChecker
{
    public static bool IsInTradingSession()
    {
        var now = DateTime.Now;
        var t = now.TimeOfDay;
        var morning = t >= new TimeSpan(9, 30, 0) && t < new TimeSpan(11, 30, 1);
        var afternoon = t >= new TimeSpan(13, 0, 0) && t < new TimeSpan(15, 0, 1);
        return morning || afternoon;
    }

    /// <summary>
    /// 收盘后（15:00:01 之后，与 IsInTradingSession 的时段结束边界一致）。
    /// 注意与午间休市（11:30-13:00）区分：午休不是收盘，挂单应保留至下午盘。
    /// </summary>
    public static bool IsAfterClose()
    {
        return DateTime.Now.TimeOfDay >= new TimeSpan(15, 0, 1);
    }

    public static bool IsInAuctionPeriod()
    {
        var t = DateTime.Now.TimeOfDay;
        return t >= new TimeSpan(9, 15, 0) && t < new TimeSpan(9, 25, 1);
    }

    public static string GetStatusDescription()
    {
        if (IsInAuctionPeriod())
        {
            return "集合竞价时段，暂不支持交易";
        }

        if (IsInTradingSession())
        {
            var t = DateTime.Now.TimeOfDay;
            return t < new TimeSpan(11, 30, 0) ? "上午交易时段" : "下午交易时段";
        }

        var now = DateTime.Now;
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return "周末休市";
        }

        var t2 = now.TimeOfDay;
        if (t2 < new TimeSpan(9, 30, 0))
        {
            return "盘前时段";
        }

        if (t2 < new TimeSpan(11, 30, 1))
        {
            return "午间休市";
        }

        return "盘后时段";
    }
}