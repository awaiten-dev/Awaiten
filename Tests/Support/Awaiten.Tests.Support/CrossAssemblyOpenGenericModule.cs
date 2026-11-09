using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A clock an importing container registers for the internal open generic store of <see cref="OpenGenericModule" />.</summary>
public interface IOpenClock;

/// <summary>The async initializer convention of <see cref="OpenGenericModule" />.</summary>
public interface IOpenWarmup
{
	/// <summary>Initializes the instance once after it is built.</summary>
	Task WarmUpAsync();
}

/// <summary>A store per entity type, built from an internal open generic implementation.</summary>
public interface IOpenStore<T>
	where T : class
{
	/// <summary>The store the decorator wraps, or <see langword="null" /> for the undecorated store.</summary>
	IOpenStore<T>? Inner { get; }

	/// <summary>The clock the container passed to the store.</summary>
	IOpenClock Clock { get; }

	/// <summary>Whether the module's convention initialized the store.</summary>
	bool Initialized { get; }

	/// <summary>Whether the container disposed the store.</summary>
	bool Disposed { get; }
}

/// <summary>A handler per message type, fronted by an internal open generic composite.</summary>
public interface IOpenHandler<T>
{
	/// <summary>How many handlers handle the message.</summary>
	int Count { get; }
}

internal sealed class OpenStore<T>(IOpenClock clock) : IOpenStore<T>, IOpenWarmup, System.IDisposable
	where T : class
{
	public IOpenStore<T>? Inner => null;

	public IOpenClock Clock => clock;

	public bool Initialized { get; private set; }

	public bool Disposed { get; private set; }

	public Task WarmUpAsync()
	{
		Initialized = true;
		return Task.CompletedTask;
	}

	public void Dispose() => Disposed = true;
}

internal sealed class LoggingOpenStore<T>(IOpenStore<T> inner) : IOpenStore<T>
	where T : class
{
	public IOpenStore<T>? Inner => inner;

	public IOpenClock Clock => inner.Clock;

	public bool Initialized => inner.Initialized;

	public bool Disposed => inner.Disposed;
}

internal sealed class FirstOpenHandler<T> : IOpenHandler<T>
{
	public int Count => 1;
}

internal sealed class SecondOpenHandler<T> : IOpenHandler<T>
{
	public int Count => 1;
}

internal sealed class AllOpenHandlers<T>(IEnumerable<IOpenHandler<T>> handlers) : IOpenHandler<T>
{
	public int Count => handlers.Sum(handler => handler.Count);
}

/// <summary>A ledger per entry type, which the module also registers under an internal closing.</summary>
public interface IOpenLedger<T>
{
	/// <summary>The ledger the decorator wraps, or <see langword="null" /> for the undecorated ledger.</summary>
	IOpenLedger<T>? Inner { get; }
}

/// <summary>A report over the ledger of an internal entry type.</summary>
public interface IOpenLedgerReport
{
	/// <summary>The type names of the ledger's decorator chain, outermost first.</summary>
	string Chain { get; }
}

internal sealed class LedgerEntry;

internal sealed class EntryLedger : IOpenLedger<LedgerEntry>
{
	public IOpenLedger<LedgerEntry>? Inner => null;
}

internal sealed class AuditedOpenLedger<T>(IOpenLedger<T> inner) : IOpenLedger<T>
{
	public IOpenLedger<T>? Inner => inner;
}

internal sealed class OpenLedgerReport(IOpenLedger<LedgerEntry> ledger) : IOpenLedgerReport
{
	public string Chain => ledger.Inner is { } inner ? $"{ledger.GetType().Name}>{inner.GetType().Name}" : ledger.GetType().Name;
}

/// <summary>
///     A <c>[Module]</c> whose internal open generic registration, decorator and composite its generated generic
///     factories build for a container in another assembly without <c>InternalsVisibleTo</c>, which closes them per use,
///     and whose open decorator also decorates a closing under an internal type argument.
/// </summary>
[Module]
[AsyncInitializer<IOpenWarmup>(nameof(IOpenWarmup.WarmUpAsync))]
[Scoped(typeof(OpenStore<>), typeof(IOpenStore<>))]
[Decorate(typeof(LoggingOpenStore<>), typeof(IOpenStore<>))]
[Transient(typeof(FirstOpenHandler<>), typeof(IOpenHandler<>))]
[Transient(typeof(SecondOpenHandler<>), typeof(IOpenHandler<>))]
[Composite(typeof(AllOpenHandlers<>), typeof(IOpenHandler<>))]
[Singleton<EntryLedger, IOpenLedger<LedgerEntry>>]
[Decorate(typeof(AuditedOpenLedger<>), typeof(IOpenLedger<>))]
[Singleton<OpenLedgerReport, IOpenLedgerReport>]
public static partial class OpenGenericModule;

