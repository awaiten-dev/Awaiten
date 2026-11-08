using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of module shapes every container now treats alike: a private <c>Factory</c> or <c>Instance</c>
///     member of a public service, called through the module's wrapper, and a decorator's collection of an internal
///     type nothing registers, passed empty.
/// </summary>
public partial class ModuleLeftoverShapeTests
{
	[Test]
	public async Task ContainerInAnotherAssembly_CallsThePrivateMembersThroughTheWrappers()
	{
		using LeftoverShapesContainer.Root container = new();

		await That(container.Resolve<IPrivateFactoryKettle>()).IsNotNull()
			.Because("the module's public wrapper calls the private factory");
		await That(container.Resolve<IPrivateInstanceShelf>()).IsSameAs(CrossAssemblyLeftoverShapesModule.ExpectedShelf);
	}

	[Test]
	public async Task ContainerInAnotherAssembly_PassesAnEmptyCollectionOfAnUnregisteredInternalType()
	{
		using LeftoverShapesContainer.Root container = new();

		await That(container.Resolve<ILeftoverGrinder>().Secrets).IsEqualTo(0)
			.Because("nothing registers the internal element type, so the decorator receives the empty collection a container in the module's assembly passes");
	}

	[Test]
	public async Task ContainerInTheModulesAssembly_CallsThePrivateFactoryThroughTheWrapper()
	{
		using LocalPrivateFactoryContainer.Root container = new();

		await That(container.Resolve<ILocalKettle>()).Is<LocalPrivateFactoryModule.Kettle>();
	}

	public interface ILocalKettle;

	[Module]
	[Singleton<ILocalKettle>(Factory = nameof(Make))]
	public static partial class LocalPrivateFactoryModule
	{
		public sealed class Kettle : ILocalKettle;

		private static ILocalKettle Make() => new Kettle();
	}

	[Container]
	[Import(typeof(CrossAssemblyLeftoverShapesModule))]
	public static partial class LeftoverShapesContainer;

	[Container]
	[Import(typeof(LocalPrivateFactoryModule))]
	public static partial class LocalPrivateFactoryContainer;
}
