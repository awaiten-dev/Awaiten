namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A private <c>Factory</c> or <c>Instance</c> member of a module: every container calls it through the wrapper the
///     module generates, and a module that cannot receive the wrapper reports it once in its own build.
/// </summary>
public class ModulePrivateMemberTests
{
	private const string ExternalContainer = """
		using Awaiten;
		using Lib;

		namespace MyCode;

		[Container]
		[Import(typeof(CoffeeModule))]
		public static partial class MyContainer;
		""";

	private const string SameAssemblyContainer = """

		[Container]
		[Import(typeof(CoffeeModule))]
		public static partial class SameContainer;
		""";

	private static (GeneratorResult Library, GeneratorResult Same, GeneratorResult External, GeneratorResult Friend) RunAll(string library)
		=> (Generator.Run(library),
			Generator.Run(library + SameAssemblyContainer),
			Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer),
			Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"), ExternalContainer));

	private static string Library(string modifiers, string registration, string member) => $$"""
		using Awaiten;

		namespace Lib;

		public interface IGreeter { }
		public interface IOther { }
		public interface IClock { }
		public sealed class Clock : IClock { }
		internal interface ISecret { }
		internal sealed class Secret : ISecret { }
		internal sealed class Greeter : IGreeter, IOther { }

		[Module]
		[Singleton<Clock, IClock>]
		{{registration}}
		{{modifiers}} class CoffeeModule
		{
			{{member}}
		}
		""";

	[Test]
	[Arguments("[Singleton<IGreeter>(Factory = \"Factory\")]", "private static IGreeter Factory() => new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Factory = nameof(Make))]", "private static IGreeter Make(IClock clock) => new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Instance = nameof(Shared))]", "private static IGreeter Shared { get; } = new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Factory = nameof(Make))]", "private static Greeter Make() => new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Instance = nameof(Shared))]", "private static Greeter Shared { get; } = new Greeter();")]
	public async Task OfAPublicService_IsCalledThroughTheWrapperByEveryContainer(string registration, string member)
	{
		(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("public static partial", registration, member));

		await That(library.Diagnostics).IsEmpty();
		await That(same.Diagnostics).IsEmpty()
			.Because("a container in the module's assembly calls the wrapper of a member it cannot reach");
		await That(external.Diagnostics).IsEmpty()
			.Because("the module wraps a private member of a public service, which no container could call otherwise");
		await That(friend.Diagnostics).IsEmpty()
			.Because("a friend names an internal implementation, so it calls a wrapper that returns it rather than the public service");
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.");
		await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.");
	}

	[Test]
	[Arguments("public static")]
	[Arguments("internal static")]
	public async Task OfANonPartialModule_ReportsAwt194InTheLibraryOnce(string modifiers)
	{
		string library = Library(modifiers, "[Singleton<IGreeter>(Factory = nameof(Make))]", "private static IGreeter Make() => new Greeter();");

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);

		await That(alone.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT194") && diagnostic.Contains("'Lib.CoffeeModule' produces 'Lib.IGreeter' through its private member 'Make'"))
			.Because("no container can call the private member, and the module cannot receive the wrapper that would, so the library reports it before any consumer");
		await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT194"))
			.Because("the container in the module's assembly leaves the one cause to the module instead of adding AWT153");
	}

	[Test]
	[Arguments("[Singleton<Greeter, IGreeter>(Factory = nameof(Make))][Singleton<Greeter, IOther>(Factory = nameof(Make))]", "private static Greeter Make() => new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Factory = nameof(Make))][Singleton<Greeter, IOther>(Factory = nameof(Make))]", "internal static Greeter Make() => new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Instance = nameof(Shared))][Singleton<Greeter, IOther>(Instance = nameof(Shared))]", "private static Greeter Shared { get; } = new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Factory = nameof(Make))][Singleton<Secret, ISecret>]", "private static IGreeter Make(ISecret secret) => new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Factory = nameof(Make))][Singleton<Secret, ISecret>]", "internal static IGreeter Make(ISecret secret) => new Greeter();")]
	public async Task NoPublicWrapperCanServe_IsBuiltThroughAHandleForAContainerInAnotherAssembly(string registration, string member)
	{
		(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("public static partial", registration, member));

		await That(library.Diagnostics).IsEmpty();
		await That(same.Diagnostics).IsEmpty();
		await That(external.Diagnostics).IsEmpty()
			.Because("no public wrapper can return an internal implementation of several services or take an internal parameter, so the module builds it through an instance handle");
		await That(friend.Diagnostics).IsEmpty();
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.Alias_");
	}

