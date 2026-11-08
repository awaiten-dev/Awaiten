using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The open generic registrations, decorators and composites a module or a base container builds through a generated
///     generic factory, so a container that cannot construct the open type still closes it: the library alone, a container
///     in the same assembly, a container in another assembly with and without <c>InternalsVisibleTo</c>, and a base
///     container with an extending container.
/// </summary>
public class OpenGenericFactoryTests
{
	private const string Consumer = """
	                                using Awaiten;
	                                using Lib;

	                                namespace App;

	                                public sealed class Order { }

	                                public sealed class Checkout
	                                {
	                                    public Checkout(IRepository<Order> orders) { }
	                                }

	                                [Container]
	                                [Import(typeof(DataModule))]
	                                [Transient<Checkout>]
	                                public static partial class AppContainer
	                                {
	                                }
	                                """;

	private static string Granted(string library)
		=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

	private static string WithContainer(string library)
		=> library + """

		             public sealed class Order { }

		             public sealed class Checkout
		             {
		                 public Checkout(IRepository<Order> orders) { }
		             }

		             [Container]
		             [Import(typeof(DataModule))]
		             [Transient<Checkout>]
		             public static partial class LibContainer
		             {
		             }
		             """;

	private static string ModuleSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.StartsWith("Awaiten.ModuleScan.", StringComparison.Ordinal)).Value;

	private static string ContainerSource(GeneratorResult result, string container)
		=> result.Sources.Single(source => source.Key.Contains(container, StringComparison.Ordinal) && !source.Key.StartsWith("Awaiten.ModuleScan.", StringComparison.Ordinal)).Value;

	public class Registrations
	{
		private const string Internal = """
		                                using Awaiten;

		                                namespace Lib;

		                                public interface IClock { }
		                                public sealed class Clock : IClock { }
		                                public interface IRepository<T> { }
		                                internal sealed class Repository<T> : IRepository<T> where T : class
		                                {
		                                    public Repository(IClock clock) { }
		                                }

		                                [Module]
		                                [Singleton<Clock, IClock>]
		                                [Scoped(typeof(Repository<>), typeof(IRepository<>))]
		                                public static partial class DataModule { }
		                                """;

		private const string Private = """
		                               using Awaiten;

		                               namespace Lib;

		                               public interface IClock { }
		                               public sealed class Clock : IClock { }
		                               public interface IRepository<T> { }

		                               [Module]
		                               [Singleton<Clock, IClock>]
		                               [Scoped(typeof(DataModule.Repository<>), typeof(IRepository<>))]
		                               public static partial class DataModule
		                               {
		                                   private sealed class Repository<T> : IRepository<T> where T : class
		                                   {
		                                       public Repository(IClock clock) { }
		                                   }
		                               }
		                               """;

		[Test]
		public async Task TheLibrary_GeneratesAGenericFactoryWithTheConstraints()
		{
			GeneratorResult result = Generator.Run(Internal);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("public static global::Lib.IRepository<T> Registration_Repository_").And
				.Contains("<T>(global::Lib.IClock @clock) where T : class => new global::Lib.Repository<T>(@clock);")
				.Because("the factory takes the implementation's type parameters and constraints, so a consumer closes it per use");
		}

		[Test]
		public async Task AContainerInTheSameAssembly_ConstructsTheInternalImplementation()
		{
			GeneratorResult result = Generator.Run(WithContainer(Internal));

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "LibContainer")).Contains("new global::Lib.Repository<global::Lib.Order>(");
		}

		[Test]
		public async Task AContainerInAnotherAssembly_ClosesTheGeneratedFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Internal, Consumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("the consumer closes the module's generic factory instead of reporting AWT230");
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Registration_Repository_").And
				.Contains("<global::App.Order>(");
		}

		[Test]
		public async Task AFriendContainer_ConstructsTheInternalImplementation()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Internal), Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("new global::Lib.Repository<global::App.Order>(");
		}

		[Test]
		public async Task APrivateImplementation_IsBuiltForAContainerInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run(WithContainer(Private));

			await That(result.Diagnostics).IsEmpty()
				.Because("the container calls the module's factory per closing instead of reporting AWT216");
			await That(ContainerSource(result, "LibContainer")).Contains("global::Lib.DataModule.Internals.Registration_Repository_").And
				.Contains("<global::Lib.Order>(");
		}

		[Test]
		public async Task APrivateImplementation_IsBuiltForAContainerInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Private, Consumer);

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task APrivateImplementation_IsBuiltForAFriendContainer()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Private), Consumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("InternalsVisibleTo does not open a private type, so a friend calls the factory too");
		}

		[Test]
		public async Task AClosingThatViolatesTheConstraints_IsReportedInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Internal, """
			                                                                                 using Awaiten;
			                                                                                 using Lib;

			                                                                                 namespace App;

			                                                                                 public sealed class Checkout
			                                                                                 {
			                                                                                     public Checkout(IRepository<int> numbers) { }
			                                                                                 }

			                                                                                 [Container]
			                                                                                 [Import(typeof(DataModule))]
			                                                                                 [Transient<Checkout>]
			                                                                                 public static partial class AppContainer
			                                                                                 {
			                                                                                 }
			                                                                                 """);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT126"))
				.Because("the closing is checked against the implementation's constraints, which the factory carries too, and the missing closing is not reported again");
		}
	}

	public class ClosedAndOpenRegistrations
	{
		private const string ReaderConsumer = """
		                                      using Awaiten;
		                                      using Lib;

		                                      namespace App;

		                                      public sealed class Checkout
		                                      {
		                                          public Checkout(IRepository<Item> repository, IReader<Item> reader) { }
		                                      }

		                                      [Container]
		                                      [Import(typeof(DataModule))]
		                                      [Transient<Checkout>]
		                                      public static partial class AppContainer
		                                      {
		                                      }
		                                      """;

		private static string Library(string repository, string covering = " : IReader<T>")
			=> $$"""
			     using Awaiten;

			     namespace Lib;

			     public interface IReader<T> { }
			     public interface IRepository<T>{{covering}} { }
			     public sealed class Item { }

			     [Module]
			     [Singleton<{{repository}}<Item>, IReader<Item>>]
			     [Singleton(typeof({{repository}}<>), typeof(IRepository<>))]
			     public static partial class DataModule
			     {
			         private sealed class Hidden<T> : IRepository<T>, IReader<T> { }
			     }

			     internal sealed class Repository<T> : IRepository<T>, IReader<T> { }
			     """;

		private static string InTheSameAssembly(string library)
			=> library + ReaderConsumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty);

		[Test]
		[Arguments("Repository")]
		[Arguments("DataModule.Hidden")]
		public async Task AContainerInAnotherAssembly_SharesOneInstanceThroughTheCoveringFactory(string repository)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(repository), ReaderConsumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("the open factory returns IRepository<Item>, which covers the closed registration's service, so it builds the one instance");
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Registration_").And.Contains("<global::Lib.Item>()");
		}

		[Test]
		public async Task AContainerInTheSameAssembly_SharesOneInstanceOfAPrivateType()
		{
			GeneratorResult result = Generator.Run(InTheSameAssembly(Library("DataModule.Hidden")));

			await That(result.Diagnostics).IsEmpty()
				.Because("the container calls the one factory whose return type covers both services");
		}

		[Test]
		public async Task ServicesNoFactoryCovers_AreReportedInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library("Repository", covering: string.Empty), ReaderConsumer);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("Lib.IReader<Lib.Item>, Lib.IRepository<Lib.Item>"))
				.Because("neither generated factory returns a type that serves both services of the one instance");
		}

		[Test]
		public async Task ServicesNoFactoryCovers_AreReportedForAPrivateTypeInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run(InTheSameAssembly(Library("DataModule.Hidden", covering: string.Empty)));

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT216") && diagnostic.Contains("no accessible type it converts to covers all of its services"));
		}

		[Test]
		public async Task AFriend_ConstructsTheInternalTypeOnce()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library("Repository", covering: string.Empty)), ReaderConsumer);

			await That(result.Diagnostics).IsEmpty();
		}
	}

	public class Constraints
	{
		[Test]
		[Arguments("where T : struct", "where T : struct", "int")]
		[Arguments("where T : unmanaged", "where T : unmanaged", "int")]
		[Arguments("where T : notnull", "where T : notnull", "string")]
		[Arguments("where T : class?", "where T : class?", "string")]
		[Arguments("where T : class, new()", "where T : class, new()", "App.Order")]
		[Arguments("where T : IEntity", "where T : global::Lib.IEntity", "App.Order")]
		[Arguments("where T : IEquatable<T>", "where T : global::System.IEquatable<T>", "App.Order")]
		public async Task AreCopiedOntoTheFactory(string declared, string emitted, string closing)
		{
			string library = $$"""
			                   #nullable enable
			                   using System;
			                   using Awaiten;

			                   namespace Lib;

			                   public interface IEntity { }
			                   public interface IRepository<T> { }
			                   internal sealed class Repository<T> : IRepository<T> {{declared}} { }

			                   [Module]
			                   [Singleton(typeof(Repository<>), typeof(IRepository<>))]
			                   public static partial class DataModule { }
			                   """;

			GeneratorResult own = Generator.Run(library);
			GeneratorResult consumed = Generator.RunWithGeneratedReferencedAssembly(library, $$"""
			                                                                                     #nullable enable
			                                                                                     using System;
			                                                                                     using Awaiten;
			                                                                                     using Lib;

			                                                                                     namespace App;

			                                                                                     public sealed class Order : IEntity, IEquatable<Order>
			                                                                                     {
			                                                                                         public bool Equals(Order? other) => ReferenceEquals(this, other);
			                                                                                     }

			                                                                                     public sealed class Checkout
			                                                                                     {
			                                                                                         public Checkout(IRepository<{{closing}}> repository) { }
			                                                                                     }

			                                                                                     [Container]
			                                                                                     [Import(typeof(DataModule))]
			                                                                                     [Transient<Checkout>]
			                                                                                     public static partial class AppContainer
			                                                                                     {
			                                                                                     }
			                                                                                     """);

			await That(own.Diagnostics).IsEmpty();
			await That(ModuleSource(own)).Contains($"<T>() {emitted} => new global::Lib.Repository<T>();");
			await That(consumed.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AConstraintOnAnInternalType_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				internal interface IEntity { }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> where T : IEntity { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).Contains("*AWT231*DataModule*Lib.Repository<T>*its type parameter 'T' is constrained to 'Lib.IEntity'*").AsWildcard();
		}

		[Test]
		public async Task AConstraintOnAnInternalType_DoesNotFailTheLibrary()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				internal interface IEntity { }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> where T : IEntity { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""");

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("public const string RegistrationSkipped_Repository_");
		}
	}

	public class Shapes
	{
		[Test]
		public async Task SeveralTypeParametersAndNestedClosings_AreClosedPositionally()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IMap<TKey, TValue> { }
				internal sealed class Map<TKey, TValue> : IMap<TKey, TValue> where TKey : notnull { }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }
				internal sealed class Repository<T1, T2> : IMap<T1, T2> where T1 : notnull { }

				[Module]
				[Singleton(typeof(Map<,>), typeof(IMap<,>))]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", """
				     using System.Collections.Generic;
				     using Awaiten;
				     using Lib;

				     namespace App;

				     public sealed class Checkout
				     {
				         public Checkout(IMap<string, List<int>> map, IRepository<IRepository<string>> nested) { }
				     }

				     [Container]
				     [Import(typeof(DataModule))]
				     [Transient<Checkout>]
				     public static partial class AppContainer
				     {
				     }
				     """);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("Registration_Map_").And
				.Contains("<string, global::System.Collections.Generic.List<int>>(").And
				.Contains("<global::Lib.IRepository<string>>(");
		}

		[Test]
		public async Task AVariantService_IsClosedLikeAnInvariantOne()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IRepository<out T> { }
				internal sealed class Repository<T> : IRepository<T> where T : class { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task SeveralServices_ShareOneFactoryReturningTheCoveringService()
		{
			const string library = """
			                       using Awaiten;

			                       namespace Lib;

			                       public interface IReader<T> { }
			                       public interface IRepository<T> : IReader<T> { }
			                       internal sealed class Repository<T> : IRepository<T> { }

			                       [Module]
			                       [Singleton(typeof(Repository<>), typeof(IReader<>))]
			                       [Singleton(typeof(Repository<>), typeof(IRepository<>))]
			                       public static partial class DataModule { }
			                       """;

			GeneratorResult own = Generator.Run(library);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, """
				using Awaiten;
				using Lib;

				namespace App;

				public sealed class Order { }

				public sealed class Checkout
				{
				    public Checkout(IReader<Order> reader, IRepository<Order> repository) { }
				}

				[Container]
				[Import(typeof(DataModule))]
				[Transient<Checkout>]
				public static partial class AppContainer
				{
				}
				""");

			await That(ModuleSource(own)).Contains("public static global::Lib.IRepository<T> Registration_Repository_")
				.Because("one factory returns the service that covers the other, so both closings share one instance");
			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task SeveralServicesOfAPrivateType_AreBuiltForAContainerInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				public interface IReader<T> { }
				public interface IRepository<T> : IReader<T> { }
				public sealed class Order { }

				public sealed class Checkout
				{
				    public Checkout(IReader<Order> reader, IRepository<Order> repository) { }
				}

				[Module]
				[Singleton(typeof(DataModule.Repository<>), typeof(IReader<>))]
				[Singleton(typeof(DataModule.Repository<>), typeof(IRepository<>))]
				public static partial class DataModule
				{
				    private sealed class Repository<T> : IRepository<T> { }
				}

				[Container]
				[Import(typeof(DataModule))]
				[Transient<Checkout>]
				public static partial class LibContainer
				{
				}
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("the container holds the one instance as the covering service the factory returns");
		}

		[Test]
		public async Task SeveralServicesWithoutACoveringService_AreReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IReader<T> { }
				public interface IWriter<T> { }
				internal sealed class Repository<T> : IReader<T>, IWriter<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IReader<>))]
				[Singleton(typeof(Repository<>), typeof(IWriter<>))]
				public static partial class DataModule { }
				""", """
				     using Awaiten;
				     using Lib;

				     namespace App;

				     public sealed class Order { }

				     public sealed class Checkout
				     {
				         public Checkout(IReader<Order> reader) { }
				     }

				     [Container]
				     [Import(typeof(DataModule))]
				     [Transient<Checkout>]
				     public static partial class AppContainer
				     {
				     }
				     """);

			await That(result.Diagnostics).Contains("*AWT231*it is registered under several services (Lib.IReader<T>, Lib.IWriter<T>)*").AsWildcard();
			await That(result.Diagnostics).DoesNotContain("*error CS*").AsWildcard();
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard()
				.Because("the closing the consumer needs comes from the registration AWT231 already reports");
		}

		[Test]
		public async Task KeysFallbacksAndContextualBindings_ApplyPerClosing()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }
				internal sealed class Archive<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), Fallback = Fallback.Silent)]
				[Singleton(typeof(Archive<>), typeof(IRepository<>), Key = "archive")]
				public static partial class DataModule { }
				""", """
				     using Awaiten;
				     using Lib;

				     namespace App;

				     public sealed class Order { }
				     public sealed class OwnRepository<T> : IRepository<T> { }

				     public sealed class Checkout
				     {
				         public Checkout(IRepository<Order> orders, [FromKey("archive")] IRepository<Order> archive) { }
				     }

				     [Container]
				     [Import(typeof(DataModule))]
				     [Singleton(typeof(OwnRepository<>), typeof(IRepository<>))]
				     [Transient<Checkout>]
				     public static partial class AppContainer
				     {
				     }
				     """);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("Registration_Archive_").And
				.DoesNotContain("Registration_Repository_")
				.Because("the container's own registration replaces the module's fallback for every closing, while the keyed one stays");
		}

		[Test]
		public async Task OptionalAndDeferredInjectedProperties_AreMirroredOntoTheGenericFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				#nullable enable
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T>
				{
				    [Inject(Optional = true)] public IClock? Clock { get; set; }
				    [Inject(Deferred = true)] public IRepository<T>? Self { get; set; }
				}

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("Deferred_Registration_Repository_").And.Contains("_Self<global::App.Order>(");
		}

		[Test]
		public async Task AnInternalModule_BuildsItsPrivateTypeUnderAnInternalServiceThroughAFactory()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				internal interface IRepository<T> { }

				[Module]
				[Singleton(typeof(DataModule.Repository<>), typeof(IRepository<>))]
				internal static partial class DataModule
				{
				    private sealed class Repository<T> : IRepository<T> { }
				}

				internal sealed class Checkout
				{
				    public Checkout(IRepository<string> names) { }
				}

				[Container]
				[Import(typeof(DataModule))]
				[Transient<Checkout>]
				internal static partial class LibContainer
				{
				}
				""");

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("public static global::Lib.IRepository<T> Registration_Repository_")
				.Because("the factory of an internal module may take and return any type of its assembly");
		}

		[Test]
		public async Task APublicModule_BuildsItsPrivateTypeUnderAnInternalServiceThroughAnInternalFactory()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				internal interface IRepository<T> { }

				[Module]
				[Singleton(typeof(DataModule.Repository<>), typeof(IRepository<>))]
				public static partial class DataModule
				{
				    private sealed class Repository<T> : IRepository<T> { }
				}

				internal sealed class Checkout
				{
				    public Checkout(IRepository<string> names) { }
				}

				[Container]
				[Import(typeof(DataModule))]
				[Transient<Checkout>]
				internal static partial class LibContainer
				{
				}
				""");

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("internal static global::Lib.IRepository<T> Registration_Repository_")
				.Because("a public factory could not return the internal service");
		}

		[Test]
		public async Task ATypeParameterNamedLikeAParameter_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public interface IRepository<T> { }
				internal sealed class Repository<clock> : IRepository<clock>
				{
				    public Repository(IClock clock) { }
				}

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).Contains("*AWT231*its constructor parameter or injected property 'clock' has the name of a type parameter*").AsWildcard();
		}

		[Test]
		public async Task AnInternalParameterType_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				internal interface IClock { }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T>
				{
				    public Repository(IClock clock) { }
				}

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).Contains("*AWT231*DataModule*its constructor parameter 'clock' has the type 'Lib.IClock', which is not accessible outside the module's assembly*").AsWildcard();
		}

		[Test]
		public async Task AnInaccessibleConvention_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using System.Threading.Tasks;
				using Awaiten;

				namespace Lib;

				internal interface IWarmup { Task WarmUpAsync(); }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T>, IWarmup
				{
				    public Task WarmUpAsync() => Task.CompletedTask;
				}

				[Module]
				[AsyncInitializer<IWarmup>(nameof(IWarmup.WarmUpAsync))]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static partial class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).Contains("*AWT231*DataModule*initialized by the async initializer 'Lib.IWarmup.WarmUpAsync'*").AsWildcard()
				.Because("the consumer could not run the convention, so the closing is not built without it");
		}
	}

	public class Hooks
	{
		private const string Internal = """
		                                using Awaiten;

		                                namespace Lib;

		                                public interface IAudit { }
		                                public sealed class Audit : IAudit { }
		                                public interface IRepository<T> { }
		                                internal sealed class Repository<T> : IRepository<T> where T : class { }

		                                [Module]
		                                [Singleton<Audit, IAudit>]
		                                [Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
		                                public static partial class DataModule
		                                {
		                                    internal static void Activate<T>(Repository<T> repository, IAudit audit) where T : class { }

		                                    internal static void Release(object repository) { }
		                                }
		                                """;

		private const string Private = """
		                               using Awaiten;

		                               namespace Lib;

		                               public interface IAudit { }
		                               public sealed class Audit : IAudit { }
		                               public interface IRepository<T> { }

		                               [Module]
		                               [Singleton<Audit, IAudit>]
		                               [Scoped(typeof(DataModule.Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
		                               public static partial class DataModule
		                               {
		                                   private sealed class Repository<T> : IRepository<T> where T : class { }

		                                   private static void Activate<T>(Repository<T> repository, IAudit audit) where T : class { }
		                               }
		                               """;

		[Test]
		public async Task TheLibrary_GeneratesAGenericWrapperPerHook()
		{
			GeneratorResult result = Generator.Run(Internal);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("public static void Hook_OnActivated_Repository_").And
				.Contains("<T>(global::Lib.IRepository<T> @awaiten__instance, global::Lib.IAudit @audit) where T : class => Activate<T>((global::Lib.Repository<T>)@awaiten__instance, @audit);").And
				.Contains("<T>(global::Lib.IRepository<T> @awaiten__instance) where T : class => Release((object)@awaiten__instance);")
				.Because("the wrapper takes the definition's type parameters and constraints, so a consumer closes it like the factory");
		}

		[Test]
		public async Task AContainerInTheSameAssembly_CallsTheHooksItself()
		{
			GeneratorResult result = Generator.Run(WithContainer(Internal));

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "LibContainer")).Contains("new global::Lib.Repository<global::Lib.Order>(").And
				.Contains("global::Lib.DataModule.Activate<global::Lib.Order>(");
		}

		[Test]
		public async Task AContainerInAnotherAssembly_RunsTheClosedWrappers()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Internal, Consumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("the consumer closes the module's hook wrappers with each closing instead of reporting AWT231");
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Registration_Repository_").And
				.Contains("global::Lib.DataModule.Internals.Hook_OnActivated_Repository_").And
				.Contains("global::Lib.DataModule.Internals.Hook_OnRelease_Repository_").And
				.Contains("<global::App.Order>(");
		}

		[Test]
		public async Task AFriendContainer_CallsTheHooksItself()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Internal), Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Activate<global::App.Order>(").And
				.DoesNotContain("Hook_OnActivated_Repository_");
		}

		[Test]
		public async Task APrivateImplementation_RunsTheWrapperInEveryAssembly()
		{
			GeneratorResult same = Generator.Run(WithContainer(Private));
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Private, Consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(Private), Consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(ContainerSource(same, "LibContainer")).Contains("global::Lib.DataModule.Internals.Hook_OnActivated_Repository_").And
				.Contains("<global::Lib.Order>(")
				.Because("only the module can name its private implementation and call its private hook");
			await That(external.Diagnostics).IsEmpty();
			await That(ContainerSource(external, "AppContainer")).Contains("global::Lib.DataModule.Internals.Hook_OnActivated_Repository_");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "AppContainer")).Contains("global::Lib.DataModule.Internals.Hook_OnActivated_Repository_");
		}

		[Test]
		public async Task AHookParameterOfTheTypeParameter_ClosesPerClosing()
		{
			const string library = """
			                       using Awaiten;

			                       namespace Lib;

			                       public interface IValidator<T> { }
			                       public sealed class Validator<T> : IValidator<T> { }
			                       public interface IRepository<T> { }
			                       internal sealed class Repository<T> : IRepository<T> { }

			                       [Module]
			                       [Singleton(typeof(Validator<>), typeof(IValidator<>))]
			                       [Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
			                       public static partial class DataModule
			                       {
			                           internal static void Activate<T>(Repository<T> repository, IValidator<T> validator) { }
			                       }
			                       """;

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("the wrapper's IValidator<T> parameter closes the open validator for each closing of the repository");
			await That(ContainerSource(result, "AppContainer")).Contains("new global::Lib.Validator<global::App.Order>(");
		}

		[Test]
		public async Task AnExtendingContainer_RunsTheBasesWrapper()
		{
			const string shop = """
			                    using Awaiten;

			                    namespace Lib;

			                    public interface IRepository<T> { }
			                    public sealed class Order { }

			                    [Container]
			                    [Scoped(typeof(Shop.Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
			                    [Scoped<Shop.Checkout>]
			                    public static partial class Shop
			                    {
			                        private sealed class Repository<T> : IRepository<T> { }

			                        private static void Activate<T>(Repository<T> repository) { }

			                        internal sealed class Checkout
			                        {
			                            public Checkout(IRepository<Order> orders) { }
			                        }
			                    }
			                    """;
			const string extending = """
			                         using Awaiten;
			                         using Lib;

			                         namespace App;

			                         [Container(Extends = typeof(Shop))]
			                         public static partial class TestShop
			                         {
			                         }
			                         """;

			GeneratorResult same = Generator.Run(shop + extending.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty));
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(shop), extending);

			await That(same.Diagnostics).IsEmpty();
			await That(ContainerSource(same, "TestShop")).Contains("global::Lib.Shop.Internals.Hook_OnActivated_Repository_").And.Contains("<global::Lib.Order>(");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "TestShop")).Contains("global::Lib.Shop.Internals.Hook_OnActivated_Repository_");
		}

		[Test]
		public async Task AHookParameterAnotherAssemblyCannotName_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				internal sealed class Log { }
				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton<Log>]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
				    internal static void Activate(object repository, Log log) { }
				}
				""", Consumer);

			await That(result.Diagnostics).HasCount(1).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT231")
			                                                                           && diagnostic.Contains("its OnActivated hook 'Activate' cannot be run through a generated wrapper")
			                                                                           && diagnostic.Contains("Lib.Log"))
				.Because("the closing the consumer needs is not reported missing as well");
		}

		[Test]
		public async Task AGenericHookTheDefinitionCannotClose_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
				    internal static void Activate<T>(Repository<T> repository) where T : class { }
				}
				""", Consumer);

			await That(result.Diagnostics).HasCount(1).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT231")
			                                                                           && diagnostic.Contains("its OnActivated hook 'Activate' cannot be run through a generated wrapper: the constraints of its generic overload do not hold for every closing of 'Lib.Repository<T>'")
			                                                                           && !diagnostic.Contains("has no accessible static method"))
				.Because("the wrapper serves every closing, so the hook's constraints must hold for the definition's own type parameters");
		}

		[Test]
		public async Task AGenericHookWithTheTypeParametersInAnotherOrder_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IRepository<TKey, TValue> { }
				internal sealed class Repository<TKey, TValue> : IRepository<TKey, TValue> { }

				[Module]
				[Singleton(typeof(Repository<,>), typeof(IRepository<,>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
				    internal static void Activate<TValue, TKey>(Repository<TKey, TValue> repository) { }
				}
				""", """
				     using Awaiten;
				     using Lib;

				     namespace App;

				     public sealed class Checkout
				     {
				         public Checkout(IRepository<int, string> names) { }
				     }

				     [Container]
				     [Import(typeof(DataModule))]
				     [Transient<Checkout>]
				     public static partial class AppContainer
				     {
				     }
				     """);

			await That(result.Diagnostics).HasCount(1).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT231")
			                                                                           && diagnostic.Contains("its OnActivated hook 'Activate' cannot be run through a generated wrapper: its generic overload does not take the type parameters of 'Lib.Repository<TKey, TValue>' in their order")
			                                                                           && !diagnostic.Contains("has no accessible static method"));
		}

		[Test]
		public async Task AHookThatNeedsAnEverLargerClosing_IsReportedOnceAtTheImport()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
				    internal static void Activate<T>(Repository<T> repository, IRepository<List<T>> next) { }
				}
				""", Consumer);

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains("AWT129"))).HasCount(1).And
				.All().Satisfy(diagnostic => diagnostic.StartsWith("(14,2): error AWT129", StringComparison.Ordinal))
				.Because("the hook's work belongs to the closed implementation, so it finds the open registration and shares its report");
		}

		public class AnOverloadOnlyAClosingAccepts
		{
			private const string GenericFirst = """
			                                    internal static void Activate<T>(IRepository<T> repository) { }

			                                    internal static void Activate(IRepository<Ledger> repository, IAudit audit) { }
			                                    """;

			private const string ClosedFirst = """
			                                   internal static void Activate(IRepository<Ledger> repository, IAudit audit) { }

			                                   internal static void Activate<T>(IRepository<T> repository) { }
			                                   """;

			private const string LedgerConsumer = """
			                                      using Awaiten;
			                                      using Lib;

			                                      namespace App;

			                                      public sealed class Checkout
			                                      {
			                                          public Checkout(IRepository<Ledger> ledgers) { }
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

				     public interface IAudit { }
				     public sealed class Audit : IAudit { }
				     public interface IRepository<T> { }
				     public sealed class Ledger { }

				     [Module]
				     [Singleton<Audit, IAudit>]
				     [Scoped(typeof(DataModule.Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				     public static partial class DataModule
				     {
				         private sealed class Repository<T> : IRepository<T> { }

				     {{overloads}}
				     }
				     """;

			private static string SameAssembly(string overloads)
				=> Library(overloads) + LedgerConsumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty);

			[Test]
			[Arguments(GenericFirst)]
			[Arguments(ClosedFirst)]
			public async Task AContainerInTheSameAssembly_ReportsTheAmbiguityForThatClosing(string overloads)
			{
				GeneratorResult result = Generator.Run(SameAssembly(overloads));

				await That(result.Diagnostics).HasCount(1).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT190") && diagnostic.Contains("'Activate'"))
					.Because("both overloads accept the closing, as for a container that builds the closing itself, whatever their order");
			}

			[Test]
			[Arguments(GenericFirst)]
			[Arguments(ClosedFirst)]
			public async Task AFriendContainer_ReportsTheAmbiguityForThatClosing(string overloads)
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library(overloads)), LedgerConsumer);

				await That(result.Diagnostics).HasCount(1).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT190") && diagnostic.Contains("'Activate'"));
			}

			[Test]
			[Arguments(GenericFirst)]
			[Arguments(ClosedFirst)]
			public async Task AContainerInAnotherAssembly_RunsTheWrapperTheModuleBound(string overloads)
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(overloads), LedgerConsumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the internal overloads are not visible to this assembly, so it runs the generic overload the module wrapped for every closing");
				await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Hook_OnActivated_Repository_").And
					.Contains("<global::Lib.Ledger>(");
			}

			[Test]
			[Arguments(GenericFirst)]
			[Arguments(ClosedFirst)]
			public async Task AClosingOnlyTheGenericOverloadAccepts_RunsItThroughTheWrapper(string overloads)
			{
				GeneratorResult result = Generator.Run(WithContainer(Library(overloads)));

				await That(result.Diagnostics).IsEmpty()
					.Because("the overload for the ledger repository does not accept the order repository, so the generic overload is the only one");
				await That(ContainerSource(result, "LibContainer")).Contains("global::Lib.DataModule.Internals.Hook_OnActivated_Repository_").And
					.Contains("<global::Lib.Order>(");
			}
		}
	}

	public class Unsupported
	{
		[Test]
		public async Task AModuleThatIsNotPartial_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>))]
				public static class DataModule { }
				""", Consumer);

			await That(result.Diagnostics).Contains("*AWT230*DataModule*the open generic registration of 'Lib.Repository<T>'*").AsWildcard()
				.Because("a module that is not partial carries neither a factory nor a record of why");
		}

		[Test]
		public async Task ATypeNestedInAGenericType_IsReportedWithTheReasonAndDoesNotFailTheLibrary()
		{
			const string library = """
			                       using Awaiten;

			                       namespace Lib;

			                       public interface IRepository<T> { }
			                       public class Outer<T>
			                       {
			                           internal sealed class Inner<U> : IRepository<U> { }
			                       }

			                       [Module]
			                       [Scoped(typeof(Outer<>.Inner<>), typeof(IRepository<>))]
			                       public static partial class DataModule { }
			                       """;

			GeneratorResult own = Generator.Run(library);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

			await That(own.Diagnostics).IsEmpty();
			await That(result.Diagnostics).Contains("*AWT125*'Lib.Outer<T>.Inner<U>' is nested in the generic type 'Lib.Outer<T>'*").AsWildcard()
				.Because("no container can take the containing type's type parameters from a closing of the service");
		}
	}

	public class InjectedProperties
	{
		private const string Library = """
		                               using Awaiten;

		                               namespace Lib;

		                               public interface IClock { }
		                               public sealed class Clock : IClock { }
		                               public interface IRepository<T>
		                               {
		                                   IClock Clock { get; }
		                               }
		                               public sealed class Repository<T> : IRepository<T>
		                               {
		                                   [Inject] internal IClock Injected { get; set; } = null!;

		                                   public IClock Clock => Injected;
		                               }

		                               [Module]
		                               [Singleton<Clock, IClock>]
		                               [Singleton(typeof(Repository<>), typeof(IRepository<>))]
		                               public static partial class DataModule { }
		                               """;

		[Test]
		public async Task AnInternalInjectedPropertyOfAPublicType_IsSetByTheFactoryInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Registration_Repository_")
				.Because("the consumer does not see the internal property, so the factory sets it instead of the property being left out");
		}

		[Test]
		public async Task AnInternalInjectedPropertyOfAPublicType_IsSetByAFriend()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library), Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("Injected = ");
		}

		public class ContainerSideEntry
		{
			private const string Library = """
			                               using Awaiten;

			                               namespace Lib;

			                               public interface IClock { }
			                               public sealed class Clock : IClock { }
			                               public interface IRepository<T> { }
			                               public sealed class Repository<T> : IRepository<T>
			                               {
			                                   [Inject] internal IClock Injected { get; set; } = null!;

			                                   public IClock Audit { get; set; }
			                               }

			                               [Module]
			                               [Singleton<Clock, IClock>]
			                               [Singleton(typeof(Repository<>), typeof(IRepository<>))]
			                               public static partial class DataModule { }
			                               """;

			private const string EntryConsumer = """
			                                     using Awaiten;
			                                     using Lib;

			                                     namespace App;

			                                     public sealed class Order { }

			                                     public sealed class Checkout
			                                     {
			                                         public Checkout(IRepository<Order> orders) { }
			                                     }

			                                     [Container]
			                                     [Import(typeof(DataModule))]
			                                     [InjectProperty(typeof(Repository<>), nameof(Repository<object>.Audit))]
			                                     [Transient<Checkout>]
			                                     public static partial class AppContainer
			                                     {
			                                     }
			                                     """;

			[Test]
			public async Task AContainerInTheSameAssembly_SetsItInTheObjectInitializer()
			{
				GeneratorResult result = Generator.Run(Library + EntryConsumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace App;", string.Empty));

				await That(result.Diagnostics).IsEmpty();
				await That(ContainerSource(result, "AppContainer")).Contains("Injected = ").And.Contains("Audit = ");
			}

			[Test]
			public async Task AContainerInAnotherAssembly_SetsItOnTheInstanceTheGenericFactoryReturns()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, EntryConsumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the entry is no AWT178: the generic factory returns the closing itself, so the container sets the property after it");
				await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Registration_Repository_").And
					.Contains(".Audit = ");
			}

			[Test]
			public async Task AFriendContainer_SetsItInTheObjectInitializer()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library), EntryConsumer);

				await That(result.Diagnostics).IsEmpty();
				await That(ContainerSource(result, "AppContainer")).Contains("Injected = ").And.Contains("Audit = ").And.DoesNotContain("Registration_Repository_");
			}

			[Test]
			public async Task ADeferredEntry_IsSetAfterTheInstanceIsCached()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, EntryConsumer.Replace(".Audit))]", ".Audit), Deferred = true)]"));

				await That(result.Diagnostics).IsEmpty();
				await That(ContainerSource(result, "AppContainer")).Contains("Registration_Repository_").And.Contains("._repository.Audit = ");
			}
		}
	}

	public class Decorators
	{
		private const string Internal = """
		                                using Awaiten;

		                                namespace Lib;

		                                public interface IClock { }
		                                public sealed class Clock : IClock { }
		                                public interface IRepository<T> { }
		                                public sealed class Repository<T> : IRepository<T> { }
		                                internal sealed class CachedRepository<T> : IRepository<T>
		                                {
		                                    public CachedRepository(IRepository<T> inner, IClock clock) { }
		                                }

		                                [Module]
		                                [Singleton<Clock, IClock>]
		                                [Scoped(typeof(Repository<>), typeof(IRepository<>))]
		                                [Decorate(typeof(CachedRepository<>), typeof(IRepository<>))]
		                                public static partial class DataModule { }
		                                """;

		private const string Private = """
		                               using Awaiten;

		                               namespace Lib;

		                               public interface IClock { }
		                               public sealed class Clock : IClock { }
		                               public interface IRepository<T> { }
		                               public sealed class Repository<T> : IRepository<T> { }

		                               [Module]
		                               [Singleton<Clock, IClock>]
		                               [Scoped(typeof(Repository<>), typeof(IRepository<>))]
		                               [Decorate(typeof(DataModule.CachedRepository<>), typeof(IRepository<>))]
		                               public static partial class DataModule
		                               {
		                                   private sealed class CachedRepository<T> : IRepository<T>
		                                   {
		                                       public CachedRepository(IRepository<T> inner, IClock clock) { }
		                                   }
		                               }
		                               """;

		[Test]
		public async Task TheLibrary_GeneratesAGenericFactoryTakingTheInnerService()
		{
			GeneratorResult result = Generator.Run(Internal);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("public static global::Lib.IRepository<T> Decorator_CachedRepository_").And
				.Contains("<T>(global::Lib.IRepository<T> @inner, global::Lib.IClock @clock) => new global::Lib.CachedRepository<T>(@inner, @clock);");
		}

		[Test]
		public async Task AContainerInAnotherAssembly_ClosesTheGeneratedFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Internal, Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Decorator_CachedRepository_");
		}

		[Test]
		public async Task AFriendContainer_ConstructsTheInternalDecorator()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Internal), Consumer);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("new global::Lib.CachedRepository<global::App.Order>(");
		}

		[Test]
		public async Task AContainerInTheSameAssembly_ConstructsTheInternalDecorator()
		{
			GeneratorResult result = Generator.Run(WithContainer(Internal));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task APrivateDecorator_IsBuiltForAContainerInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run(WithContainer(Private));

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "LibContainer")).Contains("global::Lib.DataModule.Internals.Decorator_CachedRepository_");
		}

		[Test]
		public async Task AClosingOfAPrivateDecorator_IsBuiltForAContainerInTheSameAssembly()
		{
			GeneratorResult result = Generator.Run(WithContainer("""
			                                                     using Awaiten;

			                                                     namespace Lib;

			                                                     public interface IRepository<T> { }
			                                                     public sealed class Repository<T> : IRepository<T> { }

			                                                     [Module]
			                                                     [Scoped<Repository<Order>, IRepository<Order>>]
			                                                     [Decorate<DataModule.CachedRepository<Order>, IRepository<Order>>]
			                                                     public static partial class DataModule
			                                                     {
			                                                         private sealed class CachedRepository<T> : IRepository<T>
			                                                         {
			                                                             public CachedRepository(IRepository<T> inner) { }
			                                                         }
			                                                     }
			                                                     """));

			await That(result.Diagnostics).IsEmpty()
				.Because("the closed decorator's constructor is matched by the closed factory's parameters, not by its definition's");
			await That(ContainerSource(result, "LibContainer")).Contains("global::Lib.DataModule.Internals.Decorator_CachedRepository_");
		}

		[Test]
		public async Task APrivateDecorator_IsBuiltForContainersInAnotherAssembly()
		{
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Private, Consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(Private), Consumer);

			await That(external.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
		}
	}

	public class DecoratorsOfInternalClosings
	{
		private const string ReaderConsumer = """
		                                      using Awaiten;
		                                      using Lib;

		                                      namespace App;

		                                      public sealed class Checkout
		                                      {
		                                          public Checkout(Reader reader) { }
		                                      }

		                                      [Container]
		                                      [Import(typeof(DataModule))]
		                                      [Transient<Checkout>]
		                                      public static partial class AppContainer
		                                      {
		                                      }
		                                      """;

		private static string Library(string decoratorAccessibility)
			=> $$"""
			     using Awaiten;

			     namespace Lib;

			     public interface IRepository<T> { }
			     internal sealed class Secret { }
			     internal sealed class SecretRepository : IRepository<Secret> { }
			     {{decoratorAccessibility}} sealed class CachedRepository<T> : IRepository<T>
			     {
			         public CachedRepository(IRepository<T> inner) { }
			     }
			     public sealed class Reader
			     {
			         internal Reader(IRepository<Secret> secrets) { }
			     }

			     [Module]
			     [Singleton<SecretRepository, IRepository<Secret>>]
			     [Decorate(typeof(CachedRepository<>), typeof(IRepository<>))]
			     [Singleton<Reader>]
			     public static partial class DataModule { }
			     """;

		[Test]
		[Arguments("public")]
		[Arguments("internal")]
		public async Task AContainerInAnotherAssembly_DecoratesTheHandle(string accessibility)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(accessibility), ReaderConsumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("the module closes the open decorator onto its internal closing, which the consumer cannot name, and builds it into the handle");
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Decorator_CachedRepository_");
		}

		[Test]
		[Arguments("public")]
		[Arguments("internal")]
		public async Task ContainersThatBuildTheInternalClosing_CloseTheOpenDecorator(string accessibility)
		{
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(Library(accessibility)), ReaderConsumer);
			GeneratorResult same = Generator.Run(Library(accessibility) + """

			                                                               [Container]
			                                                               [Import(typeof(DataModule))]
			                                                               public static partial class LibContainer
			                                                               {
			                                                               }
			                                                               """);

			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "AppContainer")).Contains("new global::Lib.CachedRepository<global::Lib.Secret>(");
			await That(same.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AKeyNoClosingHas_IsReportedOnceInAnotherAssembly()
		{
			string library = Library("internal").Replace(
				"[Decorate(typeof(CachedRepository<>), typeof(IRepository<>))]",
				"""[Decorate(typeof(CachedRepository<>), typeof(IRepository<>), Keys = new object[] { "audit" })]""");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, ReaderConsumer);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT210") && diagnostic.Contains("'Lib.IRepository<T>'"))
				.Because("the open decorator reports the key once, as in the module's own assembly, and not again for its closing onto the handle");
		}
	}

	public class Composites
	{
		private const string Internal = """
		                                using System.Collections.Generic;
		                                using Awaiten;

		                                namespace Lib;

		                                public interface IRepository<T> { }
		                                public sealed class Repository<T> : IRepository<T> { }
		                                public sealed class Mirror<T> : IRepository<T> { }
		                                internal sealed class AllRepositories<T> : IRepository<T>
		                                {
		                                    public AllRepositories(IEnumerable<IRepository<T>> repositories) { }
		                                }

		                                [Module]
		                                [Scoped(typeof(Repository<>), typeof(IRepository<>))]
		                                [Scoped(typeof(Mirror<>), typeof(IRepository<>))]
		                                [Composite(typeof(AllRepositories<>), typeof(IRepository<>))]
		                                public static partial class DataModule { }
		                                """;

		[Test]
		public async Task TheLibrary_GeneratesAGenericFactoryTakingTheCollection()
		{
			GeneratorResult result = Generator.Run(Internal);

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains("public static global::Lib.IRepository<T> Composite_AllRepositories_").And
				.Contains("<T>(global::System.Collections.Generic.IEnumerable<global::Lib.IRepository<T>> @repositories) => new global::Lib.AllRepositories<T>(@repositories);");
		}

		[Test]
		public async Task AnInternalClosing_IsFrontedThroughItsHandleInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Secret { }
				internal sealed class FirstSecrets : IRepository<Secret> { }
				internal sealed class SecondSecrets : IRepository<Secret> { }
				internal sealed class AllRepositories<T> : IRepository<T>
				{
				    public AllRepositories(IEnumerable<IRepository<T>> repositories) { }
				}
				public sealed class Reader
				{
				    internal Reader(IRepository<Secret> secrets) { }
				}

				[Module]
				[Singleton<FirstSecrets, IRepository<Secret>>]
				[Singleton<SecondSecrets, IRepository<Secret>>]
				[Composite(typeof(AllRepositories<>), typeof(IRepository<>))]
				[Singleton<Reader>]
				public static partial class DataModule { }
				""", """
				     using Awaiten;
				     using Lib;

				     namespace App;

				     public sealed class Checkout
				     {
				         public Checkout(Reader reader) { }
				     }

				     [Container]
				     [Import(typeof(DataModule))]
				     [Transient<Checkout>]
				     public static partial class AppContainer
				     {
				     }
				     """);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "AppContainer")).Contains("global::Lib.DataModule.Internals.Composite_AllRepositories_")
				.Because("the module closes the open composite onto its internal closing and builds it into the handle");
		}

		[Test]
		public async Task ContainersInEveryAssembly_BuildTheComposite()
		{
			GeneratorResult same = Generator.Run(WithContainer(Internal));
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Internal, Consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(Internal), Consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(ContainerSource(external, "AppContainer")).Contains("global::Lib.DataModule.Internals.Composite_AllRepositories_");
			await That(friend.Diagnostics).IsEmpty();
		}
	}

	public class BaseContainers
	{
		private const string Base = """
		                            using System.Collections.Generic;
		                            using Awaiten;

		                            namespace Lib;

		                            public interface IClock { }
		                            public sealed class Clock : IClock { }
		                            public interface IRepository<T> { }
		                            public sealed class Order { }

		                            [Container]
		                            [Singleton<Clock, IClock>]
		                            [Scoped(typeof(Shop.Repository<>), typeof(IRepository<>))]
		                            [Decorate(typeof(Shop.CachedRepository<>), typeof(IRepository<>))]
		                            [Scoped<Shop.Checkout>]
		                            public static partial class Shop
		                            {
		                                private sealed class Repository<T> : IRepository<T> where T : class
		                                {
		                                    public Repository(IClock clock) { }
		                                }

		                                private sealed class CachedRepository<T> : IRepository<T> where T : class
		                                {
		                                    public CachedRepository(IRepository<T> inner) { }
		                                }

		                                internal sealed class Checkout
		                                {
		                                    public Checkout(IRepository<Order> orders) { }
		                                }
		                            }
		                            """;

		private const string Extending = """
		                                 using Awaiten;
		                                 using Lib;

		                                 namespace App;

		                                 public sealed class FakeClock : IClock { }

		                                 [Container(Extends = typeof(Shop))]
		                                 [Singleton<FakeClock, IClock>]
		                                 public static partial class TestShop
		                                 {
		                                 }
		                                 """;

		[Test]
		public async Task TheBase_GeneratesInternalGenericFactories()
		{
			GeneratorResult result = Generator.Run(Base);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "Shop")).Contains("internal static global::Lib.IRepository<T> Registration_Repository_").And
				.Contains("internal static global::Lib.IRepository<T> Decorator_CachedRepository_");
		}

		[Test]
		public async Task AnExtendingContainerInTheSameAssembly_BuildsTheClosingsThroughThem()
		{
			GeneratorResult result = Generator.Run(Base + """

			                                              [Container(Extends = typeof(Shop))]
			                                              public static partial class TestShop
			                                              {
			                                              }
			                                              """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the extending container calls the base's factories per closing instead of reporting AWT226");
			await That(ContainerSource(result, "TestShop")).Contains("global::Lib.Shop.Internals.Registration_Repository_").And
				.Contains("global::Lib.Shop.Internals.Decorator_CachedRepository_");
		}

		[Test]
		public async Task AnExtendingContainerInAnotherAssembly_BuildsTheClosingsThroughThem()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Base), Extending);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, "TestShop")).Contains("global::Lib.Shop.Internals.Registration_Repository_");
		}

		[Test]
		public async Task AnExtendingContainerWithoutInternalsVisibleTo_IsToldToGrantIt()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Base, Extending);

			await That(result.Diagnostics).Contains("*AWT226*Lib.Shop.Repository<T>*internal generated factory*InternalsVisibleTo*").AsWildcard();
		}

		private const string ExtendingInTheSameAssembly = """

		                                                  [Container(Extends = typeof(Shop))]
		                                                  public static partial class TestShop
		                                                  {
		                                                  }
		                                                  """;

		private static string InjectPropertyBase(string entry, string clock = "[Singleton<Clock, IClock>]")
			=> $$"""
			     using Awaiten;

			     namespace Lib;

			     public interface IClock { }
			     public sealed class Clock : IClock { }
			     public interface IRepository<T> { }
			     public sealed class Order { }

			     [Container]
			     {{clock}}
			     [Scoped(typeof(Shop.Repository<>), typeof(IRepository<>))]
			     {{entry}}
			     [Scoped<Shop.Checkout>]
			     public static partial class Shop
			     {
			         private sealed class Repository<T> : IRepository<T>
			         {
			             public IClock Clock { get; set; } = null!;
			         }

			         internal sealed class Checkout
			         {
			             public Checkout(IRepository<Order> orders) { }
			         }
			     }
			     """;

		[Test]
		public async Task AnInjectPropertyEntryOfTheBase_IsSetByTheGenericFactory()
		{
			string library = InjectPropertyBase("""[InjectProperty(typeof(Shop.Repository<>), "Clock")]""");

			GeneratorResult same = Generator.Run(library + ExtendingInTheSameAssembly);
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Extending);

			await That(ContainerSource(same, ".Shop.")).Contains("<T>(global::Lib.IClock @Clock) => new global::Lib.Shop.Repository<T>() { Clock = @Clock };")
				.Because("the base mirrors its container-side entry onto the factory like an [Inject] property");
			await That(same.Diagnostics).IsEmpty()
				.Because("the extending container in the same assembly passes the property by name");
			await That(other.Diagnostics).IsEmpty();
			await That(ContainerSource(other, "TestShop")).Contains("global::Lib.Shop.Internals.Registration_Repository_").And.DoesNotContain("<global::Lib.Order>()")
				.Because("the extending container in another assembly resolves the factory's parameter instead of dropping the property");
		}

		[Test]
		public async Task AKeyedInjectPropertyEntryOfTheBase_IsMirroredWithItsKey()
		{
			GeneratorResult result = Generator.Run(InjectPropertyBase("""[InjectProperty(typeof(Shop.Repository<>), "Clock", Key = "wall")]""", """[Singleton<Clock, IClock>(Key = "wall")]""") + ExtendingInTheSameAssembly);

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result, ".Shop.")).Contains("""<T>([global::Awaiten.FromKey("wall")] global::Lib.IClock @Clock)""");
		}

		[Test]
		[Arguments("Optional = true", "is injected only when its service is registered")]
		[Arguments("Deferred = true", "is injected after construction")]
		public async Task AnInjectPropertyEntryTheFactoryCannotExpress_IsReportedWithTheReason(string option, string reason)
		{
			GeneratorResult result = Generator.Run(InjectPropertyBase($$"""[InjectProperty(typeof(Shop.Repository<>), "Clock", {{option}})]""") + ExtendingInTheSameAssembly);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT226") && diagnostic.Contains($"its property 'Clock' {reason}"));
		}

		[Test]
		public async Task AnOptionalInjectProperty_IsBuiltForExtendingContainers()
		{
			string library = "#nullable enable" + Environment.NewLine + InjectPropertyBase(string.Empty).Replace("public IClock Clock { get; set; } = null!;", "[Inject(Optional = true)] public IClock? Clock { get; set; }");

			GeneratorResult same = Generator.Run(library + ExtendingInTheSameAssembly);
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Extending);

			await That(same.Diagnostics).IsEmpty()
				.Because("the generic factory mirrors an optional [Inject] property, unlike the factory of a closed private type");
			await That(other.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task ADeferredInjectProperty_IsSetThroughTheBasesSetter()
		{
			string library = InjectPropertyBase(string.Empty).Replace("public IClock Clock { get; set; } = null!;", "[Inject(Deferred = true)] public IClock Clock { get; set; } = null!;");

			GeneratorResult same = Generator.Run(library + ExtendingInTheSameAssembly);
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Extending);

			await That(same.Diagnostics).IsEmpty()
				.Because("the base emits the setter wrapper of its generic factory's deferred property, which the extending container calls");
			await That(other.Diagnostics).IsEmpty();
			await That(ContainerSource(other, "TestShop")).Contains("global::Lib.Shop.Internals.Deferred_Registration_Repository_")
				.Because("an extending container in another assembly finds the setter next to the factory instead of leaving the property unset");
		}

		[Test]
		public async Task AClosedInjectPropertyEntryForAClosing_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.Run(InjectPropertyBase("""[InjectProperty(typeof(Shop.Repository<Order>), "Clock")]""") + ExtendingInTheSameAssembly);

			await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT226") && diagnostic.Contains("'Lib.Shop.Repository<Lib.Order>'"))
				.Because("one generic factory serves every closing, so it cannot fill a property for one closing only");
		}

		[Test]
		public async Task AnOpenRegistrationTheBaseCannotBuild_IsReportedWithTheReason()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				public sealed class Order { }

				[Container]
				[Scoped(typeof(Shop.Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				[Scoped<Shop.Checkout>]
				public static partial class Shop
				{
				    private static void Activate<T>(Repository<T> repository) where T : class { }

				    private sealed class Repository<T> : IRepository<T> { }

				    internal sealed class Checkout
				    {
				        public Checkout(IRepository<Order> orders) { }
				    }
				}

				[Container(Extends = typeof(Shop))]
				public static partial class TestShop
				{
				}
				""");

			await That(result.Diagnostics).HasCount(1).And.All().Satisfy(diagnostic => diagnostic.Contains("AWT226") && diagnostic.Contains("its OnActivated hook 'Activate' cannot be run through a generated wrapper"))
				.Because("the closing the inherited consumer needs is not reported missing as well");
		}
	}

	public class ExpansionDepth
	{
		[Test]
		public async Task AWT129_PointsAtTheOpenRegistration()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace MyCode;

				public interface INode<T> { }
				public sealed class Node<T> : INode<T>
				{
				    public Node(INode<Node<T>> next) { }
				}

				public sealed class Root
				{
				    public Root(INode<int> node) { }
				}

				[Container]
				[Transient(typeof(Node<>), typeof(INode<>))]
				[Transient<Root>]
				public static partial class MyContainer
				{
				}
				""");

			await That(result.Diagnostics).Contains("*(17,2)*AWT129*").AsWildcard()
				.Because("the diagnostic is reported at the open registration that recurses");
		}
	}
}