/// <summary>Reads entities of one type, a service <see cref="ISharedOpenRepository{T}" /> covers.</summary>
public interface ISharedOpenReader<T>;

/// <summary>A repository per entity type, built from an internal type the module registers both closed and open.</summary>
public interface ISharedOpenRepository<T> : ISharedOpenReader<T>;

/// <summary>The entity type <see cref="SharedOpenGenericModule" /> registers its repository closed for.</summary>
public sealed class SharedOpenEntity;

internal sealed class SharedOpenRepository<T> : ISharedOpenRepository<T>;

/// <summary>
///     A <c>[Module]</c> that registers its internal repository closed under one service and open under another, so a
///     container in another assembly builds the one instance of a closing through one of the two generated factories.
/// </summary>
[Module]
[Singleton<SharedOpenRepository<SharedOpenEntity>, ISharedOpenReader<SharedOpenEntity>>]
[Singleton(typeof(SharedOpenRepository<>), typeof(ISharedOpenRepository<>))]
public static partial class SharedOpenGenericModule;

/// <summary>The log the hooks of <see cref="HookedOpenGenericModule" /> write to, one line per hook call.</summary>
public sealed class OpenHookLog
{
	/// <summary>The hook calls in order.</summary>
	public List<string> Entries { get; } = new();
}

/// <summary>A cache per entry type, built from an internal open generic implementation whose module hooks every closing.</summary>
public interface IHookedOpenCache<T>
	where T : class;

internal sealed class HookedOpenCache<T> : IHookedOpenCache<T>
	where T : class;

/// <summary>
///     A <c>[Module]</c> whose internal open generic registration names a generic activation hook and a release hook,
///     which a container in another assembly without <c>InternalsVisibleTo</c> runs through the generic wrappers the
///     module generates beside its generic factory.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(HookedOpenCache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class HookedOpenGenericModule
{
	internal static void Activate<T>(HookedOpenCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"activated {typeof(T).Name}");

	internal static void Release(object cache, OpenHookLog log)
		=> log.Entries.Add("released");
}

/// <summary>A public cache per entry type, which a container in another assembly constructs itself.</summary>
public sealed class PublicHookedOpenCache<T> : IHookedOpenCache<T>
	where T : class;

/// <summary>
///     A <c>[Module]</c> whose public open generic registration names an internal generic activation hook and an internal
///     release hook, which a container in another assembly without <c>InternalsVisibleTo</c> runs through the generic
///     wrappers the module generates, since it cannot call them.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(PublicHookedOpenCache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class PublicHookedOpenGenericModule
{
	internal static void Activate<T>(PublicHookedOpenCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"activated {typeof(T).Name}");

	internal static void Release<T>(PublicHookedOpenCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"released {typeof(T).Name}");
}

/// <summary>A public cache per entry type whose module has an internal overload of its hook for another type.</summary>
public sealed class OverloadedHookOpenCache<T> : IHookedOpenCache<T>
	where T : class;

/// <summary>
///     A <c>[Module]</c> whose public open generic registration names public generic hooks beside an internal overload
///     for another type, which accepts no closing, so a container in another assembly without <c>InternalsVisibleTo</c>
///     calls the public hooks for every closing it builds.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(OverloadedHookOpenCache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class OverloadedHookOpenGenericModule
{
	public static void Activate<T>(OverloadedHookOpenCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"activated {typeof(T).Name}");

	internal static void Activate<T>(PublicHookedOpenCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"activated another cache of {typeof(T).Name}");

	public static void Release<T>(OverloadedHookOpenCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"released {typeof(T).Name}");
}

/// <summary>A cache per value type, built from an internal open generic implementation whose module hooks every closing.</summary>
public interface IHookedValueCache<T>;

internal sealed class HookedValueCache<T> : IHookedValueCache<T>
	where T : struct;

