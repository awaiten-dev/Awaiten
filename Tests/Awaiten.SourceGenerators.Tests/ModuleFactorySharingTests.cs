using System.Collections.Generic;
using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     Registrations of a public implementation that a module of another assembly builds through its generated factory,
///     declared by a module of the container's assembly or of a third library, closed over an open generic registration
///     of the module, or inherited from a base container. Each calls the module's factory, as a container in the
///     module's own assembly constructs one instance, and takes the async initializer conventions of its declarer.
/// </summary>
public class ModuleFactorySharingTests
{
	private const string InternalConstructor = "internal Greeter() { }";
	private const string InternalProperty = "[Inject] internal IClock Clock { get; set; } = null!;";

	private static string Library(string members = InternalConstructor)
		=> $$"""
		     using Awaiten;

		     namespace Lib;

		     public interface IGreeter { }
		     public interface IClock { }
		     public sealed class SystemClock : IClock { }
		     public sealed class Greeter : IGreeter { {{members}} }

		     [Module]
		     [Singleton<SystemClock, IClock>]
		     [Singleton<Greeter, IGreeter>]
		     public static partial class GreetingModule;
		     """;

	private static string Granted(string library)
		=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

	private static string WithoutHeader(string source)
		=> source.Replace("using Awaiten;", string.Empty)
			.Replace("using Lib;", string.Empty)
			.Replace("namespace App;", string.Empty)
			.Replace("namespace LibB;", string.Empty)
			.Replace("namespace Base;", string.Empty)
			.Replace("LibB.", string.Empty)
			.Replace("Base.", string.Empty)
			.Replace("Lib.", string.Empty);

	private static string Container(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("AppContainer")).Value;

	public class ModuleOfTheContainersAssembly
	{
		private static string Consumer(string imports, string registration = "[Singleton<Greeter>]")
			=> $$"""
			     using Awaiten;
			     using Lib;

			     namespace App;

			     [Module]
			     {{registration}}
			     public static partial class LocalModule
			     {
			         internal static void Ready(Greeter greeter) { }
			     }

			     [Container]
			     {{imports}}
			     public static partial class AppContainer;
			     """;

		[Test]
		[Arguments("[Import(typeof(GreetingModule))]\n[Import(typeof(LocalModule))]", "[Singleton<Greeter>]")]
		[Arguments("[Import(typeof(LocalModule))]\n[Import(typeof(GreetingModule))]", "[Singleton<Greeter>(OnActivated = nameof(Ready))]")]
		[Arguments("[Import(typeof(GreetingModule))]\n[Import(typeof(LocalModule))]", "[Singleton<Greeter, IGreeter>(Key = \"local\")]")]
		public async Task ARegistrationOfTheImplementation_CallsTheFactoryOfTheModuleOfItsAssembly(string imports, string registration)
		{
			string consumer = Consumer(imports, registration);

			GeneratorResult same = Generator.Run(Library() + "\n" + WithoutHeader(consumer));
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(Library()), consumer);
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(Library(), consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Library(), consumer);

			foreach (GeneratorResult result in new[] { same, friend, other, reference, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the container builds the implementation through the factory of the module of its assembly, as a container there constructs it");
			}

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
					.And.DoesNotContain("new global::Lib.Greeter(")
					.Because("the local module's registration calls the factory that the module of the implementation's assembly generated");
			}
		}

