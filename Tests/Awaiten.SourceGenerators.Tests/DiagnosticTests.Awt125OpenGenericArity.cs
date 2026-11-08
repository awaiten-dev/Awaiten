using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt125OpenGenericArity
	{
		[Test]
		public async Task ReportsWhenTheImplementationAndServiceHaveDifferentArity()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRepository<T> { }
			                                       // Two type parameters declared for a one-parameter service: no closed service can be mapped.
			                                       public sealed class Repository<TKey, TValue> { }

			                                       [Container]
			                                       [Transient(typeof(Repository<,>), typeof(IRepository<>))]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT125*").AsWildcard()
				.Because("the open implementation's arity must match the service's, so a closed service can be re-mapped onto the implementation");
		}

		[Test]
		public async Task DoesNotReportWhenTheArityMatches()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Order { }
			                                       public interface IRepository<T> { }
			                                       public sealed class Repository<T> : IRepository<T> { }
			                                       public sealed class Root { public Root(IRepository<Order> orders) { } }

			                                       [Container]
			                                       [Transient(typeof(Repository<>), typeof(IRepository<>))]
			                                       [Transient<Root>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT125*").AsWildcard()
				.Because("the implementation and service both declare one type parameter");
		}

		public class NestedInAGenericType
		{
			private const string Consumer = """
			                                using Awaiten;
			                                using Lib;

			                                namespace App;

			                                public sealed class Checkout
			                                {
			                                    public Checkout(IRepository<string> names) { }
			                                }

			                                [Container]
			                                [Import(typeof(DataModule))]
			                                [Transient<Checkout>]
			                                public static partial class AppContainer
			                                {
			                                }
			                                """;

			private const string Module = """
			                              using Awaiten;

			                              namespace Lib;

			                              public interface IRepository<T> { }
			                              public class Outer<T>
			                              {
			                                  public sealed class Inner<U> : IRepository<U> { }
			                              }

			                              [Module]
			                              [Scoped(typeof(Outer<>.Inner<>), typeof(IRepository<>))]
			                              public static partial class DataModule { }
			                              """;

			[Test]
			[Arguments("[Scoped(typeof(Outer<>.Inner<>), typeof(IRepository<>))]", "MyCode.Outer<T>.Inner<U>")]
			[Arguments("[Scoped(typeof(Outer<>.Pair<>), typeof(IPair<,>))]", "MyCode.Outer<T>.Pair<U>")]
			[Arguments("[Scoped(typeof(Outer<>.Single), typeof(IRepository<>))]", "MyCode.Outer<T>.Single")]
			[Arguments("[Decorate(typeof(Outer<>.Logging<>), typeof(IRepository<>))]", "MyCode.Outer<T>.Logging<U>")]
			[Arguments("[Composite(typeof(Outer<>.Composite<>), typeof(IRepository<>))]", "MyCode.Outer<T>.Composite<U>")]
			public async Task AnImplementationOfTheContainer_IsReportedInsteadOfBrokenCode(string registration, string implementation)
			{
				GeneratorResult result = Generator.Run($$"""
				                                         using Awaiten;

				                                         namespace MyCode;

				                                         public interface IRepository<T> { }
				                                         public interface IPair<T, U> { }
				                                         public sealed class Repository<T> : IRepository<T> { }
				                                         public class Outer<T>
				                                         {
				                                             public sealed class Inner<U> : IRepository<U> { }
				                                             public sealed class Pair<U> : IPair<T, U> { }
				                                             public sealed class Single : IRepository<T> { }
				                                             public sealed class Logging<U> : IRepository<U> { public Logging(IRepository<U> inner) { } }
				                                             public sealed class Composite<U> : IRepository<U> { public Composite(IRepository<U>[] all) { } }
				                                         }
				                                         public sealed class Root { public Root(IRepository<string> names, IPair<int, string> pairs) { } }

				                                         [Container]
				                                         [Scoped(typeof(Repository<>), typeof(IRepository<>))]
				                                         {{registration}}
				                                         [Transient<Root>]
				                                         public static partial class MyContainer
				                                         {
				                                         }
				                                         """);

				await That(result.Diagnostics).Contains($"*AWT125*'{implementation}'*is nested in the generic type 'MyCode.Outer<T>'*declare it outside*").AsWildcard()
					.Because("a closing of the service supplies only the type arguments of the nested type itself, never those of the type that contains it");
				await That(result.Diagnostics).DoesNotContain("*CS*").AsWildcard()
					.Because("the registration is left out instead of generating code that names the containing type unbound");
			}

			[Test]
			public async Task AServiceNestedInAGenericType_IsReported()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public class Outer<T>
				                                       {
				                                           public interface IRepository<U> { }
				                                       }
				                                       public sealed class Repository<U> : Outer<int>.IRepository<U> { }

				                                       [Container]
				                                       [Scoped(typeof(Repository<>), typeof(Outer<>.IRepository<>))]
				                                       public static partial class MyContainer
				                                       {
				                                       }
				                                       """);

				await That(result.Diagnostics).Contains("*AWT125*'MyCode.Outer<T>.IRepository<U>' is nested in the generic type 'MyCode.Outer<T>'*").AsWildcard();
			}

			[Test]
			public async Task ATypeNestedInANonGenericType_IsNotReported()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IRepository<T> { }
				                                       public static class Outer
				                                       {
				                                           public sealed class Repository<T> : IRepository<T> { }
				                                       }
				                                       public sealed class Root { public Root(IRepository<string> names) { } }

				                                       [Container]
				                                       [Scoped(typeof(Outer.Repository<>), typeof(IRepository<>))]
				                                       [Transient<Root>]
				                                       public static partial class MyContainer
				                                       {
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty();
			}

			[Test]
			public async Task TheLibraryAlone_ReportsNothing()
			{
				GeneratorResult result = Generator.Run(Module);

				await That(result.Diagnostics).IsEmpty()
					.Because("a module leaves a malformed open registration to the container that imports it");
			}

			[Test]
			public async Task AContainerInTheSameAssembly_ReportsIt()
			{
				GeneratorResult result = Generator.Run(Module + Consumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty));

				await That(result.Diagnostics).Contains("*AWT125*'Lib.Outer<T>.Inner<U>' is nested in the generic type 'Lib.Outer<T>'*").AsWildcard();
				await That(result.Diagnostics).DoesNotContain("*CS*").AsWildcard();
			}

			[Test]
			public async Task AContainerInAnotherAssembly_ReportsItAtTheImport()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Module, Consumer);

				await That(result.Diagnostics).Contains("(12,2): error AWT125*'Lib.Outer<T>.Inner<U>' is nested in the generic type 'Lib.Outer<T>'*").AsWildcard()
					.Because("the module's attribute has no syntax in the consumer, so the diagnostic points at the [Import]");
				await That(result.Diagnostics).DoesNotContain("*AWT231*").AsWildcard();
			}

			[Test]
			public async Task AFriendContainer_ReportsIt()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
					Module.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), Consumer);

				await That(result.Diagnostics).Contains("*AWT125*'Lib.Outer<T>.Inner<U>' is nested in the generic type 'Lib.Outer<T>'*").AsWildcard();
				await That(result.Diagnostics).DoesNotContain("*CS*").AsWildcard();
			}

			[Test]
			public async Task AnExtendingContainer_DoesNotRepeatTheBasesDiagnostic()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IRepository<T> { }
				                                       public class Outer<T>
				                                       {
				                                           public sealed class Inner<U> : IRepository<U> { }
				                                       }

				                                       [Container]
				                                       [Scoped(typeof(Outer<>.Inner<>), typeof(IRepository<>))]
				                                       public static partial class Shop
				                                       {
				                                       }

				                                       [Container(Extends = typeof(Shop))]
				                                       public static partial class TestShop
				                                       {
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT125") && diagnostic.Contains("nested in the generic type"))
					.Because("the base reports its malformed registration, and the extending container does not repeat it or emit broken code");
			}
		}
	}
}
