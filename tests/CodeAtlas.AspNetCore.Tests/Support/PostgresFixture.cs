using Npgsql;
using Testcontainers.PostgreSql;

namespace CodeAtlas.AspNetCore.Tests.Support;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand("CREATE TABLE products (sku text PRIMARY KEY, name text NOT NULL); INSERT INTO products VALUES ('SECRET-SKU-123', 'Apple')", connection);
        await create.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
