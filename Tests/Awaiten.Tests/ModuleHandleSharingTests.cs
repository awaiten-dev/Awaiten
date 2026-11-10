using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Registrations of a container in another assembly that share the instance a module of Awaiten.Tests.Support builds
///     through a generated handle, as a container in the module's own assembly coalesces them into one instance.
/// </summary>
public class ModuleHandleSharingTests
{
	[Test]
	public async Task PublicImplementationTheContainerRegistersToo_IsOneInstance()
	{
		using PublicStoreContainer.Root container = new();

		await That(container.Resolve<PublicHandledStore>()).IsSameAs(container.Resolve<IHandledStoreUser>().Store)
			.Because("the container's registration of the public implementation serves the instance the module's handle holds");
	}

	[Test]
	public async Task PublicImplementationAModuleOfTheContainerRegistersToo_IsOneInstance()
	{
		using PublicStoreModuleContainer.Root container = new();

		await That(container.Resolve<PublicHandledStore>()).IsSameAs(container.Resolve<IHandledStoreUser>().Store)
			.Because("a module of this assembly that registers the public implementation shares the handle's instance too");
	}

	[Test]
	public async Task TransientDisposableThroughAnAlias_IsWithheldOnTheRoot()
	{
		using AliasedDisposableContainer.Root container = new();

		await That((object)container is IAwaitenResolver<IAliasedCup>).IsFalse()
			.Because("the root withholds a transient disposable under strict lifetime safety, also when an alias serves it from a handle");
		await That(() => container.Resolve<IAliasedCup>()).Throws<InvalidOperationException>()
			.WithMessage("*withheld*Owned<*").AsWildcard();

		using AliasedDisposableContainer.Scope scope = container.CreateScope();
		await That(scope.Resolve<IAliasedCup>()).IsNotNull();
	}

	[Test]
	public async Task InternalServiceOfAModuleTheContainerDoesNotImport_IsProvidedByTheImportedModule()
	{
		using ProvidedStoreContainer.Root container = new();

		await That(container.Resolve<IProvidedChef>().Store.GetType().Name).IsEqualTo("ProvidedStore")
			.Because("the module first by name declares the handle, but the imported module that registers the store serves it");
	}

	[Test]
	public async Task EagerDefaultTheContainerOverrides_LeavesTheHandleLazy()
	{
		using OverriddenEagerContainer.Root container = new();

		await That(OverriddenClockCounter.Constructed).IsEqualTo(0)
			.Because("the eager registration is an overridable default the container overrides, so only the lazy registration under the internal service remains");
		await That(container.Resolve<IOverriddenClock>()).IsExactly<LocalClock>();
	}

	[Test]
	public async Task EveryRegistrationOverridden_LeavesTheHandleUnbuilt()
	{
		using DroppedEagerContainer.Root container = new();

		await That(DroppedClockCounter.Constructed).IsEqualTo(0)
			.Because("the container overrides every registration of the implementation, so nothing is left to build eagerly");
	}

	[Test]
	public async Task InternalImplementationTwoModulesRegisterUnderOnePublicServiceEach_IsOneInstance()
	{
		using GrillContainer.Root container = new();

		await That((object)container.Resolve<IGrillFront>()).IsSameAs(container.Resolve<IGrillBack>())
			.Because("both modules build the grill into one instance handle, as a container in their own assembly builds one grill");
	}

	[Test]
	public async Task ContextualEagerDefaultBesideAnOverrideOfItsService_IsBuiltEagerly()
	{
		int before = ContextualClockCounter.Constructed;
		using ContextualEagerContainer.Root container = new();

		await That(ContextualClockCounter.Constructed - before).IsEqualTo(1)
			.Because("the override claims the unkeyed service only, so the contextual eager default survives and comes first, as in the module's own assembly");
		await That(container.Resolve<ContextualWatch>().Clock.GetType().Name).IsEqualTo("ContextualClock");
	}

	[Test]
	public async Task DecoratorOfAnInternalServiceAnotherModuleRegisters_DecoratesIt()
	{
		IDecoratedTillUser user;
		using (DecoratedTillContainer.Root container = new())
		{
			user = container.Resolve<IDecoratedTillUser>();
			await That(user.Till.GetType().Name).IsEqualTo("LoggingTill")
				.Because("the decorating module builds its decorator into the handle the registering module declares");
		}

		await That(user.DecoratorDisposed).IsTrue()
			.Because("the container disposes the decorator link through the handle variant of its disposal");
	}

