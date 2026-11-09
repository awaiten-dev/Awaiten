using System;
using System.Collections.Generic;
using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

internal sealed class FriendVariantInjection : IVariantInjection
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
///     A <c>[Module]</c> with the registration of <see cref="CrossAssemblyVariantInjectionModule" />, in an assembly that
///     grants Awaiten.Tests <c>InternalsVisibleTo</c>, so a container there fills the properties itself.
/// </summary>
[Module]
[Transient<FriendVariantInjection, IVariantInjection>]
public static partial class CrossAssemblyFriendVariantInjectionModule;
