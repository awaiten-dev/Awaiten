using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt106FactoryHidesAsyncInitialization
	{
		[Test]
		public async Task ReportsWhenTheFactoryConstructsAnAsyncInitializableConcreteType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create() => new FooImpl();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT106*").AsWildcard()
				.Because("the factory provably constructs a type a convention initializes, which its declared return type hides");

			string diagnostic = result.Diagnostics.Single(d => d.Contains("AWT106"));
			await That(diagnostic).Contains("MyCode.FooImpl")
				.Because("the message names the concrete type the author should return");
			await That(diagnostic).Contains("MyCode.IFoo")
				.Because("the message names the declared return type that hides the capability");
			await That(diagnostic).Contains("async-initialized")
				.Because("the message identifies the hidden capability as asynchronous initialization");
		}

		[Test]
		public async Task ReportsWhenTheBodyReturnsALocalOfTheConcreteType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create()
			                                           {
			                                               FooImpl foo = new FooImpl();
			                                               return foo;
			                                           }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT106*").AsWildcard()
				.Because("the returned local's inferred type is the hidden concrete type");
		}

		[Test]
		public async Task ReportsASingleDiagnostic_WhenSeveralReturnsYieldTheSameHiddenType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create()
			                                           {
			                                               FooImpl foo = new FooImpl();
			                                               if (foo.ToString().Length > 100)
			                                               {
			                                                   return new FooImpl();
			                                               }

			                                               return foo;
			                                           }
			                                       }
			                                       """);

			await That(result.Diagnostics.Count(d => d.Contains("AWT106"))).IsEqualTo(1)
				.Because("several returns of the same hidden concrete type surface one diagnostic, not one per return");
		}

		[Test]
		public async Task DoesNotReport_WhenTheDeclaredReturnTypeAlreadyExposesTheCapability()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public sealed class FooImpl : IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<FooImpl>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static FooImpl Create() => new FooImpl();
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_WhenTheDeclaredInterfaceExtendsTheConventionType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFooAsync : IInitializable { }

			                                       public sealed class FooImpl : IFooAsync
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFooAsync>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFooAsync Create() => new FooImpl();
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_WhenTheBodyReturnsANonSpecialType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo { }

			                                       [Container]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create() => new FooImpl();
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_WhenTheConcreteTypeIsNotStaticallyProvableFromTheBody()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Build() => new FooImpl();
			                                           private static IFoo Create() => Build();
			                                       }
			                                       """);

			// The concrete FooImpl is behind a helper call typed IFoo, not provable without interprocedural analysis, so the lint stays silent.
			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_ForAConstructedRegistrationWithoutAFactory()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public sealed class FooImpl : IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<FooImpl>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			// No factory: the container constructs the type directly and already sees its capability.
			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_WhenANestedLambdaReturnsTheConcreteType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create()
			                                           {
			                                               Func<FooImpl> make = () => new FooImpl();
			                                               IFoo result = make();
			                                               return result;
			                                           }
			                                       }
			                                       """);

			// The 'new FooImpl()' is in the nested lambda, not the factory's return; the factory returns an IFoo-typed local, so nothing is provable.
			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_ForAnAsyncTaskFactory_WhichOwnsItsInitialization()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static async Task<IFoo> Create()
			                                           {
			                                               FooImpl foo = new FooImpl();
			                                               await foo.InitializeAsync(default);
			                                               return foo;
			                                           }
			                                       }
			                                       """);

			// An async Task<T> factory is the manual-initialization escape hatch: the container awaits it and does not drive InitializeAsync, so the lint stays silent.
			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_ForAnAsyncValueTaskFactory_WhichOwnsItsInitialization()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static async ValueTask<IFoo> Create()
			                                           {
			                                               FooImpl foo = new FooImpl();
			                                               await foo.InitializeAsync(default);
			                                               return foo;
			                                           }
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReport_ForAHiddenDisposable_SinceTheContainerDisposesFactoryOutputs()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IDisposable
			                                       {
			                                           public void Dispose() { }
			                                       }

			                                       [Container]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create() => new FooImpl();
			                                       }
			                                       """);

			// A hidden IDisposable is not reported: the container disposes factory outputs behind a runtime `is IDisposable` check, so there is no leak.
			await That(result.Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
		}

		[Test]
		public async Task ReportsAsyncInitialization_WhenTheHiddenTypeIsAlsoDisposable()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

			                                       public interface IFoo { }

			                                       public sealed class FooImpl : IFoo, IInitializable, IDisposable
			                                       {
			                                           public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                           public void Dispose() { }
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<IFoo>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IFoo Create() => new FooImpl();
			                                       }
			                                       """);

			string diagnostic = result.Diagnostics.Single(d => d.Contains("AWT106"));
			await That(diagnostic).Contains("async-initialized")
				.Because("async initialization - not disposal - is the unfixable capability the lint reports");
		}

		public class InAModule
		{
			private const string Container = """
				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""";

			private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

			private static string Library(string registration, string factory, bool grant = false) => $$"""
				using System.Threading.Tasks;
				using Awaiten;

				{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

				namespace Lib;

				public interface IInitializable { Task InitializeAsync(); }
				public interface IFoo { }
				public interface IRepo<T> { }
				public sealed class FooImpl : IFoo, IInitializable { public Task InitializeAsync() => Task.CompletedTask; }
				public sealed class Repo<T> : IRepo<T>, IInitializable { public Task InitializeAsync() => Task.CompletedTask; }

				[Module]
				[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
				[{{registration}}]
				public static partial class CoffeeModule
				{
				    {{factory}}
				}
				""";

			[Test]
			[Arguments("Singleton<IFoo>(Factory = nameof(Create))", "public static IFoo Create() => new FooImpl();")]
			[Arguments("Singleton<IFoo>(Factory = nameof(Create))", "internal static IFoo Create() => new FooImpl();")]
			[Arguments("Singleton(typeof(IRepo<>), Factory = nameof(Create))", "public static IRepo<T> Create<T>() => new Repo<T>();")]
			public async Task IsReportedOnceInTheModulesOwnBuild(string registration, string factory)
			{
				await That(Generator.Run(Library(registration, factory)).Diagnostics).HasSingle().Which.Contains("*AWT106*'Create'*").AsWildcard()
					.Because("a container in another assembly sees no body, so the module checks its own factories");
				await That(Generator.Run(Library(registration, factory, grant: true)).Diagnostics).HasSingle().Which.Contains("*AWT106*").AsWildcard();
				await That(Generator.Run(Library(registration, factory) + Container).Diagnostics).HasSingle().Which.Contains("*AWT106*").AsWildcard()
					.Because("the container in the module's assembly leaves it to the module, so it is reported once");
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(registration, factory), Consumer).Diagnostics).DoesNotContain("*AWT106*").AsWildcard();
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(registration, factory, grant: true), Consumer).Diagnostics).DoesNotContain("*AWT106*").AsWildcard()
					.Because("every container agrees: the module's build is the one that reports it");
			}
		}
	}
}
