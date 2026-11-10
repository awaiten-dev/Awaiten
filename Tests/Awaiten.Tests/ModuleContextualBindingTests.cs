using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A <c>WhenInjectedInto</c> binding of a container reaches the dependencies of an implementation that a module of
///     another assembly builds through a generated member, as it reaches them in a container of the module's own
///     assembly: the constructor, a deferred <c>[Inject]</c> property, an <c>OnActivated</c> hook and the own
///     dependencies of a decorator or a composite.
/// </summary>
public partial class ModuleContextualBindingTests
{
	[Test]
	public async Task AnImplementationBuiltThroughAnInstanceHandle_ReceivesTheContextualImplementation()
	{
		using ContextualContainer.Root container = new();

		IHandledFlavored handled = container.Resolve<IHandledFlavored>();

		await That(handled.Flavor).Is<GuestFlavor>()
			.Because("the handle's factory takes the constructor parameter, which the binding names through the implementation");
		await That(handled.Late).Is<GuestFlavor>()
			.Because("the generated setter of the deferred property is redirected like the property itself");
		await That(handled.Seasoned).Is<GuestFlavor>()
			.Because("the hook wrapper's parameter is redirected like the hook's own");
	}

	[Test]
	public async Task AnImplementationBuiltThroughAGeneratedFactory_ReceivesTheContextualImplementationInEveryMember()
	{
		using ContextualContainer.Root container = new();

		IFactoryFlavored built = container.Resolve<IFactoryFlavored>();

		await That(built.Flavor).Is<GuestFlavor>();
		await That(built.Late).Is<GuestFlavor>()
			.Because("the generated setter of the deferred property is redirected like the property itself");
		await That(built.Seasoned).Is<GuestFlavor>();
	}

	[Test]
	public async Task AScanMatchBuiltThroughAGeneratedScanFactory_ReceivesTheContextualImplementation()
	{
		using ContextualContainer.Root container = new();

		await That(container.Resolve<IScannedFlavored>().Flavor).Is<GuestFlavor>()
			.Because("the binding names the match the scan factory builds, not the scanned service");
	}

	[Test]
	public async Task ADecoratorBuiltThroughAGeneratedFactory_ReceivesTheContextualImplementation()
	{
		using ContextualContainer.Root container = new();

		IFlavoredCup cup = container.Resolve<IFlavoredCup>();

		await That(cup).Is<TastingCup>();
		await That(cup.Flavor).Is<GuestFlavor>();
		await That(((TastingCup)cup).Inner).Is<PlainCup>()
			.Because("the binding never replaces the inner instance the decorator wraps");
	}

	[Test]
	public async Task AKeyedCompositeBuiltThroughAGeneratedFactory_ReceivesTheContextualImplementation()
	{
		using ContextualContainer.Root container = new();

		IFlavoredCup flight = container.Resolve<IFlavoredCup>("flight");

		await That(flight).Is<CupFlight>();
		await That(flight.Flavor).Is<GuestFlavor>();
		await That(((CupFlight)flight).Cups).HasSingle().Which.Is<PlainCup>()
			.Because("the binding never reaches the collection the composite fans out over");
	}

	[Test]
	public async Task AnotherConsumer_KeepsTheUnconditionalImplementation()
	{
		using ContextualContainer.Root container = new();

		await That(container.Resolve<PlainTaster>().Flavor).Is<HouseFlavor>();
		await That(container.Resolve<IBrewFlavor>()).Is<HouseFlavor>();
	}

	public sealed class GuestFlavor : IBrewFlavor;

	[Container]
	[Import(typeof(ContextualBindingModule))]
	[Singleton<GuestFlavor, IBrewFlavor>(WhenInjectedInto = typeof(HandledFlavored))]
	[Singleton<GuestFlavor, IBrewFlavor>(WhenInjectedInto = typeof(FactoryFlavored))]
	[Singleton<GuestFlavor, IBrewFlavor>(WhenInjectedInto = typeof(ScannedFlavored))]
	[Singleton<GuestFlavor, IBrewFlavor>(WhenInjectedInto = typeof(TastingCup))]
	[Singleton<GuestFlavor, IBrewFlavor>(WhenInjectedInto = typeof(CupFlight))]
	public static partial class ContextualContainer;
}
