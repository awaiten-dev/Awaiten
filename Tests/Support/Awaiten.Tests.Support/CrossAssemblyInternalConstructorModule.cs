using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>The service <see cref="CrossAssemblyInternalConstructorModule" /> registers <see cref="InternalConstructorKettle" /> under.</summary>
public interface IInternalConstructorKettle;

/// <summary>A public kettle with an internal constructor, which another assembly builds only through the module's generated factory.</summary>
public sealed class InternalConstructorKettle : IInternalConstructorKettle
{
	internal InternalConstructorKettle() { }
}

/// <summary>A repository with an internal constructor, which the module registers as an open generic.</summary>
public sealed class InternalConstructorRepository<T>
{
	internal InternalConstructorRepository() { }
}

/// <summary>The type of the async initializer convention of <see cref="CrossAssemblyInternalConstructorModule" />.</summary>
public interface IModuleWarmable
{
	/// <summary>Warms the instance up.</summary>
	Task WarmAsync(CancellationToken cancellationToken);
}

/// <summary>The type of an async initializer convention a container in another assembly declares.</summary>
public interface IContainerWarmable
{
	/// <summary>Warms the instance up.</summary>
	Task ContainerWarmAsync(CancellationToken cancellationToken);
}

/// <summary>A brewer with an internal constructor that records which convention initialized it.</summary>
public sealed class InternalConstructorBrewer : IModuleWarmable, IContainerWarmable
{
	internal InternalConstructorBrewer() { }

	/// <summary>The conventions that ran on this instance, in order.</summary>
	public List<string> Steps { get; } = new();

	/// <inheritdoc />
	public Task WarmAsync(CancellationToken cancellationToken)
	{
		Steps.Add("module");
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task ContainerWarmAsync(CancellationToken cancellationToken)
	{
		Steps.Add("container");
		return Task.CompletedTask;
	}
}

/// <summary>
///     A <c>[Module]</c> that builds public implementations with internal constructors through its generated factories,
///     which a container in another assembly calls for each registration of them, also one of a module of another
///     assembly or a closed registration of a closing of its open generic registration.
/// </summary>
[Module]
[AsyncInitializer<IModuleWarmable>(nameof(IModuleWarmable.WarmAsync))]
[Singleton<InternalConstructorKettle, IInternalConstructorKettle>]
[Singleton<InternalConstructorBrewer>]
[Singleton(typeof(InternalConstructorRepository<>))]
public static partial class CrossAssemblyInternalConstructorModule;

/// <summary>A container in the module's assembly that another assembly extends, inheriting its registration of <see cref="InternalConstructorKettle" />.</summary>
[Container]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
[Singleton<InternalConstructorKettle>]
public static partial class CrossAssemblyInternalConstructorBaseContainer;
