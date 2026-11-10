using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Elsewhere;
using Awaiten.Tests.GrantingSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Registrations of a container in another assembly of an implementation that a module of Awaiten.Tests.Support builds
///     through its generated factory, which the container calls for every registration, as a container in the module's
///     own assembly constructs one instance for all of them, a friend module of another assembly that builds it through a
///     generated factory of its own, and an extending container that inherits such a registration.
/// </summary>
public class ModuleFactorySharingTests
{
	[Test]
	public async Task AContainerRegistrationBesideTheModule_IsOneInstanceWithThePropertySetOnce()
	{
		using GreeterBesideModuleContainer.Root container = new();

		FactorySharingGreeter greeter = container.Resolve<FactorySharingGreeter>();

		await That(container.Resolve<IFactorySharingGreeter>()).IsSameAs(greeter)
			.Because("the container's registration calls the module's generated factory, so both coalesce into one singleton");
		await That(greeter.InjectedClock).IsSameAs(container.Resolve<FactorySharingClock>());
		await That(greeter.ClockAssignments).IsEqualTo(1)
			.Because("only the module's generated factory sets the internal property, once per instance");
	}

	[Test]
	public async Task AContainerRegistrationBesideAFriendModule_IsOneInstanceWithThePropertySetOnce()
	{
		using GreeterBesideFriendModuleContainer.Root container = new();

		FriendFactorySharingGreeter greeter = container.Resolve<FriendFactorySharingGreeter>();

		await That(container.Resolve<IFactorySharingGreeter>()).IsSameAs(greeter);
		await That(greeter.InjectedClock).IsSameAs(container.Resolve<FactorySharingClock>());
		await That(greeter.ClockAssignments).IsEqualTo(1);
	}

	[Test]
	public async Task AContainerRegistrationBesideALocalModule_IsOneInstanceWithThePropertySetOnce()
	{
		using GreeterBesideLocalModuleContainer.Root container = new();

		LocalFactorySharingGreeter greeter = container.Resolve<LocalFactorySharingGreeter>();

		await That(container.Resolve<IFactorySharingGreeter>()).IsSameAs(greeter);
		await That(greeter.InjectedClock).IsSameAs(container.Resolve<FactorySharingClock>());
		await That(greeter.ClockAssignments).IsEqualTo(1);
	}

	[Test]
	public async Task AHookedContainerRegistrationBesideTheModule_RunsTheContainersHooksOnceOnTheSharedInstance()
	{
		HookedFactorySharingGreeter greeter;
		await using (HookedGreeterBesideModuleContainer.Root container = new())
		{
			greeter = await container.ResolveAsync<HookedFactorySharingGreeter>();

			await That(await container.ResolveAsync<IHookedFactorySharingGreeter>()).IsSameAs(greeter)
				.Because("the hooked registration calls the module's generated factory, so both coalesce into one singleton");
			await That(greeter.ClockAssignments).IsEqualTo(1);
			await That(greeter.Steps).IsEqualTo(["activate", "convention",])
				.Because("the container's activation hook runs once, before the container's async initializer convention");
		}

		await That(greeter.Steps).IsEqualTo(["activate", "convention", "release", "dispose",])
			.Because("the container's release hook runs once, before the instance is disposed");
	}

	[Test]
	public async Task AModuleBesideAFriendModuleThatBuildsTheImplementationThroughItsOwnFactory_RunsTheFriendsHookOnEveryInstance()
	{
		using KettleOfAFriendAndTheModuleContainer.Root container = new();

		await That(container.Resolve<GrantingKettle>()).IsNotSameAs(container.Resolve<GrantingKettle>());
		await That(container.Resolve<GrantingKettle>().Activations).IsEqualTo(1);
		await That(((GrantingKettle)container.Resolve<IGrantingKettle>()).Activations).IsEqualTo(1)
			.Because("the module's registration calls the generated factory of the friend module, which comes first, so its instances take the friend module's activation hook once");
	}

