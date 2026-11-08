using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A nullable-enabled consumer whose dependency carries a nullable annotation that the registration does not
///     (<c>IRepo&lt;string?&gt;</c> against <c>IRepo&lt;&gt;</c>, <c>Task&lt;IStore?[]&gt;</c>). The registration is
///     found without the annotation, and the generated value must still convert to the declared type without a
///     nullability warning (CS8619, CS8620), which breaks a build with warnings as errors.
/// </summary>
public class NullableAnnotationTests
{
	private static string Source(string lifetime, string parameter, bool asyncInitialized, string members = "", string registrations = "")
		=> $$"""
		     #nullable enable
		     using Awaiten;
		     using System;
		     using System.Collections.Generic;
		     using System.Threading;
		     using System.Threading.Tasks;

		     namespace MyCode;

		     public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }
		     public interface IRepo<T> { }
		     public sealed class Repo<T> : IRepo<T>{{(asyncInitialized ? ", IInitializable" : "")}}
		     {
		         public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		     }
		     public interface IStore { }
		     public sealed class Store : IStore{{(asyncInitialized ? ", IInitializable" : "")}}
		     {
		         public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		     }
		     public sealed class Consumer
		     {
		         public Consumer({{parameter}} dependency) { }
		     }

		     [Container]
		     [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
		     [{{lifetime}}(typeof(Repo<>), typeof(IRepo<>))]
		     [{{lifetime}}<Store, IStore>]
		     [{{lifetime}}<Store, IStore>(Key = "main")]
		     {{registrations}}
		     [Transient<Consumer>]
		     public static partial class MyContainer
		     {
		     {{members}}
		     }
		     """;

	[Test]
	[Arguments("Singleton", "IRepo<string?>")]
	[Arguments("Scoped", "IRepo<string?>")]
	[Arguments("Transient", "IRepo<string?>")]
	[Arguments("Scoped", "IRepo<string?>?")]
	[Arguments("Scoped", "Func<IRepo<string?>>")]
	[Arguments("Scoped", "Lazy<IRepo<string?>>")]
	[Arguments("Singleton", "Owned<IRepo<string?>>")]
	[Arguments("Scoped", "Owned<IRepo<string?>>")]
	[Arguments("Scoped", "Func<Owned<IRepo<string?>>>")]
	[Arguments("Singleton", "Task<IRepo<string?>>")]
	[Arguments("Scoped", "Task<IRepo<string?>>")]
	[Arguments("Transient", "Task<IRepo<string?>>")]
	[Arguments("Scoped", "Func<Task<IRepo<string?>>>")]
	[Arguments("Scoped", "Lazy<Task<IRepo<string?>>>")]
	[Arguments("Singleton", "Task<Owned<IRepo<string?>>>")]
	[Arguments("Transient", "Func<Task<Owned<IRepo<string?>>>>")]
	[Arguments("Scoped", "IEnumerable<IRepo<string?>>")]
	[Arguments("Scoped", "IRepo<string?>[]")]
	[Arguments("Scoped", "IAsyncEnumerable<IRepo<string?>>")]
	[Arguments("Scoped", "IStore?[]")]
	[Arguments("Singleton", "Task<IEnumerable<IStore?>>")]
	[Arguments("Scoped", "Task<IStore?[]>")]
	[Arguments("Transient", "Task<IReadOnlyList<IStore?>>")]
	[Arguments("Scoped", "Task<IEnumerable<IRepo<string?>>>")]
	[Arguments("Singleton", "IReadOnlyDictionary<string, IStore?>")]
	[Arguments("Scoped", "IReadOnlyDictionary<string, IStore?>")]
	[Arguments("Scoped", "Task<IReadOnlyDictionary<string, IStore?>>")]
	[Arguments("Scoped", "Task<IStore?>")]
	[Arguments("Scoped", "Lazy<IStore?>")]
	[Arguments("Scoped", "Owned<IStore?>")]
	public async Task SynchronousImplementation_CompilesWithoutWarnings(string lifetime, string parameter)
	{
		GeneratorResult result = Generator.Run(Source(lifetime, parameter, asyncInitialized: false));

		await That(result.Diagnostics).IsEmpty()
			.Because("the generated value converts to the declared type even though its nullable annotations differ from the registration");
	}

