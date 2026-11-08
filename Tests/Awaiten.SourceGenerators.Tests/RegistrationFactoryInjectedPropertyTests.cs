using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The <c>[Inject]</c> properties of an implementation a module builds through its generated factory, for a
///     container in the module's assembly, one in another assembly and one in an assembly the module grants
///     <c>InternalsVisibleTo</c>: each shape is filled, or rejected, alike.
/// </summary>
public class RegistrationFactoryInjectedPropertyTests
{
	private const string Consumer = """
	                                using Awaiten;
	                                using Lib;

	                                namespace App;

	                                [Container]
	                                [Import(typeof(GreetingModule))]
	                                public static partial class AppContainer { }
	                                """;

	private const string ClockConsumer = """
	                                     using Awaiten;
	                                     using Lib;

	                                     namespace App;

	                                     [Container]
	                                     [Import(typeof(GreetingModule))]
	                                     [Transient<SystemClock, IClock>]
	                                     public static partial class AppContainer { }
	                                     """;

	private static string Granted(string library)
		=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

	private static string GrantedToAnother(string library)
		=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"AnotherAssembly\")]");

	private static string WithLibraryContainer(string library, bool clock)
		=> library + (clock
			? "\n[Container]\n[Import(typeof(GreetingModule))]\n[Transient<SystemClock, IClock>]\npublic static partial class LibContainer { }\n"
			: "\n[Container]\n[Import(typeof(GreetingModule))]\npublic static partial class LibContainer { }\n");

	private static string ModuleSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.StartsWith("Awaiten.ModuleScan.", StringComparison.Ordinal)).Value;

	private static string ContainerSource(GeneratorResult result, string container)
		=> result.Sources.Single(source => source.Key.Contains(container)).Value;

	private static Containers RunEverywhere(string library, bool clock)
		=> new(
			Generator.Run(WithLibraryContainer(library, clock)),
			Generator.RunWithGeneratedReferencedAssembly(library, clock ? ClockConsumer : Consumer),
			Generator.RunWithGeneratedReferencedAssembly(Granted(library), clock ? ClockConsumer : Consumer));

	private sealed record Containers(GeneratorResult Same, GeneratorResult Other, GeneratorResult Friend)
	{
		public string[] AllDiagnostics => [..Same.Diagnostics, ..Other.Diagnostics, ..Friend.Diagnostics,];
	}

	private static async Task<Analyses> AnalyzeEverywhere(string library, bool clock)
		=> await AnalyzeEverywhere(WithLibraryContainer(library, clock), library, clock ? ClockConsumer : Consumer);

	private static async Task<Analyses> AnalyzeEverywhere(string same, string library, string consumer)
		=> new(
			await Analyzer.Run<AwaitenAnalyzer>(same),
			await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(library, consumer),
			await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(Granted(library), consumer));

	private sealed record Analyses(string[] Same, string[] Other, string[] Friend)
	{
		public string[] AllDiagnostics => [..Same, ..Other, ..Friend,];
	}

	private static string Library(string members, string type = "internal sealed class Greeter : IGreeter")
		=> $$"""
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

	public class OptionalProperty
	{
		private static readonly string Module = Library("[Inject(Optional = true)] public IClock Clock { get; set; } = null!;");

		[Test]
		public async Task TheFactory_TakesAPresenceFlagBesideTheValue()
		{
			GeneratorResult result = Generator.Run(Module);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("global::Lib.IClock @Clock = default!, bool awaiten__has_Clock = false)")
				.And.Contains("if (awaiten__has_Clock)")
				.Because("a registered service that resolves to null must still be assigned, which only a separate flag tells apart from a missing one");
		}

		[Test]
		public async Task ARegisteredService_IsPassedAsPresentEverywhere()
		{
			Containers containers = RunEverywhere(Module, clock: true);

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("Registration_Greeter_*(*, true)").AsWildcard()
				.Because("the consumer resolves the registered clock and marks it present");
		}

		[Test]
		public async Task AMissingService_IsPassedAsAbsentEverywhere()
		{
			Containers containers = RunEverywhere(Module, clock: false);

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("Registration_Greeter_*(default!, default!)").AsWildcard()
				.Because("the consumer passes neither a value nor the flag for a service it does not register");
		}

		[Test]
		public async Task AMissingService_IsNotImportedFromTheHostEverywhere()
		{
			Containers containers = new(
				Generator.Run(WithLibraryContainer(Module, clock: false).Replace("[Import(typeof(GreetingModule))]", "[Import(typeof(GreetingModule))]\n[ImportServices]")),
				Generator.RunWithGeneratedReferencedAssembly(Module, Consumer.Replace("[Import(typeof(GreetingModule))]", "[Import(typeof(GreetingModule))]\n[ImportServices]")),
				Generator.RunWithGeneratedReferencedAssembly(Granted(Module), Consumer.Replace("[Import(typeof(GreetingModule))]", "[Import(typeof(GreetingModule))]\n[ImportServices]")));

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("Registration_Greeter_*(default!, default!)").AsWildcard()
				.Because("a container that constructs the type itself leaves a missing optional property as it is rather than asking the host for it");
		}

		[Test]
		[Arguments("[Inject(Optional = true)] public IClock A { get; set; } = null!;", "[Inject(Optional = true)] public IClock awaiten__has_A { get; set; } = null!;")]
		[Arguments("[Inject(Optional = true)] public IClock awaiten__has_A { get; set; } = null!;", "[Inject(Optional = true)] public IClock A { get; set; } = null!;")]
		[Arguments("[Inject(Optional = true)] public IClock A { get; init; } = null!;", "[Inject] public IClock awaiten__init_A { get; set; } = null!;")]
		public async Task NamedLikeThePresenceFlagOfAnother_IsReportedByTheModuleInAnyOrder(string first, string second)
		{
			GeneratorResult result = Generator.Run(Library($"{first}\n{second}"));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200") && diagnostic.Contains("presence flag"))
				.Because("the factory would declare two parameters of the same name, so the module reports the property instead of emitting code that does not compile");
		}

		[Test]
		[Arguments("[Inject(Optional = true)] public IClock awaiten__instance { get; set; } = null!;")]
		[Arguments("[Inject(Deferred = true)] public IClock awaiten__instance { get; set; } = null!;\n[Inject(Optional = true)] public IClock Backup { get; set; } = null!;")]
		public async Task NamedLikeTheInstanceOfTheFactory_IsFilledEverywhere(string members)
		{
			Containers containers = RunEverywhere(Library(members), clock: true);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("the generated factory and setter pick names for their own locals and parameters that no property takes");
		}

		[Test]
		public async Task NamedWithThePrefixOfAPresenceFlag_IsResolvedLikeAnyOtherProperty()
		{
			string module = Library("[Inject(Optional = true)] public bool awaiten__has_Ready { get; set; }");

			Containers containers = RunEverywhere(module, clock: false);

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("Registration_Greeter_*(default!, default!)").AsWildcard()
				.Because("only the parameter right after the value it flags is a presence flag, so a missing optional bool is passed as absent, not as a present flag");
		}

		[Test]
		[Arguments("public required System.Collections.Generic.IEnumerable<IClock> Clocks { get; set; }")]
		[Arguments("public required System.Collections.Generic.IEnumerable<IClock> Clocks { get; init; }")]
		public async Task OfARequiredCollection_IsFilledEverywhere(string property)
		{
			Containers containers = RunEverywhere(Library($"[Inject(Optional = true)] {property}"), clock: false);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("a collection is always filled, empty without a registration, so the factory sets a required one like a container in the module's assembly");
		}

		[Test]
		public async Task OfACollectionOfAPrivateNestedType_IsPassedWithoutAFlagByAContainerInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace Lib;

			                                       public interface IGreeter { }
			                                       public interface IClock { }

			                                       [Module]
			                                       [Transient<GreetingModule.Greeter, IGreeter>]
			                                       public static partial class GreetingModule
			                                       {
			                                           private sealed class Greeter : IGreeter
			                                           {
			                                               [Inject(Optional = true)] public System.Collections.Generic.IEnumerable<IClock> Clocks { get; set; } = null!;
			                                           }
			                                       }

			                                       [Container]
			                                       [Import(typeof(GreetingModule))]
			                                       public static partial class LibContainer { }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the factory takes an optional collection as a plain parameter, which has no presence flag to pass");
		}

		[Test]
		public async Task OfAPrivateNestedType_IsMarkedPresentByAContainerInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace Lib;

			                                       public interface IGreeter { }
			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }

			                                       [Module]
			                                       [Transient<GreetingModule.Greeter, IGreeter>]
			                                       public static partial class GreetingModule
			                                       {
			                                           private sealed class Greeter : IGreeter
			                                           {
			                                               [Inject(Optional = true)] public IClock Clock { get; set; } = null!;
			                                           }
			                                       }

			                                       [Container]
			                                       [Import(typeof(GreetingModule))]
			                                       [Transient<SystemClock, IClock>]
			                                       public static partial class LibContainer { }
			                                       """);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "LibContainer")).Contains("awaiten__has_Clock: true")
				.Because("the container passes the property and its presence flag to the module's factory by name");
		}
	}

	public class InitOnlyOptionalProperty
	{
		private static readonly string Module = Library("[Inject(Optional = true)] public IClock Clock { get; init; } = null!;");

		[Test]
		public async Task TheFactory_AssignsItInTheObjectInitializerOnlyWhenPresent()
		{
			GeneratorResult result = Generator.Run(Module);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("awaiten__init_Clock ? new global::Lib.Greeter() { Clock = @Clock } : new global::Lib.Greeter()")
				.Because("an init-only property can only be assigned while the instance is constructed");
		}

		[Test]
		public async Task IsFilledEverywhere_WithTheSameWarning()
		{
			Containers containers = RunEverywhere(Module, clock: true);
			Analyses analyses = await AnalyzeEverywhere(Module, clock: true);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("the analyzer reports the warning, so it can be suppressed in source");
			await That(analyses.Same).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"));
			await That(analyses.Other).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"))
				.Because("a container in another assembly warns about the init-only optional property like one that sees it");
			await That(analyses.Friend).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"));
		}

		[Test]
		public async Task TheWarningOfAContainerInAnotherAssembly_IsReportedAtItsImport()
		{
			Analyses analyses = await AnalyzeEverywhere(WithLibraryContainer(Module, clock: true), Module, ClockConsumer);

			await That(analyses.Other).HasSingle().Matching(diagnostic => diagnostic.StartsWith("(7,2): warning AWT158", StringComparison.Ordinal))
				.Because("a warning with a location can be suppressed where it arises instead of for the whole project");
			await That(analyses.Friend).HasSingle().Matching(diagnostic => diagnostic.StartsWith("(7,2): warning AWT158", StringComparison.Ordinal))
				.Because("a property read from another assembly has no location of its own");
		}

		[Test]
		public async Task TheWarning_CanBeSuppressedAtTheImport()
		{
			string suppressed = ClockConsumer.Replace("[Import(typeof(GreetingModule))]", "#pragma warning disable AWT158\n[Import(typeof(GreetingModule))]\n#pragma warning restore AWT158");

			Analyses analyses = await AnalyzeEverywhere(WithLibraryContainer(Module, clock: true), Module, suppressed);

			await That(analyses.Other).IsEmpty();
			await That(analyses.Friend).IsEmpty();
		}

		[Test]
		public async Task OfAnExplicitlyRegisteredCollection_IsFilledEverywhereWithoutAWarning()
		{
			string module = Library("[Inject(Optional = true)] public System.Collections.Generic.IEnumerable<IClock> Clocks { get; init; } = null!;");
			const string registration = "[Transient<System.Collections.Generic.IEnumerable<IClock>>(Factory = nameof(Clocks))]";
			const string factory = "{ private static System.Collections.Generic.IEnumerable<IClock> Clocks() => []; }";

			Analyses analyses = await AnalyzeEverywhere(
				WithLibraryContainer(module, clock: false).Replace("public static partial class LibContainer { }", $"{registration}\npublic static partial class LibContainer {factory}"),
				module,
				Consumer.Replace("public static partial class AppContainer { }", $"{registration}\npublic static partial class AppContainer {factory}"));

			await That(analyses.AllDiagnostics).IsEmpty()
				.Because("a collection-typed property is never omitted, whether it is synthesized or registered as a whole");
		}

		[Test]
		public async Task OfACollection_IsFilledEverywhereWithoutAWarning()
		{
			string module = Library("[Inject(Optional = true)] public System.Collections.Generic.IEnumerable<IClock> Clocks { get; init; } = null!;");

			Containers containers = RunEverywhere(module, clock: false);
			Analyses analyses = await AnalyzeEverywhere(module, clock: false);

			await That([..containers.AllDiagnostics, ..analyses.AllDiagnostics,]).IsEmpty()
				.Because("a collection is always filled, empty without a registration, so its init-only accessor loses nothing");
		}

		[Test]
		public async Task OfAPrivateNestedType_IsMarkedPresentByAContainerInTheSameAssembly()
		{
			const string source = """
			                      using Awaiten;

			                      namespace Lib;

			                      public interface IGreeter { }
			                      public interface IClock { }
			                      public sealed class SystemClock : IClock { }

			                      [Module]
			                      [Transient<GreetingModule.Greeter, IGreeter>]
			                      public static partial class GreetingModule
			                      {
			                          private sealed class Greeter : IGreeter
			                          {
			                              [Inject(Optional = true)] public IClock Clock { get; init; } = null!;
			                          }
			                      }

			                      [Container]
			                      [Import(typeof(GreetingModule))]
			                      [Transient<SystemClock, IClock>]
			                      public static partial class LibContainer { }
			                      """;

			GeneratorResult result = Generator.Run(source);

			await That(result.Diagnostics).IsEmpty();
			await That(await Analyzer.Run<AwaitenAnalyzer>(source)).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"));
			await That(ContainerSource(result, "LibContainer")).Contains("awaiten__init_Clock: true");
		}

		[Test]
		public async Task SeveralOfThem_AreCombined()
		{
			GeneratorResult result = Generator.Run(Library("""
			                                               [Inject(Optional = true)] public IClock Clock { get; init; } = null!;
			                                                   [Inject(Optional = true)] public IClock Backup { get; init; } = null!;
			                                               """));

			await That(result.Diagnostics).IsEmpty()
				.Because("each combination of present init-only properties gets its own object initializer");
		}

		[Test]
		public async Task FourOfThem_AreFilledEverywhere()
		{
			Containers containers = RunEverywhere(Library(InitOnlyClocks(4)), clock: false);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("four init-only optional properties need sixteen object initializers, which the factory still emits");
		}

		[Test]
		public async Task MoreThanFour_AreReportedByTheModule()
		{
			GeneratorResult result = Generator.Run(Library(InitOnlyClocks(5)));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200"));
			await That(result.Diagnostics).Contains("*AWT200*Lib.Greeter*'Clock4' is one of more than 4 init-only optional [Inject] properties*").AsWildcard()
				.Because("each further init-only optional property doubles the object initializers the factory needs");
		}

		private static string InitOnlyClocks(int count)
			=> string.Join("\n", Enumerable.Range(0, count).Select(index => $"[Inject(Optional = true)] public IClock Clock{index} {{ get; init; }} = null!;"));
	}

	public class OptionalPropertyOfAnInternalService
	{
		[Test]
		public async Task IsFilledThroughTheHandleEverywhere()
		{
			string module = """
			                using Awaiten;

			                namespace Lib;

			                public interface IGreeter { }
			                public interface IClock { }
			                public sealed class SystemClock : IClock { }
			                internal interface IKettle { }
			                internal sealed class Kettle : IKettle { }

			                internal sealed class Greeter : IGreeter
			                {
			                    [Inject(Optional = true)] public IKettle Kettle { get; init; } = null!;
			                    [Inject(Optional = true)] public IKettle Spare { get; set; } = null!;
			                    [Inject(Optional = true, Deferred = true)] public IKettle Late { get; set; } = null!;
			                }

			                [Module]
			                [Singleton<Kettle, IKettle>]
			                [Transient<Greeter, IGreeter>]
			                public static partial class GreetingModule { }
			                """;

			Containers containers = RunEverywhere(module, clock: false);
			Analyses analyses = await AnalyzeEverywhere(module, clock: false);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("the factory takes the internal service through its handle, whose value it assigns when the flag says the handle is present");
			await That(analyses.Same).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"));
			await That(analyses.Other).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"));
			await That(analyses.Friend).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT158"));
		}
	}

	public class ValueTypeOptionalProperty
	{
		private static readonly string Module = Library("[Inject(Optional = true)] public Owned<IClock> Clock { get; set; }");

		[Test]
		public async Task TheFactory_TakesTheValueTypeItself()
		{
			GeneratorResult result = Generator.Run(Module);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("global::Awaiten.Owned<global::Lib.IClock> @Clock = default!, bool awaiten__has_Clock = false");
		}

		[Test]
		public async Task ARegisteredService_IsFilledEverywhere()
		{
			Containers containers = RunEverywhere(Module, clock: true);

			await That(containers.AllDiagnostics).IsEmpty();
		}

		[Test]
		public async Task AMissingService_IsLeftOutEverywhere()
		{
			Containers containers = RunEverywhere(Module, clock: false);

			await That(containers.AllDiagnostics).IsEmpty();
		}
	}

	public class OptionalDeferredProperty
	{
		private static readonly string Module = Library("[Inject(Optional = true, Deferred = true)] public IClock Clock { get; set; } = null!;");

		[Test]
		public async Task TheSetter_TakesAnOptionalValue()
		{
			GeneratorResult result = Generator.Run(Module);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("_Clock(global::Lib.IGreeter @awaiten__instance, global::Lib.IClock @Clock = default!)")
				.Because("the default tells a consumer that the property is optional, so it calls the setter only for a registered service");
		}

		[Test]
		public async Task ARegisteredService_IsAssignedEverywhere()
		{
			Containers containers = RunEverywhere(Module, clock: true);

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("Deferred_Registration_Greeter_");
		}

		[Test]
		public async Task AMissingService_IsLeftOutEverywhere()
		{
			Containers containers = RunEverywhere(Module, clock: false);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("a missing optional service is no error, deferred or not");
			await That(ContainerSource(containers.Other, "AppContainer")).DoesNotContain("Deferred_Registration_Greeter_");
		}
	}

	public class RejectedShapes
	{
		[Test]
		[Arguments("[Inject(Optional = true)] public required IClock Clock { get; set; }", "AWT157", "*Optional*required*")]
		[Arguments("[Inject(Deferred = true)] public IClock Clock { get; init; } = null!;", "AWT144", "*Deferred*init-only*")]
		[Arguments("[Inject(Deferred = true)] public required IClock Clock { get; set; }", "AWT144", "*Deferred*required*")]
		public async Task AShapeNoContainerSupports_IsReportedByTheModuleAndTheContainer(string members, string containerId, string reason)
		{
			string module = Library(members);

			GeneratorResult own = Generator.Run(module);
			GeneratorResult same = Generator.Run(WithLibraryContainer(module, clock: true));

			await That(own.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200"));
			await That(own.Diagnostics).Contains($"*AWT200*Lib.Greeter*'Clock'{reason}which no container supports*").AsWildcard();
			await That(same.Diagnostics).Contains($"*{containerId}*").AsWildcard()
				.Because("a container in the module's assembly rejects the property itself");
		}
	}

	public class InaccessibleProperty
	{
		private const string UserType = "public class Greeter : IGreeter";

		[Test]
		[Arguments("[Inject] internal IClock Clock { get; set; } = null!;")]
		[Arguments("[Inject] public IClock Clock { get; internal set; } = null!;")]
		[Arguments("[Inject] protected internal IClock Clock { get; set; } = null!;")]
		public async Task OfAPublicImplementation_IsSetEverywhere(string members)
		{
			string module = Library(members, UserType);

			Containers containers = RunEverywhere(module, clock: true);

			await That(containers.AllDiagnostics).IsEmpty()
				.Because("an [Inject] property another assembly cannot set is never silently left out");
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
				.Because("a container in another assembly builds the implementation through the module's factory, which sets the property");
			await That(ContainerSource(containers.Friend, "AppContainer")).Contains("Clock = ")
				.Because("a friend assembly sets the property itself");
			await That(ContainerSource(containers.Same, "LibContainer")).Contains("Clock = ");
		}

		[Test]
		public async Task OfAnInternalServiceType_IsSetEverywhere()
		{
			string module = """
			                using Awaiten;

			                namespace Lib;

			                public interface IGreeter { }
			                internal interface IKettle { }
			                internal sealed class Kettle : IKettle { }
			                public sealed class SystemClock { }
			                public interface IClock { }

			                public sealed class Greeter : IGreeter
			                {
			                    [Inject] internal IKettle Kettle { get; set; } = null!;
			                }

			                [Module]
			                [Singleton<Kettle, IKettle>]
			                [Transient<Greeter, IGreeter>]
			                public static partial class GreetingModule { }
			                """;

			Containers containers = RunEverywhere(module, clock: false);

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_");
		}

		[Test]
		[Arguments("[Inject] private IClock Clock { get; set; } = null!;")]
		[Arguments("[Inject] private protected IClock Clock { get; set; } = null!;")]
		public async Task ThatNoContainerCanSet_IsReportedByTheModuleEvenWithInternalsVisibleTo(string members)
		{
			string module = Library(members, UserType);

			GeneratorResult own = Generator.Run(module);
			GeneratorResult granted = Generator.Run(Granted(module));
			GeneratorResult same = Generator.Run(WithLibraryContainer(module, clock: true));

			await That(own.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200") && diagnostic.Contains("'Clock' has no setter"));
			await That(granted.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200"))
				.Because("a friend assembly cannot see a private property either, so it would silently leave it out");
			await That(same.Diagnostics).Contains("*AWT136*").AsWildcard();
		}

		[Test]
		public async Task OfAnInternalTypeTheFactoryCannotTake_IsReportedWithTheRemedyForThePublicImplementation()
		{
			string module = Library("[Inject] internal Helper Helper { get; set; } = null!;", UserType)
				.Replace("public sealed class SystemClock", "internal sealed class Helper { }\npublic sealed class SystemClock");

			GeneratorResult result = Generator.Run(module);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200"));
			await That(result.Diagnostics).Contains("*'Lib.Greeter' is self-compiled from a module registration because its [Inject] property 'Helper' is not public*make the [Inject] property 'Helper', its setter and its type public*").AsWildcard()
				.Because("the implementation and its constructor are public already, so only the hidden property keeps another assembly from constructing it");
		}

		[Test]
		public async Task OfAPublicImplementationWithARecordedSkip_IsReportedWithTheRealCause()
		{
			string module = GrantedToAnother(Library("""
			                                         public Greeter(IClock Clock) { }

			                                             [Inject] internal IClock Clock { get; set; } = null!;
			                                         """, UserType));

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(module, ClockConsumer);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231"));
			await That(result.Diagnostics).Contains("*registers 'Lib.Greeter', whose [Inject] property this container does not see*make the [Inject] properties of 'Lib.Greeter', their setters and their types public*").AsWildcard()
				.Because("the container can construct the public implementation, but not set the property it does not see");
		}

		[Test]
		public async Task WithAProtectedSetter_IsReportedByEveryContainer()
		{
			string module = Library("[Inject] public IClock Clock { get; protected set; } = null!;", "public class Greeter : IGreeter");

			Containers containers = RunEverywhere(module, clock: true);

			await That(Generator.Run(module).Diagnostics).IsEmpty()
				.Because("another assembly sees the protected setter, so its container reports it like one in the module's assembly");
			await That(containers.Same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"));
			await That(containers.Other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"));
			await That(containers.Friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT136"));
		}

		[Test]
		public async Task BesideAContainerSideEntry_IsReportedByAContainerInAnotherAssembly()
		{
			string module = Library("""
			                        [Inject] internal IClock Clock { get; set; } = null!;
			                            public IClock Backup { get; set; } = null!;
			                        """, UserType);

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(module, ClockConsumer.Replace("[Transient<SystemClock, IClock>]", "[InjectProperty<Greeter>(nameof(Greeter.Backup))]\n[Transient<SystemClock, IClock>]"));

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT178"))
				.Because("the module's factory builds the implementation whole, so the container reports the entry it cannot fill instead of leaving it out");
		}
		[Test]
		public async Task OfAPublicDecorator_IsSetEverywhere()
		{
			string module = """
			                using Awaiten;

			                namespace Lib;

			                public interface IGreeter { }
			                public interface IClock { }
			                public sealed class SystemClock : IClock { }
			                public sealed class Greeter : IGreeter { }

			                public sealed class LoudGreeter : IGreeter
			                {
			                    public LoudGreeter(IGreeter inner) { }

			                    [Inject] internal IClock Clock { get; set; } = null!;
			                }

			                [Module]
			                [Transient<Greeter, IGreeter>]
			                [Decorate<LoudGreeter, IGreeter>]
			                public static partial class GreetingModule { }
			                """;

			Containers containers = RunEverywhere(module, clock: true);

			await That(containers.AllDiagnostics).IsEmpty();
			await That(ContainerSource(containers.Other, "AppContainer")).Contains("global::Lib.GreetingModule.Internals.Decorator_LoudGreeter_")
				.Because("a container in another assembly builds the decorator through the module's factory, which sets the property");
			await That(ContainerSource(containers.Friend, "AppContainer")).Contains("Clock = ");
		}

		[Test]
		public async Task OfAPublicDecoratorTheFactoryCannotMirror_IsReportedByTheModuleWithoutInternalsVisibleTo()
		{
			const string module = """
			                      using Awaiten;

			                      namespace Lib;

			                      public interface IGreeter { }
			                      public interface IClock { }
			                      public sealed class Greeter : IGreeter { }

			                      public sealed class LoudGreeter : IGreeter
			                      {
			                          public LoudGreeter(IGreeter inner, IClock Clock) { }

			                          [Inject] internal IClock Clock { get; set; } = null!;
			                      }

			                      [Module]
			                      [Transient<Greeter, IGreeter>]
			                      [Decorate<LoudGreeter, IGreeter>]
			                      public static partial class GreetingModule { }
			                      """;

			GeneratorResult result = Generator.Run(module);
			GeneratorResult granted = Generator.Run(Granted(module));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT200") && diagnostic.Contains("has the name of a constructor parameter"))
				.Because("a container in another assembly constructs the public decorator itself and would silently leave out the property it does not see");
			await That(granted.Diagnostics).IsEmpty()
				.Because("a friend constructs the decorator itself, so the module records the reason for a container without the grant");
		}

		[Test]
		public async Task OfAPublicDecoratorOfAModuleThatIsNotPartial_IsReported()
		{
			const string module = """
			                      using Awaiten;

			                      namespace Lib;

			                      public interface IGreeter { }
			                      public interface IClock { }
			                      public sealed class Greeter : IGreeter { }

			                      public sealed class LoudGreeter : IGreeter
			                      {
			                          public LoudGreeter(IGreeter inner) { }

			                          [Inject] internal IClock Clock { get; set; } = null!;
			                      }

			                      [Module]
			                      [Transient<Greeter, IGreeter>]
			                      [Decorate<LoudGreeter, IGreeter>]
			                      public static class GreetingModule { }
			                      """;

			GeneratorResult result = Generator.Run(module);
			GeneratorResult granted = Generator.Run(Granted(module));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT194") && diagnostic.Contains("whose [Inject] property another assembly cannot set"))
				.Because("a container in another assembly constructs the public decorator itself, so only the module's factory can set the property");
			await That(granted.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT194"));
		}

		[Test]
		public async Task OfAModuleThatIsNotPartial_IsReportedEvenWithInternalsVisibleTo()
		{
			GeneratorResult result = Generator.Run(Granted(Library("[Inject] internal IClock Clock { get; set; } = null!;", UserType).Replace("public static partial class", "public static class")));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT194") && diagnostic.Contains("whose [Inject] property another assembly cannot set"))
				.Because("a container without InternalsVisibleTo constructs the public implementation itself, so only the module's factory can set the property");
		}
	}

	public class PublicDecoratorOfALibraryWithInternalsVisibleTo
	{
		private const string Module = """
		                              using Awaiten;

		                              namespace Lib;

		                              public interface IGreeter { }
		                              internal sealed class Helper { }
		                              public sealed class Greeter : IGreeter { }

		                              public sealed class LoudGreeter : IGreeter
		                              {
		                                  public LoudGreeter(IGreeter inner) { }

		                                  [Inject] internal Helper Helper { get; set; } = null!;
		                              }

		                              [Module]
		                              [Transient<Greeter, IGreeter>]
		                              [Decorate<LoudGreeter, IGreeter>]
		                              public static partial class GreetingModule { }

		                              [Container]
		                              [Import(typeof(GreetingModule))]
		                              [Transient<Helper>]
		                              internal static partial class LibContainer { }
		                              """;

		[Test]
		public async Task IsBuiltByTheLibraryAndAFriend()
		{
			GeneratorResult library = Generator.Run(Granted(Module));
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(Module), """
			                                                                                     using Awaiten;
			                                                                                     using Lib;

			                                                                                     namespace App;

			                                                                                     [Container]
			                                                                                     [Import(typeof(GreetingModule))]
			                                                                                     [Transient<Helper>]
			                                                                                     internal static partial class AppContainer { }
			                                                                                     """);

			await That(library.Diagnostics).IsEmpty()
				.Because("a friend constructs the decorator itself, so the module records why it generates no factory, like for a registration");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "AppContainer")).Contains("Helper = ");
		}

		[Test]
		public async Task IsReportedByAContainerWithoutTheGrant()
		{
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(GrantedToAnother(Module), Consumer);

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("'Lib.LoudGreeter'"))
				.Because("the container could construct the decorator, but not set the property it does not see");
		}

		[Test]
		public async Task AsAComposite_IsTreatedAlike()
		{
			string module = Module
				.Replace("public LoudGreeter(IGreeter inner) { }", "public LoudGreeter(System.Collections.Generic.IEnumerable<IGreeter> all) { }")
				.Replace("[Decorate<LoudGreeter, IGreeter>]", "[Composite<LoudGreeter, IGreeter>]");

			GeneratorResult library = Generator.Run(Granted(module));
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(GrantedToAnother(module), Consumer);

			await That(library.Diagnostics).IsEmpty();
			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("'Lib.LoudGreeter'"));
		}
	}

	public class ExtendedContainer
	{
		private const string Extending = """
		                                 using Awaiten;
		                                 using Lib;

		                                 namespace App;

		                                 [Container(Extends = typeof(LibContainer))]
		                                 public static partial class AppContainer { }
		                                 """;

		private static readonly string Module = WithLibraryContainer(Library("[Inject] internal IClock Clock { get; set; } = null!;", "public class Greeter : IGreeter"), clock: true);

		[Test]
		public async Task InAnotherAssembly_BuildsAModuleRegistrationThroughTheModulesFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Module, Extending);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
				.Because("the extending container does not see the internal property, so only the module's factory sets it like the base does");
		}

		[Test]
		public async Task InAFriendAssembly_SetsThePropertyItself()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Module), Extending);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("Clock = ");
		}

		[Test]
		public async Task InAnotherAssembly_ReportsAModuleRegistrationWithARecordedSkip()
		{
			string module = WithLibraryContainer(GrantedToAnother(Library("""
			                                                              public Greeter(IClock Clock) { }

			                                                                  [Inject] internal IClock Clock { get; set; } = null!;
			                                                              """, "public class Greeter : IGreeter")), clock: true);

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(module, Extending);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231"))
				.Because("the module recorded why it generated no factory, and the extending container cannot set the property it does not see");
		}

		[Test]
		public async Task InAnotherAssembly_ReportsARegistrationOfTheBaseWhosePropertyItCannotSet()
		{
			string library = """
			                 using Awaiten;

			                 namespace Lib;

			                 public interface IGreeter { }
			                 public interface IClock { }
			                 public sealed class SystemClock : IClock { }

			                 public class Greeter : IGreeter
			                 {
			                     [Inject] internal IClock Clock { get; set; } = null!;
			                 }

			                 [Container]
			                 [Transient<Greeter, IGreeter>]
			                 [Transient<SystemClock, IClock>]
			                 public static partial class LibContainer { }
			                 """;

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, Extending);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Extending);

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT226") && diagnostic.Contains("Lib.Greeter"))
				.Because("the extending container cannot set the internal property it does not see, so it never builds the type without it");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "AppContainer")).Contains("Clock = ");
		}

		[Test]
		public async Task InAnotherAssembly_ReportsAScanMatchOfTheBaseWhosePropertyItCannotSet()
		{
			string library = """
			                 using Awaiten;

			                 namespace Lib;

			                 public interface IGreeter { }
			                 public interface IClock { }
			                 public sealed class SystemClock : IClock { }

			                 public class Greeter : IGreeter
			                 {
			                     [Inject] internal IClock Clock { get; set; } = null!;
			                 }

			                 [Container]
			                 [Scan<IGreeter>(As = ScanAs.Marker)]
			                 [Transient<SystemClock, IClock>]
			                 public static partial class LibContainer { }
			                 """;

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, Extending);

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT193") && diagnostic.Contains("Lib.Greeter"))
				.Because("a scan never fails the build, so the extending container skips the match with a warning instead of building it without the property");
		}

		[Test]
		public async Task InAnotherAssembly_ReportsADecoratorOfTheBaseWhosePropertyItCannotSet()
		{
			string library = """
			                 using Awaiten;

			                 namespace Lib;

			                 public interface IGreeter { }
			                 public interface IClock { }
			                 public sealed class SystemClock : IClock { }
			                 public sealed class Greeter : IGreeter { }

			                 public sealed class LoudGreeter : IGreeter
			                 {
			                     public LoudGreeter(IGreeter inner) { }

			                     [Inject] internal IClock Clock { get; set; } = null!;
			                 }

			                 [Container]
			                 [Transient<Greeter, IGreeter>]
			                 [Decorate<LoudGreeter, IGreeter>]
			                 [Transient<SystemClock, IClock>]
			                 public static partial class LibContainer { }
			                 """;

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, Extending);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Extending);

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT226") && diagnostic.Contains("decorator 'Lib.LoudGreeter'"));
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task InAnotherAssembly_ReportsAClosingOfAnOpenRegistrationOfTheBaseWhosePropertyItCannotSet()
		{
			string library = """
			                 using Awaiten;

			                 namespace Lib;

			                 public interface IGen<T> { }
			                 public interface IClock { }
			                 public sealed class SystemClock : IClock { }

			                 public class Gen<T> : IGen<T>
			                 {
			                     [Inject] internal IClock Clock { get; set; } = null!;
			                 }

			                 [Container]
			                 [Transient(typeof(Gen<>), typeof(IGen<>))]
			                 [Transient<SystemClock, IClock>]
			                 public static partial class LibContainer { }
			                 """;
			string extending = Extending.Replace("public static partial class AppContainer { }", "[Transient<Consumer>]\npublic static partial class AppContainer { }\n\npublic sealed class Consumer(IGen<int> gen) { public IGen<int> Gen { get; } = gen; }");

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, extending);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), extending);

			await That(other.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT226") && diagnostic.Contains("Lib.Gen<int>") && diagnostic.Contains("'Clock'"))
				.Because("the base records the hidden property of its open registration, so no closing is built without it, even one the base never builds itself");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "AppContainer")).Contains("Clock = ");
		}
	}

	public class PrivateDecorator
	{
		[Test]
		public async Task WithAnOptionalProperty_IsBuiltThroughItsFactoryInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace Lib;

			                                       public interface IGreeter { }
			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }
			                                       public sealed class Greeter : IGreeter { }

			                                       [Module]
			                                       [Transient<Greeter, IGreeter>]
			                                       [Decorate<GreetingModule.LoudGreeter, IGreeter>]
			                                       public static partial class GreetingModule
			                                       {
			                                           private sealed class LoudGreeter : IGreeter
			                                           {
			                                               public LoudGreeter(IGreeter inner) { }

			                                               [Inject(Optional = true)] public IClock Clock { get; set; } = null!;
			                                           }
			                                       }

			                                       [Container]
			                                       [Import(typeof(GreetingModule))]
			                                       [Transient<SystemClock, IClock>]
			                                       public static partial class LibContainer { }
			                                       """);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "LibContainer")).Contains("global::Lib.GreetingModule.Internals.Decorator_LoudGreeter_")
				.And.Contains("awaiten__has_Clock: true")
				.Because("the container finds the constructor the factory mirrors behind the parameters of the optional property");
		}
	}
}
