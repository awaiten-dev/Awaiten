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

internal sealed class OtherGeneralVariantCollectionHandler : IVariantCollectionHandler<VariantCollectionEvent>
{
	public string Name => "other general";
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
///     while the module registers two handlers of the general event, which the collection holds in registration order,
///     through variance in a container of this assembly and through the handle of the general handler in one of another
///     assembly.
/// </summary>
[Module]
[Singleton<VariantCollectionBrew, IVariantCollectionBrew>]
[Singleton<GeneralVariantCollectionHandler, IVariantCollectionHandler<VariantCollectionEvent>>]
[Transient<OtherGeneralVariantCollectionHandler, IVariantCollectionHandler<VariantCollectionEvent>>]
[Decorate<HandlingVariantCollectionBrew, IVariantCollectionBrew>]
public static partial class CrossAssemblyVariantCollectionModule;

/// <summary>A reader that reports the handlers it and its decorator received.</summary>
public interface IVariantCollectionReader
{
	/// <summary>The names of the handlers the reader and then its decorator received, in order.</summary>
	IReadOnlyList<string> Handlers { get; }
}

/// <summary>A public contravariant handler.</summary>
public interface IPublicVariantCollectionHandler<in T>
{
	/// <summary>The name of the handler.</summary>
	string Name { get; }
}

internal interface ISecretVariantCollectionEvent;

/// <summary>A public event that implements an internal one.</summary>
public sealed class PublicVariantCollectionEvent : ISecretVariantCollectionEvent;

/// <summary>A public handler of the public event.</summary>
public sealed class FirstPublicVariantCollectionHandler : IPublicVariantCollectionHandler<PublicVariantCollectionEvent>
{
	/// <inheritdoc />
	public string Name => "first";
}

/// <summary>Another public handler of the public event.</summary>
public sealed class SecondPublicVariantCollectionHandler : IPublicVariantCollectionHandler<PublicVariantCollectionEvent>
{
	/// <inheritdoc />
	public string Name => "second";
}

internal sealed class SecretVariantCollectionHandler : IPublicVariantCollectionHandler<ISecretVariantCollectionEvent>
{
	public string Name => "secret";
}

internal sealed class VariantCollectionReader(IEnumerable<IPublicVariantCollectionHandler<PublicVariantCollectionEvent>> handlers) : IVariantCollectionReader
{
	public IReadOnlyList<string> Handlers { get; } = [..handlers.Select(handler => handler.Name),];
}

internal sealed class HandlingVariantCollectionReader(IVariantCollectionReader inner, IReadOnlyList<IPublicVariantCollectionHandler<PublicVariantCollectionEvent>> handlers) : IVariantCollectionReader
{
	public IReadOnlyList<string> Handlers { get; } = [..inner.Handlers, ..handlers.Select(handler => handler.Name),];
}

/// <summary>
///     A <c>[Module]</c> whose registration and decorator take a collection of a public contravariant handler of a public
///     event, while the module registers two public handlers of that event and, between them, one of an internal event it
///     implements. The collection holds the exact handlers in registration order and then the internal one through
///     variance in a container of this assembly, and through the public collection and the handle of the internal handler
///     in one of another assembly.
/// </summary>
[Module]
[Singleton<FirstPublicVariantCollectionHandler, IPublicVariantCollectionHandler<PublicVariantCollectionEvent>>]
[Singleton<SecretVariantCollectionHandler, IPublicVariantCollectionHandler<ISecretVariantCollectionEvent>>]
[Transient<SecondPublicVariantCollectionHandler, IPublicVariantCollectionHandler<PublicVariantCollectionEvent>>]
[Singleton<VariantCollectionReader, IVariantCollectionReader>]
[Decorate<HandlingVariantCollectionReader, IVariantCollectionReader>]
public static partial class CrossAssemblyPublicVariantCollectionModule;
