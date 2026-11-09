using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A module another assembly can name passes a closing of a public generic service over a type internal to its
///     assembly, such as <c>ILog&lt;Brew&gt;</c> for an internal <c>Brew</c>, on to a container in another assembly
///     through a generated closing handle. The container decides who closes the definition: it asks its host when it
///     imports the definition, and builds the closing through the module's factory when it keeps the module's own open
///     registration of it.
/// </summary>
public class ModuleClosingOverInternalTypeTests
{
	private const string Handle = "Closed_ILog_ac973d6c";

	private const string QualifiedHandle = "global::Lib.CoffeeModule.Internals." + Handle;

	private static string Library(string registrations = "[Singleton(typeof(Log<>), typeof(ILog<>))]", string members = "") => $$"""
		using Awaiten;

		namespace Lib;

		public interface ILog<T> { }
		public sealed class Log<T> : ILog<T>, System.IDisposable { public void Dispose() { } }
		public interface IBrew { }
		public interface ICup { }
		internal sealed class Brew : IBrew { public Brew(ILog<Brew> log) { } }
		internal sealed class Cup : ICup { public Cup(ILog<Brew> log) { } }

		[Module]
		{{registrations}}
		[Transient<Brew, IBrew>]
		[Transient<Cup, ICup>]
		public static partial class CoffeeModule { {{members}} }
		""";

	private static string Consumer(string attributes = "") => $$"""
		using Awaiten;

		namespace App;

		[Container]
		[Import(typeof(Lib.CoffeeModule))]
		{{attributes}}
		public static partial class Shop;
		""";

