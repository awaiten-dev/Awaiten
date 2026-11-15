namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     A module registers public exact handlers and an internal handler that a collection of handlers of the public
	///     <c>Derived</c> holds through contravariance, so its generated factory takes the public collection and the
	///     handles apart. A second library, or the container, registers the public <c>IHandler&lt;object&gt;</c>, which
	///     the collection holds through variance as well.
	/// </summary>
	public class Awt265VariantCollectionOrderDiffers
	{
		private const string Shared = "namespace Lib; public interface IHandler<in T> { }";

		private const string Other = """
			using Awaiten;

			namespace Ext;

			public sealed class AnyHandler : Lib.IHandler<object> { }

			[Module]
			[Singleton<AnyHandler, Lib.IHandler<object>>]
			public static partial class AnyModule { }
			""";

		private const string Library = """
			using System.Collections.Generic;
			using Awaiten;

			namespace Lib;

			internal interface ISecret { }
			public class Derived : ISecret { }
			public sealed class DerivedHandler : IHandler<Derived> { }
			public sealed class OtherDerivedHandler : IHandler<Derived> { }
			internal sealed class SecretHandler : IHandler<ISecret> { }
			public interface IReader { }
			internal sealed class Reader : IReader { public Reader(IEnumerable<IHandler<Derived>> handlers) { } }

			[Module]
			[Singleton<DerivedHandler, IHandler<Derived>>]
			[Singleton<SecretHandler, IHandler<ISecret>>]
			[Singleton<OtherDerivedHandler, IHandler<Derived>>]
			[Singleton<Reader, IReader>]
			public static partial class BrewModule { }
			""";

		private const string Expected =
			"(7,2): error AWT265: 'Lib.Reader' takes a collection of 'Lib.IHandler<Lib.Derived>' that holds 'Ext.AnyHandler', which the module 'Ext.AnyModule' registers under 'Lib.IHandler<object>', before the registrations of 'Lib.IHandler<Lib.ISecret>' that 'Lib.BrewModule' serves through generated handles, while a container in the assembly of 'Lib.BrewModule' holds it after them; register 'Ext.AnyHandler' under 'Lib.IHandler<Lib.Derived>' instead, or import 'Ext.AnyModule' ahead of 'Lib.BrewModule'";

		private static string App(string attributes) => $$"""
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(Lib.BrewModule))]
			{{attributes}}
			public static partial class MyContainer;
			""";

		private static string OwnContainer(string attributes)
			=> Library + "\n[Container]\n" + attributes + "\npublic static partial class OwnContainer { }";

		private static string Container(GeneratorResult result) => result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		[Test]
		public async Task AModuleImportedAfterTheModule_IsReportedAtItsImport()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other, Library,], App("[Import(typeof(Ext.AnyModule))]"));
			GeneratorResult own = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other,], OwnContainer("[Import(typeof(Lib.BrewModule))]\n[Import(typeof(Ext.AnyModule))]"));

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("the container can pass 'AnyHandler' only before the handles of 'SecretHandler'");
			await That(own.Diagnostics).IsEmpty();
			await That(own.Sources["Awaiten.Lib.OwnContainer.g.cs"]).Contains("new global::Lib.Reader(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveSecretHandler(__s.__root), Root.ResolveAnyHandler(__s.__root) })")
				.Because("a container of the module's assembly orders the variant members by registration, and it imports the module first");
		}

		[Test]
		public async Task AgainstReferenceAssemblies_ReportsTheSame()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other, Library,], App("[Import(typeof(Ext.AnyModule))]"), referenceAssemblies: true);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected);
		}

		[Test]
		public async Task AModuleImportedAheadOfTheModule_BuildsTheSameOrder()
		{
			string app = App("").Replace("[Import(typeof(Lib.BrewModule))]", "[Import(typeof(Ext.AnyModule))]\n[Import(typeof(Lib.BrewModule))]");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other, Library,], app);
			GeneratorResult own = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other,], OwnContainer("[Import(typeof(Ext.AnyModule))]\n[Import(typeof(Lib.BrewModule))]"));

			await That(result.Diagnostics).IsEmpty()
				.Because("a container of the module's assembly with these imports holds 'AnyHandler' before 'SecretHandler' too");
			await That(Container(result)).Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveAnyHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
			await That(own.Diagnostics).IsEmpty();
			await That(own.Sources["Awaiten.Lib.OwnContainer.g.cs"]).Contains("new global::Lib.Reader(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveAnyHandler(__s.__root), Root.ResolveSecretHandler(__s.__root) })");
		}

		[Test]
		public async Task ARegistrationUnderTheElementType_BuildsTheSameOrder()
		{
			string other = Other
				.Replace("public sealed class AnyHandler : Lib.IHandler<object> { }", "public sealed class AnyHandler : Lib.IHandler<Lib.Derived> { }")
				.Replace("[Singleton<AnyHandler, Lib.IHandler<object>>]", "[Singleton<AnyHandler, Lib.IHandler<Lib.Derived>>]");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Library, other,], App("[Import(typeof(Ext.AnyModule))]"));

			await That(result.Diagnostics).IsEmpty()
				.Because("the collection holds 'AnyHandler' among the exact members, which lead in every assembly");
			await That(Container(result)).Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveAnyHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
		}

		[Test]
		public async Task AContainerRegistration_BuildsTheSameOrder()
		{
			const string registration = "[Singleton<Ext.AnyHandler, Lib.IHandler<object>>]";

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other, Library,], App(registration));
			GeneratorResult own = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other,], OwnContainer("[Import(typeof(Lib.BrewModule))]\n" + registration));

			await That(result.Diagnostics).IsEmpty()
				.Because("the container's own registrations precede those of its imports, so a container of the module's assembly holds 'AnyHandler' before 'SecretHandler' too");
			await That(Container(result)).Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveAnyHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
			await That(own.Diagnostics).IsEmpty();
			await That(own.Sources["Awaiten.Lib.OwnContainer.g.cs"]).Contains("new global::Lib.Reader(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveAnyHandler(__s.__root), Root.ResolveSecretHandler(__s.__root) })");
		}

		[Test]
		public async Task AContainerFallbackRegistration_IsReportedAtTheRegistration()
		{
			const string registration = "[Singleton<Ext.AnyHandler, Lib.IHandler<object>>(Fallback = Fallback.Silent)]";

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other, Library,], App(registration));
			GeneratorResult own = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other,], OwnContainer("[Import(typeof(Lib.BrewModule))]\n" + registration));

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(
					"(7,2): error AWT265: 'Lib.Reader' takes a collection of 'Lib.IHandler<Lib.Derived>' that holds 'Ext.AnyHandler', which the container registers under 'Lib.IHandler<object>', before the registrations of 'Lib.IHandler<Lib.ISecret>' that 'Lib.BrewModule' serves through generated handles, while a container in the assembly of 'Lib.BrewModule' holds it after them; register 'Ext.AnyHandler' under 'Lib.IHandler<Lib.Derived>' instead")
				.Because("an overridable default follows every registration without Fallback, whatever its position, so no order of the attributes puts it first");
			await That(own.Sources["Awaiten.Lib.OwnContainer.g.cs"]).Contains("new global::Lib.Reader(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveSecretHandler(__s.__root), Root.ResolveAnyHandler(__s.__root) })");
		}

		[Test]
		public async Task AContainerRegistrationUnderTheElementType_BuildsTheSameOrder()
		{
			const string registration = "[Singleton<Ext.AnyHandler, Lib.IHandler<Lib.Derived>>]";

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other, Library,], App(registration));
			GeneratorResult own = Generator.RunWithGeneratedReferencedAssemblyChain([Shared, Other,], OwnContainer("[Import(typeof(Lib.BrewModule))]\n" + registration));

			await That(result.Diagnostics).IsEmpty()
				.Because("the collection holds 'AnyHandler' among the exact members, which lead in every assembly");
			await That(Container(result)).Contains("(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveAnyHandler(__s.__root), Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root) }, new global::Lib.BrewModule.Internals.Hidden_IHandler_");
			await That(own.Sources["Awaiten.Lib.OwnContainer.g.cs"]).Contains("new global::Lib.Reader(new global::Lib.IHandler<global::Lib.Derived>[] { Root.ResolveAnyHandler(__s.__root), Root.ResolveDerivedHandler(__s.__root), Root.ResolveOtherDerivedHandler(__s.__root), Root.ResolveSecretHandler(__s.__root) })");
		}
	}
}
