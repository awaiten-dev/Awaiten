using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     An error the check of a base in another assembly finds in the inherited graph: that assembly built although the
///     check fails, because its build suppressed the error or ran a generator without the check, so the extending
///     container in a third assembly reports the error itself instead of taking it for one the base reported.
/// </summary>
public class ExtendedBaseErrorTests
{
	private const string Extending = """
	                                 using Awaiten;

	                                 namespace MyCode;

	                                 [Container(Extends = typeof(Production.Shop))]
	                                 public static partial class TestShop { }
	                                 """;

	private static string Base(string declarations) => $$"""
		using Awaiten;
		using Lib;

		namespace Production;

		{{declarations}}
		public static partial class Shop { }
		""";

	private static async Task ReportsOnceAtTheExtends(GeneratorResult result, string id)
	{
		await That(result.Diagnostics).HasSingle().Which.Contains($"(5,12): error {id}:*").AsWildcard()
			.Because("the inherited graph is broken whatever the base's build reported, so the extending container reports the error at its Extends");
		await That(result.Sources.Single(source => source.Key.Contains("TestShop")).Value).Contains("has registration errors")
			.Because("a container with an error emits only its shell");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task AMissingDependencyOfAnInheritedModule_IsReported(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        namespace Lib;

		                                                        public interface IClock { }
		                                                        public sealed class Barista { public Barista(IClock clock) { } }

		                                                        [Module]
		                                                        [Singleton<Barista>]
		                                                        public static class BaristaModule { }
		                                                        """, Base("[Container]\n[Import(typeof(BaristaModule))]"), Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT101");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task AMissingDependencyOfTheBase_IsReported(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("namespace Lib;", """
		                                                                          using Awaiten;

		                                                                          namespace Production;

		                                                                          public interface IClock { }
		                                                                          public sealed class Barista { public Barista(IClock clock) { } }

		                                                                          [Container]
		                                                                          [Singleton<Barista>]
		                                                                          public static partial class Shop { }
		                                                                          """, Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT101");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task ARegistrationTheModuleGeneratesNoFactoryFor_IsReported(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lib.Tests")]

		                                                        namespace Lib;

		                                                        public interface IClock { }
		                                                        public interface IWelcome { }
		                                                        public sealed class SystemClock : IClock { }

		                                                        internal sealed class Welcome : IWelcome
		                                                        {
		                                                            public Welcome([FromKey("utc")] IClock Clock) { }

		                                                            [Inject, FromKey("utc")]
		                                                            public IClock Clock { get; set; } = null!;
		                                                        }

		                                                        [Module]
		                                                        [Singleton<SystemClock, IClock>(Key = "utc")]
		                                                        [Singleton<Welcome, IWelcome>]
		                                                        public static partial class WelcomeModule { }
		                                                        """, Base("[Container]\n[Import(typeof(WelcomeModule))]"), Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT231");
		await That(result.Diagnostics.Single()).Contains("'Lib.Welcome'");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task AModuleThatProvidesAnInternalServiceNotImported_IsReported(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        namespace Lib;

		                                                        public interface IGrinder { }
		                                                        internal sealed class BeanStore { }
		                                                        internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

		                                                        [Module]
		                                                        [Singleton<BeanStore>]
		                                                        public static partial class StorageModule { }

		                                                        [Module]
		                                                        [Singleton<Grinder, IGrinder>]
		                                                        public static partial class CoffeeModule { }
		                                                        """, Base("[Container]\n[Import(typeof(CoffeeModule))]"), Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT238");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task ADirectiveTheHandleOfAModuleCannotApply_IsReported(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        namespace Lib;

		                                                        internal interface IStore { }
		                                                        public sealed class Store : IStore { }
		                                                        public interface IGrinder { }
		                                                        internal sealed class Grinder : IGrinder { public Grinder(IStore store) { } }

		                                                        [Module]
		                                                        [Singleton<Store, IStore>]
		                                                        [Singleton<Grinder, IGrinder>]
		                                                        public static partial class CoffeeModule { }
		                                                        """, Base("[Container]\n[Import(typeof(CoffeeModule))]\n[Singleton<Store>(SuppressDisposal = true)]"), Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT241");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task AnInaccessibleDecoratorPredicateOfAModule_IsReported(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        namespace Lib;

		                                                        public interface IClient { }
		                                                        public sealed class Client : IClient { }
		                                                        public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

		                                                        [Module]
		                                                        [Singleton<Client, IClient>]
		                                                        [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
		                                                        public static class ClientModule
		                                                        {
		                                                        	internal static bool Enabled => true;
		                                                        }
		                                                        """, Base("[Container]\n[Import(typeof(ClientModule))]"), Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT247");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task AMissingFactoryOfAnInheritedModule_IsReportedOnceAsUnreachable(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        namespace Lib;

		                                                        public sealed class Clock { }

		                                                        [Module]
		                                                        [Singleton<Clock>(Factory = "Missing")]
		                                                        public static class ClockModule { }
		                                                        """, Base("[Container]\n[Import(typeof(ClockModule))]"), Extending, build);

		await ReportsOnceAtTheExtends(result, "AWT226");
		await That(result.Diagnostics).DoesNotContain("*AWT108*").AsWildcard()
			.Because("the extending container cannot tell a missing member from one a reference assembly leaves out, so the one report is AWT226");
	}

	[Test]
	public async Task AnAnalyzerErrorOfTheBase_IsReported()
	{
		string[] diagnostics = await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>("""
		                                                                                 using Awaiten;
		                                                                                 using System;

		                                                                                 namespace Production;

		                                                                                 public sealed class Tool : IDisposable { public void Dispose() { } }
		                                                                                 public sealed class Depot { public Depot(Func<Tool> tools) { } }

		                                                                                 [Container]
		                                                                                 [Transient<Tool>]
		                                                                                 [Singleton<Depot>]
		                                                                                 public static partial class Shop { }
		                                                                                 """, Extending);

		await That(diagnostics).HasSingle().Which.Contains("error AWT118")
			.Because("the base's assembly built without the analyzer's error, so the inherited root-accumulating factory is reported where it is extended");
	}

	[Test]
	[Arguments(BaseBuild.Suppressed)]
	[Arguments(BaseBuild.WithoutGenerator)]
	[Arguments(BaseBuild.CompilationReference)]
	public async Task AWarningOfTheBase_IsNotRepeated(BaseBuild build)
	{
		GeneratorResult result = Generator.RunWithModuleAndBase("""
		                                                        using Awaiten;

		                                                        namespace Lib;

		                                                        public interface IClock { }
		                                                        public sealed class ClockA : IClock { }
		                                                        public sealed class ClockB : IClock { }
		                                                        public sealed class Barista { public Barista(IClock clock) { } }

		                                                        [Module]
		                                                        [Singleton<ClockA, IClock>(Fallback = Fallback.Warn)]
		                                                        public static class ModuleA { }

		                                                        [Module]
		                                                        [Singleton<ClockB, IClock>(Fallback = Fallback.Warn)]
		                                                        public static class ModuleB { }
		                                                        """, Base("[Container]\n[Import(typeof(ModuleA))]\n[Import(typeof(ModuleB))]\n[Singleton<Barista>]"), Extending, build);

		await That(result.Diagnostics).IsEmpty()
			.Because("a warning leaves the inherited graph valid, so it stays the base's to report");
	}
}
