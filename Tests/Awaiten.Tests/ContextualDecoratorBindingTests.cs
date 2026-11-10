using System.Collections.Generic;
using System.Linq;

namespace Awaiten.Tests;

/// <summary>
///     A <c>WhenInjectedInto</c> binding that names a decorator or a composite reaches the dependencies it takes besides
///     the inner instance it wraps or the collection it fans out over.
/// </summary>
public sealed partial class ContextualDecoratorBindingTests
{
	[Test]
	public async Task ADecorator_ReceivesTheContextualImplementation_AroundEveryMember()
	{
		using CoffeeShop.Root shop = new();

		ICup[] cups = shop.Resolve<IEnumerable<ICup>>().ToArray();

		await That(cups).HasCount(2).And.All().Satisfy(cup => cup is TastingCup { Flavor: SpecialFlavor, });
		await That(cups.Select(cup => ((TastingCup)cup).Inner.GetType())).IsEqualTo([typeof(Espresso), typeof(Latte),])
			.Because("the inner instance stays the member the decorator wraps");
	}

	[Test]
	public async Task AKeyedComposite_ReceivesTheContextualImplementation_AndFansOutOverItsKey()
	{
		using CoffeeShop.Root shop = new();

		ICup flight = shop.Resolve<ICup>("flight");

		await That(flight).Is<CupFlight>();
		await That(((CupFlight)flight).Flavor).Is<SpecialFlavor>();
		await That(((CupFlight)flight).Cups).HasSingle().Which.Is<Espresso>();
	}

	[Test]
	public async Task AnotherConsumer_KeepsTheUnconditionalImplementation()
	{
		using CoffeeShop.Root shop = new();

		await That(shop.Resolve<IFlavor>()).Is<HouseFlavor>();
	}

	public interface IFlavor;

	public sealed class HouseFlavor : IFlavor;

	public sealed class SpecialFlavor : IFlavor;

	public interface ICup;

	public sealed class Espresso : ICup;

	public sealed class Latte : ICup;

	public sealed class TastingCup(ICup inner, IFlavor flavor) : ICup
	{
		public ICup Inner => inner;
		public IFlavor Flavor => flavor;
	}

	public sealed class CupFlight(IEnumerable<ICup> cups, IFlavor flavor) : ICup
	{
		public IReadOnlyList<ICup> Cups { get; } = cups.ToList();
		public IFlavor Flavor => flavor;
	}

	[Container]
	[Singleton<HouseFlavor, IFlavor>]
	[Singleton<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof(TastingCup))]
	[Singleton<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof(CupFlight))]
	[Transient<Espresso, ICup>]
	[Transient<Latte, ICup>]
	[Transient<Espresso, ICup>(Key = "flight")]
	[Decorate<TastingCup, ICup>]
	[Composite<CupFlight, ICup>(Key = "flight")]
	public static partial class CoffeeShop;
}