	[Test]
	[Arguments("public static partial")]
	[Arguments("internal static partial")]
	public async Task OverloadedPrivateFactory_IsReportedAsAmbiguousOnceInTheLibrary(string modifiers)
	{
		string library = Library(modifiers, "[Singleton<IGreeter>(Factory = nameof(Make))]", "private static IGreeter Make() => new Greeter(); private static IGreeter Make(IClock clock) => new Greeter();");

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);

		await That(alone.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT112") && diagnostic.Contains("more than one accessible method 'Make'"))
			.Because("no wrapper can choose between the private overloads, so the library reports the ambiguity before any consumer does");
		await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT112"))
			.Because("the container in the module's assembly leaves the one cause to the module instead of adding AWT153");
	}

	[Test]
	[Arguments("public static partial", "private static Greeter Make(ref int count) => new Greeter();", "AWT153")]
	[Arguments("public static partial", "private static Greeter Make() => new Greeter(); private static Greeter Make(IClock clock) => new Greeter();", "AWT112")]
	[Arguments("public static", "private static Greeter Make() => new Greeter();", "AWT194")]
	public async Task NoWrapperCanServe_LeavesAConsumingContainerOfTheSameAssemblyWithOnlyItsShell(string modifiers, string member, string id)
	{
		string library = Library(modifiers, "[Singleton<Greeter, IGreeter>(Factory = nameof(Make))]", member) + """

			public sealed class Barista { public Barista(IGreeter greeter) { } }

			[Container]
			[Import(typeof(CoffeeModule))]
			[Transient<Barista>]
			public static partial class SameContainer;
			""";

		GeneratorResult same = Generator.Run(library);

		await That(same.Diagnostics).HasSingle().Which.Contains(id)
			.Because("the module reports why no container can call the member, so the container that needs it leaves the registration out without taking that for a bug of the generator (AWT255)");
		await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("has registration errors");
	}

	/// <summary>
	///     A generic <c>Factory</c> member of an open registration, which the module wraps for no container.
	/// </summary>
	public class OfAnOpenRegistration
	{
		private const string ExternalConsumer = """
			using Awaiten;
			using Lib;

			namespace App;

			public sealed class Order { }
			public sealed class Checkout { public Checkout(IRepository<Order> orders) { } }

			[Container]
			[Import(typeof(DataModule))]
			[Transient<Checkout>]
			public static partial class AppContainer;
			""";

		private const string SameAssemblyConsumer = """

			public sealed class Order { }
			public sealed class Checkout { public Checkout(IRepository<Order> orders) { } }

			[Container]
			[Import(typeof(DataModule))]
			[Transient<Checkout>]
			public static partial class LibContainer;
			""";

		private static string OpenLibrary(string modifiers, string repository, string member) => $$"""
			using Awaiten;

			namespace Lib;

			public interface IRepository<T> { }
			{{repository}} sealed class Repository<T> : IRepository<T> { }

			[Module]
			[Singleton(typeof(IRepository<>), Factory = nameof(Make))]
			{{modifiers}} class DataModule
			{
				{{member}}
			}
			""";

		private static string Granted(string library)
			=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

