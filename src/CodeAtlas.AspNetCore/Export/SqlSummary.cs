using System.Text.RegularExpressions;

namespace CodeAtlas.AspNetCore.Export;

/// <summary>
/// Reduces SQL to its operation and table names; literals, comments, parameters and columns never survive.
/// Fails closed: when the text contains anything this reader cannot parse reliably (dollar-quoted or E'' strings,
/// unterminated literals or comments), only the operation is kept; an unknown operation becomes "SQL".
/// </summary>
internal static partial class SqlSummary
{
    public static string Summarize(string sql)
    {
        var operation = Operation().Match(sql);
        var head = operation.Success ? operation.Groups[1].Value.ToUpperInvariant() : "SQL";
        if (!operation.Success || Unreadable().IsMatch(sql)) return head;

        var code = Comment().Replace(Literal().Replace(sql, "''"), " ");
        if (code.Replace("''", "").Contains('\'') || code.Contains("/*")) return head; // unterminated literal or comment

        var tables = Table().Matches(code).Select(m => m.Groups[1].Value.Replace("\"", "")).Distinct().Take(4);
        return string.Join(' ', new[] { head }.Concat(tables));
    }

    // Dollar-quoted strings ($$…$$, $tag$…$tag$) and escape strings (E'…'): not parsed, so never trusted.
    [GeneratedRegex(@"\$[A-Za-z_]*\$|(?<![A-Za-z0-9_])[Ee]'")]
    private static partial Regex Unreadable();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex Literal();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|WITH|MERGE|CALL)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Operation();

    [GeneratedRegex(@"\b(?:FROM|INTO|UPDATE|JOIN)\s+((?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)(?:\.(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex Table();
}
