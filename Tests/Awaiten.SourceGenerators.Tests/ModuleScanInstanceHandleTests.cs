using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A self-compiled module scan match exposed under several interfaces is built through a generated instance handle,
///     like an internal implementation a module registers under several services, so every container shares one
///     instance across its exposures.
/// </summary>
public class ModuleScanInstanceHandleTests
{
	private static string Library(string? friend = null, string scans = "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]") => $$"""
		using System;
		using System.Threading.Tasks;
		using Awaiten;

		{{(friend is null ? "" : $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{friend}\")]")}}

		namespace Lib;

		public interface IPlugin { }
		public interface IRoaster { }
		public interface IClock { }

		public interface IWarmable
		{
		    Task WarmAsync();
		}

		internal sealed class Roaster : IPlugin, IRoaster, IWarmable, IDisposable
		{
		    public Roaster(IClock clock) { }

		    public Task WarmAsync() => Task.CompletedTask;

		    public void Dispose() { }
		}

		[Module]
		[AsyncInitializer<IWarmable>(nameof(IWarmable.WarmAsync))]
		{{scans}}
		public static partial class PluginModule
		{
		    internal static void Activate(Roaster roaster) { }
		}
		""";

	private const string Consumer = """
	                                using Awaiten;
	                                using Lib;

	                                namespace MyCode;

	                                public sealed class SystemClock : IClock { }

	                                [Container]
	                                [Import(typeof(PluginModule))]
	                                [Singleton<SystemClock, IClock>]
	                                public static partial class MyContainer;
	                                """;

