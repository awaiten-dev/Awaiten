using CoffeeShop;
using CoffeeShop.Composition;

namespace Awaiten.ExampleTests;

/// <summary>
///     The domain module of the "Design principles &amp; the composition root" page: the composition root imports the
///     domain's <see cref="PaymentModule" /> and so uses its internal payment gateway, which it cannot name.
/// </summary>
public partial class DomainModuleExample
{
	[Test]
	public async Task Barista_ChargesThroughTheDomainsInternalGateway()
	{
		using CoffeeShop.Root shop = new();

		Receipt receipt = shop.Resolve<Barista>().Serve(3.50m);

		await That(receipt.Amount).IsEqualTo(3.50m);
	}

	[Container]
	[Import(typeof(PaymentModule))]
	[Transient<Barista>]
	public static partial class CoffeeShop;
}
