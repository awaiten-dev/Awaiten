namespace Awaiten.Tests.Support;

/// <summary>A public store that <see cref="CrossAssemblyPublicStoreModule" /> registers under an internal service only.</summary>
public sealed class PublicHandledStore : IHandledStoreAccess;

/// <summary>A service whose internal implementation takes the public store through its internal service.</summary>
public interface IHandledStoreUser
{
	/// <summary>The store the user received.</summary>
	object Store { get; }
}

internal interface IHandledStoreAccess;

internal sealed class HandledStoreUser(IHandledStoreAccess store) : IHandledStoreUser
{
	public object Store => store;
}

/// <summary>A <c>[Module]</c> that registers a public implementation under an internal service, so it builds it through a handle.</summary>
[Module]
[Singleton<PublicHandledStore, IHandledStoreAccess>]
[Singleton<HandledStoreUser, IHandledStoreUser>]
public static partial class CrossAssemblyPublicStoreModule;

/// <summary>A public service of a transient disposable that <see cref="CrossAssemblyAliasedDisposableModule" /> registers under an internal service too.</summary>
public interface IAliasedCup;

internal interface IAliasedCupSlot;

internal sealed class AliasedCup : IAliasedCup, IAliasedCupSlot, System.IDisposable
{
	public void Dispose()
	{
	}
}

/// <summary>A <c>[Module]</c> that registers a transient disposable under a public and an internal service, so a consumer reaches it through an alias.</summary>
[Module]
[Transient<AliasedCup, IAliasedCup>]
[Transient<AliasedCup, IAliasedCupSlot>]
public static partial class CrossAssemblyAliasedDisposableModule;

/// <summary>A chef whose internal implementation takes an internal store that only other modules register.</summary>
public interface IProvidedChef
{
	/// <summary>The internal store the chef received.</summary>
	object Store { get; }
}

internal interface IProvidedStore;

internal sealed class UnimportedProvidedStore : IProvidedStore;

internal sealed class ProvidedStore : IProvidedStore;

internal sealed class ProvidedChef(IProvidedStore store) : IProvidedChef
{
	public object Store => store;
}

/// <summary>A <c>[Module]</c>, first by name, that registers the internal store and declares its handle, which the container does not import.</summary>
[Module]
[Singleton<UnimportedProvidedStore, IProvidedStore>]
public static partial class CrossAssemblyProvidedAModule;

/// <summary>A <c>[Module]</c> that registers the internal store with an implementation of its own.</summary>
[Module]
[Singleton<ProvidedStore, IProvidedStore>]
public static partial class CrossAssemblyProvidedBModule;

/// <summary>A <c>[Module]</c> whose chef takes the internal store the other modules register.</summary>
[Module]
[Singleton<ProvidedChef, IProvidedChef>]
public static partial class CrossAssemblyProvidedCModule;

/// <summary>A clock that <see cref="CrossAssemblyOverriddenEagerModule" /> registers as an eager overridable default.</summary>
public interface IOverriddenClock;

/// <summary>Counts the constructions of the clock behind <see cref="IOverriddenClock" />, so a test sees whether it is built eagerly.</summary>
public static class OverriddenClockCounter
{
	private static int _constructed;

	/// <summary>How often the clock was constructed.</summary>
	public static int Constructed => System.Threading.Volatile.Read(ref _constructed);

	internal static void Increment() => System.Threading.Interlocked.Increment(ref _constructed);
}

internal interface IOverriddenTick;

internal sealed class OverriddenClock : IOverriddenClock, IOverriddenTick
{
	public OverriddenClock() => OverriddenClockCounter.Increment();
}

/// <summary>A <c>[Module]</c> whose eager registration is an overridable default beside a lazy one under an internal service.</summary>
[Module]
[Singleton<OverriddenClock, IOverriddenClock>(Eager = true, Fallback = Fallback.Silent)]
[Singleton<OverriddenClock, IOverriddenTick>]
public static partial class CrossAssemblyOverriddenEagerModule;

/// <summary>A front service of the internal grill that <see cref="CrossAssemblyGrillFrontModule" /> registers.</summary>
public interface IGrillFront;

/// <summary>A back service of the internal grill that <see cref="CrossAssemblyGrillBackModule" /> registers.</summary>
public interface IGrillBack;

internal sealed class Grill : IGrillFront, IGrillBack;

/// <summary>A <c>[Module]</c> that registers the internal grill under one public service.</summary>
[Module]
[Singleton<Grill, IGrillFront>]
public static partial class CrossAssemblyGrillFrontModule;

/// <summary>A <c>[Module]</c> that registers the same internal grill under another public service.</summary>
[Module]
[Singleton<Grill, IGrillBack>]
public static partial class CrossAssemblyGrillBackModule;

