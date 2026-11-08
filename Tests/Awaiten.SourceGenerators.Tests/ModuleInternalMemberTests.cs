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
}
