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

	private const string Caller = """
		namespace Shop
		{
		    public static class Caller
		    {
		        public static string Clock(ShopContainer.Root root)
		            => root.TryResolve(typeof(IClock), out object? clock) ? clock.ToString() : "";

		        public static string KeyedClock(ShopContainer.Root root)
		            => root.TryResolve(typeof(IClock), "keyed", out object? clock) ? clock.ToString() : "";
		    }
		}
		""";

	private const string NotNullWhenPolyfill = """
		namespace System.Diagnostics.CodeAnalysis
		{
		    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
		    internal sealed class NotNullWhenAttribute : Attribute
		    {
		        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;
		        public bool ReturnValue { get; }
		    }
		}

		namespace Own
		{
		    public static class Lookup
		    {
		        public static bool TryFind([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
		        {
		            value = "found";
		            return true;
		        }

		        public static int Length() => TryFind(out string? value) ? value.Length : 0;
		    }
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

	[Test]
	[Arguments(LanguageVersion.CSharp11)]
	[Arguments(LanguageVersion.Latest)]
	public async Task TryResolve_TellsTheCallerTheInstanceIsNotNullWhenItReturnsTrue(LanguageVersion languageVersion)
	{
		GeneratorResult result = Generator.RunPolyfillFree(languageVersion, Shop + Caller);

		await That(result.Diagnostics).IsEmpty()
			.Because("from C# 11 the generated file declares its own file-local NotNullWhenAttribute, which the compiler honors like the real one");
	}

	[Test]
	[Arguments(LanguageVersion.CSharp9)]
	[Arguments(LanguageVersion.CSharp10)]
	public async Task BelowCSharp11_TryResolveCarriesNoAnnotation(LanguageVersion languageVersion)
	{
		GeneratorResult result = Generator.RunPolyfillFree(languageVersion, Shop);

		await That(result.Sources.Values.Single()).Contains("#pragma warning disable CS8767")
			.Because("a file-local attribute needs C# 11 and any other copy could clash with a polyfill of the project, so the generated TryResolve cannot repeat the interface's annotation");
		await That(result.Sources.Values.Single()).DoesNotContain("NotNullWhenAttribute");
	}

	[Test]
	public async Task SeveralContainers_EachDeclareTheirOwnCopy()
	{
		GeneratorResult result = Generator.RunPolyfillFree(LanguageVersion.Latest, Shop + Caller + """
			namespace Shop
			{
			    [Awaiten.Container]
			    [Awaiten.Transient(typeof(Clock), typeof(IClock))]
			    public static partial class OtherContainer { }

			    public static class OtherCaller
			    {
			        public static string Clock(OtherContainer.Root root)
			            => root.TryResolve(typeof(IClock), out object? clock) ? clock.ToString() : "";
			    }
			}
			""");

		await That(result.Sources).HasCount(2);
		await That(result.Sources["Awaiten.Shop.ShopContainer.g.cs"]).Contains("file sealed class NotNullWhenAttribute");
		await That(result.Sources["Awaiten.Shop.OtherContainer.g.cs"]).Contains("file sealed class NotNullWhenAttribute")
			.Because("a file-local copy is visible only in its own file, so every generated container file needs one");
		await That(result.Diagnostics).IsEmpty()
			.Because("a file-local type never clashes with the copy in another generated file, and each copy annotates the TryResolve of its own container, so neither caller gets CS8602");
	}

	[Test]
	public async Task APolyfillOfTheProject_IsUsedInsteadOfAFileLocalCopy()
	{
		GeneratorResult result = Generator.RunPolyfillFree(LanguageVersion.Latest, Shop + Caller + NotNullWhenPolyfill);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources.Values.Single()).DoesNotContain("class NotNullWhenAttribute")
			.Because("the project's own polyfill is accessible, and unlike a file-local copy it carries the annotation into other assemblies");
	}

	[Test]
	public async Task APolyfillTheGeneratorCannotSee_DoesNotClash()
	{
		GeneratorResult result = Generator.RunPolyfillFree(LanguageVersion.Latest, Shop + Caller, laterSource: NotNullWhenPolyfill);

		await That(result.Diagnostics).IsEmpty()
			.Because("another source generator such as PolySharp may add the same polyfill, which a generator does not see");
	}

	[Test]
	public async Task AFriendAssemblyWithItsOwnPolyfill_DoesNotSeeTheFileLocalCopy()
	{
		GeneratorResult result = Generator.RunPolyfillFreeWithGeneratedReferencedAssembly(
			Shop.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"),
			NotNullWhenPolyfill + """
				namespace Friend
				{
				    public static class Caller
				    {
				        public static bool HasClock(Shop.ShopContainer.Root root) => root.TryResolve(typeof(Shop.IClock), out _);
				    }
				}
				""");

		await That(result.Diagnostics).IsEmpty()
			.Because("an internal polyfill in the referenced assembly would conflict with the friend's own copy (CS0436)");
	}
}
