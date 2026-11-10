using System.Collections.Generic;
using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The scans of a library that grants <c>InternalsVisibleTo</c> take the internal services of its modules through
///     their handles, as its registrations do, so a container in an assembly the library grants nothing builds the same
///     graph as without the grant, while a container in a granted assembly calls internal members that take the services directly.
/// </summary>
public class ModuleScanOfAGrantingLibraryTests
{
	private const string Usings = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n";

	private static string Grant(string library, string? friend)
		=> friend is null ? library : library.Replace("namespace Lib;", $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{friend}\")]\n\nnamespace Lib;");

	private static GeneratorResult Run(string library, string container, string consumer, string? grant = null)
		=> consumer switch
		{
			"library" => Generator.Run(Grant(library, grant)),
			"same" => Generator.Run(Grant(library, grant) + "\n" + container.Replace("{0}", "SameContainer")),
			"friend" => Generator.RunWithGeneratedReferencedAssembly(Grant(library, "TestAssembly"), Usings + container.Replace("{0}", "MyContainer")),
			"reference" => Generator.RunWithGeneratedReferenceAssembly(Grant(library, grant), Usings + container.Replace("{0}", "MyContainer")),
			_ => Generator.RunWithGeneratedReferencedAssembly(Grant(library, grant), Usings + container.Replace("{0}", "MyContainer")),
		};

	private static string? ContainerSource(GeneratorResult result)
		=> result.Sources.GetValueOrDefault("Awaiten.MyCode.MyContainer.g.cs");

