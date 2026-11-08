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

			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.CoffeeModule.Make'"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.CoffeeModule.Make'"))
				.Because("the container calls the module's generated wrapper, but the user declared the member the wrapper calls");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.CoffeeModule.Make'"));
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

			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("(a constructor and factory 'Lib.OtherModule.Create')"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("(a constructor and factory 'Lib.OtherModule.Create')"))
				.Because("the module's generated factory stands for the constructor the user registered");
		}
	}
}
