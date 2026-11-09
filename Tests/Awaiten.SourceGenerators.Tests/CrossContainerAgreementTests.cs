using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A module of a library read by a container in the library's own assembly, by one in another assembly, and by one
///     the library grants <c>InternalsVisibleTo</c>: each reports the same precise diagnostic once, in the words of what
///     the user declared, and none drops a registration silently.
/// </summary>
public class CrossContainerAgreementTests
{
	private static string Friend(string library)
		=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

	private static string ContainerSource(GeneratorResult result, string container)
		=> result.Sources.Single(source => source.Key.EndsWith($".{container}.g.cs", StringComparison.Ordinal)).Value;

	public class GeneratedMemberNames
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IGreeter { }
			public interface IOther { }
			public sealed class Greeter : IGreeter, IOther { }

			[Module]
			[Singleton<Greeter, IGreeter>(Factory = nameof(Make))]
			public static partial class CoffeeModule
			{
				internal static Greeter Make() => new Greeter();
			}
			""";

		[Test]
		public async Task AConflictWithAWrappedFactory_NamesTheUsersMemberInEveryContainer()
		{
			GeneratorResult same = Generator.Run(Library + """

				[Container]
				[Import(typeof(CoffeeModule))]
				[Singleton<Greeter, IOther>]
				public static partial class SameContainer;
				""");
			const string consumer = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[Singleton<Greeter, IOther>]
				public static partial class MyContainer;
				""";
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library, consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(Library), consumer);

			await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.CoffeeModule.Make'"));
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.CoffeeModule.Make'"))
				.Because("the container calls the module's generated wrapper, but the user declared the member the wrapper calls");
			await That(friend.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.CoffeeModule.Make'"));
		}
	}

	public class UnbuildableInternalService
	{
		private const string PrivateImplementation = """
			using Awaiten;

			namespace Lib;

			public interface IGreeter { }
			internal interface ISecret { }
			internal interface IPot { }
			internal sealed class Secret : ISecret { public Secret(System.Func<IPot> pot) { } }

			[Module]
			[Singleton<Secret, ISecret>]
			[Singleton<CoffeeModule.Greeter, IGreeter>]
			public static partial class CoffeeModule
			{
				private sealed class Greeter : IGreeter { public Greeter(ISecret secret) { } }
			}
			""";

		[Test]
		public async Task OfAPrivateImplementation_NamesTheServiceTheModuleCannotBuild()
		{
			GeneratorResult library = Generator.Run(PrivateImplementation);
			GeneratorResult friend = Generator.Run(Friend(PrivateImplementation));

			await That(library.Diagnostics).Contains("*AWT195*'Lib.CoffeeModule.Greeter'*its constructor parameter 'secret' needs the internal service 'Lib.ISecret', which the module cannot build*").AsWildcard()
				.Because("the private implementation fails only because of the service, whose own cause the module reports, not because the service is internal");
			await That(friend.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT195") && diagnostic.Contains("needs the internal service 'Lib.ISecret'"))
				.Because("under InternalsVisibleTo the parameter type is accessible to the friends, so the message does not claim it is not");
			await That(library.Diagnostics).DoesNotContain("*'Lib.ISecret', which is not accessible outside the module's assembly*").AsWildcard();
		}

		private const string HookParameter = """
			using Awaiten;

			namespace Lib;

			public interface IGreeter { }
			internal interface ISecret { }
			internal interface IPot { }
			internal sealed class Secret : ISecret { public Secret(System.Func<IPot> pot) { } }
			internal sealed class Greeter : IGreeter { }

			[Module]
			[Singleton<Secret, ISecret>]
			[Singleton<Greeter, IGreeter>(OnActivated = nameof(Activate))]
			public static partial class CoffeeModule
			{
				internal static void Activate(Greeter greeter, ISecret secret) { }
			}
			""";

		[Test]
		public async Task AsAHookParameter_IsReportedOnceForItsCause()
		{
			const string consumer = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""";
			GeneratorResult library = Generator.Run(HookParameter);
			GeneratorResult same = Generator.Run(HookParameter + """

				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class SameContainer;
				""");
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(HookParameter, consumer);

			await That(library.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT195") && diagnostic.Contains("'Lib.Secret'"))
				.Because("the hook fails only because the module cannot build the secret, which it reports once where it arises");
			await That(same.Diagnostics).DoesNotContain("*AWT203*").AsWildcard();
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("needs the internal service 'Lib.ISecret', which the module cannot build"))
				.Because("the container names why the module generated no hook wrapper instead of claiming a hook it cannot find (AWT164)");
		}
	}

	public class StartedInternalSingleton
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			internal interface ISecret { }
			internal sealed class Secret : ISecret { }

			[Module]
			[Singleton<Secret, ISecret>(Eager = true)]
			public static class CoffeeModule { }
			""";

		private const string Consumer = """
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		[Test]
		public async Task WithoutAnyGrant_DoesNotBlameInternalsVisibleTo()
		{
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer);

			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("because its build reported an error for the module or ran without the Awaiten generator"))
				.Because("the module's assembly grants no InternalsVisibleTo, so it left the singleton to no friend; its own build reported AWT194");
		}

		[Test]
		public async Task WithAGrantToAnotherAssembly_NamesTheModuleThatIsNotPartial()
		{
			string granted = Library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Friend\")]\nnamespace Lib;");

			await That(Generator.Run(granted).Diagnostics).IsEmpty()
				.Because("the module leaves its internal services to the assemblies it grants InternalsVisibleTo, so the library builds");
			await That(Generator.RunWithGeneratedReferencedAssembly(granted, Consumer).Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT231") && diagnostic.Contains("because the module, or a class that contains it, is not partial"));
		}
	}

	public class ImplementationOfTwoModules
	{
		private static string Library() => """
			using Awaiten;

			namespace Lib;

			public interface IGreeter { }
			public interface IOther { }
			public interface IClock { }
			public sealed class SystemClock : IClock { }
			public sealed class Greeter : IGreeter, IOther
			{
				[Inject] internal IClock Clock { get; set; } = null!;
			}

			[Module]
			[Singleton<Greeter, IGreeter>]
			[Singleton<SystemClock, IClock>]
			public static partial class CoffeeModule { }

			[Module]
			[Singleton<Greeter, IOther>]
			public static partial class OtherModule { }
			""";

		private const string Consumer = """
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Container]
			[Import(typeof(CoffeeModule))]
			[Import(typeof(OtherModule))]
			public static partial class MyContainer;
			""";

		[Test]
		public async Task WithAHiddenInjectedProperty_IsOneInstanceInEveryContainer()
		{
			GeneratorResult same = Generator.Run(Library() + """

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(OtherModule))]
				public static partial class SameContainer;
				""");
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(Library()), Consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("both modules generate the same factory for the implementation, which a container in their assembly constructs once");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(external, "MyContainer").Split(["Registration_Greeter_"], StringSplitOptions.None).Length - 1).IsEqualTo(1)
				.Because("one singleton serves both services, so one factory call builds it");
		}

		[Test]
		public async Task SharedThroughAnInstanceHandle_ReportsAContainerSideEntryInsteadOfDroppingIt()
		{
			string library = Library()
				.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface IKettle { }\ninternal sealed class Kettle : IKettle { }\ninternal interface IPot { }\ninternal sealed class Pot : IPot { }")
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "[Inject] internal IClock Clock { get; set; } = null!;\n    public IClock Backup { get; set; } = null!;")
				.Replace("public static partial class CoffeeModule", "[Singleton<Kettle, IKettle>]\npublic static partial class CoffeeModule")
				.Replace("public static partial class OtherModule", "[Singleton<Pot, IPot>]\npublic static partial class OtherModule");
			string consumer = Consumer.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup))]\npublic static partial class MyContainer;");

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), consumer);

			await That(Generator.Run(library).Sources.Values).Contains("*HiddenRegistration_Greeter_*").AsWildcard()
				.Because("the modules build the implementation into a shared instance handle");
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT178") && diagnostic.Contains("'Backup'") && diagnostic.Contains("share one instance of it through a generated handle"))
				.Because("the handle builds the implementation inside the module, so the container reports the entry instead of warning that nothing registers the type and leaving it out");
			await That(friend.Diagnostics).IsEmpty()
				.Because("a friend constructs the implementation itself and fills the entry");
		}

		/// <summary>The library with an internal service in <c>CoffeeModule</c> only, so only that module builds the implementation through a handle.</summary>
		private static string OneModuleWithAHandle() => Library()
			.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface IKettle { }\ninternal sealed class Kettle : IKettle { }")
			.Replace("public static partial class CoffeeModule", "[Singleton<Kettle, IKettle>]\npublic static partial class CoffeeModule");

		[Test]
		public async Task SharedThroughTheHandleOfOneModule_HasItsInjectedPropertySetByTheHandle()
		{
			string library = OneModuleWithAHandle();

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, Consumer);

			await That(Generator.Run(library).Sources.Values).Contains("*HiddenRegistration_Greeter_*(global::Lib.IClock @Clock) => new *(new global::Lib.Greeter() { Clock = @Clock });*").AsWildcard()
				.Because("the factory of the handle sets the internal property on the instance it builds");
			await That(external.Diagnostics).IsEmpty();
			await That(reference.Diagnostics).IsEmpty();
			await That(ContainerSource(external, "MyContainer")).Contains("Internals.Alias_Greeter_").And.DoesNotContain(" Clock = ").And.DoesNotContain(".Clock = ")
				.Because("the other module's registration shares the handle's instance through an alias, and the container sets nothing on it, so the property is set once");
		}

		[Test]
		[Arguments("[Import(typeof(OtherModule))]")]
		[Arguments("[Singleton<Greeter, IOther>]")]
		[Arguments("[Singleton<Greeter>]")]
		[Arguments("[Scan<IOther>(InAssembliesOf = new[] { typeof(IOther) }, As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]")]
		public async Task SharedThroughTheHandleOfOneModule_ReportsAContainerSideEntryInsteadOfDroppingIt(string registration)
		{
			string library = OneModuleWithAHandle()
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "[Inject] internal IClock Clock { get; set; } = null!;\n    public IClock Backup { get; set; } = null!;");
			string container = $$"""

				[Container]
				[Import(typeof(CoffeeModule))]
				{{registration}}
				[InjectProperty<Greeter>(nameof(Greeter.Backup))]
				public static partial class MyContainer;
				""";
			string consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n" + container;

			GeneratorResult same = Generator.Run(library + container);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), consumer);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(ContainerSource(same, "MyContainer")).Contains("Backup = ");
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(friend, "MyContainer")).Contains("Backup = ")
				.Because("a friend constructs the implementation itself and fills the entry");
			await That(ContainerSource(Generator.RunWithGeneratedReferencedAssembly(library, consumer.Replace("[InjectProperty<Greeter>(nameof(Greeter.Backup))]", string.Empty)), "MyContainer")).Contains("Internals.Alias_Greeter_")
				.Because("only the module that declares something internal builds the implementation into an instance handle, which the other registration shares through an alias");
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT178") && diagnostic.Contains("'Backup'") && diagnostic.Contains("share one instance of it through a generated handle"))
				.Because("the alias returns the implementation, but the handle builds it inside the module, where no container-side entry reaches");
			await That(reference.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT178") && diagnostic.Contains("'Backup'") && diagnostic.Contains("share one instance of it through a generated handle"));
		}

		[Test]
		public async Task HandledForAnInternalServiceOfOneModule_NamesThatServiceAsTheReason()
		{
			string library = Library()
				.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface ISecret { }")
				.Replace("public sealed class Greeter : IGreeter, IOther", "public sealed class Greeter : IGreeter, IOther, ISecret")
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "public IClock Backup { get; set; } = null!;")
				.Replace("[Singleton<SystemClock, IClock>]", "[Singleton<SystemClock, IClock>]\n[Singleton<Greeter, ISecret>]");
			string consumer = Consumer
				.Replace("[Import(typeof(OtherModule))]", string.Empty)
				.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup))]\npublic static partial class MyContainer;");

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT178") && diagnostic.Contains("registered under the internal service 'Lib.ISecret'"))
				.Because("one module builds the implementation through a handle for its internal service, so no other module shares it and the reason names that service");
		}

		[Test]
		public async Task ProducedDifferently_NamesTheConstructorInEveryContainer()
		{
			string library = Library()
				.Replace("[Singleton<Greeter, IOther>]", "[Singleton<Greeter, IOther>(Factory = nameof(Create))]")
				.Replace("public static partial class OtherModule { }", "public static partial class OtherModule { public static Greeter Create() => new Greeter(); }");

			GeneratorResult same = Generator.Run(library + """

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(OtherModule))]
				public static partial class SameContainer;
				""");
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

			await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("(a constructor and factory 'Lib.OtherModule.Create')"));
			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("(a constructor and factory 'Lib.OtherModule.Create')"))
				.Because("the module's generated factory stands for the constructor the user registered");
		}
	}
}
