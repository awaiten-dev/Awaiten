using System.Threading;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of the internal async initializer conventions of a public module in another assembly: a container
///     without <c>InternalsVisibleTo</c> runs each of them exactly once per instance through what the module generates.
/// </summary>
public sealed partial class ModuleInternalConventionTests
{
	[Container]
	[Import(typeof(InternalConventionModule))]
	public static partial class ConventionShop;

	[Container]
	[Import(typeof(ServiceConventionModule))]
	public static partial class ServiceConventionShop;

	[Test]
	public async Task TakingAnInternalService_TheConventionRunsOnceWithTheModulesInstance()
	{
		using CancellationTokenSource cancellation = new();
		await using ServiceConventionShop.Root container = new();

		ServiceConventionBrewer brewer = await container.ResolveAsync<ServiceConventionBrewer>(cancellation.Token);
		IServiceConventionGrinder grinder = await container.ResolveAsync<IServiceConventionGrinder>(cancellation.Token);

		await That(brewer.Runs).IsEqualTo(1)
			.Because("the container runs the convention through the module's wrapper, which takes the internal recipe through its handle");
		await That(grinder.Device.Runs).IsEqualTo(1);
		await That(brewer.Recipe).IsNotNull();
		await That(grinder.Device.Recipe).IsSameAs(brewer.Recipe)
			.Because("the recipe is a singleton of the module, which the container builds once through its handle");
		await That(brewer.Token).IsEqualTo(cancellation.Token)
			.Because("the wrapper forwards the resolve-time token to the convention");
	}

	[Test]
	public async Task OnAPublicImplementation_TheConventionsRunOncePerInstanceAfterOnActivated()
	{
		using CancellationTokenSource cancellation = new();
		await using ConventionShop.Root container = new();

		InternalConventionBrewer first = await container.ResolveAsync<InternalConventionBrewer>(cancellation.Token);
		InternalConventionBrewer second = await container.ResolveAsync<InternalConventionBrewer>(cancellation.Token);

		await That(first.Steps).IsEqualTo(["activated", "warmed", "calibrated",])
			.Because("the container runs the internal method and the internal interface through the module's wrappers, after the activation hook and in declaration order");
		await That(second.Steps).IsEqualTo(["activated", "warmed", "calibrated",])
			.Because("a transient is initialized once per instance");
		await That(first.Token).IsEqualTo(cancellation.Token)
			.Because("the wrapper forwards the resolve-time token to the convention");
		await That(container.GetResolvability<InternalConventionBrewer>().Status).IsEqualTo(AwaitenResolvabilityStatus.AsyncOnly)
			.Because("the conventions make the brewer async-initialized, though this container cannot see them");
	}

	[Test]
	public async Task OnAnInternalImplementationUnderOneService_TheConventionRunsOncePerScope()
	{
		await using ConventionShop.Root container = new();
		await using IAwaitenScope scope = await container.CreateScopeAsync();

		IInternalConventionGrinder first = await scope.ResolveAsync<IInternalConventionGrinder>();
		IInternalConventionGrinder second = await scope.ResolveAsync<IInternalConventionGrinder>();

		await That(second).IsSameAs(first);
		await That(first.Device.Steps).IsEqualTo(["warmed",])
			.Because("the module's factory builds the internal grinder and the container runs the convention through the module's wrapper once");
	}

	[Test]
	public async Task OnAnInternalImplementationUnderTwoServices_TheConventionRunsOnceOnTheSharedInstance()
	{
		await using ConventionShop.Root container = new();

		IInternalConventionKettle kettle = await container.ResolveAsync<IInternalConventionKettle>();
		IInternalConventionHeater heater = await container.ResolveAsync<IInternalConventionHeater>();

		await That(heater.Device).IsSameAs(kettle.Device);
		await That(kettle.Device.Steps).IsEqualTo(["warmed",])
			.Because("the container runs the internal method through the wrapper of the kettle's handle once");
	}

	[Test]
	public async Task OnAnInternalImplementationAPrivateFactoryReturns_TheConventionsRunOncePerInstance()
	{
		await using ConventionShop.Root container = new();

		IInternalConventionMill first = await container.ResolveAsync<IInternalConventionMill>();
		IInternalConventionMill second = await container.ResolveAsync<IInternalConventionMill>();

		await That(second).IsNotSameAs(first);
		await That(first.Device.Steps).IsEqualTo(["warmed", "calibrated",])
			.Because("the module records the conventions of the mill its factory returns as the public service, and the container runs them through the module's wrappers");
		await That(second.Device.Steps).IsEqualTo(["warmed", "calibrated",]);
		await That(container.GetResolvability<IInternalConventionMill>().Status).IsEqualTo(AwaitenResolvabilityStatus.AsyncOnly)
			.Because("the conventions make the mill async-initialized, though this container sees only its public service");
	}

	[Test]
	public async Task OnAnInternalImplementationAPrivateAsyncFactoryReturns_TheConventionsRunOnce()
	{
		await using ConventionShop.Root container = new();

		IInternalConventionAsyncMill first = await container.ResolveAsync<IInternalConventionAsyncMill>();
		IInternalConventionAsyncMill second = await container.ResolveAsync<IInternalConventionAsyncMill>();

		await That(second).IsSameAs(first);
		await That(first.Device.Steps).IsEqualTo(["warmed", "calibrated",])
			.Because("the container awaits the factory's wrapper and runs the conventions the module recorded once on the singleton");
	}

	[Test]
	public async Task OnAScanMatch_TheConventionRunsOnce()
	{
		await using ConventionShop.Root container = new();

		await container.InitializeAsync();
		IInternalConventionPlugin plugin = await container.ResolveAsync<IInternalConventionPlugin>();

		await That(plugin.Device.Steps).IsEqualTo(["warmed",])
			.Because("InitializeAsync initializes the async singleton once and a later resolve returns it as it is");
	}
}
