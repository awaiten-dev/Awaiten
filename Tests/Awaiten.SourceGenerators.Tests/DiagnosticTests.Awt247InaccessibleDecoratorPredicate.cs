namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt247InaccessibleDecoratorPredicate
	{
		[Test]
		public async Task ReportsAPrivatePredicateOfAModuleInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run(SameAssemblyModule("internal", "private"));

			await That(result.Diagnostics).Contains("*AWT247*'MyCode.ClientModule.Enabled'*private to the module 'MyCode.ClientModule'; make it internal or public*").AsWildcard();
			await That(result.Diagnostics).HasCount(1)
				.Because("the module's own build leaves a module a container of its assembly imports to that container");
		}

		[Test]
		[Arguments("internal", "internal")]
		[Arguments("internal", "public")]
		[Arguments("public", "public")]
		public async Task DoesNotReportAReachablePredicateOfAModuleInTheSameAssembly(string module, string predicate)
		{
			GeneratorResult result = Generator.Run(SameAssemblyModule(module, predicate));

			await That(result.Diagnostics).IsEmpty();
		}

		public class InTheModulesOwnBuild
		{
			[Test]
			[Arguments("internal")]
			[Arguments("private")]
			public async Task ReportsANonPublicPredicateOfAPublicModule(string predicate)
			{
				GeneratorResult result = Generator.Run(Module("public", predicate));

				await That(result.Diagnostics).Contains("*AWT247*'MyCode.ClientModule.Enabled'*the module 'MyCode.ClientModule' is public, so containers in other assemblies import it, but they cannot call a predicate that is not public; make it public*").AsWildcard()
					.Because("the library that declares the module sees the mistake, not only the containers of its consumers, which cannot fix it");
				await That(result.Diagnostics).HasCount(1);
			}

			[Test]
			public async Task ReportsAnInternalPredicateOfAPublicModuleThatAContainerOfItsAssemblyImports()
			{
				GeneratorResult result = Generator.Run(SameAssemblyModule("public", "internal"));

				await That(result.Diagnostics).Contains("*AWT247*'MyCode.ClientModule.Enabled'*is public, so containers in other assemblies import it*").AsWildcard()
					.Because("the container of the module's assembly can call the predicate, but a container of another assembly cannot");
				await That(result.Diagnostics).HasCount(1);
			}

			[Test]
			public async Task ReportsAPrivatePredicateOfAnInternalModule()
			{
				GeneratorResult result = Generator.Run(Module("internal", "private"));

				await That(result.Diagnostics).Contains("*AWT247*private to the module 'MyCode.ClientModule'; make it internal or public*").AsWildcard();
				await That(result.Diagnostics).HasCount(1);
			}

			[Test]
			[Arguments("internal", "internal")]
			[Arguments("public", "public")]
			public async Task DoesNotReportAPredicateEveryImportingContainerCanCall(string module, string predicate)
			{
				GeneratorResult result = Generator.Run(Module(module, predicate));

				await That(result.Diagnostics).IsEmpty();
			}

			private static string Module(string module, string predicate)
				=> $$"""
				     using Awaiten;

				     namespace MyCode;

				     public interface IClient { }
				     public sealed class Client : IClient { }
				     public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

				     [Module]
				     [Singleton<Client, IClient>]
				     [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
				     {{module}} static partial class ClientModule
				     {
				     	{{predicate}} static bool Enabled() => true;
				     }
				     """;
		}

		[Test]
		public async Task ReportsAPrivatePredicateOfAnExtendedBaseContainer()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                       [Container]
			                                       [Singleton<Client, IClient>]
			                                       [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class BaseContainer
			                                       {
			                                       	private static bool Enabled() => true;
			                                       }

			                                       [Container(Extends = typeof(BaseContainer))]
			                                       public static partial class TestContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT247*private to the container 'MyCode.BaseContainer'; make it internal or public*").AsWildcard()
				.Because("the extending container's root evaluates the predicate of the inherited decorator itself");
			await That(result.Diagnostics).HasCount(1)
				.Because("the base container calls its own private predicate, so only the extending container reports it");
		}

		[Test]
		public async Task ReportsANonPublicPredicateOfAModuleInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly(OtherAssemblyModule("internal"), Importer);

			await That(result.Diagnostics).Contains("*AWT247*'Lib.ClientModule.Enabled'*the module 'Lib.ClientModule' is in another assembly, whose non-public members the container cannot call; make it public*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportAPublicPredicateOfAModuleInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly(OtherAssemblyModule("public"), Importer);

			await That(result.Diagnostics).IsEmpty();
		}

		private const string Importer = """
		                                using Awaiten;

		                                namespace MyCode;

		                                [Container]
		                                [Import(typeof(Lib.ClientModule))]
		                                public static partial class MyContainer
		                                {
		                                }
		                                """;

		private static string SameAssemblyModule(string module, string predicate)
			=> $$"""
			     using Awaiten;

			     namespace MyCode;

			     public interface IClient { }
			     public sealed class Client : IClient { }
			     public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			     [Module]
			     [Singleton<Client, IClient>]
			     [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			     {{module}} static partial class ClientModule
			     {
			     	{{predicate}} static bool Enabled() => true;
			     }

			     [Container]
			     [Import(typeof(ClientModule))]
			     public static partial class MyContainer
			     {
			     }
			     """;

		private static string OtherAssemblyModule(string accessibility)
			=> $$"""
			     using Awaiten;

			     namespace Lib;

			     public interface IClient { }
			     public sealed class Client : IClient { }
			     public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			     [Module]
			     [Singleton<Client, IClient>]
			     [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			     public static partial class ClientModule
			     {
			     	{{accessibility}} static bool Enabled => true;
			     }
			     """;
	}
}
