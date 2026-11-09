namespace CoffeeShop;

/// <summary>
///     A slice of clean coffee-shop domain: plain classes that have never heard of a container.
///     The architecture test asserts that nothing in this assembly except the <c>CoffeeShop.Composition</c>
///     namespace references Awaiten.
/// </summary>
public interface IPaymentGateway
{
	Receipt Charge(decimal amount);
}

public sealed record Receipt(decimal Amount);

public sealed class Barista(IPaymentGateway gateway)
{
	public Receipt Serve(decimal price) => gateway.Charge(price);
}
