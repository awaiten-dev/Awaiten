namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     An internal <c>Factory</c> member or lifecycle hook of a public module: a container in another assembly calls it
///     through what the module generates (a public wrapper, or an instance handle for a factory taking internal
///     services), while a container in the module's assembly or in a friend calls it directly.
/// </summary>
public class ModuleInternalMemberTests
{
	private const string ExternalContainer = """
		using Awaiten;
		using Lib;

		namespace MyCode;

		[Container]
		[Import(typeof(CoffeeModule))]
		public static partial class MyContainer;
		""";

	private const string SameAssemblyContainer = """

		[Container]
		[Import(typeof(CoffeeModule))]
		public static partial class SameContainer;
		""";

	private static (GeneratorResult Library, GeneratorResult Same, GeneratorResult External, GeneratorResult Friend) RunAll(string library)
		=> (Generator.Run(library),
			Generator.Run(library + SameAssemblyContainer),
			Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer),
			Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"), ExternalContainer));

	private static string Library(string registration, string member) => $$"""
		using Awaiten;
		using System.Threading;
		using System.Threading.Tasks;

		namespace Lib;

		public interface IGrinder { }
		public interface IPlugin { }
		public abstract class PluginBase : IPlugin { }
		public sealed class Plugin : PluginBase { }
		internal sealed class BeanStore { }
		internal sealed class Settings { }
		internal interface IBeans { }
		internal sealed class Beans : IBeans { }
		internal sealed class Grinder(BeanStore store) : IGrinder { public BeanStore Store { get; } = store; }

		[Module]
		{{registration}}
		public static partial class CoffeeModule
		{
			{{member}}
		}
		""";

	public class Factories
	{
		[Test]
		[Arguments("[Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store) => new Grinder(store);")]
		[Arguments("[Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static Task<IGrinder> CreateGrinder(BeanStore store) => Task.FromResult<IGrinder>(new Grinder(store));")]
		[Arguments("[Singleton<BeanStore>][Singleton<Grinder, IGrinder>(Factory = nameof(CreateGrinder))]", "internal static Grinder CreateGrinder(BeanStore store) => new Grinder(store);")]
		[Arguments("[Singleton<Beans, IBeans>][Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store, IBeans beans) => new Grinder(store);")]
		public async Task WithInternalParameters_IsUsableEverywhere(string registration, string member)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(registration, member));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty()
				.Because("a container in the module's assembly calls the internal factory directly");
			await That(external.Diagnostics).IsEmpty()
				.Because("the module builds what the factory produces through an instance handle, which takes the internal services through their handles");
			await That(friend.Diagnostics).IsEmpty()
				.Because("a friend calls the internal factory directly and builds the internal services itself");
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.");
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.CreateGrinder(");
		}

