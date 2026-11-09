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

/// <summary>A grinder an internal factory member of <see cref="WarmedProducerModule" /> produces, which its convention warms up.</summary>
public interface IWarmedGrinder
{
	/// <summary>Whether the convention warmed the internal implementation up.</summary>
	bool WarmedUp { get; }
}

/// <summary>Something <see cref="WarmedProducerModule" /> warms up by its async initializer convention.</summary>
public interface IProducedWarmUp
{
	/// <summary>Warms the instance up.</summary>
	Task WarmUpAsync();
}

internal sealed class WarmedGrinder : IWarmedGrinder, IProducedWarmUp
{
	public bool WarmedUp { get; private set; }

	public async Task WarmUpAsync()
	{
		await Task.Yield();
		WarmedUp = true;
	}
}

/// <summary>
///     A <c>[Module]</c> whose internal <c>Factory</c> member returns an internal implementation that a convention of
///     the module initializes, though the public service the wrapper returns does not implement the convention type.
/// </summary>
[Module]
[AsyncInitializer<IProducedWarmUp>(nameof(IProducedWarmUp.WarmUpAsync))]
[Singleton<WarmedGrinder, IWarmedGrinder>(Factory = nameof(CreateGrinder))]
public static partial class WarmedProducerModule
{
	internal static WarmedGrinder CreateGrinder() => new();
}