/// <summary>A clock <see cref="CrossAssemblyContextualEagerModule" /> registers as an eager overridable default for <see cref="ContextualWatch" />.</summary>
public interface IContextualClock;

/// <summary>Counts the constructions of the internal clock of <see cref="CrossAssemblyContextualEagerModule" />.</summary>
public static class ContextualClockCounter
{
	private static int _constructed;

	/// <summary>How often the clock was constructed.</summary>
	public static int Constructed => System.Threading.Volatile.Read(ref _constructed);

	internal static void Increment() => System.Threading.Interlocked.Increment(ref _constructed);
}

/// <summary>The consumer the eager clock of <see cref="CrossAssemblyContextualEagerModule" /> is injected into.</summary>
public sealed class ContextualWatch(IContextualClock clock)
{
	/// <summary>The clock the watch received.</summary>
	public IContextualClock Clock => clock;
}

internal interface IContextualTick;

internal sealed class ContextualClock : IContextualClock, IContextualTick
{
	public ContextualClock() => ContextualClockCounter.Increment();
}

/// <summary>A <c>[Module]</c> whose eager registration is an overridable default only for the watch, beside a lazy one under an internal service.</summary>
[Module]
[Singleton<ContextualClock, IContextualClock>(Eager = true, Fallback = Fallback.Silent, WhenInjectedInto = typeof(ContextualWatch))]
[Singleton<ContextualClock, IContextualTick>(Fallback = Fallback.Silent)]
[Singleton<ContextualWatch>]
public static partial class CrossAssemblyContextualEagerModule;

/// <summary>A clock <see cref="CrossAssemblyDroppedEagerModule" /> registers as an eager overridable default.</summary>
public interface IDroppedClock;

/// <summary>A timer <see cref="CrossAssemblyDroppedEagerModule" /> registers the same implementation under as an overridable default.</summary>
public interface IDroppedTimer;

/// <summary>Counts the constructions of the implementation behind <see cref="IDroppedClock" />.</summary>
public static class DroppedClockCounter
{
	private static int _constructed;

	/// <summary>How often the implementation was constructed.</summary>
	public static int Constructed => System.Threading.Volatile.Read(ref _constructed);

	internal static void Increment() => System.Threading.Interlocked.Increment(ref _constructed);
}

internal sealed class DroppedClock : IDroppedClock, IDroppedTimer
{
	public DroppedClock() => DroppedClockCounter.Increment();
}

/// <summary>A <c>[Module]</c> whose registrations of one internal implementation are all overridable defaults, one of them eager.</summary>
[Module]
[Singleton<DroppedClock, IDroppedClock>(Eager = true, Fallback = Fallback.Silent)]
[Singleton<DroppedClock, IDroppedTimer>(Fallback = Fallback.Silent)]
public static partial class CrossAssemblyDroppedEagerModule;

/// <summary>A user of an internal till that one module registers and another module decorates.</summary>
public interface IDecoratedTillUser
{
	/// <summary>The internal till the user received.</summary>
	object Till { get; }

	/// <summary>Whether the decorator around the till was disposed.</summary>
	bool DecoratorDisposed { get; }
}

internal interface IDecoratedTill;

internal sealed class DecoratedTill : IDecoratedTill;

internal sealed class LoggingTill(IDecoratedTill inner) : IDecoratedTill, System.IDisposable
{
	public IDecoratedTill Inner => inner;

	public bool Disposed { get; private set; }

	public void Dispose() => Disposed = true;
}

internal sealed class DecoratedTillUser(IDecoratedTill till) : IDecoratedTillUser
{
	public object Till => till;

	public bool DecoratorDisposed => till is LoggingTill { Disposed: true, };
}

/// <summary>A <c>[Module]</c> that registers the internal till and its user.</summary>
[Module]
[Singleton<DecoratedTill, IDecoratedTill>]
[Singleton<DecoratedTillUser, IDecoratedTillUser>]
public static partial class CrossAssemblyTillModule;

/// <summary>A <c>[Module]</c> that decorates the internal till the other module registers.</summary>
[Module]
[Decorate<LoggingTill, IDecoratedTill>]
public static partial class CrossAssemblyTillLoggingModule;

/// <summary>A clock <see cref="CrossAssemblyHandledFactoryModule" /> produces through a factory that takes an internal service.</summary>
public interface IHandledFactoryClock;

/// <summary>Counts what <see cref="CrossAssemblyHandledFactoryModule" /> builds, so a test sees whether its factory runs.</summary>
public static class HandledFactoryCounter
{
	private static int _factoryCalls;
	private static int _timeSources;

