using System;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A grinder an internal factory member of <see cref="InternalProducerModule" /> produces.</summary>
public interface IProducedGrinder
{
	/// <summary>Whether the container disposed the internal implementation.</summary>
	bool Disposed { get; }
}

/// <summary>A kettle an internal asynchronous factory member of <see cref="InternalProducerModule" /> produces.</summary>
public interface IProducedKettle;

/// <summary>A menu an internal instance member of <see cref="InternalProducerModule" /> holds.</summary>
public interface IProducedMenu;

internal sealed class ProducedGrinder : IProducedGrinder, IDisposable
{
	public bool Disposed { get; private set; }

	public void Dispose() => Disposed = true;
}

internal sealed class ProducedKettle : IProducedKettle;

internal sealed class ProducedMenu : IProducedMenu;

/// <summary>
///     A <c>[Module]</c> whose internal <c>Factory</c> and <c>Instance</c> members return internal implementations of
///     public services, which a container in another assembly without <c>InternalsVisibleTo</c> calls through the public
///     wrappers the module generates, typed as the services.
/// </summary>
[Module]
[Singleton<ProducedGrinder, IProducedGrinder>(Factory = nameof(CreateGrinder))]
[Singleton<ProducedKettle, IProducedKettle>(Factory = nameof(CreateKettleAsync))]
[Singleton<ProducedMenu, IProducedMenu>(Instance = nameof(Menu))]
public static partial class InternalProducerModule
{
	internal static readonly ProducedMenu Menu = new();

	internal static ProducedGrinder CreateGrinder() => new();

	internal static async Task<ProducedKettle> CreateKettleAsync()
	{
		await Task.Yield();
		return new ProducedKettle();
	}
}
