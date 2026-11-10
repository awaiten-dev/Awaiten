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

/// <summary>
///     A <c>[Module]</c> whose internal async initializer convention takes the internal recipe it registers, so an
///     importing container in another assembly without <c>InternalsVisibleTo</c> runs it through the wrapper the module
///     generates, which takes the recipe through its handle.
/// </summary>
[Module]
[AsyncInitializer<ServiceConventionDevice>(nameof(ServiceConventionDevice.PrepareAsync))]
[Singleton<ServiceConventionRecipe>]
[Transient<ServiceConventionBrewer>]
[Singleton<ServiceConventionGrinder, IServiceConventionGrinder>]
public static partial class ServiceConventionModule;
