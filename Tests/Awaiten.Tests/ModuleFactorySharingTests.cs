using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Registrations of a container in another assembly of an implementation that a module of Awaiten.Tests.Support builds
///     through its generated factory, which the container calls for every registration, as a container in the module's
///     own assembly constructs one instance for all of them, and an extending container that inherits such a registration.
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
				.Because("the container's activation hook runs once, before the module's async initializer convention");
		}

		await That(greeter.Steps).IsEqualTo(["activate", "convention", "release", "dispose",])
			.Because("the container's release hook runs once, before the instance is disposed");
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
}

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
[Import(typeof(CrossAssemblyFactorySharingModule))]
public static partial class FactorySharingBaseContainer;

[Container(Extends = typeof(FactorySharingBaseContainer))]
public static partial class ExtendingFactorySharingContainer;

[Container(Extends = typeof(CrossAssemblyFactorySharingBaseContainer))]
public static partial class ExtendingThirdAssemblyFactorySharingContainer;