	[Test]
	public async Task FactoryTakingAnInternalServiceReplacedByAnExtendingContainer_NeverRuns()
	{
		using ReplacedHandledFactoryContainer.Root container = new();

		await That(container.Resolve<IHandledFactoryClock>()).IsExactly<FakeHandledFactoryClock>()
			.Because("the extending container's factory replaces the inherited one, although the module calls it through a handle");
		await That(HandledFactoryCounter.FactoryCalls).IsEqualTo(0);
		await That(HandledFactoryCounter.TimeSources).IsEqualTo(0)
			.Because("nothing the container resolved needs the internal service the replaced factory takes");
	}

	[Test]
	public async Task FactoryTakingAnInternalServiceBesideTheContainersOwnFactory_LosesTheSingleResolution()
	{
		using OwnHandledFactoryContainer.Root container = new();

		await That(container.Resolve<IHandledFactoryClock>()).IsExactly<FakeHandledFactoryClock>()
			.Because("the container's own factory wins over the module's factory of the same service");
		await That(HandledFactoryCounter.FactoryCalls).IsEqualTo(0);
	}

	[Test]
	public async Task TwoFactoriesOfOneServiceTakingAnInternalService_AreBothCollectionMembers()
	{
		using TwoClocksContainer.Root container = new();

		await That(container.Resolve<IEnumerable<IProducedClock>>().Select(clock => clock.Name)).IsEqualTo(["first", "second",])
			.Because("each factory is a producer of its own, as in a container of the module's own assembly");
	}

	[Test]
	public async Task FactoriesOfOneServiceInTwoModules_AreAllCollectionMembers()
	{
		using ThreeClocksContainer.Root container = new();

		await That(container.Resolve<IEnumerable<IProducedClock>>().Select(clock => clock.Name)).IsEqualTo(["first", "second", "third",])
			.Because("the factory of the other module is a producer of its own too, not a second production of the same instance");
	}

	[Test]
	public async Task FactoriesOfAClassAnotherModuleConstructs_AreTwoProducersWhenThatModuleIsNotImported()
	{
		using ImportedProducersContainer.Root container = new();

		ImportedProducerClock[] clocks = container.Resolve<IEnumerable<ImportedProducerClock>>().ToArray();

		await That(clocks.Select(clock => clock.Name)).IsEqualTo(["a", "b",])
			.Because("neither imported module constructs the class, so each factory is a producer of its own, as in a container of the modules' own assembly that imports the same modules");
		await That(clocks[0]).IsNotSameAs(clocks[1]);
		// AWT155 warns of exactly this single resolution, as in a container of the modules' own assembly.
#pragma warning disable AWT155
		await That(container.Resolve<ImportedProducerClock>()).IsSameAs(clocks[0])
			.Because("the first producer wins the single resolution");
#pragma warning restore AWT155
	}

	[Test]
	public async Task FactoryOfAClassAnotherModuleScans_IsOneInstance()
	{
		using ScannedProducerContainer.Root container = new();

		await That(container.Resolve<IScannedProducerAppliance>()).IsSameAs(container.Resolve<ScannedProducerClock>())
			.Because("the scan names the class, so the factory produces its one instance and the scan match shares it, as in a container of the modules' own assembly that imports the same modules");
		await That(container.Resolve<IEnumerable<IScannedProducerAppliance>>()).HasSingle();
	}

	[Test]
	public async Task FactoryOfAClassItsOwnModuleScans_IsOneInstance()
	{
		using SelfScannedContainer.Root container = new();

		SelfScannedClock clock = container.Resolve<SelfScannedClock>();

		await That(clock.FromFactory).IsTrue()
			.Because("the scan match yields to the module's registration of the class, as in a container of the module's own assembly");
		await That(container.Resolve<ISelfScannedAppliance>()).IsSameAs(clock);
		await That(container.Resolve<IEnumerable<ISelfScannedAppliance>>()).HasSingle();
	}

	[Test]
	public async Task InstanceOfAClassAnotherModuleScans_IsSharedWithoutTheModuleOfTheScansServices()
	{
		using SharedScanContainer.Root container = new();

		await That(container.Resolve<SharedScanClock>()).IsSameAs(CrossAssemblySharedScanInstanceModule.Default);
		await That(container.Resolve<ISharedScanAppliance>()).IsSameAs(CrossAssemblySharedScanInstanceModule.Default)
			.Because("the scan match shares the registered instance and never builds the clock with the internal service another module registers");
	}
}

[Container]
[Import(typeof(CrossAssemblySelfScannedModule))]
public static partial class SelfScannedContainer;

