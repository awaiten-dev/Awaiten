using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     In an assembly with several modules, each module emits only the handles, instance handles, factories and aliases
///     that the generated members of some module take, since a container in another assembly reaches an internal
///     service through nothing else.
/// </summary>
public class ModuleHandleDemandTests
{
	private static uint Fnv1a(string text)
	{
		uint hash = 2166136261;
		foreach (char character in text)
		{
			hash = unchecked((hash ^ character) * 16777619);
		}

		return hash;
	}

	private static string Handle(string service)
		=> $"Hidden_{service}_{Fnv1a($"global::Lib.{service}"):x8}";

	private static string Instance(string implementation)
		=> $"HiddenInstance_{implementation}_{Fnv1a($"global::Lib.{implementation}"):x8}";

	private static string HiddenFactory(string implementation)
		=> $"HiddenRegistration_{implementation}_{Fnv1a($"global::Lib.{implementation}"):x8}";

	private static string ModuleSource(GeneratorResult result, string module)
		=> result.Sources.Single(source => source.Key == $"Awaiten.ModuleScan.Lib.{module}.g.cs").Value;

	private static string Container(params string[] modules) => $$"""
		[Container]
		{{string.Join("\n", modules.Select(module => $"[Import(typeof({module}))]"))}}
		public static partial class MyContainer;
		""";

	private static GeneratorResult Same(string library, params string[] modules)
		=> Generator.Run(library + Container(modules));

