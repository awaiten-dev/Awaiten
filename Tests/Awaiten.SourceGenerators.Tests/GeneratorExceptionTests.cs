using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A generator that throws only leaves warning CS8785 and emits nothing, which a test that asserts a single diagnostic
///     or the absence of one would miss, so the test helpers fail on it.
/// </summary>
public class GeneratorExceptionTests
{
	[Test]
	public async Task FailsTheTest_InsteadOfLeavingOnlyAWarning()
	{
		GeneratorDriverRunResult result = CSharpGeneratorDriver.Create(new ThrowingGenerator())
			.RunGenerators(CSharpCompilation.Create("TestAssembly", [CSharpSyntaxTree.ParseText("public class Shop { }"),]))
			.GetRunResult();

		await That(() => Generator.FailOnGeneratorException(result)).Throws<InvalidOperationException>()
			.WithMessage("The generator threw: *SyntaxTree is not part of the compilation*").AsWildcard()
			.Because("every run of the generator in a test goes through this check, so an exception fails the test whatever it asserts");
	}

	private sealed class ThrowingGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
			=> context.RegisterSourceOutput(context.CompilationProvider, (_, _) => throw new ArgumentException("SyntaxTree is not part of the compilation"));
	}
}