	private static string ModuleSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("ModuleScan")).Value;

	private static string ContainerSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("Shop")).Value;

	public class LibraryBuild
	{
		[Test]
		public async Task AConstructorTakingAClosingOverItself_TakesItsHandle()
		{
			GeneratorResult result = Generator.Run(Library());
			string module = ModuleSource(result);

			await That(result.Diagnostics).IsEmpty()
				.Because("a container in another assembly decides who closes ILog<>, so the library build rejects nothing");
			await That(module).Contains($"public static global::Lib.IBrew Registration_Brew_11926eed({QualifiedHandle} @log) => new global::Lib.Brew(@log.Value);")
				.Because("the factory takes the closing through its handle and passes on the instance it holds");
			await That(module).Contains($"public sealed class {Handle} : global::System.IDisposable")
				.Because("the closing the module builds is disposable, so the handle forwards the disposal");
			await That(module).Contains("public const string Definition = \"global::Lib.ILog<>\";");
			await That(module).Contains("public const string Closing = \"global::Lib.ILog<global::Lib.Brew>\";");
			await That(module).Contains("public static readonly global::System.Type ServiceType = typeof(global::Lib.ILog<global::Lib.Brew>);")
				.Because("a container that imports ILog<> asks its host for the closed type it cannot name");
			await That(module).Contains($"public static {Handle} FromImport(object instance) => new {Handle}((global::Lib.ILog<global::Lib.Brew>)instance);");
			await That(module).Contains($"public static {Handle} Registration_{Handle}() => new {Handle}(new global::Lib.Log<global::Lib.Brew>());")
				.Because("the module registers ILog<> itself, so it closes its own registration for a container that keeps it");
		}

		[Test]
		public async Task TwoMembersTakingOneClosing_ShareOneHandle()
		{
			GeneratorResult result = Generator.Run(Library());
			string module = ModuleSource(result);

			await That(module.Split([$"public sealed class {Handle}",], StringSplitOptions.None)).HasCount(2)
				.Because("Brew and Cup take the same closing, so a container registers one handle and shares its singleton");
			await That(module).Contains($"Registration_Cup_");
		}

		[Test]
		public async Task WithoutAnOpenRegistration_TheHandleHasNoFactory()
		{
			GeneratorResult result = Generator.Run(Library(registrations: ""));
			string module = ModuleSource(result);

			await That(result.Diagnostics).IsEmpty()
				.Because("a container in another assembly may import ILog<> from its host");
			await That(module).Contains($"public sealed class {Handle}");
			await That(module).DoesNotContain($"public sealed class {Handle} :")
				.Because("the module builds no closing, so the handle implements no disposal");
			await That(module).DoesNotContain($"Registration_{Handle}");
			await That(module).DoesNotContain("RegistrationSkipped");
		}

		[Test]
		public async Task AnOpenRegistrationWithAHook_RecordsWhyTheHandleHasNoFactory()
		{
			GeneratorResult result = Generator.Run(Library(
				registrations: "[Singleton(typeof(Log<>), typeof(ILog<>), OnActivated = nameof(Track))]",
				members: "internal static void Track<T>(Log<T> log) { }"));
			string module = ModuleSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(module).DoesNotContain($"Registration_{Handle}");
			await That(module).Contains("public const string RegistrationSkipped = \"its registration names the OnActivated hook 'Track', which the factory cannot run\";")
				.Because("a container that keeps the registration reports why it cannot build the closing");
		}

		[Test]
		public async Task ARelationshipOverTheClosing_StillReportsAwt195()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				public interface ILog<T> { }
				public interface IBrew { }
				internal sealed class Brew : IBrew { public Brew(System.Lazy<ILog<Brew>> log) { } }

				[Module]
				[Transient<Brew, IBrew>]
				public static partial class CoffeeModule { }
				""");

			await That(result.Diagnostics).HasSingle().Which.Contains("*error AWT195: 'Lib.Brew' is self-compiled from a module registration, but its constructor parameter 'log' has the type 'System.Lazy<Lib.ILog<Lib.Brew>>', which is not accessible outside the module's assembly*").AsWildcard()
				.Because("only a direct dependency on the closing goes through a handle");
		}

		[Test]
		public async Task AClosingOverATypePrivateToTheModule_StillReportsAwt195()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;

				namespace Lib;

				public interface ILog<T> { }
				public interface IBrew { }

				[Module]
				[Transient<CoffeeModule.Brew, IBrew>]
				public static partial class CoffeeModule
				{
					private sealed class Brew : IBrew { public Brew(ILog<Brew> log) { } }
				}
				""");

			await That(result.Diagnostics).HasSingle().Which.Contains("*error AWT195: 'Lib.CoffeeModule.Brew' is self-compiled from a module registration, but its constructor parameter 'log' needs 'Lib.CoffeeModule.Brew', which is private to the module or a class containing it*").AsWildcard()
				.Because("no container can close ILog<> over a type private to the module, not even one in its own assembly");
		}
	}

	public class HostImport
	{
		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		[Arguments("compilation")]
		public async Task AnOpenImport_AsksTheHostForTheClosing(string reference)
		{
			GeneratorResult result = Run(reference, Library(registrations: ""), Consumer("[ImportService(typeof(Lib.ILog<>))]"));
			string container = ContainerSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(container).Contains($"global::Lib.CoffeeModule.Internals.Registration_Brew_11926eed({QualifiedHandle}.FromImport(__s.__ResolveExternal({QualifiedHandle}.ServiceType, null)))")
				.Because("the container asks its external resolver for the closed type the handle records and wraps what it returns");
			await That(container).Contains($"new global::Awaiten.AwaitenExternalDependency({QualifiedHandle}.ServiceType, null)")
				.Because("the container advertises the closing, so a host verifies that it provides it");
		}

		[Test]
		public async Task TheBlanketImport_AsksTheHostWhenTheModuleRegistersNoDefinition()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(registrations: ""), Consumer("[ImportServices]"));

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result)).Contains($"{QualifiedHandle}.FromImport(__s.__ResolveExternal({QualifiedHandle}.ServiceType, null))");
		}

		[Test]
		public async Task AnOpenImport_WinsOverTheModulesOpenRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer("[ImportService(typeof(Lib.ILog<>))]"));
			string container = ContainerSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(container).Contains($"{QualifiedHandle}.FromImport(")
				.Because("an imported definition is host-owned, as it is for a closing the container can name");
			await That(container).DoesNotContain($"Registration_{Handle}(");
		}

		[Test]
		public async Task AKeyedClosing_ForwardsTheKey()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface ILog<T> { }
				public interface IBrew { }
				internal sealed class Brew : IBrew { public Brew([FromKey("audit")] ILog<Brew> log) { } }

				[Module]
				[Transient<Brew, IBrew>]
				public static partial class CoffeeModule { }
				""", Consumer("[ImportService(typeof(Lib.ILog<>))]"));

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result)).Contains($"{QualifiedHandle}.FromImport(__s.__ResolveExternal({QualifiedHandle}.ServiceType, \"audit\"))");
		}
	}

	public class ModuleRegistration
	{
		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		[Arguments("compilation")]
		public async Task TheModulesOpenRegistration_BuildsTheClosingThroughTheHandle(string reference)
		{
			GeneratorResult result = Run(reference, Library(), Consumer());
			string container = ContainerSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(container).Contains($"global::Lib.CoffeeModule.Internals.Registration_{Handle}()")
				.Because("the container keeps the module's registration of ILog<>, which the module closed in its own build");
			await That(container).Contains($"private volatile {QualifiedHandle}? _closed_ILog_ac973d6c;")
				.Because("the handle has the lifetime of the module's registration, a singleton");
			await That(container).Contains("Add(__s._closed_ILog_ac973d6c);")
				.Because("the container owns and disposes what the module's registration builds");
		}

		[Test]
		public async Task TheBlanketImport_KeepsTheModulesOpenRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer("[ImportServices]"));
			string container = ContainerSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(container).Contains($"Registration_{Handle}()")
				.Because("the blanket import serves only what nothing registers");
			await That(container).DoesNotContain($"{QualifiedHandle}.FromImport(");
		}

		[Test]
		public async Task TheClosingsDependencies_ResolveFromTheContainer()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public interface ILog<T> { }
				public sealed class Log<T> : ILog<T> { public Log(IClock clock) { } }
				public interface IBrew { }
				internal sealed class Brew : IBrew { public Brew(ILog<Brew> log) { } }

				[Module]
				[Scoped(typeof(Log<>), typeof(ILog<>))]
				[Transient<Brew, IBrew>]
				public static partial class CoffeeModule { }
				""", Consumer("[ImportService<Lib.IClock>]"));
			string container = ContainerSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(container).Contains($"Registration_{Handle}((global::Lib.IClock)__s.__ResolveExternal(typeof(global::Lib.IClock), null))")
				.Because("the factory mirrors the closed implementation's constructor, whose parameters the container resolves");
		}

		[Test]
		public async Task ACapturedScopedDependency_NamesTheClosing()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public sealed class Clock : IClock { }
				public interface ILog<T> { }
				public sealed class Log<T> : ILog<T> { public Log(IClock clock) { } }
				public interface IBrew { }
				internal sealed class Brew : IBrew { public Brew(ILog<Brew> log) { } }

				[Module]
				[Singleton(typeof(Log<>), typeof(ILog<>))]
				[Scoped<Clock, IClock>]
				[Transient<Brew, IBrew>]
				public static partial class CoffeeModule { }
				""", Consumer());

			await That(result.Diagnostics).HasSingle().Which.Contains("*AWT105*'Lib.ILog<Lib.Brew> (internal to module Lib.CoffeeModule)'*'Lib.IClock'*").AsWildcard()
				.Because("the handle has the lifetime of the open registration, so its dependencies are checked against it and named by the closing");
		}
	}

	public class OtherSetups
	{
		[Test]
		public async Task AScanMatch_TakesTheClosingThroughItsHandle()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface ILog<T> { }
				public interface IBrew { }
				internal sealed class Brew : IBrew { public Brew(ILog<Brew> log) { } }

				[Module]
				[Scan<IBrew>(As = ScanAs.MatchingInterface)]
				public static partial class CoffeeModule { }
				""", Consumer("[ImportService(typeof(Lib.ILog<>))]"));

			await That(result.Diagnostics).IsEmpty()
				.Because("a scan factory mirrors the constructor like a registration factory");
			await That(ContainerSource(result)).Contains($"{QualifiedHandle}.FromImport(__s.__ResolveExternal({QualifiedHandle}.ServiceType, null))");
		}

		[Test]
		public async Task AFriend_ConstructsTheImplementationItself()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library().Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"),
				Consumer());
			string container = ContainerSource(result);

			await That(result.Diagnostics).IsEmpty();
			await That(container).Contains("new global::Lib.Brew(")
				.Because("an assembly the library grants InternalsVisibleTo closes ILog<> over Brew itself");
			await That(container).DoesNotContain(Handle)
				.Because("such a container needs no handle");
		}

		[Test]
		public async Task AContainerInTheModulesAssembly_ConstructsTheImplementationItself()
		{
			GeneratorResult result = Generator.Run(Library() + """

				[Awaiten.Container]
				[Awaiten.Import(typeof(Lib.CoffeeModule))]
				public static partial class Shop;
				""");

			await That(result.Diagnostics).IsEmpty();
			await That(ContainerSource(result)).Contains("new global::Lib.Brew(");
		}
	}

	private static GeneratorResult Run(string reference, string library, string consumer)
		=> reference switch
		{
			"reference" => Generator.RunWithGeneratedReferenceAssembly(library, consumer),
			"compilation" => Generator.RunWithGeneratedCompilationReference(library, consumer),
			_ => Generator.RunWithGeneratedReferencedAssembly(library, consumer),
		};
}
