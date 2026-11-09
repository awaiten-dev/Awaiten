using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

internal sealed class FriendDeferredClosingUser : IDeferredClosingUser
{
	[Inject(Deferred = true)]
	public IClosingStore<string>? Names { get; set; }

	[Inject(Optional = true, Deferred = true)]
	public IClosingStore<string>? OptionalNames { get; set; }
}

/// <summary>
///     A <c>[Module]</c> with the registration of <see cref="CrossAssemblyDeferredClosingModule" />, in an assembly that
///     grants Awaiten.Tests <c>InternalsVisibleTo</c>, so a container there assigns the properties itself.
/// </summary>
[Module]
[Transient<FriendDeferredClosingUser, IDeferredClosingUser>]
public static partial class CrossAssemblyFriendDeferredClosingModule;
