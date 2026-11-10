using System.Threading;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A device whose internal async initializer convention takes an internal service of the module.</summary>
public abstract class ServiceConventionDevice
{
	/// <summary>The recipe the convention received, or <see langword="null" /> before it ran.</summary>
	public object? Recipe { get; private set; }

	/// <summary>How often the convention ran on this instance.</summary>
	public int Runs { get; private set; }

	/// <summary>The token the convention received, or <see langword="null" /> before it ran.</summary>
	public CancellationToken? Token { get; private set; }

	internal Task PrepareAsync(ServiceConventionRecipe recipe, CancellationToken cancellationToken)
	{
		Recipe = recipe;
		Runs++;
		Token = cancellationToken;
		return Task.CompletedTask;
	}
}

internal sealed class ServiceConventionRecipe;

/// <summary>A brewer the container constructs itself.</summary>
public sealed class ServiceConventionBrewer : ServiceConventionDevice;

/// <summary>A grinder the module registers under one service.</summary>
public interface IServiceConventionGrinder
{
	/// <summary>The device behind the service.</summary>
	ServiceConventionDevice Device { get; }
}

internal sealed class ServiceConventionGrinder : ServiceConventionDevice, IServiceConventionGrinder
{
	public ServiceConventionDevice Device => this;
}

/// <summary>A repository the module builds from an internal open generic implementation.</summary>
public interface IServiceConventionRepository<T>
{
	/// <summary>The recipe the convention received, or <see langword="null" /> before it ran.</summary>
	object? Recipe { get; }

	/// <summary>How often the convention ran on this instance.</summary>
	int Runs { get; }
}

internal interface IServiceConventionStartable
{
	Task StartAsync(ServiceConventionRecipe recipe);
}

internal sealed class ServiceConventionRepository<T> : IServiceConventionRepository<T>, IServiceConventionStartable
{
	public object? Recipe { get; private set; }

	public int Runs { get; private set; }

	public Task StartAsync(ServiceConventionRecipe recipe)
	{
		Recipe = recipe;
		Runs++;
		return Task.CompletedTask;
	}
}

/// <summary>
///     A <c>[Module]</c> whose internal async initializer conventions take the internal recipe it registers, so an
///     importing container in another assembly without <c>InternalsVisibleTo</c> runs them through the wrappers the
///     module generates, which take the recipe through its handle.
/// </summary>
[Module]
[AsyncInitializer<ServiceConventionDevice>(nameof(ServiceConventionDevice.PrepareAsync))]
[AsyncInitializer<IServiceConventionStartable>(nameof(IServiceConventionStartable.StartAsync))]
[Singleton(typeof(ServiceConventionRepository<>), typeof(IServiceConventionRepository<>))]
[Singleton<ServiceConventionRecipe>]
[Transient<ServiceConventionBrewer>]
[Singleton<ServiceConventionGrinder, IServiceConventionGrinder>]
public static partial class ServiceConventionModule;

/// <summary>A clock the convention of <see cref="ConventionBaseContainer" /> takes.</summary>
public sealed class ConventionBaseClock;

/// <summary>A terminal whose internal async initializer convention <see cref="ConventionBaseContainer" /> declares.</summary>
public sealed class ConventionBaseTerminal
{
	/// <summary>The clock the convention received, or <see langword="null" /> before it ran.</summary>
	public ConventionBaseClock? Clock { get; private set; }

	/// <summary>How often the convention ran on this instance.</summary>
	public int Runs { get; private set; }

	internal Task StartAsync(ConventionBaseClock clock)
	{
		Clock = clock;
		Runs++;
		return Task.CompletedTask;
	}
}

/// <summary>
///     A <c>[Container]</c> whose internal async initializer convention a container that extends it in another assembly
///     without <c>InternalsVisibleTo</c> runs through the public wrapper the base generates.
/// </summary>
[Container]
[AsyncInitializer<ConventionBaseTerminal>(nameof(ConventionBaseTerminal.StartAsync))]
[Singleton<ConventionBaseClock>]
[Singleton<ConventionBaseTerminal>]
public static partial class ConventionBaseContainer;
