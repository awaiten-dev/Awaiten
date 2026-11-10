using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     AWT249 is reported by <see cref="AwaitenAnalyzer" /> rather than the generator, so a base container meant only
	///     for extension can suppress it at the attribute.
	/// </summary>
	public class Awt249OpenContributionWithoutClosing
	{
		private const string Types = """
			public interface IHandler<T> { }
			public sealed class OrderHandler : IHandler<int> { }
			public sealed class Logging<T> : IHandler<T> { public Logging(IHandler<T> inner) { } }
			public sealed class Fanout<T> : IHandler<T> { public Fanout(System.Collections.Generic.IEnumerable<IHandler<T>> all) { } }
			""";

		private static string Source(string declarations) => $$"""
			using Awaiten;

			namespace MyCode;

			{{Types}}

			{{declarations}}
			""";

		private static async Task<string[]> Awt249Reports(string source)
			=> (await Analyzer.Run<AwaitenAnalyzer>(source)).Where(diagnostic => diagnostic.Contains("AWT249")).ToArray();

		[Test]
		[Arguments("[Decorate(typeof(Logging<>), typeof(IHandler<>))]", "decorator 'MyCode.Logging<T>'")]
		[Arguments("[Decorate(typeof(Logging<>), typeof(IHandler<>), Keys = new object[] { null })]", "decorator 'MyCode.Logging<T>'")]
		[Arguments("[Composite(typeof(Fanout<>), typeof(IHandler<>))]", "composite 'MyCode.Fanout<T>'")]
		public async Task WithoutAClosing_WarnsAtTheAttribute(string attribute, string subject)
		{
			string source = Source($$"""
				[Container]
				{{attribute}}
				public static partial class MyContainer;
				""");

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt249Reports(source);

			await That(result.Diagnostics).IsEmpty()
				.Because("the generator leaves the warning to the analyzer");
			await That(reports).HasSingle().Which.Satisfies(report => report.Contains("warning AWT249") && report.Contains(subject) && report.Contains("'MyCode.IHandler<T>'"));
		}

		[Test]
		[Arguments("[Decorate(typeof(Logging<>), typeof(IHandler<>))]")]
		[Arguments("[Composite(typeof(Fanout<>), typeof(IHandler<>))]")]
		public async Task WithAClosing_DoesNotWarn(string attribute)
		{
			string[] reports = await Awt249Reports(Source($$"""
				[Container]
				[Transient<OrderHandler, IHandler<int>>]
				{{attribute}}
				public static partial class MyContainer;
				"""));

			await That(reports).IsEmpty();
		}

		[Test]
		public async Task PragmaAroundTheAttributeOfABaseMeantForExtension_SuppressesIt()
		{
			string[] reports = await Awt249Reports(Source("""
				[Container]
				#pragma warning disable AWT249
				[Decorate(typeof(Logging<>), typeof(IHandler<>))]
				#pragma warning restore AWT249
				public static partial class BaseContainer;

				[Container(Extends = typeof(BaseContainer))]
				[Transient<OrderHandler, IHandler<int>>]
				public static partial class MyContainer;
				"""));

			await That(reports).IsEmpty()
				.Because("the base suppresses its own warning, and the extending container registers a closing the inherited decorator wraps");
		}

		[Test]
		public async Task InheritedByAContainerWithoutAClosing_IsReportedOnlyByTheBase()
		{
			string[] reports = await Awt249Reports(Source("""
				[Container]
				[Decorate(typeof(Logging<>), typeof(IHandler<>))]
				public static partial class BaseContainer;

				[Container(Extends = typeof(BaseContainer))]
				public static partial class MyContainer;
				"""));

			await That(reports).HasSingle()
				.Because("the base reports its own declaration, and suppressing it there must not leave a copy on every extending container");
		}

		[Test]
		public async Task DeclaredByAnImportedModule_DoesNotWarn()
		{
			string[] reports = await Awt249Reports(Source("""
				[Module]
				[Decorate(typeof(Logging<>), typeof(IHandler<>))]
				public static partial class LoggingModule;

				[Container]
				[Import(typeof(LoggingModule))]
				public static partial class MyContainer;
				"""));

			await That(reports).IsEmpty()
				.Because("a module declares its open decorator for whatever closings the containers that import it register");
		}

		[Test]
		public async Task ForAClosingDroppedForAReportedReason_DoesNotWarn()
		{
			string source = Source("""
				public interface IRepo<T> { }
				public sealed class Repo<T> : IRepo<T> where T : class { }
				public sealed class LoggingRepo<T> : IRepo<T> { public LoggingRepo(IRepo<T> inner) { } }
				public sealed class Consumer { public Consumer(IRepo<int> repo) { } }

				[Container]
				[Singleton(typeof(Repo<>), typeof(IRepo<>))]
				[Transient<Consumer>]
				[Decorate(typeof(LoggingRepo<>), typeof(IRepo<>))]
				public static partial class MyContainer;
				""");

			string[] reports = await Analyzer.RunExpectingErrors<AwaitenAnalyzer>(source, "AWT126");

			await That(Generator.Run(source).Diagnostics).Contains("*AWT126*").AsWildcard();
			await That(reports).DoesNotContain("*AWT249*").AsWildcard()
				.Because("the closing the decorator would wrap was dropped for the constraint AWT126 reports");
		}

		[Test]
		public async Task AClosedOrKeyedDecorator_KeepsItsError()
		{
			GeneratorResult closed = Generator.Run(Source("""
				[Container]
				[Decorate<Logging<int>, IHandler<int>>]
				public static partial class MyContainer;
				"""));
			GeneratorResult keyed = Generator.Run(Source("""
				[Container]
				[Decorate(typeof(Logging<>), typeof(IHandler<>), Keys = new object[] { "audit" })]
				public static partial class MyContainer;
				"""));

			await That(closed.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT123"));
			await That(keyed.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT210"));
		}
	}
}
