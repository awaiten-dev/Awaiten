namespace Awaiten.Tests.Support;

/// <summary>The dependency every implementation of <see cref="ContextualBindingModule" /> takes.</summary>
public interface IBrewFlavor;

/// <summary>The flavor <see cref="ContextualBindingModule" /> registers for every consumer.</summary>
public sealed class HouseFlavor : IBrewFlavor;

/// <summary>What an implementation of <see cref="ContextualBindingModule" /> received for its flavor.</summary>
public interface IFlavored
{
	/// <summary>The flavor its constructor received.</summary>
	IBrewFlavor Flavor { get; }

	/// <summary>The flavor its deferred <c>[Inject]</c> property received.</summary>
	IBrewFlavor? Late { get; }

	/// <summary>The flavor the module's <c>OnActivated</c> hook received.</summary>
	IBrewFlavor? Seasoned { get; }
}

/// <summary>The public service of <see cref="HandledFlavored" />.</summary>
public interface IHandledFlavored : IFlavored;

internal interface IInternalFlavored;

/// <summary>
///     A public implementation <see cref="ContextualBindingModule" /> also registers under an internal service, so a
///     container in another assembly builds it through the module's generated instance handle.
/// </summary>
public sealed class HandledFlavored(IBrewFlavor flavor) : IHandledFlavored, IInternalFlavored
{
	/// <inheritdoc />
	public IBrewFlavor Flavor => flavor;

	/// <inheritdoc />
	[Inject(Deferred = true)]
	public IBrewFlavor? Late { get; set; }

	/// <inheritdoc />
	public IBrewFlavor? Seasoned { get; internal set; }
}

/// <summary>The public service of <see cref="FactoryFlavored" />.</summary>
public interface IFactoryFlavored : IFlavored;

/// <summary>
///     A public implementation with an internal constructor, which a container in another assembly builds through the
///     factory <see cref="ContextualBindingModule" /> generates.
/// </summary>
public sealed class FactoryFlavored : IFactoryFlavored
{
	internal FactoryFlavored(IBrewFlavor flavor) => Flavor = flavor;

	/// <inheritdoc />
	public IBrewFlavor Flavor { get; }

	/// <inheritdoc />
	[Inject(Deferred = true)]
	public IBrewFlavor? Late { get; set; }

	/// <inheritdoc />
	public IBrewFlavor? Seasoned { get; internal set; }
}

/// <summary>The marker the scan of <see cref="ContextualBindingModule" /> matches.</summary>
public interface IScannedFlavored
{
	/// <summary>The flavor its constructor received.</summary>
	IBrewFlavor Flavor { get; }
}

/// <summary>The match of the scan of <see cref="ContextualBindingModule" />, built through the module's generated scan factory.</summary>
public sealed class ScannedFlavored(IBrewFlavor flavor) : IScannedFlavored
{
	/// <inheritdoc />
	public IBrewFlavor Flavor => flavor;
}

/// <summary>A consumer of <see cref="ContextualBindingModule" /> that no contextual binding names.</summary>
public sealed class PlainTaster(IBrewFlavor flavor)
{
	/// <summary>The flavor its constructor received.</summary>
	public IBrewFlavor Flavor => flavor;
}

/// <summary>
///     A <c>[Module]</c> that builds its implementations through generated members, for a container in another assembly
///     whose <c>WhenInjectedInto</c> bindings name them.
/// </summary>
[Module]
[Singleton<HouseFlavor, IBrewFlavor>]
[Singleton<HandledFlavored, IHandledFlavored>(OnActivated = nameof(SeasonHandled))]
[Singleton<HandledFlavored, IInternalFlavored>]
[Singleton<FactoryFlavored, IFactoryFlavored>(OnActivated = nameof(SeasonFactory))]
[Singleton<PlainTaster>]
[Scan<IScannedFlavored>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
public static partial class ContextualBindingModule
{
	internal static void SeasonHandled(HandledFlavored flavored, IBrewFlavor flavor) => flavored.Seasoned = flavor;

	internal static void SeasonFactory(FactoryFlavored flavored, IBrewFlavor flavor) => flavored.Seasoned = flavor;
}
