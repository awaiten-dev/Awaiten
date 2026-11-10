using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A decorator of a module in another assembly takes a collection of an internal contravariant handler that the
///     module registers only for a more general event (<see cref="CrossAssemblyVariantCollectionModule" />). The
///     module's generated factory takes the collection through the handle of the general handler, as a container in the
///     module's own assembly fills it through variance.
/// </summary>
public class VariantCollectionAcrossAssembliesTests
{
	[Test]
	public async Task AnotherAssembly_PassesTheVariantRegistration()
	{
		using VariantCollectionContainer.Root container = new();

		await That(container.Resolve<IVariantCollectionBrew>().Handlers).IsEqualTo(["general",])
			.Because("a collection of handlers of the specific event holds the handler of the general event through contravariance");
	}
}

[Container]
[Import(typeof(CrossAssemblyVariantCollectionModule))]
public static partial class VariantCollectionContainer;