		[Test]
		[Arguments("public static partial", "public")]
		[Arguments("public static partial", "internal")]
		[Arguments("internal static partial", "internal")]
		[Arguments("public static", "public")]
		public async Task Private_IsReportedOnceInTheLibrary(string modifiers, string repository)
		{
			string library = OpenLibrary(modifiers, repository, "private static Repository<T> Make<T>() => new Repository<T>();");

			GeneratorResult alone = Generator.Run(library);
			GeneratorResult same = Generator.Run(library + SameAssemblyConsumer);

			await That(alone.Diagnostics).HasSingle().Which.StartsWith("(9,2): error AWT153: 'Lib.IRepository<T>' cannot be produced: the member 'Make' exists on the module 'Lib.DataModule' but is not accessible")
				.Because("the module generates no wrapper for a generic member, so no container can call the private one, and the library reports it before any consumer, partial or not");
			await That(same.Diagnostics).HasSingle().Which.StartsWith("(9,2): error AWT153")
				.Because("the container in the module's assembly leaves the one cause to the module instead of taking the missing closing for a bug of the generator (AWT255)");
			await That(same.Sources["Awaiten.Lib.LibContainer.g.cs"]).Contains("has registration errors");
		}

		[Test]
		[Arguments("public")]
		[Arguments("internal")]
		public async Task Private_IsReportedAsMissingInAContainerOfAnotherAssembly(string repository)
		{
			string library = OpenLibrary("public static partial", repository, "private static Repository<T> Make<T>() => new Repository<T>();");

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalConsumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), ExternalConsumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, ExternalConsumer);

			foreach (GeneratorResult result in new[] { external, friend, reference, })
			{
				await That(result.Diagnostics).HasSingle().Which.StartsWith("(10,2): error AWT108: 'Lib.IRepository<App.Order>' cannot be produced: the module 'Lib.DataModule' has no accessible method 'Make'")
					.Because("another assembly does not see a private member, as for a private member the module cannot wrap");
			}
		}

		[Test]
		public async Task PrivateOfAnotherType_IsReportedOnlyAsMissingByTheContainer()
		{
			string library = OpenLibrary("public static partial", "public", "private static System.Collections.Generic.List<T> Make<T>() => new();");

			GeneratorResult alone = Generator.Run(library);
			GeneratorResult same = Generator.Run(library + SameAssemblyConsumer);

			await That(alone.Diagnostics).IsEmpty()
				.Because("a member that produces another type would not serve the registration even if it were accessible, so the module does not name its accessibility as the cause");
			await That(same.Diagnostics).HasSingle().Which.Contains("*AWT108*'Make'*").AsWildcard()
				.Because("the container reports the missing factory once");
		}

		[Test]
		[Arguments("public static partial class DataModule<TModule>", "typeof(DataModule<int>)")]
		[Arguments("public partial class DataModule", "typeof(DataModule)")]
		public async Task PrivateOnAModuleTheContainerReportsItself_IsNotReportedTwice(string declaration, string import)
		{
			string library = OpenLibrary("public static partial", "public", "private static Repository<T> Make<T>() => new Repository<T>();")
				.Replace("public static partial class DataModule", declaration);

			GeneratorResult alone = Generator.Run(library);
			GeneratorResult same = Generator.Run(library + SameAssemblyConsumer.Replace("typeof(DataModule)", import));

			await That(alone.Diagnostics).IsEmpty()
				.Because("a container reports the private member of a generic or non-static module itself, as for a closed registration");
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT153"))
				.Because("the module and the container must not both report the member");
		}

		[Test]
		[Arguments("public")]
		[Arguments("internal")]
		public async Task Internal_IsCalledDirectlyByTheModulesAssemblyAndItsFriends(string repository)
		{
			string library = OpenLibrary("public static partial", repository, "internal static Repository<T> Make<T>() => new Repository<T>();");

			GeneratorResult alone = Generator.Run(library);
			GeneratorResult same = Generator.Run(library + SameAssemblyConsumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), ExternalConsumer);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalConsumer);

			await That(alone.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(same.Sources["Awaiten.Lib.LibContainer.g.cs"]).Contains("global::Lib.DataModule.Make<global::Lib.Order>()");
			await That(friend.Diagnostics).IsEmpty();
			await That(friend.Sources["Awaiten.App.AppContainer.g.cs"]).Contains("global::Lib.DataModule.Make<global::App.Order>()");
			await That(external.Diagnostics).HasSingle().Which.Contains("*AWT108*'Make'*").AsWildcard()
				.Because("the module generates no public wrapper for a generic member");
		}
	}
}
