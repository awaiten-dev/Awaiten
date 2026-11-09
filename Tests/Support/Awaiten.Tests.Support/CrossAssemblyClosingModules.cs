using System;

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
