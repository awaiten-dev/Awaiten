namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     A module registers a type a container can name under a service private to the module. No container can name the
	///     service, so none emits code that names it: the library's build reports a singleton a container would start, a
	///     container of the library reports every such registration, and a container of another assembly drops it or
	///     reports a started one as AWT231.
	/// </summary>
	public class Awt216PrivateModuleService
	{
		private const string Eager = "[Singleton<DataModule.Warmer, DataModule.IWarmer>(Eager = true)]";

		private const string AsyncInitialized = "[AsyncInitializer<DataModule.Warmer>(nameof(DataModule.Warmer.WarmAsync))]\n[Singleton<DataModule.Warmer, DataModule.IWarmer>]";

		private const string KeyedEager = "[Singleton<DataModule.Warmer, DataModule.IWarmer>(Eager = true, Key = \"main\")]";

		private const string DecoratedEager = "[Singleton<DataModule.Warmer, DataModule.IWarmer>(Eager = true)]\n[Decorate<DataModule.LoggingWarmer, DataModule.IWarmer>]";

		private const string Plain = "[Singleton<DataModule.Warmer, DataModule.IWarmer>]";

		private const string SameContainer = """

			[Container]
			[Import(typeof(DataModule))]
			public static partial class SameContainer;
			""";

		private const string Consumer = """
			using Awaiten;
			using Lib;

			namespace App;

			[Container]
			[Import(typeof(DataModule))]
			public static partial class AppContainer;
			""";

		private const string Remedy = "register it under an accessible service, or make 'Lib.DataModule.IWarmer' internal or public";

		private static string Library(string registrations, string implementation = "internal") => $$"""
			using Awaiten;
			using System.Threading.Tasks;

			namespace Lib;

			public interface IHeater { }

			[Module]
			{{registrations}}
			public static partial class DataModule
			{
				private interface IWarmer { }
				{{implementation}} sealed class Warmer : IWarmer, IHeater { public Task WarmAsync() => Task.CompletedTask; }
				private sealed class LoggingWarmer : IWarmer { public LoggingWarmer(IWarmer inner) { } }
				internal static Warmer Make() => new Warmer();
			}
			""";

		private static string Granted(string library)
			=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

		[Test]
		[Arguments(Eager, "internal")]
		[Arguments(Eager, "public")]
		[Arguments(AsyncInitialized, "internal")]
		[Arguments(KeyedEager, "internal")]
		[Arguments(DecoratedEager, "internal")]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>(Factory = nameof(DataModule.Make), Eager = true)]", "internal")]
		public async Task LibraryBuild_ReportsAStartedSingleton(string registrations, string implementation)
		{
			GeneratorResult result = Generator.Run(Library(registrations, implementation));

			await That(result.Diagnostics).HasSingle().Which
				.Contains($"*AWT216*'Lib.DataModule.Warmer' cannot be registered: 'Lib.DataModule.IWarmer' is private or protected and 'Lib.DataModule.Warmer' is registered under it, so no container can name the service; {Remedy}").AsWildcard()
				.Because("a container starts the singleton though nothing depends on it, and none can name the service to hold it");
		}

		[Test]
		public async Task LibraryBuild_LeavesASingletonNothingStarts()
		{
			GeneratorResult result = Generator.Run(Library(Plain));

			await That(result.Diagnostics).IsEmpty()
				.Because("a container of another assembly drops a registration it cannot name, and nothing depends on it");
		}

		[Test]
		public async Task LibraryBuild_NamesTheTypeArgumentAServiceCarries()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				public interface IBox<T> { }

				[Module]
				[Singleton<DataModule.Warmer, IBox<DataModule.Secret>>(Eager = true)]
				public static partial class DataModule
				{
					private sealed class Secret { }
					internal sealed class Warmer : IBox<Secret> { }
				}
				""");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*AWT216*'Lib.DataModule.Warmer' cannot be registered: 'Lib.DataModule.Secret' is private or protected and 'Lib.DataModule.Warmer' is registered under the service 'Lib.IBox<Lib.DataModule.Secret>', which carries it*make 'Lib.DataModule.Secret' internal or public").AsWildcard();
		}

		[Test]
		[Arguments(Eager)]
		[Arguments(AsyncInitialized)]
		[Arguments(KeyedEager)]
		[Arguments(Plain)]
		[Arguments("[Transient<DataModule.Warmer, DataModule.IWarmer>]")]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>(Key = \"main\")]")]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>(Eager = true)]\n[Singleton<DataModule.Warmer, IHeater>]")]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>(Factory = nameof(DataModule.Make), Eager = true)]")]
		public async Task SameAssembly_ReportsTheRegistrationInsteadOfNamingTheService(string registrations)
		{
			GeneratorResult result = Generator.Run(Library(registrations) + SameContainer);

			await That(result.Diagnostics).HasSingle().Which
				.Contains($"*AWT216*'Lib.DataModule.Warmer'*'Lib.DataModule.IWarmer' is private or protected*; {Remedy}").AsWildcard()
				.Because("the container cannot name the service either, so it reports the registration rather than emit code that fails with CS0122");
		}

		[Test]
		[Arguments(DecoratedEager)]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>]\n[Decorate<DataModule.LoggingWarmer, DataModule.IWarmer>]")]
		public async Task SameAssembly_DoesNotReportTheDecoratorOfTheDroppedService(string registrations)
		{
			GeneratorResult result = Generator.Run(Library(registrations) + SameContainer);

			await That(result.Diagnostics).HasSingle().Which.Contains("*AWT216*'Lib.DataModule.IWarmer'*").AsWildcard()
				.Because("the registration was dropped for a reported reason, so the decorator over it has nothing to decorate without a second error");
		}

		[Test]
		[Arguments(Eager)]
		[Arguments(AsyncInitialized)]
		[Arguments(KeyedEager)]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>(Factory = nameof(DataModule.Make), Eager = true)]")]
		public async Task Friend_IsToldThatTheGrantDoesNotReachTheService(string registrations)
		{
			GeneratorResult full = Generator.RunWithGeneratedReferencedAssembly(Granted(Library(registrations)), Consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Granted(Library(registrations)), Consumer);

			foreach (GeneratorResult result in new[] { full, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*AWT231*'Lib.DataModule.Warmer'*'Lib.DataModule.IWarmer', is private to the module, so no container can name it; the InternalsVisibleTo grant to this assembly reaches only internal members, not 'Lib.DataModule.IWarmer'*").AsWildcard()
					.Because("a friend can call the internal factory member, but still cannot name the service");
			}
		}

		[Test]
		[Arguments(Eager)]
		[Arguments(AsyncInitialized)]
		[Arguments(KeyedEager)]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>(Eager = true)]\n[Singleton<DataModule.Warmer, IHeater>]")]
		public async Task External_ReportsAStartedSingleton(string registrations)
		{
			GeneratorResult full = Generator.RunWithGeneratedReferencedAssembly(Library(registrations), Consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Library(registrations), Consumer);

			foreach (GeneratorResult result in new[] { full, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*AWT231*'Lib.DataModule.Warmer'*'Lib.DataModule.IWarmer', is private to the module, so no container can name it; make 'Lib.DataModule.IWarmer', which is private to the module, internal or public").AsWildcard()
					.Because("the module's factory for the accessible service does not let the container name the private one");
			}
		}

		[Test]
		[Arguments(Plain)]
		[Arguments("[Singleton<DataModule.Warmer, DataModule.IWarmer>]\n[Singleton<DataModule.Warmer, IHeater>]")]
		public async Task External_DropsASingletonNothingStarts(string registrations)
		{
			GeneratorResult full = Generator.RunWithGeneratedReferencedAssembly(Library(registrations), Consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Library(registrations), Consumer);

			foreach (GeneratorResult result in new[] { full, reference, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("nothing can resolve or depend on a service the container cannot name");
			}
		}
	}
}
