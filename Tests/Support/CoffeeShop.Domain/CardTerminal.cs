namespace CoffeeShop;

/// <summary>
///     The payment gateway the coffee shop uses in production. It stays internal: only <see cref="IPaymentGateway" />
///     is part of the domain's surface, and the module in <c>CoffeeShop.Composition</c> registers it.
/// </summary>
internal sealed class CardTerminal : IPaymentGateway
{
	public Receipt Charge(decimal amount) => new(amount);
}