	[Test]
	public async Task AnExtendingContainerOfABaseInThisAssembly_BuildsTheInternalImplementation()
	{
		using ExtendingFactorySharingContainer.Root container = new();

		await That(container.Resolve<IFactorySharingKettle>()).IsSameAs(container.Resolve<IFactorySharingKettle>())
			.Because("the extending container builds the inherited internal implementation through the module's generated factory");
	}

	[Test]
	public async Task AnExtendingContainerOfABaseInAThirdAssembly_BuildsTheInternalImplementation()
	{
		using ExtendingThirdAssemblyFactorySharingContainer.Root container = new();

		await That(container.Resolve<IFactorySharingKettle>()).IsSameAs(container.Resolve<IFactorySharingKettle>());
		await That(container.Resolve<IFactorySharingGreeter>().GetType()).IsEqualTo(typeof(FactorySharingGreeter));
		await That(((FactorySharingGreeter)container.Resolve<IFactorySharingGreeter>()).ClockAssignments).IsEqualTo(1);
	}

	[Test]
	public async Task ARegistrationOfAModuleOfThisAssembly_IsOneInstanceWithTheModulesRegistration()
	{
		using KettleBesideLocalModuleContainer.Root container = new();

		await That(container.Resolve<InternalConstructorKettle>()).IsSameAs(container.Resolve<IInternalConstructorKettle>())
			.Because("the local module's registration calls the generated factory of the kettle's module, so both coalesce into one singleton");
	}

	[Test]
	public async Task ARegistrationOfAModuleOfAThirdLibrary_IsOneInstanceWithTheModulesRegistration()
	{
		using KettleOfAThirdLibraryContainer.Root container = new();

		await That(container.Resolve<InternalConstructorKettle>()).IsSameAs(container.Resolve<IInternalConstructorKettle>())
			.Because("the third library cannot construct the kettle, so its registration calls the generated factory of the kettle's module");
	}

	[Test]
	public async Task AClosedRegistrationOfAClosingOfTheModulesOpenRegistration_IsOneInstance()
	{
		using RepositoryBesideModuleContainer.Root container = new();

		RepositoryConsumer consumer = container.Resolve<RepositoryConsumer>();

		await That(consumer.Beans).IsSameAs(container.Resolve<InternalConstructorRepository<int>>())
			.Because("the closed registration calls the module's generic factory, so it and the closing are one singleton");
		await That(consumer.Orders).IsSameAs(container.Resolve<InternalConstructorRepository<string>>())
			.Because("a closing only the open registration serves is built through the module's generic factory too");
	}

	[Test]
	public async Task AContainerRegistrationThatComesFirst_RunsOnlyTheContainersConventions()
	{
		await using BrewerBesideModuleContainer.Root container = new();

		InternalConstructorBrewer brewer = await container.ResolveAsync<InternalConstructorBrewer>();

		await That(brewer.Steps).IsEqualTo(["container",])
			.Because("the container's registration comes first, so its conventions initialize the instance the module's factory builds, as in the module's own assembly");
	}

	[Test]
	public async Task AnExtendingContainerOfABaseInTheModulesAssembly_SharesTheInheritedKettle()
	{
		using ExtendingInternalConstructorBaseContainer.Root container = new();

		await That(container.Resolve<InternalConstructorKettle>()).IsSameAs(container.Resolve<IInternalConstructorKettle>())
			.Because("the inherited registration calls the module's generated factory instead of the constructor it cannot reach");
	}

	[Test]
	public async Task AnExtendingContainerOfABaseInAThirdAssembly_SharesTheInheritedKettle()
	{
		using ExtendingThirdLibraryKettleBaseContainer.Root container = new();

		await That(container.Resolve<InternalConstructorKettle>()).IsSameAs(container.Resolve<IInternalConstructorKettle>());
	}
}

