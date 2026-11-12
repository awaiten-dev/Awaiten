using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt182ScanNoMatchingInterface
	{
		[Test]
		public async Task ReportsWhenAMatchingInterfaceScanMatchesATypeWithNoConventionInterface()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IDrink { }
			                                       public interface ILatte { }
			                                       public sealed class Latte : IDrink, ILatte { }
			                                       public sealed class Mocha : IDrink { }   // no IMocha

			                                       [Container]
			                                       [Scan(typeof(IDrink), As = ScanAs.MatchingInterface)]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT182*").AsWildcard()
				.And.Contains("*Mocha*").AsWildcard()
				.Because("Mocha matched the scan but implements no interface named IMocha");
		}

		[Test]
		public async Task DoesNotReportWhenTheSelfFlagIsAlsoSet()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IDrink { }
			                                       public sealed class Mocha : IDrink { }   // no IMocha

			                                       [Container]
			                                       [Scan(typeof(IDrink), As = ScanAs.Self | ScanAs.MatchingInterface)]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT182*").AsWildcard()
				.Because("the Self flag still registers the match as its own concrete type");
		}

		[Test]
		public async Task DoesNotReportForAMarkerlessScan()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IConformingWidget { }
			                                       public sealed class ConformingWidget : IConformingWidget { }
			                                       public sealed class BareWidget { }   // no interface, no convention

			                                       [Container]
			                                       [Scan(As = ScanAs.MatchingInterface, NamePatterns = new[] { "*Widget" })]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT182*").AsWildcard()
				.Because("a markerless scan skips a non-conforming type silently instead of warning per type");
		}

		[Test]
		public async Task ReportsForAModuleScanMatchWithNoConventionInterface()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace Lib;

			                                       public interface IDrink { }
			                                       public interface ILatte { }
			                                       internal sealed class Latte : IDrink, ILatte { }
			                                       internal sealed class Mocha : IDrink { }

			                                       [Module]
			                                       [Scan<IDrink>(As = ScanAs.MatchingInterface)]
			                                       public static partial class DrinkModule { }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("warning AWT182") && diagnostic.Contains("'Lib.Mocha'") && diagnostic.Contains("'IMocha'"))
				.Because("a module scan reports a match with no matching interface like a container scan does");
		}

		[Test]
		public async Task DoesNotReportForAModuleScanMatchWhenTheSelfFlagIsAlsoSet()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace Lib;

			                                       public interface IDrink { }
			                                       internal sealed class Mocha : IDrink { }

			                                       [Module]
			                                       [Scan<IDrink>(As = ScanAs.Self | ScanAs.MatchingInterface)]
			                                       public static partial class DrinkModule { }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("warning AWT196") && diagnostic.Contains("'Lib.Mocha'"))
				.Because("the Self exposure exists but names a type no consumer of the public module can name");
		}
	}
}