/// <summary>
///     A <c>[Module]</c> whose internal open generic registration of value type caches names an activation hook beside a
///     generic overload for reference types, which accepts no closing, so a container in another assembly without
///     <c>InternalsVisibleTo</c> builds every closing through the generic factory and runs the hook through its wrapper.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(HookedValueCache<>), typeof(IHookedValueCache<>), OnActivated = nameof(Activate))]
public static partial class ValueHookedOpenGenericModule
{
	internal static void Activate<T>(HookedValueCache<T> cache, OpenHookLog log)
		where T : struct
		=> log.Entries.Add($"activated {typeof(T).Name}");

	internal static void Activate<T>(IHookedValueCache<T> cache, OpenHookLog log)
		where T : class
		=> log.Entries.Add($"activated a cache of the reference type {typeof(T).Name}");
}

/// <summary>An entry of the caches of <see cref="EntryHookedOpenGenericModule" />, whose type parameter it constrains.</summary>
public class CacheEntry;

/// <summary>A cache per entry type, built from an internal open generic implementation whose module hooks every closing.</summary>
public interface IHookedEntryCache<T>;

internal sealed class HookedEntryCache<T> : IHookedEntryCache<T>
	where T : CacheEntry;

/// <summary>
///     A <c>[Module]</c> whose internal open generic registration of caches constrained to a class type names an
///     activation hook beside a generic overload for value types, which accepts no closing, so a container in another
///     assembly without <c>InternalsVisibleTo</c> builds every closing through the generic factory and runs the hook
///     through its wrapper.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(HookedEntryCache<>), typeof(IHookedEntryCache<>), OnActivated = nameof(Activate))]
public static partial class EntryHookedOpenGenericModule
{
	internal static void Activate<T>(HookedEntryCache<T> cache, OpenHookLog log)
		where T : CacheEntry
		=> log.Entries.Add($"activated {typeof(T).Name}");

	internal static void Activate<T>(IHookedEntryCache<T> cache, OpenHookLog log)
		where T : struct
		=> log.Entries.Add($"activated a cache of the value type {typeof(T).Name}");
}

internal sealed class AsyncHookedOpenCache<T> : IHookedOpenCache<T>
	where T : class;

/// <summary>
///     A <c>[Module]</c> whose internal open generic registration names an asynchronous generic activation hook and an
///     asynchronous release hook, which a container in another assembly without <c>InternalsVisibleTo</c> awaits through
///     the generic wrappers the module generates beside its generic factory.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(AsyncHookedOpenCache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class AsyncHookedOpenGenericModule
{
	internal static async Task Activate<T>(AsyncHookedOpenCache<T> cache, OpenHookLog log)
		where T : class
	{
		await Task.Yield();
		log.Entries.Add($"activated {typeof(T).Name}");
	}

	internal static ValueTask Release<T>(AsyncHookedOpenCache<T> cache, OpenHookLog log)
		where T : class
	{
		log.Entries.Add($"released {typeof(T).Name}");
		return default;
	}
}

/// <summary>A journal per entry type, built from the public <see cref="InjectedOpenJournal{T}" />.</summary>
public interface IInjectedOpenJournal<T>
{
	/// <summary>The clock the module's internal <c>[Inject]</c> property received.</summary>
	IOpenClock Clock { get; }

	/// <summary>The clock a container-side <c>[InjectProperty]</c> entry filled, or <see langword="null" />.</summary>
	IOpenClock? Audit { get; }
}

/// <summary>A public open generic journal whose internal <c>[Inject]</c> property only the module's generated factory can set.</summary>
public sealed class InjectedOpenJournal<T> : IInjectedOpenJournal<T>
{
	[Inject]
	internal IOpenClock Injected { get; set; } = null!;

	/// <inheritdoc />
	public IOpenClock Clock => Injected;

	/// <inheritdoc />
	public IOpenClock? Audit { get; set; }
}

/// <summary>
///     A <c>[Module]</c> whose public open generic journal a container in another assembly without
///     <c>InternalsVisibleTo</c> builds through the generated generic factory, because of its internal <c>[Inject]</c> property.
/// </summary>
[Module]
[Singleton(typeof(InjectedOpenJournal<>), typeof(IInjectedOpenJournal<>))]
public static partial class InjectedOpenJournalModule;
