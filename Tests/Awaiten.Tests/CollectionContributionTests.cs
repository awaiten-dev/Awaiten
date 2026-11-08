using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of plugin-style composition, where several modules or registrations each contribute an
///     implementation of one service: every contribution is a collection member unless a registration without
///     <c>Fallback</c> overrides it, and single resolution takes the first contribution in precedence order.
/// </summary>
public partial class CollectionContributionTests
{
	public class StrongContributions
	{
		[Test]
		public async Task TheCollectionHoldsTheContributionOfEveryModule()
		{
			using StrongShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Vanilla), typeof(Caramel),])
				.Because("each module contributes its own syrup, in the order of the imports");
		}

		[Test]
		public async Task SingleResolutionTakesTheEarlierImport()
		{
			using StrongShop.Root shop = new();

#pragma warning disable AWT155
			ISyrup syrup = shop.Resolve<ISyrup>();
#pragma warning restore AWT155

			await That(syrup).Is<Vanilla>()
				.Because("the earlier import wins single resolution");
		}
	}

	public class DefaultContributions
	{
		[Test]
		public async Task TheCollectionHoldsEveryDefaultWhenNothingOverridesThem()
		{
			using DefaultsShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Vanilla), typeof(Caramel),])
				.Because("a default only leaves the collection when a registration without Fallback overrides it, and none does");
		}

		[Test]
		public async Task SingleResolutionTakesTheEarlierDefault()
		{
			using DefaultsShop.Root shop = new();

			await That(shop.Resolve<ISyrup>()).Is<Vanilla>()
				.Because("among defaults, the first in declaration order wins single resolution");
		}

		[Test]
		public async Task AStrongRegistrationOverridesTheDefaultEvenInTheCollection()
		{
			using MixedShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Caramel),])
				.Because("an overridden default is replaced in full, collections included");
			await That(shop.Resolve<ISyrup>()).Is<Caramel>()
				.Because("the registration without Fallback wins single resolution although its module is imported later");
		}

		[Test]
		public async Task DefaultsOnTheContainerItselfAreAllCollectionMembers()
		{
			using ContainerDefaultsShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Vanilla), typeof(Caramel),])
				.Because("two defaults on one container override each other no more than two defaults of two modules");
			await That(shop.Resolve<ISyrup>()).Is<Vanilla>()
				.Because("the first declared default wins single resolution");
		}

		[Test]
		public async Task AContainerDefaultAndAModuleDefaultAreBothCollectionMembers()
		{
			using ContainerAndModuleDefaultsShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Hazelnut), typeof(Vanilla),])
				.Because("the container's registrations come before the imported ones");
			await That(shop.Resolve<ISyrup>()).Is<Hazelnut>()
				.Because("the container's default precedes the module's");
		}

		[Test]
		public async Task AnEagerDefaultLosingToAnotherDefaultIsBuiltAtStartup()
		{
			EagerCaramel.Built = 0;

			using EagerDefaultsShop.Root shop = new();

			await That(EagerCaramel.Built).IsEqualTo(1)
				.Because("a default losing single resolution to another default is still a collection member, so an eager one is built with the root");
		}

		[Test]
		public async Task AClosedDefaultAndTheClosingOfAnOpenDefaultAreBothCollectionMembers()
		{
			using OpenDefaultsShop.Root shop = new();

			ToppingBar bar = shop.Resolve<ToppingBar>();

			await That(bar.Toppings.Select(topping => topping.GetType())).IsEqualTo([typeof(OrderTopping), typeof(GenericTopping<Order>),])
				.Because("a closing of an open default is overridden only by a registration without Fallback");
			await That(shop.Resolve<ITopping<Order>>()).Is<OrderTopping>()
				.Because("a closed registration precedes the closing of an open one");
		}

		[Test]
		public async Task AStrongOpenRegistrationDoesNotReplaceAClosedDefault()
		{
			using StrongOpenOverClosedDefaultShop.Root shop = new();

			ToppingBar bar = shop.Resolve<ToppingBar>();

			await That(bar.Toppings.Select(topping => topping.GetType())).IsEqualTo([typeof(OrderTopping), typeof(GenericTopping<Order>),])
				.Because("only an explicit closed registration without Fallback replaces a closed default, not the closing of an open one");
			await That(shop.Resolve<ITopping<Order>>()).Is<OrderTopping>()
				.Because("the closed default precedes the closing of the open registration");
		}

		[Test]
		public async Task AScanMatchDoesNotReplaceADefault()
		{
			using ScanOverDefaultShop.Root shop = new();

			GlazeBar bar = shop.Resolve<GlazeBar>();

			await That(bar.Glazes.Select(glaze => glaze.GetType())).IsEqualTo([typeof(SugarGlaze), typeof(HoneyGlaze),])
				.Because("a scan match yields to every other registration and replaces no default");
			await That(shop.Resolve<IGlaze>()).Is<SugarGlaze>()
				.Because("the default precedes the scan matches");
		}
	}

	public class KeyedContributions
	{
		[Test]
		public async Task DefaultsUnderTheSameKeyLeaveTheFirstInTheKeyedDictionary()
		{
			using KeyedDefaultsShop.Root shop = new();

			IReadOnlyDictionary<string, ISyrup> syrups = shop.Resolve<IReadOnlyDictionary<string, ISyrup>>();

			await That(syrups).HasCount(1)
				.Because("a dictionary holds one implementation per key");
			await That(syrups["house"]).Is<Vanilla>()
				.Because("the first default under the key wins it");
			await That(shop.Resolve<ISyrup>("house")).Is<Vanilla>()
				.Because("a keyed resolve takes the same winner as the dictionary");
		}

		[Test]
		public async Task DefaultsUnderDifferentKeysAreAllInTheKeyedDictionary()
		{
			using KeyedShop.Root shop = new();

			IReadOnlyDictionary<string, ISyrup> syrups = shop.Resolve<IReadOnlyDictionary<string, ISyrup>>();

			await That(syrups.Keys).IsEqualTo(["vanilla", "caramel",]).InAnyOrder()
				.Because("each module contributes under its own key");
		}
	}

	public class CompositeContributions
	{
		[Test]
		public async Task TheCompositeFansOutToTheContributionOfEveryModule()
		{
			using CompositeShop.Root shop = new();

			ISyrup syrup = shop.Resolve<ISyrup>();

			await That(syrup).Is<AllSyrups>()
				.Because("the composite takes single resolution");
			await That(((AllSyrups)syrup).Syrups.Select(member => member.GetType())).IsEqualTo([typeof(Vanilla), typeof(Caramel),])
				.Because("the composite receives every contribution");
		}
	}

	public class ExtendingContainerContributions
	{
		[Test]
		public async Task TheDefaultsOfTheBaseAndTheExtendingContainerAreBothCollectionMembers()
		{
			using ExtendingShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Vanilla), typeof(Caramel),])
				.Because("a default of the extending container's module overrides no default of the base's module");
			await That(shop.Resolve<ISyrup>()).Is<Vanilla>()
				.Because("the base's imports precede the extending container's own");
		}

		[Test]
		public async Task ADefaultOnTheExtendingContainerJoinsAnInheritedDefault()
		{
			using ExtendingDefaultShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Caramel), typeof(Vanilla),])
				.Because("a default on the extending container replaces no inherited default, so both are collection members");
			await That(shop.Resolve<ISyrup>()).Is<Caramel>()
				.Because("the extending container's own registrations precede the inherited ones");
		}

		[Test]
		public async Task AnInheritedRegistrationWithoutFallbackReplacesADefaultOnTheExtendingContainer()
		{
			using ExtendingStrongBaseShop.Root shop = new();

			SyrupBar bar = shop.Resolve<SyrupBar>();

			await That(bar.Syrups.Select(syrup => syrup.GetType())).IsEqualTo([typeof(Vanilla),])
				.Because("a default on the extending container replaces nothing, and the inherited registration without Fallback replaces it");
		}
	}

	public class CrossAssemblyContributions
	{
		[Test]
		public async Task TheCollectionHoldsTheContributionOfEveryModuleOfAnotherAssembly()
		{
			using LibraryShop.Root shop = new();

			LibrarySyrupBar bar = shop.Resolve<LibrarySyrupBar>();

			await That(bar.Syrups).HasCount(2)
				.Because("each module of the library contributes its own syrup");
			await That(bar.Syrups[0]).Is<LibraryVanilla>()
				.Because("the earlier import comes first");
		}

		[Test]
		public async Task TheCollectionHoldsEveryDefaultOfModulesOfAnotherAssembly()
		{
			using LibraryDefaultsShop.Root shop = new();

			LibrarySyrupBar bar = shop.Resolve<LibrarySyrupBar>();

			await That(bar.Syrups).HasCount(2)
				.Because("no registration without Fallback overrides the library's defaults");
			await That(shop.Resolve<ILibrarySyrup>()).Is<LibraryVanilla>()
				.Because("the earlier default wins single resolution");
		}
	}

	public interface ISyrup;

	public sealed class Vanilla : ISyrup;

	public sealed class Caramel : ISyrup;

	public sealed class Hazelnut : ISyrup;

	public sealed class EagerCaramel : ISyrup
	{
		public static int Built;

		public EagerCaramel() => Interlocked.Increment(ref Built);
	}

	public sealed class SyrupBar(IEnumerable<ISyrup> syrups)
	{
		public ISyrup[] Syrups { get; } = syrups.ToArray();
	}

	public sealed class AllSyrups(IEnumerable<ISyrup> syrups) : ISyrup
	{
		public ISyrup[] Syrups { get; } = syrups.ToArray();
	}

	public sealed class LibrarySyrupBar(IReadOnlyList<ILibrarySyrup> syrups)
	{
		public IReadOnlyList<ILibrarySyrup> Syrups { get; } = syrups;
	}

	public sealed class Order;

	public interface ITopping<T>;

	public sealed class OrderTopping : ITopping<Order>;

	public sealed class GenericTopping<T> : ITopping<T>;

	public sealed class ToppingBar(IEnumerable<ITopping<Order>> toppings)
	{
		public ITopping<Order>[] Toppings { get; } = toppings.ToArray();
	}

	[Module]
	[Singleton<Vanilla, ISyrup>]
	public static class VanillaModule;

	[Module]
	[Singleton<Caramel, ISyrup>]
	public static class CaramelModule;

	[Module]
	[Singleton<Vanilla, ISyrup>(Fallback = Fallback.Silent)]
	public static class VanillaDefaultModule;

	[Module]
	[Singleton<Caramel, ISyrup>(Fallback = Fallback.Silent)]
	public static class CaramelDefaultModule;

	[Module]
	[Singleton<EagerCaramel, ISyrup>(Fallback = Fallback.Silent, Eager = true)]
	public static class EagerCaramelDefaultModule;

	[Module]
	[Singleton<Vanilla, ISyrup>(Key = "house", Fallback = Fallback.Silent)]
	public static class VanillaHouseModule;

	[Module]
	[Singleton<Caramel, ISyrup>(Key = "house", Fallback = Fallback.Silent)]
	public static class CaramelHouseModule;

	[Module]
	[Singleton<Vanilla, ISyrup>(Key = "vanilla")]
	public static class VanillaKeyedModule;

	[Module]
	[Singleton<Caramel, ISyrup>(Key = "caramel")]
	public static class CaramelKeyedModule;

	public interface IGlaze;

	public sealed class SugarGlaze : IGlaze;

	public sealed class HoneyGlaze : IGlaze;

	public sealed class GlazeBar(IEnumerable<IGlaze> glazes)
	{
		public IGlaze[] Glazes { get; } = glazes.ToArray();
	}

	[Module]
	[Singleton<OrderTopping, ITopping<Order>>(Fallback = Fallback.Silent)]
	public static class OrderToppingModule;

	[Module]
	[Singleton<SugarGlaze, IGlaze>(Fallback = Fallback.Silent)]
	public static class SugarGlazeDefaultModule;

	[Module]
	[Singleton(typeof(GenericTopping<>), typeof(ITopping<>), Fallback = Fallback.Silent)]
	public static class GenericToppingModule;

	[Container]
	[Import(typeof(VanillaModule))]
	[Import(typeof(CaramelModule))]
	[Transient<SyrupBar>]
	public static partial class StrongShop;

	[Container]
	[Import(typeof(VanillaDefaultModule))]
	[Import(typeof(CaramelDefaultModule))]
	[Transient<SyrupBar>]
	public static partial class DefaultsShop;

	[Container]
	[Import(typeof(VanillaDefaultModule))]
	[Import(typeof(CaramelModule))]
	[Transient<SyrupBar>]
	public static partial class MixedShop;

	[Container]
	[Import(typeof(VanillaDefaultModule))]
	[Import(typeof(EagerCaramelDefaultModule))]
	public static partial class EagerDefaultsShop;

	[Container]
	[Singleton<Vanilla, ISyrup>(Fallback = Fallback.Silent)]
	[Singleton<Caramel, ISyrup>(Fallback = Fallback.Silent)]
	[Transient<SyrupBar>]
	public static partial class ContainerDefaultsShop;

	[Container]
	[Import(typeof(VanillaDefaultModule))]
	[Singleton<Hazelnut, ISyrup>(Fallback = Fallback.Silent)]
	[Transient<SyrupBar>]
	public static partial class ContainerAndModuleDefaultsShop;

	[Container]
	[Import(typeof(OrderToppingModule))]
	[Import(typeof(GenericToppingModule))]
	[Transient<ToppingBar>]
	public static partial class OpenDefaultsShop;

	[Container]
	[Import(typeof(OrderToppingModule))]
	[Singleton(typeof(GenericTopping<>), typeof(ITopping<>))]
	[Transient<ToppingBar>]
	public static partial class StrongOpenOverClosedDefaultShop;

	[Container]
	[Import(typeof(SugarGlazeDefaultModule))]
	[Scan<IGlaze>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
	[Transient<GlazeBar>]
	public static partial class ScanOverDefaultShop;

	[Container]
	[Import(typeof(VanillaHouseModule))]
	[Import(typeof(CaramelHouseModule))]
	public static partial class KeyedDefaultsShop;

	[Container]
	[Import(typeof(VanillaKeyedModule))]
	[Import(typeof(CaramelKeyedModule))]
	public static partial class KeyedShop;

	[Container]
	[Import(typeof(VanillaModule))]
	[Import(typeof(CaramelModule))]
	[Composite<AllSyrups, ISyrup>]
	public static partial class CompositeShop;

	[Container]
	[Import(typeof(VanillaDefaultModule))]
	[Transient<SyrupBar>]
	public static partial class BaseShop;

	[Container(Extends = typeof(BaseShop))]
	[Import(typeof(CaramelDefaultModule))]
	public static partial class ExtendingShop;

	[Container]
	[Singleton<Vanilla, ISyrup>(Fallback = Fallback.Silent)]
	[Transient<SyrupBar>]
	public static partial class BaseDefaultShop;

	[Container(Extends = typeof(BaseDefaultShop))]
	[Singleton<Caramel, ISyrup>(Fallback = Fallback.Silent)]
	public static partial class ExtendingDefaultShop;

	[Container]
	[Singleton<Vanilla, ISyrup>]
	[Transient<SyrupBar>]
	public static partial class StrongBaseShop;

	[Container(Extends = typeof(StrongBaseShop))]
	[Singleton<Caramel, ISyrup>(Fallback = Fallback.Silent)]
	public static partial class ExtendingStrongBaseShop;

	[Container]
	[Import(typeof(CrossAssemblyVanillaModule))]
	[Import(typeof(CrossAssemblyCaramelModule))]
	[Transient<LibrarySyrupBar>]
	public static partial class LibraryShop;

	[Container]
	[Import(typeof(CrossAssemblyVanillaDefaultModule))]
	[Import(typeof(CrossAssemblyCaramelDefaultModule))]
	[Transient<LibrarySyrupBar>]
	public static partial class LibraryDefaultsShop;
}
