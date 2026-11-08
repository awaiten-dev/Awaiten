using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A deferred <c>[Inject]</c> property of a module's registration closes the container's open generic, for a
///     container in the module's assembly, one in another assembly that assigns it through the module's setter wrapper
///     (<see cref="CrossAssemblyDeferredClosingModule" />) and one in an assembly the module grants
///     <c>InternalsVisibleTo</c> (<see cref="CrossAssemblyFriendDeferredClosingModule" />).
/// </summary>
public class DeferredPropertyClosingTests
{
	[Test]
	public async Task SameAssembly_AssignsTheClosing()
	{
		using LocalDeferredClosingContainer.Root container = new();

		await ExpectClosings(container.Resolve<IDeferredClosingUser>());
	}

	[Test]
	public async Task AnotherAssembly_AssignsTheClosing()
	{
		using SupportDeferredClosingContainer.Root container = new();

		await ExpectClosings(container.Resolve<IDeferredClosingUser>());
	}

	[Test]
	public async Task FriendAssembly_AssignsTheClosing()
	{
		using FriendDeferredClosingContainer.Root container = new();

		await ExpectClosings(container.Resolve<IDeferredClosingUser>());
	}

	private static async Task ExpectClosings(IDeferredClosingUser user)
	{
		await That(user.Names).Is<ClosingStore<string>>()
			.Because("the deferred property demands the closing of the open generic like a constructor parameter");
		await That(user.OptionalNames).Is<ClosingStore<string>>();
	}
}

public sealed class ClosingStore<T> : IClosingStore<T>;

internal sealed class LocalDeferredClosingUser : IDeferredClosingUser
{
	[Inject(Deferred = true)]
	public IClosingStore<string>? Names { get; set; }

	[Inject(Optional = true, Deferred = true)]
	public IClosingStore<string>? OptionalNames { get; set; }
}

[Module]
[Transient<LocalDeferredClosingUser, IDeferredClosingUser>]
public static partial class LocalDeferredClosingModule;

[Container]
[Import(typeof(LocalDeferredClosingModule))]
[Transient(typeof(ClosingStore<>), typeof(IClosingStore<>))]
public static partial class LocalDeferredClosingContainer;

[Container]
[Import(typeof(CrossAssemblyDeferredClosingModule))]
[Transient(typeof(ClosingStore<>), typeof(IClosingStore<>))]
public static partial class SupportDeferredClosingContainer;

[Container]
[Import(typeof(CrossAssemblyFriendDeferredClosingModule))]
[Transient(typeof(ClosingStore<>), typeof(IClosingStore<>))]
public static partial class FriendDeferredClosingContainer;
