namespace Awaiten.Tests.Support;

/// <summary>An open generic service a consumer registers for the deferred properties of <see cref="IDeferredClosingUser" />.</summary>
public interface IClosingStore<T>;

/// <summary>The deferred <c>[Inject]</c> properties of one implementation that request a closing of <see cref="IClosingStore{T}" />.</summary>
public interface IDeferredClosingUser
{
	/// <summary>A deferred property.</summary>
	IClosingStore<string>? Names { get; }

	/// <summary>A property that is both optional and deferred.</summary>
	IClosingStore<string>? OptionalNames { get; }
}

internal sealed class SupportDeferredClosingUser : IDeferredClosingUser
{
	[Inject(Deferred = true)]
	public IClosingStore<string>? Names { get; set; }

	[Inject(Optional = true, Deferred = true)]
	public IClosingStore<string>? OptionalNames { get; set; }
}

/// <summary>
///     A <c>[Module]</c> whose internal registration requests a closing of an open generic through deferred
///     <c>[Inject]</c> properties, which a container in another assembly fills through the module's setter wrappers.
/// </summary>
[Module]
[Transient<SupportDeferredClosingUser, IDeferredClosingUser>]
public static partial class CrossAssemblyDeferredClosingModule;
