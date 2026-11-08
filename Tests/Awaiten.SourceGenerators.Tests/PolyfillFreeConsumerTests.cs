using System.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The generated code compiles without warnings in a net48 project without a nullable polyfill, where
///     <c>NotNullWhenAttribute</c> exists only as the internal copy inside Awaiten's netstandard2.0 asset.
/// </summary>
public class PolyfillFreeConsumerTests
{
	private const string Shop = """
		using System;
		using Awaiten;

		namespace Shop
		{
		    public interface IClock { }
		    public sealed class Clock : IClock { }

		    [Container]
		    [Singleton(typeof(Clock), typeof(IClock))]
		    [Singleton(typeof(Clock), typeof(IClock), Key = "keyed")]
		    public static partial class ShopContainer { }
		}
		""";

	[Test]
	[Arguments(LanguageVersion.CSharp9)]
	[Arguments(LanguageVersion.CSharp10)]
	[Arguments(LanguageVersion.Latest)]
	public async Task AContainer_CompilesWithoutWarnings(LanguageVersion languageVersion)
	{
		GeneratorResult result = Generator.RunPolyfillFree(languageVersion, Shop);

		await That(result.Diagnostics).IsEmpty()
			.Because("a consumer with warnings as errors must compile the generated container");
		await That(result.Sources.Values.Single()).Contains("#pragma warning disable CS8767")
			.Because("the project cannot apply the attribute, so the generated TryResolve cannot repeat the interface's annotation");
	}

	[Test]
	public async Task AContainerWithRegistrationErrors_ReportsOnlyItsError()
	{
		GeneratorResult result = Generator.RunPolyfillFree(LanguageVersion.Latest, """
			using Awaiten;

			namespace Shop
			{
			    public interface IClock { }
			    public sealed class Clock : IClock { public Clock(string zone) { } }

			    [Container]
			    [Singleton(typeof(Clock), typeof(IClock))]
			    public static partial class ShopContainer { }
			}
			""");

		await That(result.Diagnostics).HasSingle().Which.Contains("*AWT101*").AsWildcard()
			.Because("the throwing shell of a container with errors carries the same TryResolve members");
	}
}
