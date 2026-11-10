using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Awaiten.SourceGenerators.Entities;

/// <summary>
///     An equatable snapshot of a <see cref="Location" /> that does not capture any Roslyn symbol or
///     syntax instance, so it can flow through the incremental pipeline without breaking caching.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
	public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

	/// <summary>
	///     The syntax tree of <paramref name="compilation" /> of the same file path whose text the span fits, else
	///     <see langword="null" />, such as for a location in another file of a referenced compilation that shares the path.
	/// </summary>
	public SyntaxTree? TreeIn(Compilation compilation, CancellationToken cancellationToken)
		=> compilation.SyntaxTrees.FirstOrDefault(candidate => candidate.FilePath == FilePath
		                                                       && TextSpan.End <= candidate.Length
		                                                       && candidate.GetLineSpan(TextSpan, cancellationToken).Span == LineSpan);

	public static LocationInfo? From(Location? location)
	{
		if (location?.SourceTree is null)
		{
			return null;
		}

		return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
	}
}