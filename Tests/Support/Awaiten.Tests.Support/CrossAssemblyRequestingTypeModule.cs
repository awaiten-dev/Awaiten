using System;

namespace Awaiten.Tests.Support;

/// <summary>A log named after the type that requested it, built by a factory with a <c>[RequestingType]</c> parameter.</summary>
public sealed class RequestingTypeLog(string category)
{
	/// <summary>The name of the requesting type, or <c>&lt;root&gt;</c> when nothing requested the log.</summary>
	public string Category { get; } = category;
}

/// <summary>A service that shows the log its implementation was constructed with.</summary>
public interface IRequestingTypeLogged
{
	/// <summary>The log the implementation received.</summary>
	RequestingTypeLog Log { get; }
}

/// <summary>A kettle whose implementation stays internal.</summary>
public interface IRequestingTypeKettle : IRequestingTypeLogged;

/// <summary>A grinder whose base implementation and decorator stay internal.</summary>
public interface IRequestingTypeGrinder : IRequestingTypeLogged;

/// <summary>The marker the module's <c>[Scan]</c> selects on, also the service the match is exposed as.</summary>
public interface IRequestingTypePlugin : IRequestingTypeLogged;

internal sealed class RequestingTypeKettle(RequestingTypeLog log) : IRequestingTypeKettle
{
	public RequestingTypeLog Log { get; } = log;
}

internal sealed class RequestingTypeGrinder(RequestingTypeLog log) : IRequestingTypeGrinder
{
	public RequestingTypeLog Log { get; } = log;
}

internal sealed class LoggingRequestingTypeGrinder(IRequestingTypeGrinder inner, RequestingTypeLog log) : IRequestingTypeGrinder
{
	public IRequestingTypeGrinder Inner { get; } = inner;

	public RequestingTypeLog Log { get; } = log;
}

internal sealed class RequestingTypePlugin(RequestingTypeLog log) : IRequestingTypePlugin
{
	public RequestingTypeLog Log { get; } = log;
}

/// <summary>
///     A <c>[Module]</c> in a referenced assembly without <c>InternalsVisibleTo</c> whose internal implementation,
///     decorator and scan match take a log from a <c>[RequestingType]</c> factory, so a consumer observes the requesting
///     type each of them presents through the module's generated factories.
/// </summary>
[Module]
[Transient<RequestingTypeLog>(Factory = nameof(CreateLog))]
[Singleton<RequestingTypeKettle, IRequestingTypeKettle>]
[Singleton<RequestingTypeGrinder, IRequestingTypeGrinder>]
[Decorate<LoggingRequestingTypeGrinder, IRequestingTypeGrinder>]
[Scan<IRequestingTypePlugin>(As = ScanAs.Marker)]
public static partial class RequestingTypeModule
{
	/// <summary>Names the log after the type that requested it.</summary>
	public static RequestingTypeLog CreateLog([RequestingType] Type? requestingType) => new(requestingType?.Name ?? "<root>");
}
