using Npgsql;

namespace MinimalApiSample.Data;

public static class Database
{
    /// <summary>Creates the two tables and the demo products once. Runs at start-up, outside any request.</summary>
    public static async Task EnsureCreatedAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS products (sku text PRIMARY KEY, name text NOT NULL, stock int NOT NULL);
            CREATE TABLE IF NOT EXISTS orders (
                id serial PRIMARY KEY,
                sku text NOT NULL REFERENCES products (sku),
                quantity int NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now());
            INSERT INTO products (sku, name, stock) VALUES
                ('KB-01', 'Mechanical keyboard', 1000000),
                ('MON-27', '27-inch monitor', 0)
            ON CONFLICT (sku) DO NOTHING;
            """);
        await command.ExecuteNonQueryAsync();
    }
}
