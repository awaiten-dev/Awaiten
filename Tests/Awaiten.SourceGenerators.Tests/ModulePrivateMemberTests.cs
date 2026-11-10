using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

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

	/// <summary>
	///     A module error about a private member no container can call that does not fail the build, because it is lowered
	///     in an <c>.editorconfig</c>, through <c>NoWarn</c> or disabled with a <c>#pragma</c>: a container of the module's
	///     assembly leaves the registration out, as if it were never declared.
	/// </summary>
	public class WhenTheModulesErrorDoesNotFailTheBuild
	{
		private const string ClosedRegistration = "[Singleton<Greeter, IGreeter>(Factory = nameof(Make))]";
		private const string OpenRegistration = "[Singleton(typeof(IRepository<>), Factory = nameof(Make))]";

		private static string Library(string modifiers, string registration, string member) => $$"""
			using Awaiten;

			namespace Lib;

			public interface IGreeter { }
			public sealed class Greeter : IGreeter { }
			public interface IClock { }
			public sealed class Clock : IClock { }
			public interface IRepository<T> { }
			public sealed class Repository<T> : IRepository<T> { }
			public sealed class Order { }

			[Module]
			[Singleton<Clock, IClock>]
			{{registration}}
			{{modifiers}} class CoffeeModule
			{
				{{member}}
			}
			""";

		private static string Container(string registration) => $$"""

			public sealed class Barista { public Barista(IGreeter greeter) { } }
			public sealed class Checkout { public Checkout(IRepository<Order> orders) { } }
			public sealed class Till { public Till(IClock clock) { } }

			[Container]
			[Import(typeof(CoffeeModule))]
			{{registration}}
			public static partial class SameContainer;
			""";

		private const string FriendContainer = """
			using Awaiten;
			using Lib;

			namespace App;

			public sealed class Barista { public Barista(IGreeter greeter) { } }
			public sealed class Checkout { public Checkout(IRepository<Order> orders) { } }
			public sealed class Till { public Till(IClock clock) { } }

			[Container]
			[Import(typeof(CoffeeModule))]
			[Transient<Till>]
			public static partial class AppContainer;
			""";

		/// <summary>
		///     Runs the generator over <paramref name="source" /> with the module's <paramref name="id" /> lowered or
		///     suppressed through <paramref name="path" />.
		/// </summary>
		private static GeneratorResult RunLowered(string source, string id, string path)
			=> path switch
			{
				"editorconfig" => Generator.Run(new Dictionary<string, string> { ["Lib.cs"] = source, }, $"[*.cs]\ndotnet_diagnostic.{id}.severity = warning"),
				"nowarn" => Generator.Run(source, new Dictionary<string, ReportDiagnostic> { [id] = ReportDiagnostic.Suppress, }),
				_ => Generator.Run($"#pragma warning disable {id}\n" + source),
			};

		[Test]
		[MatrixDataSource]
		public async Task AConsumer_ReportsTheMissingServiceWithTheReason(
			[Matrix("ref", "overload", "notPartial", "hiddenParameter", "generic", "genericRef")] string shape,
			[Matrix("editorconfig", "nowarn", "pragma")] string path)
		{
			(string id, string modifiers, string registration, string member, string consumer, string service, string reason) = Shape(shape);

			GeneratorResult result = RunLowered(Library(modifiers, registration, member) + Container(consumer), id, path);

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains("AWT255"))).IsEmpty()
				.Because("the container knows why the registration is missing, so it does not take that for a bug of the generator");
			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains(": error "))).HasSingle().Which
				.Contains($"*error AWT101: '*' cannot be resolved: '*' requires '{service}', which is not registered on the container; its registration on the module 'Lib.CoffeeModule' is left out, because {reason} ({id})").AsWildcard()
				.Because("the module's error no longer fails the build, so the container leaves the registration out and reports the consumer's missing dependency with the cause");
			await That(result.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("has registration errors");
		}

		[Test]
		[MatrixDataSource]
		public async Task NoConsumer_WiresTheRestOfTheGraph(
			[Matrix("ref", "overload", "notPartial", "hiddenParameter", "generic", "genericRef")] string shape,
			[Matrix("editorconfig", "nowarn", "pragma")] string path)
		{
			(string id, string modifiers, string registration, string member, _, _, _) = Shape(shape);

			GeneratorResult result = RunLowered(Library(modifiers, registration, member) + Container("[Transient<Till>]"), id, path);

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains(": error "))).IsEmpty()
				.Because("nothing depends on the registration the container leaves out");
			await That(result.Sources["Awaiten.Lib.SameContainer.g.cs"]).DoesNotContain("has registration errors")
				.Because("the module's error does not fail the build, so a shell would compile and throw only at run time");
		}

		[Test]
		[Arguments("ref")]
		[Arguments("overload")]
		[Arguments("notPartial")]
		[Arguments("hiddenParameter")]
		[Arguments("generic")]
		[Arguments("genericRef")]
		public async Task AnError_LeavesTheConsumingContainerOnlyItsShell(string shape)
		{
			(string id, string modifiers, string registration, string member, string consumer, _, _) = Shape(shape);

			GeneratorResult result = Generator.Run(Library(modifiers, registration, member) + Container(consumer));

			await That(result.Diagnostics).HasSingle().Which.Contains($"*error {id}:*").AsWildcard()
				.Because("the module's error fails the build, so the container adds no missing dependency it leads to");
			await That(result.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("has registration errors");
		}

		[Test]
		[Arguments("ref", "'Make' is private and its parameter 'count' is passed by reference")]
		[Arguments("overload", "the module has more than one method 'Make' returning 'Lib.Greeter' that is not public")]
		[Arguments("hiddenParameter", "'Make' is private and its parameter 'secret' has the type 'Lib.CoffeeModule.Secret', which is not public")]
		[Arguments("notPartial", "has no accessible method 'Make' returning 'Lib.IGreeter'")]
		public async Task AFriendOfAClosedRegistration_ReportsItsOwnErrorWhetherOrNotItConsumesTheService(string shape, string cause)
		{
			(_, string modifiers, string registration, string member, string consumer, _, _) = Shape(shape);
			string library = Granted(Library(modifiers, registration, member));

			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(library, FriendContainer);
			GeneratorResult consuming = Generator.RunWithGeneratedReferencedAssembly(library, FriendContainer.Replace("[Transient<Till>]", consumer));

			foreach (GeneratorResult result in new[] { friend, consuming, })
			{
				await That(result.Diagnostics).HasSingle().Which.Contains($"*error AWT108: 'Lib.IGreeter' cannot be produced: the module 'Lib.CoffeeModule'*{cause}*").AsWildcard()
					.Because("another assembly cannot call the private member either, and it builds every registration it imports, so its own build reports the cause whatever the library's build lowers");
				await That(result.Sources["Awaiten.App.AppContainer.g.cs"]).Contains("has registration errors");
			}
		}

		[Test]
		[Arguments("generic")]
		[Arguments("genericRef")]
		public async Task AFriendOfAnOpenRegistration_ReportsItsOwnErrorOnlyForAConsumer(string shape)
		{
			(_, string modifiers, string registration, string member, string consumer, _, _) = Shape(shape);
			string library = Granted(Library(modifiers, registration, member));

			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(library, FriendContainer);
			GeneratorResult consuming = Generator.RunWithGeneratedReferencedAssembly(library, FriendContainer.Replace("[Transient<Till>]", consumer));

			await That(friend.Diagnostics).IsEmpty()
				.Because("a container closes an open registration only for a consumer");
			await That(consuming.Diagnostics).HasSingle().Which.Contains("*error AWT108: 'Lib.IRepository<Lib.Order>' cannot be produced: the module 'Lib.CoffeeModule' has no accessible method 'Make'*").AsWildcard()
				.Because("another assembly does not see the private member");
		}

		[Test]
		[Arguments("editorconfig")]
		[Arguments("nowarn")]
		[Arguments("pragma")]
		public async Task AnotherErrorOfTheModule_StillLeavesTheContainerOnlyItsShell(string path)
		{
			string library = Library("public static partial", ClosedRegistration, "private static Greeter Make(ref int count) => new Greeter();")
				.Replace("[Singleton<Clock, IClock>]", "[Singleton<Clock, IClock>][Singleton<CoffeeModule.Impl, IClock>(Key = \"impl\", Factory = nameof(MakeImpl))]")
				.Replace("private static Greeter Make(ref int count) => new Greeter();", "private static Greeter Make(ref int count) => new Greeter(); private static Impl MakeImpl() => new Impl(); private sealed class Impl : IClock { }");

			GeneratorResult result = RunLowered(library + Container("[Transient<Barista>]"), "AWT153", path);

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains(": error "))).HasSingle().Which.Contains("*error AWT216:*").AsWildcard()
				.Because("an error of the module that still fails the build leaves the container only its shell, so it reports no missing dependency besides");
			await That(result.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("has registration errors");
		}

		[Test]
		public async Task AKeyedConsumer_NamesTheRegistrationUnderItsKey()
		{
			string library = Library("public static partial", "[Singleton<Greeter, IGreeter>(Key = \"house\", Factory = nameof(Make))]", "private static Greeter Make(ref int count) => new Greeter();");
			string container = Container("[Transient<Barista>][Transient<Roaster>]")
				+ "\npublic sealed class Roaster { public Roaster([FromKey(\"house\")] IGreeter greeter) { } }";

			GeneratorResult result = RunLowered(library + container, "AWT153", "pragma");

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains(": error "))).HasCount(2);
			await That(result.Diagnostics).Contains(diagnostic => diagnostic.Contains("'Lib.Roaster' requires 'Lib.IGreeter (key: house)', which is not registered on the container; its registration on the module 'Lib.CoffeeModule' is left out"))
				.Because("the keyed consumer misses the registration the container left out");
			await That(result.Diagnostics).Contains(diagnostic => diagnostic.Contains("'Lib.Barista' requires 'Lib.IGreeter', which is not registered on the container") && !diagnostic.Contains("left out"))
				.Because("no unkeyed registration of the service was declared, so the unkeyed consumer misses it for no other reason");
		}

		[Test]
		public async Task AnError_LeavesAContainerExtendingAnImporterOnlyItsShell()
		{
			string library = Library("public static partial", ClosedRegistration, "private static Greeter Make(ref int count) => new Greeter();");
			string containers = Container("[Transient<Till>]") + """

				[Container(Extends = typeof(SameContainer))]
				[Transient<Barista>]
				public static partial class ExtendingContainer;
				""";

			GeneratorResult result = Generator.Run(library + containers);

			await That(result.Diagnostics).HasSingle().Which.Contains("*error AWT153:*").AsWildcard()
				.Because("the module's error fails the build, so the container that inherits the module's registration adds no missing dependency it leads to");
			await That(result.Sources["Awaiten.Lib.ExtendingContainer.g.cs"]).Contains("has registration errors");
		}

		[Test]
		[Arguments("editorconfig")]
		[Arguments("nowarn")]
		[Arguments("pragma")]
		public async Task AnExtendingContainer_ReportsTheMissingServiceWithTheReason(string path)
		{
			string library = Library("public static partial", ClosedRegistration, "private static Greeter Make(ref int count) => new Greeter();");
			string containers = Container("[Transient<Till>]") + """

				[Container(Extends = typeof(SameContainer))]
				[Transient<Barista>]
				public static partial class ExtendingContainer;
				""";

			GeneratorResult result = RunLowered(library + containers, "AWT153", path);

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains(": error "))).HasSingle().Which
				.Contains("*error AWT101: 'Lib.Barista' cannot be resolved: * its registration on the module 'Lib.CoffeeModule' is left out, because *(AWT153)").AsWildcard()
				.Because("the container inherits the module's registration, which it leaves out like the base");
			await That(result.Sources["Awaiten.Lib.SameContainer.g.cs"]).DoesNotContain("has registration errors")
				.Because("nothing in the base depends on the registration it leaves out");
		}

		[Test]
		[Arguments("editorconfig")]
		[Arguments("nowarn")]
		[Arguments("pragma")]
		public async Task AContainerNestedInTheModule_CallsThePrivateMemberItself(string path)
		{
			string library = Library("public static partial", OpenRegistration, """
				private static Repository<T> Make<T>() => new Repository<T>();

				[Container]
				[Import(typeof(CoffeeModule))]
				[Transient<Checkout>]
				public static partial class Nested;
				""");

			GeneratorResult result = RunLowered(library + Container("[Transient<Till>]"), "AWT153", path);

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains(": error "))).IsEmpty()
				.Because("a container nested in the module reaches its private member, so it keeps the registration");
			await That(result.Sources["Awaiten.Lib.CoffeeModule+Nested.g.cs"]).Contains("global::Lib.CoffeeModule.Make<global::Lib.Order>()");
		}

		private static string Granted(string library)
			=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

		private static (string Id, string Modifiers, string Registration, string Member, string Consumer, string Service, string Reason) Shape(string shape)
			=> shape switch
			{
				"ref" => ("AWT153", "public static partial", ClosedRegistration, "private static Greeter Make(ref int count) => new Greeter();", "[Transient<Barista>]", "Lib.IGreeter",
					"its Factory member 'Make' is private, and the module can generate no wrapper for it"),
				"overload" => ("AWT112", "public static partial", ClosedRegistration, "private static Greeter Make() => new Greeter(); private static Greeter Make(IClock clock) => new Greeter();", "[Transient<Barista>]", "Lib.IGreeter",
					"its Factory member 'Make' is private and overloaded, so no wrapper can choose between the overloads"),
				"notPartial" => ("AWT194", "public static", ClosedRegistration, "private static Greeter Make() => new Greeter();", "[Transient<Barista>]", "Lib.IGreeter",
					"its Factory member 'Make' is private, and the module is not partial, so it cannot receive the wrapper a container would call"),
				"hiddenParameter" => ("AWT195", "public static partial", ClosedRegistration, "private static Greeter Make(Secret secret) => new Greeter(); private sealed class Secret { }", "[Transient<Barista>]", "Lib.IGreeter",
					"its Factory member 'Make' is private, and its parameter 'secret' has a type private to the module, which no container can resolve"),
				"generic" => ("AWT153", "public static partial", OpenRegistration, "private static Repository<T> Make<T>() => new Repository<T>();", "[Transient<Checkout>]", "Lib.IRepository<Lib.Order>",
					"its Factory member 'Make' is generic and private, and the module generates no wrapper for a generic member"),
				_ => ("AWT153", "public static", OpenRegistration, "private static Repository<T> Make<T>(ref int count) => new Repository<T>();", "[Transient<Checkout>]", "Lib.IRepository<Lib.Order>",
					"its Factory member 'Make' is generic and private, and the module generates no wrapper for a generic member"),
			};
	}
}
