using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A public implementation with internal <c>[Inject]</c> properties registered directly on a container, without a
///     module: a container in its own assembly and one in an assembly granted <c>InternalsVisibleTo</c>
///     (<see cref="FriendShapeUser" />) set the properties. One in an assembly without the grant cannot see them and
///     reports AWT244 instead of building the implementation without them.
/// </summary>
public class DirectlyRegisteredInjectedPropertyTests
{
	[Test]
	public async Task SameAssembly_SetsTheInternalProperties()
	{
		using DirectLocalShapeUserContainer.Root container = new();

		await ExpectKettles(container.Resolve<IInjectedShapeUser>(), container.Resolve<IInjectedShapeKettle>());
	}

	[Test]
	public async Task FriendAssembly_SetsTheInternalProperties()
	{
		using DirectFriendShapeUserContainer.Root container = new();

		await ExpectKettles(container.Resolve<IInjectedShapeUser>(), container.Resolve<IInjectedShapeKettle>());
	}

	private static async Task ExpectKettles(IInjectedShapeUser user, IInjectedShapeKettle kettle)
	{
		await That(user.InternalKettle).IsSameAs(kettle)
			.Because("a container that sees the internal property sets it when it constructs the implementation itself");
		await That(user.SetterKettle).IsSameAs(kettle);
		await That(user.HasHiddenKettle).IsTrue();
	}
}

[Container]
[Singleton<LocalShapeKettle, IInjectedShapeKettle>]
[Singleton<LocalHiddenKettle, ILocalHiddenKettle>]
[Transient<LocalShapeUser, IInjectedShapeUser>]
public static partial class DirectLocalShapeUserContainer;

[Container]
[Singleton<FriendShapeKettle, IInjectedShapeKettle>]
[Singleton<FriendHiddenKettle, IFriendHiddenKettle>]
[Transient<FriendShapeUser, IInjectedShapeUser>]
public static partial class DirectFriendShapeUserContainer;
