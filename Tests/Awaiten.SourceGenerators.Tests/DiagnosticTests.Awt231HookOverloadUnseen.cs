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

			[Test]
			[Arguments(PublicGenericHook, "a container that sees only the public one would call it")]
			[Arguments(PublicClosedHook, "a container that sees only the public one would call it")]
			[Arguments("internal static void Ready<T>(Repository<T> repository) { }\n\ninternal static void Ready(Repository<Order> repository) { }", "a generated wrapper would run the wrong overload")]
			public async Task TheRecordedReason_NamesWhatAContainerInAnotherAssemblyWouldRun(string overloads, string consequence)
			{
				GeneratorResult result = Generator.Run(Library(overloads));

				await That(ModuleSource(result)).Contains($"that accepts 'Lib.Repository<Lib.Order>' too, so {consequence} where a container in the module's assembly reports AWT190")
					.Because("a container in another assembly sees only a public overload, and none when both are internal");
			}

			[Test]
			[Arguments("internal static void Ready(object repository) { }", "object")]
			[Arguments("internal static void Ready<T>(IRepository<T> repository) { }", "Lib.IRepository<T>")]
			public async Task AnInternalOverloadForEveryClosing_IsReportedInAnotherAssembly(string overload, string taking)
			{
				string library = Library("public static void Ready<T>(Repository<T> repository) { }\n\n" + overload);

				GeneratorResult own = Generator.Run(SameAssembly(library, Consumer));
				GeneratorResult result = Run(Reference.ReferenceAssembly, library, Consumer);

				await That(own.Diagnostics).HasCount(2).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT190", StringComparison.Ordinal))
					.Because("both overloads accept every closing");
				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo($"*AWT231*'Lib.Repository<T>' with the OnActivated hook 'Ready', which this container does not run, because*taking '{taking}', that accepts every closing of 'Lib.Repository<T>' too*").AsWildcard()
					.Because("this assembly sees only the public overload, which it would call for every closing");
			}

			[Test]
			public async Task AnOverloadWithManyVariantBindings_IsCountedWithoutTryingEveryBinding()
			{
				string parameters = string.Join(", ", Enumerable.Range(1, 8).Select(index => $"T{index}"));
				string library = $$"""
				                   using Awaiten;

				                   namespace Lib;

				                   public interface IVariant<{{string.Join(", ", Enumerable.Range(1, 8).Select(index => $"out A{index}"))}}> { }
				                   public interface IRepository<{{parameters}}> { }
				                   public sealed class Repository<{{parameters}}> : IRepository<{{parameters}}>, IVariant<{{string.Join(", ", Enumerable.Repeat("string", 8))}}> { }

				                   [Module]
				                   [Scoped(typeof(Repository<,,,,,,,>), typeof(IRepository<,,,,,,,>), OnActivated = nameof(Ready))]
				                   public static partial class DataModule
				                   {
				                       public static void Ready<{{parameters}}>(Repository<{{parameters}}> repository) { }

				                       internal static void Ready<{{parameters}}>(IVariant<{{parameters}}> repository) where T1 : struct { }
				                   }
				                   """;
				System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

				GeneratorResult result = Generator.Run(library);

				await That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(30))
					.Because("each of the eight variant type arguments binds to any of the supertypes of string, which multiply to far more bindings than can be tried");
				await That(ModuleSource(result)).Contains("public const string HookSkipped_OnActivated_Repository_").And.Contains("it may bind")
					.Because("an overload the search cannot decide counts as competing, which keeps another assembly from calling the public one");
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

			[Test]
			public async Task AGenericOverloadTheCallInfers_IsRecorded()
			{
				string library = Library.Replace("internal static void Ready(Repository repository) { }", "internal static void Ready<T>(T repository) where T : IRepository { }");

				GeneratorResult own = Generator.Run(library);
				GeneratorResult same = Generator.Run(SameAssembly(library, Consumer));
				GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Consumer);
				GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

				await That(ModuleSource(own)).Contains("public const string HookSkipped_OnActivated_Repository_")
					.Because("a call without type arguments infers the generic overload for the repository, which C# prefers to the one for objects");
				await That(same.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Ready'*").AsWildcard();
				await That(friend.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Ready'*").AsWildcard();
				await That(external.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.Repository' with the OnActivated hook 'Ready', which this container does not run, because*another overload that is not public, taking 'T', that accepts 'Lib.Repository' too*").AsWildcard()
					.Because("this assembly sees only the overload for objects, while the module's assembly would run the generic one");
			}

			[Test]
			[Arguments("internal static void Ready<T>(T repository) where T : struct { }")]
			[Arguments("internal static void Ready<T>(System.Collections.Generic.IList<T> items) { }")]
			public async Task AGenericOverloadTheCallDoesNotInfer_IsNotRecorded(string overload)
			{
				string library = Library.Replace("internal static void Ready(Repository repository) { }", overload);

				GeneratorResult own = Generator.Run(library);
				GeneratorResult same = Generator.Run(SameAssembly(library, Consumer));
				GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

				await That(own.Sources.Values).All().Satisfy(source => !source.Contains("HookSkipped_", StringComparison.Ordinal))
					.Because("the generic overload does not accept the repository, so C# never picks it");
				await That(same.Diagnostics).IsEmpty();
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

		public class APartlyBoundOverload
		{
			private const string Library = """
			                               using Awaiten;

			                               namespace Lib;

			                               public interface IPair<T, U> { }
			                               public sealed class Order { }
			                               public sealed class Repository<T, U> : IPair<T, U> { }

			                               [Module]
			                               [Scoped(typeof(Repository<,>), typeof(IPair<,>), OnActivated = nameof(Ready))]
			                               public static partial class DataModule
			                               {
			                                   public static void Ready<T, U>(Repository<T, U> repository) { }

			                                   internal static void Ready<T, U>(IPair<Order, U> pair) where U : struct { }
			                               }
			                               """;

			private const string Consumer = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IPair<Order, int> orders) { }
			                                }

			                                [Container]
			                                [Import(typeof(DataModule))]
			                                [Transient<Checkout>]
			                                public static partial class AppContainer
			                                {
			                                }
			                                """;

			[Test]
			public async Task TheLibrary_RecordsTheHook()
			{
				GeneratorResult result = Generator.Run(Library);

				await That(result.Diagnostics).IsEmpty();
				await That(ModuleSource(result)).Contains("public const string HookSkipped_OnActivated_Repository_").And
					.Contains("that accepts 'Lib.Repository<Lib.Order, U>' too")
					.Because("the overload binds only the first type parameter, and some closing of the second meets its constraint");
			}

			[Test]
			public async Task AContainerInTheSameAssembly_ReportsTheAmbiguity()
			{
				GeneratorResult result = Generator.Run(SameAssembly(Library, Consumer));

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.IPair<Lib.Order, int>'*'Ready'*").AsWildcard()
					.Because("both overloads accept the pair of an order and a number");
			}

			[Test]
			[Arguments(Reference.ImplementationAssembly)]
			[Arguments(Reference.ReferenceAssembly)]
			public async Task AContainerInAnotherAssembly_ReportsTheHookInsteadOfCallingThePublicOverload(Reference reference)
			{
				GeneratorResult result = Run(reference, Library, Consumer);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.Repository<T, U>' with the OnActivated hook 'Ready', which this container does not run, because*taking 'Lib.IPair<Lib.Order, U>'*").AsWildcard();
			}
		}

		public class AClassTypeConstraint
		{
			private const string Library = """
			                               using Awaiten;

			                               namespace Lib;

			                               public class Entity { }
			                               public interface IRepository<T> { }
			                               public sealed class Repository<T> : IRepository<T> where T : Entity { }

			                               [Module]
			                               [Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Ready))]
			                               public static partial class DataModule
			                               {
			                                   public static void Ready<T>(Repository<T> repository) where T : Entity { }

			                                   internal static void Ready<T>(IRepository<T> repository) where T : struct { }
			                               }
			                               """;

			private const string Consumer = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRepository<Entity> entities) { }
			                                }

			                                [Container]
			                                [Import(typeof(DataModule))]
			                                [Transient<Checkout>]
			                                public static partial class AppContainer
			                                {
			                                }
			                                """;

			[Test]
			public async Task AnOverloadForValueTypes_IsNotRecorded()
			{
				GeneratorResult own = Generator.Run(Library);
				GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer);

				await That(own.Sources.Values).All().Satisfy(source => !source.Contains("HookSkipped_", StringComparison.Ordinal))
					.Because("a type parameter constrained to a class type admits only reference types, which the overload for value types never accepts");
				await That(external.Diagnostics).IsEmpty();
				await That(external.Sources.Values).Any().Satisfy(source => source.Contains("global::Lib.DataModule.Ready<global::Lib.Entity>(", StringComparison.Ordinal));
			}

			[Test]
			public async Task AnOverloadForValueTypesOfAnEnumConstrainedType_IsRecorded()
			{
				string library = Library.Replace("where T : Entity", "where T : System.Enum");

				GeneratorResult own = Generator.Run(library);

				await That(ModuleSource(own)).Contains("public const string HookSkipped_OnActivated_Repository_")
					.Because("System.Enum admits enums, which the overload for value types accepts too");
			}
		}

		public class AModuleImportedByABaseInAnotherAssembly
		{
			private const string Module = """
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

			private const string Base = """
			                            using Awaiten;
			                            using Lib;

			                            namespace Shop;

			                            [Container]
			                            [Import(typeof(DataModule))]
			                            public static partial class ShopContainer
			                            {
			                            }
			                            """;

			private const string Extender = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRepository repository) { }
			                                }

			                                [Container(Extends = typeof(Shop.ShopContainer))]
			                                [Transient<Checkout>]
			                                public static partial class TestShop
			                                {
			                                }
			                                """;

			private static string GrantedTo(string library, string assembly)
				=> library.Replace("using Awaiten;", $"using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{assembly}\")]");

			[Test]
			[Arguments(false)]
			[Arguments(true)]
			public async Task AnExtendingContainerInAThirdAssembly_ReportsTheHookInsteadOfCallingThePublicOverload(bool baseIsFriend)
			{
				string module = baseIsFriend ? GrantedTo(Module, "Library2") : Module;

				GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([module, Base,], Extender);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT226*'Lib.Repository' cannot be built by 'App.TestShop', which inherits it from 'Shop.ShopContainer' through Extends: its OnActivated hook 'Ready' has another overload that is not public, taking 'Lib.Repository', that accepts 'Lib.Repository' too*; give the hook methods distinct names").AsWildcard()
					.Because("the extending container calls the inherited hook from its own generated code, so it must not rely on the base reporting it, as a base built by an older generator does not");
				await That(result.Sources.Values).All().Satisfy(source => !source.Contains("global::Lib.DataModule.Ready(", StringComparison.Ordinal));
			}

			[Test]
			public async Task AnExtendingFriendOfTheModule_ReportsTheAmbiguity()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Granted(Module), Base,], Extender);

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.IRepository'*'Ready'*").AsWildcard()
					.Because("a friend of the module sees the internal overload and binds the hook itself");
			}

			[Test]
			public async Task AnExtendingContainerInTheBasesAssembly_LeavesTheReportToTheBase()
			{
				string own = Extender.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty).Replace("Shop.ShopContainer", "ShopContainer");

				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Module, Base + own);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT231*'Lib.DataModule'*'Lib.Repository' with the OnActivated hook 'Ready', which this container does not run*").AsWildcard()
					.Because("the base reports the hook itself, so the extending container adds no second error");
			}
		}

		public class AnOpenTypeOfAModuleImportedByABaseInAnotherAssembly
		{
			private const string Module = """
			                              using Awaiten;

			                              namespace Lib;

			                              public interface IRepository<T> { }
			                              public sealed class Order { }
			                              public sealed class Repository<T> : IRepository<T> { }

			                              [Module]
			                              [Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Ready))]
			                              public static partial class DataModule
			                              {
			                                  public static void Ready<T>(Repository<T> repository) { }

			                                  internal static void Ready(Repository<Order> repository) { }
			                              }
			                              """;

			private const string Base = """
			                            using Awaiten;
			                            using Lib;

			                            namespace Shop;

			                            public sealed class Seed
			                            {
			                                public Seed(IRepository<Order> orders) { }
			                            }

			                            [Container]
			                            [Import(typeof(DataModule))]
			                            [Transient<Seed>]
			                            public static partial class ShopContainer
			                            {
			                            }
			                            """;

			private const string Extender = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRepository<Order> orders, IRepository<int> numbers) { }
			                                }

			                                [Container(Extends = typeof(Shop.ShopContainer))]
			                                [Transient<Checkout>]
			                                public static partial class TestShop
			                                {
			                                }
			                                """;

			[Test]
			public async Task AnExtendingContainerInAThirdAssembly_ReportsTheHookAlsoForAClosingOfTheBase()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Module, Base,], Extender);

				await That(result.Diagnostics).HasSingle().Which
					.IsEqualTo("*AWT226*'Lib.Repository<T>' cannot be built by 'App.TestShop', which inherits it from 'Shop.ShopContainer' through Extends: its OnActivated hook 'Ready' has another overload, taking 'Lib.Repository<Lib.Order>'*; give the hook methods distinct names").AsWildcard()
					.Because("the base reports the same AWT231 for its own closing, which would hide the extending container's report and leave the hook out silently");
			}
		}

		public class AScannedPublicType
		{
			private const string Library = """
			                               using Awaiten;

			                               namespace Lib;

			                               public interface IPlugin { }
			                               public interface IRoaster { }
			                               public sealed class Roaster : IPlugin, IRoaster { }

			                               [Module]
			                               [Scan<IPlugin>(As = ScanAs.MatchingInterface, OnActivated = nameof(Wire))]
			                               public static partial class PluginModule
			                               {
			                                   public static void Wire(object roaster) { }

			                                   internal static void Wire(Roaster roaster) { }
			                               }
			                               """;

			private const string Consumer = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRoaster roaster) { }
			                                }

			                                [Container]
			                                [Import(typeof(PluginModule))]
			                                [Transient<Checkout>]
			                                public static partial class AppContainer
			                                {
			                                }
			                                """;

			[Test]
			public async Task TheLibrary_ReportsTheAmbiguity()
			{
				GeneratorResult result = Generator.Run(Library);

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.Roaster'*'Wire'*").AsWildcard()
					.Because("the module binds the hook of each match in its own build, where it sees both overloads");
			}

			[Test]
			public async Task AContainerInTheSameAssembly_ReportsTheAmbiguity()
			{
				GeneratorResult result = Generator.Run(SameAssembly(Library, Consumer));

				await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Lib.Roaster'*'Wire'*").AsWildcard();
			}

			[Test]
			[Arguments(false, Reference.ImplementationAssembly)]
			[Arguments(true, Reference.ImplementationAssembly)]
			[Arguments(false, Reference.ReferenceAssembly)]
			public async Task AContainerInAnotherAssembly_CallsNoOverload(bool friend, Reference reference)
			{
				GeneratorResult result = Run(reference, friend ? Granted(Library) : Library, Consumer);

				await That(result.Sources.Values).All().Satisfy(source => !source.Contains("global::Lib.PluginModule.Wire(", StringComparison.Ordinal))
					.Because("the generated registration of the match names no hook the module could not bind, so no container picks an overload itself");
			}
		}
	}
}
