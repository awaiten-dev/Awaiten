namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt259RegisteredRelationshipType
	{
		private static GeneratorResult Run(string registrations, string consumer, string members = "")
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;
			                   using System.Collections.Generic;
			                   using System.Threading.Tasks;

			                   namespace MyCode;

			                   public interface IClock { }
			                   public sealed class SystemClock : IClock { }
			                   public sealed class Reader { {{consumer}} }

			                   [Container]
			                   {{registrations}}
			                   [Singleton<Reader>]
			                   public static partial class MyContainer
			                   {
			                       {{members}}
			                   }
			                   """);

		[Test]
		public async Task ReportsAFuncRegisteredAsAServiceAtTheDependency()
		{
			GeneratorResult result = Run(
				"[Singleton<SystemClock, IClock>]\n[Singleton<Func<IClock>>(Factory = nameof(CreateClockFactory))]",
				"public Reader(Func<IClock> clock) { }",
				"private static Func<IClock> CreateClockFactory() => () => new SystemClock();");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(10,57): error AWT259: 'MyCode.Reader' cannot be resolved: 'MyCode.Reader' requires 'System.Func<MyCode.IClock>', which the container registers as a service, but it injects 'System.Func<MyCode.IClock>' as the relationship over 'MyCode.IClock' and never hands out that service; register a named type, such as an interface, in place of 'System.Func<MyCode.IClock>', or register 'MyCode.IClock' and let the container build the relationship")
				.Because("the parameter would silently get the built-in Func over the IClock registration instead of the registered Func<IClock>");
		}

		[Test]
		public async Task ReportsInPlaceOfAMissingWrappedService()
		{
			GeneratorResult result = Run(
				"[Singleton<Func<IClock>>(Factory = nameof(CreateClockFactory))]",
				"public Reader(Func<IClock> clock) { }",
				"private static Func<IClock> CreateClockFactory() => () => new SystemClock();");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*").AsWildcard()
				.Because("AWT101 would claim IClock is missing, although the cause is that the registered Func<IClock> is never injected");
		}

		[Test]
		[Arguments("Func<IClock>")]
		[Arguments("Lazy<IClock>")]
		[Arguments("Task<IClock>")]
		[Arguments("Func<Owned<IClock>>")]
		[Arguments("Func<Task<IClock>>")]
		[Arguments("Lazy<Task<IClock>>")]
		[Arguments("Task<Owned<IClock>>")]
		[Arguments("Func<IEnumerable<IClock>>")]
		public async Task ReportsEveryRelationshipShape(string shape)
		{
			GeneratorResult result = Run(
				$"[Singleton<SystemClock, IClock>]\n[Singleton<{shape}>(Instance = nameof(Registered))]",
				$"public Reader({shape} clock) {{ }}",
				$"private static {shape} Registered => null!;");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*'MyCode.Reader' requires '*', which the container registers as a service, but it injects * as the relationship over *").AsWildcard()
				.Because("injection builds every relationship shape itself, so none of them reaches the registration");
		}

		[Test]
		public async Task ReportsAKeyedRegistrationForTheKeyedDependency()
		{
			GeneratorResult result = Run(
				"[Singleton<SystemClock, IClock>(Key = \"utc\")]\n[Singleton<Lazy<IClock>>(Key = \"utc\", Instance = nameof(Registered))]",
				"public Reader([FromKey(\"utc\")] Lazy<IClock> clock) { }",
				"private static Lazy<IClock> Registered => null!;");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*requires 'System.Lazy<MyCode.IClock> (key: utc)', which the container registers as a service, but it injects 'System.Lazy<MyCode.IClock> (key: utc)' as the relationship over 'MyCode.IClock (key: utc)'*register 'MyCode.IClock (key: utc)' and let the container build the relationship").AsWildcard()
				.Because("a [FromKey] relationship is built over the keyed service, never from the keyed registration of the relationship type");
		}

		[Test]
		public async Task ReportsAParameterizedFactory()
		{
			GeneratorResult result = Run(
				"[Singleton<Func<int, IClock>>(Key = \"k\", Instance = nameof(Registered))]",
				"public Reader([FromKey(\"k\")] Func<int, IClock> clocks) { }",
				"private static Func<int, IClock> Registered => null!;");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*requires 'System.Func<int, MyCode.IClock> (key: k)'*").AsWildcard()
				.Because("a Func with runtime arguments is the factory relationship of the keyed IClock, not the registered delegate");
		}

		[Test]
		public async Task ReportsAFactoryParameter()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }
			                                       public sealed class Reader { public Reader(IClock clock) { } }

			                                       [Container]
			                                       [Singleton<Func<IClock>>(Instance = nameof(Registered))]
			                                       [Singleton<Reader>(Factory = nameof(CreateReader))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static Func<IClock> Registered => () => new SystemClock();
			                                           private static Reader CreateReader(Func<IClock> clock) => new(clock());
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*'MyCode.Reader' requires 'System.Func<MyCode.IClock>'*").AsWildcard()
				.Because("a factory parameter is classified like a constructor parameter");
		}

		[Test]
		[Arguments("[Inject] public Func<IClock> Clock { get; set; } = null!;")]
		[Arguments("[Inject(Optional = true)] public Func<IClock> Clock { get; set; } = null!;")]
		public async Task ReportsAnInjectedProperty(string property)
		{
			GeneratorResult result = Run(
				"[Singleton<Func<IClock>>(Instance = nameof(Registered))]",
				property,
				"private static Func<IClock> Registered => () => new SystemClock();");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*'MyCode.Reader' requires 'System.Func<MyCode.IClock>'*").AsWildcard()
				.Because("an injected property is classified like a constructor parameter, and Optional does not make the registration reachable");
		}

		[Test]
		public async Task ReportsAnImportedRelationshipType()
		{
			GeneratorResult result = Run(
				"[Singleton<SystemClock, IClock>]\n[ImportService<Func<IClock>>]",
				"public Reader(Func<IClock> clock) { }");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT259*requires 'System.Func<MyCode.IClock>', which the container imports as a service, but it injects 'System.Func<MyCode.IClock>' as the relationship over 'MyCode.IClock' and never hands out that service; import a named type, such as an interface, in place of 'System.Func<MyCode.IClock>'").AsWildcard()
				.Because("only a direct dependency is routed to the external resolver, so the imported Func<IClock> never reaches the consumer");
		}

		[Test]
		public async Task DoesNotReportARegistrationNothingInjects()
		{
			GeneratorResult result = Run(
				"[Singleton<Func<IClock>>(Instance = nameof(Registered))]",
				"public Reader() { }",
				"private static Func<IClock> Registered => () => new SystemClock();");

			await That(result.Diagnostics).IsEmpty()
				.Because("a registered relationship type is still served by a typed resolve and the MS.DI bridge");
		}

		[Test]
		public async Task DoesNotReportACollectionOfTheRegistration()
		{
			GeneratorResult result = Run(
				"[Singleton<Func<IClock>>(Instance = nameof(Registered))]",
				"public Reader(IEnumerable<Func<IClock>> clocks) { }",
				"private static Func<IClock> Registered => () => new SystemClock();");

			await That(result.Diagnostics).IsEmpty()
				.Because("a collection of the registered type holds that registration");
		}

		[Test]
		public async Task DoesNotReportARegistrationUnderAnotherKey()
		{
			GeneratorResult result = Run(
				"[Singleton<SystemClock, IClock>]\n[Singleton<Func<IClock>>(Key = \"k\", Instance = nameof(Registered))]",
				"public Reader(Func<IClock> clock) { }",
				"private static Func<IClock> Registered => () => new SystemClock();");

			await That(result.Diagnostics).IsEmpty()
				.Because("the unkeyed Func<IClock> has no registration that it could bypass");
		}

		[Test]
		[Arguments("Task<Func<IClock>>")]
		[Arguments("Owned<Func<IClock>>")]
		public async Task DoesNotReportARelationshipOverTheRegistration(string shape)
		{
			GeneratorResult result = Run(
				"[Singleton<Func<IClock>>(Instance = nameof(Registered))]",
				$"public Reader({shape} clock) {{ }}",
				"private static Func<IClock> Registered => () => new SystemClock();");

			await That(result.Diagnostics).IsEmpty()
				.Because("the dependency wraps the registered Func<IClock> itself, so it does not bypass the registration");
		}
	}
}
