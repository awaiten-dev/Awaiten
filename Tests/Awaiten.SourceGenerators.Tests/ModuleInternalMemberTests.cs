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
		public async Task Generic_OfAClosedRegistration_IsReportedEverywhere()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("[Singleton<Plugin, IPlugin>(OnActivated = nameof(Ready))]", "internal static void Ready<T>(T plugin) { }"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164"))
				.Because("a generic hook binds its type arguments only from an open generic scan's marker, so no container can close it for a closed registration");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT164"));
			await That(external.Diagnostics).Contains("*AWT164*").AsWildcard();
			await That(friend.Diagnostics).Contains("*AWT164*").AsWildcard();
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
