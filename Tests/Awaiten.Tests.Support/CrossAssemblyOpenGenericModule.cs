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