		[Test]
		public async Task WithoutTheModuleOfTheImplementationsAssembly_TheContainerReportsTheConstructor()
		{
			string consumer = Consumer("[Import(typeof(LocalModule))]");

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(Library(), consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Library(), consumer);

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which.Contains("error AWT104: 'Lib.Greeter' has no accessible constructor for the container to call")
					.Because("nothing the container imports can build the implementation, and the local module leaves the report to the container");
			}
		}

		[Test]
		public async Task TheModuleAlone_ReportsNothing()
		{
			const string module = """
			                      using Awaiten;
			                      using Lib;

			                      namespace App;

			                      [Module]
			                      [Singleton<Greeter>]
			                      public static partial class LocalModule;
			                      """;

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(Library(), module);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Library(), module);

			await That(other.Diagnostics).IsEmpty()
				.Because("a container that imports the module beside a module of the implementation's assembly builds the implementation through that module's factory");
			await That(reference.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments("", InternalConstructor)]
		[Arguments("[Singleton<Greeter, IGreeter>]", "private Greeter() { }")]
		public async Task TheModuleAlone_ReportsTheConstructorThatNoModuleOfTheImplementationsAssemblyBuilds(string registration, string members)
		{
			const string module = """
			                      using Awaiten;
			                      using Lib;

			                      namespace App;

			                      [Module]
			                      [Singleton<Greeter>]
			                      public static partial class LocalModule;
			                      """;
			string library = Library(members).Replace("[Singleton<Greeter, IGreeter>]", registration);

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, module);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, module);

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which.Contains("error AWT104: 'Lib.Greeter' has no accessible constructor for the container to call")
					.Because("no module of the implementation's assembly generates a factory a container could call instead");
			}
		}
	}

	public class ModuleOfAThirdLibrary
	{
		private const string OtherLibrary = """
		                                    using Awaiten;

		                                    namespace LibB;

		                                    [Module]
		                                    [Singleton<Lib.Greeter>]
		                                    public static partial class OtherModule;
		                                    """;

		private static string Consumer(string imports)
			=> $$"""
			     using Awaiten;

			     namespace App;

			     [Container]
			     {{imports}}
			     public static partial class AppContainer;
			     """;

		[Test]
		[Arguments("[Import(typeof(Lib.GreetingModule))]\n[Import(typeof(LibB.OtherModule))]")]
		[Arguments("[Import(typeof(LibB.OtherModule))]\n[Import(typeof(Lib.GreetingModule))]")]
		public async Task ItsRegistration_CallsTheFactoryOfTheModuleOfTheImplementationsAssembly(string imports)
		{
			GeneratorResult same = Generator.Run(Library() + "\n" + WithoutHeader(OtherLibrary) + "\n" + WithoutHeader(Consumer(imports)));
			GeneratorResult chain = Generator.RunWithGeneratedReferencedAssemblyChain([Library(), OtherLibrary,], Consumer(imports));
			GeneratorResult references = Generator.RunWithGeneratedReferencedAssemblyChain([Library(), OtherLibrary,], Consumer(imports), referenceAssemblies: true);

			foreach (GeneratorResult result in new[] { same, chain, references, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("both registrations are one instance, as in one assembly");
			}

			foreach (GeneratorResult result in new[] { chain, references, })
			{
				await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
					.And.DoesNotContain("new global::Lib.Greeter(")
					.Because("the third library's registration calls the factory of the module of the implementation's assembly");
			}
		}

		[Test]
		public async Task TheLibrary_BuildsWithoutAnError()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), OtherLibrary);

			await That(result.Diagnostics).IsEmpty()
				.Because("the library cannot construct the implementation, but a container that imports a module of the implementation's assembly too builds it through that module's factory");
		}

		[Test]
		public async Task TheLibrary_ReportsTheConstructorWhenNoModuleOfTheImplementationsAssemblyBuildsIt()
		{
			string library = Library().Replace("[Singleton<Greeter, IGreeter>]", string.Empty);

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, OtherLibrary);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, OtherLibrary);

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which.Contains("error AWT104: 'Lib.Greeter' has no accessible constructor for the container to call")
					.Because("no container can build the implementation, so the library reports it in its own build");
			}
		}

		[Test]
		public async Task ImportedAlone_ReportsWhyNothingBuildsTheImplementation()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(), OtherLibrary,], Consumer("[Import(typeof(LibB.OtherModule))]"));

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("error AWT231: The imported module 'LibB.OtherModule' registers 'Lib.Greeter'")
			                                                                         && diagnostic.Contains("because no constructor of it is accessible outside 'Library1'"))
				.Because("the library recorded why it generated no factory, and the container imports no module that builds the implementation");
		}
	}

	public class OpenGenericRegistration
	{
		private static string RepositoryLibrary(string registration)
			=> $$"""
			     using Awaiten;

			     namespace Lib;

			     public interface IRepository<T> { }
			     public sealed class Repository<T> : IRepository<T> { internal Repository() { } }

			     [Module]
			     {{registration}}
			     public static partial class RepositoryModule;
			     """;

		private static string Consumer(string registrations)
			=> $$"""
			     using Awaiten;
			     using Lib;

			     namespace App;

			     public sealed class Cafe { public Cafe(IRepository<string> orders, Repository<int> beans) { } }

			     [Container]
			     [Import(typeof(RepositoryModule))]
			     {{registrations}}
			     public static partial class AppContainer;
			     """;

		[Test]
		[Arguments("[Singleton(typeof(Repository<>))]", "[Singleton<Repository<int>>]")]
		[Arguments("[Singleton(typeof(Repository<>), typeof(IRepository<>))]", "[Singleton<Repository<int>>]")]
		[Arguments("[Singleton(typeof(Repository<>), typeof(IRepository<>))]", "[Singleton<Repository<int>>]\n[Singleton<Repository<string>>]\n[Singleton<Cafe>]")]
		[Arguments("[Singleton(typeof(Repository<>))]", "[Singleton<Repository<int>, IRepository<int>>]\n[Singleton<Repository<string>, IRepository<string>>]\n[Singleton<Cafe>]")]
		public async Task AClosedRegistrationOfAClosing_CallsTheModulesGenericFactory(string moduleRegistration, string registrations)
		{
			string library = RepositoryLibrary(moduleRegistration);
			string consumer = Consumer(registrations);

			GeneratorResult same = Generator.Run(library + "\n" + WithoutHeader(consumer));
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), consumer);
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

			foreach (GeneratorResult result in new[] { same, friend, other, reference, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the closed registration and a closing of the module's open registration are one instance, as in the module's own assembly");
			}

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(Container(result)).Contains("global::Lib.RepositoryModule.Internals.Registration_Repository_")
					.And.DoesNotContain("new global::Lib.Repository<int>()")
					.Because("the closed registration calls the module's generic factory, closed with its type arguments");
			}
		}

		[Test]
		public async Task AClosedRegistrationOfAThirdLibrary_CallsTheModulesGenericFactory()
		{
			const string otherLibrary = """
			                            using Awaiten;

			                            namespace LibB;

			                            [Module]
			                            [Singleton<Lib.Repository<int>>]
			                            public static partial class OtherModule;
			                            """;
			const string consumer = """
			                        using Awaiten;

			                        namespace App;

			                        [Container]
			                        [Import(typeof(LibB.OtherModule))]
			                        [Import(typeof(Lib.RepositoryModule))]
			                        public static partial class AppContainer;
			                        """;
			string library = RepositoryLibrary("[Singleton(typeof(Repository<>))]");

			GeneratorResult build = Generator.RunWithGeneratedReferencedAssembly(library, otherLibrary);
			GeneratorResult chain = Generator.RunWithGeneratedReferencedAssemblyChain([library, otherLibrary,], consumer);
			GeneratorResult references = Generator.RunWithGeneratedReferencedAssemblyChain([library, otherLibrary,], consumer, referenceAssemblies: true);

			await That(build.Diagnostics).IsEmpty()
				.Because("a container that imports the module too builds the closing through the module's generic factory");
			foreach (GeneratorResult result in new[] { chain, references, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(Container(result)).Contains("global::Lib.RepositoryModule.Internals.Registration_Repository_")
					.And.DoesNotContain("new global::Lib.Repository<int>()")
					.Because("the third library's closed registration calls the module's generic factory, closed with its type arguments");
			}
		}

		[Test]
		public async Task AClosedRegistrationOfAModuleOfTheContainersAssembly_ReportsTheConstructorWithoutAGenericFactory()
		{
			const string module = """
			                      using Awaiten;
			                      using Lib;

			                      namespace App;

			                      [Module]
			                      [Singleton<Repository<int>>]
			                      public static partial class LocalModule;
			                      """;
			string library = RepositoryLibrary(string.Empty);

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, module);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, module);

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which.Contains("error AWT104: 'Lib.Repository<int>' has no accessible constructor for the container to call")
					.Because("no module of the implementation's assembly generates a factory a container could call instead");
			}
		}

		[Test]
		public async Task AnOpenRegistrationAsItself_IsBuiltThroughTheModulesGenericFactory()
		{
			string library = RepositoryLibrary("[Singleton(typeof(Repository<>))]");
			string consumer = Consumer("[Singleton<Cafe>]").Replace("IRepository<string> orders", "Repository<string> orders");

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the module generates a generic factory for an open registration of the implementation as itself too");
				await That(Container(result)).Contains("global::Lib.RepositoryModule.Internals.Registration_Repository_")
					.And.DoesNotContain("new global::Lib.Repository<int>()");
			}
		}
	}

	public class AsyncInitializerConventions
	{
		private const string ConventionLibrary = """
		                                         using Awaiten;
		                                         using System.Threading;
		                                         using System.Threading.Tasks;

		                                         namespace Lib;

		                                         public interface IGreeter { }
		                                         public interface IWarm { Task WarmAsync(CancellationToken cancellationToken); }
		                                         public interface ICool { Task CoolAsync(CancellationToken cancellationToken); }
		                                         public sealed class Greeter : IGreeter, IWarm, ICool
		                                         {
		                                             MEMBERS
		                                             public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                             public Task CoolAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                         }
		                                         public interface IClock { }
		                                         public sealed class SystemClock : IClock { }

		                                         [Module]
		                                         [AsyncInitializer<IWarm>(nameof(IWarm.WarmAsync))]
		                                         [Singleton<SystemClock, IClock>]
		                                         [Singleton<Greeter, IGreeter>]
		                                         public static partial class GreetingModule;
		                                         """;

		private const string WarmCall = "((global::Lib.IWarm)created).WarmAsync(";
		private const string CoolCall = "((global::Lib.ICool)created).CoolAsync(";

		private static string Strip(string source)
			=> WithoutHeader(source).Replace("using System.Threading;", string.Empty).Replace("using System.Threading.Tasks;", string.Empty);

		[Test]
		[Arguments(InternalConstructor)]
		[Arguments(InternalProperty)]
		public async Task AContainerRegistrationThatComesFirst_TakesTheContainersConventions(string members)
		{
			const string consumer = """
			                        using Awaiten;
			                        using Lib;

			                        namespace App;

			                        [Container]
			                        [AsyncInitializer<ICool>(nameof(ICool.CoolAsync))]
			                        [Import(typeof(GreetingModule))]
			                        [Singleton<Greeter>]
			                        public static partial class AppContainer;
			                        """;
			string library = ConventionLibrary.Replace("MEMBERS", members);

			GeneratorResult same = Generator.Run(library + "\n" + Strip(consumer));
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

			foreach (GeneratorResult result in new[] { same, other, reference, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(Container(result)).Contains(CoolCall).Exactly(1)
					.And.DoesNotContain(WarmCall)
					.Because("the container's registration comes first, so the container's conventions initialize the one instance, as in the module's own assembly");
			}
		}

		[Test]
		public async Task ARegistrationOfAnExtendingContainer_TakesTheConventionsItInheritsToo()
		{
			const string consumer = """
			                        using Awaiten;
			                        using Lib;

			                        namespace App;

			                        [Container]
			                        [AsyncInitializer<ICool>(nameof(ICool.CoolAsync))]
			                        public static partial class BaseContainer;

			                        [Container(Extends = typeof(BaseContainer))]
			                        [Import(typeof(GreetingModule))]
			                        [Singleton<Greeter>]
			                        public static partial class AppContainer;
			                        """;
			string library = ConventionLibrary.Replace("MEMBERS", InternalConstructor);

			GeneratorResult same = Generator.Run(library + "\n" + Strip(consumer));
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			foreach (GeneratorResult result in new[] { same, other, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(Container(result)).Contains(CoolCall).Exactly(1)
					.And.DoesNotContain(WarmCall)
					.Because("the extending container's own registration comes first, so the conventions it inherits from its base initialize the one instance");
			}
		}

		[Test]
		[Arguments(InternalConstructor)]
		[Arguments(InternalProperty)]
		public async Task ARegistrationOfAThirdLibraryThatComesFirst_TakesTheConventionsOfItsModule(string members)
		{
			const string otherLibrary = """
			                            using Awaiten;

			                            namespace LibB;

			                            [Module]
			                            [AsyncInitializer<Lib.ICool>(nameof(Lib.ICool.CoolAsync))]
			                            [Singleton<Lib.Greeter>]
			                            public static partial class OtherModule;
			                            """;
			const string consumer = """
			                        using Awaiten;

			                        namespace App;

			                        [Container]
			                        [Import(typeof(LibB.OtherModule))]
			                        [Import(typeof(Lib.GreetingModule))]
			                        public static partial class AppContainer;
			                        """;
			string library = ConventionLibrary.Replace("MEMBERS", members);

			GeneratorResult same = Generator.Run(library + "\n" + Strip(otherLibrary) + "\n" + Strip(consumer));
			GeneratorResult chain = Generator.RunWithGeneratedReferencedAssemblyChain([library, otherLibrary,], consumer);

			foreach (GeneratorResult result in new[] { same, chain, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(Container(result)).Contains(CoolCall).Exactly(1)
					.And.DoesNotContain(WarmCall)
					.Because("the other module's registration comes first, so its conventions initialize the one instance");
			}

			await That(Container(chain)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_");
		}

		[Test]
		public async Task AClosedRegistrationOfAClosing_TakesTheContainersConventions()
		{
			const string library = """
			                       using Awaiten;
			                       using System.Threading;
			                       using System.Threading.Tasks;

			                       namespace Lib;

			                       public interface IWarm { Task WarmAsync(CancellationToken cancellationToken); }
			                       public interface ICool { Task CoolAsync(CancellationToken cancellationToken); }
			                       public sealed class Repository<T> : IWarm, ICool
			                       {
			                           internal Repository() { }
			                           public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                           public Task CoolAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                       }

			                       [Module]
			                       [AsyncInitializer<IWarm>(nameof(IWarm.WarmAsync))]
			                       [Singleton(typeof(Repository<>))]
			                       public static partial class RepositoryModule;
			                       """;
			const string consumer = """
			                        using Awaiten;
			                        using Lib;

			                        namespace App;

			                        [Container]
			                        [AsyncInitializer<ICool>(nameof(ICool.CoolAsync))]
			                        [Import(typeof(RepositoryModule))]
			                        [Singleton<Repository<int>>]
			                        public static partial class AppContainer;
			                        """;

			GeneratorResult same = Generator.Run(library + "\n" + Strip(consumer));
			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			foreach (GeneratorResult result in new[] { same, other, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(Container(result)).Contains(CoolCall).Exactly(1)
					.And.DoesNotContain(WarmCall)
					.Because("the container declares the closed registration, so its conventions initialize the instance the module's generic factory builds");
			}
		}
	}

	public class InheritedRegistration
	{
		private const string BaseContainer = """
		                                     using Awaiten;
		                                     using Lib;

		                                     namespace Base;

		                                     [Container]
		                                     [Import(typeof(GreetingModule))]
		                                     [Singleton<Greeter>]
		                                     public static partial class BaseContainer;
		                                     """;

		private const string Extending = """
		                                 using Awaiten;

		                                 namespace App;

		                                 [Container(Extends = typeof(Base.BaseContainer))]
		                                 public static partial class AppContainer;
		                                 """;

		[Test]
		[Arguments(InternalConstructor)]
		[Arguments(InternalProperty)]
		public async Task OfABaseInItsOwnAssembly_CallsTheModulesFactory(string members)
		{
			GeneratorResult chain = Generator.RunWithGeneratedReferencedAssemblyChain([Library(members), BaseContainer,], Extending);
			GeneratorResult references = Generator.RunWithGeneratedReferencedAssemblyChain([Library(members), BaseContainer,], Extending, referenceAssemblies: true);

			foreach (GeneratorResult result in new[] { chain, references, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the inherited registration and the inherited module's are one instance, which the module's factory builds");
				await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
					.And.DoesNotContain("new global::Lib.Greeter(");
			}
		}

		[Test]
		[Arguments(InternalConstructor)]
		[Arguments(InternalProperty)]
		public async Task OfABaseInTheModulesAssembly_CallsTheModulesFactory(string members)
		{
			string library = Library(members) + "\n" + WithoutHeader(BaseContainer);
			string extending = Extending.Replace("Base.BaseContainer", "Lib.BaseContainer");

			GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, extending);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, extending);

			foreach (GeneratorResult result in new[] { other, reference, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the inherited registration and the inherited module's are one instance, which the module's factory builds");
				await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
					.And.DoesNotContain("new global::Lib.Greeter(");
			}
		}

		[Test]
		public async Task OfAClosingOfTheModulesOpenRegistration_CallsTheModulesGenericFactory()
		{
			const string library = """
			                       using Awaiten;

			                       namespace Lib;

			                       public sealed class Repository<T> { internal Repository() { } }

			                       [Module]
			                       [Singleton(typeof(Repository<>))]
			                       public static partial class RepositoryModule;
			                       """;
			const string baseContainer = """
			                             using Awaiten;
			                             using Lib;

			                             namespace Base;

			                             [Container]
			                             [Import(typeof(RepositoryModule))]
			                             [Singleton<Repository<int>>]
			                             public static partial class BaseContainer;
			                             """;

			GeneratorResult chain = Generator.RunWithGeneratedReferencedAssemblyChain([library, baseContainer,], Extending);
			GeneratorResult references = Generator.RunWithGeneratedReferencedAssemblyChain([library, baseContainer,], Extending, referenceAssemblies: true);
			GeneratorResult baseInLibrary = Generator.RunWithGeneratedReferencedAssembly(library + "\n" + WithoutHeader(baseContainer), Extending.Replace("Base.BaseContainer", "Lib.BaseContainer"));

			foreach (GeneratorResult result in new[] { chain, references, baseInLibrary, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the inherited closed registration calls the module's generic factory instead of the constructor it cannot reach");
				await That(Container(result)).Contains("global::Lib.RepositoryModule.Internals.Registration_Repository_")
					.And.DoesNotContain("new global::Lib.Repository<int>()");
			}
		}
	}
}