[Container]
[Import(typeof(CrossAssemblySharedScanInstanceModule))]
[Import(typeof(CrossAssemblySharedScanScanModule))]
public static partial class SharedScanContainer;

[Container]
[Import(typeof(CrossAssemblyScannedProducerAModule))]
[Import(typeof(CrossAssemblyScannedProducerScanModule))]
public static partial class ScannedProducerContainer;

[Container]
[Import(typeof(CrossAssemblyImportedProducerAModule))]
[Import(typeof(CrossAssemblyImportedProducerBModule))]
public static partial class ImportedProducersContainer;

[Container]
[Import(typeof(CrossAssemblyTwoClocksModule))]
public static partial class TwoClocksContainer;

[Container]
[Import(typeof(CrossAssemblyTwoClocksModule))]
[Import(typeof(CrossAssemblyThirdClockModule))]
public static partial class ThreeClocksContainer;

/// <summary>The clock this assembly produces over the factory of <see cref="CrossAssemblyHandledFactoryModule" />.</summary>
public sealed class FakeHandledFactoryClock : IHandledFactoryClock;

[Container]
[Import(typeof(CrossAssemblyHandledFactoryModule))]
public static partial class HandledFactoryContainer;

[Container(Extends = typeof(HandledFactoryContainer))]
[Singleton<IHandledFactoryClock>(Factory = nameof(CreateFakeClock))]
public static partial class ReplacedHandledFactoryContainer
{
#pragma warning disable CA1859 // the factory returns the registered service, which is the shape under test
	private static IHandledFactoryClock CreateFakeClock() => new FakeHandledFactoryClock();
#pragma warning restore CA1859
}

[Container]
[Import(typeof(CrossAssemblyHandledFactoryModule))]
[Singleton<IHandledFactoryClock>(Factory = nameof(CreateFakeClock))]
public static partial class OwnHandledFactoryContainer
{
#pragma warning disable CA1859 // the factory returns the registered service, which is the shape under test
	private static IHandledFactoryClock CreateFakeClock() => new FakeHandledFactoryClock();
#pragma warning restore CA1859
}

/// <summary>The clock this assembly registers over the eager default of <see cref="CrossAssemblyOverriddenEagerModule" />.</summary>
public sealed class LocalClock : IOverriddenClock;

/// <summary>The clock this assembly registers for every consumer but the watch of <see cref="CrossAssemblyContextualEagerModule" />.</summary>
public sealed class LocalContextualClock : IContextualClock;

[Container]
[Import(typeof(CrossAssemblyGrillFrontModule))]
[Import(typeof(CrossAssemblyGrillBackModule))]
public static partial class GrillContainer;

[Container]
[Import(typeof(CrossAssemblyContextualEagerModule))]
[Singleton<LocalContextualClock, IContextualClock>]
public static partial class ContextualEagerContainer;

/// <summary>The clock and timer this assembly registers over the defaults of <see cref="CrossAssemblyDroppedEagerModule" />.</summary>
public sealed class LocalClockAndTimer : IDroppedClock, IDroppedTimer;

[Container]
[Import(typeof(CrossAssemblyDroppedEagerModule))]
[Singleton<LocalClockAndTimer, IDroppedClock>]
[Singleton<LocalClockAndTimer, IDroppedTimer>]
public static partial class DroppedEagerContainer;

[Container]
[Import(typeof(CrossAssemblyOverriddenEagerModule))]
[Singleton<LocalClock, IOverriddenClock>]
public static partial class OverriddenEagerContainer;

[Container]
[Import(typeof(CrossAssemblyTillModule))]
[Import(typeof(CrossAssemblyTillLoggingModule))]
public static partial class DecoratedTillContainer;

[Container]
[Import(typeof(CrossAssemblyProvidedBModule))]
[Import(typeof(CrossAssemblyProvidedCModule))]
public static partial class ProvidedStoreContainer;

[Container]
[Import(typeof(CrossAssemblyAliasedDisposableModule))]
public static partial class AliasedDisposableContainer;

[Container]
[Import(typeof(CrossAssemblyPublicStoreModule))]
[Singleton<PublicHandledStore>]
public static partial class PublicStoreContainer;

/// <summary>A <c>[Module]</c> of this assembly that registers the public store a module of another assembly builds through a handle.</summary>
[Module]
[Singleton<PublicHandledStore>]
public static class LocalPublicStoreModule;

[Container]
[Import(typeof(LocalPublicStoreModule))]
[Import(typeof(CrossAssemblyPublicStoreModule))]
public static partial class PublicStoreModuleContainer;
