namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt242RelationshipOverImportedService
	{
		private const string DeferredLazyClockModule = """
		                                               using System;
		                                               using Awaiten;

		                                               namespace Lib;

		                                               public interface IGreeter { }
		                                               public interface IClock { }

		                                               internal sealed class Greeter : IGreeter
		                                               {
		                                                   [Inject(Deferred = true)] public Lazy<IClock> Clock { get; set; } = null!;
		                                               }

		                                               [Module]
		                                               [Transient<Greeter, IGreeter>]
		                                               public static partial class GreetingModule { }
		                                               """;

		[Test]
		[Arguments("Lazy<IFeed<string>>")]
		[Arguments("Func<IFeed<string>>")]
		[Arguments("Func<int, IFeed<string>>")]
		[Arguments("Owned<IFeed<string>>")]
		[Arguments("Func<Owned<IFeed<string>>>")]
		[Arguments("Task<IFeed<string>>")]
		[Arguments("Task<Owned<IFeed<string>>>")]
		[Arguments("Func<Task<IFeed<string>>>")]
		[Arguments("Func<Task<Owned<IFeed<string>>>>")]
		[Arguments("Lazy<Task<IFeed<string>>>")]
		public async Task ReportsARelationshipOverAClosedImport(string relationship)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System;
			                                         using System.Threading.Tasks;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public interface IFeed<T> { }
			                                         public sealed class Reader { public Reader({{relationship}} feed) { } }

			                                         [Container]
			                                         [ImportService<IFeed<string>>]
			                                         [Singleton<Reader>]
			                                         public static partial class MyContainer;
			                                         """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.Reader' requires '*', a relationship over 'MyCode.IFeed<string>'*depend on 'MyCode.IFeed<string>' directly*").AsWildcard()
				.Because("only a direct dependency of an imported service is routed to the external resolver, so the relationship over it names the import and the remedy");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard()
				.Because("the service is declared external, so calling it unregistered would point the user at a registration that AWT175 forbids");
			await That(result.Diagnostics).DoesNotContain("*error CS*").AsWildcard()
				.Because("the container still emits its shell, so the user's code keeps compiling around the diagnostic");
		}

		[Test]
		public async Task ReportsAKeyedRelationshipOverAClosedImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader([FromKey("utc")] Lazy<IClock> clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock (key: utc)'*").AsWildcard()
				.Because("a keyed direct dependency of an imported service is routed with its key, so a keyed relationship over it is the same mistake");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsARelationshipOverAClosingOfAnOpenImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface ILog<T> { }
			                                       public sealed class Reader { public Reader(Func<ILog<Reader>> log) { } }

			                                       [Container]
			                                       [ImportService(typeof(ILog<>))]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.ILog<MyCode.Reader>'*").AsWildcard()
				.Because("a direct ILog<Reader> would be routed by the open import, so the relationship over the unregistered closing is the same mistake");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsARelationshipOverAnExplicitImportUnderImportServices()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(Lazy<IClock> clock) { } }

			                                       [Container]
			                                       [ImportServices]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("the explicit import declares the service host-owned, whatever the blanket fall-through does");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsARelationshipOverAnInheritedImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(Lazy<IClock> clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       [Singleton<Reader>]
			                                       public static partial class TestShop;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("an extending container routes the imports of its base like its own");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsARelationshipOverAnImportDeclaredOnAModule()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(Lazy<IClock> clock) { } }

			                                       [Module]
			                                       [ImportService<IClock>]
			                                       public static class HostModule;

			                                       [Container]
			                                       [Import(typeof(HostModule))]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("an import declared on a module routes for the whole container, exactly like one on the container");
		}

		[Test]
		[Arguments("[Inject]")]
		[Arguments("[Inject(Optional = true)]")]
		public async Task ReportsAnInjectedPropertyOverAnImport(string inject)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public interface IClock { }
			                                         public sealed class Reader
			                                         {
			                                             {{inject}}
			                                             public Lazy<IClock> Clock { get; set; } = null!;
			                                         }

			                                         [Container]
			                                         [ImportService<IClock>]
			                                         [Singleton<Reader>]
			                                         public static partial class MyContainer;
			                                         """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("an imported service can never be registered (AWT175), so even an optional member over it could never be filled");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsADeferredPropertyOfAnotherAssemblysModuleOverAnImport()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(DeferredLazyClockModule, """
			                                                                       using Awaiten;

			                                                                       namespace App;

			                                                                       [Container]
			                                                                       [Import(typeof(Lib.GreetingModule))]
			                                                                       [ImportService<Lib.IClock>]
			                                                                       public static partial class MyContainer;
			                                                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'Lib.IClock'*").AsWildcard()
				.Because("the deferred property is filled through the module's generated setter, whose parameter is classified like the property");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsAFactoryParameterOverAnImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(Func<IClock> clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>(Factory = nameof(MakeReader))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static Reader MakeReader(Func<IClock> clock) => new(clock);
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("a factory parameter is classified like a constructor parameter");
		}

		[Test]
		public async Task ReportsALifecycleHookParameterOverAnImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>(OnActivated = nameof(Started))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static void Started(Reader reader, Lazy<IClock> clock) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("a hook parameter is resolved from the graph like a constructor parameter");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsADecoratorParameterOverAnImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public interface IBrewer { }
			                                       public sealed class Brewer : IBrewer { }
			                                       public sealed class TimedBrewer : IBrewer { public TimedBrewer(IBrewer inner, Lazy<IClock> clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Brewer, IBrewer>]
			                                       [Decorate<TimedBrewer, IBrewer>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT242*'MyCode.IClock'*").AsWildcard()
				.Because("a decorator's other parameters are classified like a constructor parameter");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportADirectDependencyOnAnImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IClock clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the direct dependency is the shape the external resolver serves");
		}

		[Test]
		public async Task DoesNotReportARelationshipOverAnUnimportedService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public interface IGrinder { }
			                                       public sealed class Reader { public Reader(Lazy<IGrinder> grinder) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*IGrinder*").AsWildcard()
				.Because("a service the container does not import is simply missing");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		[Arguments("Lazy<Typo>")]
		[Arguments("Func<Typo>")]
		[Arguments("Owned<Typo>")]
		[Arguments("Task<Typo>")]
		public async Task DoesNotReportARelationshipUnderImportServicesAlone(string relationship)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System;
			                                         using System.Threading.Tasks;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public sealed class Typo { }
			                                         public sealed class Reader { public Reader({{relationship}} typo) { } }

			                                         [Container]
			                                         [ImportServices]
			                                         [Singleton<Reader>]
			                                         public static partial class MyContainer;
			                                         """);

			await That(result.Diagnostics).Contains("*AWT101*'MyCode.Typo'*").AsWildcard()
				.Because("the blanket fall-through does not declare a service host-owned, so an unregistered one is most likely a forgotten registration");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportAParameterizedFuncUnderImportServicesAlone()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Widget { public Widget([Arg] int size) { } }
			                                       public sealed class Reader { public Reader(Func<int, Widget> widget) { } }

			                                       [Container]
			                                       [ImportServices]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*'MyCode.Widget'*").AsWildcard()
				.Because("a widget needs a runtime argument the host cannot supply, so it is a missing registration, not a service to take directly from the host");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportAKeyedRelationshipUnderImportServices()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader([FromKey("utc")] Lazy<IClock> clock) { } }

			                                       [Container]
			                                       [ImportServices]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.Because("[ImportServices] does not route a keyed direct dependency either, so the keyed service is simply missing");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportAnInjectedPropertyUnderImportServices()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader
			                                       {
			                                           [Inject]
			                                           public Lazy<IClock> Clock { get; set; } = null!;
			                                       }

			                                       [Container]
			                                       [ImportServices]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.Because("[ImportServices] does not route an injected property even in its direct form, so the service is simply missing");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportADeferredPropertyOfAnotherAssemblysModuleUnderImportServices()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(DeferredLazyClockModule, """
			                                                                       using Awaiten;

			                                                                       namespace App;

			                                                                       [Container]
			                                                                       [Import(typeof(Lib.GreetingModule))]
			                                                                       [ImportServices]
			                                                                       public static partial class MyContainer;
			                                                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.Because("the generated setter fills an injected property, which [ImportServices] does not route even in its direct form");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportAnOwnedHandleThroughLazy()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(Lazy<Owned<IClock>> clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).Contains("*AWT121*").AsWildcard()
				.Because("an Owned<T> handle can never be produced through Lazy, which is the more specific mistake");
			await That(result.Diagnostics).DoesNotContain("*AWT242*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportACollectionOverAnImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IEnumerable<IClock> clocks) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a collection is synthesized from the container's registrations and is never missing, so it stays empty");
		}
	}
}
