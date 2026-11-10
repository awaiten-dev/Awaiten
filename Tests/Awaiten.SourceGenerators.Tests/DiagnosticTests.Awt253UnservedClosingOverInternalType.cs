namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt253UnservedClosingOverInternalType
	{
		private const string Prefix = "error AWT253: 'Lib.IBrew' cannot be resolved: 'Lib.Brew' requires 'Lib.ILog<Lib.Brew>', a closing over a type internal to the assembly of the module 'Lib.CoffeeModule', which this container cannot close itself, and ";

		private const string Suffix = "; or grant this assembly InternalsVisibleTo in the module's assembly";

		private static string Library(string registrations = "", string members = "", string parameter = "ILog<Brew> log") => $$"""
			using Awaiten;

			namespace Lib;

			public interface ILog<T> { }
			public sealed class Log<T> : ILog<T> { }
			public sealed class LoggedLog<T> : ILog<T> { public LoggedLog(ILog<T> inner) { } }
			public interface IBrew { }
			internal sealed class Brew : IBrew { public Brew({{parameter}}) { } }

			[Module]
			{{registrations}}
			[Transient<Brew, IBrew>]
			public static partial class CoffeeModule { {{members}} }

			[Module]
			[Singleton(typeof(Log<>), typeof(ILog<>))]
			public static partial class LogModule { }
			""";

		private static string Consumer(string attributes = "") => $$"""
			using Awaiten;

			namespace App;

			[Container]
			[Import(typeof(Lib.CoffeeModule))]
			{{attributes}}
			public static partial class Shop;
			""";

		[Test]
		public async Task NothingProvidesTheDefinition_ReportsAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer());

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*{Prefix}neither the container nor an imported module registers or imports 'Lib.ILog<>'; import it from the host with [ImportService(typeof(Lib.ILog<>))], or register it in the module 'Lib.CoffeeModule'{Suffix}").AsWildcard()
				.Because("the container names the closing and its definition instead of the generated handle (AWT101)");
		}

		[Test]
		public async Task TheContainerRegistersTheDefinition_ReportsAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer("[Singleton(typeof(Lib.Log<>), typeof(Lib.ILog<>))]"));

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*{Prefix}'Lib.ILog<>' is registered by the container, which cannot close it over that type; register 'Lib.ILog<>' in the module 'Lib.CoffeeModule' instead, or import it from the host with [ImportService(typeof(Lib.ILog<>))]{Suffix}").AsWildcard()
				.Because("the container would have to name Lib.Brew to close its own registration");
		}

		[Test]
		public async Task TheContainersRegistrationReplacesTheModules_ReportsAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library(registrations: "[Singleton(typeof(Log<>), typeof(ILog<>), Fallback = Fallback.Silent)]"),
				Consumer("[Singleton(typeof(Lib.Log<>), typeof(Lib.ILog<>))]"));

			await That(result.Diagnostics).HasSingle().Which.Contains($"*{Prefix}'Lib.ILog<>' is registered by the container, which cannot close it over that type;*").AsWildcard()
				.Because("the container's registration wins over the module's overridable default, so the module's closing would be the wrong one");
		}

		[Test]
		public async Task AnotherModuleRegistersTheDefinition_ReportsAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer("[Import(typeof(Lib.LogModule))]"));

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*{Prefix}'Lib.ILog<>' is registered by the module 'Lib.LogModule', which cannot close it over that type; register 'Lib.ILog<>' in the module 'Lib.CoffeeModule' instead, or import it from the host with [ImportService(typeof(Lib.ILog<>))]{Suffix}").AsWildcard()
				.Because("only the module whose member takes the closing closes its own registration");
		}

		[Test]
		public async Task TheModuleCannotCloseItsRegistration_ReportsTheRecordedReason()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library(registrations: "[Singleton(typeof(Log<>), typeof(ILog<>), OnRelease = nameof(Flush))]", members: "internal static void Flush<T>(Log<T> log) { }"),
				Consumer());

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*{Prefix}the module registers 'Lib.ILog<>', but generated no factory that closes it over that type because its registration names the OnRelease hook 'Flush', which the factory cannot run; import 'Lib.ILog<>' from the host with [ImportService(typeof(Lib.ILog<>))]{Suffix}").AsWildcard();
		}

		[Test]
		public async Task TheContainerDecoratesTheDefinition_ReportsAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library(registrations: "[Singleton(typeof(Log<>), typeof(ILog<>))]"),
				Consumer("[Decorate(typeof(Lib.LoggedLog<>), typeof(Lib.ILog<>))]"));

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*{Prefix}a decorator or composite of 'Lib.ILog<>' cannot apply to that closing; remove it, or import 'Lib.ILog<>' from the host with [ImportService(typeof(Lib.ILog<>))]{Suffix}").AsWildcard()
				.Because("the container would silently skip its decorator on the closing it cannot name");
		}

		[Test]
		[Arguments("", "neither the container nor an imported module registers or imports 'Lib.ILog<>'")]
		[Arguments("[Singleton(typeof(Lib.Log<>), typeof(Lib.ILog<>))]", "'Lib.ILog<>' is registered by the container, which cannot close it over that type")]
		public async Task ADecoratorPredicateTakingAnUnservedClosing_ReportsAwt253(string registrations, string reason)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library(parameter: "", members: "internal static bool IsFancy(ILog<Brew> log) => true;")
					.Replace("[Transient<Brew, IBrew>]", "[Transient<Brew, IBrew>][Decorate<FancyBrew, IBrew>(When = nameof(IsFancy))]")
					.Replace("public interface IBrew { }", "public interface IBrew { }\npublic sealed class FancyBrew : IBrew { public FancyBrew(IBrew inner) { } }"),
				Consumer(registrations));

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*error AWT253: 'Lib.IBrew' cannot be resolved: 'Lib.CoffeeModule.IsFancy' requires 'Lib.ILog<Lib.Brew>', a closing over a type internal to the assembly of the module 'Lib.CoffeeModule', which this container cannot close itself, and {reason}*").AsWildcard()
				.Because("the predicate takes the closing through its handle, so the container names why it cannot serve it, as for a constructor (AWT101)");
		}

		[Test]
		public async Task AKeyedClosingTheContainerDoesNotImport_ReportsAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				Library(registrations: "[Singleton(typeof(Log<>), typeof(ILog<>))]", parameter: "[FromKey(\"audit\")] ILog<Brew> log"),
				Consumer());

			await That(result.Diagnostics).HasSingle().Which.Contains(
				"*error AWT253: 'Lib.IBrew' cannot be resolved: 'Lib.Brew' requires 'Lib.ILog<Lib.Brew> (key: audit)', a closing over a type internal to the assembly of the module 'Lib.CoffeeModule', which this container cannot close itself, and the container builds such a closing only from the module's unkeyed open registration of 'Lib.ILog<>', so it serves a keyed one only from its host; import 'Lib.ILog<>' from the host with [ImportService(typeof(Lib.ILog<>))]" + Suffix).AsWildcard()
				.Because("the module's unkeyed registration does not serve a keyed closing");
		}

		[Test]
		[Arguments("full")]
		[Arguments("reference")]
		[Arguments("compilation")]
		public async Task OnlyAConditionalRegistrationOfTheDefinition_ReportsAwt253(string reference)
		{
			string library = Library(registrations: "[Singleton(typeof(Log<>), typeof(ILog<>), WhenInjectedInto = typeof(LoggedLog<>))]");
			GeneratorResult result = reference switch
			{
				"reference" => Generator.RunWithGeneratedReferenceAssembly(library, Consumer()),
				"compilation" => Generator.RunWithGeneratedCompilationReference(library, Consumer()),
				_ => Generator.RunWithGeneratedReferencedAssembly(library, Consumer()),
			};

			await That(result.Diagnostics).HasSingle().Which.Contains(
				$"*{Prefix}'Lib.ILog<>' is registered only with WhenInjectedInto, and the container builds such a closing only from an unconditional open registration of 'Lib.ILog<>' in the module 'Lib.CoffeeModule'; register it there without WhenInjectedInto, or import it from the host with [ImportService(typeof(Lib.ILog<>))]{Suffix}").AsWildcard()
				.Because("the conditional registration does not serve the closing, so the container names it instead of claiming nothing registers the definition");
		}

		[Test]
		public async Task TheContainerImportsTheDefinition_DoesNotReportAwt253()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer("[ImportService(typeof(Lib.ILog<>))]"));

			await That(result.Diagnostics).IsEmpty();
		}
	}
}
