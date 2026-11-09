using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt123Awt124Decorator
	{
		[Test]
		public async Task ReportsAwt123WhenTheDecoratedServiceHasNoRegistration()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       // IService has no registration to decorate.
			                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner) { } }

			                                       [Container]
			                                       [Decorate<LoggingDecorator, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT123*").AsWildcard()
				.Because("a [Decorate] over a service with no registration has nothing to wrap");
		}

		[Test]
		public async Task ReportsAwt124WhenTheDecoratorHasNoSingleInnerParameter()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       // Two IService parameters: ambiguous which one receives the inner instance.
			                                       public sealed class BadDecorator : IService { public BadDecorator(IService a, IService b) { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<BadDecorator, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT124*").AsWildcard()
				.Because("a decorator with more than one parameter assignable to the service is ambiguous");
		}

		[Test]
		public async Task ReportsAwt124WhenTheDecoratorTakesNoInnerParameter()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       // No IService parameter at all: nothing receives the inner instance.
			                                       public sealed class BadDecorator : IService { public BadDecorator() { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<BadDecorator, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT124*").AsWildcard()
				.Because("a decorator with no parameter assignable to the service cannot receive the inner instance");
		}

		[Test]
		public async Task DoesNotReportForAWellFormedDecorator()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner) { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<LoggingDecorator, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a decorator with exactly one inner parameter over a registered service is well-formed");
		}

		[Test]
		public async Task DoesNotReportAwt124WhenAnExtraParameterIsAlsoAssignableFromTheService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       // (IService inner, object state): `object` is technically assignable-from IService,
			                                       // but the inner is the most-derived assignable parameter, so it is unambiguous.
			                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner, object state) { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<LoggingDecorator, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT124*").AsWildcard()
				.Because("the inner is the most-derived assignable parameter, so an unrelated `object` sibling is not ambiguous");
		}

		[Test]
		public async Task DoesNotReportAwt124ForADecoratorWithAnInternalConstructor()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       // The container builds the decorator through its internal (same-assembly) constructor,
			                                       // so validation must consider it too - a public-only check would wrongly report AWT124.
			                                       public sealed class LoggingDecorator : IService { internal LoggingDecorator(IService inner) { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<LoggingDecorator, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the decorator's internal same-assembly constructor is the one the container constructs, so it is well-formed");
		}

		[Test]
		public async Task ReportsAwt124WhenTheOnlyServiceAssignableParameterIsKeyed()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       // The single IService parameter is [FromKey]-ed: it selects a specific keyed
			                                       // registration, so it is a separate dependency, not the chain inner - leaving the
			                                       // decorator with no unkeyed parameter to receive the inner instance.
			                                       public sealed class Deco : IService { public Deco([FromKey("k")] IService inner) { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<Deco, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT124*").AsWildcard()
				.Because("a [FromKey] parameter is not the chain inner, so the decorator has no parameter to receive it");
		}

		[Test]
		public async Task DiagnosticsNameTheRealDecoratorTypeNotTheSyntheticIdentity()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       // Depending on the collection of the decorated service closes a cycle (the collection
			                                       // now yields the decorator itself), so this reports AWT102 naming the decorator.
			                                       public sealed class Deco : IService { public Deco(IService inner, IEnumerable<IService> all) { } }

			                                       [Container]
			                                       [Transient<Real, IService>]
			                                       [Decorate<Deco, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT102*MyCode.Deco*").AsWildcard()
				.Because("the cycle diagnostic must name the real decorator type");
			await That(result.Diagnostics).DoesNotContain("*@__dec:*").AsWildcard()
				.Because("the internal synthetic '@__dec:' identity must not leak into user-facing diagnostics");
		}

		[Test]
		public async Task CaptiveDependencyDiagnosticNamesTheRealDecoratorType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IService { }
			                                       public sealed class Real : IService { }
			                                       public sealed class Narrower { }
			                                       // The decorator inherits Real's singleton lifetime and takes a scoped side-dependency,
			                                       // so the singleton decorator captively holds the scoped Narrower - reported as AWT105.
			                                       public sealed class Deco : IService { public Deco(IService inner, Narrower narrower) { } }

			                                       [Container]
			                                       [Singleton<Real, IService>]
			                                       [Scoped<Narrower>]
			                                       [Decorate<Deco, IService>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT105*MyCode.Deco*").AsWildcard()
				.Because("the captive-dependency diagnostic must name the real decorator type");
			await That(result.Diagnostics).DoesNotContain("*@__dec:*").AsWildcard()
				.Because("the internal synthetic '@__dec:' identity must not leak into the captive-dependency diagnostic");
		}

		public class Imports
		{
			[Test]
			public async Task DoesNotReportAwt123ForADecoratorOverAnImportedService()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IService { }
				                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner) { } }

				                                       [Container]
				                                       [ImportService<IService>]
				                                       [Decorate<LoggingDecorator, IService>]
				                                       [Decorate<LoggingDecorator, IService>(Keys = ["primary"])]
				                                       public static partial class MyContainer;
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("a decorator wraps the instance the host provides under each key it selects, and its chain is no registration that contradicts the import (AWT175)");
			}

			[Test]
			public async Task ReportsAwt123ForAllKeysOverAnImportedService()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IService { }
				                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner) { } }

				                                       [Container]
				                                       [ImportService<IService>]
				                                       [Decorate<LoggingDecorator, IService>(AllKeys = true)]
				                                       public static partial class MyContainer;
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT123*'MyCode.IService' is decorated by 'MyCode.LoggingDecorator' under all keys*only the host knows the keys*name the keys to decorate in Keys*").AsWildcard()
					.Because("the container cannot see the keys the host provides an import under");
			}

			[Test]
			public async Task ReportsAwt123ForAnOpenDecoratorOverAnOpenImport()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IFeed<T> { }
				                                       public sealed class AuditedFeed<T> : IFeed<T> { public AuditedFeed(IFeed<T> inner) { } }
				                                       public sealed class Reader { public Reader(IFeed<int> feed) { } }

				                                       [Container]
				                                       [ImportService(typeof(IFeed<>))]
				                                       [Transient<Reader>]
				                                       [Decorate(typeof(AuditedFeed<>), typeof(IFeed<>))]
				                                       public static partial class MyContainer;
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT123*'MyCode.IFeed<T>' is decorated by the open decorator 'MyCode.AuditedFeed<T>'*imported as an open generic*[ImportService<T>]*").AsWildcard()
					.Because("the closings of an open import are known only from the dependencies, so the decorator would silently wrap none");
			}

			[Test]
			public async Task ReportsAwt123UnderImportServicesForAnUndeclaredService()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IService { }
				                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner) { } }

				                                       [Container]
				                                       [ImportServices]
				                                       [Decorate<LoggingDecorator, IService>]
				                                       public static partial class MyContainer;
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT123*[ImportServices] declares no service imported*declare it with [ImportService]*").AsWildcard()
					.Because("the blanket fall-through declares no type, so a misspelled decorated service is not silently wrapped around a host lookup");
			}

			[Test]
			public async Task ReportsAwt105WhenASingletonCapturesADecoratorOfAnImportThatTakesAScopedService()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IService { }
				                                       public sealed class Session { }
				                                       public sealed class SessionDecorator : IService { public SessionDecorator(IService inner, Session session) { } }
				                                       public sealed class Consumer { public Consumer(IService service) { } }

				                                       [Container]
				                                       [ImportService<IService>]
				                                       [Scoped<Session>]
				                                       [Singleton<Consumer>]
				                                       [Decorate<SessionDecorator, IService>]
				                                       public static partial class MyContainer;
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT105*Consumer*Session*").AsWildcard()
					.Because("the decorator chain over an import is transient, so the singleton captures the scoped service through it");
			}

			[Test]
			public async Task ReportsAwt175WhenTheDecoratedImportIsAlsoRegistered()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IService { }
				                                       public sealed class Real : IService { }
				                                       public sealed class LoggingDecorator : IService { public LoggingDecorator(IService inner) { } }

				                                       [Container]
				                                       [ImportService<IService>]
				                                       [Singleton<Real, IService>]
				                                       [Decorate<LoggingDecorator, IService>]
				                                       public static partial class MyContainer;
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT175*").AsWildcard()
					.Because("the decorator wraps the registration, which still contradicts the import");
			}
		}
	}
}
