namespace Sample.Lib.Orders;

public record Order(string Id, int Quantity, int UnitPrice);

public sealed class OrderService
{
    private readonly ICalculator _calculator;
    private readonly List<Order> _orders = new();

    public OrderService(ICalculator calculator)
    {
        _calculator = calculator;
    }

    public int Place(Order order)
    {
        _orders.Add(order);
        return Total(order);
    }

    public int Total(Order order) => _calculator.Multiply(order.Quantity, order.UnitPrice);

    public int GrandTotal()
    {
        var sum = 0;
        foreach (var order in _orders)
        {
            sum = _calculator.Add(sum, Total(order));
        }

        return sum;
    }
}
