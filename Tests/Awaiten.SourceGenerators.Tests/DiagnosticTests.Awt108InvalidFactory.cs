namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt108InvalidFactory
	{
		[Test]
		public async Task ReportsWhenNoMethodOfThatNameReturnsTheServiceType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(Factory = nameof(Missing))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static int Missing() => 0;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT108*").AsWildcard();
		}

		[Test]
		public async Task ReportsWhenTheNamedMemberIsNotAMethod()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(Factory = nameof(NotAMethod))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static Service NotAMethod = new Service();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT108*").AsWildcard()
				.Because("a field is not a usable factory method even when its type matches");
		}

		[Test]
		public async Task ReportsWhenTheFactoryNameIsEmpty()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(Factory = "")]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT108*").AsWildcard()
				.Because("an empty factory name is a mistake, not a silent fall back to the constructor");
		}

		public class GenericFactoryOfAClosedRegistration
		{
			private const string Module = """
			                              using Awaiten;

			                              [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

			                              namespace Lib;

			                              public interface IGrinder { }
			                              public sealed class Grinder : IGrinder { }
			                              public sealed class BeanStore { }

			                              [Module]
			                              [Singleton<BeanStore>]
			                              [Singleton<IGrinder>(Factory = nameof(CreateGrinder))]
			                              public static partial class GrinderModule
			                              {
			                              	ACCESS static IGrinder CreateGrinder<T>(BeanStore store) => new Grinder();
			                              }
			                              """;

			private const string Consumer = """
			                                using Awaiten;

			                                namespace App;

			                                [Container]
			                                [Import(typeof(Lib.GrinderModule))]
			                                public static partial class AppContainer { }
			                                """;

			private const string LibraryContainer = """

			                                        [Container]
			                                        [Import(typeof(GrinderModule))]
			                                        public static partial class LibraryContainer { }
			                                        """;

			[Test]
			public async Task ReportsTheContainersOwnGenericFactory()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IGrinder { }
				                                       public sealed class Grinder : IGrinder { }

				                                       [Container]
				                                       [Singleton<IGrinder>(Factory = nameof(CreateGrinder))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static IGrinder CreateGrinder<T>() => new Grinder();
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT108*'CreateGrinder'*generic*'T'*").AsWildcard()
					.Because("a closed registration has no closing to bind 'T' from, so the call could not compile");
			}

			[Test]
			public async Task ReportsAGenericFactoryWhoseReturnTypeNamesTheTypeParameter()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IRepository<T> { }
				                                       public sealed class Repository<T> : IRepository<T> { }

				                                       [Container]
				                                       [Singleton<IRepository<int>>(Factory = nameof(Create))]
				                                       [Singleton<Repository<string>>(Factory = nameof(CreateRepository))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static IRepository<T> Create<T>() => new Repository<T>();
				                                       	private static TRepository CreateRepository<TRepository, TKey>() where TRepository : new() => new TRepository();
				                                       }
				                                       """);

				await That(result.Diagnostics).HasCount(2)
					.Because("C# infers no type argument from a return type, and neither does Awaiten for a closed registration").And
					.Contains("*AWT108*'Create'*generic*'T'*").AsWildcard().And
					.Contains("*AWT108*'CreateRepository'*generic*'TRepository', 'TKey'*").AsWildcard();
			}

			[Test]
			[Arguments("internal")]
			[Arguments("private")]
			[Arguments("public")]
			public async Task ReportsTheModulesGenericFactoryInTheLibraryBuild(string access)
			{
				GeneratorResult result = Generator.Run(Module.Replace("ACCESS", access));

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT108*'CreateGrinder'*module 'Lib.GrinderModule'*generic*'T'*").AsWildcard()
					.Because("the fault lies in the module's declaration, so the library fails to build even without a container");
			}

			[Test]
			[Arguments("internal")]
			[Arguments("private")]
			[Arguments("public")]
			public async Task ReportsTheModulesGenericFactoryOnceBesideAContainerOfTheSameAssembly(string access)
			{
				GeneratorResult result = Generator.Run(Module.Replace("ACCESS", access) + LibraryContainer);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT108*'CreateGrinder'*generic*'T'*").AsWildcard()
					.Because("the module reports it in its own build, so the container of its assembly leaves it to the module and emits no call");
			}

			[Test]
			[Arguments("internal")]
			[Arguments("public")]
			public async Task ReportsTheModulesGenericFactoryInAFriendContainer(string access)
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Module.Replace("ACCESS", access), Consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT108*'CreateGrinder'*generic*'T'*").AsWildcard()
					.Because("a container that sees the generic member names why it cannot call it instead of emitting a call with an unbound 'T'");
			}

			[Test]
			public async Task ReportsTheModulesPublicGenericFactoryInAnExternalContainer()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
					Module.Replace("ACCESS", "public").Replace("[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]", string.Empty),
					Consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT108*'CreateGrinder'*generic*'T'*").AsWildcard()
					.Because("every container that sees the generic member reports it alike");
			}

			[Test]
			public async Task DoesNotReportAGenericOverloadBesideANonGenericFactory()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IGrinder { }
				                                       public sealed class Grinder : IGrinder { }

				                                       [Container]
				                                       [Singleton<IGrinder>(Factory = nameof(CreateGrinder))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static IGrinder CreateGrinder() => new Grinder();
				                                       	private static IGrinder CreateGrinder<T>() => new Grinder();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the generic overload cannot produce a closed registration, so the non-generic one is the only factory and not an ambiguity");
			}
		}
	}
}
