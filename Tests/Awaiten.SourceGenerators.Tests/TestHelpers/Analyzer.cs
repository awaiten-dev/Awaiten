using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Awaiten.SourceGenerators.Tests.TestHelpers;

/// <summary>
///     Runs a <see cref="DiagnosticAnalyzer" /> over an in-memory compilation and returns its diagnostics.
///     The <see cref="AwaitenGenerator" /> runs first, as in a real build, so a usage-site diagnostic (AWT156)
///     can resolve the generated <c>Root</c>/<c>Scope</c> types.
/// </summary>
public static class Analyzer
{
	public static Task<string[]> Run<TAnalyzer>(
		[StringSyntax("c#-test")] string source,
		params Type[] assemblyTypes)
		where TAnalyzer : DiagnosticAnalyzer, new()
		=> Run<TAnalyzer>(source, additionalReferences: [], assemblyTypes);

	/// <summary>
	///     Compiles <paramref name="referencedSource" /> into a separate referenced assembly (with the
	///     <see cref="AwaitenGenerator" /> applied, so a <c>[Container]</c> there carries its generated
	///     <c>Root</c>/<c>Scope</c> into the metadata reference), then runs the analyzer over
	///     <paramref name="source" />. For cross-assembly scenarios such as disposing another project's container.
	/// </summary>
	public static Task<string[]> RunWithReferencedAssembly<TAnalyzer>(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		(Compilation referenced, _) = Generator.RunGenerator(referencedSource, [], [], "ReferencedAssembly");
		return Run<TAnalyzer>(source, [Generator.EmitToReference(referenced),], []);
	}

	/// <summary>
	///     Like <see cref="RunWithReferencedAssembly{TAnalyzer}" />, but references the generated assembly as a compilation, as
	///     the IDE references another project of the solution, so its symbols keep their syntax trees.
	/// </summary>
	public static Task<string[]> RunWithCompilationReference<TAnalyzer>(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		(Compilation referenced, _) = Generator.RunGenerator(referencedSource, [], [], "ReferencedAssembly");
		return Run<TAnalyzer>(source, [Generator.ToCompilationReference(referenced),], []);
	}

	/// <summary>
	///     Like <see cref="Run{TAnalyzer}(string, Type[])" />, but compiles <paramref name="source" /> at
	///     <paramref name="languageVersion" />, where the generator may report AWT243 instead of generating a container.
	/// </summary>
	public static Task<string[]> Run<TAnalyzer>(
		LanguageVersion languageVersion,
		[StringSyntax("c#-test")] string source)
		where TAnalyzer : DiagnosticAnalyzer, new()
		=> Analyze<TAnalyzer>(Generator.RunGenerator(source, [], [], languageVersion: languageVersion), "AWT243");

	/// <summary>
	///     Like <see cref="Run{TAnalyzer}(string, Type[])" />, for a <paramref name="source" /> on which the generator
	///     reports the <paramref name="expectedErrors" />.
	/// </summary>
	public static Task<string[]> RunExpectingErrors<TAnalyzer>(
		[StringSyntax("c#-test")] string source,
		params string[] expectedErrors)
		where TAnalyzer : DiagnosticAnalyzer, new()
		=> Analyze<TAnalyzer>(Generator.RunGenerator(source, [], []), expectedErrors);

	private static Task<string[]> Run<TAnalyzer>(
		[StringSyntax("c#-test")] string source,
		MetadataReference[] additionalReferences,
		Type[] assemblyTypes)
		where TAnalyzer : DiagnosticAnalyzer, new()
		=> Analyze<TAnalyzer>(Generator.RunGenerator(source, additionalReferences, assemblyTypes));

	private static async Task<string[]> Analyze<TAnalyzer>(
		(Compilation Output, GeneratorDriverRunResult Result) run,
		params string[] expectedErrors)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		(Compilation outputCompilation, GeneratorDriverRunResult generatorResult) = run;

		// A snippet that fails to compile would pass negative assertions vacuously, so fail loudly instead.
		string[] errors = generatorResult.Diagnostics
			.Concat(outputCompilation.GetDiagnostics())
			.Where(d => d.Severity == DiagnosticSeverity.Error && !expectedErrors.Contains(d.Id))
			.Select(d => d.ToString())
			.ToArray();
		if (errors.Length > 0)
		{
			throw new InvalidOperationException(
				"The analyzer test source does not compile:"
				+ Environment.NewLine + string.Join(Environment.NewLine, errors));
		}

		CompilationWithAnalyzers withAnalyzers = outputCompilation.WithAnalyzers(
			ImmutableArray.Create<DiagnosticAnalyzer>(new TAnalyzer()));
		ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();

		// An analyzer that throws only leaves warning AD0001, which a test asserting a single diagnostic or the absence of one would miss.
		if (diagnostics.FirstOrDefault(d => d.Id == "AD0001") is { } exception)
		{
			throw new InvalidOperationException($"The analyzer threw: {exception}");
		}

		// GetAnalyzerDiagnosticsAsync includes in-source-suppressed diagnostics; drop them to mirror a real build.
		return diagnostics.Where(d => !d.IsSuppressed).Select(d => d.ToString()).ToArray();
	}
}
