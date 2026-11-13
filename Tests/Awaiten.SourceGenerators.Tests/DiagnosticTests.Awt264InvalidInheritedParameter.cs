using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt264InvalidInheritedParameter
	{
		private static GeneratorResult Run(string testShop, string extra = "")
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;
			                   using System.Collections.Generic;
			                   using System.Threading.Tasks;

			                   namespace MyCode;

			                   public interface IStore { }
			                   public sealed class DiskStore : IStore { }
			                   public sealed class FakeStore : IStore { }
			                   public interface IClock { }
			                   public sealed class SystemClock : IClock { }
			                   {{extra}}

			                   [Container]
			                   [Singleton<DiskStore, IStore>]
			                   [Singleton<SystemClock, IClock>]
			                   public static partial class Shop;

			                   {{testShop}}
			                   """);

		[Test]
		public async Task DoesNotReportAReplacementThatTakesTheRegistrationItReplaces()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<IStore>(Factory = nameof(Store))]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store([Inherited] Func<IStore> production) => production();
			                             }
			                             """);

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments("IStore")]
		[Arguments("Lazy<IStore>")]
		[Arguments("Task<IStore>")]
		[Arguments("Func<Task<IStore>>")]
		[Arguments("Lazy<Task<IStore>>")]
		public async Task DoesNotReportASupportedShape(string shape)
		{
			GeneratorResult result = Run($$"""
			                               [Container(Extends = typeof(Shop))]
			                               [Singleton<IStore>(Factory = nameof(Store))]
			                               public static partial class TestShop
			                               {
			                                   private static IStore Store([Inherited] {{shape}} production) => new FakeStore();
			                               }
			                               """);

			await That(result.Diagnostics).DoesNotContain("*AWT264*").AsWildcard()
				.Because("[Inherited] takes the service and its deferred and async shapes");
		}

		[Test]
		public async Task ReportsAContainerWithoutExtends()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IStore { }

			                                       [Container]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class Shop
			                                       {
			                                           private static IStore Store([Inherited] Func<IStore> production) => production();
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(12,58): error AWT264: Parameter 'production' of 'MyCode.Shop.Store' is marked [Inherited], but [Inherited] applies only to a parameter of a Factory member that a registration declared on a container with Extends names; remove [Inherited]")
				.Because("a container without a base replaces nothing");
		}

		[Test]
		public async Task ReportsAConstructorParameter()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<Report>]
			                             public static partial class TestShop;
			                             """,
				"public sealed class Report { public Report([Inherited] IStore store) { } }");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264: Parameter 'store' of 'MyCode.Report'*applies only to a parameter of a Factory member*").AsWildcard();
		}

		[Test]
		public async Task ReportsALifecycleHookParameter()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<IStore>(Factory = nameof(Store), OnActivated = nameof(Activate))]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store() => new FakeStore();

			                                 private static void Activate(IStore store, [Inherited] IStore production) { }
			                             }
			                             """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264: Parameter 'production' of 'MyCode.TestShop.Activate'*").AsWildcard()
				.Because("a hook belongs to the instance, not to the registration the factory replaces");
		}

		[Test]
		public async Task ReportsADecoratorPredicateParameter()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Decorate<LoggingStore, IStore>(When = nameof(IsLogged))]
			                             public static partial class TestShop
			                             {
			                                 private static bool IsLogged([Inherited] IClock clock) => true;
			                             }
			                             """,
				"public sealed class LoggingStore(IStore inner) : IStore { public IStore Inner => inner; }");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264: Parameter 'clock' of 'MyCode.TestShop.IsLogged'*").AsWildcard();
		}

		[Test]
		public async Task ReportsAFactoryOfAModule()
		{
			GeneratorResult result = Run("""
			                             [Module]
			                             [Singleton<IStore>(Factory = nameof(Store))]
			                             public static class StoreModule
			                             {
			                                 internal static IStore Store([Inherited] Func<IStore> production) => production();
			                             }

			                             [Container(Extends = typeof(Shop))]
			                             [Import(typeof(StoreModule))]
			                             public static partial class TestShop;
			                             """);

			await That(result.Diagnostics).Contains("*error AWT264: Parameter 'production' of 'MyCode.StoreModule.Store'*").AsWildcard()
				.Because("a module's registration replaces nothing of the base");
		}

		[Test]
		public async Task ReportsAnotherServiceThanTheRegistration()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<IStore>(Factory = nameof(Store))]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store([Inherited] IClock clock) => new FakeStore();
			                             }
			                             """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*its registration serves 'MyCode.IStore', not 'MyCode.IClock'; take 'MyCode.IStore'*").AsWildcard();
		}

		[Test]
		public async Task ReportsWhenTheBaseHasNoSuchRegistration()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<IStore>(Factory = nameof(Store), Key = "backup")]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store([Inherited] IStore production) => production;
			                             }
			                             """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*the base 'MyCode.Shop' has no registration of 'MyCode.IStore' that its registration replaces*").AsWildcard()
				.Because("the base registers no store under the key backup");
		}

		[Test]
		public async Task ReportsAFallbackReplacement()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<IStore>(Factory = nameof(Store), Fallback = Fallback.Silent)]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store([Inherited] IStore production) => production;
			                             }
			                             """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*its registration has Fallback, so it replaces nothing; remove Fallback, or remove [Inherited]*").AsWildcard()
				.Because("a Fallback registration replaces nothing, and the inherited store wins over it");
		}

		[Test]
		public async Task ReportsAFallbackRegistrationThatFillsAGapOnce()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Singleton<IStore>(Factory = nameof(Store), Key = "backup", Fallback = Fallback.Silent)]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store([Inherited] IStore production) => production;
			                             }
			                             """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*its registration has Fallback*").AsWildcard()
				.Because("the parameter is reported with one cause, although the registration fills the backup gap");
		}

		[Test]
		[Arguments("[Arg]", "[Arg]")]
		[Arguments("[FromKey(\"other\")]", "[FromKey]")]
		public async Task ReportsACombinationWithAnotherParameterAttribute(string attribute, string named)
		{
			GeneratorResult result = Run($$"""
			                               [Container(Extends = typeof(Shop))]
			                               [Singleton<IStore>(Factory = nameof(Store))]
			                               public static partial class TestShop
			                               {
			                                   private static IStore Store([Inherited] {{attribute}} IStore production) => production;
			                               }
			                               """);

			await That(result.Diagnostics).Contains($"*error AWT264*it also carries {named}; remove {named}*").AsWildcard();
		}

		[Test]
		[Arguments("IEnumerable<IStore>")]
		[Arguments("Owned<IStore>")]
		[Arguments("Func<string, IStore>")]
		public async Task ReportsAnUnsupportedShape(string shape)
		{
			GeneratorResult result = Run($$"""
			                               [Container(Extends = typeof(Shop))]
			                               [Singleton<IStore>(Factory = nameof(Store))]
			                               public static partial class TestShop
			                               {
			                                   private static IStore Store([Inherited] {{shape}} production) => new FakeStore();
			                               }
			                               """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*it takes '*'; take 'MyCode.IStore' as itself, Lazy<T>, Func<T>*").AsWildcard();
		}

		[Test]
		public async Task ReportsABaseThatRegistersTheServiceSeveralTimes()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IStore { }
			                                       public sealed class DiskStore : IStore { }
			                                       public sealed class CloudStore : IStore { }

			                                       [Container]
			                                       [Singleton<DiskStore, IStore>]
			                                       [Singleton<CloudStore, IStore>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] IStore production) => production;
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*the base 'MyCode.Shop' registers several 'MyCode.IStore', so [Inherited] cannot pick one; remove [Inherited] and build the production service in the factory, or key the registrations in the base and replace one of them under its key").AsWildcard()
				.Because("the base's collection has no single registration to keep, and its synthetic key must not leak into a duplicate key error");
		}

		[Test]
		public async Task ReportsABaseThatRegistersACompositeOverTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IStore { }
			                                       public sealed class DiskStore : IStore { }
			                                       public sealed class CloudStore : IStore { }
			                                       public sealed class CompositeStore(IEnumerable<IStore> stores) : IStore { public IEnumerable<IStore> Stores => stores; }

			                                       [Container]
			                                       [Singleton<DiskStore, IStore>]
			                                       [Singleton<CloudStore, IStore>]
			                                       [Composite<CompositeStore, IStore>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] IStore production) => production;
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*the base 'MyCode.Shop' registers several 'MyCode.IStore', so [Inherited] cannot pick one*").AsWildcard()
				.Because("the composite fronts several members, none of which is the one registration [Inherited] would take");
		}

		[Test]
		public async Task ReportsABaseCompositeThatServesTheServiceInPlaceOfTheReplacement()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IStore { }
			                                       public sealed class DiskStore : IStore { }
			                                       public sealed class CompositeStore(IEnumerable<IStore> stores) : IStore { public IEnumerable<IStore> Stores => stores; }

			                                       [Container]
			                                       [Singleton<DiskStore, IStore>]
			                                       [Composite<CompositeStore, IStore>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] IStore production) => production;
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*but a composite serves 'MyCode.IStore' in place of its registration, which therefore replaces no single registration; remove [Inherited] and build the production service in the factory").AsWildcard()
				.Because("the replacement joins the composite as a member, and the message must not read as if it served another service");
		}

		[Test]
		public async Task ReportsAnOpenGenericRegistrationOfTheBase()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRepo<T> { }
			                                       public sealed class Repo<T> : IRepo<T> { }
			                                       public sealed class WrappedRepo(IRepo<int> inner) : IRepo<int> { public IRepo<int> Inner => inner; }

			                                       [Container]
			                                       [Singleton(typeof(Repo<>), typeof(IRepo<>))]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IRepo<int>>(Factory = nameof(Repo))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IRepo<int> Repo([Inherited] IRepo<int> production) => new WrappedRepo(production);
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*the base 'MyCode.Shop' registers 'MyCode.IRepo<int>' only through an open generic registration, which [Inherited] cannot take; remove [Inherited] and build the production service in the factory, or register 'MyCode.IRepo<int>' closed in the base").AsWildcard();
		}

		[Test]
		public async Task ReportsAServiceTheBaseImports()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IStore { }

			                                       [Container]
			                                       [ImportService<IStore>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] Func<IStore> production) => production();
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT264*the base 'MyCode.Shop' imports 'MyCode.IStore' with [ImportService<T>], which [Inherited] cannot take; remove [Inherited] and build the production service in the factory").AsWildcard();
		}

		[Test]
		public async Task ReportsEachParameterOfAModuleInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly("""
			                                                             using Awaiten;
			                                                             using System;

			                                                             namespace ModuleAssembly;

			                                                             public interface IStore { }
			                                                             public interface IClock { }

			                                                             [Module]
			                                                             [Singleton<IStore>(Factory = nameof(Store))]
			                                                             [Singleton<IClock>(Factory = nameof(Clock))]
			                                                             public static class TestModule
			                                                             {
			                                                                 public static IStore Store([Inherited] Func<IStore> production) => production();

			                                                                 public static IClock Clock([Inherited] Func<IClock> production) => production();
			                                                             }
			                                                             """, """
			                                                                  using Awaiten;
			                                                                  using ModuleAssembly;

			                                                                  namespace MyCode;

			                                                                  public sealed class DiskStore : IStore { }
			                                                                  public sealed class SystemClock : IClock { }

			                                                                  [Container]
			                                                                  [Singleton<DiskStore, IStore>]
			                                                                  [Singleton<SystemClock, IClock>]
			                                                                  public static partial class Shop;

			                                                                  [Container(Extends = typeof(Shop))]
			                                                                  [Import(typeof(TestModule))]
			                                                                  public static partial class TestShop;
			                                                                  """);

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics.Where(diagnostic => diagnostic.StartsWith("(15,", StringComparison.Ordinal) && diagnostic.Contains("error AWT264"))).HasCount(2)
				.Because("a parameter read from metadata has no source location, so each is reported at the [Import] of its module");
			await That(result.Diagnostics).Contains("*'ModuleAssembly.TestModule.Store'*").AsWildcard();
			await That(result.Diagnostics).Contains("*'ModuleAssembly.TestModule.Clock'*").AsWildcard();
		}

		[Test]
		public async Task DoesNotKeepTheReplacedRegistrationForAnOverloadTheFactoryDoesNotResolveTo()
		{
			GeneratorResult result = Run("""
			                             [Container(Extends = typeof(Shop))]
			                             [Scoped<DiskStore>]
			                             [Singleton<IStore>(Factory = nameof(Store))]
			                             public static partial class TestShop
			                             {
			                                 private static IStore Store() => new FakeStore();

			                                 private static void Store([Inherited] IStore production) { }
			                             }
			                             """);

			await That(result.Diagnostics).IsEmpty()
				.Because("only the overload the factory resolves to decides whether the replaced singleton store is kept, which would conflict with the scoped one");
		}
	}

	public class InheritedRegistrationRules
	{
		[Test]
		public async Task ReportsACaptiveScopedInheritedRegistration()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IStore { }
			                                       public sealed class DiskStore : IStore { }

			                                       [Container]
			                                       [Scoped<DiskStore, IStore>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] IStore production) => production;
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT105*").AsWildcard()
				.Because("a singleton replacement would capture the scoped registration it replaces");
		}

		[Test]
		public async Task ReportsASynchronousFuncOverAnAsyncInitializedInheritedRegistration()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IStore { }
			                                       public sealed class DiskStore : IStore { }

			                                       [Container]
			                                       [Singleton<DiskStore, IStore>(OnActivated = nameof(OpenAsync))]
			                                       public static partial class Shop
			                                       {
			                                           internal static Task OpenAsync(DiskStore store) => Task.CompletedTask;
			                                       }

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] Func<IStore> production) => production();
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT119*").AsWildcard()
				.Because("an async-initialized registration is reached only through the async shapes, [Inherited] or not");
		}

		[Test]
		public async Task ReportsNothingForTheKeptRegistrationOfAnUnusedEagerSingleton()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IStore { }
			                                       public sealed class DiskStore : IStore { }
			                                       public sealed class FakeStore : IStore { }

			                                       [Container]
			                                       [Singleton<DiskStore, IStore>(Eager = true)]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Transient<IStore>(Factory = nameof(Store))]
			                                       public static partial class TestShop
			                                       {
			                                           private static IStore Store([Inherited] Lazy<IStore> production) => new FakeStore();
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty();
		}
	}
}