	[Test]
	[Arguments("Singleton", "IRepo<string?>")]
	[Arguments("Scoped", "IRepo<string?>")]
	[Arguments("Transient", "IRepo<string?>")]
	[Arguments("Singleton", "Task<IRepo<string?>>")]
	[Arguments("Scoped", "Task<IRepo<string?>>")]
	[Arguments("Transient", "Task<IRepo<string?>>")]
	[Arguments("Scoped", "Func<Task<IRepo<string?>>>")]
	[Arguments("Singleton", "Lazy<Task<IRepo<string?>>>")]
	[Arguments("Singleton", "Task<Owned<IRepo<string?>>>")]
	[Arguments("Scoped", "Task<Owned<IRepo<string?>>>")]
	[Arguments("Transient", "Func<Task<Owned<IRepo<string?>>>>")]
	[Arguments("Scoped", "IAsyncEnumerable<IRepo<string?>>")]
	[Arguments("Singleton", "Task<IEnumerable<IStore?>>")]
	[Arguments("Scoped", "Task<IStore?[]>")]
	[Arguments("Transient", "Task<IReadOnlyList<IStore?>>")]
	[Arguments("Singleton", "Task<IReadOnlyDictionary<string, IStore?>>")]
	[Arguments("Scoped", "Task<IReadOnlyDictionary<string, IStore?>>")]
	[Arguments("Scoped", "Task<IStore?>")]
	public async Task AsyncInitializedImplementation_CompilesWithoutWarnings(string lifetime, string parameter)
	{
		GeneratorResult result = Generator.Run(Source(lifetime, parameter, asyncInitialized: true));

		await That(result.Diagnostics).IsEmpty()
			.Because("the awaited value converts to the declared type even though its nullable annotations differ from the registration");
	}

	[Test]
	public async Task KeyedClosedRegistration_CompilesWithoutWarnings()
	{
		GeneratorResult result = Generator.Run(Source("Scoped", "[FromKey(\"main\")] IRepo<string?>", asyncInitialized: false,
			registrations: "[Scoped<Repo<string>, IRepo<string>>(Key = \"main\")]"));

		await That(result.Diagnostics).IsEmpty()
			.Because("a closed registration, which cannot spell a nullable annotation in its attribute, is found without it");
	}

	[Test]
	public async Task NoNestedAnnotation_EmitsNoNullForgivingOperator()
	{
		GeneratorResult result = Generator.Run(Source("Scoped", "IRepo<string>", asyncInitialized: false));

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain(")!")
			.Because("a dependency whose declared type matches the registration needs no conversion");
	}

	[Test]
	public async Task NestedAnnotation_IsForgivenOnTheWholeValue()
	{
		GeneratorResult result = Generator.Run(Source("Scoped", "IRepo<string?>", asyncInitialized: false));

		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"])
			.Contains("new global::MyCode.Consumer((ResolveRepo(__s))!)")
			.Because("nullable annotations do not exist at run time, so the value converts as is");
	}