/// <summary>A consumer of the closing <see cref="RepositoryBesideModuleContainer" /> registers closed and of one it does not.</summary>
public sealed class RepositoryConsumer(InternalConstructorRepository<int> beans, InternalConstructorRepository<string> orders)
{
	public InternalConstructorRepository<int> Beans { get; } = beans;

	public InternalConstructorRepository<string> Orders { get; } = orders;
}

[Module]
[Singleton<InternalConstructorKettle>]
public static partial class LocalKettleModule;

[Container]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
[Import(typeof(LocalKettleModule))]
public static partial class KettleBesideLocalModuleContainer;

[Container]
[Import(typeof(CrossAssemblyThirdLibraryKettleModule))]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
public static partial class KettleOfAThirdLibraryContainer;

[Container]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
[Singleton<InternalConstructorRepository<int>>]
[Singleton<RepositoryConsumer>]
public static partial class RepositoryBesideModuleContainer;

[Container]
[AsyncInitializer<IContainerWarmable>(nameof(IContainerWarmable.ContainerWarmAsync))]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
[Singleton<InternalConstructorBrewer>]
public static partial class BrewerBesideModuleContainer;

[Container(Extends = typeof(CrossAssemblyInternalConstructorBaseContainer))]
public static partial class ExtendingInternalConstructorBaseContainer;

[Container(Extends = typeof(CrossAssemblyThirdLibraryKettleBaseContainer))]
public static partial class ExtendingThirdLibraryKettleBaseContainer;

/// <summary>The local counterpart of <see cref="FactorySharingGreeter" />, registered by a module of this assembly.</summary>
public sealed class LocalFactorySharingGreeter : IFactorySharingGreeter
{
	private FactorySharingClock _clock = null!;

	[Inject]
	internal FactorySharingClock Clock
	{
		get => _clock;
		set
		{
			_clock = value;
			ClockAssignments++;
		}
	}

	public FactorySharingClock InjectedClock => Clock;

	public int ClockAssignments { get; private set; }
}

[Module]
[Singleton<FactorySharingClock>]
[Singleton<LocalFactorySharingGreeter, IFactorySharingGreeter>]
public static partial class LocalFactorySharingModule;

[Container]
[Import(typeof(CrossAssemblyFactorySharingModule))]
[Singleton<FactorySharingGreeter>]
public static partial class GreeterBesideModuleContainer;

[Container]
[AsyncInitializer<ISelfCompiledStartable>(nameof(ISelfCompiledStartable.StartAsync))]
[Import(typeof(CrossAssemblyHookedFactorySharingModule))]
[Singleton<HookedFactorySharingGreeter>(OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class HookedGreeterBesideModuleContainer
{
	private static void Activate(HookedFactorySharingGreeter greeter) => greeter.Steps.Add("activate");

	private static void Release(HookedFactorySharingGreeter greeter) => greeter.Steps.Add("release");
}

[Container]
[Import(typeof(CrossAssemblyFriendFactorySharingModule))]
[Singleton<FriendFactorySharingGreeter>]
public static partial class GreeterBesideFriendModuleContainer;

[Container]
[Import(typeof(LocalFactorySharingModule))]
[Singleton<LocalFactorySharingGreeter>]
public static partial class GreeterBesideLocalModuleContainer;

[Container]
[Import(typeof(ElsewhereKettleModule))]
[Import(typeof(GrantingKettleModule))]
public static partial class KettleOfAFriendAndTheModuleContainer;

[Container]
[Import(typeof(CrossAssemblyFactorySharingModule))]
public static partial class FactorySharingBaseContainer;

[Container(Extends = typeof(FactorySharingBaseContainer))]
public static partial class ExtendingFactorySharingContainer;

[Container(Extends = typeof(CrossAssemblyFactorySharingBaseContainer))]
public static partial class ExtendingThirdAssemblyFactorySharingContainer;
