using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     The <c>[Inject]</c> properties of a module's registration honor generic variance like constructor parameters,
///     for a container in the module's assembly, one in another assembly that builds the implementation through the
///     module's generated factory (<see cref="CrossAssemblyVariantInjectionModule" />) and one in an assembly the module
///     grants <c>InternalsVisibleTo</c> (<see cref="CrossAssemblyFriendVariantInjectionModule" />).
/// </summary>
public class InjectedPropertyVarianceTests
{
	[Test]
	public async Task SameAssembly_FillsEveryPropertyWithTheVariantRegistration()
	{
		using LocalVariantInjectionContainer.Root container = new();

		await ExpectVariantHandler(container.Resolve<IVariantInjection>());
	}

	[Test]
	public async Task AnotherAssembly_FillsEveryPropertyWithTheVariantRegistration()
	{
		using SupportVariantInjectionContainer.Root container = new();

		await ExpectVariantHandler(container.Resolve<IVariantInjection>());
	}

	[Test]
	public async Task FriendAssembly_FillsEveryPropertyWithTheVariantRegistration()
	{
		using FriendVariantInjectionContainer.Root container = new();

		await ExpectVariantHandler(container.Resolve<IVariantInjection>());
	}

	private static async Task ExpectVariantHandler(IVariantInjection injection)
	{
		await That(injection.Handler).Is<VariantEventHandler>()
			.Because("a handler of the base event satisfies the request for a handler of the specific event through contravariance");
		await That(injection.HandlerFactory!()).Is<VariantEventHandler>();
		await That(injection.OptionalHandler).Is<VariantEventHandler>()
			.Because("a variant registration is not a missing one, so the optional property is filled");
		await That(injection.DeferredHandler).Is<VariantEventHandler>();
		await That(injection.Handlers!.ToList()).HasSingle().Which.Is<VariantEventHandler>();
		await That(injection.DeferredHandlers!.ToList()).HasSingle().Which.Is<VariantEventHandler>()
			.Because("a deferred collection property gains the variant registration like a collection parameter");
	}
}

public sealed class VariantEventHandler : IVariantHandler<VariantEvent>;

internal sealed class LocalVariantInjection : IVariantInjection
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

[Module]
[Transient<LocalVariantInjection, IVariantInjection>]
public static partial class LocalVariantInjectionModule;

[Container]
[Import(typeof(LocalVariantInjectionModule))]
[Transient<VariantEventHandler, IVariantHandler<VariantEvent>>]
public static partial class LocalVariantInjectionContainer;

[Container]
[Import(typeof(CrossAssemblyVariantInjectionModule))]
[Transient<VariantEventHandler, IVariantHandler<VariantEvent>>]
public static partial class SupportVariantInjectionContainer;

[Container]
[Import(typeof(CrossAssemblyFriendVariantInjectionModule))]
[Transient<VariantEventHandler, IVariantHandler<VariantEvent>>]
public static partial class FriendVariantInjectionContainer;
