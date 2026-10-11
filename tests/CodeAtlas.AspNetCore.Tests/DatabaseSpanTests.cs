using System.Text;
using CodeAtlas.AspNetCore.Tests.Support;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CodeAtlas.AspNetCore.Tests;

public class DatabaseSpanTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private Task<TestApp> StartAsync(bool keepDb = false) => TestApp.StartAsync(
        app => app.MapGet("/products/{sku}", async (string sku, NpgsqlDataSource data) =>
        {
            await using var command = data.CreateCommand("SELECT name FROM products WHERE sku = $1");
            command.Parameters.AddWithValue(sku);
            return Results.Ok(await command.ExecuteScalarAsync());
        }).WithCodeAtlasHandlerSpan(),
        b => b.Services.AddSingleton(NpgsqlDataSource.Create(db.ConnectionString)),
        o => o.KeepDbStatements = keepDb);

    [Fact]
    public async Task Database_span_is_a_summary_under_the_handler_with_no_sql_parameters_or_credentials()
    {
        await using var app = await StartAsync();

        var (response, traceId) = await app.SendAsync(HttpMethod.Get, "/products/SECRET-SKU-123");

        Assert.Equal("\"Apple\"", await response.Content.ReadAsStringAsync());
        var spans = await app.Sink.WaitForTraceAsync(traceId);
        var handler = spans.Single(s => (string?)s.Attr("codeatlas.component") == "handler");
        var query = spans.Single(s => s.Scope == "Npgsql" && s.Name == "SELECT products");
        Assert.Equal(handler.SpanId, query.ParentSpanId);
        Assert.Equal("SELECT products", query.Attr("db.query.summary"));
        Assert.Null(query.Attr("db.query.text"));
        Assert.Null(query.Attr("db.npgsql.data_source"));

        var wire = string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray())));
        Assert.DoesNotContain("SECRET-SKU-123", wire);
        Assert.DoesNotContain("WHERE sku", wire);
        Assert.DoesNotContain("Password", wire);
    }

    [Fact]
    public async Task Sql_text_can_be_kept_but_parameter_values_still_never_leave()
    {
        await using var app = await StartAsync(keepDb: true);

        var (_, traceId) = await app.SendAsync(HttpMethod.Get, "/products/SECRET-SKU-123");

        var query = (await app.Sink.WaitForTraceAsync(traceId)).Single(s => s.Scope == "Npgsql" && s.Name == "SELECT products");
        Assert.Equal("SELECT name FROM products WHERE sku = $1", query.Attr("db.query.text"));
        Assert.DoesNotContain("SECRET-SKU-123", string.Join("\n", app.Sink.Requests.Select(r => Encoding.UTF8.GetString(r.ToByteArray()))));
    }
}
