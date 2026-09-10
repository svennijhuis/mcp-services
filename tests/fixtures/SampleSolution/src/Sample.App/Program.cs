using Sample.Lib;
using Sample.Lib.Orders;

var service = new OrderService(new Calculator());
service.Place(new Order("a", 2, 10));
service.Place(new Order("b", 1, 5));
Console.WriteLine(service.GrandTotal());
