using Awaiten.Tests.ContainerSupport;

namespace Awaiten.Tests;

public partial class ContainerExtendsTests
{
	public class OpenCompositeWithoutABaseClosing
	{
		[Test]
		public async Task ABaseInTheSameAssembly_FrontsTheClosingTheExtendingContainerRequests()
		{
			using LocalOpenCompositeTestShop.Root container = new();

			await That(container.Resolve<OpenBaseHandlerUser>().Handler).Is<AllOpenBaseHandlers<string>>()
				.Because("the inherited open composite fronts the closing only the extending container requests");
			await That(container.Resolve<OpenBaseHandlerUser>().Handler.Count).IsEqualTo(1);
		}

		[Test]
		public async Task ABaseInAnotherAssembly_FrontsTheClosingTheExtendingContainerRequests()
		{
			using OpenCompositeTestShop.Root container = new();

			await That(container.Resolve<OpenBaseHandlerUser>().Handler).Is<AllOpenBaseHandlers<string>>()
				.Because("the inherited open composite fronts the closing only the extending container requests");
			await That(container.Resolve<OpenBaseHandlerUser>().Handler.Count).IsEqualTo(1);
		}
	}

	public sealed class OpenBaseHandlerUser
	{
		public OpenBaseHandlerUser(IOpenBaseHandler<string> handler) => Handler = handler;

		public IOpenBaseHandler<string> Handler { get; }
	}

	[Container]
	[Transient(typeof(OpenBaseHandler<>), typeof(IOpenBaseHandler<>))]
	[Composite(typeof(AllOpenBaseHandlers<>), typeof(IOpenBaseHandler<>))]
	public static partial class LocalOpenCompositeShop;

	[Container(Extends = typeof(LocalOpenCompositeShop))]
	[Transient<OpenBaseHandlerUser>]
	public static partial class LocalOpenCompositeTestShop;

	[Container(Extends = typeof(CrossAssemblyOpenCompositeBaseContainer))]
	[Transient<OpenBaseHandlerUser>]
	public static partial class OpenCompositeTestShop;
}
