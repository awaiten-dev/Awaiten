using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The severity a user configures for an Awaiten diagnostic in an <c>.editorconfig</c>, and a <c>#pragma</c> in source,
///     apply to the diagnostics the generator reports, as they do to the compiler's.
/// </summary>
public class DiagnosticConfigurationTests
{
	private const string SkippedScanMatch = """
		using Awaiten;

		namespace MyCode;

		public interface IPlugin { }
		public sealed class NeedsName : IPlugin
		{
		    public NeedsName(string name) { }
		}

		[Container]
		[Scan(typeof(IPlugin), SkipUnconstructable = true)]
		public static partial class MyContainer
		{
		}
		""";

	private const string MissingDependency = """
		using Awaiten;

		namespace MyCode;

		public sealed class Grinder { }
		public sealed class Cup
		{
		    public Cup(Grinder grinder) { }
		}

		[Container]
		[Transient<Cup>]
		public static partial class MyContainer
		{
		}
		""";

	[Test]
	public async Task EditorConfig_RaisesAWarningToAnError()
	{
		GeneratorResult result = Generator.Run(
			new Dictionary<string, string> { ["Container.cs"] = SkippedScanMatch, },
			"""
			[*.cs]
			dotnet_diagnostic.AWT141.severity = error
			""");

		await That(result.Diagnostics).HasSingle().Which.Contains("/src/Container.cs(12,2): error AWT141:*").AsWildcard();
	}

	[Test]
	public async Task EditorConfig_LowersAnErrorToAWarning()
	{
		GeneratorResult result = Generator.Run(
			new Dictionary<string, string> { ["Container.cs"] = MissingDependency, },
			"""
			[*.cs]
			dotnet_diagnostic.AWT101.severity = warning
			""");

		await That(result.Diagnostics).HasSingle().Which.Contains("/src/Container.cs(*): warning AWT101:*Grinder*").AsWildcard();
	}

	[Test]
	public async Task EditorConfig_None_SuppressesTheDiagnostic()
	{
		GeneratorResult result = Generator.Run(
			new Dictionary<string, string> { ["Container.cs"] = SkippedScanMatch, },
			"""
			[*.cs]
			dotnet_diagnostic.AWT141.severity = none
			""");

		await That(result.Diagnostics).IsEmpty();
	}

	[Test]
	public async Task EditorConfig_SectionOfAnotherFile_DoesNotApply()
	{
		GeneratorResult result = Generator.Run(
			new Dictionary<string, string> { ["Container.cs"] = SkippedScanMatch, ["Other.cs"] = "namespace MyCode; public class Other { }", },
			"""
			[Other.cs]
			dotnet_diagnostic.AWT141.severity = error
			""");

		await That(result.Diagnostics).HasSingle().Which.Contains("/src/Container.cs(12,2): warning AWT141:*").AsWildcard()
			.Because("the severity applies to the file the diagnostic is reported in");
	}

	[Test]
	public async Task Pragma_SuppressesTheDiagnosticWhereItIsDisabled()
	{
		GeneratorResult disabled = Generator.Run("#pragma warning disable AWT141\n" + SkippedScanMatch);
		GeneratorResult restored = Generator.Run("#pragma warning disable AWT141\n#pragma warning restore AWT141\n" + SkippedScanMatch);

		await That(disabled.Diagnostics).IsEmpty();
		await That(restored.Diagnostics).HasSingle().Which.Contains("*warning AWT141:*").AsWildcard()
			.Because("the warning is restored before the scan it is reported at");
	}

	[Test]
	public async Task EditOfAnotherFile_ReportsTheDiagnosticAgainWithoutEmittingTheContainerAgain()
	{
		CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
		CSharpCompilation compilation = CSharpCompilation.Create(
			"TestAssembly",
			[CSharpSyntaxTree.ParseText(SkippedScanMatch, parseOptions, "/src/Container.cs"),],
			References.For([]),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		GeneratorDriver driver = CSharpGeneratorDriver.Create([new AwaitenGenerator().AsSourceGenerator(),], [], parseOptions, null)
			.RunGenerators(compilation);
		GeneratorRunResult first = Generator.FailOnGeneratorException(driver.GetRunResult()).Results.Single();

		CSharpCompilation edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace MyCode; public class Other { }", parseOptions, "/src/Other.cs"));
		GeneratorRunResult second = Generator.FailOnGeneratorException(driver.RunGenerators(edited).GetRunResult()).Results.Single();

		await That(second.GeneratedSources.Single().SourceText).IsSameAs(first.GeneratedSources.Single().SourceText)
			.Because("the container's model is unchanged, so its source is not emitted again although the compilation changed");
		await That(second.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Id == "AWT141" && diagnostic.Location.SourceTree == edited.SyntaxTrees[0])
			.Because("the diagnostic is located in the tree of the compilation it is reported for");
	}
}
