using System.Diagnostics.Metrics;

namespace ControllerSample.Services;

/// <summary>In-memory orders. No span of its own: a failure here appears as a frame in the action span's exception.</summary>
public sealed class OrderService
{
    public const string MeterName = "ControllerSample";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<int> Created = Meter.CreateCounter<int>("orders.created");

    private readonly Lock _lock = new();
    private readonly List<Order> _orders = [];
    private int _nextId;

    public IReadOnlyList<Order> All()
    {
        lock (_lock) return [.. _orders];
    }

    public Order Create(string sku, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        lock (_lock)
        {
            var order = new Order(++_nextId, sku, quantity);
            _orders.Add(order);
            Created.Add(1);
            return order;
        }
    }
}

public sealed record Order(int Id, string Sku, int Quantity);
