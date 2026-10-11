using CodeAtlas.AspNetCore.Export;

namespace CodeAtlas.AspNetCore.Tests;

public class SqlSummaryTests
{
    [Theory]
    [InlineData("SELECT p.id FROM products AS p WHERE p.sku = @p0", "SELECT products")]
    [InlineData("INSERT INTO \"audit_logs\" (a) VALUES ($1);\nINSERT INTO products (sku) VALUES ($2)", "INSERT audit_logs products")]
    [InlineData("UPDATE users SET name = $1 WHERE id = $2", "UPDATE users")]
    [InlineData("DELETE FROM refresh_tokens WHERE expires < now()", "DELETE refresh_tokens")]
    [InlineData("WITH recent AS (SELECT 1) SELECT * FROM orders o JOIN customers c ON c.id = o.customer_id", "WITH orders customers")]
    [InlineData("SELECT o.id FROM public.\"Orders\" AS o", "SELECT public.Orders")]
    [InlineData("SELECT 1", "SELECT")]
    [InlineData("VACUUM", "SQL")]
    public void Keeps_only_operation_and_tables(string sql, string expected) =>
        Assert.Equal(expected, SqlSummary.Summarize(sql));

    [Theory]
    [InlineData("SELECT * FROM t WHERE note = 'from secret_table'")]
    [InlineData("SELECT * FROM t -- join secret_table\n")]
    [InlineData("SELECT * FROM t /* from secret_table */")]
    public void Words_inside_literals_and_comments_never_appear(string sql)
    {
        var summary = SqlSummary.Summarize(sql);

        Assert.Equal("SELECT t", summary);
        Assert.DoesNotContain("secret", summary);
    }

    // Fail closed: when the text has constructs the summarizer cannot read reliably, keep only the operation.
    [Theory]
    [InlineData(@"INSERT INTO notes VALUES (E'it\'s from Alice')", "INSERT")]
    [InlineData("SELECT $$ from secret_table $$ FROM t", "SELECT")]
    [InlineData("SELECT $body$ join secret_table $body$ FROM t", "SELECT")]
    [InlineData("SELECT * FROM t WHERE a = 'unterminated from secret_table", "SELECT")]
    [InlineData("SELECT * FROM t /* unterminated from secret_table", "SELECT")]
    [InlineData("vacuum from secret_table", "SQL")]
    public void Unreliable_text_yields_only_the_operation(string sql, string expected)
    {
        var summary = SqlSummary.Summarize(sql);

        Assert.Equal(expected, summary);
        Assert.DoesNotContain("secret", summary);
        Assert.DoesNotContain("Alice", summary);
    }

    [Fact]
    public void Positional_parameters_are_not_mistaken_for_dollar_quotes() =>
        Assert.Equal("SELECT products", SqlSummary.Summarize("SELECT name FROM products WHERE sku = $1 AND id = $2"));
}
