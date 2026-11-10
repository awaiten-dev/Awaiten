namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     An <c>[AsyncInitializer&lt;T&gt;]</c> convention of a public base container whose type or method is internal: a
///     container that extends it in another assembly runs it through a wrapper the base generates, while one in the
///     base's assembly or in a friend calls it directly. A base that generated no wrapper is reported, never left out.
/// </summary>
public class BaseContainerInternalConventionTests
{
	private const string ExtendingContainer = """
		using Awaiten;

		namespace MyCode;

		[Container(Extends = typeof(Lib.Shop))]
		public static partial class TestShop;
		""";

	private const string SameAssemblyContainer = """

		[Container(Extends = typeof(Shop))]
		public static partial class SameShop;
		""";

	private const string MethodConvention = "[AsyncInitializer<Terminal>(nameof(Terminal.StartAsync))]";

	private const string InterfaceConvention = "[AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]";

	private static string Library(string types, string attributes) => $$"""
		using Awaiten;
		using System.Threading;
		using System.Threading.Tasks;

		namespace Lib;

		internal interface IStartable { Task StartAsync(); }
		{{types}}

		[Container]
		{{attributes}}
		public static partial class Shop;
		""";

	private static string Friend(string library)
		=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

	[Test]
	[Arguments("public sealed class Terminal { internal Task StartAsync() => Task.CompletedTask; }", MethodConvention)]
	[Arguments("public sealed class Terminal : IStartable { public Task StartAsync() => Task.CompletedTask; }", InterfaceConvention)]
	public async Task AnInternalConvention_RunsOnceInEveryExtendingContainer(string types, string convention)
	{
		string library = Library(types, convention + "[Singleton<Terminal>]");

		GeneratorResult built = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);
		GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), ExtendingContainer);

		await That(built.Diagnostics).IsEmpty();
		await That(built.Sources["Awaiten.Lib.Shop.g.cs"]).Contains("public static class Internals")
			.And.Contains("Initializer_")
			.Because("the base generates a public wrapper for the convention an extending container in another assembly cannot call");
		foreach (GeneratorResult result in new[]
		         {
			         Generator.RunWithGeneratedReferencedAssembly(library, ExtendingContainer),
			         Generator.RunWithGeneratedReferenceAssembly(library, ExtendingContainer),
			         Generator.RunWithGeneratedCompilationReference(library, ExtendingContainer),
		         })
		{
			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains("await global::Lib.Shop.Internals.Initializer_").Exactly(1)
				.And.DoesNotContain(").StartAsync(")
				.Because("the extending container runs the inherited convention once through the base's wrapper");
		}

		await That(same.Diagnostics).IsEmpty();
		await That(same.Sources["Awaiten.Lib.SameShop.g.cs"]).Contains(").StartAsync()").Exactly(1)
			.And.DoesNotContain("Initializer_")
			.Because("an extending container in the base's assembly calls the convention directly");
		await That(friend.Diagnostics).IsEmpty();
		await That(friend.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains(").StartAsync()").Exactly(1)
			.And.DoesNotContain("Initializer_")
			.Because("a friend sees the internal convention and calls it directly");
	}

	[Test]
	public async Task OnTheExtendingContainersOwnRegistration_RunsThroughTheBaseWrapper()
	{
		string library = Library("public sealed class Terminal { internal Task StartAsync() => Task.CompletedTask; }", MethodConvention);

		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, """
			using Awaiten;

			namespace MyCode;

			[Container(Extends = typeof(Lib.Shop))]
			[Singleton<Lib.Terminal>]
			public static partial class TestShop;
			""");

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains("await global::Lib.Shop.Internals.Initializer_Terminal_").Exactly(1)
			.Because("the inherited convention judges the extending container's own registrations too, and runs through the wrapper of the base that declares it");
	}

	[Test]
	public async Task WithParameters_ForwardsThemThroughTheWrapper()
	{
		string library = Library(
			"public sealed class Clock { }\npublic sealed class Terminal { internal Task StartAsync(Clock clock, CancellationToken cancellationToken) => Task.CompletedTask; }",
			MethodConvention + "[Singleton<Clock>][Singleton<Terminal>]");

		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, ExtendingContainer);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains("await global::Lib.Shop.Internals.Initializer_Terminal_").Exactly(1)
			.And.Contains("cancellationToken)")
			.Because("the extending container resolves the wrapper's parameters like the convention's, passing the resolve-time token");
	}

	[Test]
	public async Task WithAParameterOfAnInternalType_IsReportedWithTheRecordedCause()
	{
		string library = Library(
			"internal sealed class Settings { }\npublic sealed class Terminal { internal Task StartAsync(Settings settings) => Task.CompletedTask; }",
			MethodConvention + "[Singleton<Settings>][Singleton<Terminal>]");

		GeneratorResult built = Generator.Run(library);
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, ExtendingContainer);

		await That(built.Sources["Awaiten.Lib.Shop.g.cs"]).Contains("InitializerSkipped_Terminal_")
			.And.Contains("InitializerRemedy_Terminal_")
			.Because("the base records why it generates no wrapper and what lets it");
		await That(result.Diagnostics).Contains(
				"*error AWT234: 'Lib.Terminal' is initialized by the async initializer 'Lib.Terminal.StartAsync', but the method is not accessible from the container 'MyCode.TestShop', and the container 'Lib.Shop' generated no wrapper for it because its parameter 'settings' has the type 'Lib.Settings', which is not public; make the parameter type public, or grant this assembly InternalsVisibleTo in the container's assembly").AsWildcard()
			.Because("the extending container names the cause and the remedy the base recorded");
	}

	[Test]
	public async Task OfAContainerThatExtendsAnother_IsNotGenerated()
	{
		GeneratorResult result = Generator.Run(Library("public sealed class Terminal { internal Task StartAsync() => Task.CompletedTask; }", "[Singleton<Terminal>]") + """

			[Container(Extends = typeof(Shop))]
			[AsyncInitializer<Terminal>(nameof(Terminal.StartAsync))]
			public static partial class SameShop;
			""");

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.Lib.SameShop.g.cs"]).DoesNotContain("static class Internals")
			.Because("no container can extend a container that extends another, so it needs no wrapper");
	}
}
