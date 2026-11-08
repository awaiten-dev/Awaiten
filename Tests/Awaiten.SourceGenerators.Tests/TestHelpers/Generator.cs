using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests.TestHelpers;

/// <summary>
///     Drives the <see cref="AwaitenGenerator" /> over an in-memory compilation and returns the generated
///     sources plus any diagnostics.
/// </summary>
public static class Generator
{
	private static readonly string[] NoWarn =
	[
		"CS8019", /* Unnecessary using directive. */
	];

	/// <summary>The compile references of <c>Awaiten.Tests.PolyfillFreeSupport</c> for net48, which the build writes next to the tests.</summary>
	private static readonly Lazy<MetadataReference[]> PolyfillFreeReferences = new(() =>
		System.IO.File.ReadAllLines(System.IO.Path.Combine(AppContext.BaseDirectory, "polyfill-free.references"))
			.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
			.ToArray());

	public static GeneratorResult Run([StringSyntax("c#-test")] string source, params Type[] assemblyTypes)
		=> Run(source, additionalReferences: [], assemblyTypes);

	/// <summary>
	///     Like <see cref="Run(string, Type[])" />, but parses <paramref name="source" /> and the generated code at
	///     <paramref name="languageVersion" /> under the project-level <paramref name="nullable" /> context.
	/// </summary>
	public static GeneratorResult Run(
		LanguageVersion languageVersion,
		[StringSyntax("c#-test")] string source,
		NullableContextOptions nullable = NullableContextOptions.Disable)
		=> Collect(RunGenerator(source, [], [], languageVersion: languageVersion, nullable: nullable));

	/// <summary>
	///     Like <see cref="Run(string, Type[])" />, but with the severities of <paramref name="diagnosticOptions" />, as a
	///     user sets them in an <c>.editorconfig</c> or with <c>NoWarn</c> and <c>WarningsAsErrors</c>.
	/// </summary>
	public static GeneratorResult Run([StringSyntax("c#-test")] string source, IReadOnlyDictionary<string, ReportDiagnostic> diagnosticOptions)
		=> Collect(RunGenerator(source, [], [], diagnosticOptions: diagnosticOptions));

