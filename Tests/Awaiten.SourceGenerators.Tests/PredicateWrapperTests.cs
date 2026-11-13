namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     An internal <c>When</c> predicate of a public module or base container: a container in another assembly calls it
///     through the public wrapper its owner generates, while a container in the owner's assembly or in a friend calls it
///     directly. A predicate no wrapper can serve is AWT247 in the container that cannot call it, worded by the cause.
/// </summary>
public class PredicateWrapperTests
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

	private const string Grant = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;";

	private static string Library(string registration, string member, string modifiers = "public static partial") => $$"""
		using Awaiten;
		using System;
		using System.Linq;

		namespace Lib;

		public interface IGrinder { }
		public sealed class Grinder : IGrinder { }
		public sealed class LoggingGrinder(IGrinder inner) : IGrinder { public IGrinder Inner { get; } = inner; }
		public sealed class Settings { public bool Audit { get; init; } }
		internal sealed class BeanStore { }
		internal sealed class Unregistered { }

		[Module]
		[Singleton<Grinder, IGrinder>]
		{{registration}}
		[Decorate<LoggingGrinder, IGrinder>(When = nameof(Audit))]
		{{modifiers}} class CoffeeModule
		{
			{{member}}
		}
		""";

	public class EagerWhenOfModules
	{
		private static string EagerLibrary(string registration, string member, string decorator = "") => $$"""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			public sealed class Grinder : IGrinder { }
			public sealed class LoggingGrinder(IGrinder inner) : IGrinder { public IGrinder Inner { get; } = inner; }
			public sealed class Settings { public bool Warm { get; init; } }
			internal sealed class BeanStore { }

			[Module]
			[Singleton<Grinder, IGrinder>(EagerWhen = nameof(Warm))]
			{{registration}}
			{{decorator}}
			public static partial class CoffeeModule
			{
				{{member}}
			}
			""";

		[Test]
		[Arguments("", "internal static bool Warm => true;", "")]
		[Arguments("[Singleton<Settings>]", "internal static bool Warm(Settings settings) => settings.Warm;", "")]
		[Arguments("[Singleton<BeanStore>]", "internal static bool Warm(BeanStore store) => true;", "")]
		[Arguments("", "internal static bool Warm => true;", "[Decorate<LoggingGrinder, IGrinder>(When = nameof(Warm))]")]
		public async Task InternalPredicate_IsUsableEverywhere(string registration, string member, string decorator)
		{
			string library = EagerLibrary(registration, member, decorator);

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("the module generates a public wrapper for the EagerWhen predicate like for a When predicate");
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).IsEmpty();
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);
			await That(external.Diagnostics).IsEmpty();
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.Predicate_Warm")
				.Because("a container in another assembly calls the predicate through the module's wrapper");
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", Grant), ExternalContainer);
			await That(friend.Diagnostics).IsEmpty();
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Warm").And.DoesNotContain("Predicate_Warm");
		}

		[Test]
		public async Task PredicateSharedWithADecorator_GetsOneWrapperAndOneFlag()
		{
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(
				EagerLibrary("", "internal static bool Warm => true;", "[Decorate<LoggingGrinder, IGrinder>(When = nameof(Warm))]"), ExternalContainer);

			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("__when0 = global::Lib.CoffeeModule.Internals.Predicate_Warm;").And.DoesNotContain("__when1")
				.Because("the EagerWhen and the When name the same predicate, so the root reads it once");
		}
	}

	public class Modules
	{
		[Test]
		[Arguments("", "internal static bool Audit => Environment.GetCommandLineArgs().Contains(\"--audit\");")]
		[Arguments("", "internal static bool Audit = true;")]
		[Arguments("", "internal static bool Audit() => true;")]
		[Arguments("[Singleton<Settings>]", "internal static bool Audit(Settings settings) => settings.Audit;")]
		[Arguments("[Singleton<Settings>(Instance = nameof(Current))]", "public static readonly Settings Current = new(); internal static bool Audit(Settings settings) => settings.Audit;")]
		[Arguments("[ImportService<Settings>]", "internal static bool Audit(Settings settings) => settings.Audit;")]
		[Arguments("[Singleton<BeanStore>]", "internal static bool Audit(BeanStore store) => true;")]
		public async Task InternalPredicate_IsUsableEverywhere(string registration, string member)
		{
			string library = Library(registration, member);

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("the module generates a public wrapper, so every importer can call the predicate");
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).IsEmpty();
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);
			await That(external.Diagnostics).IsEmpty();
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.Predicate_Audit")
				.Because("a container in another assembly calls the predicate through the module's wrapper");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).IsEmpty()
				.Because("a reference assembly leaves out the internal predicate, but not the public wrapper");
			await That(Generator.RunWithGeneratedCompilationReference(library, ExternalContainer).Diagnostics).IsEmpty();
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", Grant), ExternalContainer);
			await That(friend.Diagnostics).IsEmpty();
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Audit").And.DoesNotContain("Predicate_Audit")
				.Because("a friend calls the internal predicate directly and builds the internal services itself");
		}

		[Test]
		public async Task Wrapper_IsAPublicMemberOfTheInternalsClass()
		{
			GeneratorResult result = Generator.Run(Library("[Singleton<BeanStore>]", "internal static bool Audit(BeanStore store) => true;"));

			await That(result.Sources["Awaiten.ModuleScan.Lib.CoffeeModule.g.cs"]).Contains("public static bool Predicate_Audit(global::Lib.CoffeeModule.Internals.Hidden_BeanStore_")
				.Because("an internal service parameter is taken through its handle, like a factory or hook parameter");
		}

		[Test]
		public async Task PropertyPredicate_IsReadOnceInTheRootConstructor()
		{
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library("", "internal static bool Audit => true;"), ExternalContainer);

			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("__when0 = global::Lib.CoffeeModule.Internals.Predicate_Audit;")
				.Because("a property predicate gets a property wrapper, which the root reads once like the property itself");
		}

		[Test]
		[Arguments("[Scoped<Settings>]", "internal static bool Audit(Settings settings) => settings.Audit;", "'Lib.Settings': it is scoped")]
		[Arguments("[Scoped<BeanStore>]", "internal static bool Audit(BeanStore store) => true;", "'Lib.BeanStore (internal to module Lib.CoffeeModule)': it is scoped")]
		public async Task PredicateTakingAServiceTheRootDoesNotShare_IsAwt248Everywhere(string registration, string member, string cause)
		{
			string library = Library(registration, member);

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).HasSingle().Which.Contains("error AWT248: The predicate 'Lib.CoffeeModule.Audit' cannot take 'Lib.");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.Contains($"error AWT248: The predicate 'Lib.CoffeeModule.Audit' cannot take {cause}")
				.Because("the wrapper keeps the rule that a predicate takes only services the root shares, and names the predicate and the service the user wrote");
		}

		[Test]
		public async Task PredicateTakingAnUnregisteredInternalType_ExternalAwt247NamesTheParameter()
		{
			string library = Library("", "internal static bool Audit(Unregistered value) => true;");

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("a container of the module's assembly may still call the predicate itself");
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).HasSingle().Which.Contains("AWT101");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(
				"error AWT247: The predicate 'Lib.CoffeeModule.Audit' is not accessible from the container: the module 'Lib.CoffeeModule' is in another assembly, whose non-public members the container cannot call, and it generated no public wrapper for it: 'Audit' is internal and its parameter 'value' has the type 'Lib.Unregistered', which is not public; make the predicate and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.Contains("'Audit' is internal and its parameter 'value' has the type 'Lib.Unregistered', which is not public");
		}

		[Test]
		public async Task PrivatePredicate_IsAwt247InTheModulesBuildAndEveryContainer()
		{
			string library = Library("", "private static bool Audit => true;");

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.Contains("error AWT247: The predicate 'Lib.CoffeeModule.Audit' is not accessible from the container: it is private to the module 'Lib.CoffeeModule'; make it internal or public")
				.Because("no container can call a private predicate, so the library sees the error before its consumers do");
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).HasSingle().Which.Contains("it is private to the module 'Lib.CoffeeModule'; make it internal or public")
				.Because("the container of the module's assembly reports it in place of the module");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.Contains("error AWT247: The predicate 'Lib.CoffeeModule.Audit'");
		}

		[Test]
		public async Task InternalPredicateOfANonPartialModule_ExternalAwt247NamesTheModuleShape()
		{
			string library = Library("", "internal static bool Audit => true;", "public static");

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("a container of the module's assembly or a friend still calls the predicate");
			await That(Generator.Run(library + SameAssemblyContainer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(
				"error AWT247: The predicate 'Lib.CoffeeModule.Audit' is not accessible from the container: the module 'Lib.CoffeeModule' is in another assembly, whose non-public members the container cannot call, and it generated no public wrapper for it: the module, or a class that contains it, is not partial; make the predicate and its parameter types public, or grant this assembly InternalsVisibleTo in the module's assembly");
		}

		[Test]
		public async Task InternalPredicateOfAModuleBuiltWithoutTheGenerator_ExternalAwt247SuggestsTheGrant()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly(Library("", "internal static bool Audit => true;"), ExternalContainer);

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
				"error AWT247: The predicate 'Lib.CoffeeModule.Audit' is not accessible from the container: the module 'Lib.CoffeeModule' is in another assembly, whose non-public members the container cannot call; make it public, or grant this assembly InternalsVisibleTo in the module's assembly");
		}
	}

	public class BaseContainers
	{
		private static string Base(string member, string registration = "") => $$"""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			public sealed class Grinder : IGrinder { }
			public sealed class LoggingGrinder(IGrinder inner) : IGrinder { public IGrinder Inner { get; } = inner; }
			public sealed class Settings { public bool Audit { get; init; } }

			[Container]
			[Singleton<Grinder, IGrinder>]
			{{registration}}
			[Decorate<LoggingGrinder, IGrinder>(When = nameof(Audit))]
			public static partial class BaseShop
			{
				{{member}}
			}
			""";

		private const string Extending = """
			using Awaiten;

			namespace MyCode;

			[Container(Extends = typeof(Lib.BaseShop))]
			public static partial class TestShop;
			""";

		[Test]
		[Arguments("internal static bool Audit => true;", "")]
		[Arguments("internal static bool Audit(Settings settings) => settings.Audit;", "[Singleton<Settings>]")]
		public async Task InternalPredicate_IsCalledThroughThePublicWrapper(string member, string registration)
		{
			string library = Base(member, registration);

			GeneratorResult own = Generator.Run(library);
			await That(own.Diagnostics).IsEmpty();
			await That(own.Sources["Awaiten.Lib.BaseShop.g.cs"]).Contains("public static class Internals").And.Contains("public static bool Predicate_Audit");
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Extending);
			await That(external.Diagnostics).IsEmpty();
			await That(external.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains("global::Lib.BaseShop.Internals.Predicate_Audit")
				.Because("an extending container in another assembly calls the inherited predicate through the base's wrapper");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, Extending).Diagnostics).IsEmpty();
		}

		[Test]
		public async Task PrivatePredicate_IsAwt247InAnExtendingContainerOfAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Base("private static bool Audit => true;"), Extending);

			await That(result.Diagnostics).HasSingle().Which.EndsWith("error AWT247: The predicate 'Lib.BaseShop.Audit' is not accessible from the container: the container 'Lib.BaseShop' is in another assembly, whose non-public members the container cannot call; make it public, or grant this assembly InternalsVisibleTo in the container's assembly")
				.Because("the base wraps only an internal predicate, and the metadata of another assembly does not show a private one");
		}
	}
}