	/// <summary>How often the module's factory was called.</summary>
	public static int FactoryCalls => System.Threading.Volatile.Read(ref _factoryCalls);

	/// <summary>How often the internal time source was constructed.</summary>
	public static int TimeSources => System.Threading.Volatile.Read(ref _timeSources);

	internal static void CallFactory() => System.Threading.Interlocked.Increment(ref _factoryCalls);

	internal static void ConstructTimeSource() => System.Threading.Interlocked.Increment(ref _timeSources);
}

internal sealed class HandledTimeSource
{
	public HandledTimeSource() => HandledFactoryCounter.ConstructTimeSource();
}

internal sealed class HandledSystemClock(HandledTimeSource source) : IHandledFactoryClock
{
	public HandledTimeSource Source => source;
}

/// <summary>A <c>[Module]</c> whose factory of a public service takes an internal service, so a container in another assembly calls it through a handle.</summary>
[Module]
[Singleton<HandledTimeSource>]
[Singleton<IHandledFactoryClock>(Factory = nameof(CreateClock), SuppressDisposal = true)]
public static partial class CrossAssemblyHandledFactoryModule
{
	internal static IHandledFactoryClock CreateClock(HandledTimeSource source)
	{
		HandledFactoryCounter.CallFactory();
		return new HandledSystemClock(source);
	}
}

/// <summary>A clock that <see cref="CrossAssemblyTwoClocksModule" /> and <see cref="CrossAssemblyThirdClockModule" /> produce through several factories that take an internal service.</summary>
public interface IProducedClock
{
	/// <summary>The name of the factory that produced the clock.</summary>
	string Name { get; }
}

internal sealed class ProducedTimeSource;

internal sealed class ProducedClock(string name, ProducedTimeSource source) : IProducedClock
{
	public string Name => name;

	public ProducedTimeSource Source => source;
}

/// <summary>A <c>[Module]</c> with two factories of one public service that take an internal service, so a container in another assembly calls each through a handle of its own.</summary>
[Module]
[Singleton<ProducedTimeSource>]
[Singleton<IProducedClock>(Factory = nameof(CreateFirst))]
[Singleton<IProducedClock>(Factory = nameof(CreateSecond))]
public static partial class CrossAssemblyTwoClocksModule
{
	internal static IProducedClock CreateFirst(ProducedTimeSource source) => new ProducedClock("first", source);

	internal static IProducedClock CreateSecond(ProducedTimeSource source) => new ProducedClock("second", source);
}

/// <summary>A <c>[Module]</c> with another factory of the service of <see cref="CrossAssemblyTwoClocksModule" />, which takes the internal service that module registers.</summary>
[Module]
[Singleton<IProducedClock>(Factory = nameof(CreateThird))]
public static partial class CrossAssemblyThirdClockModule
{
	internal static IProducedClock CreateThird(ProducedTimeSource source) => new ProducedClock("third", source);
}

/// <summary>A clock that <see cref="CrossAssemblyImportedProducerAModule" /> and <see cref="CrossAssemblyImportedProducerBModule" /> produce through factories that take an internal service, and that <see cref="CrossAssemblyImportedProducerCModule" /> constructs.</summary>
public sealed class ImportedProducerClock : System.IDisposable
{
	internal ImportedProducerClock(string name, ImportedProducerTimeSource source)
	{
		Name = name;
		Source = source;
	}

	/// <summary>The name of the module that produced the clock.</summary>
	public string Name { get; }

	internal ImportedProducerTimeSource Source { get; }

	/// <inheritdoc />
	public void Dispose()
	{
	}
}

internal sealed class ImportedProducerTimeSource;

/// <summary>A <c>[Module]</c> with a factory of the clock that takes an internal service.</summary>
[Module]
[Singleton<ImportedProducerTimeSource>]
[Singleton<ImportedProducerClock>(Factory = nameof(CreateA))]
public static partial class CrossAssemblyImportedProducerAModule
{
	internal static ImportedProducerClock CreateA(ImportedProducerTimeSource source) => new("a", source);
}

/// <summary>A <c>[Module]</c> with another factory of the clock, which takes the internal service <see cref="CrossAssemblyImportedProducerAModule" /> registers.</summary>
[Module]
[Singleton<ImportedProducerClock>(Factory = nameof(CreateB))]
public static partial class CrossAssemblyImportedProducerBModule
{
	internal static ImportedProducerClock CreateB(ImportedProducerTimeSource source) => new("b", source);
}

/// <summary>A <c>[Module]</c> that constructs the clock, so a container importing it keeps one instance of the clock.</summary>
[Module]
[Singleton<ImportedProducerClock>]
public static partial class CrossAssemblyImportedProducerCModule;
