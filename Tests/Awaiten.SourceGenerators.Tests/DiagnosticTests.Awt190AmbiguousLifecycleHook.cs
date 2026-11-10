using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt190AmbiguousLifecycleHook
	{
		[Test]
		public async Task ReportsWhenTwoOverloadsBothAcceptTheInstance()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }
			                                       public sealed class Settings { }

			                                       [Container]
			                                       [Singleton<Settings>]
			                                       [Singleton<Service>(OnActivated = nameof(Started))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Started(Service service) { }
			                                       	private static void Started(Service service, Settings settings) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT190*").AsWildcard()
				.Because("two accessible methods named Started accept the instance, so the container cannot choose one");
		}

		[Test]
		public async Task ReportsWhenOverloadsDifferOnlyInTheInstanceParameterType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(OnRelease = nameof(Stopping))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Stopping(Service service) { }
			                                       	private static void Stopping(object instance) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT190*").AsWildcard()
				.Because("both the exact and the object overload accept the instance, so the release hook choice is order-dependent");
		}

		[Test]
		public async Task ReportsWhenTheCallInfersAGenericOverloadBesideANonGenericOne()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(OnActivated = nameof(Started))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Started(object instance) { }
			                                       	private static void Started<T>(T instance) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Started'*").AsWildcard()
				.Because("the generated call has no type arguments, so C# infers the generic overload for the service and prefers it to the one for objects");
		}

		[Test]
		[Arguments("private static void Started(params Service[] instances) { }")]
		[Arguments("private static void Started<T>(params T[] instances) { }")]
		[Arguments("private static void Started(params System.Collections.Generic.IEnumerable<Service> instances) { }")]
		public async Task ReportsWhenTheCallExpandsAParamsOverloadBesideANonGenericOne(string overload)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public sealed class Service { }

			                                         [Container]
			                                         [Singleton<Service>(OnActivated = nameof(Started))]
			                                         public static partial class MyContainer
			                                         {
			                                         	private static void Started(object instance) { }
			                                         	{{overload}}
			                                         }
			                                         """);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo("*AWT190*'Started'*").AsWildcard()
				.Because("C# binds the generated call in the expanded form of the params overload, which takes the service itself, and prefers it to the one for objects");
		}

		[Test]
		[Arguments("private static void Started(ref Service instance) { }")]
		[Arguments("private static void Started(out Service instance) { instance = null!; }")]
		[Arguments("private static void Started<T>(ref T instance) { }")]
		[Arguments("private static void Started<T>(T instance, out int attempts) { attempts = 0; }")]
		public async Task DoesNotReportAnOverloadTheCallCannotPassByReference(string overload)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public sealed class Service { }

			                                         [Container]
			                                         [Singleton<Service>(OnActivated = nameof(Started))]
			                                         public static partial class MyContainer
			                                         {
			                                         	private static void Started(object instance) { }
			                                         	{{overload}}
			                                         }
			                                         """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the generated call passes its arguments by value, so C# never binds an overload that takes one by reference");
		}

		[Test]
		public async Task DoesNotReportAParamsOverloadWhoseElementDoesNotAcceptTheInstance()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(OnActivated = nameof(Started))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Started(object instance) { }
			                                       	private static void Started(params int[] values) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task DoesNotReportWhenOnlyOneOverloadAcceptsTheInstance()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(OnActivated = nameof(Started))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Started(Service service) { }
			                                       	private static void Started(int unrelated) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT190*").AsWildcard()
				.Because("the int overload does not accept the instance, so there is exactly one usable hook and no ambiguity");
			await That(result.Diagnostics).DoesNotContain("*AWT164*").AsWildcard()
				.Because("the int overload does not accept the instance, so there is exactly one usable hook and no ambiguity");
		}

		[Test]
		public async Task ReportsWhenAGenericScanHookOverloadBindsBesideANonGenericOne()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IView<TViewModel> { }
			                                       public sealed class MainWindow : IView<int> { }

			                                       [Container]
			                                       [Scan(typeof(IView<>), As = ScanAs.Marker, OnActivated = nameof(Wire))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Wire<TViewModel>(IView<TViewModel> view) { }
			                                       	private static void Wire(object instance) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT190*").AsWildcard()
				.Because("the generic overload binds the single closing and the object overload also accepts the match, so the container cannot choose one");
		}

		[Test]
		public async Task ReportsWhenAnAmbiguousGenericScanHookOverloadSitsBesideANonGenericOne()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IView<TViewModel> { }
			                                       public sealed class DualView : IView<int>, IView<string> { }

			                                       [Container]
			                                       [Scan(typeof(IView<>), As = ScanAs.Marker, OnActivated = nameof(Wire))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Wire<TViewModel>(IView<TViewModel> view) { }
			                                       	private static void Wire(object instance) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT190*").AsWildcard()
				.Because("the generic overload could dispatch through either closing and the object overload also accepts the match, so silently preferring the sibling would let an extra closing change which method runs");
			await That(result.Diagnostics).DoesNotContain("*AWT198*").AsWildcard()
				.Because("the collision is between overloads, not closings: settling the closings would still leave two usable overloads");
		}

		[Test]
		public async Task DoesNotReportWhenTheSameNameServesDistinctRegistrationsOneMatchEach()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Alpha { }
			                                       public sealed class Beta { }

			                                       [Container]
			                                       [Singleton<Alpha>(OnActivated = nameof(Started))]
			                                       [Singleton<Beta>(OnActivated = nameof(Started))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Started(object instance) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT190*").AsWildcard()
				.Because("a single shared object hook is one match per registration, so neither registration is ambiguous");
		}

		[Test]
		public async Task ReportsAnOverloadedModuleScanHook()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace Lib;

			                                       public interface IClock { }
			                                       public interface IPlugin { }
			                                       public interface IRoaster { }
			                                       internal sealed class Roaster : IPlugin, IRoaster { }

			                                       [Module]
			                                       [Scan<IPlugin>(As = ScanAs.MatchingInterface, OnActivated = nameof(Wire))]
			                                       public static partial class PluginModule
			                                       {
			                                       	internal static void Wire(Roaster roaster) { }
			                                       	internal static void Wire(Roaster roaster, IClock clock) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT190*Wire*").AsWildcard()
				.Because("two module methods named Wire accept the match, so the hook is ambiguous at the module's own build");
		}
	}
}
