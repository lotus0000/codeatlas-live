using Npgsql;

namespace MinimalApiSample.Orders;

/// <summary>
/// Plain application code. It has no span of its own: CodeAtlas shows its database calls under the handler that
/// called it, and its frames in exception stack traces.
/// </summary>
public sealed class OrderService(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand("SELECT sku, name, stock FROM products ORDER BY sku");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var products = new List<Product>();
        while (await reader.ReadAsync(cancellationToken))
            products.Add(new Product(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return products;
    }

    public async Task<int> CreateAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var check = new NpgsqlCommand("SELECT stock FROM products WHERE sku = $1", connection, transaction))
        {
            check.Parameters.Add(new NpgsqlParameter { Value = sku });
            var stock = await check.ExecuteScalarAsync(cancellationToken) as int? ?? throw new InvalidOperationException($"Unknown product: {sku}");
            if (stock < quantity) throw new InvalidOperationException($"Out of stock: {sku}");
        }

        await using (var take = new NpgsqlCommand("UPDATE products SET stock = stock - $1 WHERE sku = $2", connection, transaction))
        {
            take.Parameters.Add(new NpgsqlParameter { Value = quantity });
            take.Parameters.Add(new NpgsqlParameter { Value = sku });
            await take.ExecuteNonQueryAsync(cancellationToken);
        }

        int id;
        await using (var insert = new NpgsqlCommand("INSERT INTO orders (sku, quantity) VALUES ($1, $2) RETURNING id", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = sku });
            insert.Parameters.Add(new NpgsqlParameter { Value = quantity });
            id = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        await transaction.CommitAsync(cancellationToken);
        return id;
    }
}

public sealed record Product(string Sku, string Name, int Stock);
