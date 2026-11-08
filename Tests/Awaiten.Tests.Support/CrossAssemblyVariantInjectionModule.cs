using System;
using System.Collections.Generic;

namespace Awaiten.Tests.Support;

/// <summary>The event a consumer registers a handler of for the <c>[Inject]</c> properties of <see cref="IVariantInjection" />.</summary>
public class VariantEvent;

/// <summary>The more specific event the <c>[Inject]</c> properties of <see cref="IVariantInjection" /> request a handler of.</summary>
public sealed class SpecificVariantEvent : VariantEvent;

/// <summary>Another specific event the deferred collection of <see cref="IVariantInjection" /> requests handlers of.</summary>
public sealed class OtherVariantEvent : VariantEvent;

/// <summary>A contravariant handler, so a handler of <see cref="VariantEvent" /> handles a <see cref="SpecificVariantEvent" />.</summary>
public interface IVariantHandler<in T>;

/// <summary>The <c>[Inject]</c> property shapes of one implementation that request a handler of a more specific event than the registered one.</summary>
public interface IVariantInjection
{
	/// <summary>A direct property.</summary>
	IVariantHandler<SpecificVariantEvent>? Handler { get; }

	/// <summary>A <see cref="Func{TResult}" /> property.</summary>
	Func<IVariantHandler<SpecificVariantEvent>>? HandlerFactory { get; }

	/// <summary>An optional property.</summary>
	IVariantHandler<SpecificVariantEvent>? OptionalHandler { get; }

	/// <summary>A property that is both optional and deferred.</summary>
	IVariantHandler<SpecificVariantEvent>? DeferredHandler { get; }

	/// <summary>A collection property.</summary>
	IEnumerable<IVariantHandler<SpecificVariantEvent>>? Handlers { get; }

	/// <summary>A deferred collection property, of another event than <see cref="Handlers" /> so it is requested on its own.</summary>
	IEnumerable<IVariantHandler<OtherVariantEvent>>? DeferredHandlers { get; }
}

internal sealed class SupportVariantInjection : IVariantInjection
{
	[Inject]
	public IVariantHandler<SpecificVariantEvent>? Handler { get; set; }

	[Inject]
	public Func<IVariantHandler<SpecificVariantEvent>>? HandlerFactory { get; set; }

	[Inject(Optional = true)]
	public IVariantHandler<SpecificVariantEvent>? OptionalHandler { get; set; }

	[Inject(Optional = true, Deferred = true)]
	public IVariantHandler<SpecificVariantEvent>? DeferredHandler { get; set; }

	[Inject]
	public IEnumerable<IVariantHandler<SpecificVariantEvent>>? Handlers { get; set; }

	[Inject(Deferred = true)]
	public IEnumerable<IVariantHandler<OtherVariantEvent>>? DeferredHandlers { get; set; }
}

/// <summary>
///     A <c>[Module]</c> whose internal registration requests a handler of <see cref="SpecificVariantEvent" /> through
///     every <c>[Inject]</c> property shape, which a container in another assembly fills through the module's
///     generated factory.
/// </summary>
[Module]
[Transient<SupportVariantInjection, IVariantInjection>]
public static partial class CrossAssemblyVariantInjectionModule;
