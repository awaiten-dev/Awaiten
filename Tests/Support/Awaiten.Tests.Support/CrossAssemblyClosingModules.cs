using System;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>What a test reads off any closing of <see cref="IClosingLog{T}" />.</summary>
public interface IClosingLog
{
	/// <summary>The type the log is closed over.</summary>
	Type Category { get; }

	/// <summary>Whether a container disposed the log.</summary>
	bool Disposed { get; }
}

/// <summary>A generic log, like <c>ILogger&lt;TCategoryName&gt;</c>, that internal implementations take closed over themselves.</summary>
#pragma warning disable S2326 // T only names the closing, like ILogger<TCategoryName>.
public interface IClosingLog<T> : IClosingLog;
#pragma warning restore S2326

/// <summary>The implementation <see cref="CrossAssemblyClosingModule" /> registers for every closing of <see cref="IClosingLog{T}" />.</summary>
public sealed class ClosingLog<T> : IClosingLog<T>, IDisposable
{
	/// <inheritdoc />
	public Type Category => typeof(T);

	/// <inheritdoc />
	public bool Disposed { get; private set; }

	/// <inheritdoc />
	public void Dispose() => Disposed = true;
}

/// <summary>A brew whose implementation is internal and takes a log closed over itself.</summary>
public interface IClosingBrew
{
	/// <summary>The log the brew was constructed with.</summary>
	IClosingLog Log { get; }
}

/// <summary>A cup whose implementation is internal and takes the log of the brew.</summary>
public interface IClosingCup
{
	/// <summary>The log the cup was constructed with.</summary>
	IClosingLog Log { get; }
}

internal sealed class ClosingBrew(IClosingLog<ClosingBrew> log) : IClosingBrew
{
	public IClosingLog Log { get; } = log;
}

internal sealed class ClosingCup(IClosingLog<ClosingBrew> log) : IClosingCup
{
	public IClosingLog Log { get; } = log;
}

/// <summary>
///     A <c>[Module]</c> that registers <see cref="IClosingLog{T}" /> as an open generic and internal implementations that
///     take it closed over an internal type. A container in another assembly cannot name the closing, so it builds it
///     through the factory the module generated from its own registration, unless it imports the definition from its host.
/// </summary>
[Module]
[Singleton(typeof(ClosingLog<>), typeof(IClosingLog<>))]
[Transient<ClosingBrew, IClosingBrew>]
[Transient<ClosingCup, IClosingCup>]
public static partial class CrossAssemblyClosingModule;

/// <summary>A tea whose implementation is internal and takes a log closed over itself, which the host provides.</summary>
public interface IImportedClosingTea
{
	/// <summary>The log the tea was constructed with.</summary>
	IClosingLog Log { get; }
}

internal sealed class ImportedClosingTea(IClosingLog<ImportedClosingTea> log) : IImportedClosingTea
{
	public IClosingLog Log { get; } = log;
}

/// <summary>A <c>[Module]</c> whose internal implementation takes a closing of <see cref="IClosingLog{T}" /> it does not register, so a container imports the definition from its host.</summary>
[Module]
[Transient<ImportedClosingTea, IImportedClosingTea>]
public static partial class CrossAssemblyImportedClosingModule;

/// <summary>An espresso whose implementation is internal and whose hook, async initializer and injected property take a log closed over it.</summary>
public interface IClosingEspresso
{
	/// <summary>The log the <c>OnActivated</c> hook received.</summary>
	IClosingLog? Activated { get; }

	/// <summary>The log the async initializer received.</summary>
	IClosingLog? WarmedUp { get; }

	/// <summary>The log injected into an <c>[Inject]</c> property.</summary>
	IClosingLog? Injected { get; }
}

internal sealed class ClosingEspresso : IClosingEspresso
{
	[Inject]
	public IClosingLog<ClosingEspresso> Journal { get; set; } = null!;

	public IClosingLog? Injected => Journal;

	public IClosingLog? Activated { get; set; }

	public IClosingLog? WarmedUp { get; private set; }

	internal Task WarmUpAsync(IClosingLog<ClosingEspresso> log)
	{
		WarmedUp = log;
		return Task.CompletedTask;
	}
}

/// <summary>The decorator of <see cref="IClosingEspresso" /> that a predicate taking the log closed over the espresso applies.</summary>
public sealed class DecoratedClosingEspresso(IClosingEspresso inner) : IClosingEspresso
{
	/// <summary>The decorated espresso.</summary>
	public IClosingEspresso Inner { get; } = inner;

	/// <inheritdoc />
	public IClosingLog? Activated => Inner.Activated;

	/// <inheritdoc />
	public IClosingLog? WarmedUp => Inner.WarmedUp;

	/// <inheritdoc />
	public IClosingLog? Injected => Inner.Injected;
}

/// <summary>
///     A <c>[Module]</c> whose hook, decorator predicate, async initializer convention and injected property take a closing of
///     <see cref="IClosingLog{T}" /> over an internal type, which a container in another assembly passes through the
///     handle the module generated.
/// </summary>
[Module]
[Singleton(typeof(ClosingLog<>), typeof(IClosingLog<>))]
[Transient<ClosingEspresso, IClosingEspresso>(OnActivated = nameof(Activate))]
[Decorate<DecoratedClosingEspresso, IClosingEspresso>(When = nameof(Decorates))]
[AsyncInitializer<ClosingEspresso>(nameof(ClosingEspresso.WarmUpAsync))]
public static partial class CrossAssemblyClosingMembersModule
{
	internal static void Activate(ClosingEspresso espresso, IClosingLog<ClosingEspresso> log) => espresso.Activated = log;

	internal static bool Decorates(IClosingLog<ClosingEspresso> log) => log.Category == typeof(ClosingEspresso);
}