	private static GeneratorResult External(string library, params string[] modules)
		=> Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container(modules));

	private static GeneratorResult Friend(string library, params string[] modules)
		=> External(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\n\nnamespace Lib;"), modules);

	public class SeveralModules
	{
		private const string Library = """
			using System;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			public interface IRoaster { }
			internal interface IBeanStore { }
			internal interface ISilo { }
			internal interface IAudit { }
			internal interface IClock { }
			internal sealed class BeanStore : IBeanStore, IDisposable { public BeanStore(ISilo silo) { } public void Dispose() { } }
			internal sealed class Silo : ISilo { }
			internal sealed class Audit : IAudit { }
			internal sealed class Clock : IClock { }
			internal sealed class LoggingStore : IBeanStore, IDisposable { public LoggingStore(IBeanStore inner) { } public void Dispose() { } }
			internal sealed class AuditTrail : IAudit { public AuditTrail(IAudit inner) { } }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }
			internal sealed class Roaster : IRoaster { public Roaster(ISilo silo) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			[Singleton<Silo, ISilo>]
			[Singleton<Audit, IAudit>]
			[Singleton<Clock, IClock>]
			public static partial class StorageModule { }

			[Module]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }

			[Module]
			[Transient<Roaster, IRoaster>]
			public static partial class RoastingModule { }

			[Module]
			[Decorate<LoggingStore, IBeanStore>]
			[Decorate<AuditTrail, IAudit>]
			public static partial class LoggingModule { }
			""";

		private static readonly string[] Modules = ["StorageModule", "CoffeeModule", "RoastingModule", "LoggingModule",];

		[Test]
		public async Task TheModuleThatRegistersTheServices_EmitsOnlyWhatAnotherModuleTakes()
		{
			string storage = ModuleSource(Generator.Run(Library), "StorageModule");

			await That(storage).Contains($"public class {Handle("IBeanStore")}")
				.And.Contains($"public class {Handle("ISilo")}")
				.And.Contains($"public class {Handle("IAudit")}")
				.And.Contains($"{HiddenFactory("BeanStore")}(")
				.And.Contains($"{HiddenFactory("Silo")}(")
				.And.Contains($"{HiddenFactory("Audit")}(")
				.Because("the grinder takes the bean store, which takes the silo, the roaster takes the silo, and the logging module decorates the bean store and the audit");
			await That(storage).DoesNotContain("Clock")
				.Because("no generated member of any module takes the clock, so a container in another assembly cannot reach it through any handle, factory or alias");
		}

		[Test]
		public async Task TheHandleOfADecoratedService_HasAVariantOnlyForTheDisposalOfADecorator()
		{
			string storage = ModuleSource(Generator.Run(Library), "StorageModule");

			await That(storage).Contains($"internal sealed class Disposable : {Handle("IBeanStore")}, global::System.IDisposable")
				.Because("the logging module wraps its disposable decorator in the handle of the bean store, which another module declares");
			await That(storage).DoesNotContain($" : {Handle("IAudit")}, ")
				.And.DoesNotContain($" : {Handle("ISilo")}, ")
				.Because("no decorator of the audit or the silo needs a disposal of its own");
		}

		[Test]
		public async Task TheOtherModules_TakeTheHandlesOfTheModuleThatDeclaresThem()
		{
			GeneratorResult result = Generator.Run(Library);

			await That(ModuleSource(result, "CoffeeModule")).Contains($"global::Lib.StorageModule.Internals.{Handle("IBeanStore")}");
			await That(ModuleSource(result, "RoastingModule")).Contains($"global::Lib.StorageModule.Internals.{Handle("ISilo")}");
			await That(ModuleSource(result, "LoggingModule")).Contains($"global::Lib.StorageModule.Internals.{Handle("IAudit")}");
		}

		[Test]
		public async Task EveryContainer_BuildsTheServicesWithoutDiagnostics()
		{
			await That(Generator.Run(Library).Diagnostics).IsEmpty();
			await That(Same(Library, Modules).Diagnostics).IsEmpty();
			await That(Friend(Library, Modules).Diagnostics).IsEmpty();
			await That(External(Library, Modules).Diagnostics).IsEmpty()
				.Because("every handle a module takes is declared and built, whatever is left out");
		}

		[Test]
		public async Task AContainerInAnotherAssemblyImportingOneModule_TakesTheHandlesItNeedsFromAnotherImport()
		{
			await That(External(Library, "RoastingModule", "StorageModule").Diagnostics).IsEmpty();
			await That(External(Library, "RoastingModule").Diagnostics).Contains("*AWT238*Lib.RoastingModule*Lib.ISilo*Lib.StorageModule*").AsWildcard()
				.Because("the silo stays with the module that registers it, which the container has to import");
		}
	}

	public class AnImplementationAnotherModuleRegistersToo
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IFront { }
			internal interface IStore { }
			internal sealed class Store : IStore, IFront { }

			[Module]
			[Singleton<Store, IStore>(OnActivated = nameof(Activate))]
			public static partial class AModule
			{
			    internal static int Activations;

			    internal static void Activate(Store store) => Activations++;
			}

			[Module]
			[Singleton<Store, IFront>]
			public static partial class BModule { }
			""";

		[Test]
		public async Task IsBuiltThroughItsHandle_ThoughNothingTakesItsInternalService()
		{
			await That(ModuleSource(Generator.Run(Library), "AModule")).Contains($"{HiddenFactory("Store")}(")
				.And.Contains($"public class {Instance("Store")}")
				.Because("a container of the modules' assembly coalesces both registrations into one instance with the directives of the first, so a consumer needs the module's handle too");
		}

		[Test]
		public async Task EveryContainer_BuildsItWithoutDiagnostics()
		{
			await That(Generator.Run(Library).Diagnostics).IsEmpty();
			await That(Same(Library, "AModule", "BModule").Diagnostics).IsEmpty();
			await That(Friend(Library, "AModule", "BModule").Diagnostics).IsEmpty();
			await That(External(Library, "AModule", "BModule").Diagnostics).IsEmpty();
			await That(External(Library, "AModule", "BModule").Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"global::Lib.AModule.Internals.{HiddenFactory("Store")}(")
				.Because("the container builds the one instance through the handle of the module imported first, which runs its hook");
		}
	}

	public class APublicImplementationUnderAnInternalServiceOnly
	{
		private static string Library(bool severalModules) => $$"""
			using Awaiten;

			namespace Lib;

			public interface IFront { }
			internal interface IStore { }
			public sealed class Store : IStore, IFront { }
			internal interface IOther { }
			internal sealed class Other : IOther { }

			[Module]
			[Singleton<Store, IStore>(OnActivated = nameof(Activate))]
			public static partial class AModule
			{
			    public static int Activations;

			    internal static void Activate(Store store) => Activations++;
			}

			{{(severalModules ? "[Module]\n[Singleton<Other, IOther>]\npublic static partial class BModule { }" : "")}}
			""";

		private const string ImportOnly = """
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Container]
			[Import(typeof(AModule))]
			public static partial class MyContainer;
			""";

		private const string Coalescing = """
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Module]
			[Singleton<Store, IFront>]
			public static partial class LocalModule;

			[Container]
			[Import(typeof(AModule))]
			[Import(typeof(LocalModule))]
			public static partial class MyContainer;
			""";

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task IsBuiltThroughItsHandle(bool severalModules)
		{
			await That(ModuleSource(Generator.Run(Library(severalModules)), "AModule")).Contains($"{HiddenFactory("Store")}(")
				.Because("a container in another assembly can name the public implementation and register it itself, which then shares the handle's instance");
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task EveryContainer_ImportsTheModuleWithoutDiagnostics(bool severalModules)
		{
			string library = Library(severalModules);

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Same(library, "AModule").Diagnostics).IsEmpty();
			await That(Friend(library, "AModule").Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ImportOnly).Diagnostics).IsEmpty()
				.Because("the container registers the implementation's internal service through the handle, which it can name, not under the service, which it cannot");
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task ARegistrationOfTheConsumer_SharesTheHandlesInstanceAndItsHook(bool severalModules)
		{
			string library = Library(severalModules);

			await That(Generator.Run(library + Container("AModule")).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Coalescing).Diagnostics).IsEmpty()
				.Because("the consumer's registration coalesces with the handle, which runs the module's internal hook through its wrapper");
		}
	}

	public class AProviderThatCannotBuildTheService
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IChef { }
			internal interface IStore { }
			internal sealed class LockedStore : IStore { private LockedStore() { } }
			internal sealed class Store : IStore { }
			internal sealed class Chef : IChef { public Chef(IStore store) { } }

			[Module]
			[Singleton<LockedStore, IStore>]
			public static partial class AModule { }

			[Module]
			[Singleton<Store, IStore>]
			public static partial class BModule { }

			[Module]
			[Singleton<Chef, IChef>]
			public static partial class CModule { }
			""";

		[Test]
		public async Task StillDeclaresTheHandleTheOtherModulesTake()
		{
			GeneratorResult result = Generator.Run(Library);

			await That(result.Diagnostics).IsEmpty()
				.Because("the module first by name declares the handle of the service, though it builds nothing into it");
			await That(ModuleSource(result, "AModule")).Contains($"public class {Handle("IStore")}");
			await That(ModuleSource(result, "BModule")).Contains($"global::Lib.AModule.Internals.{Handle("IStore")}");
		}

		[Test]
		public async Task EveryContainer_BuildsTheServiceOfAnotherModuleWithoutDiagnostics()
		{
			await That(Same(Library, "BModule", "CModule").Diagnostics).IsEmpty();
			await That(Friend(Library, "BModule", "CModule").Diagnostics).IsEmpty();
			await That(External(Library, "BModule", "CModule").Diagnostics).IsEmpty()
				.Because("the chef takes the store the second module builds through the handle the first one declares");
		}
	}
}
