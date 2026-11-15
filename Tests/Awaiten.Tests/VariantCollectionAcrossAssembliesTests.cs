using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A decorator of a module in another assembly takes a collection of an internal contravariant handler that the
///     module registers only for a more general event (<see cref="CrossAssemblyVariantCollectionModule" />). The
///     module's generated factory takes the collection through the handle of the general handler, as a container in the
///     module's own assembly fills it through variance. For a public element type, the factory takes the public
///     collection as well (<see cref="CrossAssemblyPublicVariantCollectionModule" />).
/// </summary>
public class VariantCollectionAcrossAssembliesTests
{
	[Test]
	public async Task AnotherAssembly_PassesTheVariantRegistration()
	{
		using VariantCollectionContainer.Root container = new();

		await That(container.Resolve<IVariantCollectionBrew>().Handlers).IsEqualTo(["general", "other general",])
			.Because("a collection of handlers of the specific event holds the handlers of the general event through contravariance, in registration order as in the module's own assembly");
	}

	[Test]
	public async Task AnotherAssembly_PassesThePublicMembersBeforeTheVariantRegistration()
	{
		using PublicVariantCollectionContainer.Root container = new();

		await That(container.Resolve<IVariantCollectionReader>().Handlers).IsEqualTo(["first", "second", "secret", "first", "second", "secret",])
			.Because("the registration and its decorator receive the public handlers of the event in registration order and then the internal handler through contravariance, as in the module's own assembly");
	}

	[Test]
	public async Task AnotherAssembly_SetsTheDeferredPropertyWithThePublicMembersBeforeTheVariantRegistration()
	{
		await using LateVariantCollectionContainer.Root container = new();

		ILateVariantCollectionReader reader = await container.ResolveAsync<ILateVariantCollectionReader>();

		await That(reader.Late).IsEqualTo(["first", "second", "secret",])
			.Because("the deferred property holds the public handlers in registration order and then the internal handler through contravariance, as in the module's own assembly");
		await That(reader.Initialized).IsEqualTo(["first", "second", "secret",])
			.Because("the async initializer receives the same collection as the property");
	}

	[Test]
	public async Task AnotherAssembly_PassesThePublicMembersBeforeTheCovariantRegistration()
	{
		using CovariantCollectionContainer.Root container = new();

		await That(container.Resolve<IProducerVariantCollectionReader>().Producers).IsEqualTo(["first", "second", "secret",])
			.Because("the collection holds the public producers in registration order and then the internal producer through covariance, as in the module's own assembly");
	}
}

[Container]
[Import(typeof(CrossAssemblyVariantCollectionModule))]
public static partial class VariantCollectionContainer;

[Container]
[Import(typeof(CrossAssemblyPublicVariantCollectionModule))]
public static partial class PublicVariantCollectionContainer;

[Container]
[Import(typeof(CrossAssemblyLateVariantCollectionModule))]
public static partial class LateVariantCollectionContainer;

[Container]
[Import(typeof(CrossAssemblyCovariantCollectionModule))]
public static partial class CovariantCollectionContainer;
