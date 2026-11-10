using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators.Entities;

/// <summary>
///     An equatable description of a diagnostic to report, deferred until the source-output stage.
///     <see cref="Severity" /> optionally overrides the descriptor's default severity (e.g. a rule that is
///     a warning by default but escalates to an error under strict lifetime safety).
/// </summary>
internal sealed record DiagnosticInfo(
	DiagnosticDescriptor Descriptor,
	LocationInfo? Location,
	EquatableArray<string> MessageArgs,
	DiagnosticSeverity? Severity = null)
{
	/// <summary>
	///     The severity this diagnostic is actually reported at: the <see cref="Severity" /> override when
	///     set, otherwise the descriptor's default.
	/// </summary>
	public DiagnosticSeverity EffectiveSeverity => Severity ?? Descriptor.DefaultSeverity;

	public Diagnostic ToDiagnostic() => Create(Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None);

	/// <summary>
	///     The diagnostic to report in <paramref name="compilation" />, located in its syntax tree of the same file path, because
	///     the compiler applies the <c>.editorconfig</c> severities of a file and its <c>#pragma warning</c> directives only to a
	///     location in a tree of the compilation; <see cref="LocationInfo.ToLocation" /> yields a location in no tree. A location
	///     whose span does not fit the file of that path here, such as one in another file of a referenced compilation that shares
	///     the path, keeps that external location.
	/// </summary>
	public Diagnostic ToDiagnostic(Compilation compilation, CancellationToken cancellationToken)
	{
		if (Location is not { } source)
		{
			return Create(Microsoft.CodeAnalysis.Location.None);
		}

		SyntaxTree? tree = source.TreeIn(compilation, cancellationToken);
		return Create(tree is null ? source.ToLocation() : Microsoft.CodeAnalysis.Location.Create(tree, source.TextSpan));
	}

	private Diagnostic Create(Location location)
	{
		object?[] args = MessageArgs.AsArray().Cast<object?>().ToArray();
		return Severity is { } severity
			? Diagnostic.Create(Descriptor, location, severity, additionalLocations: null, properties: null, args)
			: Diagnostic.Create(Descriptor, location, args);
	}
}
