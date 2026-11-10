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
			"friendReference" => Generator.RunWithGeneratedReferenceAssembly(Grant(library, "TestAssembly"), Usings + container.Replace("{0}", "MyContainer")),
			"reference" => Generator.RunWithGeneratedReferenceAssembly(Grant(library, grant), Usings + container.Replace("{0}", "MyContainer")),
			"compilation" => Generator.RunWithGeneratedCompilationReference(Grant(library, grant), Usings + container.Replace("{0}", "MyContainer")),
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
		[Arguments("friendReference", null)]
		[Arguments("full", null)]
		[Arguments("full", "Other")]
		[Arguments("reference", null)]
		[Arguments("reference", "Other")]
		[Arguments("compilation", null)]
		[Arguments("compilation", "Other")]
		public async Task BuildsInEveryContainer(string consumer, string? grant)
		{
			GeneratorResult result = Run(Library, Container, consumer, grant);

			await That(result.Diagnostics).IsEmpty()
				.Because("the hook's wrapper takes the internal service through the handle the other module builds, whatever the library grants");
		}

		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		[Arguments("compilation")]
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
			await That(Run(Library, Container, "library", "Other").Sources.Single(source => source.Key == "Awaiten.ModuleScan.Lib.PluginModule.g.cs").Value)
				.Contains("internal static void InternalScanHook_OnActivated_Roaster_")
				.And.Contains("public static void ScanHook_OnActivated_Roaster_")
				.And.DoesNotContain("InternalScan_Roaster_")
				.Because("the public factory takes no handle, so a container in a granted assembly calls it and the module generates no internal one");
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
		[Arguments("friendReference", null)]
		[Arguments("full", null)]
		[Arguments("full", "Other")]
		[Arguments("reference", null)]
		[Arguments("reference", "Other")]
		[Arguments("compilation", null)]
		[Arguments("compilation", "Other")]
		public async Task BuildsInEveryContainer(string consumer, string? grant)
		{
			GeneratorResult result = Run(Library, Container, consumer, grant);

			await That(result.Diagnostics).IsEmpty()
				.Because("the factories take the internal service through its handle, whatever the library grants");
		}

		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		[Arguments("compilation")]
		public async Task AContainerOfAnAssemblyTheLibraryGrantsNothing_BuildsTheGraphOfALibraryWithoutTheGrant(string consumer)
		{
			string? granted = ContainerSource(Run(Library, Container, consumer, "Other"));

			await That(granted).IsEqualTo(ContainerSource(Run(Library, Container, consumer)))
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
		[Arguments("friendReference", null)]
		[Arguments("full", null)]
		[Arguments("full", "Other")]
		[Arguments("reference", null)]
		[Arguments("reference", "Other")]
		[Arguments("compilation", null)]
		[Arguments("compilation", "Other")]
		public async Task BuildsInEveryContainer(string consumer, string? grant)
		{
			GeneratorResult result = Run(Library, Container, consumer, grant);

			await That(result.Diagnostics).IsEmpty()
				.Because("the scan builds the match through the handle of the internal service, as without the grant, instead of leaving it to the granted assemblies");
		}

		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		[Arguments("compilation")]
		public async Task AContainerOfAnAssemblyTheLibraryGrantsNothing_BuildsTheGraphOfALibraryWithoutTheGrant(string consumer)
		{
			await That(ContainerSource(Run(Library, Container, consumer, "Other"))).IsEqualTo(ContainerSource(Run(Library, Container, consumer)))
				.Because("granting InternalsVisibleTo to one assembly must not change what the library serves any other");
		}
	}

	public class AHookOfAMatchTheModuleGeneratesNoFactoryFor
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IPlugin { }
			public interface IRoaster { }
			internal sealed class Roaster(BeanStore store) : IPlugin, IRoaster { public BeanStore Store { get; } = store; }
			internal sealed class BeanStore { }

			[Module]
			[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, OnActivated = nameof(Wire))]
			public static partial class PluginModule
			{
				internal static void Wire(Roaster roaster, BeanStore store) { }
			}

			[Module][Singleton<BeanStore>] public static partial class StoreModule;
			""";

		private const string Container = """
			[Container][Import(typeof(PluginModule))][Import(typeof(StoreModule))]
			public static partial class {0};
			""";

		[Test]
		[Arguments("library")]
		[Arguments("same")]
		[Arguments("friend")]
		[Arguments("friendReference")]
		public async Task BuildsInTheLibraryAndAGrantedAssembly(string consumer)
		{
			GeneratorResult result = Run(Library, Container, consumer, "Other");

			await That(result.Diagnostics).IsEmpty()
				.Because("a container granted InternalsVisibleTo constructs the match itself and calls the internal hook wrapper, which takes the store it builds rather than its handle");
		}

		[Test]
		public async Task AContainerOfAGrantedAssembly_CallsTheInternalWrapperThatTakesTheServiceDirectly()
		{
			GeneratorResult result = Run(Library, Container, "friend");

			await That(ContainerSource(result)).Contains("Internals.InternalScanHook_OnActivated_Roaster_")
				.And.Contains("new global::Lib.Roaster(")
				.And.DoesNotContain("Hidden_BeanStore_");
			await That(Run(Library, Container, "library", "Other").Sources.Single(source => source.Key == "Awaiten.ModuleScan.Lib.PluginModule.g.cs").Value)
				.Contains("public const string ScanSkipped_Roaster_")
				.And.Contains("internal static void InternalScanHook_OnActivated_Roaster_")
				.And.DoesNotContain("InternalScan_Roaster_")
				.Because("the module generates no factory for the match, so it generates only the hook wrapper that takes the store directly");
		}
	}
}
