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
	public async Task AKeyedCollection_IsStillFixedEmpty()
	{
		GeneratorResult result = Generator.Run(Library().Replace("IEnumerable<IHandler<Derived>> handlers", "[FromKey(\"spare\")] IEnumerable<IHandler<Derived>> handlers"));

		await That(result.Diagnostics).IsEmpty();
		await That(Module(result)).Contains("global::System.Array.Empty<global::Lib.IHandler<global::Lib.Derived>>()")
			.Because("a keyed collection holds no variance match, so no module of the assembly provides it");
	}

	/// <summary>
	///     The element type of the collection is public, so the module registers its exact handlers without a handle,
	///     next to a handler of an internal interface that the collection holds through contravariance.
	/// </summary>
	public sealed class PublicMembers
	{
		private const string Registrations = """
			[Singleton<DerivedHandler, IHandler<Derived>>]
			[Singleton<SecretHandler, IHandler<ISecret>>]
			[Singleton<OtherDerivedHandler, IHandler<Derived>>]
			""";

		private static string Library(string grants = "", string registrations = Registrations, string handlers = "IEnumerable<IHandler<Derived>> handlers") => $$"""
			using System.Collections.Generic;
			using Awaiten;

			{{grants}}

			namespace Lib;

			public interface IHandler<in T> { }
			internal interface ISecret { }
			public class Derived : ISecret { }
			internal sealed class SubDerived : Derived { }
			public sealed class DerivedHandler : IHandler<Derived> { }
			public sealed class OtherDerivedHandler : IHandler<Derived> { }
			internal sealed class SecretHandler : IHandler<ISecret> { }
			internal sealed class SubHandler : IHandler<SubDerived> { }
			public sealed class ObjectHandler : IHandler<object> { }
			internal sealed class BothHandler : IHandler<Derived>, IHandler<ISecret> { }
			public interface IBrew { }
			internal sealed class Brew : IBrew { }
			internal sealed class Counting : IBrew { public Counting(IBrew inner, {{handlers}}) { } }
			public interface IReader { }
			internal sealed class Reader : IReader { public Reader({{handlers}}) { } }

			[Module]
			[Singleton<Brew, IBrew>]
			{{registrations}}
			[Singleton<Reader, IReader>]
			[Decorate<Counting, IBrew>]
			public static partial class BrewModule { }
			""";

		private static string OwnAssembly(string library)
			=> library.Replace("namespace Lib;", "namespace Lib {") + "}\n" + App.Replace("using Awaiten;", "").Replace("namespace MyCode;", "namespace MyCode {") + "}";

		[Test]
		public async Task TheModulesFactories_TakeThePublicMembersBeforeTheHandles()
		{
			GeneratorResult result = Generator.Run(Library());

			await That(result.Diagnostics).IsEmpty();
			await That(Module(result)).Contains("Registration_Reader_").And.Contains("(global::System.Collections.Generic.IEnumerable<global::Lib.IHandler<global::Lib.Derived>> @awaiten__exact_handlers, global::System.Collections.Generic.IEnumerable<global::Lib.BrewModule.Internals.Hidden_IHandler_")
				.Because("the collection holds the public exact handlers, which no handle serves, before the internal variant one");
			await That(Module(result)).Contains("global::Lib.IBrew @inner, global::System.Collections.Generic.IEnumerable<global::Lib.IHandler<global::Lib.Derived>> @awaiten__exact_handlers, global::System.Collections.Generic.IEnumerable<global::Lib.BrewModule.Internals.Hidden_IHandler_")
				.Because("the decorator's factory takes the public members too");
			await That(Module(result)).Contains("global::System.Linq.Enumerable.Concat<global::Lib.IHandler<global::Lib.Derived>>(@awaiten__exact_handlers, global::System.Linq.Enumerable.Select<global::Lib.BrewModule.Internals.Hidden_IHandler_");
		}

		[Test]
		public async Task TheModulesAssembly_HoldsTheExactMembersBeforeTheVariantOne()
		{
			GeneratorResult result = Generator.Run(OwnAssembly(Library()));

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.Reader(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveSecretHandler(__s.__root) })")
				.Because("a collection holds the exact members in registration order, followed by the variance members");
		}

		[Test]
		public async Task AFriendContainer_HoldsTheExactMembersBeforeTheVariantOne()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(Friend),], App);

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveSecretHandler(__s.__root) }");
		}

		[Test]
		public async Task AContainerOfAnotherAssembly_PassesThePublicMembersAndTheHandles()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(),], App);

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("Registration_Reader_").And.Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_")
				.Because("the container passes the public handlers it registers and the handle of the internal one");
			await That(Container(result)).Contains("Decorator_Counting_").And.Contains("(Root.ResolveIBrew(__s.__root), new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
		}

		[Test]
		public async Task AContainerOfAnotherAssemblyAgainstAReferenceAssembly_PassesThePublicMembersAndTheHandles()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(),], App, referenceAssemblies: true);

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
		}

		[Test]
		[Arguments("IReadOnlyList<IHandler<Derived>> handlers", "global::System.Collections.Generic.IReadOnlyList<global::Lib.IHandler<global::Lib.Derived>> @awaiten__exact_handlers, global::System.Collections.Generic.IReadOnlyList<global::Lib.BrewModule.Internals.Hidden_IHandler_")]
		[Arguments("IHandler<Derived>[] handlers", "global::Lib.IHandler<global::Lib.Derived>[] @awaiten__exact_handlers, global::Lib.BrewModule.Internals.Hidden_IHandler_")]
		public async Task AListOrArray_TakesThePublicMembersInTheSameShape(string handlers, string expected)
		{
			GeneratorResult module = Generator.Run(Library(handlers: handlers));
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library(handlers: handlers),], App);

			await That(module.Diagnostics).IsEmpty();
			await That(Module(module)).Contains(expected);
			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
		}

		[Test]
		public async Task AKeyedCollection_TakesThePublicCollectionAlone()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library(registrations: Registrations + "\n[Singleton<ObjectHandler, IHandler<Derived>>(Key = \"spare\")]", handlers: "[FromKey(\"spare\")] IEnumerable<IHandler<Derived>> handlers"),], App);

			await That(result.Diagnostics).IsEmpty()
				.Because("a keyed collection holds no variance match, so the factory takes it as it is");
			await That(Container(result)).Contains("Registration_Reader_").And.Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveObjectHandler(__s.__root) })");
		}

		[Test]
		public async Task AComposite_ComposesThePublicMembersAndTheHandles()
		{
			string library = Library(registrations: Registrations + "\n[Composite<CompositeHandler, IHandler<Derived>>]")
				.Replace("public interface IBrew { }", "public interface IBrew { } internal sealed class CompositeHandler : IHandler<Derived> { public CompositeHandler(IEnumerable<IHandler<Derived>> all) { } }");

			GeneratorResult module = Generator.Run(library);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([library,], App);

			await That(module.Diagnostics).IsEmpty();
			await That(Module(module)).Contains("Composite_CompositeHandler_").And.Contains("(global::System.Collections.Generic.IEnumerable<global::Lib.IHandler<global::Lib.Derived>> @awaiten__exact_all, global::System.Collections.Generic.IEnumerable<global::Lib.BrewModule.Internals.Hidden_IHandler_");
			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("Composite_CompositeHandler_").And.Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_")
				.Because("the composite composes the public members before the handle of the internal one, as in the module's own assembly");
		}

		[Test]
		public async Task AComposite_InTheModulesAssembly_ComposesTheExactMembersBeforeTheVariantOne()
		{
			string library = Library(registrations: Registrations + "\n[Composite<CompositeHandler, IHandler<Derived>>]")
				.Replace("public interface IBrew { }", "public interface IBrew { } internal sealed class CompositeHandler : IHandler<Derived> { public CompositeHandler(IEnumerable<IHandler<Derived>> all) { } }");

			GeneratorResult result = Generator.Run(OwnAssembly(library));

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.CompositeHandler(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveSecretHandler(__s.__root) })");
		}

		[Test]
		public async Task ACovariantInterface_TakesThePublicMembersBeforeTheHandles()
		{
			string library = Library(
					registrations: "[Singleton<BaseProducer, IProducer<Base>>]\n[Singleton<SecretProducer, IProducer<Secret>>]",
					handlers: "IEnumerable<IProducer<Base>> handlers")
				.Replace("public interface IBrew { }", "public interface IBrew { } public interface IProducer<out T> { } public class Base { } internal sealed class Secret : Base { } public sealed class BaseProducer : IProducer<Base> { } internal sealed class SecretProducer : IProducer<Secret> { }");

			GeneratorResult module = Generator.Run(library);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([library,], App);

			await That(module.Diagnostics).IsEmpty();
			await That(Module(module)).Contains("global::System.Collections.Generic.IEnumerable<global::Lib.IProducer<global::Lib.Base>> @awaiten__exact_handlers, global::System.Collections.Generic.IEnumerable<global::Lib.BrewModule.Internals.Hidden_IProducer_");
			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("(new global::Lib.IProducer<global::Lib.Base>[] { Root.ResolveBaseProducer(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IProducer_");
		}

		[Test]
		public async Task AnInjectedProperty_TakesThePublicMembersBeforeTheHandles()
		{
			string library = Library().Replace(
				"internal sealed class Reader : IReader { public Reader(IEnumerable<IHandler<Derived>> handlers) { } }",
				"internal sealed class Reader : IReader { public Reader(IEnumerable<IHandler<Derived>> handlers) { } [Inject] public IEnumerable<IHandler<Derived>> Handlers { get; set; } = null!; [Inject(Optional = true)] public IEnumerable<IHandler<Derived>> Spare { get; set; } = null!; }");

			GeneratorResult module = Generator.Run(library);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([library,], App);

			await That(module.Diagnostics).IsEmpty();
			await That(Module(module)).Contains("_FirstProperty = 2;")
				.Because("the public collection the constructor takes is a parameter of its own before the first property");
			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_").Exactly(4)
				.Because("the container resolves the public collection of the constructor, of each property and of the decorator before its handles");
		}

		[Test]
		public async Task AHookParameter_TakesThePublicMembersBeforeTheHandles()
		{
			string library = Library()
				.Replace("[Singleton<Reader, IReader>]", "[Singleton<Reader, IReader>(OnActivated = nameof(Seen))]")
				.Replace("public static partial class BrewModule { }", "public static partial class BrewModule { internal static void Seen(Reader reader, IEnumerable<IHandler<Derived>> seen) { } }");

			GeneratorResult module = Generator.Run(library);
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([library,], App);

			await That(module.Diagnostics).IsEmpty();
			await That(Module(module)).Contains("global::System.Collections.Generic.IEnumerable<global::Lib.IHandler<global::Lib.Derived>> @awaiten__exact_seen, global::System.Collections.Generic.IEnumerable<global::Lib.BrewModule.Internals.Hidden_IHandler_");
			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_").Exactly(3)
				.Because("the hook takes the public members before the handles, like the constructor of the registration and of the decorator");
		}

		[Test]
		public async Task APublicVariantService_IsReportedInTheModulesBuild()
		{
			GeneratorResult result = Generator.Run(Library(registrations: Registrations + "\n[Singleton<ObjectHandler, IHandler<object>>]"));

			await That(result.Diagnostics).Contains("*error AWT237: *'Lib.Reader'*takes the internal service 'Lib.IHandler<Lib.ISecret>' as 'System.Collections.Generic.IEnumerable<Lib.IHandler<Lib.Derived>>'*").AsWildcard()
				.Because("a container of the module's assembly orders the public and the internal variant services by registration, which the factory cannot match when it takes them apart");
		}

		[Test]
		public async Task APublicVariantService_GivesTheDecoratorNoFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library(registrations: Registrations + "\n[Singleton<ObjectHandler, IHandler<object>>]").Replace("[Singleton<Reader, IReader>]", ""),], App);

			await That(result.Diagnostics).HasSingle().Which.Contains("error AWT230: The imported module 'Lib.BrewModule' declares the decorator 'Lib.Counting'");
		}

		[Test]
		public async Task APublicVariantServiceNextToAnInternalExactService_IsReportedInTheModulesBuild()
		{
			GeneratorResult result = Generator.Run(Library(
				registrations: "[Singleton<SubHandler, IHandler<SubDerived>>]\n[Singleton<DerivedHandler, IHandler<Derived>>]",
				handlers: "IEnumerable<IHandler<SubDerived>> handlers"));

			await That(result.Diagnostics).Contains("*error AWT237: *'Lib.Reader'*takes the internal service 'Lib.IHandler<Lib.SubDerived>' as 'System.Collections.Generic.IEnumerable<Lib.IHandler<Lib.SubDerived>>'*").AsWildcard()
				.Because("the handles of the internal exact service leave out the public variant one");
		}

		[Test]
		public async Task AnImplementationOfTheExactAndTheVariantService_IsReportedInTheModulesBuild()
		{
			GeneratorResult result = Generator.Run(Library(registrations: "[Singleton<BothHandler, IHandler<Derived>>]\n[Singleton<BothHandler, IHandler<ISecret>>]"));

			await That(result.Diagnostics).Contains("*error AWT237: *'Lib.Reader'*takes the internal service 'Lib.IHandler<Lib.ISecret>'*").AsWildcard()
				.Because("a container of the module's assembly holds the one instance once, while the public members and the handles would hold it twice");
		}
	}
}
