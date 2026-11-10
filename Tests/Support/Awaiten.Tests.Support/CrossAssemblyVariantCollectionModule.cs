using System.Collections.Generic;
using System.Linq;

namespace Awaiten.Tests.Support;

/// <summary>A brew whose decorator reports the handlers it received.</summary>
public interface IVariantCollectionBrew
{
	/// <summary>The names of the handlers the outermost link received, in order.</summary>
	IReadOnlyList<string> Handlers { get; }
}

internal class VariantCollectionEvent;

internal sealed class SpecificVariantCollectionEvent : VariantCollectionEvent;

internal interface IVariantCollectionHandler<in T>
{
	string Name { get; }
}

internal sealed class GeneralVariantCollectionHandler : IVariantCollectionHandler<VariantCollectionEvent>
{
	public string Name => "general";
}

internal sealed class VariantCollectionBrew : IVariantCollectionBrew
{
	public IReadOnlyList<string> Handlers => [];
}

internal sealed class HandlingVariantCollectionBrew(IVariantCollectionBrew inner, IEnumerable<IVariantCollectionHandler<SpecificVariantCollectionEvent>> handlers) : IVariantCollectionBrew
{
	public IReadOnlyList<string> Handlers { get; } = [..inner.Handlers, ..handlers.Select(handler => handler.Name),];
}

/// <summary>
///     A <c>[Module]</c> whose decorator takes a collection of an internal contravariant handler of a specific event,
///     while the module registers a handler of the general event, which the collection holds through variance in a
///     container of this assembly and, through the handle of the general handler, in one of another assembly.
/// </summary>
[Module]
[Singleton<VariantCollectionBrew, IVariantCollectionBrew>]
[Singleton<GeneralVariantCollectionHandler, IVariantCollectionHandler<VariantCollectionEvent>>]
[Decorate<HandlingVariantCollectionBrew, IVariantCollectionBrew>]
public static partial class CrossAssemblyVariantCollectionModule;
