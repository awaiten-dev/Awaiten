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
	public async Task AnInternalConvention_RunsThroughItsWrapper()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library().Replace("public interface IWarmable", "internal interface IWarmable"), Consumer);

		await That(result.Diagnostics).IsEmpty()
			.Because("the convention runs on the instance the handle holds through a wrapper the module generates, so a consumer never calls it");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("HiddenInitializer_Roaster_");
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
