using System.Linq;
using System.Text.RegularExpressions;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     Decorators and composites of a service a module registers under an internal type: the library alone, a container
///     in the library's assembly, one in another assembly and one the library grants <c>InternalsVisibleTo</c> accept and
///     reject the same shapes.
/// </summary>
public partial class ModuleHiddenDecoratorShapeTests
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

	private static uint Fnv1a(string text)
	{
		uint hash = 2166136261;
		foreach (char character in text)
		{
			hash = unchecked((hash ^ character) * 16777619);
		}

		return hash;
	}

	private static string Handle(string service)
		=> $"global::Lib.CoffeeModule.Internals.Hidden_{service[(service.LastIndexOf('.') + 1)..]}_{Fnv1a(service):x8}";

	private static string Decorator(string decorator, string service)
		=> $"Decorator_{decorator[(decorator.LastIndexOf('.') + 1)..]}_{Fnv1a($"{decorator}\0{service}"):x8}";

	private static string[] Ids(GeneratorResult result)
		=> result.Diagnostics.Select(diagnostic => DiagnosticId().Match(diagnostic).Value).Where(id => id.Length > 0).Distinct().Order().ToArray();

	[GeneratedRegex("AWT\\d{3}")]
	private static partial Regex DiagnosticId();

	private static (GeneratorResult Library, GeneratorResult Same, GeneratorResult External, GeneratorResult Friend) RunAll(string library)
		=> (Generator.Run(library),
			Generator.Run(library + SameAssemblyContainer),
			Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer),
			Generator.RunWithGeneratedReferencedAssembly(library.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), ExternalContainer));

	public class BaseTypeInnerParameter
	{
		private static string Library(string baseDeclaration, string decorator) => $$"""
			using Awaiten;

			namespace Lib;

			{{baseDeclaration}}
			public interface IGrinder { }
			internal interface IBeanStore : IStore { }
			internal sealed class BeanStore : IBeanStore { }
			{{decorator}}
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			[Singleton<Grinder, IGrinder>]
			[Decorate<LoggingStore, IBeanStore>]
			public static partial class CoffeeModule { }
			""";

		[Test]
		[Arguments("public interface IStore { }", "internal sealed class LoggingStore : IBeanStore { public LoggingStore(IStore inner) { } }")]
		[Arguments("internal interface IStore { }", "internal sealed class LoggingStore : IBeanStore { public LoggingStore(IStore inner) { } }")]
		[Arguments("public interface IStore { }", "public sealed class LoggingStore : IBeanStore { public LoggingStore(IStore inner) { } }")]
		[Arguments("public interface IStore { }", "internal sealed class LoggingStore : IBeanStore { public LoggingStore(object inner) { } }")]
		public async Task IsAcceptedByEveryContainer(string baseDeclaration, string decorator)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(baseDeclaration, decorator));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the module's factory takes the handle for the inner parameter, whose type is a base type of the internal service");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task TakesTheHandleOfTheServiceAndPassesTheInstanceOn()
		{
			string handle = Handle("global::Lib.IBeanStore");
			string factory = Decorator("global::Lib.LoggingStore", "global::Lib.IBeanStore");
			(GeneratorResult library, _, GeneratorResult external, _) = RunAll(Library("public interface IStore { }", "internal sealed class LoggingStore : IBeanStore { public LoggingStore(IStore inner) { } }"));

			await That(library.Sources.Single(source => source.Key.Contains("ModuleScan")).Value)
				.Contains($"public static {handle} {factory}({handle} @inner) => new {handle}(new global::Lib.LoggingStore(@inner.Value));");
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.{factory}(")
				.Because("the container decorates the handle through the module's factory");
		}

		[Test]
		public async Task OfADecoratorPrivateToTheModule_IsAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll("""
				using Awaiten;

				namespace Lib;

				public interface IStore { }
				public interface IGrinder { }
				internal interface IBeanStore : IStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Decorate<CoffeeModule.LoggingStore, IBeanStore>]
				public static partial class CoffeeModule
				{
					private sealed class LoggingStore : IBeanStore { public LoggingStore(IStore inner) { } }
				}
				""");
			string factory = Decorator("global::Lib.CoffeeModule.LoggingStore", "global::Lib.IBeanStore");

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.Internal{factory}(")
				.Because("a container granted InternalsVisibleTo builds the internal service itself and calls the factory that takes it directly");
		}

		[Test]
		public async Task OfADecoratorOfAnotherModule_IsAcceptedByEveryContainer()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IStore { }
				public interface IGrinder { }
				internal interface IBeanStore : IStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class LoggingStore : IBeanStore { public LoggingStore(IStore inner) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }

				[Module]
				[Decorate<LoggingStore, IBeanStore>]
				public static partial class LoggingModule { }
				""";
			const string container = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(LoggingModule))]
				public static partial class MyContainer;
				""";

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.Run(library + """

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(LoggingModule))]
				public static partial class SameContainer;
				""").Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, container).Diagnostics).IsEmpty()
				.Because("the decorating module builds its decorator into the handle the registering module declares");
		}

		[Test]
		public async Task Ambiguous_ReportsAwt124InEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"public interface IStore { }",
				"internal sealed class LoggingStore : IBeanStore { public LoggingStore(IStore first, IStore second) { } }"));

			await That(library.Diagnostics).IsEmpty();
			await That(Ids(same)).IsEqualTo(["AWT124",]);
			await That(Ids(external)).IsEqualTo(["AWT124",])
				.Because("neither parameter is the inner one, so the factory takes neither as the handle");
			await That(Ids(friend)).IsEqualTo(["AWT124",]);
		}
	}

	public class CompositeCollection
	{
		[Test]
		public async Task OfABaseTypeOfTheService_ReportsAwt133InEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IStore { }
				public interface IGrinder { }
				internal interface IBeanStore : IStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class AllStores : IBeanStore { public AllStores(IEnumerable<IStore> stores) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Composite<AllStores, IBeanStore>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(Ids(same)).IsEqualTo(["AWT133",]);
			await That(external.Diagnostics).Contains("*AWT133*'Lib.IStore', not the composed service 'Lib.IBeanStore (internal to module Lib.CoffeeModule)'*").AsWildcard()
				.Because("the container judges the collection against the internal service the handle holds");
			await That(Ids(external)).IsEqualTo(["AWT133",]);
			await That(Ids(friend)).IsEqualTo(["AWT133",]);
		}

		[Test]
		[Arguments("[Singleton<OtherStore, IStore>]")]
		[Arguments("")]
		public async Task OfAnInternalBaseTypeOfTheService_ReportsAwt133InEveryContainer(string registration)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll($$"""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				internal interface IStore { }
				public interface IGrinder { }
				internal interface IBeanStore : IStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class OtherStore : IStore { }
				internal sealed class AllStores : IBeanStore { public AllStores(IEnumerable<IStore> stores) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				{{registration}}
				[Composite<AllStores, IBeanStore>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(Ids(same)).IsEqualTo(["AWT133",]);
			await That(external.Diagnostics).Contains("*AWT133*'Lib.AllStores' fans out over a collection of 'Lib.IStore', not the composed service 'Lib.IBeanStore*").AsWildcard()
				.Because("the container judges the collection against the internal service, whether the factory takes it as the handles of the internal base type or the module could build no factory");
			await That(Ids(external)).IsEqualTo(["AWT133",]);
			await That(Ids(friend)).IsEqualTo(["AWT133",]);
		}
	}

	public class UnregisteredInternalDependency
	{
		[Test]
		[Arguments("IBeanStore")]
		[Arguments("IStore")]
		public async Task OfADecorator_ReportsAwt101InEveryContainer(string inner)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll($$"""
				using Awaiten;

				namespace Lib;

				public interface IStore { }
				public interface IGrinder { }
				internal interface IClock { }
				internal interface IBeanStore : IStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class LoggingStore : IBeanStore { public LoggingStore({{inner}} inner, IClock clock) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Decorate<LoggingStore, IBeanStore>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(Ids(same)).IsEqualTo(["AWT101",]);
			await That(external.Diagnostics).Contains("*AWT101*'Lib.IBeanStore' cannot be resolved: 'Lib.LoggingStore' requires 'Lib.IClock'*").AsWildcard()
				.Because("the module records the internal dependency nothing registers, so the container reports it as a container in the module's assembly does");
			await That(Ids(external)).IsEqualTo(["AWT101",]);
			await That(Ids(friend)).IsEqualTo(["AWT101",]);
		}

		[Test]
		public async Task OfAComposite_ReportsAwt101InEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IClock { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class AllStores : IBeanStore { public AllStores(IEnumerable<IBeanStore> stores, IClock clock) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Composite<AllStores, IBeanStore>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(Ids(same)).IsEqualTo(["AWT101",]);
			await That(external.Diagnostics).Contains("*AWT101*'Lib.AllStores' requires 'Lib.IClock'*").AsWildcard();
			await That(Ids(external)).IsEqualTo(["AWT101",]);
			await That(Ids(friend)).IsEqualTo(["AWT101",]);
		}

		private static string OfAPublicService(string decorator, string attribute) => $$"""
			using System;
			using System.Collections.Generic;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal interface ISecret { }
			internal sealed class Grinder : IGrinder { }
			{{decorator}}

			[Module]
			[Singleton<Grinder, IGrinder>]
			{{attribute}}
			public static partial class CoffeeModule { }
			""";

		[Test]
		[Arguments("internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, ISecret secret) { } }", "[Decorate<LoggingGrinder, IGrinder>]", "'Lib.LoggingGrinder' requires 'Lib.ISecret'")]
		[Arguments("internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, Lazy<ISecret> secret) { } }", "[Decorate<LoggingGrinder, IGrinder>]", "'Lib.LoggingGrinder' requires 'Lib.ISecret'")]
		[Arguments("internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, Func<ISecret> secret) { } }", "[Decorate<LoggingGrinder, IGrinder>]", "'Lib.LoggingGrinder' requires 'Lib.ISecret'")]
		[Arguments("internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, [FromKey(\"vault\")] ISecret secret) { } }", "[Decorate<LoggingGrinder, IGrinder>]", "'Lib.LoggingGrinder' requires 'Lib.ISecret (key: vault)'")]
		[Arguments("internal sealed class AllGrinders : IGrinder { public AllGrinders(IEnumerable<IGrinder> all, ISecret secret) { } }", "[Composite<AllGrinders, IGrinder>]", "'Lib.AllGrinders' requires 'Lib.ISecret'")]
		public async Task OfAPublicService_ReportsAwt101InEveryContainer(string decorator, string attribute, string expected)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(OfAPublicService(decorator, attribute));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains(expected));
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains(expected))
				.Because("the module records the internal dependency nothing registers, so the container reports it as a container in the module's assembly does");
			await That(friend.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains(expected));
		}

		[Test]
		[Arguments("Lazy<IClock>", "'Lib.LoggingStore' requires 'Lib.IClock'")]
		[Arguments("Func<IClock>", "'Lib.LoggingStore' requires 'Lib.IClock'")]
		[Arguments("[FromKey(\"utc\")] IClock", "'Lib.LoggingStore' requires 'Lib.IClock (key: utc)'")]
		public async Task OfAnInternalServiceThroughARelationshipOrKey_ReportsAwt101InEveryContainer(string parameter, string expected)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll($$"""
				using System;
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IClock { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class LoggingStore : IBeanStore { public LoggingStore(IBeanStore inner, {{parameter}} clock) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Decorate<LoggingStore, IBeanStore>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains(expected));
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains(expected))
				.Because("the recorded dependency names the service behind the relationship and its key, as a container in the module's assembly does");
			await That(friend.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT101") && diagnostic.Contains(expected));
		}

		[Test]
		[Arguments("IEnumerable<ISecret>")]
		[Arguments("IReadOnlyList<ISecret>")]
		[Arguments("ISecret[]")]
		public async Task AsACollectionOfAPublicService_IsAnEmptyCollectionInEveryContainer(string parameter)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(OfAPublicService(
				$"internal sealed class LoggingGrinder : IGrinder {{ public LoggingGrinder(IGrinder inner, {parameter} secrets) {{ }} }}", "[Decorate<LoggingGrinder, IGrinder>]"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("no container in another assembly can register the internal element type, so the module's factory passes the empty collection a container in its own assembly passes");
			await That(friend.Diagnostics).IsEmpty();
			await That(library.Sources.Single(source => source.Key.Contains("ModuleScan")).Value).Contains("global::System.Array.Empty<global::Lib.ISecret>()");
		}

		[Test]
		public async Task AsACollectionOfAnInternalService_IsAnEmptyCollectionInEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IClock { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class LoggingStore : IBeanStore { public LoggingStore(IBeanStore inner, IEnumerable<IClock> clocks) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Decorate<LoggingStore, IBeanStore>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AsACollectionOfAnElementTheAssemblyRegisters_IsNotReplacedByAnEmptyOne()
		{
			(GeneratorResult library, _, GeneratorResult external, _) = RunAll(OfAPublicService(
				"internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, IEnumerable<ISecret> secrets) { } }\ninternal sealed class Secret : ISecret { }",
				"[Decorate<LoggingGrinder, IGrinder>]\n[Singleton<Secret, ISecret>]"));

			await That(library.Sources.Single(source => source.Key.Contains("ModuleScan")).Value).DoesNotContain("global::System.Array.Empty<global::Lib.ISecret>()")
				.Because("a registered element must reach the decorator, so the factory cannot stand in an empty collection for it");
			await That(external.Diagnostics).IsEmpty();
		}
	}

	public class SharedImplementation
	{
		[Test]
		public async Task DecoratorsOfEachService_AreAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll("""
				using Awaiten;

				namespace Lib;

				public interface IStock { }
				public interface IGrinder { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore, IStock { }
				internal sealed class LoggingStore : IBeanStore { public LoggingStore(IBeanStore inner) { } }
				internal sealed class CountingStock : IStock { public CountingStock(IStock inner) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store, IStock stock) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<BeanStore, IStock>]
				[Singleton<Grinder, IGrinder>]
				[Decorate<LoggingStore, IBeanStore>]
				[Decorate<CountingStock, IStock>]
				public static partial class CoffeeModule { }
				""");
			string source = external.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{Decorator("global::Lib.LoggingStore", "global::Lib.IBeanStore")}(")
				.And.Contains($"global::Lib.CoffeeModule.Internals.{Decorator("global::Lib.CountingStock", "global::Lib.IStock")}(")
				.Because("each service of the implementation is decorated by its own chain around the instance handle");
		}
	}
}
