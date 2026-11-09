namespace Awaiten.Tests.Support;

/// <summary>A document <see cref="ConstrainedFormatterModule" /> formats as text.</summary>
public interface ITextDocument;

/// <summary>A document <see cref="ConstrainedFormatterModule" /> formats as binary.</summary>
public interface IBinaryDocument;

/// <summary>A formatter per document type, built from the internal implementation whose constraints the type satisfies.</summary>
public interface IDocumentFormatter<T>
{
	/// <summary>The name of the implementation that formats the document.</summary>
	string Kind { get; }
}

internal sealed class TextDocumentFormatter<T> : IDocumentFormatter<T>
	where T : ITextDocument
{
	public string Kind => "text";
}

internal sealed class BinaryDocumentFormatter<T> : IDocumentFormatter<T>
	where T : IBinaryDocument
{
	public string Kind => "binary";
}

/// <summary>
///     A <c>[Module]</c> with two internal open generic implementations of one service under disjoint constraints, which
///     a container in another assembly closes through the generated generic factories for the type arguments each allows.
/// </summary>
[Module]
[Transient(typeof(TextDocumentFormatter<>), typeof(IDocumentFormatter<>))]
[Transient(typeof(BinaryDocumentFormatter<>), typeof(IDocumentFormatter<>))]
public static partial class ConstrainedFormatterModule;
