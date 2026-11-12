namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt161EagerAsyncSingleton
	{
		[Test]
		public async Task ReportsAnAsyncInitializedSingletonWithEagerWhen()
		{
			GeneratorResult result = Generator.Run(Container("[Container]"));

			await That(result.Diagnostics).HasSingle().Which.Contains("*error AWT161*'MyCode.Kettle' is marked Eager or EagerWhen*").AsWildcard()
				.Because("a predicate that allows it builds the singleton synchronously in the root's constructor, like Eager");
		}

		[Test]
		public async Task DoesNotReportAnAsyncInitializedSingletonWithEagerWhenUnderSyncAfterInit()
		{
			GeneratorResult result = Generator.Run(Container("[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]"));

			await That(result.Diagnostics).IsEmpty();
		}

		private static string Container(string container) => $$"""
			using System.Threading.Tasks;
			using Awaiten;

			namespace MyCode;

			public sealed class Kettle { public Task WarmAsync() => Task.CompletedTask; }

			{{container}}
			[AsyncInitializer<Kettle>(nameof(Kettle.WarmAsync))]
			[Singleton<Kettle>(EagerWhen = nameof(Warm))]
			public static partial class MyContainer
			{
			    private static bool Warm => true;
			}
			""";

		public class InAModule
		{
			private const string Container = """
				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""";

			private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

			private static string Library(string registration, string members = "") => $$"""
				using System.Threading.Tasks;
				using Awaiten;

				namespace Lib;

				public interface IKettle { }
				internal interface IHeater { }
				public sealed class Kettle : IKettle, IHeater { public Task WarmAsync() => Task.CompletedTask; }
				public sealed class ColdKettle : IKettle, IHeater { }

				[Module]
				[AsyncInitializer<Kettle>(nameof(Kettle.WarmAsync))]
				[{{registration}}]
				public static partial class CoffeeModule
				{
				    {{members}}
				}
				""";

			[Test]
			[Arguments("Singleton<Kettle, IKettle>(Eager = true)", "", "Lib.Kettle")]
			[Arguments("Singleton<IKettle>(Factory = nameof(Make), Eager = true)", "public static Kettle Make() => new Kettle();", "Lib.IKettle")]
			[Arguments("Singleton<IHeater>(Factory = nameof(Make), Eager = true)", "internal static Kettle Make() => new Kettle();", "Lib.IHeater")]
			[Arguments("Singleton<IKettle>(Factory = nameof(Make), Eager = true)", "public static async Task<IKettle> Make() { await Task.Yield(); return new ColdKettle(); }", "Lib.IKettle")]
			[Arguments("Singleton<IKettle>(Factory = nameof(Make), Eager = true, OnActivated = nameof(Heat))", "public static IKettle Make() => new ColdKettle();\n    public static Task Heat(IKettle kettle) => Task.CompletedTask;", "Lib.IKettle")]
			[Arguments("Singleton<Kettle, IKettle>(EagerWhen = nameof(Warm))", "public static bool Warm => true;", "Lib.Kettle")]
			public async Task IsAWarningInTheModulesOwnBuild(string registration, string members, string service)
			{
				await That(Generator.Run(Library(registration, members)).Diagnostics).HasSingle().Which.Contains($"*warning AWT161*'{service}'*Eager*AsyncSafety.SyncAfterInit*").AsWildcard()
					.Because("only a container that opts into SyncAfterInit accepts it, so the author learns it before a consumer does");
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(registration, members), Consumer).Diagnostics).HasSingle().Which.Contains("*error AWT161*").AsWildcard()
					.Because("a container with the default AsyncSafety still cannot construct it at build time");
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(registration, members), Consumer.Replace("[Container]", "[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]")).Diagnostics).IsEmpty();
			}

			[Test]
			public async Task ContainerInTheModulesAssembly_ReportsItOnce()
			{
				string library = Library("Singleton<Kettle, IKettle>(Eager = true)");

				await That(Generator.Run(library + Container).Diagnostics).HasSingle().Which.Contains("*error AWT161*'Lib.Kettle'*").AsWildcard()
					.Because("the container that imports the module reports it, so the module leaves it to it");
			}

			[Test]
			[Arguments("Singleton<ColdKettle, IKettle>(Eager = true)", "")]
			[Arguments("Singleton<Kettle, IKettle>", "")]
			[Arguments("Singleton<IKettle>(Instance = nameof(Shared), Eager = true)", "public static readonly IKettle Shared = new Kettle();")]
			public async Task IsNotReportedWithoutEagerAsyncInitialization(string registration, string members)
				=> await That(Generator.Run(Library(registration, members)).Diagnostics).IsEmpty();
		}
	}
}
