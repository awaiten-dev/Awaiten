namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     A container that the first library grants <c>InternalsVisibleTo</c> builds the decorator of that library itself,
	///     whose collection takes an internal element type that a second library, which grants the container nothing,
	///     registers through a generated handle the container cannot read.
	/// </summary>
	public class Awt257CollectionMissesHandledRegistration
	{
		private const string Friend = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]";

		private const string App = """
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(Lib.BrewModule))]
			[Import(typeof(Lib2.ThingModule))]
			public static partial class MyContainer;
			""";

		private const string Expected =
			"(7,2): error AWT257: 'Lib.Counting' takes a collection of 'Lib.IThing', which would miss 'Lib2.Thing': 'Lib2.ThingModule' registers it under 'Lib.IThing' through a generated handle this container cannot read; grant this assembly InternalsVisibleTo in the assembly of 'Lib2.ThingModule', or make 'Lib.IThing' public";

		private static string Library1(string grants = Friend, string parameter = "IEnumerable<IThing> things", string types = "internal interface IThing { }") => $$"""
			using System.Collections.Generic;
			using Awaiten;

			[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Library2")]
			{{grants}}

			namespace Lib;

			public interface IBrew { }
			{{types}}
			internal sealed class Brew : IBrew { }
			internal sealed class Counting : IBrew { public Counting(IBrew inner, {{parameter}}) { } }

			[Module]
			[Singleton<Brew, IBrew>]
			[Decorate<Counting, IBrew>]
			public static partial class BrewModule { }
			""";

		private static string Library2(string grants = "", string registration = "[Singleton<Thing, Lib.IThing>]", string thing = "public sealed class Thing : Lib.IThing { }") => $$"""
			using Awaiten;

			{{grants}}

			namespace Lib2;

			{{thing}}

			[Module]
			{{registration}}
			public static partial class ThingModule { }
			""";

		private static string Container(GeneratorResult result) => result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		[Test]
		public async Task AContainerTheFirstLibraryGrantsOnly_ReportsTheRegistrationAtItsImport()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(),], App);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("the container builds 'Counting' itself, while the second library serves 'Thing' only through a handle the container cannot read");
		}

		[Test]
		public async Task AgainstReferenceAssemblies_ReportsTheSame()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(),], App, referenceAssemblies: true);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected);
		}

		[Test]
		public async Task AContainerBothLibrariesGrant_PassesTheRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(Friend),], App);

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.Counting(Root.ResolveBrew(__s.__root), new global::Lib.IThing[] { Root.ResolveThing(__s.__root) })")
				.Because("the container registers 'Thing' under 'Lib.IThing' itself, the first remedy the diagnostic names");
		}

		[Test]
		public async Task APublicElementType_PassesTheRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(types: "public interface IThing { }"), Library2(),], App);

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.Counting(Root.ResolveBrew(__s.__root), new global::Lib.IThing[] { Root.ResolveThing(__s.__root) })")
				.Because("the second library registers a public service without a handle, the second remedy the diagnostic names");
		}

		[Test]
		public async Task AContainerNeitherLibraryGrants_ReportsAwt252Instead()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(grants: ""), Library2(),], App);

			await That(result.Diagnostics).HasSingle().Which.Contains("AWT252")
				.Because("the container calls the module's factory, which passes the collection empty in the library's build");
		}

		[Test]
		public async Task AllInOneAssembly_PassesTheRegistration()
		{
			GeneratorResult result = Generator.Run("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib
				{
					public interface IBrew { }
					internal interface IThing { }
					internal sealed class Brew : IBrew { }
					internal sealed class Counting : IBrew { public Counting(IBrew inner, IEnumerable<IThing> things) { } }

					[Module]
					[Singleton<Brew, IBrew>]
					[Decorate<Counting, IBrew>]
					public static partial class BrewModule { }
				}

				namespace Lib2
				{
					public sealed class Thing : Lib.IThing { }

					[Module]
					[Singleton<Thing, Lib.IThing>]
					public static partial class ThingModule { }
				}

				namespace MyCode
				{
					[Container]
					[Import(typeof(Lib.BrewModule))]
					[Import(typeof(Lib2.ThingModule))]
					public static partial class MyContainer;
				}
				""");

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AVarianceMatch_IsReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					Library1(parameter: "IEnumerable<IHandler<Order>> handlers", types: "internal interface IHandler<in T> { } internal class Message { } internal sealed class Order : Message { }"),
					Library2(registration: "[Singleton<Handler, Lib.IHandler<Lib.Message>>]", thing: "public sealed class Handler : Lib.IHandler<Lib.Message> { }"),
				],
				App);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(7,2): error AWT257: 'Lib.Counting' takes a collection of 'Lib.IHandler<Lib.Order>', which would miss 'Lib2.Handler': 'Lib2.ThingModule' registers it under 'Lib.IHandler<Lib.Message>' through a generated handle this container cannot read; grant this assembly InternalsVisibleTo in the assembly of 'Lib2.ThingModule', or make 'Lib.IHandler<Lib.Message>' public")
				.Because("a collection of 'IHandler<Order>' holds the contravariant registration of 'IHandler<Message>'");
		}

		[Test]
		public async Task AnInvariantGeneric_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					Library1(parameter: "IEnumerable<IHandler<Order>> handlers", types: "internal interface IHandler<T> { } internal class Message { } internal sealed class Order : Message { }"),
					Library2(registration: "[Singleton<Handler, Lib.IHandler<Lib.Message>>]", thing: "public sealed class Handler : Lib.IHandler<Lib.Message> { }"),
				],
				App);

			await That(result.Diagnostics).IsEmpty()
				.Because("an invariant collection never holds a registration of another closing");
		}

		[Test]
		public async Task AKeyedCollection_IsReportedForARegistrationUnderItsKey()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(parameter: "[FromKey(\"spare\")] IEnumerable<IThing> things"), Library2(registration: "[Singleton<Thing, Lib.IThing>(Key = \"spare\")]"),],
				App);

			await That(result.Diagnostics).HasSingle().Which.Contains("AWT257: 'Lib.Counting' takes a collection of 'Lib.IThing (key: spare)', which would miss 'Lib2.Thing'");
		}

		[Test]
		public async Task ARegistrationUnderAnotherKey_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(registration: "[Singleton<Thing, Lib.IThing>(Key = \"spare\")]"),], App);

			await That(result.Diagnostics).IsEmpty()
				.Because("the unkeyed collection never holds a keyed registration");
		}

		[Test]
		public async Task AKeyedDictionary_IsReportedForAKeyedRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(parameter: "IReadOnlyDictionary<string, IThing> things"), Library2(registration: "[Singleton<Thing, Lib.IThing>(Key = \"spare\")]"),],
				App);

			await That(result.Diagnostics).HasSingle().Which.Contains("AWT257: 'Lib.Counting' takes a collection of 'Lib.IThing', which would miss 'Lib2.Thing'");
		}

		[Test]
		public async Task AnAsyncCollection_IsReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(parameter: "IAsyncEnumerable<IThing> things"), Library2(),], App);

			await That(result.Diagnostics).HasSingle().Which.Contains("AWT257: 'Lib.Counting' takes a collection of 'Lib.IThing'");
		}

		[Test]
		public async Task AWhenInjectedIntoRegistration_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(), Library2(registration: "[Singleton<Thing, Lib.IThing>(WhenInjectedInto = typeof(Lib.Counting))]"),],
				App);

			await That(result.Diagnostics).DoesNotContain("*AWT257*").AsWildcard()
				.Because("a contextual registration is no member of a collection");
		}

		[Test]
		public async Task AConsumerOfTheContainersAssembly_IsReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(), Library2(),],
				"""
				using System.Collections.Generic;
				using Awaiten;

				namespace MyCode;

				internal sealed class Reader { public Reader(IEnumerable<Lib.IThing> things) { } }

				[Container]
				[Import(typeof(Lib2.ThingModule))]
				[Singleton<Reader>]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.StartsWith("(9,2): error AWT257: 'MyCode.Reader' takes a collection of 'Lib.IThing', which would miss 'Lib2.Thing'");
		}

		[Test]
		public async Task ACollectionOfTheHandledServiceAsAConsumer_IsReportedOnce()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(), Library2(),],
				"""
				using System.Collections.Generic;
				using Awaiten;

				namespace MyCode;

				internal sealed class Reader { public Reader(IEnumerable<Lib.IThing> things, Lib.IThing[] same) { } }

				[Container]
				[Import(typeof(Lib2.ThingModule))]
				[Singleton<Reader>]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.Contains("AWT257")
				.Because("two shapes of one collection miss the same registration");
		}
	}
}
