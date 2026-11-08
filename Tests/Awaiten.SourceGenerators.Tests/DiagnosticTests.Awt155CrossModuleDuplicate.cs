using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt155CrossModuleDuplicate
	{
		[Test]
		public async Task ReportsWhenTwoModulesStronglyRegisterTheSameServiceWithDifferentImplementations()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
				.Because("which module wins is decided only by [Import] order, invisible at either module");
		}

		[Test]
		public async Task ReportsWhenTwoModulesOpenGenericTemplatesCollideOnTheSameClosedService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRepo<T> { }
			                                       public sealed class RepoA<T> : IRepo<T> { }
			                                       public sealed class RepoB<T> : IRepo<T> { }
			                                       public sealed class Order { }
			                                       public sealed class Consumer
			                                       {
			                                           public Consumer(IRepo<Order> repo) { }
			                                       }

			                                       [Module]
			                                       [Singleton(typeof(RepoA<>), typeof(IRepo<>))]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton(typeof(RepoB<>), typeof(IRepo<>))]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT155*IRepo*ModuleA*ModuleB*").AsWildcard()
				.Because("the closed registrations expanded from two modules' open templates collide exactly like two hand-written registrations - the winner is decided only by [Import] order");
		}

		[Test]
		public async Task DoesNotReportWhenAnExplicitRegistrationBeatsAnotherModulesOpenGenericTemplate()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRepo<T> { }
			                                       public sealed class Repo<T> : IRepo<T> { }
			                                       public sealed class Order { }
			                                       public sealed class SpecialRepo : IRepo<Order> { }
			                                       public sealed class Consumer
			                                       {
			                                           public Consumer(IRepo<Order> repo) { }
			                                       }

			                                       [Module]
			                                       [Singleton(typeof(Repo<>), typeof(IRepo<>))]
			                                       public static class TemplateModule { }

			                                       [Module]
			                                       [Singleton<SpecialRepo, IRepo<Order>>]
			                                       public static class SpecialModule { }

			                                       [Container]
			                                       [Import(typeof(TemplateModule))]
			                                       [Import(typeof(SpecialModule))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("an explicit registration beating another module's expanded template is deterministic regardless of [Import] order, so the collision is not ambiguous");
		}

		[Test]
		public async Task DoesNotReportWhenTheContainerOverridesAModule()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ModuleClock : IClock { }
			                                       public sealed class AppClock : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ModuleClock, IClock>]
			                                       public static class ClockModule { }

			                                       [Container]
			                                       [Import(typeof(ClockModule))]
			                                       [Singleton<AppClock, IClock>]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("the container overriding a module is the intended override mechanism");
		}

		[Test]
		public async Task DoesNotReportWhenBothModulesRegisterTheSameImplementation()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SharedClock : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<SharedClock, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<SharedClock, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("the same implementation coalesces into one instance; there is no ambiguity");
		}

		[Test]
		public async Task DoesNotReportWhenTheLaterModulesRegistrationIsAnOverridableDefault()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>(Fallback = Fallback.Warn)]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("an overridable default yields by design; being overridden is its purpose");
		}

		[Test]
		public async Task KeyedCollisionsKeepReportingAwt117Instead()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>(Key = "clock")]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>(Key = "clock")]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT117*").AsWildcard()
				.Because("a keyed duplicate is already its own error");
			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("AWT155 is limited to unkeyed collisions to avoid double-reporting");
		}

		[Test]
		public async Task DoesNotReportWhenNothingInTheGraphResolvesTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("no dependency in the graph depends on which registration wins single resolution");
		}

		[Test]
		[Arguments("IEnumerable<IClock>")]
		[Arguments("IReadOnlyList<IClock>")]
		[Arguments("IClock[]")]
		[Arguments("IAsyncEnumerable<IClock>")]
		[Arguments("Task<IReadOnlyList<IClock>>")]
		public async Task DoesNotReportWhenTheServiceIsOnlyConsumedAsACollection(string shape)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System.Collections.Generic;
			                                         using System.Threading.Tasks;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public interface IClock { }
			                                         public sealed class ClockA : IClock { }
			                                         public sealed class ClockB : IClock { }
			                                         public sealed class Consumer({{shape}} clocks) { }

			                                         [Module]
			                                         [Singleton<ClockA, IClock>]
			                                         public static class ModuleA { }

			                                         [Module]
			                                         [Singleton<ClockB, IClock>]
			                                         public static class ModuleB { }

			                                         [Container]
			                                         [Import(typeof(ModuleA))]
			                                         [Import(typeof(ModuleB))]
			                                         [Singleton<Consumer>]
			                                         public static partial class MyContainer
			                                         {
			                                         }
			                                         """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("a collection receives every contribution, so the order of the imports decides nothing it sees");
		}

		[Test]
		public async Task DoesNotReportWhenTheServiceIsOnlyConsumedAsAKeyedDictionary()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class ClockC : IClock { }
			                                       public sealed class Consumer(IReadOnlyDictionary<string, IClock> clocks) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       [Singleton<ClockC, IClock>(Key = "c")]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("a keyed dictionary holds only keyed registrations, so it never resolves the unkeyed winner");
		}

		[Test]
		public async Task DoesNotReportWhenACompositeFrontsTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class AllClocks(IEnumerable<IClock> clocks) : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Composite<AllClocks, IClock>]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("the composite takes single resolution and fans out to every contribution");
		}

		[Test]
		public async Task DoesNotReportWhenAnOpenCompositeFrontsTheClosing()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRepo<T> { }
			                                       public sealed class RepoA<T> : IRepo<T> { }
			                                       public sealed class RepoB<T> : IRepo<T> { }
			                                       public sealed class AllRepos<T>(IEnumerable<IRepo<T>> repos) : IRepo<T> { }
			                                       public sealed class Order { }
			                                       public sealed class Consumer(IRepo<Order> repo) { }

			                                       [Module]
			                                       [Singleton(typeof(RepoA<>), typeof(IRepo<>))]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton(typeof(RepoB<>), typeof(IRepo<>))]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Composite(typeof(AllRepos<>), typeof(IRepo<>))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("the closed composite takes single resolution of the closing and fans out to every contribution");
		}

		[Test]
		public async Task ReportsWhenADecoratedServiceIsResolvedSingly()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class LoggingClock(IClock inner) : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Decorate<LoggingClock, IClock>]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
				.Because("the decorator wraps the single winner, which only the order of the imports decides");
		}

		[Test]
		[Arguments("Func<IClock>")]
		[Arguments("Lazy<IClock>")]
		[Arguments("Owned<IClock>")]
		public async Task ReportsWhenARelationshipResolvesTheServiceSingly(string shape)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public interface IClock { }
			                                         public sealed class ClockA : IClock { }
			                                         public sealed class ClockB : IClock { }
			                                         public sealed class Consumer({{shape}} clock) { }

			                                         [Module]
			                                         [Singleton<ClockA, IClock>]
			                                         public static class ModuleA { }

			                                         [Module]
			                                         [Singleton<ClockB, IClock>]
			                                         public static class ModuleB { }

			                                         [Container]
			                                         [Import(typeof(ModuleA))]
			                                         [Import(typeof(ModuleB))]
			                                         [Transient<Consumer>]
			                                         public static partial class MyContainer
			                                         {
			                                         }
			                                         """);

			await That(result.Diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
				.Because("a relationship hands out the single winner, which only the order of the imports decides");
		}

		[Test]
		public async Task SuggestsAnOverridableDefaultWhenTheServiceIsOnlyResolvedSingly()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT155*override it on the container, or make one registration an overridable default*").AsWildcard()
				.Because("with no collection in the graph, an overridden default drops nothing anyone sees");
		}

		[Test]
		public async Task DoesNotSuggestAnOverridableDefaultWhenTheServiceIsAlsoConsumedAsACollection()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class Consumer(IClock clock) { }
			                                       public sealed class Board(IEnumerable<IClock> clocks) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       [Singleton<Board>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			string awt155 = result.Diagnostics.Single(diagnostic => diagnostic.Contains("AWT155"));
			await That(awt155).DoesNotContain("overridable default")
				.Because("a default that a strong registration overrides leaves the collection, so the advice would drop a member");
			await That(awt155).Contains("register the implementation single resolution should use on the container too")
				.Because("re-registering one implementation on the container decides single resolution and keeps every member");
		}

		[Test]
		public async Task DoesNotSuggestAnOverridableDefaultWhenAVarianceCollectionHoldsTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public class Base { }
			                                       public sealed class Derived : Base { }
			                                       public interface IHandler<in T> { }
			                                       public sealed class HandlerA : IHandler<Base> { }
			                                       public sealed class HandlerB : IHandler<Base> { }
			                                       public sealed class Consumer(IHandler<Base> handler) { }
			                                       public sealed class Board(IEnumerable<IHandler<Derived>> handlers) { }

			                                       [Module]
			                                       [Singleton<HandlerA, IHandler<Base>>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<HandlerB, IHandler<Base>>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       [Singleton<Board>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			string awt155 = result.Diagnostics.Single(diagnostic => diagnostic.Contains("AWT155"));
			await That(awt155).DoesNotContain("overridable default")
				.Because("the collection of IHandler<Derived> holds every IHandler<Base> through variance, so an overridden default would leave it");
		}

		[Test]
		public async Task DoesNotReportWhenEveryRegistrationIsAnOverridableDefault()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>(Fallback = Fallback.Silent)]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>(Fallback = Fallback.Silent)]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("defaults colliding are AWT148's to report, and Fallback.Silent opts out of it");
		}

		[Test]
		public async Task ReportsForModulesOfAnotherAssemblyWhenTheServiceIsResolvedSingly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
			                                                                      using Awaiten;

			                                                                      namespace Lib;

			                                                                      public interface IClock { }
			                                                                      public sealed class ClockA : IClock { }
			                                                                      internal sealed class ClockB : IClock { }

			                                                                      [Module]
			                                                                      [Singleton<ClockA, IClock>]
			                                                                      public static partial class ModuleA { }

			                                                                      [Module]
			                                                                      [Singleton<ClockB, IClock>]
			                                                                      public static partial class ModuleB { }
			                                                                      """, """
			                                                                           using Awaiten;
			                                                                           using Lib;

			                                                                           namespace MyCode;

			                                                                           public sealed class Consumer(IClock clock) { }

			                                                                           [Container]
			                                                                           [Import(typeof(ModuleA))]
			                                                                           [Import(typeof(ModuleB))]
			                                                                           [Singleton<Consumer>]
			                                                                           public static partial class MyContainer
			                                                                           {
			                                                                           }
			                                                                           """);

			await That(result.Diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
				.Because("modules of another assembly collide like modules of the container's own assembly");
		}

		[Test]
		public async Task DoesNotReportForModulesOfAnotherAssemblyWhenTheServiceIsOnlyConsumedAsACollection()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
			                                                                      using Awaiten;

			                                                                      namespace Lib;

			                                                                      public interface IClock { }
			                                                                      public sealed class ClockA : IClock { }
			                                                                      internal sealed class ClockB : IClock { }

			                                                                      [Module]
			                                                                      [Singleton<ClockA, IClock>]
			                                                                      public static partial class ModuleA { }

			                                                                      [Module]
			                                                                      [Singleton<ClockB, IClock>]
			                                                                      public static partial class ModuleB { }
			                                                                      """, """
			                                                                           using System.Collections.Generic;
			                                                                           using Awaiten;
			                                                                           using Lib;

			                                                                           namespace MyCode;

			                                                                           public sealed class Consumer(IEnumerable<IClock> clocks) { }

			                                                                           [Container]
			                                                                           [Import(typeof(ModuleA))]
			                                                                           [Import(typeof(ModuleB))]
			                                                                           [Singleton<Consumer>]
			                                                                           public static partial class MyContainer
			                                                                           {
			                                                                           }
			                                                                           """);

			await That(result.Diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("a collection receives every contribution of either assembly");
		}

		[Test]
		public async Task ReportsWhenAnExtendingContainerResolvesAServiceItsModuleAndTheBasesModuleRegister()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB : IClock { }
			                                       public sealed class Board(IEnumerable<IClock> clocks) { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Singleton<Board>]
			                                       public static partial class BaseContainer;

			                                       [Container(Extends = typeof(BaseContainer))]
			                                       [Import(typeof(ModuleB))]
			                                       public static partial class CollectionContainer;

			                                       [Container(Extends = typeof(BaseContainer))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class SingleContainer;
			                                       """);

			await That(result.Diagnostics.Count(diagnostic => diagnostic.Contains("AWT155"))).IsEqualTo(1)
				.Because("only the extending container that resolves IClock singly depends on which module wins");
			await That(result.Diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
				.Because("the base's module is imported first, so it wins single resolution");
		}

		[Test]
		public async Task Analyzer_ReportsAtATypedResolveOfAServiceNothingInTheGraphResolvesSingly()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using System;
			                                                           using Awaiten;

			                                                           namespace MyCode;

			                                                           public interface IClock { }
			                                                           public sealed class ClockA : IClock { }
			                                                           public sealed class ClockB : IClock { }

			                                                           [Module]
			                                                           [Singleton<ClockA, IClock>]
			                                                           public static class ModuleA { }

			                                                           [Module]
			                                                           [Singleton<ClockB, IClock>]
			                                                           public static class ModuleB { }

			                                                           [Container]
			                                                           [Import(typeof(ModuleA))]
			                                                           [Import(typeof(ModuleB))]
			                                                           public static partial class MyContainer;

			                                                           public static class Program
			                                                           {
			                                                               public static void Run()
			                                                               {
			                                                                   using MyContainer.Root root = new();
			                                                                   IClock typed = root.Resolve<IClock>();
			                                                                   object untyped = root.Resolve(typeof(IClock));
			                                                               }
			                                                           }
			                                                           """);

			await That(diagnostics.Count(diagnostic => diagnostic.Contains("AWT155"))).IsEqualTo(2)
				.Because("both the typed and the typeof resolve hand out the single winner, which only the order of the imports decides");
			await That(diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
				.Because("the call site names the same collision the registration would");
		}

		[Test]
		public async Task Analyzer_DoesNotReportAtAResolveOfTheCollection()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using System.Collections.Generic;
			                                                           using Awaiten;

			                                                           namespace MyCode;

			                                                           public interface IClock { }
			                                                           public sealed class ClockA : IClock { }
			                                                           public sealed class ClockB : IClock { }

			                                                           [Module]
			                                                           [Singleton<ClockA, IClock>]
			                                                           public static class ModuleA { }

			                                                           [Module]
			                                                           [Singleton<ClockB, IClock>]
			                                                           public static class ModuleB { }

			                                                           [Container]
			                                                           [Import(typeof(ModuleA))]
			                                                           [Import(typeof(ModuleB))]
			                                                           public static partial class MyContainer;

			                                                           public static class Program
			                                                           {
			                                                               public static void Run()
			                                                               {
			                                                                   using MyContainer.Root root = new();
			                                                                   IEnumerable<IClock> clocks = root.Resolve<IEnumerable<IClock>>();
			                                                               }
			                                                           }
			                                                           """);

			await That(diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("a resolved collection receives every contribution");
		}

		[Test]
		public async Task Analyzer_DoesNotRepeatACollisionTheGraphAlreadyReports()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using Awaiten;

			                                                           namespace MyCode;

			                                                           public interface IClock { }
			                                                           public sealed class ClockA : IClock { }
			                                                           public sealed class ClockB : IClock { }
			                                                           public sealed class Consumer(IClock clock) { }

			                                                           [Module]
			                                                           [Singleton<ClockA, IClock>]
			                                                           public static class ModuleA { }

			                                                           [Module]
			                                                           [Singleton<ClockB, IClock>]
			                                                           public static class ModuleB { }

			                                                           [Container]
			                                                           [Import(typeof(ModuleA))]
			                                                           [Import(typeof(ModuleB))]
			                                                           [Singleton<Consumer>]
			                                                           public static partial class MyContainer;

			                                                           public static class Program
			                                                           {
			                                                               public static void Run()
			                                                               {
			                                                                   using MyContainer.Root root = new();
			                                                                   IClock clock = root.Resolve<IClock>();
			                                                               }
			                                                           }
			                                                           """);

			await That(diagnostics).DoesNotContain("*AWT155*").AsWildcard()
				.Because("the generator already reports the collision at the registration, once for the whole graph");
		}

		[Test]
		public async Task Analyzer_DoesNotSuggestAnOverridableDefaultWhenTheGraphConsumesTheServiceAsACollection()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using System.Collections.Generic;
			                                                           using Awaiten;

			                                                           namespace MyCode;

			                                                           public interface IClock { }
			                                                           public sealed class ClockA : IClock { }
			                                                           public sealed class ClockB : IClock { }
			                                                           public sealed class Board(IEnumerable<IClock> clocks) { }

			                                                           [Module]
			                                                           [Singleton<ClockA, IClock>]
			                                                           public static class ModuleA { }

			                                                           [Module]
			                                                           [Singleton<ClockB, IClock>]
			                                                           public static class ModuleB { }

			                                                           [Container]
			                                                           [Import(typeof(ModuleA))]
			                                                           [Import(typeof(ModuleB))]
			                                                           [Singleton<Board>]
			                                                           public static partial class MyContainer;

			                                                           public static class Program
			                                                           {
			                                                               public static void Run()
			                                                               {
			                                                                   using MyContainer.Root root = new();
			                                                                   IClock clock = root.Resolve<IClock>();
			                                                               }
			                                                           }
			                                                           """);

			string awt155 = diagnostics.Single(diagnostic => diagnostic.Contains("AWT155"));
			await That(awt155).DoesNotContain("overridable default")
				.Because("the collection the graph consumes would lose the member a strong registration overrides");
		}
	}
}
