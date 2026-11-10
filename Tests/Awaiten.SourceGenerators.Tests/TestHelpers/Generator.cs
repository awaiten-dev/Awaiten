using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

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
	///     Like <see cref="Run(string, Type[])" />, but compiles each of <paramref name="files" /> as a file under <c>/src</c>
	///     with the severities of <paramref name="editorConfig" />, the text of the <c>/src/.editorconfig</c>, applied per file as the
	///     compiler applies them.
	/// </summary>
	public static GeneratorResult Run(IReadOnlyDictionary<string, string> files, [StringSyntax("ini")] string editorConfig)
	{
		CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
		AnalyzerConfigSet configs = AnalyzerConfigSet.Create(ImmutableArray.Create(AnalyzerConfig.Parse(editorConfig, "/src/.editorconfig")));
		CSharpCompilation compilation = CSharpCompilation.Create(
			"TestAssembly",
			files.Select(file => CSharpSyntaxTree.ParseText(file.Value, parseOptions, $"/src/{file.Key}")),
			References.For([]),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithSyntaxTreeOptionsProvider(new EditorConfigOptions(configs)));

		GeneratorDriver driver = CSharpGeneratorDriver.Create([new AwaitenGenerator().AsSourceGenerator(),], [], parseOptions, null);
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation outputCompilation, out _);
		return Collect((outputCompilation, driver.GetRunResult()));
	}

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
	///     Like <see cref="RunWithGeneratedReferencedAssembly(string, string)" />, but references the reference assembly of
	///     the generated assembly, as a project reference does in a build, which leaves out private members and, without
	///     <c>InternalsVisibleTo</c>, internal ones.
	/// </summary>
	public static GeneratorResult RunWithGeneratedReferenceAssembly(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
	{
		(Compilation referencedOutput, _) = RunGenerator(referencedSource, [], [], "ReferencedAssembly");
		return Run(source, [EmitToReference(referencedOutput, new EmitOptions(metadataOnly: true, includePrivateMembers: false)),]);
	}

	/// <summary>
	///     Like <see cref="RunWithGeneratedReferencedAssembly(string, string)" />, but references the generated assembly as a compilation,
	///     as the IDE references another project of the solution, so its internal members stay visible to the symbols.
	/// </summary>
	public static GeneratorResult RunWithGeneratedCompilationReference(
		[StringSyntax("c#-test")] string referencedSource,
		[StringSyntax("c#-test")] string source)
	{
		(Compilation referencedOutput, _) = RunGenerator(referencedSource, [], [], "ReferencedAssembly");
		return Run(source, [referencedOutput.ToMetadataReference(),]);
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
	///     Like <see cref="RunWithGeneratedReferencedAssembly" />, but with a chain of generated referenced assemblies:
	///     each of <paramref name="referencedSources" /> is compiled with the generator against the ones before it, so a
	///     library can reference another generated library, and <paramref name="source" /> references all of them. With
	///     <paramref name="referenceAssemblies" />, each is referenced by its reference assembly, as a project reference does in a build.
	/// </summary>
	public static GeneratorResult RunWithGeneratedReferencedAssemblyChain(
		string[] referencedSources,
		[StringSyntax("c#-test")] string source,
		bool referenceAssemblies = false)
	{
		List<MetadataReference> references = new();
		foreach (string referencedSource in referencedSources)
		{
			(Compilation referenced, _) = RunGenerator(referencedSource, references.ToArray(), [], $"Library{references.Count + 1}");
			references.Add(EmitToReference(referenced, referenceAssemblies ? new EmitOptions(metadataOnly: true, includePrivateMembers: false) : null));
		}

		return Run(source, references.ToArray());
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
	///     Runs the generator over <paramref name="moduleSource" /> into a library, builds <paramref name="baseSource" />
	///     against it as <paramref name="build" /> says, and runs the generator over <paramref name="source" /> against both,
	///     for a container that extends a base of another assembly which imports a module of a third one. The generator's
	///     diagnostics never fail the referenced builds, as when the user suppresses them.
	/// </summary>
	public static GeneratorResult RunWithModuleAndBase(
		[StringSyntax("c#-test")] string moduleSource,
		[StringSyntax("c#-test")] string baseSource,
		[StringSyntax("c#-test")] string source,
		BaseBuild build)
	{
		(Compilation module, _) = RunGenerator(moduleSource, [], [], "ModuleAssembly");
		MetadataReference moduleReference = EmitToReference(module);
		MetadataReference baseReference;
		if (build == BaseBuild.WithoutGenerator)
		{
			baseReference = EmitToReference(CSharpCompilation.Create(
				"BaseAssembly",
				[CSharpSyntaxTree.ParseText(baseSource, new CSharpParseOptions(LanguageVersion.Latest)),],
				[..References.For([]), moduleReference,],
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
		}
		else
		{
			(Compilation @base, _) = RunGenerator(baseSource, [moduleReference,], [], "BaseAssembly");
			baseReference = build == BaseBuild.CompilationReference ? @base.ToMetadataReference() : EmitToReference(@base);
		}

		return Run(source, [moduleReference, baseReference,]);
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
			..runResult.Diagnostics.Where(x => !NoWarn.Contains(x.Id) && !x.IsSuppressed).Select(x => x.ToString()),
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
	internal static MetadataReference EmitToReference(Compilation compilation, EmitOptions? options = null)
	{
		using System.IO.MemoryStream stream = new();
		EmitResult emitted = compilation.Emit(stream, options: options);
		if (!emitted.Success)
		{
			throw new InvalidOperationException(
				"The referenced assembly does not compile: "
				+ string.Join(Environment.NewLine, emitted.Diagnostics.Select(d => d.ToString())));
		}

		return MetadataReference.CreateFromImage(stream.ToArray());
	}

	/// <summary>The severities of an <c>.editorconfig</c>, which the compiler reads per syntax tree.</summary>
	internal sealed class EditorConfigOptions(AnalyzerConfigSet configs) : SyntaxTreeOptionsProvider
	{
		public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) => GeneratedKind.Unknown;

		public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
			=> configs.GetOptionsForSourcePath(tree.FilePath).TreeOptions.TryGetValue(diagnosticId, out severity);

		public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
			=> configs.GlobalConfigOptions.TreeOptions.TryGetValue(diagnosticId, out severity);
	}
}

/// <summary>How <see cref="Generator.RunWithModuleAndBase" /> builds the assembly of the base container.</summary>
public enum BaseBuild
{
	/// <summary>With the generator, its errors suppressed, as with <c>#pragma</c>, <c>.editorconfig</c> or <c>NoWarn</c>.</summary>
	Suppressed,

	/// <summary>Without the generator, as by an older generator that does not know the check.</summary>
	WithoutGenerator,

	/// <summary>With the generator, referenced as a compilation, as the IDE references a project whose build reports the error.</summary>
	CompilationReference,
}
