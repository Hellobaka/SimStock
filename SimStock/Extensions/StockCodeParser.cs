using TdxProtocol;

namespace SimStock;

/// <summary>
/// 股票代码解析。支持 sz/sh/bj 前缀，无前缀时智能推断交易所。
/// </summary>
public static class StockCodeParser
{
    /// <summary>
    /// 带前缀解析: "sz000001" → (MarketSZ, "000001")
    /// 返回 null 表示格式不正确
    /// </summary>
    public static (byte market, string code)? TryParseWithPrefix(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim().ToLowerInvariant();

        // 匹配: 可选2字母前缀 + 数字代码（1-6位）
        var match = System.Text.RegularExpressions.Regex.Match(input, @"^(sz|sh|bj)?(\d{1,6})$");
        if (!match.Success)
        {
            return null;
        }

        var prefix = match.Groups[1].Value;
        var code = match.Groups[2].Value.PadLeft(6, '0');

        byte market;
        if (prefix == "sh")
            market = TdxConstants.MarketSH;
        else if (prefix == "bj")
            market = TdxConstants.MarketBJ;
        else if (prefix == "sz")
            market = TdxConstants.MarketSZ;
        else
            return null; // 无前缀，留给 TryInferMarket 推断

        return (market, code);
    }

    /// <summary>
    /// 无前缀时根据代码段推断交易所。支持短代码（如 "1" → sz000001）。
    /// 返回 null 表示无法推断。
    /// </summary>
    public static (byte market, string code)? TryInferMarket(string code)
    {
        code = code.PadLeft(6, '0');
        var prefix = code[..2];
        var prefix3 = code[..3];

        // 沪市: 60xxxx, 601xxx, 603xxx, 605xxx, 688xxx(科创板)
        if (prefix == "60" || prefix == "68")
            return (TdxConstants.MarketSH, code);

        // 深市: 00xxxx, 30xxxx, 301xxx(创业板)
        if (prefix == "00" || prefix == "30")
            return (TdxConstants.MarketSZ, code);

        // 北交所: 83xxxx, 87xxxx, 43xxxx, 92xxxx
        if (prefix is "83" or "87" or "43" or "92")
            return (TdxConstants.MarketBJ, code);

        return null;
    }

    /// <summary>
    /// 标准化代码: (MarketSZ, "1") → "sz000001"
    /// </summary>
    public static string NormalizeCode(byte market, string code)
    {
        var prefix = market switch
        {
            TdxConstants.MarketSH => "sh",
            TdxConstants.MarketBJ => "bj",
            _ => "sz"
        };
        return prefix + code.PadLeft(6, '0');
    }

    /// <summary>
    /// 反向解析: "sz000001" → (MarketSZ, "000001")
    /// </summary>
    public static (byte market, string code)? ParseNormalized(string normalizedCode)
    {
        return TryParseWithPrefix(normalizedCode);
    }

    /// <summary>去掉交易所前缀用于显示: "sz000001" → "000001"</summary>
    public static string ToDisplayCode(string normalizedCode)
    {
        if (normalizedCode.Length >= 2 && normalizedCode[0] is >= 'a' and <= 'z' && normalizedCode[1] is >= 'a' and <= 'z')
            return normalizedCode[2..];
        return normalizedCode;
    }

    /// <summary>
    /// 统一的股票显示格式: "贵州茅台（sh600519）"。
    /// 名称查不到时（StockNameService 未命中会返回代码本身）只返回代码，避免出现 "sz000001（sz000001）"。
    /// </summary>
    public static string ToDisplayStock(string? name, string normalizedCode)
    {
        if (string.IsNullOrWhiteSpace(name) || name == normalizedCode)
        {
            return normalizedCode;
        }
        return $"{name}（{normalizedCode}）";
    }
}