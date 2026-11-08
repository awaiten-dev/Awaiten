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

	public class PrivateMemberOfAnInternalImplementation
	{
		[Test]
		public async Task ContainerInAnotherAssembly_CallsTheWrapperThatReturnsTheService()
			=> await ProducesAndDisposes(new TuckedShapeContainer.Root(), TuckedShapeModule.ExpectedShelf);

		[Test]
		public async Task ContainerInAFriendAssembly_CallsTheWrapperThatReturnsTheImplementation()
			=> await ProducesAndDisposes(new FriendTuckedShapeContainer.Root(), ContainerSupport.FriendTuckedShapeModule.ExpectedShelf);

		[Test]
		public async Task ContainerInTheModulesAssembly_CallsTheWrapperThatReturnsTheImplementation()
			=> await ProducesAndDisposes(new ContainerSupport.FriendTuckedShapeLocalContainer.Root(), ContainerSupport.FriendTuckedShapeModule.ExpectedShelf);

		private static async Task ProducesAndDisposes(IAwaitenRoot container, ITuckedShelf expectedShelf)
		{
			ITuckedKettle kettle = container.Resolve<ITuckedKettle>();
			ITuckedShelf shelf = container.Resolve<ITuckedShelf>();
			ITuckedPot pot = container.Resolve<ITuckedPot>();
			ITuckedPan pan = container.Resolve<ITuckedPan>();
			ITuckedRack rack = container.Resolve<ITuckedRack>();
			ITuckedHook hook = container.Resolve<ITuckedHook>();
			ITuckedScale scale = container.Resolve<ITuckedScale>();
			ITuckedSpoon spoon;
			ITuckedLadle ladle;
			await using (IAwaitenScope scope = container.CreateScope())
			{
				spoon = scope.Resolve<ITuckedSpoon>();
				ladle = scope.Resolve<ITuckedLadle>();
			}

			await container.DisposeAsync();

			await That(shelf).IsSameAs(expectedShelf);
			await That(kettle.Disposed).IsTrue()
				.Because("the container disposes the internal implementation the private factory returns, whatever type it holds it as");
			await That(pan).IsSameAs(pot)
				.Because("both services of the implementation the private factory returns share its one singleton");
			await That(pot.Disposed).IsTrue();
			await That(hook).IsSameAs(rack);
			await That(ladle).IsSameAs(spoon)
				.Because("both services of the implementation the internal factory returns share its one instance per scope");
			await That(spoon.Disposed).IsTrue();
			await That(scale.Weighed).IsTrue()
				.Because("the private factory receives the internal service it takes");
		}
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

	[Container]
	[Import(typeof(TuckedShapeModule))]
	public static partial class TuckedShapeContainer;

	[Container]
	[Import(typeof(ContainerSupport.FriendTuckedShapeModule))]
	public static partial class FriendTuckedShapeContainer;
}
