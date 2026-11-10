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
		public async Task SharedThroughAnInstanceHandle_SetsAContainerSideEntryThroughTheHandle()
		{
			string library = Library()
				.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface IKettle { }\ninternal sealed class Kettle : IKettle { }\ninternal interface IPot { }\ninternal sealed class Pot : IPot { }")
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "[Inject] internal IClock Clock { get; set; } = null!;\n    public IClock Backup { get; set; } = null!;")
				.Replace("public static partial class CoffeeModule", "[Singleton<Kettle, IKettle>]\npublic static partial class CoffeeModule")
				.Replace("public static partial class OtherModule", "[Singleton<Pot, IPot>]\npublic static partial class OtherModule");
			string consumer = Consumer.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup))]\npublic static partial class MyContainer;");

			GeneratorResult same = Generator.Run(library + consumer.Substring(consumer.IndexOf("[Container]", StringComparison.Ordinal)));
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), consumer);

			await That(Generator.Run(library).Sources.Values).Contains("*HiddenRegistration_Greeter_*").AsWildcard()
				.Because("the modules build the implementation into a shared instance handle");
			await That(same.Diagnostics).IsEmpty();
			await That(ContainerSource(same, "MyContainer")).Contains("Backup = ");
			await That(external.Diagnostics).IsEmpty()
				.Because("the container sets the entry on the instance the handle built, as a container of the module's own assembly sets it while constructing the implementation");
			await That(ContainerSource(external, "MyContainer")).Contains("*Internals.Alias_Greeter_*(__instance).Backup = __value0;*").AsWildcard()
				.Because("the container reaches the implementation through the module's alias that returns it from the handle");
			await That(reference.Diagnostics).IsEmpty();
			await That(ContainerSource(reference, "MyContainer")).Contains("*Internals.Alias_Greeter_*(__instance).Backup = __value0;*").AsWildcard();
			await That(friend.Diagnostics).IsEmpty()
				.Because("a friend constructs the implementation itself and fills the entry");
			await That(ContainerSource(friend, "MyContainer")).Contains("Backup = ");
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
		public async Task SharedThroughTheHandleOfOneModule_SetsAContainerSideEntryThroughTheHandle(string registration)
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
			await That(external.Diagnostics).IsEmpty()
				.Because("the container sets the entry through the alias on the instance the handle built, which the other registration shares");
			await That(ContainerSource(external, "MyContainer")).Contains("*Internals.Alias_Greeter_*(__instance).Backup = __value0;*").AsWildcard();
			await That(reference.Diagnostics).IsEmpty();
			await That(ContainerSource(reference, "MyContainer")).Contains("*Internals.Alias_Greeter_*(__instance).Backup = __value0;*").AsWildcard();
		}

		[Test]
		[Arguments("")]
		[Arguments("[Import(typeof(OtherModule))]")]
		[Arguments("[Singleton<Brew<int>>]")]
		public async Task AClosingSharedThroughTheHandleOfOneModule_SetsAnOpenGenericEntryThroughTheHandle(string registration)
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public sealed class SystemClock : IClock { }
				public interface IBrew { }
				public interface IOtherBrew { }
				internal interface IKettle { }
				internal sealed class Kettle : IKettle { }
				public sealed class Brew<T> : IBrew, IOtherBrew
				{
					[Inject] internal IClock Clock { get; set; } = null!;
					public IClock Backup { get; set; } = null!;
				}

				[Module]
				[Singleton<Kettle, IKettle>]
				[Singleton<Brew<int>, IBrew>]
				[Singleton<SystemClock, IClock>]
				public static partial class CoffeeModule { }

				[Module]
				[Singleton<Brew<int>, IOtherBrew>]
				public static partial class OtherModule { }
				""";
			string container = $$"""

				[Container]
				[Import(typeof(CoffeeModule))]
				{{registration}}
				[InjectProperty(typeof(Brew<>), nameof(Brew<int>.Backup))]
				public static partial class MyContainer;
				""";
			string consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n" + container;

			GeneratorResult same = Generator.Run(library + container);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(ContainerSource(same, "MyContainer")).Contains("Backup = ");
			await That(external.Diagnostics).IsEmpty()
				.Because("the entry of the definition reaches the closing the handle builds, which the container sets through the module's alias");
			await That(ContainerSource(external, "MyContainer")).Contains("*Internals.Alias_Brew_*(__instance).Backup = __value0;*").AsWildcard();
			await That(reference.Diagnostics).IsEmpty();
			await That(ContainerSource(reference, "MyContainer")).Contains("*Internals.Alias_Brew_*(__instance).Backup = __value0;*").AsWildcard();
		}

		[Test]
		public async Task SharedThroughTheScanHandleOfAModule_SetsAContainerSideEntryThroughTheHandle()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGreeter { }
				public interface IOther { }
				public interface IClock { }
				public sealed class SystemClock : IClock { }
				public sealed class Greeter : IGreeter, IOther
				{
					public IClock Backup { get; set; } = null!;
				}

				[Module]
				[Scan<IOther>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
				[Singleton<SystemClock, IClock>]
				public static partial class CoffeeModule { }
				""";
			const string container = """

				[Container]
				[Import(typeof(CoffeeModule))]
				[Singleton<Greeter>]
				[InjectProperty<Greeter>(nameof(Greeter.Backup))]
				public static partial class MyContainer;
				""";
			string consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n" + container;

			GeneratorResult same = Generator.Run(library + container);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), consumer);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(ContainerSource(Generator.RunWithGeneratedReferencedAssembly(library, consumer.Replace("[InjectProperty<Greeter>(nameof(Greeter.Backup))]", string.Empty)), "MyContainer")).Contains("Internals.Alias_Greeter_")
				.Because("the module's scan builds the match it exposes under two services into an instance handle, which the container's registration shares through an alias");
			await That(external.Diagnostics).IsEmpty()
				.Because("the container sets the entry through the scan's alias on the instance the scan handle built");
			await That(ContainerSource(external, "MyContainer")).Contains("*Internals.Alias_Greeter_*(__instance).Backup = __value0;*").AsWildcard();
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);
			await That(reference.Diagnostics).IsEmpty();
			await That(ContainerSource(reference, "MyContainer")).Contains("*Internals.Alias_Greeter_*(__instance).Backup = __value0;*").AsWildcard();
		}

		[Test]
		public async Task DeferredEntrySharedThroughAnInstanceHandleAndImportedAlone_NamesTheOtherModuleAsTheReason()
		{
			string library = Library()
				.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface IKettle { }\ninternal sealed class Kettle : IKettle { }")
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "[Inject] internal IClock Clock { get; set; } = null!;\n    public IClock Backup { get; set; } = null!;")
				.Replace("public static partial class CoffeeModule", "[Singleton<Kettle, IKettle>]\npublic static partial class CoffeeModule");
			string consumer = Consumer
				.Replace("[Import(typeof(OtherModule))]", string.Empty)
				.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]\npublic static partial class MyContainer;");

			GeneratorResult same = Generator.Run(library + consumer.Substring(consumer.IndexOf("[Container]", StringComparison.Ordinal)));
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);
			GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), consumer);

			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty()
				.Because("a friend constructs the implementation itself and sets the deferred entry");
			await That(external.Diagnostics).HasSingle().Which.EndsWith("error AWT178: 'Lib.Greeter' is built by the generated factory of 'Lib.CoffeeModule' because the module 'Lib.OtherModule' of its assembly registers it too, and the modules share one instance of it through a generated handle, and the container sets no Deferred entry on the instance a generated handle holds, so its property 'Backup' cannot be filled by [InjectProperty], which applies only to a container-constructed instance; grant this assembly InternalsVisibleTo in the assembly of 'Lib.CoffeeModule', so the container constructs 'Lib.Greeter' itself")
				.Because("the container imports only the one module, so the reason names the other module that makes its assembly share the instance, and a deferred entry is set where no alias reaches");
			await That(reference.Diagnostics).HasSingle().Which.Contains("and the container sets no Deferred entry on the instance a generated handle holds");
		}

		[Test]
		public async Task RegisteredUnderSeveralPublicServicesAndSharedWithAnotherModule_NamesTheOtherModuleAsTheReason()
		{
			string library = Library()
				.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface ISecret { }")
				.Replace("public sealed class Greeter : IGreeter, IOther", "public sealed class Greeter : IGreeter, IOther, ISecret")
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "[Inject] internal IClock Clock { get; set; } = null!;\n    public IClock Backup { get; set; } = null!;")
				.Replace("[Singleton<Greeter, IOther>]", "[Singleton<Greeter, ISecret>]")
				.Replace("[Singleton<Greeter, IGreeter>]", "[Singleton<Greeter, IGreeter>]\n[Singleton<Greeter, IOther>]");
			string consumer = Consumer
				.Replace("[Import(typeof(OtherModule))]", string.Empty)
				.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]\npublic static partial class MyContainer;");

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(external.Diagnostics).HasSingle().Which.Contains("because the module 'Lib.OtherModule' of its assembly registers it too, and the modules share one instance of it through a generated handle, and the container sets no Deferred entry")
				.Because("a factory returning the public implementation would serve both services, so the module builds it into a handle only because the other module registers it under an internal service");
		}

		[Test]
		public async Task ProducedThroughAMemberAndSharedWithAnotherModule_IsAFactoryProducedTypeInEveryContainer()
		{
			string library = Library()
				.Replace("public interface IClock { }", "public interface IClock { }\ninternal interface ISecret { }")
				.Replace("public sealed class Greeter : IGreeter, IOther", "public sealed class Greeter : IGreeter, IOther, ISecret")
				.Replace("[Inject] internal IClock Clock { get; set; } = null!;", "public IClock Backup { get; set; } = null!;")
				.Replace("[Singleton<Greeter, IOther>]", "[Singleton<Greeter, ISecret>]")
				.Replace("[Singleton<Greeter, IGreeter>]", "[Singleton<Greeter, IGreeter>(Factory = nameof(Make))]")
				.Replace("public static partial class CoffeeModule { }", "public static partial class CoffeeModule { public static Greeter Make() => new(); }");
			const string expected = "error AWT178: 'Lib.Greeter' is produced by a Factory or Instance registration, so its property 'Backup' cannot be filled by [InjectProperty]; property injection applies only to a container-constructed instance";

			GeneratorResult same = Generator.Run(library + """

				[Container]
				[Import(typeof(CoffeeModule))]
				[InjectProperty<Greeter>(nameof(Greeter.Backup))]
				public static partial class SameContainer;
				""");
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer
				.Replace("[Import(typeof(OtherModule))]", string.Empty)
				.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup))]\npublic static partial class MyContainer;"));

			await That(same.Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module's member builds the instance whole in every assembly, so neither the handle nor an InternalsVisibleTo grant is the reason");
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
				.Replace("public static partial class MyContainer;", "[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]\npublic static partial class MyContainer;");

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(external.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT178") && diagnostic.Contains("registered under the internal service 'Lib.ISecret'") && diagnostic.Contains("Deferred"))
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

	public class EntryOnAnImplementationBuiltThroughAHandle
	{
		/// <summary>A public greeter the module registers under an internal service, so it builds it through an instance handle.</summary>
		private const string Library = """
			using Awaiten;

			namespace Lib
			{
				public interface IClock { }
				public sealed class SystemClock : IClock { }
				internal interface ISecret { }
				public sealed class Greeter : ISecret
				{
					[Inject] public IClock Clock { get; set; } = null!;
					[Inject] internal IClock Hidden { get; set; } = null!;
					public IClock Backup { get; set; } = null!;
					public IClock Frozen { get; init; } = null!;
					public IClock Spare { get; set; } = null!;
					public IClock Fixed => null!;
				}

				[Module]
				[Singleton<Greeter, ISecret>]
				[Singleton<SystemClock, IClock>]
				public static partial class CoffeeModule { }
			}
			""";

		private static string Granting(string library)
			=> library.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

		private static string Consumer(string entry) => $$"""
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Container]
			[Import(typeof(CoffeeModule))]
			{{entry}}
			public static partial class MyContainer;
			""";

		private static GeneratorResult Same(string library, string entry) => Generator.Run(library + $$"""

			namespace Lib
			{
				[Container]
				[Import(typeof(CoffeeModule))]
				{{entry}}
				public static partial class MyContainer;
			}
			""");

		[Test]
		public async Task ForAPublicInjectProperty_IsReportedAsDuplicateInEveryContainer()
		{
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Clock))]";

			await That(Same(Library, entry).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard()
				.Because("the handle's factory fills the [Inject] property, so the entry is redundant, as in the module's own assembly");
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
		}

		[Test]
		public async Task ForAnInternalInjectProperty_IsReportedAsDuplicateInEveryContainer()
		{
			const string entry = "[InjectProperty<Greeter>(\"Hidden\")]";

			await That(Same(Library, entry).Diagnostics).HasSingle().Which.Contains("*AWT181*'Hidden'*").AsWildcard();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Hidden'*").AsWildcard();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Hidden'*").AsWildcard()
				.Because("the handle's factory takes the internal [Inject] property as a parameter, which tells the container about a property it cannot see");
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Hidden'*").AsWildcard();
		}

		[Test]
		public async Task ForAnInitOnlyProperty_NamesTheModulesFactory()
		{
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Frozen))]";

			await That(Same(Library, entry).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which
				.EndsWith("error AWT178: 'Lib.Greeter' is built by the generated factory of 'Lib.CoffeeModule', so its init-only property 'Frozen' cannot be filled by [InjectProperty], since the container can only set it after the factory returned the instance; give the property a set accessor")
				.Because("the container sets the entry on the instance the handle holds, after its factory constructed it, as for any type a generated factory builds");
		}

		[Test]
		public async Task ThatIsOptionalWithoutARegistration_LeavesThePropertyAlone()
		{
			string library = Library.Replace("public IClock Backup { get; set; } = null!;", "public IClock Backup { get; set; } = null!;\n        public System.IFormatProvider Format { get; set; } = null!;");
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Format), Optional = true)]";

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, Consumer(entry));

			await That(Same(library, entry).Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(ContainerSource(external, "MyContainer")).DoesNotContain("Format = ");
		}

		[Test]
		public async Task OfAHandleBuiltFromRuntimeArguments_NamesTheArguments()
		{
			string library = Library
				.Replace("public IClock Frozen { get; init; } = null!;", "public IClock Frozen { get; init; } = null!;\n        public Greeter([Arg] string name) { }")
				.Replace("[Singleton<Greeter, ISecret>]", "[Transient<Greeter, ISecret>]");
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Backup))]";

			await That(Same(library, entry).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(library), Consumer(entry)).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer(entry)).Diagnostics).HasSingle().Which
				.EndsWith("error AWT178: 'Lib.Greeter' is built by the generated factory of 'Lib.CoffeeModule' because it is registered under the internal service 'Lib.ISecret', which a container in another assembly reaches only through a generated handle, and the handle builds it from runtime [Arg] arguments, so the container reaches the instance only through a factory of the handle, so its property 'Backup' cannot be filled by [InjectProperty], which applies only to a container-constructed instance; grant this assembly InternalsVisibleTo in the assembly of 'Lib.CoffeeModule', so the container constructs 'Lib.Greeter' itself")
				.Because("the alias of a handle built from runtime arguments takes a factory of the handle, not the handle the container built");
		}

		[Test]
		public async Task OfAModuleOfAnOlderGenerator_NamesTheMissingAlias()
		{
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Backup))]";
			string generated = Generator.Run(Library).Sources.Single(source => source.Key.StartsWith("Awaiten.ModuleScan.", StringComparison.Ordinal)).Value;
			await That(generated).Contains("public static global::Lib.Greeter Alias_Greeter_");
			string older = Library + "\n" + generated.Replace("public static global::Lib.Greeter Alias_Greeter_", "public static global::Lib.Greeter Renamed_Greeter_");

			await That(Generator.RunWithReferencedAssembly(Library + "\n" + generated, Consumer(entry)).Diagnostics).IsEmpty()
				.Because("the module built with the current generator returns the implementation from its handle");
			await That(Generator.RunWithReferencedAssembly(older, Consumer(entry)).Diagnostics).HasSingle().Which
				.Contains("*AWT178*because it is registered under the internal service 'Lib.ISecret'*, and the module was built by an older Awaiten generator, which generated no member that returns it from the handle, so its property 'Backup'*").AsWildcard()
				.Because("without the alias the container cannot reach the implementation the handle holds");
		}

		[Test]
		public async Task OfTheProducerOfOneOfSeveralModules_IsAFactoryProducedType()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public sealed class SystemClock : IClock { }
				public interface IBrew { IClock Backup { get; set; } }
				internal sealed class Brew : IBrew { public IClock Backup { get; set; } = null!; }
				internal interface IKettle { }
				internal sealed class Kettle : IKettle { }

				[Module]
				[Singleton<Kettle, IKettle>]
				[Singleton<SystemClock, IClock>]
				[Singleton<IBrew>(Factory = nameof(Make))]
				public static partial class FirstModule { internal static IBrew Make(IKettle kettle) => new Brew(); }

				[Module]
				[Singleton<IBrew>(Factory = nameof(Make))]
				public static partial class SecondModule { internal static IBrew Make(IKettle kettle) => new Brew(); }
				""";
			const string container = """

				[Container]
				[Import(typeof(FirstModule))]
				[Import(typeof(SecondModule))]
				[InjectProperty<IBrew>(nameof(IBrew.Backup))]
				public static partial class MyContainer;
				""";
			const string expected = "error AWT178: 'Lib.IBrew' is produced by a Factory or Instance registration, so its property 'Backup' cannot be filled by [InjectProperty]; property injection applies only to a container-constructed instance";

			GeneratorResult same = Generator.Run(library + container);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n" + container);

			await That(same.Diagnostics).Contains(diagnostic => diagnostic.EndsWith(expected, StringComparison.Ordinal));
			await That(external.Diagnostics).Contains(diagnostic => diagnostic.EndsWith(expected, StringComparison.Ordinal))
				.Because("each module's member produces the service whole, also when it builds it into a handle of its own");
			await That(external.Diagnostics).DoesNotContain("*AWT180*").AsWildcard();
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task OfADefinitionWithAClosingAFactoryBuildsIntoAHandle_ReportsThatClosingWhateverTheOrder(bool factoryFirst)
		{
			const string constructed = "[Singleton<Brew<int>, ISecret<int>>]";
			const string produced = "[Singleton<Brew<string>, ISecret<string>>(Factory = nameof(Make))]";
			string library = $$"""
				using Awaiten;

				namespace Lib
				{
					public interface IClock { }
					public sealed class SystemClock : IClock { }
					internal interface ISecret<T> { }
					public sealed class Brew<T> : ISecret<T> { public IClock Backup { get; set; } = null!; }

					[Module]
					{{(factoryFirst ? produced : constructed)}}
					{{(factoryFirst ? constructed : produced)}}
					[Singleton<SystemClock, IClock>]
					public static partial class CoffeeModule { internal static Brew<string> Make() => new(); }
				}
				""";
			const string entry = "[InjectProperty(typeof(Brew<>), nameof(Brew<int>.Backup))]";
			const string expected = "error AWT178: 'Lib.Brew<string>' is produced by a Factory or Instance registration, so its property 'Backup' cannot be filled by [InjectProperty]; property injection applies only to a container-constructed instance";

			await That(Same(library, entry).Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the closing the module's factory builds into its handle gets no entry, even when the other closing's handle takes it");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		[Arguments("nameof(Greeter.Clock)", "Clock")]
		[Arguments("\"Hidden\"", "Hidden")]
		public async Task ThatIsDeferredForAnInjectProperty_IsReportedAsDuplicateInEveryContainer(string name, string property)
		{
			string entry = $"[InjectProperty<Greeter>({name}, Deferred = true)]";
			string expected = $"warning AWT181: The property '{property}' on 'Lib.Greeter' carries [Inject] and also has an [InjectProperty] entry; the [Inject] member wins, so the entry's Optional/Deferred/Key are ignored - remove one";

			await That(Same(Library, entry).Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the [Inject] property wins in the module's own assembly too, so the entry is ignored and its Deferred flag never matters");
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		[Arguments("\"Missing\"", "Missing")]
		[Arguments("nameof(Greeter.Fixed)", "Fixed")]
		public async Task ThatIsDeferredForNoSettableProperty_IsReportedAsNotFoundInEveryContainer(string name, string property)
		{
			string entry = $"[InjectProperty<Greeter>({name}, Deferred = true)]";
			string expected = $"error AWT177: 'Lib.Greeter' has no settable property '{property}' for [InjectProperty] to fill; check the name (use nameof), and that it is a property with a set/init accessor rather than a field, method or read-only member";

			await That(Same(Library, entry).Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the entry names no settable property in any assembly, which is the mistake to fix before the handle matters");
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task ThatIsDeferredForASettableProperty_IsNotSetThroughTheHandle()
		{
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]";

			await That(Same(Library, entry).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which
				.Contains("*error AWT178: 'Lib.Greeter' is built by the generated factory of 'Lib.CoffeeModule' because it is registered under the internal service 'Lib.ISecret'*, and the container sets no Deferred entry on the instance a generated handle holds, so its property 'Backup'*").AsWildcard()
				.Because("a valid Deferred entry is the one a generated handle cannot take");
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT178*'Backup'*").AsWildcard();
		}

		[Test]
		public async Task ThatIsDeferredNextToAnotherEntry_IsReportedAloneWhileTheOtherIsSet()
		{
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]\n[InjectProperty<Greeter>(nameof(Greeter.Spare))]";

			await That(Same(Library, entry).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT178*'Backup'*").AsWildcard()
				.Because("only the Deferred entry is one the handle cannot take, the other is set through it");
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT178*'Backup'*").AsWildcard();
		}

		[Test]
		public async Task ThatIsDeferredForAnInjectPropertyNextToAnotherEntry_IsReportedAsDuplicateWhileTheOtherIsSet()
		{
			const string entry = "[InjectProperty<Greeter>(nameof(Greeter.Clock), Deferred = true)]\n[InjectProperty<Greeter>(nameof(Greeter.Spare))]";

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer(entry));

			await That(Same(Library, entry).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(Library), Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
			await That(external.Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
			await That(Generator.RunWithGeneratedReferenceAssembly(Library, Consumer(entry)).Diagnostics).HasSingle().Which.Contains("*AWT181*'Clock'*").AsWildcard();
			await That(ContainerSource(external, "MyContainer")).Contains("*Internals.Alias_Greeter_*Spare = *").AsWildcard();
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task ForAnInjectPropertyOfADefinitionWithAClosingAFactoryBuildsIntoAHandle_ReportsThatClosingToo(bool deferred)
		{
			const string library = """
				using Awaiten;

				namespace Lib
				{
					public interface IClock { }
					public sealed class SystemClock : IClock { }
					internal interface ISecret<T> { }
					public sealed class Brew<T> : ISecret<T> { [Inject] public IClock Clock { get; set; } = null!; }

					[Module]
					[Singleton<Brew<int>, ISecret<int>>]
					[Singleton<Brew<string>, ISecret<string>>(Factory = nameof(Make))]
					[Singleton<SystemClock, IClock>]
					public static partial class CoffeeModule { internal static Brew<string> Make() => new(); }
				}
				""";
			string entry = $"[InjectProperty(typeof(Brew<>), nameof(Brew<int>.Clock), Deferred = {(deferred ? "true" : "false")})]";

			foreach (GeneratorResult result in new[]
			         {
				         Same(library, entry),
				         Generator.RunWithGeneratedReferencedAssembly(Granting(library), Consumer(entry)),
				         Generator.RunWithGeneratedReferencedAssembly(library, Consumer(entry)),
				         Generator.RunWithGeneratedReferenceAssembly(library, Consumer(entry)),
			         })
			{
				await That(result.Diagnostics).HasCount(2);
				await That(result.Diagnostics).Contains("*warning AWT181: The property 'Clock' on 'Lib.Brew<int>' carries [Inject]*").AsWildcard();
				await That(result.Diagnostics).Contains("*error AWT178: 'Lib.Brew<string>' is produced by a Factory or Instance registration, so its property 'Clock' cannot be filled by [InjectProperty]*").AsWildcard()
					.Because("the closing the module's factory produces gets no entry, although the handle of the other closing reported it as a duplicate of its [Inject] property");
			}
		}

		[Test]
		public async Task ThatIsDeferredOnABaseOfTheModulesAssembly_IsReportedInTheExtendingContainer()
		{
			string library = Library + """

				namespace Lib
				{
					[Container]
					[Import(typeof(CoffeeModule))]
					[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]
					public static partial class Shop;
				}
				""";
			const string extending = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container(Extends = typeof(Shop))]
				public static partial class MyContainer;
				""";

			await That(Generator.Run(library + "\nnamespace Lib { [Container(Extends = typeof(Shop))] public static partial class MyContainer; }").Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Granting(library), extending).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, extending).Diagnostics).HasSingle().Which
				.Contains("*error AWT178: 'Lib.Greeter' is built by the generated factory of 'Lib.CoffeeModule'*, and the container sets no Deferred entry on the instance a generated handle holds, so its property 'Backup'*").AsWildcard()
				.Because("the base constructs the implementation and sets the entry, while the extending container builds it through the handle, which sets no Deferred entry");
			await That(Generator.RunWithGeneratedReferenceAssembly(library, extending).Diagnostics).HasSingle().Which.Contains("*AWT178*'Backup'*").AsWildcard();
		}

		[Test]
		public async Task ThatIsDeferredOnABaseOfTheContainersAssembly_IsReportedOnce()
		{
			const string consumer = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[InjectProperty<Greeter>(nameof(Greeter.Backup), Deferred = true)]
				public static partial class Shop;

				[Container(Extends = typeof(Shop))]
				public static partial class MyContainer;
				""";

			await That(Generator.RunWithGeneratedReferencedAssembly(Library, consumer).Diagnostics).HasSingle().Which.Contains("*AWT178*'Backup'*").AsWildcard()
				.Because("the base reports its own entry, so the extending container does not report it again");
		}
	}
}
