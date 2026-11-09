using CoffeeShop;

namespace Awaiten.ExampleTests;

/// <summary>
///     The example of the "Extending containers" page: a test container extends the production coffee shop and
///     replaces only the payment gateway, so the barista from the production composition never charges a real card.
///     A second test container turns a conditional decorator off by replacing the settings its predicate takes.
/// </summary>
public partial class ExtendingContainersExample
{
	[Test]
	public async Task Barista_ChargesThePrice()
	{
		using TestCoffeeShop.Root shop = new();

		Receipt receipt = shop.Resolve<Barista>().Serve(3.50m);

		await That(receipt.Amount).IsEqualTo(3.50m);
	}

	[Test]
	public async Task TestCoffeeBar_SkipsTheMonitoring()
	{
		using CoffeeBar.Root production = new();
		using TestCoffeeBar.Root test = new();

		await That(production.Resolve<IPaymentTerminal>()).IsExactly<MonitoringTerminal>();
		await That(test.Resolve<IPaymentTerminal>()).IsExactly<CardTerminal>();
	}

	public sealed class CardGateway : IPaymentGateway
	{
		public Receipt Charge(decimal amount) => throw new InvalidOperationException("A test must not charge a real card.");
	}

	public sealed class FakeGateway : IPaymentGateway
	{
		public Receipt Charge(decimal amount) => new(amount);
	}

	[Container]
	[Singleton<CardGateway, IPaymentGateway>]
	[Singleton<Barista>]
	public static partial class ProductionCoffeeShop;

	[Container(Extends = typeof(ProductionCoffeeShop))]
	[Singleton<FakeGateway, IPaymentGateway>]
	public static partial class TestCoffeeShop;

	public sealed class MonitoringSettings
	{
		public bool Enabled { get; init; } = true;
	}

	public interface IPaymentTerminal;

	public sealed class CardTerminal : IPaymentTerminal;

	public sealed class MonitoringTerminal(IPaymentTerminal inner) : IPaymentTerminal
	{
		public IPaymentTerminal Inner { get; } = inner;
	}

	[Container]
	[Singleton<MonitoringSettings>]
	[Singleton<CardTerminal, IPaymentTerminal>]
	[Decorate<MonitoringTerminal, IPaymentTerminal>(When = nameof(IsMonitored))]
	public static partial class CoffeeBar
	{
		public static bool IsMonitored(MonitoringSettings settings) => settings.Enabled;
	}

	[Container(Extends = typeof(CoffeeBar))]
	[Singleton<MonitoringSettings>(Instance = nameof(NoMonitoring))]
	public static partial class TestCoffeeBar
	{
		private static readonly MonitoringSettings NoMonitoring = new() { Enabled = false, };
	}
}