	public sealed class Producers
	{
		[Test]
		[Arguments("Repo<string?>", "Repo<string>, IRepo<string>")]
		[Arguments("IRepo<string?>", "IRepo<string>")]
		public async Task Factory_ReturningANestedAnnotation_CompilesWithoutWarnings(string returnType, string registration)
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "IRepo<string>", asyncInitialized: false,
				members: $"private static {returnType} Create() => new Repo<string?>();")
				.Replace("[Scoped(typeof(Repo<>), typeof(IRepo<>))]", $"[Scoped<{registration}>(Factory = nameof(Create))]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments("IRepo<string>", "Repo<string?>? Create() => new Repo<string?>()", "Repo<string>, IRepo<string>")]
		[Arguments("IRepo<string>", "IRepo<string?>? Create() => new Repo<string?>()", "IRepo<string>")]
		[Arguments("Task<IRepo<string>>", "Task<Repo<string?>?> Create() => Task.FromResult<Repo<string?>?>(new Repo<string?>())", "Repo<string>, IRepo<string>")]
		public async Task Factory_ReturningANullableNestedAnnotation_ReportsOnlyAwt242(string parameter, string factory, string registration)
		{
			GeneratorResult result = Generator.Run(Source("Scoped", parameter, asyncInitialized: false,
				members: $"private static {factory};")
				.Replace("[Scoped(typeof(Repo<>), typeof(IRepo<>))]", $"[Scoped<{registration}>(Factory = nameof(Create))]"));

			await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT242*").AsWildcard()
				.Because("the possible null is reported once at the registration, and the nested annotation, which the registration cannot spell, is forgiven like on a non-nullable factory");
		}

		[Test]
		public async Task AsyncFactory_ReturningANestedAnnotation_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "Task<IRepo<string>>", asyncInitialized: false,
				members: "private static Task<Repo<string?>> Create() => Task.FromResult(new Repo<string?>());")
				.Replace("[Scoped(typeof(Repo<>), typeof(IRepo<>))]", "[Scoped<Repo<string>, IRepo<string>>(Factory = nameof(Create))]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task Instance_OfANullableNestedAnnotation_ReportsOnlyAwt242()
		{
			GeneratorResult result = Generator.Run(Source("Singleton", "IRepo<string>", asyncInitialized: false,
					members: "private static Repo<string?>? Repo { get; } = new Repo<string?>();")
				.Replace("[Singleton(typeof(Repo<>), typeof(IRepo<>))]", "[Singleton<Repo<string>, IRepo<string>>(Instance = nameof(Repo))]"));

			await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT242*").AsWildcard()
				.Because("an Instance that may be null is reported once at the registration");
		}

		[Test]
		public async Task Instance_OfANestedAnnotation_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Singleton", "IRepo<string>", asyncInitialized: false,
				members: "private static Repo<string?> Repo { get; } = new Repo<string?>();")
				.Replace("[Singleton(typeof(Repo<>), typeof(IRepo<>))]", "[Singleton<Repo<string>, IRepo<string>>(Instance = nameof(Repo))]"));

			await That(result.Diagnostics).IsEmpty();
		}
	}

	public sealed class Modules
	{
		[Test]
		public async Task SelfCompiledScanInAnotherAssembly_CompilesWithoutWarnings()
		{
			const string module = """
				#nullable enable
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				public interface IRepo<T> { }
				public sealed class Repo<T> : IRepo<T> { }
				public interface IPlugin { }
				public interface IRoaster { }

				internal sealed class Roaster : IPlugin, IRoaster
				{
				    public Roaster(IRepo<string?> repo, Task<IRepo<string?>> repoTask) { }
				}

				[Module]
				[Scan<IPlugin>(As = ScanAs.MatchingInterface)]
				public static partial class PluginModule { }
				""";

			GeneratorResult moduleResult = Generator.Run(module);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(module, """
				#nullable enable
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(PluginModule))]
				[Singleton(typeof(Repo<>), typeof(IRepo<>))]
				public static partial class MyContainer
				{
				}
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("the module's factory keeps the annotations of the constructor, and the container forgives its values");
			await That(moduleResult.Diagnostics).IsEmpty()
				.Because("the module's own generated factory compiles without warnings, which the referenced compilation does not report");
		}

		[Test]
		public async Task FactoryOfAModule_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run("""
				#nullable enable
				using Awaiten;

				namespace MyCode;

				public interface IRepo<T> { }
				public sealed class Repo<T> : IRepo<T> { }
				public sealed class Consumer { public Consumer(IRepo<string?> repo) { } }

				[Module]
				[Singleton<Repo<string>, IRepo<string>>(Factory = nameof(Create))]
				public static partial class RepoModule
				{
				    internal static Repo<string?> Create() => new Repo<string?>();
				}

				[Container]
				[Import(typeof(RepoModule))]
				[Transient<Consumer>]
				public static partial class MyContainer
				{
				}
				""");

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task PrivateFactoryOfAModuleForAnInternalService_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run("""
				#nullable enable
				using Awaiten;

				namespace MyCode;

				internal interface IRepo<T> { }
				internal sealed class Repo<T> : IRepo<T> { }
				internal sealed class Consumer { public Consumer(IRepo<string?> repo) { } }

				[Module]
				[Singleton<Repo<string>, IRepo<string>>(Factory = nameof(Create))]
				public static partial class RepoModule
				{
				    private static Repo<string?> Create() => new Repo<string?>();
				}

				[Container]
				[Import(typeof(RepoModule))]
				[Transient<Consumer>]
				internal static partial class MyContainer
				{
				}
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("the module's wrapper of the private factory keeps its return type, whose value the container forgives");
		}
	}

	public sealed class Consumers
	{
		private static string WidgetSource(string parameter, string argType, bool asyncInitialized = false)
			=> Source("Scoped", parameter, asyncInitialized: false, registrations: "[Transient<Widget>]") + $$"""

				public sealed class Widget{{(asyncInitialized ? " : IInitializable" : "")}}
				{
				    public Widget([Arg] {{argType}} value) { }
				    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
				}
				""";

		[Test]
		public async Task FactoryParameter_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "IRepo<string>", asyncInitialized: false,
				members: "private static Consumer Create(IRepo<string?> repo, Task<IStore?[]> stores) => new Consumer(new Repo<string>());")
				.Replace("[Transient<Consumer>]", "[Transient<Consumer>(Factory = nameof(Create))]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task Decorator_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "IRepo<string>", asyncInitialized: false,
				registrations: "[Decorate(typeof(LoggingRepo<>), typeof(IRepo<>))]") + """

				public sealed class LoggingRepo<T> : IRepo<T>
				{
				    public LoggingRepo(IRepo<T?> inner) { }
				}
				""");

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task Composite_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "IRepo<string>", asyncInitialized: false,
				registrations: "[Composite<CompositeRepo, IRepo<string>>]") + """

				public sealed class CompositeRepo : IRepo<string>
				{
				    public CompositeRepo(IReadOnlyList<IRepo<string?>> repos) { }
				}
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("the members convert to the invariant IRepo<string?> although the registration spells IRepo<string>");
		}

		[Test]
		public async Task Hook_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "IStore", asyncInitialized: false,
					members: "private static void Activate(Consumer consumer, IRepo<string?> repo, Task<IReadOnlyDictionary<string, IStore?>> stores) { }")
				.Replace("[Transient<Consumer>]", "[Transient<Consumer>(OnActivated = nameof(Activate))]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task InjectedProperty_CompilesWithoutWarnings(bool deferred)
		{
			GeneratorResult result = Generator.Run((Source("Scoped", "IStore", asyncInitialized: false) + $$"""

				public sealed class Holder
				{
				    [Inject{{(deferred ? "(Deferred = true)" : "")}}]
				    public IRepo<string?> Repo { get; set; } = null!;

				    [Inject]
				    public Lazy<IRepo<string?>> LazyRepo { get; set; } = null!;
				}
				""").Replace("[Transient<Consumer>]", "[Transient<Consumer>]\n[Transient<Holder>]"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task FuncWithRuntimeArguments_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "Func<string?, IRepo<string?>, Widget<string?>>", asyncInitialized: false,
				registrations: "[Transient(typeof(Widget<>))]") + """

				public sealed class Widget<T>
				{
				    public Widget([Arg] string? name, [Arg] IRepo<string?> other, IRepo<T> repo) { }
				}
				""");

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments("Func<string?, Widget>", "string")]
		[Arguments("Func<string?, Owned<Widget>>", "string")]
		[Arguments("Func<string?, Task<Widget>>", "string")]
		[Arguments("Func<string?, Task<Owned<Widget>>>", "string")]
		[Arguments("Func<List<string?>?, Widget>", "List<string?>")]
		[Arguments("Func<List<string>, Widget>", "List<string?>")]
		[Arguments("Func<List<string?>, Widget>", "List<string>")]
		public async Task FuncArgumentThatTheArgDoesNotAccept_Warns(string parameter, string argType)
		{
			GeneratorResult result = Generator.Run(WidgetSource(parameter, argType));

			await That(result.Diagnostics).Contains("*warning CS86*").AsWildcard()
				.Because("the consumer may pass a value the [Arg] parameter does not accept, which the compiler reports like a direct call");
		}

		[Test]
		[Arguments("Func<string?, Widget>", "string?")]
		[Arguments("Func<string, Widget>", "string?")]
		[Arguments("Func<string?, Owned<Widget>>", "string?")]
		[Arguments("Func<string?, Task<Widget>>", "string?")]
		[Arguments("Func<string?, Task<Owned<Widget>>>", "string?")]
		[Arguments("Func<List<string?>, Widget>", "List<string?>")]
		[Arguments("Func<List<string?>?, Widget>", "List<string?>?")]
		public async Task FuncArgumentThatTheArgAccepts_CompilesWithoutWarnings(string parameter, string argType)
		{
			GeneratorResult result = Generator.Run(WidgetSource(parameter, argType));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task FuncArgumentIntoAnAsyncInitializedService_IsCheckedAgainstTheArg(bool nullableArg)
		{
			GeneratorResult result = Generator.Run(WidgetSource("Func<string?, Task<Widget>>", nullableArg ? "string?" : "string", asyncInitialized: true));

			await That(result.Diagnostics.Any(diagnostic => diagnostic.Contains("warning CS86"))).IsEqualTo(!nullableArg)
				.Because("the async resolver takes the runtime argument as the [Arg] parameter declares it");
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task FuncArgumentIntoAFactoryArg_IsCheckedAgainstTheArg(bool nullableArg)
		{
			GeneratorResult result = Generator.Run(Source("Scoped", "Func<string?, Widget>", asyncInitialized: false,
				registrations: "[Transient<Widget>(Factory = nameof(CreateWidget))]",
				members: $"private static Widget CreateWidget([Arg] {(nullableArg ? "string?" : "string")} value) => new Widget();") + """

				public sealed class Widget;
				""");

			await That(result.Diagnostics.Any(diagnostic => diagnostic.Contains("warning CS86"))).IsEqualTo(!nullableArg)
				.Because("a factory's [Arg] parameter is checked against the Func argument like a constructor's");
		}

		[Test]
		[Arguments("Func<string?, Gadget<string?>>")]
		[Arguments("Func<string, Gadget<string?>>")]
		[Arguments("Func<string?, Gadget<string>>")]
		public async Task FuncArgumentIntoAnArgOfATypeParameter_CompilesWithoutWarnings(string parameter)
		{
			GeneratorResult result = Generator.Run(Source("Scoped", parameter, asyncInitialized: false,
				registrations: "[Transient(typeof(Gadget<>))]\n[Transient<OtherConsumer>]") + """

				public sealed class Gadget<T>
				{
				    public Gadget([Arg] T value) { }
				}
				public sealed class OtherConsumer
				{
				    public OtherConsumer(Func<string, Gadget<string>> factory) { }
				}
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("every consumer shares the closing the generated code spells without annotations, so an [Arg] of a type parameter forgives the argument rather than check it against one consumer's annotations");
		}

		[Test]
		public async Task FuncArgument_IsNotForgiven()
		{
			GeneratorResult result = Generator.Run(WidgetSource("Func<string?, Widget>", "string?"));

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"])
				.Contains("new global::System.Func<string?, global::MyCode.Widget>(").And.DoesNotContain(")!")
				.Because("the generated factory spells the argument types like the consumer, so the factory needs no conversion");
		}

		[Test]
		public async Task ExternalDependency_CompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run((Source("Scoped", "IStore", asyncInitialized: false,
				registrations: "[ImportService<IFeed<string>>]") + """

				public interface IFeed<T> { }
				public sealed class Reader
				{
				    public Reader(IFeed<string?> feed) { }
				}
				""").Replace("[Transient<Consumer>]", "[Transient<Consumer>]\n[Transient<Reader>]"));

			await That(result.Diagnostics).IsEmpty();
		}
	}
}
