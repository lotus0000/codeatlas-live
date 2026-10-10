using System.Text.RegularExpressions;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>Reduces SQL to its operation and table names. Literals, comments, parameters and columns never survive.</summary>
internal static partial class SqlSummary
{
    public static string Summarize(string sql)
    {
        var code = Comment().Replace(Literal().Replace(sql, "''"), " ");
        var operation = Operation().Match(code);
        var tables = Table().Matches(code).Select(m => m.Groups[1].Value.Replace("\"", "")).Distinct().Take(4);
        return string.Join(' ', new[] { operation.Success ? operation.Groups[1].Value.ToUpperInvariant() : "SQL" }.Concat(tables));
    }

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex Literal();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|WITH|MERGE|CALL)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Operation();

    [GeneratedRegex(@"\b(?:FROM|INTO|UPDATE|JOIN)\s+((?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)(?:\.(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex Table();
}
