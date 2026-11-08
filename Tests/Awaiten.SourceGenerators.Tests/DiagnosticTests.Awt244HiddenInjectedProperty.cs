using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt244HiddenInjectedProperty
	{
		public enum Reference
		{
			ImplementationAssembly,
			ReferenceAssembly,
			CompilationReference,
		}

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

		private static GeneratorResult Run(Reference reference, string library, string consumer)
			=> reference switch
			{
				Reference.ReferenceAssembly => Generator.RunWithGeneratedReferenceAssembly(library, consumer),
				Reference.CompilationReference => Generator.RunWithGeneratedCompilationReference(library, consumer),
				_ => Generator.RunWithGeneratedReferencedAssembly(library, consumer),
			};

		[Test]
		[Arguments(Reference.ImplementationAssembly, "[Inject] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ImplementationAssembly, "[Inject(Optional = true)] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ImplementationAssembly, "[Inject(Deferred = true)] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject(Optional = true)] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject(Deferred = true)] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.CompilationReference, "[Inject] internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.CompilationReference, "[Inject(Optional = true)] internal IClock Clock { get; set; } = null!;")]
		public async Task ADirectRegistrationInAnotherAssembly_IsReported(Reference reference, string members)
		{
			GeneratorResult result = Run(reference, Library(members), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244"))
				.Because("the container cannot reach the property and would build the type without it, whether the reference shows it or not");
			await That(result.Diagnostics).Contains("*error AWT244*'App.AppContainer'*'Lib.Greeter'*'Clock'*'ReferencedAssembly'*[Module]*InternalsVisibleTo*").AsWildcard();
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly, "[Inject] private protected IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ImplementationAssembly, "[Inject] private IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ImplementationAssembly, "[Inject] internal IClock Clock { get; private set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject] private protected IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject] private IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject] internal IClock Clock { get; private set; } = null!;")]
		[Arguments(Reference.CompilationReference, "[Inject] private protected IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.CompilationReference, "[Inject] private IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.CompilationReference, "[Inject] internal IClock Clock { get; private set; } = null!;")]
		public async Task AnUnassignableProperty_IsAwt136AsInItsOwnAssembly(Reference reference, string members)
		{
			string library = Library(members, "public class Greeter : IGreeter");

			GeneratorResult other = Run(reference, library, Consumer("[Transient<Greeter, IGreeter>]"));
			GeneratorResult same = Generator.Run(SameAssembly(library, "[Transient<Greeter, IGreeter>]"));

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136") && diagnostic.Contains("'Clock'"))
				.Because("no container can assign the property, so neither a module nor InternalsVisibleTo would help, whether the reference shows it or not");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"));
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
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task ADirectRegistrationInAFriendAssembly_FillsTheInternalProperty(Reference reference)
		{
			string library = Granted(Library("[Inject] internal IClock Clock { get; set; } = null!;"));

			GeneratorResult result = Run(reference, library, Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources.Single(source => source.Key.Contains("AppContainer")).Value).Contains("new global::Lib.Greeter() { Clock = ");
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task APrivatePropertyInAFriendAssembly_IsReported(Reference reference)
		{
			string library = Granted(Library("[Inject] private IClock Clock { get; set; } = null!;"));

			GeneratorResult result = Run(reference, library, Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"))
				.Because("InternalsVisibleTo shows no private member to another assembly, and no container can assign it");
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task ARegistrationThroughTheModule_IsNotReported(Reference reference)
		{
			GeneratorResult result = Run(reference, Library("[Inject] internal IClock Clock { get; set; } = null!;"), Consumer("[Import(typeof(GreetingModule))]"));

			await That(result.Diagnostics).IsEmpty()
				.Because("the module's generated factory sets the internal property");
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly, "[Inject] public IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ImplementationAssembly, "internal IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "[Inject] public IClock Clock { get; set; } = null!;")]
		[Arguments(Reference.ReferenceAssembly, "internal IClock Clock { get; set; } = null!;")]
		public async Task AnAccessibleOrPlainProperty_IsNotReported(Reference reference, string members)
		{
			GeneratorResult result = Run(reference, Library(members), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		[Arguments(Reference.CompilationReference)]
		public async Task AnExplicitInterfaceImplementation_IsAwt136AsInItsOwnAssembly(Reference reference)
		{
			string library = Library("[Inject] IClock IHasClock.Clock { get; set; }", "public sealed class Greeter : IGreeter, IHasClock")
				                 .Replace("[Module]", string.Empty).Replace("[Transient<Greeter, IGreeter>]", string.Empty)
			                 + "\npublic interface IHasClock { IClock Clock { get; set; } }\n";

			GeneratorResult other = Run(reference, library, Consumer("[Transient<Greeter, IGreeter>]"));
			GeneratorResult same = Generator.Run(SameAssembly(library, "[Transient<Greeter, IGreeter>]"));

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136") && diagnostic.Contains("'Lib.IHasClock.Clock'"))
				.Because("no container can assign an explicit interface implementation, so making it public or registering it through a module would not help");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136") && diagnostic.Contains("'Lib.IHasClock.Clock'"));
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		[Arguments(Reference.CompilationReference)]
		public async Task AnInternalSetter_StaysAwt136(Reference reference)
		{
			GeneratorResult result = Run(reference, Library("[Inject] public IClock Clock { get; internal set; } = null!;"), Consumer("[Transient<Greeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"))
				.Because("the container sees the public property and reports its unreachable setter itself");
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task AnInheritedHiddenProperty_IsReportedForADerivedTypeInTheContainersAssembly(Reference reference)
		{
			GeneratorResult result = Run(reference,
				Library("[Inject] internal IClock Clock { get; set; } = null!;", "public class Greeter : IGreeter"),
				Consumer("[Transient<LoudGreeter, IGreeter>]") + "\npublic sealed class LoudGreeter : Greeter { }\n");

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'App.LoudGreeter'"));
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task AHiddenPropertyShadowedByAnInjectedOne_IsNotReported(Reference reference)
		{
			GeneratorResult result = Run(reference,
				Library("[Inject] internal IClock Clock { get; set; } = null!;", "public class Greeter : IGreeter"),
				Consumer("[Transient<LoudGreeter, IGreeter>]") + "\npublic sealed class LoudGreeter : Greeter { [Inject] public IClock Clock { get; set; } = null!; }\n");

			await That(result.Diagnostics).IsEmpty()
				.Because("a container in the library's assembly also fills only the most derived property of a name");
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task ADecoratorOfAnotherAssembly_IsReported(Reference reference)
		{
			string library = Library(
				"""
				public LoudGreeter(IGreeter inner) { }

				    [Inject] internal IClock Clock { get; set; } = null!;
				""",
				"public sealed class LoudGreeter : IGreeter").Replace("[Transient<Greeter, IGreeter>]", string.Empty) + "\npublic sealed class Greeter : IGreeter { }\n";

			GeneratorResult result = Run(reference, library, Consumer("[Transient<Greeter, IGreeter>]\n[Decorate<LoudGreeter, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'Lib.LoudGreeter'"));
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task AClosingOfAnOpenGenericOfAnotherAssembly_IsReported(Reference reference)
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

			GeneratorResult result = Run(reference, library, Consumer("[Transient(typeof(Repository<>), typeof(IRepository<>))]\n[Transient<Reader>]") + "\npublic sealed class Reader { public Reader(IRepository<string> names) { } }\n");

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'Lib.Repository<string>'"));
		}

		[Test]
		[Arguments(Reference.ImplementationAssembly)]
		[Arguments(Reference.ReferenceAssembly)]
		public async Task ANestedTypeOfAnotherAssembly_IsReported(Reference reference)
		{
			string library = Library("[Inject] internal IClock Clock { get; set; } = null!;")
			                 + "\npublic static class Outer { public sealed class Inner : IGreeter { [Inject] internal IClock Clock { get; set; } = null!; } }\n";

			GeneratorResult result = Run(reference, library, Consumer("[Transient<Outer.Inner, IGreeter>]"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT244") && diagnostic.Contains("'Lib.Outer.Inner'"));
		}

		[Test]
		public async Task TheLibrary_ListsOnlyThePropertiesOtherAssembliesCannotSee()
		{
			GeneratorResult result = Generator.Run(Library(
				"""
				[Inject] internal IClock Clock { get; set; } = null!;
				    [Inject] private IClock Hidden { get; set; } = null!;
				    [Inject] public IClock Other { get; set; } = null!;
				    internal IClock Plain { get; set; } = null!;
				""").Replace("[Module]", string.Empty).Replace("[Transient<Greeter, IGreeter>]", string.Empty));

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources.Single(source => source.Key.Contains("HiddenInjectedProperties")).Value)
				.Contains("[assembly: global::Awaiten.GeneratedHiddenInjectedProperty(\"Lib.Greeter\", \"Clock\", true)]")
				.And.Contains("[assembly: global::Awaiten.GeneratedHiddenInjectedProperty(\"Lib.Greeter\", \"Hidden\", false)]")
				.And.DoesNotContain("\"Other\"").And.DoesNotContain("\"Plain\"");
		}

		[Test]
		public async Task ALibraryWithoutHiddenProperties_GeneratesNoMarker()
		{
			GeneratorResult result = Generator.Run(Library("[Inject] public IClock Clock { get; set; } = null!;"));

			await That(result.Sources.Keys.Any(key => key.Contains("HiddenInjectedProperties"))).IsFalse()
				.Because("a library whose [Inject] properties every assembly sees has nothing to tell a container");
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