		[Test]
		[Arguments("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store) => new Grinder(store);", "'CreateGrinder' is internal and its parameter 'store' has the type 'Lib.BeanStore', which is not public, so the module generated no public wrapper for it")]
		[Arguments("[Singleton<Plugin>(Factory = nameof(CreatePlugin))]", "internal static Plugin CreatePlugin(BeanStore store) => new Plugin();", "'CreatePlugin' is internal and its parameter 'store' has the type 'Lib.BeanStore', which is not public, so the module generated no public wrapper for it")]
		[Arguments("[Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store) => new Grinder(store); internal static IGrinder CreateGrinder() => new Grinder(new BeanStore());", "the module has more than one method 'CreateGrinder' returning 'Lib.IGrinder' that is not public, so it generated no public wrapper")]
		public async Task WithoutWrapperOrHandle_ExternalAwt108NamesTheCause(string registration, string member, string cause)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(registration, member));

			await That(library.Diagnostics).IsEmpty()
				.Because("a container of the module's assembly may still call the factory itself");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101") || diagnostic.Contains("AWT112"));
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101") || diagnostic.Contains("AWT112"))
				.Because("a friend sees the internal factory and reports what a container of the module's assembly reports");
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT108") && diagnostic.Contains(cause))
				.Because("a container in another assembly does not see the internal factory, so the module records why it generated no wrapper for it");
		}

		[Test]
		public async Task TakingAnInternalServiceThatCannotBeBuilt_ExternalAwt108NamesTheService()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(
				Library("[Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store) => new Grinder(store);")
					.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains("'Lib.BeanStore' requires 'Lib.Settings'"));
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains("'Lib.BeanStore' requires 'Lib.Settings'"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT108") && diagnostic.Contains("its parameter 'store' takes the internal service 'Lib.BeanStore', which the module cannot build (AWT195: ") && diagnostic.Contains("'Lib.Settings'"))
				.Because("the factory would take the internal service through its handle, so the cause is what keeps the module from building that service, which the module's own build does not report while nothing it emits needs the service");
		}

		[Test]
		public async Task TakingAnInternalServiceAndAGenericOverAnInternalType_ExternalAwt108NamesTheGeneric()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(
				Library("[Singleton(typeof(Log<>), typeof(ILog<>))][Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store, ILog<Grinder> log) => new Grinder(store);")
					.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic interface ILog<T> { }\npublic sealed class Log<T> : ILog<T> { }"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT108: 'Lib.IGrinder' cannot be produced: the module 'Lib.CoffeeModule' has no accessible method 'CreateGrinder' returning 'Lib.IGrinder': 'CreateGrinder' is internal and its parameter 'log' has the type 'Lib.ILog<Lib.Grinder>', which is not public, so the module generated no public wrapper for it; make the method and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly")
				.Because("the handle of the internal service would serve 'store', so the parameter that keeps the module from wrapping the factory is 'log'");
		}

		[Test]
		public async Task WithAHandleTheModuleCannotBuild_ExternalAwt108NamesTheRejection()
		{
			string library = Library("[Singleton<BeanStore>][Singleton<GrinderBase>(Factory = nameof(CreateGrinder))][AsyncInitializer<GrinderBase>(nameof(GrinderBase.InitAsync))]", "internal static GrinderBase CreateGrinder(BeanStore store) => new BaseGrinder();")
				.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic abstract class GrinderBase { internal Task InitAsync(Settings settings) => Task.CompletedTask; }\ninternal sealed class BaseGrinder : GrinderBase { }");
			const string expected = "error AWT108: 'Lib.GrinderBase' cannot be produced: the module 'Lib.CoffeeModule' has no accessible method 'CreateGrinder' returning 'Lib.GrinderBase': 'CreateGrinder' is internal and the module cannot build what it produces through a handle (AWT234: 'Lib.GrinderBase' is registered under services whose one instance a consuming container shares through a generated handle is initialized by the async initializer 'Lib.GrinderBase.InitAsync', but its parameter of type 'Lib.Settings' is not accessible outside the module's assembly, so a consumer in another assembly cannot resolve it; widen the parameter type's accessibility or register it in the module), so it generated neither a public wrapper nor a handle for it; make the method and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("a container of the module's assembly may still call the factory and the convention itself");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the handle serves the internal parameter, so what keeps the module from serving the factory is why it cannot build the handle");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task Generic_IsReportedAsUninferableEverywhere()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder<T>() => new Grinder(new BeanStore());"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT108") && diagnostic.Contains("cannot infer its type parameter(s) 'T'"))
				.Because("no wrapper or recorded cause can help a generic factory of a closed registration, so the module reports it in its own build");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT108") && diagnostic.Contains("cannot infer its type parameter(s) 'T'"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT108") && !diagnostic.Contains("generated no public wrapper"))
				.Because("the module records no wrapper cause for a generic factory, since a wrapper is not what it lacks, and its own build already reports it precisely");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT108") && diagnostic.Contains("cannot infer its type parameter(s) 'T'"));
		}
	}

	public class InternalServiceOfAnotherModule
	{
		private const string TwoModuleContainer = """
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Container]
			[Import(typeof(CoffeeModule))]
			[Import(typeof(StoreModule))]
			public static partial class MyContainer;
			""";

		private static string TwoModules(string registration, string member) => Library(registration, member) + """

			[Module]
			[Singleton<BeanStore>]
			public static partial class StoreModule;
			""";

		[Test]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, BeanStore store) { }")]
		[Arguments("[Singleton<Plugin>(Factory = nameof(Create))]", "internal static Plugin Create(BeanStore store) => new Plugin();")]
		public async Task IsTakenThroughTheOtherModulesHandle(string registration, string member)
		{
			string library = TwoModules(registration, member);

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.Run(library + "\n[Container][Import(typeof(CoffeeModule))][Import(typeof(StoreModule))] public static partial class SameContainer;").Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, TwoModuleContainer).Diagnostics).IsEmpty()
				.Because("the module passes the internal service on from the handle of the module that registers it");
			await That(Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"), TwoModuleContainer).Diagnostics).IsEmpty();
		}

		[Test]
		public async Task ThatTheOtherModuleCannotBuild_ExternalAwt108NamesTheServiceAndTheModule()
		{
			string library = TwoModules("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store) => new Grinder(store);")
				.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }");

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"), TwoModuleContainer).Diagnostics)
				.HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains("'Lib.BeanStore' requires 'Lib.Settings'"));
			await That(Generator.RunWithGeneratedReferencedAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT108: 'Lib.IGrinder' cannot be produced: the module 'Lib.CoffeeModule' has no accessible method 'CreateGrinder' returning 'Lib.IGrinder': 'CreateGrinder' is internal and its parameter 'store' takes the internal service 'Lib.BeanStore', which the module 'Lib.StoreModule' cannot build (AWT195: 'Lib.BeanStore' is registered under the internal service 'Lib.BeanStore', which a consuming container reaches through a generated handle, but its constructor parameter 'settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module), so the module generated neither a public wrapper nor a handle for it; make the method and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly")
				.Because("the factory would take the internal service through the handle of the module that registers it, so the cause is what keeps that module from building it");
		}

		[Test]
		public async Task ThatOnlyOneOfTwoOtherModulesCanBuild_ExternalAwt108NamesTheBlockingParameter()
		{
			string library = (Library("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder(BeanStore store, ILog<Grinder> log) => new Grinder(store);") + """

					[Module]
					[Singleton<BeanStore>]
					public static partial class AStoreModule;

					[Module]
					[Singleton<BeanStore>(Factory = nameof(Create))]
					public static partial class BStoreModule
					{
						internal static BeanStore Create() => new BeanStore(new Settings());
					}
					""")
				.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }")
				.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic interface ILog<T> { }")
				.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Other\")]\nnamespace Lib;");
			const string container = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(AStoreModule))]
				[Import(typeof(BStoreModule))]
				public static partial class MyContainer;
				""";

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library.Replace(", ILog<Grinder> log", ""), container).Diagnostics).IsEmpty()
				.Because("the factory takes the internal service through the handle that 'Lib.BStoreModule' can build");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, container).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT108: 'Lib.IGrinder' cannot be produced: the module 'Lib.CoffeeModule' has no accessible method 'CreateGrinder' returning 'Lib.IGrinder': 'CreateGrinder' is internal and its parameter 'log' has the type 'Lib.ILog<Lib.Grinder>', which is not public, so the module generated no public wrapper for it; make the method and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly")
				.Because("another module that registers the internal service can build it, so its handle serves 'store' and the parameter that blocks the wrapper is 'log'");
		}
		[Test]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, BeanStore store) { }", "registers 'Lib.Plugin' with the OnActivated hook 'Ready', which this container cannot call, and the module's assembly generated no wrapper for it")]
		[Arguments("[Singleton<InternalPlugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(InternalPlugin plugin, BeanStore store) { }", "registers 'Lib.InternalPlugin', which this container cannot construct, and the module's assembly generated no OnActivated hook wrapper for it")]
		[Arguments("[Singleton<IGrinder>(Factory = nameof(CreateGrinder), OnActivated = nameof(Ready))]", "public static IGrinder CreateGrinder() => new Grinder(new BeanStore(new Settings())); internal static void Ready(IGrinder grinder, BeanStore store) { }", "registers 'Lib.IGrinder' with the OnActivated hook 'Ready', which this container cannot call, and the module's assembly generated no wrapper for it")]
		public async Task HookTakingOneThatTheOtherModuleCannotBuild_ExternalAwt231NamesTheServiceAndTheModule(string registration, string member, string subject)
		{
			string library = TwoModules(registration, member)
				.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }")
				.Replace("public sealed class Plugin : PluginBase { }", "public sealed class Plugin : PluginBase { }\ninternal sealed class InternalPlugin : IPlugin { }");
			string expected = $"error AWT231: The imported module 'Lib.CoffeeModule' {subject} because the parameter 'store' of its OnActivated hook needs the internal service 'Lib.BeanStore', which the module 'Lib.StoreModule' cannot build (AWT195: 'Lib.BeanStore' is registered under the internal service 'Lib.BeanStore', which a consuming container reaches through a generated handle, but its constructor parameter 'settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module); grant this assembly InternalsVisibleTo";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("a container of the module's assembly calls the hook directly and may register what the internal service needs");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.Contains(expected)
				.Because("the wrapper would take the internal service through the handle of the module that registers it, so the cause is what keeps that module from building it");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.Contains(expected);
		}

		private const string StoreRejection = "(AWT195: 'Lib.BeanStore' is registered under the internal service 'Lib.BeanStore', which a consuming container reaches through a generated handle, but its constructor parameter 'settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module)";

		[Test]
		[Arguments("internal sealed class Grinder(BeanStore store) : IGrinder { public BeanStore Store { get; } = store; }", "its constructor parameter 'store'")]
		[Arguments("internal sealed class Grinder : IGrinder { [Inject] public BeanStore Store { get; set; } = null!; }", "its property 'Store'")]
		public async Task RegistrationTakingOneThatTheOtherModuleCannotBuild_NamesTheServiceAndTheModule(string grinder, string member)
		{
			string library = TwoModules("[Singleton<Grinder, IGrinder>]", "")
				.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }")
				.Replace("internal sealed class Grinder(BeanStore store) : IGrinder { public BeanStore Store { get; } = store; }", grinder);
			string friendLibrary = library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Other\")]\nnamespace Lib;");
			string expected = $"error AWT231: The imported module 'Lib.CoffeeModule' registers 'Lib.Grinder', which this container cannot construct, and the module's assembly generated no factory for it because {member} needs the internal service 'Lib.BeanStore', which the module 'Lib.StoreModule' cannot build {StoreRejection}; grant this assembly InternalsVisibleTo in the module's assembly, or make 'Lib.Grinder', its constructor and the constructor's parameter types public";

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.EndsWith(
					$"error AWT195: 'Lib.Grinder' is self-compiled from a module registration, but {member} needs the internal service 'Lib.BeanStore', which the module 'Lib.StoreModule' cannot build {StoreRejection}, so the generated factory cannot take it; fix what keeps the module from building that service")
				.Because("the module that registers the internal service could share its handle but cannot build it, which nothing else reports, so the registration that needs it names why");
			await That(Generator.Run(friendLibrary).Diagnostics).IsEmpty()
				.Because("an assembly granted InternalsVisibleTo constructs the implementation itself");
			await That(Generator.RunWithGeneratedReferencedAssembly(friendLibrary, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module records the same cause for a container that cannot construct the implementation");
			await That(Generator.RunWithGeneratedReferenceAssembly(friendLibrary, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		[Arguments("[Decorate<LoggingGrinder, IGrinder>]", "decorator", "Lib.LoggingGrinder", false)]
		[Arguments("[Composite<AllGrinders, IGrinder>]", "composite", "Lib.AllGrinders", false)]
		[Arguments("[Decorate<LoggingGrinder, IGrinder>]", "decorator", "Lib.LoggingGrinder", true)]
		[Arguments("[Composite<AllGrinders, IGrinder>]", "composite", "Lib.AllGrinders", true)]
		public async Task DecoratorOrCompositeTakingOneThatAModuleCannotBuild_ExternalAwt231NamesTheService(string contribution, string kind, string type, bool sameModule)
		{
			string library = $$"""
				using Awaiten;
				using System.Collections.Generic;

				namespace Lib;

				public interface IGrinder { }
				public sealed class PublicGrinder : IGrinder { }
				public sealed class Cup { }
				internal sealed class Settings { }
				internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }
				internal sealed class LoggingGrinder(IGrinder inner, BeanStore store) : IGrinder { public IGrinder Inner { get; } = inner; public BeanStore Store { get; } = store; }
				internal sealed class AllGrinders(IEnumerable<IGrinder> all, BeanStore store) : IGrinder { public IEnumerable<IGrinder> All { get; } = all; public BeanStore Store { get; } = store; }

				[Module]
				[Singleton<PublicGrinder, IGrinder>]
				{{(sameModule ? "[Singleton<BeanStore>]" : "")}}
				{{contribution}}
				public static partial class CoffeeModule;

				[Module]
				[Singleton<Cup>]
				{{(sameModule ? "" : "[Singleton<BeanStore>]")}}
				public static partial class StoreModule;
				""";
			string cause = sameModule ? "the module cannot build" : $"the module 'Lib.StoreModule' cannot build {StoreRejection}";
			string expected = $"error AWT231: The imported module 'Lib.CoffeeModule' declares the {kind} '{type}', which this container cannot construct, and the module's assembly generated no factory for it because its constructor parameter 'store' needs the internal service 'Lib.BeanStore', which {cause}; grant this assembly InternalsVisibleTo in the module's assembly, or make '{type}', its constructor and the constructor's parameter types public";

			GeneratorResult result = Generator.Run(library);
			if (sameModule)
			{
				await That(result.Diagnostics).HasSingle().Which.EndsWith("error AWT195: 'Lib.BeanStore' is registered under the internal service 'Lib.BeanStore', which a consuming container reaches through a generated handle, but its constructor parameter 'settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module")
					.Because("the module reports why it cannot build its own service, which the factory of the contribution needs, as for a registration");
			}
			else
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("a container of the module's assembly constructs the contribution itself and may register what the internal service needs");
			}

			await That(Generator.RunWithGeneratedReferencedAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module records why it generated no factory for the contribution, so a container in another assembly names the service instead of AWT230");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task RegistrationsOfModulesTakingEachOthersUnbuildableServices_AreJudgedWithoutRecursing()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Settings { }
				internal sealed class Kettle(Silo silo) { public Silo Silo { get; } = silo; }
				internal sealed class Silo(Kettle kettle, Settings settings) { public Kettle Kettle { get; } = kettle; public Settings Settings { get; } = settings; }
				internal sealed class Grinder(Kettle kettle) : IGrinder { public Kettle Kettle { get; } = kettle; }

				[Module][Singleton<Kettle>][Singleton<Grinder, IGrinder>] public static partial class CoffeeModule;
				[Module][Singleton<Silo>] public static partial class StoreModule;
				""";
			const string expected = "error AWT231: The imported module 'Lib.CoffeeModule' registers 'Lib.Grinder', which this container cannot construct, and the module's assembly generated no factory for it because its constructor parameter 'kettle' needs the internal service 'Lib.Kettle', which the module cannot build; grant this assembly InternalsVisibleTo in the module's assembly, or make 'Lib.Grinder', its constructor and the constructor's parameter types public";

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT195: 'Lib.Kettle' is registered under the internal service 'Lib.Kettle', which a consuming container reaches through a generated handle, but its constructor parameter 'silo' needs the internal service 'Lib.Silo', which the module 'Lib.StoreModule' cannot build (AWT195: 'Lib.Silo' is registered under the internal service 'Lib.Silo', which a consuming container reaches through a generated handle, but its constructor parameter 'settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module), so the generated factory cannot take it; fix what keeps the module from building that service")
				.Because("the silo fails on the settings whatever the kettle it takes back, so the cycle is named by that cause once, and judging the modules must not ask the other module back while they are judged");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module's build names why it cannot build the kettle");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task ModulesTakingEachOthersServices_WhereAPropertyCannotBeSet_NameThatProperty()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Settings { }
				internal sealed class Kettle(Silo silo) { public Silo Silo { get; } = silo; }
				internal sealed class Silo(Kettle kettle) { public Kettle Kettle { get; } = kettle; [Inject] public Settings Settings { get; set; } = null!; }
				internal sealed class Grinder(Kettle kettle) : IGrinder { public Kettle Kettle { get; } = kettle; }

				[Module][Singleton<Kettle>][Singleton<Grinder, IGrinder>] public static partial class CoffeeModule;
				[Module][Singleton<Silo>] public static partial class StoreModule;
				""";

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT195: 'Lib.Kettle' is registered under the internal service 'Lib.Kettle', which a consuming container reaches through a generated handle, but its constructor parameter 'silo' needs the internal service 'Lib.Silo', which the module 'Lib.StoreModule' cannot build (AWT200: 'Lib.Silo' is registered under the internal service 'Lib.Silo', which a consuming container reaches through a generated handle, but its property 'Settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, which the generated factory cannot mirror and would silently drop; remove the attribute, register it in the module, or register it through a hand-written module factory), so the generated factory cannot take it; fix what keeps the module from building that service")
				.Because("the silo fails on the property it cannot set whatever the kettle its constructor takes back");
		}

		[Test]
		public async Task PropertyTakingOneThatTakesAServiceTheOtherModuleCannotBuild_NamesTheRootCause()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Secret { }
				internal sealed class Kettle(Silo silo) { public Silo Silo { get; } = silo; }
				internal sealed class Silo(Secret secret) { public Secret Secret { get; } = secret; }
				internal sealed class Grinder : IGrinder { [Inject] public Kettle K { get; set; } = null!; }

				[Module][Singleton<Kettle>][Singleton<Grinder, IGrinder>] public static partial class CoffeeModule;
				[Module][Singleton<Silo>] public static partial class StoreModule;
				""";
			const string expected = "error AWT231: The imported module 'Lib.CoffeeModule' registers 'Lib.Grinder', which this container cannot construct, and the module's assembly generated no factory for it because its property 'K' needs the internal service 'Lib.Kettle', which the module cannot build; grant this assembly InternalsVisibleTo in the module's assembly, or make 'Lib.Grinder', its constructor and the constructor's parameter types public";

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT195: 'Lib.Kettle' is registered under the internal service 'Lib.Kettle', which a consuming container reaches through a generated handle, but its constructor parameter 'silo' needs the internal service 'Lib.Silo', which the module 'Lib.StoreModule' cannot build (AWT195: 'Lib.Silo' is registered under the internal service 'Lib.Silo', which a consuming container reaches through a generated handle, but its constructor parameter 'secret' has the type 'Lib.Secret', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module), so the generated factory cannot take it; fix what keeps the module from building that service")
				.Because("registering the silo in this module too would not help, so the message names why the other module cannot build it instead of asking to move it");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(Generator.RunWithGeneratedReferenceAssembly(library, TwoModuleContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task HooksOfModulesTakingEachOthersUnbuildableServices_AreJudgedWithoutRecursing()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				internal sealed class Secret { }
				internal sealed class Kettle { }
				internal sealed class Beans(Silo silo) { public Silo Silo { get; } = silo; }
				internal sealed class Grain(Beans beans) { public Beans Beans { get; } = beans; }
				internal sealed class Silo(Grain grain) { public Grain Grain { get; } = grain; }

				[Module]
				[Singleton<Kettle>(OnActivated = nameof(Warm))]
				[Singleton<Beans>]
				public static partial class CoffeeModule
				{
					internal static void Warm(Kettle kettle, Beans beans) { }
				}

				[Module]
				[Singleton<Grain>]
				[Singleton<Silo>(OnActivated = nameof(Fill))]
				public static partial class StoreModule
				{
					internal static void Fill(Silo silo, Secret secret) { }
				}
				""";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("while the modules are judged, the hook asks why its module cannot build the beans, which must not follow the chain through the silo and the grain back into this module");
		}

		[Test]
		[Arguments("internal sealed class BeanStore { }")]
		[Arguments("internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }")]
		public async Task ArgOfOneThatTheOtherModuleRegisters_IsNotAccessible(string store)
		{
			string library = TwoModules("[Singleton<Grinder, IGrinder>]", "")
				.Replace("internal sealed class BeanStore { }", store)
				.Replace("internal sealed class Grinder(BeanStore store)", "internal sealed class Grinder([Arg] BeanStore store)");

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.Contains(
					"error AWT195: 'Lib.Grinder' is self-compiled from a module registration, but its constructor parameter 'store' has the type 'Lib.BeanStore', which is not accessible outside the module's assembly, so the generated factory cannot expose it")
				.Because("the caller passes an [Arg] to the factory and has to name its type, whatever another module that registers the type can build");
		}

		[Test]
		public async Task ScanHookTakingOneThatTheOtherModuleCannotBuild_WhileTheModulesAreJudged_IsLeftToTheContainers()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IPlugin { }
				public interface IRoaster { }
				internal sealed class Roaster : IPlugin, IRoaster { }
				internal sealed class Beans { }
				internal sealed class Settings { }
				internal sealed class Secret { }
				internal sealed class BeanStore(Settings settings, Secret secret) { public Settings Settings { get; } = settings; public Secret Secret { get; } = secret; }

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
			const string container = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container][Import(typeof(PluginModule))][Import(typeof(StoreModule))][Import(typeof(SettingsModule))]
				public static partial class MyContainer;
				""";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("judging which internal services the modules can build mirrors the scan hook, whose cause, judged from the other module, must not ask for that judgement again");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, container).Diagnostics).HasSingle().Which.EndsWith(
				"error AWT231: The imported module 'Lib.PluginModule' registers 'Lib.Roaster', which this container cannot construct, and the module's assembly generated no factory for it because the parameter 'store' of its OnActivated hook needs the internal service 'Lib.BeanStore', which the module 'Lib.StoreModule' cannot build (AWT195: 'Lib.BeanStore' is registered under the internal service 'Lib.BeanStore', which a consuming container reaches through a generated handle, but its constructor parameter 'secret' has the type 'Lib.Secret', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module); grant this assembly InternalsVisibleTo in the module's assembly, or change the module so its [Scan] can generate a factory for 'Lib.Roaster'");
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task ScanHookTakingOneThatAModuleCannotBuild_IsLeftToTheContainers(bool sameModule)
		{
			string library = $$"""
				using Awaiten;

				namespace Lib;

				public interface IPlugin { }
				public interface IRoaster { }
				internal sealed class Roaster : IPlugin, IRoaster { }
				public sealed class Cup { }
				internal sealed class Beans { }
				internal sealed class Settings { }
				internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }

				[Module]
				[Singleton<Beans>]
				{{(sameModule ? "[Singleton<BeanStore>]" : "")}}
				[Scan<IPlugin>(As = ScanAs.MatchingInterface, OnActivated = nameof(Wire))]
				public static partial class PluginModule
				{
					internal static void Wire(Roaster roaster, BeanStore store) { }
				}

				[Module]
				[Singleton<Cup>]
				{{(sameModule ? "" : "[Singleton<BeanStore>]")}}
				public static partial class StoreModule;
				""";
			const string container = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(PluginModule))]
				[Import(typeof(StoreModule))]
				public static partial class MyContainer;
				""";
			string builder = sameModule ? "the module" : "the module 'Lib.StoreModule'";
			string expected = $"error AWT231: The imported module 'Lib.PluginModule' registers 'Lib.Roaster', which this container cannot construct, and the module's assembly generated no factory for it because the parameter 'store' of its OnActivated hook needs the internal service 'Lib.BeanStore', which {builder} cannot build {StoreRejection}; grant this assembly InternalsVisibleTo in the module's assembly, or change the module so its [Scan] can generate a factory for 'Lib.Roaster'";

			GeneratorResult result = Generator.Run(library);
			if (sameModule)
			{
				await That(result.Diagnostics).HasSingle().Which.EndsWith("error AWT195: 'Lib.BeanStore' is registered under the internal service 'Lib.BeanStore', which a consuming container reaches through a generated handle, but its constructor parameter 'settings' has the type 'Lib.Settings', which is not accessible outside the module's assembly, so the generated factory cannot expose it; widen the parameter type's accessibility or register it in the module")
					.Because("the module reports why it cannot build its own service, which the hook needs, instead of the accessibility of the hook's parameter");
			}
			else
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("a container of the module's assembly calls the hook directly");
			}

			await That(Generator.RunWithGeneratedReferencedAssembly(library, container).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module skips the match whose hook it cannot wrap and records why, as for the hook of a registration");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, container).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task FactoryHookTakingOneThatTheModuleCannotBuild_ExternalAwt231NamesTheServiceAndWhy()
		{
			string library = Library("[Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder), OnActivated = nameof(Ready))]", "public static IGrinder CreateGrinder() => new Grinder(new BeanStore(new Settings())); internal static void Ready(IGrinder grinder, BeanStore store) { }")
				.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }");
			string expected = $"error AWT231: The imported module 'Lib.CoffeeModule' registers 'Lib.IGrinder' with the OnActivated hook 'Ready', which this container cannot call, and the module's assembly generated no wrapper for it because the parameter 'store' of its OnActivated hook needs the internal service 'Lib.BeanStore', which the module cannot build {StoreRejection}; grant this assembly InternalsVisibleTo in the module's assembly, or make the hook and its parameter types public";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("a container of the module's assembly calls the hook of the factory directly and may register what the internal service needs, as for the hook of a constructed registration");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module's build does not report why it cannot build the service, so the consumer names it");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}
	}

	public class NonPartialModule
	{
		private static string NonPartial(string registration, string member)
			=> Library(registration, member).Replace("public static partial class CoffeeModule", "public static class CoffeeModule");

		[Test]
		public async Task WithAnInternalFactory_ExternalAwt108NamesTheMissingPartial()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(NonPartial("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder() => new Grinder(new BeanStore());"));

			await That(library.Diagnostics).IsEmpty()
				.Because("a container of the module's assembly calls the internal factory directly, so the module only records that it carries no wrapper");
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT108: 'Lib.IGrinder' cannot be produced: the module 'Lib.CoffeeModule' has no accessible method 'CreateGrinder' returning 'Lib.IGrinder': this assembly does not see a method internal to the module's assembly, and the module generated no public wrapper for one, because the module, or a class that contains it, is not partial; make the method and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly")
				.Because("a module that is not partial receives no wrapper, which it records in a marker type that metadata carries");
		}

		[Test]
		public async Task WithAnInternalHook_ExternalAwt164NamesTheMissingPartial()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(NonPartial("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin) { }"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT164: 'Lib.IPlugin' cannot use the lifecycle hook 'Ready': the module 'Lib.CoffeeModule' has no accessible static method 'Ready' accepting 'Lib.Plugin' and returning void, Task or ValueTask; this assembly does not see a method internal to the module's assembly, and the module generated no wrapper for one, because the module, or a class that contains it, is not partial")
				.Because("a module that is not partial receives no wrapper, which it records in a marker type that metadata carries");
		}

		[Test]
		public async Task WithAnInternalFactory_InAReferenceAssembly_ExternalAwt108NamesTheMissingPartial()
		{
			GeneratorResult external = Generator.RunWithGeneratedReferenceAssembly(NonPartial("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder() => new Grinder(new BeanStore());"), ExternalContainer);

			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT108") && diagnostic.Contains("because the module, or a class that contains it, is not partial"))
				.Because("a reference assembly drops internal members but keeps the internal marker type");
		}

		[Test]
		public async Task WithAnInternalFactoryAndAnInternalOpenGeneric_ExternalAwt231NamesTheMissingPartial()
		{
			string library = NonPartial("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))][Singleton(typeof(Repo<>), typeof(IRepo<>))]", "internal static IGrinder CreateGrinder() => new Grinder(new BeanStore());")
				.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic interface IRepo<T> { }\ninternal sealed class Repo<T> : IRepo<T> { }");

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

			await That(external.Diagnostics).Contains(diagnostic => diagnostic.Contains("error AWT231: The imported module 'Lib.CoffeeModule' registers 'Lib.Repo<T>', which this container cannot construct, and the module's assembly generated no factory for it because the module, or a class that contains it, is not partial"))
				.Because("the marker the module records for its internal factory names why it carries no factory for the open generic either");
		}

		[Test]
		public async Task WithAnInternalInstance_ExternalAwt109NamesTheMissingPartial()
		{
			string library = NonPartial("[Singleton<IGrinder>(Instance = nameof(Shared))]", "internal static IGrinder Shared = new Grinder(new BeanStore());");
			(GeneratorResult result, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(library);
			const string expected = "error AWT109: 'Lib.IGrinder' cannot be exposed: the module 'Lib.CoffeeModule' has no accessible field or property 'Shared' of type 'Lib.IGrinder': this assembly does not see a field or property internal to the module's assembly, and the module generated no public wrapper for one, because the module, or a class that contains it, is not partial; make the member and its type public, or grant this assembly InternalsVisibleTo in the module's assembly";

			await That(result.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("a module that is not partial receives no wrapper, which it records in a marker type that metadata carries");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		[Arguments("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "internal static IGrinder CreateGrinder() => new Grinder(new BeanStore());", "CreateGrinder")]
		[Arguments("[Singleton<IGrinder>(Instance = nameof(Shared))]", "internal static IGrinder Shared = new Grinder(new BeanStore());", "Shared")]
		public async Task WithAnInternalMember_SeenThroughACompilationReference_Awt153NamesTheMissingPartial(string registration, string member, string name)
		{
			GeneratorResult external = Generator.RunWithGeneratedCompilationReference(NonPartial(registration, member), ExternalContainer);

			await That(external.Diagnostics).HasSingle().Which.EndsWith(
					$"error AWT153: 'Lib.IGrinder' cannot be produced: the member '{name}' exists on the module 'Lib.CoffeeModule' but is not accessible from the generated container: the module generated no public wrapper for it, because the module, or a class that contains it, is not partial; make it public, or internal within a visible assembly")
				.Because("the IDE sees the internal member of a project reference but cannot call it, so the message names why the module carries no wrapper for it");
		}

		[Test]
		public async Task WithOnlyAnInternalOpenGeneric_ExternalAwt231NamesTheMissingPartial()
		{
			string library = NonPartial("[Singleton(typeof(Repo<>), typeof(IRepo<>))]", "")
				.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic interface IRepo<T> { }\ninternal sealed class Repo<T> : IRepo<T> { }");
			const string expected = "error AWT231: The imported module 'Lib.CoffeeModule' registers 'Lib.Repo<T>', which this container cannot construct, and the module's assembly generated no factory for it because the module, or a class that contains it, is not partial; grant this assembly InternalsVisibleTo in the module's assembly, or make 'Lib.Repo<T>', its constructor and the constructor's parameter types public";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("the module never fails its build over an open generic");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module records that it is not partial, so a container in another assembly names why it carries no generic factory");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		[Arguments("[Decorate<LoggingGrinder, IGrinder>]", "decorator", "Lib.LoggingGrinder")]
		[Arguments("[Composite<AllGrinders, IGrinder>]", "composite", "Lib.AllGrinders")]
		public async Task WithAnInternalDecoratorOrComposite_ExternalAwt231NamesTheMissingPartial(string contribution, string kind, string type)
		{
			string library = NonPartial("[Singleton<PublicGrinder, IGrinder>]" + contribution, "")
				.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic sealed class PublicGrinder : IGrinder { }\ninternal sealed class LoggingGrinder(IGrinder inner) : IGrinder { public IGrinder Inner { get; } = inner; }\ninternal sealed class AllGrinders(System.Collections.Generic.IEnumerable<IGrinder> all) : IGrinder { public System.Collections.Generic.IEnumerable<IGrinder> All { get; } = all; }");
			string expected = $"error AWT231: The imported module 'Lib.CoffeeModule' declares the {kind} '{type}', which this container cannot construct, and the module's assembly generated no factory for it because the module, or a class that contains it, is not partial; grant this assembly InternalsVisibleTo in the module's assembly, or make '{type}', its constructor and the constructor's parameter types public";

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("a container of the module's assembly constructs the internal type itself");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module records that it is not partial, so a container in another assembly names why it carries no factory for the contribution");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task WithOnlyPublicMembers_EmitsNoMarker()
		{
			GeneratorResult library = Generator.Run(NonPartial("[Singleton<IGrinder>(Factory = nameof(CreateGrinder))]", "public static IGrinder CreateGrinder() => new Grinder(new BeanStore());"));

			await That(library.Diagnostics).IsEmpty();
			await That(library.Sources.Keys).IsEmpty()
				.Because("a container in any assembly calls the public factory, so nothing needs to record that the module is not partial");
		}
	}

	public class Instances
	{
		[Test]
		public async Task WithoutWrapperOrHandle_ExternalAwt109NamesTheCause()
		{
			string library = Library("[Singleton<Grinder, IGrinder>(Instance = nameof(Shared), OnActivated = nameof(Ready))]", "internal static Grinder Shared = new(new BeanStore()); internal static void Ready(Grinder grinder) { }");

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasCount(2)
				.And.Contains("(7,2): error AWT109: 'Lib.IGrinder' cannot be exposed: the module 'Lib.CoffeeModule' has no accessible field or property 'Shared' of type 'Lib.IGrinder': 'Shared' is internal and has the type 'Lib.Grinder', which is not public, and the module has no single public service to expose it as that every registration and hook of the member accepts, so it generated no public wrapper for it; make the member and its type public, or grant this assembly InternalsVisibleTo in the module's assembly")
				.And.Contains("*error AWT165: *").AsWildcard()
				.Because("an internal hook keeps the module from exposing the internal type as the service, which it records for the container that reports the hook on an instance as well");
		}

		[Test]
		public async Task UnderSeveralServices_IsNotInitializedByAConventionOfTheModule()
		{
			string library = Library("[Singleton<Grinder, IGrinder>(Instance = nameof(Shared))][Singleton<Grinder, IMill>(Instance = nameof(Shared))][AsyncInitializer<Grinder>(nameof(Grinder.InitAsync))]", "internal static Grinder Shared = new(new BeanStore());")
				.Replace("public interface IGrinder { }", "public interface IGrinder { }\npublic interface IMill { }")
				.Replace("internal sealed class Grinder(BeanStore store) : IGrinder { public BeanStore Store { get; } = store; }", "internal sealed class Grinder(BeanStore store) : IGrinder, IMill { public BeanStore Store { get; } = store; internal Task InitAsync(Settings settings) => Task.CompletedTask; }");

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("no container initializes an instance it does not own, so the convention that takes an internal type never needs a wrapper");
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).IsEmpty()
				.Because("a container in another assembly serves the instance from its handle without initializing it, as a container in the module's assembly does");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).IsEmpty();
		}
	}

	public class Hooks
	{
		[Test]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin) { }")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnRelease = nameof(Ready))]", "internal static void Ready(Plugin plugin) { }")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(IPlugin plugin) { }")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(PluginBase plugin) { }")]
		[Arguments("[Singleton<Plugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin) { }")]
		[Arguments("[Transient<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin) { }")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static Task Ready(Plugin plugin, CancellationToken cancellationToken) => Task.CompletedTask;")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnRelease = nameof(Ready))]", "internal static ValueTask Ready(Plugin plugin) => default;")]
		[Arguments("[Singleton<BeanStore>][Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, BeanStore store) { }")]
		[Arguments("[Singleton<Beans, IBeans>][Singleton<Plugin, IPlugin>(OnRelease = nameof(Ready))]", "internal static void Ready(Plugin plugin, IBeans beans) { }")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin) { } internal static void Ready(IGrinder grinder) { }")]
		[Arguments("[Singleton<BeanStore>][Singleton<IGrinder>(Factory = nameof(CreateGrinder), OnActivated = nameof(Ready))]", "public static IGrinder CreateGrinder() => new Grinder(new BeanStore()); internal static void Ready(IGrinder grinder, BeanStore store) { }")]
		public async Task Internal_IsUsableEverywhere(string registration, string member)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(registration, member));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the module generates a public wrapper for the internal hook, which takes an internal service through its handle");
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.Hook_");
			await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Ready(")
				.Because("a container in the module's assembly calls the internal hook directly");
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Ready(")
				.Because("a friend calls the internal hook directly");
		}

		[Test]
		public async Task Private_IsReportedEverywhere_NamingTheImplementationAndTheCause()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "private static void Ready(Plugin plugin) { }"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164") && diagnostic.Contains("accepting 'Lib.Plugin'") && diagnostic.Contains("'Ready' is private"))
				.Because("no container calls a private hook of a public implementation, so the module reports it in its own build");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164") && diagnostic.Contains("'Lib.IPlugin' cannot use the lifecycle hook 'Ready'") && diagnostic.Contains("accepting 'Lib.Plugin'") && diagnostic.Contains("'Ready' is private"))
				.Because("the hook takes the implementation, which the message names, not the service");
			await That(external.Diagnostics).Contains("*AWT164*").AsWildcard();
			await That(friend.Diagnostics).Contains("*AWT164*").AsWildcard();
		}

		[Test]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(string text) { }")]
		[Arguments("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "public static void Ready(string text) { }")]
		[Arguments("[Singleton<IGrinder>(Factory = nameof(CreateGrinder), OnActivated = nameof(Ready))]", "public static IGrinder CreateGrinder() => new Grinder(new BeanStore()); internal static void Ready(string text) { }")]
		[Arguments("[Singleton<IGrinder>(Factory = nameof(CreateGrinder), OnActivated = nameof(Ready))]", "public static IGrinder CreateGrinder() => new Grinder(new BeanStore()); public static void Ready(string text) { }")]
		public async Task NotTakingTheImplementation_IsReportedInTheLibraryWhateverItsAccessibility(string registration, string member)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(registration, member));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164"))
				.Because("no container can run a method that does not take the implementation as its hook, whether it is internal or public");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164"))
				.Because("the module leaves the report to the container of its assembly, which reports it once");
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164"));
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164"));
		}

		[Test]
		public async Task Generic_OfAClosedRegistration_IsReportedEverywhere()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready<T>(T plugin) { }"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164") && diagnostic.Contains("nothing binds its type parameter(s) 'T'"))
				.Because("a generic hook binds its type arguments only from an open generic scan's marker, so no container can close it for a closed registration");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164") && diagnostic.Contains("nothing binds its type parameter(s) 'T'"));
			await That(external.Diagnostics).Contains("*AWT164*").AsWildcard()
				.Because("a container in another assembly does not see the internal generic method, which the module's own build already reports precisely");
			await That(friend.Diagnostics).Contains("*AWT164*nothing binds its type parameter(s) 'T'*").AsWildcard();
		}

		[Test]
		public async Task Overloaded_IsReportedAsAmbiguous()
		{
			(GeneratorResult library, GeneratorResult same, _, GeneratorResult friend) = RunAll(Library("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin) { } internal static void Ready(IPlugin plugin) { }"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT190"))
				.Because("no wrapper can choose between the overloads, so the module reports the ambiguity in its own build, as a container of its assembly does");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT190"))
				.Because("the module leaves the report to the container of its assembly, which reports it once");
			await That(friend.Diagnostics).Contains("*AWT190*").AsWildcard();
		}

		[Test]
		public async Task WithAnArgParameter_IsReportedOnceByAContainerOfTheModulesAssembly()
		{
			(GeneratorResult library, GeneratorResult same, _, GeneratorResult friend) = RunAll(Library("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, [Arg] string name) { }"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT189"))
				.Because("no container can pass a runtime argument to a hook, so the module reports it in its own build");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT189"))
				.Because("the container of the module's assembly resolves the hook itself, so the module leaves the report to it");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT189"));
		}

		[Test]
		public async Task WithAParameterNoWrapperCanExpose_IsLeftToTheModulesAssemblyAndReportedByAnother()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, Settings settings) { }"));

			await That(library.Diagnostics).IsEmpty()
				.Because("a container of the module's assembly may register the internal service itself and call the hook directly, so the module only records why it generated no wrapper");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101"));
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("OnActivated hook 'Ready'"))
				.Because("a container in another assembly cannot call the hook, so it reports the reason the module recorded instead of dropping the hook");
		}

		[Test]
		[Arguments("")]
		[Arguments("\n[Module][Singleton<Beans, IBeans>] public static partial class OtherModule;")]
		public async Task Skipped_TakingAnInternalServiceThatCannotBeBuilt_DoesNotDemandItsHandle(string otherModule)
		{
			string library = Library("[Singleton<BeanStore>][Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, BeanStore store) { }")
				.Replace("internal sealed class BeanStore { }", "internal sealed class BeanStore(Settings settings) { public Settings Settings { get; } = settings; }") + otherModule;

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.Run(library + "\n[Container][Import(typeof(CoffeeModule))][Singleton<Settings>] public static partial class SameContainer;").Diagnostics).IsEmpty()
				.Because("the container of the module's assembly registers the internal service the hook's service needs and calls the hook directly, so the skipped hook demands no handle the module cannot build");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("'Lib.BeanStore', which the module cannot build"));
		}

		[Test]
		public async Task Skipped_TakingAnInternalService_EmitsNoHandleForIt()
		{
			GeneratorResult library = Generator.Run(Library("[Singleton<BeanStore>][Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready(Plugin plugin, BeanStore store, [FromKey(\"x\")] IPlugin other) { }"));

			await That(library.Diagnostics).IsEmpty();
			await That(string.Concat(library.Sources.Values)).DoesNotContain("Hidden_BeanStore_")
				.Because("the module generates no wrapper for the hook, so nothing takes the internal service through a handle");
		}
	}
}
