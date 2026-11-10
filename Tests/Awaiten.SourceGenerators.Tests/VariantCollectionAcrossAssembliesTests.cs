namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A decorator of a module takes a collection of an internal contravariant handler of a derived event, while a module
///     of its assembly registers only a handler of the base event, which the collection holds through variance in a
///     container of the module's assembly.
/// </summary>
public class VariantCollectionAcrossAssembliesTests
{
	private const string Friend = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]";

	private const string App = """
		using Awaiten;

		namespace MyCode;

		[Container]
		[Import(typeof(Lib.BrewModule))]
		public static partial class MyContainer;
		""";

	private static string Library(string grants = "", string registrations = "[Singleton<BaseHandler, IHandler<Base>>]") => $$"""
		using System.Collections.Generic;
		using Awaiten;

		{{grants}}

		namespace Lib;

		internal interface IHandler<in T> { }
		internal class Base { }
		internal sealed class Derived : Base { }
		public interface IBrew { }
		internal sealed class Brew : IBrew { }
		internal sealed class BaseHandler : IHandler<Base> { }
		internal sealed class DerivedHandler : IHandler<Derived> { }
		internal sealed class ObjectHandler : IHandler<object> { }
		internal sealed class Counting : IBrew { public Counting(IBrew inner, IEnumerable<IHandler<Derived>> handlers) { } }

		[Module]
		[Singleton<Brew, IBrew>]
		{{registrations}}
		[Decorate<Counting, IBrew>]
		public static partial class BrewModule { }
		""";

	private static string Module(GeneratorResult result) => result.Sources["Awaiten.ModuleScan.Lib.BrewModule.g.cs"];

	private static string Container(GeneratorResult result) => result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

	[Test]
	public async Task TheModulesFactory_TakesTheCollectionThroughTheHandleOfTheVariantService()
	{
		GeneratorResult result = Generator.Run(Library());

		await That(result.Diagnostics).IsEmpty();
		await That(Module(result)).Contains("global::System.Linq.Enumerable.Select<global::Lib.BrewModule.Internals.Hidden_IHandler_")
			.Because("the factory takes the handles of 'IHandler<Base>' and passes them on as handlers of 'Derived'");
		await That(Module(result)).DoesNotContain("global::System.Array.Empty<global::Lib.IHandler<global::Lib.Derived>>()")
			.Because("a module of the assembly provides the collection through variance, so it is not fixed empty");
	}

	[Test]
	public async Task AContainerOfAnotherAssembly_PassesTheVariantRegistration()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(),], App);

		await That(result.Diagnostics).IsEmpty();
		await That(Container(result)).Contains("Decorator_Counting_").And.Contains("new global::Lib.BrewModule.Internals.Hidden_IHandler_")
			.Because("the container passes the handle of the base handler to the module's factory");
	}

	[Test]
	public async Task AContainerOfAnotherAssemblyAgainstAReferenceAssembly_PassesTheVariantRegistration()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(),], App, referenceAssemblies: true);

		await That(result.Diagnostics).IsEmpty();
		await That(Container(result)).Contains("new global::Lib.BrewModule.Internals.Hidden_IHandler_");
	}

	[Test]
	public async Task AFriendContainer_BuildsTheDecoratorWithTheVariantRegistration()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(Friend),], App);

		await That(result.Diagnostics).IsEmpty();
		await That(Container(result)).Contains("new global::Lib.Counting(Root.ResolveBrew(__s.__root), new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveBaseHandler(__s.__root) })");
	}

	[Test]
	public async Task TheModulesAssembly_BuildsTheDecoratorWithTheVariantRegistration()
	{
		GeneratorResult result = Generator.Run(Library().Replace("namespace Lib;", "namespace Lib {") + "}\n" + App.Replace("using Awaiten;", "").Replace("namespace MyCode;", "namespace MyCode {") + "}");

		await That(result.Diagnostics).IsEmpty();
		await That(Container(result)).Contains("new global::Lib.Counting(Root.ResolveBrew(__s.__root), new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveBaseHandler(__s.__root) })");
	}

	[Test]
	public async Task AVariantServiceOfAnotherModule_IsTakenThroughItsHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[
				Library(registrations: "")
				+ """

				  [Module]
				  [Singleton<BaseHandler, IHandler<Base>>]
				  public static partial class HandlerModule { }
				  """,
			],
			App.Replace("[Import(typeof(Lib.BrewModule))]", "[Import(typeof(Lib.BrewModule))]\n[Import(typeof(Lib.HandlerModule))]"));

		await That(result.Diagnostics).IsEmpty();
		await That(Container(result)).Contains("new global::Lib.HandlerModule.Internals.Hidden_IHandler_");
	}

	[Test]
	public async Task ADecoratorOfAnExactAndAVariantService_GetsNoFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(registrations: "[Singleton<DerivedHandler, IHandler<Derived>>]\n[Singleton<BaseHandler, IHandler<Base>>]"),], App);

		await That(result.Diagnostics).HasSingle().Which.Contains("error AWT230: The imported module 'Lib.BrewModule' declares the decorator 'Lib.Counting'")
			.Because("one parameter takes the handles of one service, while the collection holds the registrations of two, so the module generates no factory instead of one that leaves a registration out");
	}

	[Test]
	public async Task ADecoratorOfTwoVariantServices_GetsNoFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(registrations: "[Singleton<BaseHandler, IHandler<Base>>]\n[Singleton<ObjectHandler, IHandler<object>>]"),], App);

		await That(result.Diagnostics).HasSingle().Which.Contains("error AWT230");
	}

	[Test]
	public async Task APublicVariantService_GetsNoFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[
				Library()
					.Replace("internal interface IHandler<in T> { }", "public interface IHandler<in T> { }")
					.Replace("internal class Base { }", "public class Base { }"),
			],
			App);

		await That(result.Diagnostics).HasSingle().Which.Contains("error AWT230: The imported module 'Lib.BrewModule' declares the decorator 'Lib.Counting'")
			.Because("the collection holds the public 'IHandler<Base>' through variance, which the factory has no handle of, so the module generates no factory instead of passing the collection empty");
	}

	[Test]
	public async Task AContextualVariantRegistration_KeepsTheExactHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[
				Library(registrations: "[Singleton<DerivedHandler, IHandler<Derived>>]\n[Singleton<BaseHandler, IHandler<Base>>(WhenInjectedInto = typeof(Other))]\n[Singleton<Other>]")
					.Replace("public interface IBrew { }", "public interface IBrew { } public sealed class Other { internal Other(IHandler<Base> handler) { } }"),
			],
			App);

		await That(result.Diagnostics).IsEmpty()
			.Because("a contextual registration is no member of a collection, so the collection holds only the exact service");
		await That(Container(result)).Contains("Decorator_Counting_").And.Contains("new global::Lib.BrewModule.Internals.Hidden_IHandler_");
	}

	[Test]
	public async Task ARegistrationOfAnExactAndAVariantService_IsReportedInTheModulesBuild()
	{
		GeneratorResult result = Generator.Run(Library(registrations: """
			[Singleton<DerivedHandler, IHandler<Derived>>]
			[Singleton<BaseHandler, IHandler<Base>>]
			[Singleton<Reader, IReader>]
			""").Replace("public interface IBrew { }", "public interface IBrew { } public interface IReader { } internal sealed class Reader : IReader { public Reader(IEnumerable<IHandler<Derived>> handlers) { } }"));

		await That(result.Diagnostics).Contains("*error AWT237: *'Lib.Reader'*takes the internal service 'Lib.IHandler<Lib.Base>' as 'System.Collections.Generic.IEnumerable<Lib.IHandler<Lib.Derived>>'*").AsWildcard()
			.Because("one parameter takes the handles of one service, while the collection holds the registrations of two");
	}

	[Test]
	public async Task ADecoratorOfVariantImplementationsRegisteredUnderTheExactService_GetsAFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(registrations: "[Singleton<BaseHandler, IHandler<Derived>>]\n[Singleton<ObjectHandler, IHandler<Derived>>]"),], App);

		await That(result.Diagnostics).IsEmpty()
			.Because("the collection holds the registrations of one service, which the factory takes through its handle");
	}

	[Test]
	public async Task ADecoratorOfAVariantImplementationRegisteredUnderTheExactServiceNextToItsOwn_GetsNoFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[Library(registrations: "[Singleton<DerivedHandler, IHandler<Derived>>]\n[Singleton<BaseHandler, IHandler<Base>>]\n[Singleton<BaseHandler, IHandler<Derived>>]"),],
			App);

		await That(result.Diagnostics).HasSingle().Which.Contains("error AWT230")
			.Because("the collection still holds the variant registration of 'IHandler<Base>' next to the exact service");
	}

	[Test]
	public async Task ADecoratorOfAnExactAndAVariantServiceThatArePublic_GetsAFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[
				Library(registrations: "[Singleton<DerivedHandler, IHandler<Derived>>]\n[Singleton<BaseHandler, IHandler<Base>>]")
					.Replace("internal interface IHandler<in T> { }", "public interface IHandler<in T> { }")
					.Replace("internal class Base { }", "public class Base { }")
					.Replace("internal sealed class Derived : Base { }", "public sealed class Derived : Base { }"),
			],
			App);

		await That(result.Diagnostics).IsEmpty()
			.Because("a collection of public services needs no handle, so it can hold several of them");
	}

	[Test]
	public async Task AKeyedCollection_IsStillFixedEmpty()
	{
		GeneratorResult result = Generator.Run(Library().Replace("IEnumerable<IHandler<Derived>> handlers", "[FromKey(\"spare\")] IEnumerable<IHandler<Derived>> handlers"));

		await That(result.Diagnostics).IsEmpty();
		await That(Module(result)).Contains("global::System.Array.Empty<global::Lib.IHandler<global::Lib.Derived>>()")
			.Because("a keyed collection holds no variance match, so no module of the assembly provides it");
	}
}