	private static string ModuleSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("ModuleScan")).Value;

	[Test]
	public async Task TheLibrary_BuildsTheMatchThroughAnInstanceHandle()
	{
		GeneratorResult result = Generator.Run(Library());

		await That(result.Diagnostics).IsEmpty()
			.Because("one instance handle shares the match across both interfaces, so AWT197 no longer applies");
		string module = ModuleSource(result);
		await That(module).Contains("public class HiddenInstance_Roaster_")
			.And.Contains("[global::Awaiten.Internals.GeneratedScanRegistrationAttribute<global::Lib.IPlugin>(\"Internals.Alias_Roaster_")
			.And.Contains("[global::Awaiten.Internals.GeneratedScanRegistrationAttribute<global::Lib.IRoaster>(\"Internals.Alias_Roaster_")
			.Because("each exposure is registered through an alias that serves the instance from the handle");
		await That(module).Contains(".Disposable(new global::Lib.Roaster(@clock));")
			.Because("the factory wraps the instance in the handle variant that forwards its disposal");
		await That(module).Contains("HiddenInitializer_Roaster_")
			.Because("the module's convention runs on the instance the handle holds");
	}

	[Test]
	public async Task TheHandleSummary_DescribesTheScan()
	{
		string module = ModuleSource(Generator.Run(Library()));

		await That(module).Contains("Generated from a module [Scan]: holds the instance of an implementation the module's scans expose under several")
			.And.DoesNotContain("registers under an internal")
			.Because("the scan's handle holds an implementation, not a service the module registers under an internal type");
	}

	[Test]
	public async Task ADiagnosticAboutTheHandle_NamesTheImplementation()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer.Replace("[Singleton<SystemClock, IClock>]", "[Scoped<SystemClock, IClock>]"));

		await That(result.Diagnostics).IsNotEmpty()
			.Because("the singleton match captures a scoped dependency");
		await That(result.Diagnostics).All().Satisfy(diagnostic => !diagnostic.Contains("HiddenInstance_"))
			.Because("a message never names a type the user did not write");
		await That(result.Diagnostics).Contains("*AWT105*the singleton 'Lib.Roaster' depends on 'Lib.IClock'*").AsWildcard();
	}

	[Test]
	public async Task AMissingDependencyOfTheHandle_NamesTheImplementation()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer.Replace("[Singleton<SystemClock, IClock>]", ""));

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("error AWT101"));
		await That(result.Diagnostics).All().Satisfy(diagnostic => !diagnostic.Contains("HiddenInstance_") && !diagnostic.Contains("@scan:") && !diagnostic.Contains("Scan_Roaster_"))
			.Because("a message never names a type or member the user did not write");
		await That(result.Diagnostics).Contains("*'Lib.Roaster'*").AsWildcard();
	}

	[Test]
	public async Task AMissingDependencyOfACollectedSingleExposureMatch_NamesNoGeneratedMember()
	{
		string library = Library(scans: "[Scan<IPlugin>(As = ScanAs.Marker)]") + """

		                                                                      internal sealed class Grinder : IPlugin
		                                                                      {
		                                                                          public Grinder(IClock clock) { }
		                                                                      }
		                                                                      """;

		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer.Replace("[Singleton<SystemClock, IClock>]", ""));

		await That(result.Diagnostics).HasCount(2).And.All().Satisfy(diagnostic => diagnostic.Contains("error AWT101"));
		await That(result.Diagnostics).All().Satisfy(diagnostic => !diagnostic.Contains("@scan:") && !diagnostic.Contains("Scan_"))
			.Because("the match that loses single resolution is named by its service, not by its generated factory");
	}

	[Test]
	public async Task SkipUnconstructable_DropsTheMatchUnderEveryExposureWithOneWarning()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
			Library(scans: "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, SkipUnconstructable = true)]"),
			Consumer.Replace("[Singleton<SystemClock, IClock>]", ""));

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("warning AWT141") && diagnostic.Contains("'Lib.Roaster'"))
			.Because("the container cannot satisfy the match's clock, so the opted-in scan drops it, its handle and every alias, as it drops a match with one exposure");
	}

	[Test]
	public async Task AnExplicitRegistrationOfOneExposure_OverridesOnlyThatExposure()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer.Replace("[Singleton<SystemClock, IClock>]", "[Singleton<SystemClock, IClock>]\n[Singleton<MyRoaster, IRoaster>]") + "\npublic sealed class MyRoaster : IRoaster { }");

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("new global::MyCode.MyRoaster(")
			.And.Contains("Alias_Roaster_")
			.Because("the explicit registration wins IRoaster, while IPlugin is still served from the handle");
	}

	[Test]
	public async Task AnOpenMarkerClosedTwice_SharesTheHandleAcrossBothClosings()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
		                                                                      using Awaiten;

		                                                                      namespace Lib;

		                                                                      public interface IHandler<T> { }
		                                                                      public sealed class Order { }
		                                                                      public sealed class Refund { }
		                                                                      internal sealed class LedgerHandler : IHandler<Order>, IHandler<Refund> { }

		                                                                      [Module]
		                                                                      [Scan(typeof(IHandler<>), As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		                                                                      public static partial class LedgerModule;
		                                                                      """, """
		                                                                           using Awaiten;
		                                                                           using Lib;

		                                                                           namespace MyCode;

		                                                                           [Container]
		                                                                           [Import(typeof(LedgerModule))]
		                                                                           public static partial class MyContainer;
		                                                                           """);

		await That(result.Diagnostics).IsEmpty()
			.Because("each closing of the open marker is an exposure the handle serves through an alias of its own");
	}

	[Test]
	public async Task AConsumerRegistrationOfAPublicMatch_SharesTheHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
			Library(scans: "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]").Replace("internal sealed class Roaster", "public sealed class Roaster"),
			Consumer.Replace("[Singleton<SystemClock, IClock>]", "[Singleton<SystemClock, IClock>]\n[Singleton<Roaster>]"));

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain("new global::Lib.Roaster(")
			.Because("the consumer serves its own registration from the handle through the alias under the implementation, as a container in the library shares one instance");
	}

	[Test]
	public async Task AContainerSideInjectPropertyOfAPublicMatch_NamesTheScanAsTheReasonForTheHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
			Library(scans: "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]")
				.Replace("internal sealed class Roaster", "public sealed class Roaster")
				.Replace("public Roaster(IClock clock) { }", "public Roaster(IClock clock) { }\n    public IClock Backup { get; set; } = null!;"),
			Consumer.Replace("public static partial class MyContainer;", "[InjectProperty<Roaster>(nameof(Roaster.Backup))]\npublic static partial class MyContainer;"));

		await That(result.Diagnostics).HasSingle().Which.EndsWith("error AWT178: 'Lib.Roaster' is built by the generated factory of 'Lib.PluginModule' because a scan of the module exposes it under several services, which share one instance of it through a generated handle, so its property 'Backup' cannot be filled by [InjectProperty], which applies only to a container-constructed instance; grant this assembly InternalsVisibleTo in the assembly of 'Lib.PluginModule', so the container constructs 'Lib.Roaster' itself")
			.Because("the module has no registration of the match, so the scan is what makes it build the match through a handle");
	}

	public class AConsumerRegistrationOfAPublicMatch
	{
		private const string Scans = "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]";

		private static string PublicLibrary(string? friend = null)
			=> Library(friend, Scans).Replace("internal sealed class Roaster", "public sealed class Roaster");

		private static string Container(string registration, string ns) => $$"""
			using Awaiten;
			using Lib;

			namespace {{ns}};

			public sealed class SystemClock : IClock { }

			[Container]
			[Import(typeof(PluginModule))]
			[Singleton<SystemClock, IClock>]
			{{registration}}
			public static partial class MyContainer
			{
			    private static void Hook(Roaster roaster) { }
			}
			""";

		private static (GeneratorResult SameAssembly, GeneratorResult External, GeneratorResult Friend) RunEverywhere(string registration)
			=> (Generator.Run(PublicLibrary() + Container(registration, "Lib").Replace("using Awaiten;", "").Replace("using Lib;", "").Replace("namespace Lib;", "")),
				Generator.RunWithGeneratedReferencedAssembly(PublicLibrary(), Container(registration, "MyCode")),
				Generator.RunWithGeneratedReferencedAssembly(PublicLibrary("TestAssembly"), Container(registration, "MyCode")));

		[Test]
		public async Task WithADifferentLifetime_YieldsTheScanAsInTheModulesAssembly()
		{
			(GeneratorResult sameAssembly, GeneratorResult external, GeneratorResult friend) = RunEverywhere("[Transient<Roaster, IRoaster>]");

			await That(sameAssembly.Diagnostics).IsEmpty()
				.Because("in the module's assembly the scan yields to the explicit registration");
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the scan's handle yields to the explicit registration too, instead of reporting conflicting lifetimes");
		}

		[Test]
		public async Task WithAnOverridableDefault_YieldsTheScanAsInTheModulesAssembly()
		{
			(GeneratorResult sameAssembly, GeneratorResult external, GeneratorResult friend) = RunEverywhere("[Scoped<Roaster, IRoaster>(Fallback = Fallback.Silent)]");

			await That(sameAssembly.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("an overridable default still precedes a scan match");
		}

		[Test]
		public async Task WithSuppressDisposal_YieldsTheScanAsInTheModulesAssembly()
		{
			(GeneratorResult sameAssembly, GeneratorResult external, GeneratorResult friend) = RunEverywhere("[Singleton<Roaster, IRoaster>(SuppressDisposal = true)]");

			await That(sameAssembly.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the container does not dispose the handle, which leaves the instance undisposed as the registration asks");
		}

		[Test]
		public async Task WithAHook_ReportsAwt241OnlyInAnExternalContainer()
		{
			(GeneratorResult sameAssembly, GeneratorResult external, GeneratorResult friend) = RunEverywhere("[Singleton<Roaster, IRoaster>(OnActivated = nameof(Hook))]");

			await That(sameAssembly.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("error AWT241") && diagnostic.Contains("it sets OnActivated"))
				.Because("the handle runs only hooks the module generated wrappers for, so it cannot run the container's hook on the instance it builds");
		}

		[Test]
		public async Task WithTwoDifferentLifetimes_ReportsAwt107()
		{
			(GeneratorResult sameAssembly, GeneratorResult external, _) = RunEverywhere("[Transient<Roaster, IRoaster>]\n[Scoped<Roaster>]");

			await That(sameAssembly.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("error AWT107"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("error AWT107") && diagnostic.Contains("'Lib.Roaster'"))
				.Because("the two explicit registrations still contradict each other, while the scan yields to the first");
		}
	}

	public class AContainerScanOfAPublicMatch
	{
		private static string PublicLibrary(string scans, string? friend = null)
			=> Library(friend, scans).Replace("internal sealed class Roaster", "public sealed class Roaster");

		private static string Container(string scan, string ns) => $$"""
			using Awaiten;
			using Lib;

			namespace {{ns}};

			public sealed class SystemClock : IClock { }

			[Container]
			[Import(typeof(PluginModule))]
			[Singleton<SystemClock, IClock>]
			{{scan}}
			public static partial class MyContainer
			{
			    private static void Hook(Roaster roaster) { }
			}
			""";

		private static (GeneratorResult SameAssembly, GeneratorResult Friend, GeneratorResult External, GeneratorResult Reference) RunEverywhere(string scans, string scan)
			=> (Generator.Run(PublicLibrary(scans) + Container(scan, "Lib").Replace("using Awaiten;", "").Replace("using Lib;", "").Replace("namespace Lib;", "")),
				Generator.RunWithGeneratedReferencedAssembly(PublicLibrary(scans, "TestAssembly"), Container(scan, "MyCode")),
				Generator.RunWithGeneratedReferencedAssembly(PublicLibrary(scans), Container(scan, "MyCode")),
				Generator.RunWithGeneratedReferenceAssembly(PublicLibrary(scans), Container(scan, "MyCode")));

		private const string Scans = "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]";

		[Test]
		public async Task WithADifferentLifetime_ReportsAwt142AsInTheModulesAssembly()
		{
			(GeneratorResult sameAssembly, GeneratorResult friend, GeneratorResult external, GeneratorResult reference) = RunEverywhere(
				Scans,
				"[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, Lifetime = Lifetime.Transient)]");

			foreach (GeneratorResult result in new[] { sameAssembly, friend, external, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*warning AWT142: 'Lib.Roaster' is matched by scans with conflicting lifetimes (Transient and Singleton); the first scan's Transient is used").AsWildcard()
					.Because("the container's own scan precedes the module's, so its lifetime wins in every assembly");
			}

			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("AwaitenRegistration(typeof(global::Lib.IRoaster), global::Awaiten.Lifetime.Transient,")
				.And.Contains("Alias_Roaster_")
				.Because("the module's handle takes the lifetime of the container's scan, which shares the handle's instance");
		}

		[Test]
		public async Task WithAHook_ReportsAwt241OnlyInAnExternalContainer()
		{
			(GeneratorResult sameAssembly, GeneratorResult friend, GeneratorResult external, GeneratorResult reference) = RunEverywhere(
				Scans,
				"[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, Lifetime = Lifetime.Singleton, OnActivated = nameof(Hook))]");

			await That(sameAssembly.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			foreach (GeneratorResult result in new[] { external, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*error AWT241: 'Lib.Roaster' is built through a generated handle of the imported module 'Lib.PluginModule'*it sets OnActivated, which the handle cannot apply*").AsWildcard()
					.Because("the handle runs only the hooks its module generated wrappers for, so it cannot run the container's hook on the instance it builds");
			}
		}

		[Test]
		public async Task WithAHookInTheSlotOfTheModulesHook_ReportsAwt241InsteadOfAwt199InAnExternalContainer()
		{
			(GeneratorResult sameAssembly, GeneratorResult friend, GeneratorResult external, GeneratorResult reference) = RunEverywhere(
				Scans.Replace("Lifetime.Singleton)]", "Lifetime.Singleton, OnActivated = nameof(Activate))]"),
				"[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, Lifetime = Lifetime.Singleton, OnActivated = nameof(Hook))]");

			foreach (GeneratorResult result in new[] { sameAssembly, friend, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*warning AWT199: 'Lib.Roaster' is matched by scans naming conflicting OnActivated hooks ('Hook' and 'Lib.PluginModule.*'); the first scan's 'Hook' is used").AsWildcard();
			}

			foreach (GeneratorResult result in new[] { external, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*error AWT241: 'Lib.Roaster' is built through a generated handle of the imported module 'Lib.PluginModule'*it sets OnActivated, which the handle cannot apply*").AsWildcard()
					.Because("the handle runs the module's hook, so it can neither run the container's hook that wins the slot in the module's assembly nor drop the module's");
			}
		}

		[Test]
		public async Task WithTheSameLifetime_KeepsTheModulesHook()
		{
			(GeneratorResult sameAssembly, GeneratorResult friend, GeneratorResult external, GeneratorResult reference) = RunEverywhere(
				Scans.Replace("Lifetime.Singleton)]", "Lifetime.Singleton, OnActivated = nameof(Activate))]"),
				"[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, Lifetime = Lifetime.Singleton)]");

			await That(sameAssembly.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(reference.Diagnostics).IsEmpty();
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Lib.PluginModule.Internals.ScanHook_OnActivated_Roaster_")
				.Because("the container's scan sets no hook, so the module's hook still runs on the shared instance, as in the module's assembly");
		}
	}

	public class AMatchOfAnotherModuleOverTheHandle
	{
		private static string Library(string marker, string options) => $$"""
			using Awaiten;

			namespace Lib;

			public interface IPlugin { }
			public interface IRoaster { }
			public interface IBrewer { }
			public sealed class Roaster : IPlugin, IRoaster, IBrewer { }

			[Module]
			[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
			public static partial class FirstModule;

			[Module]
			[Scan<{{marker}}>(As = ScanAs.Marker{{options}})]
			public static partial class SecondModule
			{
			    internal static void Hook(Roaster roaster) { }
			}

			""";

		private static string Container(bool secondFirst) => $$"""
			[Container]
			[Import(typeof({{(secondFirst ? "SecondModule" : "FirstModule")}}))]
			[Import(typeof({{(secondFirst ? "FirstModule" : "SecondModule")}}))]
			public static partial class MyContainer;
			""";

		private static (GeneratorResult SameAssembly, GeneratorResult External, GeneratorResult Reference) RunEverywhere(string marker, string options, bool secondFirst)
			=> (Generator.Run(Library(marker, options) + Container(secondFirst)),
				Generator.RunWithGeneratedReferencedAssembly(Library(marker, options), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container(secondFirst)),
				Generator.RunWithGeneratedReferenceAssembly(Library(marker, options), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container(secondFirst)));

		[Test]
		[Arguments("IRoaster", false, "Singleton", "Transient")]
		[Arguments("IRoaster", true, "Transient", "Singleton")]
		[Arguments("IBrewer", false, "Singleton", "Transient")]
		[Arguments("IBrewer", true, "Transient", "Singleton")]
		public async Task WithADifferentLifetime_ReportsAwt142AsInTheModulesAssembly(string marker, bool secondFirst, string first, string second)
		{
			(GeneratorResult sameAssembly, GeneratorResult external, GeneratorResult reference) = RunEverywhere(marker, ", Lifetime = Lifetime.Transient", secondFirst);

			foreach (GeneratorResult result in new[] { sameAssembly, external, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains($"*warning AWT142: 'Lib.Roaster' is matched by scans with conflicting lifetimes ({first} and {second}); the first scan's {first} is used").AsWildcard()
					.Because("the scan of the module imported first decides the lifetime of the one instance in every assembly");
			}

			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"AwaitenRegistration(typeof(global::Lib.{marker}), global::Awaiten.Lifetime.{first}")
				.And.Contains($"AwaitenRegistration(typeof(global::Lib.IPlugin), global::Awaiten.Lifetime.{first}")
				.Because("the other module's match serves the handle's instance, which takes the first scan's lifetime");
		}

		[Test]
		[Arguments("IRoaster")]
		[Arguments("IBrewer")]
		public async Task WithAHook_ReportsAwt241OnlyInAnExternalContainer(string marker)
		{
			(GeneratorResult sameAssembly, GeneratorResult external, GeneratorResult reference) = RunEverywhere(marker, ", Lifetime = Lifetime.Singleton, OnActivated = nameof(Hook)", false);

			await That(sameAssembly.Diagnostics).IsEmpty()
				.Because("in the module's assembly the hooks of both scans run on the one instance");
			foreach (GeneratorResult result in new[] { external, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*error AWT241: 'Lib.Roaster' is built through a generated handle of the imported module 'Lib.FirstModule'*it sets OnActivated, which the handle cannot apply*").AsWildcard()
					.Because("the other module's match serves the instance of the handle, which cannot run that module's hook");
			}
		}
	}

	[Test]
	public async Task AnInternalConvention_RunsThroughItsWrapper()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library().Replace("public interface IWarmable", "internal interface IWarmable"), Consumer);

		await That(result.Diagnostics).IsEmpty()
			.Because("the convention runs on the instance the handle holds through a wrapper the module generates, so a consumer never calls it");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("HiddenInitializer_Roaster_");
	}

	[Test]
	public async Task AnInternalConventionOfAMatchOfTwoScans_RunsThroughItsWrapper()
	{
		string library = Library(scans: """
		                                [Scan<IPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		                                [Scan<IRoaster>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		                                """).Replace("public interface IWarmable", "internal interface IWarmable");

		GeneratorResult result = Generator.Run(library);
		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

		await That(result.Diagnostics).IsEmpty()
			.Because("the second scan joins the match to the handle of the first, whose wrapper runs the convention, so a consumer never calls it");
		await That(external.Diagnostics).IsEmpty();
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("HiddenInitializer_Roaster_");
	}

	[Test]
	public async Task AnInternalConventionOfAMatchOfOneScanWithOneExposure_RunsThroughItsWrapper()
	{
		string library = Library(scans: "[Scan<IPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]").Replace("public interface IWarmable", "internal interface IWarmable");

		GeneratorResult result = Generator.Run(library);
		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

		await That(result.Diagnostics).IsEmpty()
			.Because("a match with one exposure is returned as its interface, so the module generates a wrapper that runs the internal convention on it");
		await That(external.Diagnostics).IsEmpty();
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Initializer_IWarmable_");
	}

	[Test]
	public async Task TwoScansExposingTheMatchUnderDifferentInterfaces_ShareOneHandle()
	{
		GeneratorResult result = Generator.Run(Library(scans: """
		                                                     [Scan<IPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		                                                     [Scan<IRoaster>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		                                                     """));

		await That(result.Diagnostics).IsEmpty()
			.Because("the overlap joins the second scan's interface to the handle of the first");
		string module = ModuleSource(result);
		await That(module.Split(["public class HiddenInstance_Roaster_",], System.StringSplitOptions.None)).HasCount(2)
			.Because("the match is built into a single handle");
		await That(module).Contains("[global::Awaiten.Internals.GeneratedScanRegistrationAttribute<global::Lib.IPlugin>(\"Internals.Alias_Roaster_")
			.And.Contains("[global::Awaiten.Internals.GeneratedScanRegistrationAttribute<global::Lib.IRoaster>(\"Internals.Alias_Roaster_");
	}

	[Test]
	public async Task AnExternalConsumer_ServesEveryExposureFromTheHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Scan_Roaster_")
			.And.Contains("Alias_Roaster_")
			.And.Contains("HiddenInitializer_Roaster_")
			.Because("the container builds the handle once and serves both interfaces through the aliases");
	}

	[Test]
	public async Task AnExternalConsumerOfTheReferenceAssembly_ServesEveryExposureFromTheHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferenceAssembly(Library(), Consumer);

		await That(result.Diagnostics).IsEmpty()
			.Because("a project reference compiles against the reference assembly, which still names the implementation");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Alias_Roaster_");
	}

	[Test]
	public async Task ASameAssemblyContainer_ConstructsTheMatchDirectly()
	{
		GeneratorResult result = Generator.Run(Library() + """

		                                                   public sealed class SystemClock : IClock { }

		                                                   [Container]
		                                                   [Import(typeof(PluginModule))]
		                                                   [Singleton<SystemClock, IClock>]
		                                                   public static partial class MyContainer;
		                                                   """);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains("new global::Lib.Roaster(")
			.And.DoesNotContain("Alias_Roaster_")
			.Because("a container in the module's assembly can name the implementation and registers it under each interface");
	}

	[Test]
	public async Task AFriendConsumer_ConstructsTheMatchItself()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library("TestAssembly"), Consumer);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("new global::Lib.Roaster(")
			.Because("the module leaves a match exposed under several interfaces to the assemblies it grants InternalsVisibleTo");
	}

	[Test]
	public async Task TwoModulesScanningTheMatch_ReportAwt240InAConsumerThatImportsBoth()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
		                                                                      using Awaiten;

		                                                                      namespace Lib;

		                                                                      public interface IPlugin { }
		                                                                      public interface IRoaster { }
		                                                                      internal sealed class Roaster : IPlugin, IRoaster { }

		                                                                      [Module]
		                                                                      [Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface)]
		                                                                      public static partial class FirstModule;

		                                                                      [Module]
		                                                                      [Scan<IRoaster>(As = ScanAs.Marker)]
		                                                                      [Scan<IPlugin>(As = ScanAs.Marker)]
		                                                                      public static partial class SecondModule;
		                                                                      """, """
		                                                                           using Awaiten;
		                                                                           using Lib;

		                                                                           namespace MyCode;

		                                                                           [Container]
		                                                                           [Import(typeof(FirstModule))]
		                                                                           [Import(typeof(SecondModule))]
		                                                                           public static partial class MyContainer;
		                                                                           """);

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("error AWT240")
		                                                                  && diagnostic.Contains("'Lib.Roaster' is matched by the [Scan]s of the imported modules 'Lib.FirstModule' and 'Lib.SecondModule'"))
			.Because("each module builds the match through a handle of its own, so a consumer importing both would split the one instance a container in their assembly shares");
	}

	public class AMatchBuiltFromRuntimeArguments
	{
		private const string ArgLibrary = """
			using System;
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib;

			public interface IPlugin { }
			public interface IRoaster { }
			public interface IOrder { }

			internal sealed class Roaster : IPlugin, IRoaster
			{
			    public Roaster([Arg] IOrder order) { }
			}

			[Module]
			[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface)]
			public static partial class PluginModule;
			""";

		private const string ArgConsumer = """
			using System;
			using Awaiten;
			using Lib;

			namespace MyCode;

			public sealed class Bar
			{
			    public Bar(Func<IOrder, IPlugin> plugin, Func<IOrder, IRoaster> roaster) { }
			}

			[Container]
			[Import(typeof(PluginModule))]
			[Transient<Bar>]
			public static partial class MyContainer;
			""";

		[Test]
		public async Task TheAliasesPassTheArgumentsToAFactoryOfTheHandle()
		{
			GeneratorResult result = Generator.Run(ArgLibrary);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("@awaiten__handle, [global::Awaiten.Arg] global::Lib.IOrder @awaiten__arg1) => @awaiten__handle(@awaiten__arg1).Value;")
				.And.Contains("Async(global::System.Func<global::Lib.IOrder, global::System.Threading.Tasks.Task<")
				.Because("a match built from runtime arguments is reached only through a factory, so each alias forwards them to a factory of the handle, with an async counterpart for an async-tainted handle");
		}

		[Test]
		public async Task AnExternalConsumer_ServesEveryExposureFromAFactoryOfTheHandle()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(ArgLibrary, ArgConsumer);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Alias_Roaster_");
		}

		[Test]
		public async Task ASameAssemblyOrFriendConsumer_ReportsNothing()
		{
			GeneratorResult same = Generator.Run(ArgLibrary + ArgConsumer.Replace("using Lib;", "").Replace("namespace MyCode;", "").Replace("using System;", "").Replace("using Awaiten;", ""));
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(ArgLibrary.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), ArgConsumer);

			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task OfAnAsyncInitializedMatch_AnExternalConsumerAwaitsTheAsyncAlias()
		{
			string library = ArgLibrary
				.Replace("internal sealed class Roaster : IPlugin, IRoaster", "public interface IWarmable { Task WarmAsync(); }\n\ninternal sealed class Roaster : IPlugin, IRoaster, IWarmable")
				.Replace("public Roaster([Arg] IOrder order) { }", "public Roaster([Arg] IOrder order) { }\n\n    public Task WarmAsync() => Task.CompletedTask;")
				.Replace("[Module]", "[Module]\n[AsyncInitializer<IWarmable>(nameof(IWarmable.WarmAsync))]");
			string consumer = ArgConsumer
				.Replace("using System;", "using System;\nusing System.Threading.Tasks;")
				.Replace("Func<IOrder, IPlugin> plugin, Func<IOrder, IRoaster> roaster", "Func<IOrder, Task<IPlugin>> plugin, Func<IOrder, Task<IRoaster>> roaster");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"].Split('\n').Any(line => line.Contains("Alias_Roaster_") && line.Contains("Async("))).IsTrue()
				.Because("the handle is async-initialized, so each exposure awaits an async factory of the handle through the async alias");
		}
	}

	[Test]
	public async Task ASingleExposureMatchOfAnotherModule_SharesTheHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
		                                                                      using Awaiten;

		                                                                      namespace Lib;

		                                                                      public interface IPlugin { }
		                                                                      public interface IRoaster { }
		                                                                      internal sealed class Roaster : IPlugin, IRoaster { }

		                                                                      [Module]
		                                                                      [Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
		                                                                      public static partial class FirstModule;

		                                                                      [Module]
		                                                                      [Scan<IRoaster>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		                                                                      public static partial class SecondModule;
		                                                                      """, """
		                                                                           using Awaiten;
		                                                                           using Lib;

		                                                                           namespace MyCode;

		                                                                           [Container]
		                                                                           [Import(typeof(FirstModule))]
		                                                                           [Import(typeof(SecondModule))]
		                                                                           public static partial class MyContainer;
		                                                                           """);

		await That(result.Diagnostics).IsEmpty()
			.Because("the other module's match serves its interface through the alias of the handle");
	}
}
