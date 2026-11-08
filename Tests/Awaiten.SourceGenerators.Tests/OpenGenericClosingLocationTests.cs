using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The location of a diagnostic about a closing synthesized from an open generic registration: the open
///     registration's attribute, or the <c>[Import]</c> or <c>Extends</c> that brings it in when it has no syntax in the
///     container's compilation.
/// </summary>
public class OpenGenericClosingLocationTests
{
	[Test]
	public async Task AnInvalidHookOfAClosing_IsReportedAtTheOpenRegistration()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IRepository<T> { }
		                                       public sealed class Repository<T> : IRepository<T> { }
		                                       public sealed class Checkout { public Checkout(IRepository<int> numbers, IRepository<string> names) { } }

		                                       [Container]
		                                       [Scoped(typeof(Repository<>), typeof(IRepository<>), OnActivated = "Missing")]
		                                       [Transient<Checkout>]
		                                       public static partial class MyContainer
		                                       {
		                                       }
		                                       """);

		await That(result.Diagnostics).HasCount(2).And
			.All().Satisfy(diagnostic => diagnostic.StartsWith("(10,2): error AWT164", StringComparison.Ordinal))
			.Because("each closing's diagnostic points at the open registration it was synthesized from");
	}

	[Test]
	public async Task ACaptiveDependencyOfAClosing_IsReportedAtTheOpenRegistration()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public sealed class Clock { }
		                                       public interface IRepository<T> { }
		                                       public sealed class Repository<T> : IRepository<T> { public Repository(Clock clock) { } }
		                                       public sealed class Checkout { public Checkout(IRepository<int> numbers) { } }

		                                       [Module]
		                                       [Singleton(typeof(Repository<>), typeof(IRepository<>))]
		                                       public static partial class DataModule
		                                       {
		                                       }

		                                       [Container]
		                                       [Import(typeof(DataModule))]
		                                       [Scoped<Clock>]
		                                       [Transient<Checkout>]
		                                       public static partial class MyContainer
		                                       {
		                                       }
		                                       """);

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.StartsWith("(11,2): error AWT105", StringComparison.Ordinal))
			.Because("a module of the container's own compilation has the syntax of its open registration");
	}

	[Test]
	public async Task AMissingDependencyOfAClosingOfAnotherAssembly_IsReportedAtTheImport()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
		                                                                      using Awaiten;

		                                                                      namespace Lib;

		                                                                      public sealed class Clock { }
		                                                                      public interface IRepository<T> { }
		                                                                      internal sealed class Repository<T> : IRepository<T> { public Repository(Clock clock) { } }

		                                                                      [Module]
		                                                                      [Singleton(typeof(Repository<>), typeof(IRepository<>))]
		                                                                      public static partial class DataModule { }
		                                                                      """, """
		                                                                           using Awaiten;
		                                                                           using Lib;

		                                                                           namespace App;

		                                                                           public sealed class Checkout { public Checkout(IRepository<int> numbers) { } }

		                                                                           [Container]
		                                                                           [Import(typeof(DataModule))]
		                                                                           [Transient<Checkout>]
		                                                                           public static partial class AppContainer
		                                                                           {
		                                                                           }
		                                                                           """);

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.StartsWith("(9,2): error AWT101", StringComparison.Ordinal))
			.Because("the module's attribute has no syntax in the consumer, so the diagnostic points at the [Import]");
	}

	[Test]
	public async Task AFriendContainer_ReportsAClosingAtTheImport()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
		                                                                      using Awaiten;

		                                                                      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

		                                                                      namespace Lib;

		                                                                      public sealed class Clock { }
		                                                                      public interface IRepository<T> { }
		                                                                      internal sealed class Repository<T> : IRepository<T> { public Repository(Clock clock) { } }

		                                                                      [Module]
		                                                                      [Singleton(typeof(Repository<>), typeof(IRepository<>))]
		                                                                      public static partial class DataModule { }
		                                                                      """, """
		                                                                           using Awaiten;
		                                                                           using Lib;

		                                                                           namespace App;

		                                                                           public sealed class Checkout { public Checkout(IRepository<int> numbers) { } }

		                                                                           [Container]
		                                                                           [Import(typeof(DataModule))]
		                                                                           [Transient<Checkout>]
		                                                                           public static partial class AppContainer
		                                                                           {
		                                                                           }
		                                                                           """);

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.StartsWith("(9,2): error AWT101", StringComparison.Ordinal));
	}

	[Test]
	public async Task AClosingAnExtendingContainerIntroduces_IsReportedAtTheBasesOpenRegistration()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public sealed class Clock { }
		                                       public interface IRepository<T> { }
		                                       public sealed class Repository<T> : IRepository<T> { public Repository(Clock clock) { } }
		                                       public sealed class Checkout { public Checkout(IRepository<int> numbers) { } }

		                                       [Container]
		                                       [Singleton(typeof(Repository<>), typeof(IRepository<>))]
		                                       public static partial class Shop
		                                       {
		                                       }

		                                       [Container(Extends = typeof(Shop))]
		                                       [Transient<Checkout>]
		                                       public static partial class TestShop
		                                       {
		                                       }
		                                       """);

		await That(result.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT101") && diagnostic.StartsWith("(", StringComparison.Ordinal))
			.Because("the closing the extending container's consumer needs still names a position");
	}
}