	public class AScanHookTakingAnInternalService
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IPlugin { }
			public interface IRoaster { }
			internal sealed class Roaster : IPlugin, IRoaster { }
			internal sealed class Beans { }
			internal sealed class Settings { }
			internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }

			[Module]
			[Singleton<Beans>]
			[Scan<IPlugin>(As = ScanAs.MatchingInterface, OnActivated = nameof(Wire))]
			public static partial class PluginModule
			{
				internal static void Wire(Roaster roaster, BeanStore store) { }
			}

			[Module][Singleton<BeanStore>] public static partial class StoreModule;
			[Module][Singleton<Settings>] public static partial class SettingsModule;
			""";

		private const string Container = """
			[Container][Import(typeof(PluginModule))][Import(typeof(StoreModule))][Import(typeof(SettingsModule))]
			public static partial class {0};
			""";

		[Test]
		[Arguments("library", null)]
		[Arguments("library", "Other")]
		[Arguments("same", null)]
		[Arguments("same", "Other")]
		[Arguments("friend", null)]
		[Arguments("full", null)]
		[Arguments("full", "Other")]
		[Arguments("reference", null)]
		[Arguments("reference", "Other")]
		public async Task BuildsInEveryContainer(string consumer, string? grant)
		{
			GeneratorResult result = Run(Library, Container, consumer, grant);

			await That(result.Diagnostics).IsEmpty()
				.Because("the hook's wrapper takes the internal service through the handle the other module builds, whatever the library grants");
		}

		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		public async Task AContainerOfAnAssemblyTheLibraryGrantsNothing_BuildsTheGraphOfALibraryWithoutTheGrant(string consumer)
		{
			string? granted = ContainerSource(Run(Library, Container, consumer, "Other"));

			await That(granted).IsEqualTo(ContainerSource(Run(Library, Container, consumer)))
				.Because("granting InternalsVisibleTo to one assembly must not change what the library serves any other");
			await That(granted).Contains("Internals.ScanHook_OnActivated_Roaster_").And.Contains("Hidden_BeanStore_");
		}

		[Test]
		public async Task AContainerOfAGrantedAssembly_CallsTheInternalWrapperThatTakesTheServiceDirectly()
		{
			GeneratorResult result = Run(Library, Container, "friend");

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result)).Contains("Internals.InternalScanHook_OnActivated_Roaster_")
				.And.Contains("new global::Lib.BeanStore(")
				.And.DoesNotContain("Hidden_BeanStore_")
				.Because("a container granted InternalsVisibleTo builds the internal service itself and registers no handles, as for a registration");
			await That(Run(Library, Container, "library", "TestAssembly").Sources.Single(source => source.Key == "Awaiten.ModuleScan.Lib.PluginModule.g.cs").Value)
				.Contains("internal static void InternalScanHook_OnActivated_Roaster_")
				.And.Contains("public static void ScanHook_OnActivated_Roaster_");
		}
	}

	public class AScanMatchTakingAnInternalService
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IPlugin { }
			internal interface IStore { }
			internal sealed class Store : IStore { }
			internal sealed class Roaster : IPlugin { public Roaster(IStore store) { } }

			[Module]
			[Singleton<Store, IStore>]
			[Scan<IPlugin>(As = ScanAs.Marker)]
			public static partial class CoffeeModule
			{
			    private sealed class Plugin : IPlugin { public Plugin(IStore store) { } }
			}
			""";

		private const string Container = """
			[Container][Import(typeof(CoffeeModule))]
			public static partial class {0};
			""";

		[Test]
		[Arguments("library", null)]
		[Arguments("library", "Other")]
		[Arguments("same", null)]
		[Arguments("same", "Other")]
		[Arguments("friend", null)]
		[Arguments("full", null)]
		[Arguments("full", "Other")]
		[Arguments("reference", null)]
		[Arguments("reference", "Other")]
		public async Task BuildsInEveryContainer(string consumer, string? grant)
		{
			GeneratorResult result = Run(Library, Container, consumer, grant);

			await That(result.Diagnostics).IsEmpty()
				.Because("the factories take the internal service through its handle, whatever the library grants");
		}

		[Test]
		public async Task AContainerOfAnAssemblyTheLibraryGrantsNothing_BuildsTheGraphOfALibraryWithoutTheGrant()
		{
			string? granted = ContainerSource(Run(Library, Container, "full", "Other"));

			await That(granted).IsEqualTo(ContainerSource(Run(Library, Container, "full")))
				.Because("granting InternalsVisibleTo to one assembly must not change what the library serves any other");
		}

		[Test]
		public async Task AContainerOfAGrantedAssembly_CallsTheInternalFactoriesThatTakeTheServiceDirectly()
		{
			GeneratorResult result = Run(Library, Container, "friend");

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result)).Contains("Internals.InternalScan_Roaster_")
				.And.Contains("Internals.InternalScan_Plugin_")
				.And.DoesNotContain("Hidden_")
				.Because("a container granted InternalsVisibleTo builds the internal service itself and registers no handles");
		}

		[Test]
		[Arguments(null)]
		[Arguments("Other")]
		public async Task AContainerOfTheLibrary_ConstructsTheInternalMatchItself(string? grant)
		{
			GeneratorResult result = Run(Library, Container, "same", grant);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("new global::Lib.Roaster(")
				.And.Contains("global::Lib.CoffeeModule.Internals.InternalScan_Plugin_")
				.Because("the container builds the internal store itself, so it constructs the internal match and calls the internal factory of the private one");
		}
	}

	public class AFactoryMemberProducingAScannedClass
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IAppliance { }
			internal sealed class TimeSource { }
			public sealed class Clock : IAppliance { internal Clock(TimeSource source) { } }

			[Module]
			[Singleton<TimeSource>]
			[Singleton<Clock>(Factory = nameof(CreateS))]
			[Scan<IAppliance>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
			public static partial class SModule
			{
				internal static Clock CreateS(TimeSource source) => new(source);
			}
			""";

		private const string Container = """
			[Container][Import(typeof(SModule))]
			public static partial class {0};
			""";

		[Test]
		[Arguments("library", null)]
		[Arguments("library", "Other")]
		[Arguments("same", null)]
		[Arguments("same", "Other")]
		[Arguments("friend", null)]
		public async Task BuildsInTheLibraryAndAGrantedAssembly(string consumer, string? grant)
		{
			GeneratorResult result = Run(Library, Container, consumer, grant);

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		public async Task AContainerOfAnAssemblyTheLibraryGrantsNothing_BuildsTheGraphOfALibraryWithoutTheGrant(string consumer)
		{
			GeneratorResult granted = Run(Library, Container, consumer, "Other");
			GeneratorResult plain = Run(Library, Container, consumer);

			await That(granted.Diagnostics).IsEqualTo(plain.Diagnostics)
				.Because("the scan builds the match through the handle of the internal service, as without the grant, instead of leaving it to the granted assemblies");
			await That(granted.Diagnostics).DoesNotContain("*AWT231*").AsWildcard();
			await That(ContainerSource(granted)).IsEqualTo(ContainerSource(plain));
		}
	}
}
