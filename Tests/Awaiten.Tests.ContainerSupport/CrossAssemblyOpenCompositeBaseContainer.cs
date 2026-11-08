using System.Collections.Generic;
using System.Linq;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>A handler the base registers and fronts with an open composite, without requesting any closing itself.</summary>
public interface IOpenBaseHandler<T>
{
	/// <summary>How many handlers the instance stands for.</summary>
	int Count { get; }
}

/// <summary>The open registration of <see cref="IOpenBaseHandler{T}" />.</summary>
public sealed class OpenBaseHandler<T> : IOpenBaseHandler<T>
{
	/// <inheritdoc />
	public int Count => 1;
}

/// <summary>The open composite of <see cref="IOpenBaseHandler{T}" />.</summary>
public sealed class AllOpenBaseHandlers<T> : IOpenBaseHandler<T>
{
	private readonly IEnumerable<IOpenBaseHandler<T>> _handlers;

	/// <summary>Fronts every registered handler of <typeparamref name="T" />.</summary>
	public AllOpenBaseHandlers(IEnumerable<IOpenBaseHandler<T>> handlers) => _handlers = handlers;

	/// <inheritdoc />
	public int Count => _handlers.Sum(handler => handler.Count);
}

/// <summary>A base container whose open composite fronts no closing until a container extending it requests one.</summary>
[Container]
[Transient(typeof(OpenBaseHandler<>), typeof(IOpenBaseHandler<>))]
[Composite(typeof(AllOpenBaseHandlers<>), typeof(IOpenBaseHandler<>))]
public static partial class CrossAssemblyOpenCompositeBaseContainer;
