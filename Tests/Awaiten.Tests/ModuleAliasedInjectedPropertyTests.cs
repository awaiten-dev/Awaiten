using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     An implementation with an internal <c>[Inject]</c> property that one module of Awaiten.Tests.Support builds into an
///     instance handle and another registers under the implementation itself, which a container in this assembly serves
///     through an alias of the handle.
/// </summary>
public class ModuleAliasedInjectedPropertyTests
{
	[Test]
	public async Task Singleton_IsOneInstanceWithThePropertySetOnce()
	{
		using AliasedInjectionContainer.Root container = new();

		AliasedSingletonBrew brew = container.Resolve<AliasedSingletonBrew>();

		await That((object)brew).IsSameAs(container.Resolve<IAliasedSingletonBrew>())
			.Because("the registration of the other module shares the instance the handle holds");
		await That(brew.AssignedClock).IsSameAs(container.Resolve<IAliasedInjectionClock>());
		await That(brew.ClockAssignments).IsEqualTo(1)
			.Because("the module's handle factory sets the property and the container sets nothing on the instance it serves through the alias");
	}

	[Test]
	public async Task Scoped_IsOneInstancePerScopeWithThePropertySetOnce()
	{
		using AliasedInjectionContainer.Root container = new();
		using AliasedInjectionContainer.Scope scope = container.CreateScope();

		AliasedScopedBrew brew = scope.Resolve<AliasedScopedBrew>();

		await That((object)brew).IsSameAs(scope.Resolve<IAliasedScopedBrew>());
		await That(brew.AssignedClock).IsSameAs(container.Resolve<IAliasedInjectionClock>());
		await That(brew.ClockAssignments).IsEqualTo(1);
	}

	[Test]
	public async Task Transient_HasThePropertySetOncePerInstance()
	{
		using AliasedInjectionContainer.Root container = new();

		AliasedTransientBrew first = container.Resolve<AliasedTransientBrew>();
		AliasedTransientBrew second = container.Resolve<AliasedTransientBrew>();

		await That(first).IsNotSameAs(second);
		await That(first.AssignedClock).IsSameAs(container.Resolve<IAliasedInjectionClock>());
		await That(first.ClockAssignments).IsEqualTo(1);
		await That(second.ClockAssignments).IsEqualTo(1);
	}

	[Test]
	public async Task RuntimeArgument_HasThePropertySetOnce()
	{
		using AliasedInjectionContainer.Root container = new();

		AliasedArgumentBrew brew = container.Resolve<Func<string, AliasedArgumentBrew>>()("latte");

		await That(brew.Order).IsEqualTo("latte");
		await That(brew.AssignedClock).IsSameAs(container.Resolve<IAliasedInjectionClock>());
		await That(brew.ClockAssignments).IsEqualTo(1)
			.Because("the factory of the handle sets the property on each instance it builds from the argument");
	}
}

[Container]
[Import(typeof(CrossAssemblyAliasedInjectionModule))]
[Import(typeof(CrossAssemblyAliasedInjectionOtherModule))]
public static partial class AliasedInjectionContainer;