	/// <summary>
	///     Compiles <paramref name="referencedSource" /> into a separate referenced assembly first, then runs the
	///     generator over <paramref name="source" />. For cross-assembly scenarios such as a <c>[Module]</c> in
	///     another project.
	/// </summary>
	public static GeneratorResult RunWithReferencedAssembly(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
	{
		CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
		CSharpCompilation referenced = CSharpCompilation.Create(
			"ReferencedAssembly",
			[CSharpSyntaxTree.ParseText(referencedSource, parseOptions),],
			References.For([]),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		return Run(source, [EmitToReference(referenced),]);
	}

	/// <summary>
	///     Like <see cref="RunWithReferencedAssembly" />, but runs the generator over
	///     <paramref name="referencedSource" /> first, so the referenced assembly contains the generated code
	///     (for example a <c>[Module]</c> that self-compiles its <c>[Scan]</c> into factory methods and registration
	///     attributes). Then runs the generator over <paramref name="source" /> against that assembly.
	/// </summary>
	public static GeneratorResult RunWithGeneratedReferencedAssembly(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
	{
		(Compilation referencedOutput, _) = RunGenerator(referencedSource, [], [], "ReferencedAssembly");
		return Run(source, [EmitToReference(referencedOutput),]);
	}

	/// <summary>
	///     Like <see cref="RunWithGeneratedReferencedAssembly(string, string)" />, but compiles each assembly at its own
	///     language version.
	/// </summary>
	public static GeneratorResult RunWithGeneratedReferencedAssembly(
		LanguageVersion referencedLanguageVersion,
		[StringSyntax("c#-test")] string referencedSource,
		LanguageVersion languageVersion,
		[StringSyntax("c#-test")] string source)
	{
		(Compilation referencedOutput, _) = RunGenerator(referencedSource, [], [], "ReferencedAssembly", referencedLanguageVersion);
		return Collect(RunGenerator(source, [EmitToReference(referencedOutput),], [], languageVersion: languageVersion));
	}

	/// <summary>
	///     Like <see cref="RunWithGeneratedReferencedAssembly" />, but with two independent generated referenced
	///     assemblies, for example two libraries that declare a type of the same name.
	/// </summary>
	public static GeneratorResult RunWithGeneratedReferencedAssemblies(
		[StringSyntax("c#-test")] string firstReferencedSource,
		[StringSyntax("c#-test")] string secondReferencedSource,
		[StringSyntax("c#-test")] string source)
	{
		(Compilation first, _) = RunGenerator(firstReferencedSource, [], [], "FirstAssembly");
		(Compilation second, _) = RunGenerator(secondReferencedSource, [], [], "SecondAssembly");
		return Run(source, [EmitToReference(first), EmitToReference(second),]);
	}

	/// <summary>
	///     Like <see cref="Run(LanguageVersion, string, NullableContextOptions)" />, but against the references of a net48
	///     project without a nullable polyfill, where <c>NotNullWhenAttribute</c> exists only as the internal copy inside
	///     Awaiten's netstandard2.0 asset, with nullable enabled. <paramref name="laterSource" /> joins the compilation after
	///     the generator ran, like the output of another generator.
	/// </summary>
	public static GeneratorResult RunPolyfillFree(
		LanguageVersion languageVersion,
		[StringSyntax("c#-test")] string source,
		[StringSyntax("c#-test")] string? laterSource = null)
	{
		(Compilation output, GeneratorDriverRunResult result) = RunGenerator(
			source, [], [], languageVersion: languageVersion, nullable: NullableContextOptions.Enable, references: PolyfillFreeReferences.Value);
		if (laterSource is not null)
		{
			output = output.AddSyntaxTrees(CSharpSyntaxTree.ParseText(laterSource, new CSharpParseOptions(languageVersion)));
		}

		return Collect((output, result));
	}

	/// <summary>
	///     Like <see cref="RunPolyfillFree" />, but runs the generator over <paramref name="referencedSource" /> first and
	///     compiles <paramref name="source" /> against that assembly.
	/// </summary>
	public static GeneratorResult RunPolyfillFreeWithGeneratedReferencedAssembly(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
	{
		(Compilation referenced, _) = RunGenerator(
			referencedSource, [], [], "ReferencedAssembly", nullable: NullableContextOptions.Enable, references: PolyfillFreeReferences.Value);
		return Collect(RunGenerator(
			source, [EmitToReference(referenced),], [], nullable: NullableContextOptions.Enable, references: PolyfillFreeReferences.Value));
	}

	private static GeneratorResult Run(
		[StringSyntax("c#-test")] string source,
		MetadataReference[] additionalReferences,
		params Type[] assemblyTypes)
		=> Collect(RunGenerator(source, additionalReferences, assemblyTypes));

	private static GeneratorResult Collect((Compilation Output, GeneratorDriverRunResult Result) run)
	{
		(Compilation outputCompilation, GeneratorDriverRunResult runResult) = run;

		ImmutableArray<Diagnostic> compilationDiagnostics = outputCompilation.GetDiagnostics();
		Dictionary<string, string> generatedSources = runResult.Results
			.SelectMany(r => r.GeneratedSources)
			.ToDictionary(s => s.HintName, s => s.SourceText.ToString());
		string[] diagnosticMessages =
		[
			..compilationDiagnostics.Where(x => !NoWarn.Contains(x.Id)).Select(x => x.ToString()),
			..runResult.Diagnostics.Where(x => !NoWarn.Contains(x.Id)).Select(x => x.ToString()),
		];
		return new GeneratorResult(generatedSources, diagnosticMessages);
	}

	/// <summary>
	///     The single generator-driver bootstrap shared by <see cref="Generator" /> and <see cref="Analyzer" />.
	///     Returns the post-generation compilation plus the driver's run result (generator diagnostics and
	///     generated sources).
	/// </summary>
	internal static (Compilation Output, GeneratorDriverRunResult Result) RunGenerator(
		[StringSyntax("c#-test")] string source,
		MetadataReference[] additionalReferences,
		Type[] assemblyTypes,
		string assemblyName = "TestAssembly",
		LanguageVersion languageVersion = LanguageVersion.Latest,
		NullableContextOptions nullable = NullableContextOptions.Disable,
		IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions = null,
		MetadataReference[]? references = null)
	{
		CSharpParseOptions parseOptions = new(languageVersion);
		SyntaxTree[] syntaxTrees = [CSharpSyntaxTree.ParseText(source, parseOptions),];

		CSharpCompilation compilation = CSharpCompilation.Create(
			assemblyName,
			syntaxTrees,
			[..references ?? [..References.For(assemblyTypes),], ..additionalReferences,],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, specificDiagnosticOptions: diagnosticOptions, nullableContextOptions: nullable));

		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			[new AwaitenGenerator().AsSourceGenerator(),],
			[],
			parseOptions,
			null);
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation outputCompilation, out _);
		return (outputCompilation, driver.GetRunResult());
	}

	/// <summary>
	///     Emits a compilation to an in-memory assembly and returns a metadata reference to it, failing
	///     loudly (with the offending diagnostics) when it does not compile.
	/// </summary>
	internal static MetadataReference EmitToReference(Compilation compilation)
	{
		using System.IO.MemoryStream stream = new();
		Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(stream);
		if (!emitted.Success)
		{
			throw new InvalidOperationException(
				"The referenced assembly does not compile: "
				+ string.Join(Environment.NewLine, emitted.Diagnostics.Select(d => d.ToString())));
		}

		return MetadataReference.CreateFromImage(stream.ToArray());
	}
}
