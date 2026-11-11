using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt167ContextualBinding
	{
		[Test]
		public async Task ReportsWhenTheNamedConsumerHasNoDependencyOnTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class NoClockDependency { }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NoClockDependency))]
			                                       [Singleton<NoClockDependency>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT167*").AsWildcard()
				.Because("the named consumer has no constructor dependency on the service, so the binding is dead");
		}

		[Test]
		public async Task DoesNotReportWhenTheNamedConsumerDependsOnTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class NeedsTest { public NeedsTest(IClock clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<NeedsTest>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the contextual binding is consumed by the named consumer's constructor parameter");
		}

		[Test]
		public async Task ReportsWhenTheConsumersOnlyDependencyIsSelectedByAnExplicitFromKey()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class SpecialClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class Consumer { public Consumer([FromKey("special")] IClock clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<SpecialClock, IClock>(Key = "special")]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(Consumer))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT167*").AsWildcard()
				.Because("the [FromKey] parameter takes precedence, so the contextual binding is never applied");
		}

		[Test]
		public async Task ReportsWhenTheConsumerDependsOnlyThroughAFuncRelationship()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class NeedsFunc { public NeedsFunc(Func<IClock> clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NeedsFunc))]
			                                       [Singleton<NeedsFunc>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT167*").AsWildcard()
				.Because("a Func-deferred dependency is not an unkeyed direct parameter, so the contextual binding is never applied");
		}

		[Test]
		public async Task DoesNotReportWhenTheNamedConsumerDependsThroughAnInjectProperty()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class NeedsTest { [Inject] public IClock Clock { get; set; } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<NeedsTest>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("an [Inject] property is an unkeyed direct dependency, so it consumes the contextual binding");
		}

		[Test]
		public async Task ReportsForAnExtendingContainersBindingOfAnImplementationTheBasesModuleOfAnotherAssemblyBuildsThroughItsFactory()
		{
			const string library = """
			                       using Awaiten;

			                       namespace Lib;

			                       public interface IKettle { }
			                       public sealed class Kettle : IKettle { internal Kettle() { } }

			                       [Module]
			                       [Singleton<Kettle, IKettle>]
			                       public static partial class KettleModule { }
			                       """;
			const string consumer = """
			                        using Awaiten;
			                        using Lib;

			                        namespace MyCode;

			                        public sealed class Unused { }

			                        [Container]
			                        [Import(typeof(KettleModule))]
			                        public static partial class BaseContainer;

			                        [Container(Extends = typeof(BaseContainer))]
			                        [Singleton<Kettle, IKettle>(WhenInjectedInto = typeof(Unused))]
			                        [Singleton<Unused>]
			                        public static partial class MyContainer;
			                        """;

			GeneratorResult same = Generator.Run(library + "\n" + consumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace MyCode;", string.Empty));
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(same.Diagnostics).HasSingle().Which.Contains("AWT167");
			await That(external.Diagnostics).HasSingle().Which.Contains("AWT167")
				.Because("the extending container declares the binding that calls the inherited module's generated factory, so it reports the binding it never applies");
		}

		[Test]
		public async Task ReportsForAnExtendingContainersBindingOfAnImplementationTheBasesModuleOfAnotherAssemblyBuildsThroughAHandle()
		{
			const string library = """
			                       using Awaiten;

			                       namespace Lib;

			                       internal interface IHiddenKettle { }
			                       public interface IKettle { }
			                       public sealed class Kettle : IHiddenKettle, IKettle { }

			                       [Module]
			                       [Singleton<Kettle, IHiddenKettle>]
			                       public static partial class KettleModule { }
			                       """;
			const string consumer = """
			                        using Awaiten;
			                        using Lib;

			                        namespace MyCode;

			                        public sealed class Unused { }
			                        public sealed class OtherKettle : IKettle { }

			                        [Container]
			                        [Import(typeof(KettleModule))]
			                        [Singleton<OtherKettle, IKettle>]
			                        public static partial class BaseContainer;

			                        [Container(Extends = typeof(BaseContainer))]
			                        [Singleton<Kettle, IKettle>(WhenInjectedInto = typeof(Unused))]
			                        [Singleton<Unused>]
			                        public static partial class MyContainer;
			                        """;

			GeneratorResult same = Generator.Run(library + "\n" + consumer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace MyCode;", string.Empty));
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(same.Diagnostics).HasSingle().Which.Contains("AWT167");
			await That(external.Diagnostics).HasSingle().Which.Contains("AWT167")
				.Because("the extending container declares the binding that serves the instance of the inherited module's handle, so it reports the binding it never applies");
			await That(external.Sources.Single(source => source.Key.Contains("MyContainer")).Value).Contains("global::Lib.KettleModule.Internals.Alias_Kettle_")
				.Because("the binding is served through the alias of the module's handle");
		}
	}

	public class Awt168ContextualBindingWithKey
	{
		[Test]
		public async Task ReportsWhenARegistrationSetsBothWhenInjectedIntoAndKey()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class NeedsTest { public NeedsTest(IClock clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(Key = "test", WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<NeedsTest>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT168*").AsWildcard()
				.Because("WhenInjectedInto and Key claim the same resolution slot, so the Key is silently dropped");
		}

		[Test]
		public async Task DoesNotReportForWhenInjectedIntoAlone()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class NeedsTest { public NeedsTest(IClock clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<NeedsTest>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("WhenInjectedInto without a Key is the ordinary contextual binding");
		}
	}

	public class Awt169DuplicateContextualBinding
	{
		[Test]
		public async Task ReportsWhenTwoImplementationsTargetTheSameServiceAndConsumer()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class OtherClock : IClock { }
			                                       public sealed class NeedsTest { public NeedsTest(IClock clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<OtherClock, IClock>(WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<NeedsTest>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT169*").AsWildcard()
				.Because("both bindings claim the one contextual slot for the consumer, so the resolution is ambiguous");
		}

		[Test]
		public async Task DoesNotReportForTwoContextualBindingsIntoDifferentConsumers()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class DefaultClock : IClock { }
			                                       public sealed class TestClock : IClock { }
			                                       public sealed class OtherClock : IClock { }
			                                       public sealed class NeedsTest { public NeedsTest(IClock clock) { } }
			                                       public sealed class NeedsOther { public NeedsOther(IClock clock) { } }

			                                       [Container]
			                                       [Singleton<DefaultClock, IClock>]
			                                       [Singleton<TestClock, IClock>(WhenInjectedInto = typeof(NeedsTest))]
			                                       [Singleton<OtherClock, IClock>(WhenInjectedInto = typeof(NeedsOther))]
			                                       [Singleton<NeedsTest>]
			                                       [Singleton<NeedsOther>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("each consumer has its own contextual slot, so there is no collision");
		}
	}
}
