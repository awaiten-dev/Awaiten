using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt231HookOverloadUnseen
	{
		public enum Reference
		{
			ImplementationAssembly,
			ReferenceAssembly,
			CompilationReference,
		}

		private static GeneratorResult Run(Reference reference, string library, string consumer)
			=> reference switch
			{
				Reference.ReferenceAssembly => Generator.RunWithGeneratedReferenceAssembly(library, consumer),
				Reference.CompilationReference => Generator.RunWithGeneratedCompilationReference(library, consumer),
				_ => Generator.RunWithGeneratedReferencedAssembly(library, consumer),
			};

		private static string Granted(string library)
			=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

		private static string SameAssembly(string library, string consumer)
			=> library + consumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty);

		private static string ModuleSource(GeneratorResult result)
			=> result.Sources.Single(source => source.Key.StartsWith("Awaiten.ModuleScan.", StringComparison.Ordinal)).Value;

		public class APublicOpenType
		{
			private const string PublicGenericHook = """
			                                         public static void Ready<T>(Repository<T> repository) { }

			                                         internal static void Ready(Repository<Order> repository) { }
			                                         """;

			private const string PublicClosedHook = """
			                                        internal static void Ready<T>(Repository<T> repository) { }

			                                        public static void Ready(Repository<Order> repository) { }
			                                        """;

			private const string PublicOverloadsFirst = """
			                                            public static void Ready<T>(Repository<T> repository) { }

			                                            public static void Ready(Repository<string> repository) { }

			                                            internal static void Ready(Repository<Order> repository) { }
			                                            """;

			private const string Consumer = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRepository<Order> orders, IRepository<int> numbers) { }
			                                }

			                                [Container]
			                                [Import(typeof(DataModule))]
			                                [Transient<Checkout>]
			                                public static partial class AppContainer
			                                {
			                                }
			                                """;

			private static string Library(string overloads)
				=> $$"""
				     using Awaiten;

				     namespace Lib;

				     public interface IRepository<T> { }
				     public sealed class Order { }
				     public sealed class Repository<T> : IRepository<T> { }

				     [Module]
				     [Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Ready))]
				     public static partial class DataModule
				     {
				     {{overloads}}
				     }
				     """;

			[Test]
			[Arguments(PublicGenericHook)]
			[Arguments(PublicClosedHook)]
			public async Task TheLibrary_RecordsTheHook(string overloads)
			{
				GeneratorResult result = Generator.Run(Library(overloads));

				await That(result.Diagnostics).IsEmpty()
					.Because("the library builds no closing the overloads make ambiguous");
				await That(ModuleSource(result)).Contains("public const string HookSkipped_OnActivated_Repository_").And
					.Contains("has another overload").And
					.DoesNotContain("Hook_OnActivated_Repository_");
			}

			[Test]
			[Arguments(PublicGenericHook)]
			[Arguments(PublicClosedHook)]
			public async Task AContainerInTheSameAssembly_ReportsTheAmbiguityForThatClosing(string overloads)
			{
				GeneratorResult result = Generator.Run(SameAssembly(Library(overloads), Consumer));

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.IRepository<Lib.Order>'*'Ready'*").AsWildcard()
					.Because("both overloads accept the order repository, while only the generic one accepts the repository of numbers");
			}

			[Test]
			[Arguments(PublicGenericHook)]
			[Arguments(PublicClosedHook)]
			public async Task AFriendContainer_ReportsTheAmbiguityForThatClosing(string overloads)
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library(overloads)), Consumer);

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.IRepository<Lib.Order>'*'Ready'*").AsWildcard()
					.Because("a friend sees the internal overload and binds the hook itself");
			}

			[Test]
			[Arguments(PublicGenericHook, Reference.ImplementationAssembly)]
			[Arguments(PublicGenericHook, Reference.ReferenceAssembly)]
			[Arguments(PublicGenericHook, Reference.CompilationReference)]
			[Arguments(PublicClosedHook, Reference.ImplementationAssembly)]
			[Arguments(PublicClosedHook, Reference.ReferenceAssembly)]
			[Arguments(PublicClosedHook, Reference.CompilationReference)]
			[Arguments(PublicOverloadsFirst, Reference.ImplementationAssembly)]
			public async Task AContainerInAnotherAssembly_ReportsTheHookInsteadOfCallingThePublicOverload(string overloads, Reference reference)
			{
				GeneratorResult result = Run(reference, Library(overloads), Consumer);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.DataModule'*'Lib.Repository<T>' with the OnActivated hook 'Ready', which this container does not run, because*taking 'Lib.Repository<Lib.Order>', that accepts 'Lib.Repository<Lib.Order>' too*give the hook methods distinct names*").AsWildcard()
					.Because("this assembly does not see the internal overload, so calling the public one could run another overload than the module's assembly would");
			}
		}

		public class APublicClosedType
		{
			private const string Consumer = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRepository repository) { }
			                                }

			                                [Container]
			                                [Import(typeof(DataModule))]
			                                [Transient<Checkout>]
			                                public static partial class AppContainer
			                                {
			                                }
			                                """;

			private const string Library = """
			                               using Awaiten;

			                               namespace Lib;

			                               public interface IRepository { }
			                               public sealed class Repository : IRepository { }

			                               [Module]
			                               [Scoped<Repository, IRepository>(OnActivated = nameof(Ready))]
			                               public static partial class DataModule
			                               {
			                                   public static void Ready(object repository) { }

			                                   internal static void Ready(Repository repository) { }
			                               }
			                               """;

			[Test]
			public async Task TheLibrary_RecordsTheHook()
			{
				GeneratorResult result = Generator.Run(Library);

				await That(result.Diagnostics).IsEmpty();
				await That(ModuleSource(result)).Contains("public const string HookSkipped_OnActivated_Repository_").And
					.DoesNotContain("Hook_OnActivated_Repository_");
			}

			[Test]
			public async Task AContainerInTheSameAssembly_ReportsTheAmbiguity()
			{
				GeneratorResult result = Generator.Run(SameAssembly(Library, Consumer));

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Ready'*").AsWildcard();
			}

			[Test]
			public async Task AFriendContainer_ReportsTheAmbiguity()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library), Consumer);

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Ready'*").AsWildcard()
					.Because("a friend sees the internal overload");
			}

			[Test]
			[Arguments(Reference.ImplementationAssembly)]
			[Arguments(Reference.ReferenceAssembly)]
			[Arguments(Reference.CompilationReference)]
			public async Task AContainerInAnotherAssembly_ReportsTheHookInsteadOfCallingThePublicOverload(Reference reference)
			{
				GeneratorResult result = Run(reference, Library, Consumer);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.DataModule'*'Lib.Repository' with the OnActivated hook 'Ready', which this container does not run, because*another overload that is not public, taking 'Lib.Repository', that accepts 'Lib.Repository' too*give the hook methods distinct names*").AsWildcard()
					.Because("this assembly sees only the public overload for objects, while the module's assembly reports both as ambiguous");
			}

			[Test]
			public async Task AnOverloadOfAnotherType_IsNotRecorded()
			{
				string library = Library.Replace("internal static void Ready(Repository repository) { }", "internal static void Ready(string text) { }");

				GeneratorResult own = Generator.Run(library);
				GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

				await That(own.Sources.Values).All().Satisfy(source => !source.Contains("HookSkipped_", StringComparison.Ordinal))
					.Because("the overload for text does not accept the repository");
				await That(external.Diagnostics).IsEmpty();
			}
		}

		public class APrivateOverloadOfAPrivateType
		{
			private const string Ledgers = """
			                               using Awaiten;
			                               using Lib;

			                               namespace App;

			                               public sealed class LedgerView
			                               {
			                                   public LedgerView(IRepository<Ledger> ledgers) { }
			                               }

			                               [Container(Extends = typeof(Shop))]
			                               [Transient<LedgerView>]
			                               public static partial class TestShop
			                               {
			                               }
			                               """;

			private const string Base = """
			                            using Awaiten;

			                            namespace Lib;

			                            public interface IRepository<T> { }
			                            public sealed class Order { }
			                            public sealed class Ledger { }

			                            [Container]
			                            [Scoped(typeof(Shop.Repository<>), typeof(IRepository<>), OnActivated = nameof(Ready))]
			                            [Scoped<Shop.Checkout>]
			                            public static partial class Shop
			                            {
			                                private static void Ready<T>(Repository<T> repository) { }

			                                private static void Ready(Repository<Ledger> repository) { }

			                                private sealed class Repository<T> : IRepository<T> { }

			                                internal sealed class Checkout
			                                {
			                                    public Checkout(IRepository<Order> orders) { }
			                                }
			                            }
			                            """;

			private const string Module = """
			                              using Awaiten;

			                              namespace Lib;

			                              public interface IRepository<T> { }
			                              public sealed class Ledger { }

			                              [Module]
			                              [Scoped(typeof(DataModule.Repository<>), typeof(IRepository<>), OnActivated = nameof(Ready))]
			                              public static partial class DataModule
			                              {
			                                  private static void Ready<T>(Repository<T> repository) { }

			                                  private static void Ready(Repository<Ledger> repository) { }

			                                  private sealed class Repository<T> : IRepository<T> { }
			                              }
			                              """;

			private const string ModuleConsumer = """
			                                      using Awaiten;
			                                      using Lib;

			                                      namespace App;

			                                      public sealed class LedgerView
			                                      {
			                                          public LedgerView(IRepository<Ledger> ledgers) { }
			                                      }

			                                      [Container]
			                                      [Import(typeof(DataModule))]
			                                      [Transient<LedgerView>]
			                                      public static partial class AppContainer
			                                      {
			                                      }
			                                      """;

			[Test]
			public async Task TheBase_RecordsTheHook()
			{
				GeneratorResult result = Generator.Run(Granted(Base));

				await That(result.Diagnostics).IsEmpty()
					.Because("the base builds only the order repository, which the overload for ledgers does not accept");
				await That(result.Sources.Single(source => source.Key.Contains("Shop", StringComparison.Ordinal)).Value)
					.Contains("internal static global::Lib.IRepository<T> Registration_Repository_").And
					.Contains("const string HookSkipped_OnActivated_Repository_");
			}

			[Test]
			public async Task AnExtendingContainerInTheSameAssembly_ReportsTheAmbiguityForThatClosing()
			{
				GeneratorResult result = Generator.Run(SameAssembly(Base, Ledgers));

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.Shop.Repository<Lib.Ledger>'*'Ready'*").AsWildcard()
					.Because("a container of the base's assembly sees the private overload");
			}

			[Test]
			[Arguments(Reference.ImplementationAssembly)]
			[Arguments(Reference.CompilationReference)]
			public async Task AnExtendingFriend_ReportsTheHookInsteadOfRunningTheWrapper(Reference reference)
			{
				GeneratorResult result = Run(reference, Granted(Base), Ledgers);

				await That(result.Diagnostics).HasCount(2).And
					.Contains("*AWT226*'Lib.Shop.Repository<Lib.Ledger>' cannot be built by 'App.TestShop'*'Lib.Shop'*its OnActivated hook 'Ready' has another overload, taking 'Lib.Shop.Repository<Lib.Ledger>'*base's assembly reports AWT190; give the hook methods distinct names").AsWildcard().And
					.Contains("*AWT226*'Lib.Shop.Repository<Lib.Order>' cannot be built by 'App.TestShop'*").AsWildcard()
					.Because("a friend does not see the private overload, so it cannot tell which closings the base's generic wrapper would run the wrong overload for");
			}

			[Test]
			[Arguments(Reference.ImplementationAssembly)]
			[Arguments(Reference.CompilationReference)]
			public async Task AFriendOfTheModule_IsToldTheCauseInsteadOfToGrantInternalsVisibleTo(Reference reference)
			{
				GeneratorResult result = Run(reference, Granted(Module), ModuleConsumer);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.DataModule'*'Lib.DataModule.Repository<Lib.Ledger>' with the OnActivated hook 'Ready', which this container does not run, because*taking 'Lib.DataModule.Repository<Lib.Ledger>'*give the hook methods distinct names").AsWildcard()
					.Because("the friend has access and the wrapper exists, but it does not see the private overload");
			}

			[Test]
			public async Task APrivateOverloadAfterAnInternalOne_IsReportedToAFriend()
			{
				string module = Module.Replace(
					"private static void Ready(Repository<Ledger> repository) { }",
					"internal static void Ready(IRepository<string> repository) { }\n\nprivate static void Ready(Repository<Ledger> repository) { }");

				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(module), ModuleConsumer);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.DataModule.Repository<Lib.Ledger>'*taking 'Lib.DataModule.Repository<Lib.Ledger>'*").AsWildcard()
					.Because("the friend sees the internal overload, which does not accept the ledger repository, but not the private one, which does");
			}
		}
	}
}
