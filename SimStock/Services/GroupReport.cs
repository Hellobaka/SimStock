using System.Text;

namespace SimStock;

/// <summary>
/// 按群收集一批开盘订单的执行结果，整批结束后输出一条结算消息。
/// 引擎执行过程中只往这里记录行，不再逐单发送群消息。
/// </summary>
public sealed class GroupReport
{
    private readonly List<string> _lines = new();

    public long GroupId { get; }
    public int SuccessCount { get; private set; }
    public int SkipCount { get; private set; }
    public int FailCount { get; private set; }

    public GroupReport(long groupId)
    {
        GroupId = groupId;
    }

    public void Success(long qq, string line)
    {
        _lines.Add($"✅ [CQ:at,qq={qq}] {line}");
        SuccessCount++;
    }

    public void Skip(long qq, string line)
    {
        _lines.Add($"⚠️ [CQ:at,qq={qq}] {line}");
        SkipCount++;
    }

    public void Fail(long qq, string line)
    {
        _lines.Add($"❌ [CQ:at,qq={qq}] {line}");
        FailCount++;
    }

    /// <summary>提示类内容，不计入统计</summary>
    public void Info(string line) => _lines.Add($"ℹ️ {line}");

    public bool HasLines => _lines.Count > 0;

    public string BuildMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("🔄 开盘订单结算中…");
        sb.AppendLine();

        foreach (var line in _lines)
        {
            sb.AppendLine(line);
        }

        // 没有跳过和失败时不输出结算汇总行
        if (SkipCount > 0 || FailCount > 0)
        {
            sb.AppendLine();
            var parts = new List<string>();
            if (SuccessCount > 0) parts.Add($"成功 {SuccessCount} 只");
            if (SkipCount > 0) parts.Add($"跳过 {SkipCount} 只");
            if (FailCount > 0) parts.Add($"失败 {FailCount} 只");
            sb.Append("📊 结算: ").Append(string.Join(", ", parts));
        }

        return sb.ToString();
    }
}