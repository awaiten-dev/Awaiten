namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The generated shape of a decorator over an imported service: a transient chain whose innermost link asks the
///     external resolver for the host's instance, which every consumer and the by-type dispatch reach instead of the host.
/// </summary>
public class DecoratedImportTests
{
	[Test]
	public async Task Decorator_OverAClosedImport_WrapsTheExternalLookup()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IGrinder { }
		                                       public sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner) { } }
		                                       public sealed class Barista { public Barista(IGrinder grinder) { } }

		                                       [Container]
		                                       [ImportService<IGrinder>]
		                                       [Singleton<Barista>]
		                                       [Decorate<LoggingGrinder, IGrinder>]
		                                       public static partial class MyContainer;
		                                       """);

		await That(result.Diagnostics).IsEmpty()
			.Because("a decorator wraps the instance the host hands in, so the import is something to decorate");
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		await That(source).Contains("new global::MyCode.LoggingGrinder((global::MyCode.IGrinder)__s.__ResolveExternal(typeof(global::MyCode.IGrinder), null))")
			.Because("the decorator's inner parameter asks the host for its instance");
		await That(source).Contains("new global::MyCode.Barista(ResolveLoggingGrinder(__s))")
			.Because("a consumer of the import receives the decorator, not the host's instance");
		await That(source).Contains("new global::Awaiten.AwaitenExternalDependency(typeof(global::MyCode.IGrinder), null)")
			.Because("the host must still provide the inner instance, so startup verification checks it");
		await That(source).DoesNotContain("new global::Awaiten.AwaitenRegistration(typeof(global::MyCode.IGrinder)")
			.Because("projecting the decorated import into the host would replace the host's own registration the decorator wraps");
	}

	[Test]
	public async Task Decorator_OverAnImportThatAnExtendingContainerSubstitutes_WrapsTheExternalLookup()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IGrinder { }
		                                       public sealed class Grinder : IGrinder { }
		                                       public sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner) { } }

		                                       [Module]
		                                       [Singleton<Grinder, IGrinder>]
		                                       [Decorate<LoggingGrinder, IGrinder>]
		                                       public static partial class CoffeeModule;

		                                       [Container]
		                                       [Import(typeof(CoffeeModule))]
		                                       public static partial class Shop;

		                                       [Container(Extends = typeof(Shop))]
		                                       [ImportService<IGrinder>]
		                                       public static partial class TestShop;
		                                       """);

		await That(result.Diagnostics).IsEmpty()
			.Because("the inherited decorator wraps the replacement, also when the replacement comes from the host");
		string source = result.Sources["Awaiten.MyCode.TestShop.g.cs"];
		await That(source).Contains("new global::MyCode.LoggingGrinder((global::MyCode.IGrinder)__s.__ResolveExternal(typeof(global::MyCode.IGrinder), null))");
	}
}
