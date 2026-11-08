using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of the internal <c>Factory</c> and <c>Instance</c> members of <see cref="InternalProducerModule" />,
///     which return internal implementations. Awaiten.Tests.Support grants this assembly no <c>InternalsVisibleTo</c>, so
///     the container here calls the public wrappers the module generated, which return the public services.
/// </summary>
public partial class ModuleInternalProducerTests
{
	[Test]
	public async Task InternalFactoryMember_ProducesTheSingleton()
	{
		using ProducerContainer.Root container = new();

		IProducedGrinder grinder = container.Resolve<IProducedGrinder>();

		await That(grinder.GetType().Name).IsEqualTo("ProducedGrinder");
		await That(container.Resolve<IProducedGrinder>()).IsSameAs(grinder)
			.Because("the wrapper only forwards to the factory member, and the container keeps the singleton it returned");
	}

	[Test]
	public async Task InternalFactoryMember_ItsDisposableResultIsDisposedWithTheContainer()
	{
		IProducedGrinder grinder;
		using (ProducerContainer.Root container = new())
		{
			grinder = container.Resolve<IProducedGrinder>();
			await That(grinder.Disposed).IsFalse();
		}

		await That(grinder.Disposed).IsTrue()
			.Because("the container reads the disposal off the implementation's metadata, though it cannot name the implementation");
	}

	[Test]
	public async Task InternalAsyncFactoryMember_ProducesTheSingleton()
	{
		await using ProducerContainer.Root container = new();

		IProducedKettle kettle = await container.ResolveAsync<IProducedKettle>();

		await That(kettle.GetType().Name).IsEqualTo("ProducedKettle")
			.Because("the wrapper awaits the factory member and returns its result as the public service");
		await That(await container.ResolveAsync<IProducedKettle>()).IsSameAs(kettle);
	}

	[Test]
	public async Task InternalInstanceMember_IsServed()
	{
		using ProducerContainer.Root container = new();

		await That(container.Resolve<IProducedMenu>().GetType().Name).IsEqualTo("ProducedMenu")
			.Because("the wrapper returns the instance the module holds as the public service");
	}

	[Test]
	public async Task InternalFactoryMember_RunsTheModuleConventionOnTheImplementation()
	{
		await using WarmedProducerContainer.Root container = new();

		IWarmedGrinder grinder = await container.ResolveAsync<IWarmedGrinder>();

		await That(grinder.WarmedUp).IsTrue()
			.Because("the module's convention matches the internal implementation, as in the module's own assembly, not the public service the wrapper returns");
	}

	[Container]
	[Import(typeof(InternalProducerModule))]
	public static partial class ProducerContainer;

	[Container]
	[Import(typeof(WarmedProducerModule))]
	public static partial class WarmedProducerContainer;
}
