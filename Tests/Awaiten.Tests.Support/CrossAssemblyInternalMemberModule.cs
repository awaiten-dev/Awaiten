using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A plugin the module constructs and whose internal hooks record each call.</summary>
public sealed class InternalHookedPlugin
{
	/// <summary>The hooks that ran on this instance, in order.</summary>
	public List<string> Steps { get; } = new();

	/// <summary>The internal store the activation hook received, or <see langword="null" /> before it ran.</summary>
	public object? Store { get; internal set; }
}

/// <summary>A service the module registers and activates through an internal asynchronous hook.</summary>
public interface IInternalHookedRouter
{
	/// <summary>The token the activation hook received, or <see langword="null" /> before it ran.</summary>
	CancellationToken? Token { get; }
}

/// <summary>The router behind <see cref="IInternalHookedRouter" />.</summary>
public sealed class InternalHookedRouter : IInternalHookedRouter
{
	/// <inheritdoc />
	public CancellationToken? Token { get; internal set; }
}

/// <summary>A grinder the module produces through an internal factory that takes an internal service.</summary>
public interface IInternalStoreGrinder
{
	/// <summary>The internal store the factory received.</summary>
	object Store { get; }
}

internal sealed class InternalMemberStore;

internal sealed class InternalStoreGrinder(InternalMemberStore store) : IInternalStoreGrinder
{
	public object Store { get; } = store;
}

/// <summary>
///     A <c>[Module]</c> with an internal <c>Factory</c> that takes an internal service and internal lifecycle hooks on
///     public implementations, so an importing container in another assembly without <c>InternalsVisibleTo</c> builds the
///     factory's product through a handle and runs the hooks through the public wrappers the module generates.
/// </summary>
[Module]
[Singleton<InternalMemberStore>]
[Singleton<IInternalStoreGrinder>(Factory = nameof(CreateGrinder))]
[Transient<InternalHookedPlugin>(OnActivated = nameof(Activate), OnRelease = nameof(Release))]
[Singleton<InternalHookedRouter, IInternalHookedRouter>(OnActivated = nameof(ConnectAsync))]
public static partial class InternalMemberModule
{
	internal static IInternalStoreGrinder CreateGrinder(InternalMemberStore store) => new InternalStoreGrinder(store);

	internal static void Activate(InternalHookedPlugin plugin, InternalMemberStore store)
	{
		plugin.Steps.Add("activated");
		plugin.Store = store;
	}

	internal static void Release(InternalHookedPlugin plugin) => plugin.Steps.Add("released");

	internal static async Task ConnectAsync(InternalHookedRouter router, CancellationToken cancellationToken)
	{
		await Task.Yield();
		router.Token = cancellationToken;
	}
}
