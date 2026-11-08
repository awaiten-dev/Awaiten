using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt244HiddenInjectedProperty
	{
		private static string Library(string members, string type = "public sealed class Greeter : IGreeter")
			=> $$"""
			     using System.Collections.Generic;
			     using Awaiten;

			     namespace Lib;

			     public interface IGreeter { }
			     public interface IClock { }
			     public sealed class SystemClock : IClock { }

			     {{type}}
			     {
			         {{members}}
			     }

			     [Module]
			     [Transient<Greeter, IGreeter>]
			     public static partial class GreetingModule { }
			     """;

		private static string Consumer(string registrations)
			=> $$"""
			     using System.Collections.Generic;
			     using Awaiten;
			     using Lib;

			     namespace App;

			     [Container]
			     [Transient<SystemClock, IClock>]
			     {{registrations}}
			     public static partial class AppContainer { }
			     """;

		private static string Granted(string library)
			=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

		private static string SameAssembly(string library, string registrations)
			=> library + "\n" + Consumer(registrations)
				.Replace("using System.Collections.Generic;", string.Empty)
				.Replace("using Awaiten;", string.Empty)
				.Replace("using Lib;", string.Empty)
				.Replace("namespace App;", string.Empty);

		[Test]
		[Arguments("[Inject] internal IClock Clock { get; set; } = null!;")]
		[Arguments("[Inject(Optional = true)] internal IClock Clock { get; set; } = null!;")]
		[Arguments("[Inject(Deferred = true)] internal IClock Clock { get; set; } = null!;")]
		[Arguments("[Inject] private IClock Clock { get; set; } = null!;")]
		public async Task ADirectRegistrationInAnotherAssembly_IsReported(string members)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(members), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244"))
				.Because("the container cannot see the property in metadata and would build the type without it");
			await That(result.Diagnostics).Contains("*error AWT244*'App.AppContainer'*'Lib.Greeter'*'Clock'*[Module]*InternalsVisibleTo*").AsWildcard();
		}

		[Test]
		[Arguments("[Inject] internal IClock Clock { get; set; } = null!;")]
		[Arguments("[Inject] private IClock Clock { get; set; } = null!;")]
		public async Task ADirectRegistrationInAProjectReferencedAssembly_IsReportedAlike(string members)
		{
			GeneratorResult result = Generator.RunWithGeneratedCompilationReference(Library(members), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244"))
				.Because("the IDE sees the internal property of a referenced project, and reports it as the build does");
		}

		[Test]
		public async Task ADirectRegistrationInTheSameAssembly_FillsTheInternalProperty()
		{
			string library = Library("[Inject] internal IClock Clock { get; set; } = null!;");

			GeneratorResult result = Generator.Run(SameAssembly(library, "[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources.Single(source => source.Key.Contains("AppContainer")).Value).Contains("new global::Lib.Greeter() { Clock = ");
		}

		[Test]
		public async Task ADirectRegistrationInAFriendAssembly_FillsTheInternalProperty()
		{
			string library = Granted(Library("[Inject] internal IClock Clock { get; set; } = null!;"));

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources.Single(source => source.Key.Contains("AppContainer")).Value).Contains("new global::Lib.Greeter() { Clock = ");
		}

		[Test]
		public async Task ARegistrationThroughTheModule_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library("[Inject] internal IClock Clock { get; set; } = null!;"), Consumer("[Import(typeof(GreetingModule))]"));

			await That(result.Diagnostics).IsEmpty()
				.Because("the module's generated factory sets the internal property");
		}

		[Test]
		[Arguments("[Inject] public IClock Clock { get; set; } = null!;")]
		[Arguments("internal IClock Clock { get; set; } = null!;")]
		public async Task AnAccessibleOrPlainProperty_IsNotReported(string members)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(members), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments("[Inject] public IClock Clock { get; internal set; } = null!;")]
		[Arguments("[Inject] private protected IClock Clock { get; set; } = null!;")]
		public async Task AnInternalSetterOrAPrivateProtectedProperty_StaysAwt136(string members)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(members), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"))
				.Because("the container sees the property and reports its unreachable setter itself");
		}

		[Test]
		public async Task AnInheritedHiddenProperty_IsReportedForADerivedTypeInTheContainersAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library("[Inject] internal IClock Clock { get; set; } = null!;", "public class Greeter : IGreeter"),
				Consumer("[Transient<LoudGreeter, IGreeter>]") + "\npublic sealed class LoudGreeter : Greeter { }\n");

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'App.LoudGreeter'"));
		}

		[Test]
		public async Task ADecoratorOfAnotherAssembly_IsReported()
		{
			string library = Library(
				"""
				public LoudGreeter(IGreeter inner) { }

				    [Inject] internal IClock Clock { get; set; } = null!;
				""",
				"public sealed class LoudGreeter : IGreeter").Replace("[Transient<Greeter, IGreeter>]", string.Empty) + "\npublic sealed class Greeter : IGreeter { }\n";

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer("[Transient<Greeter, IGreeter>]\n[Decorate<LoudGreeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'Lib.LoudGreeter'"));
		}

		[Test]
		public async Task AClosingOfAnOpenGenericOfAnotherAssembly_IsReported()
		{
			string library = """
			                 using Awaiten;

			                 namespace Lib;

			                 public interface IClock { }
			                 public sealed class SystemClock : IClock { }
			                 public interface IRepository<T> { }

			                 public sealed class Repository<T> : IRepository<T>
			                 {
			                     [Inject] internal IClock Clock { get; set; } = null!;
			                 }
			                 """;

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer("[Transient(typeof(Repository<>), typeof(IRepository<>))]\n[Transient<Reader>]") + "\npublic sealed class Reader { public Reader(IRepository<string> names) { } }\n");

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'Lib.Repository<string>'"));
		}

		[Test]
		public async Task AnExtendingContainerInAnotherAssembly_IsNotSilent()
		{
			string library = Library("[Inject] internal IClock Clock { get; set; } = null!;")
			                 + "\n[Container]\n[Transient<SystemClock, IClock>]\n[Transient<Greeter, IGreeter>]\npublic static partial class Shop { }\n";

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, """
			                                                                               using Awaiten;
			                                                                               using Lib;

			                                                                               namespace App;

			                                                                               [Container(Extends = typeof(Shop))]
			                                                                               public static partial class TestShop { }
			                                                                               """);

			await That(result.Diagnostics.Any(diagnostic => diagnostic.Contains("AWT244") || diagnostic.Contains("AWT226"))).IsTrue()
				.Because("an extending container that cannot see the property never builds the inherited type without it");
		}
	}
}
