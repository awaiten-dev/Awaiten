using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt218Awt219KeyedResolve
	{
		private const string KeyedContainer = """
		                                      using Awaiten;
		                                      using System;
		                                      using System.Collections.Generic;
		                                      using System.Threading;
		                                      using System.Threading.Tasks;

		                                      namespace MyCode;

		                                      public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

		                                      public interface IMilk;
		                                      public sealed class OatMilk : IMilk;
		                                      public sealed class WholeMilk : IMilk;
		                                      public enum PaymentKind { Card, Cash }
		                                      public interface IPayment;
		                                      public sealed class CardPayment : IPayment;
		                                      public interface ISyrup;
		                                      public sealed class Vanilla : ISyrup;
		                                      public interface IClock;
		                                      public sealed class Database : IInitializable
		                                      {
		                                      	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                      }
		                                      public sealed class Lease : IDisposable { public void Dispose() { } }
		                                      public sealed class AsyncLease : IInitializable, IDisposable
		                                      {
		                                      	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                      	public void Dispose() { }
		                                      }
		                                      public sealed class Barista { public Barista(IClock clock) { } }

		                                      public static class Keys { public const string Oat = "Oat"; }

		                                      [Container]
		                                      [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
		                                      [Singleton<OatMilk, IMilk>(Key = "Oat")]
		                                      [Singleton<WholeMilk, IMilk>(Key = "Whole")]
		                                      [Singleton<CardPayment, IPayment>(Key = PaymentKind.Card)]
		                                      [Singleton<Vanilla, ISyrup>(Key = typeof(Vanilla))]
		                                      [Singleton<Database>(Key = "main")]
		                                      [Transient<Lease>(Key = "short")]
		                                      [Transient<AsyncLease>(Key = "short")]
		                                      [ImportService<IClock>]
		                                      [Transient<Barista>]
		                                      public static partial class MyContainer;

		                                      """;

		private static Task<string[]> Run(string body)
			=> Analyzer.Run<AwaitenAnalyzer>(KeyedContainer + $$"""
			                                                    public static class Usage
			                                                    {
			                                                    	public static async Task Use(string runtimeKey)
			                                                    	{
			                                                    		using MyContainer.Root root = new();
			                                                    		using MyContainer.Scope scope = root.CreateScope();
			                                                    {{body}}
			                                                    	}
			                                                    }
			                                                    """);

		[Test]
		public async Task DoesNotReportARegisteredKeyOfAnySupportedKind()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IMilk>("Oat");
			                                 		root.Resolve<IMilk>(Keys.Oat);
			                                 		await root.ResolveAsync<IMilk>("Whole");
			                                 		root.Resolve<IPayment>(PaymentKind.Card);
			                                 		root.Resolve<ISyrup>(typeof(Vanilla));
			                                 		root.Resolve(typeof(IMilk), "Oat");
			                                 		await root.ResolveAsync(typeof(IPayment), PaymentKind.Card);
			                                 		scope.Resolve<IMilk>("Oat");
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218") || d.Contains("AWT219"))).IsFalse()
				.Because("a string, enum or typeof key that a registration declares selects that registration");
		}

		[Test]
		public async Task ReportsAnUnregisteredStringKey()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IMilk>("Soy");
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasCount(1)
				.Because("no IMilk is registered under \"Soy\", so the keyed resolve throws at run time");
			await That(reports[0]).Contains("*'MyCode.IMilk' has no registration under the key \"Soy\"*Resolve<MyCode.IMilk>(\"Soy\")*register it under that key*").AsWildcard()
				.Because("the warning names the service, the key and the call");
		}

		[Test]
		[Arguments("IEnumerable<IMilk>")]
		[Arguments("IMilk[]")]
		[Arguments("IReadOnlyList<IMilk>")]
		[Arguments("IAsyncEnumerable<IMilk>")]
		[Arguments("Task<IReadOnlyList<IMilk>>")]
		public async Task ReportsAKeyedCollectionAsServedOnlyToInjection(string collection)
		{
			string[] diagnostics = await Run($"""
			                                  		root.Resolve<{collection}>("Oat");
			                                  """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasSingle().Which
				.Contains("*has no registration under the key \"Oat\"*the container builds a collection under a key only for injection, not for a typed resolve; inject it through a [FromKey] parameter or property instead").AsWildcard()
				.Because("IMilk is registered under \"Oat\", so registering it under that key would not help: the keyed by-type dispatch serves registrations, never a collection of them");
		}

		[Test]
		[Arguments("Func<IMilk>", "System.Func<MyCode.IMilk>")]
		[Arguments("Lazy<IMilk>", "System.Lazy<MyCode.IMilk>")]
		[Arguments("Owned<IMilk>", "Awaiten.Owned<MyCode.IMilk>")]
		[Arguments("Func<Owned<IMilk>>", "System.Func<Awaiten.Owned<MyCode.IMilk>>")]
		[Arguments("Task<IMilk>", "System.Threading.Tasks.Task<MyCode.IMilk>")]
		public async Task ReportsAKeyedRelationshipAsServedOnlyToInjection(string relationship, string display)
		{
			string[] diagnostics = await Run($"""
			                                  		root.Resolve<{relationship}>("Oat");
			                                  """);

			await That(diagnostics.Where(d => d.Contains("AWT218"))).HasSingle().Which
				.Contains($"*has no registration under the key \"Oat\"*a keyed resolve serves only plain services, not the relationships over them; resolve 'MyCode.IMilk' under that key, or inject '{display}' through a [FromKey] parameter or property instead").AsWildcard()
				.Because("IMilk is registered under \"Oat\", but the keyed by-type dispatch serves registrations, never a relationship over one");
		}

		[Test]
		public async Task DoesNotReportTheKeyedRelationshipsTheRemedyInjects()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using Awaiten;
			                                                           using System;
			                                                           using System.Threading.Tasks;

			                                                           namespace MyCode;

			                                                           public interface IMilk;
			                                                           public sealed class OatMilk : IMilk;
			                                                           public sealed class Latte
			                                                           {
			                                                           	public Latte(
			                                                           		[FromKey("Oat")] Func<IMilk> func,
			                                                           		[FromKey("Oat")] Lazy<IMilk> lazy,
			                                                           		[FromKey("Oat")] Owned<IMilk> owned,
			                                                           		[FromKey("Oat")] Func<Owned<IMilk>> owner,
			                                                           		[FromKey("Oat")] Task<IMilk> task) { }
			                                                           }

			                                                           [Container]
			                                                           [Transient<OatMilk, IMilk>(Key = "Oat")]
			                                                           [Transient<Latte>]
			                                                           public static partial class MyContainer;
			                                                           """);

			await That(diagnostics).IsEmpty()
				.Because("[FromKey] injection builds every relationship shape over a keyed registration, which the AWT218 remedy points to");
		}

		[Test]
		public async Task ReportsAKeyedRelationshipOfAnUnregisteredKeyWithTheRegistration()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<Func<IMilk>>("Soy");
			                                 """);

			await That(diagnostics.Where(d => d.Contains("AWT218"))).HasSingle().Which
				.Contains("*a keyed resolve serves only plain services, not the relationships over them; register 'MyCode.IMilk' under that key, then resolve 'MyCode.IMilk' or inject 'System.Func<MyCode.IMilk>' through a [FromKey] parameter or property").AsWildcard()
				.Because("nothing is registered under \"Soy\", and a registration alone would still not serve the relationship by type");
		}

		[Test]
		public async Task ReportsAKeyedFactoryOfAServiceWithoutRuntimeArgumentsWithTheRegistration()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<Func<int, IMilk>>("Oat");
			                                 """);

			await That(diagnostics.Where(d => d.Contains("AWT218"))).HasSingle().Which
				.Contains("*has no registration under the key \"Oat\"*register it under that key on the [Container], or call TryResolve if it is optional").AsWildcard()
				.And.DoesNotContain("[FromKey]")
				.Because("IMilk takes no runtime arguments, so injecting a Func<int, IMilk> would be AWT113, and only a registration of the factory itself serves it");
		}

		[Test]
		[Arguments("Lazy<Owned<IMilk>>")]
		[Arguments("Lazy<Task<Owned<IMilk>>>")]
		public async Task ReportsAKeyedLazyOverOwnedWithTheShapesThatProduceAHandle(string relationship)
		{
			string[] diagnostics = await Run($"""
			                                  		root.Resolve<{relationship}>("Oat");
			                                  """);

			await That(diagnostics.Where(d => d.Contains("AWT218"))).HasSingle().Which
				.Contains("*an Owned<T> disposal handle cannot be produced through Lazy<…>, and a keyed resolve serves only plain services; inject Owned<T>, Func<…, Owned<T>>, Task<Owned<T>> or Func<…, Task<Owned<T>>> through a [FromKey] parameter or property instead").AsWildcard()
				.Because("injecting the requested Lazy over an Owned<T> under the key would be AWT121");
		}

		[Test]
		[Arguments("Func<Database>", "System.Func<System.Threading.Tasks.Task<MyCode.Database>>")]
		[Arguments("Lazy<Database>", "System.Lazy<System.Threading.Tasks.Task<MyCode.Database>>")]
		[Arguments("Owned<Database>", "System.Threading.Tasks.Task<Awaiten.Owned<MyCode.Database>>")]
		[Arguments("Func<Owned<Database>>", "System.Func<System.Threading.Tasks.Task<Awaiten.Owned<MyCode.Database>>>")]
		[Arguments("Task<Database>", "System.Threading.Tasks.Task<MyCode.Database>")]
		public async Task ReportsAKeyedRelationshipOverAnAsyncInitializedServiceWithItsTaskForm(string relationship, string asyncShape)
		{
			string[] diagnostics = await Run($"""
			                                  		root.Resolve<{relationship}>("main");
			                                  """);

			await That(diagnostics.Where(d => d.Contains("AWT218"))).HasSingle().Which
				.Contains($"*'MyCode.Database' is async-initialized, and a keyed resolve serves only plain services, not the relationships over them; resolve 'MyCode.Database' under that key through ResolveAsync, or inject '{asyncShape}' through a [FromKey] parameter or property instead").AsWildcard()
				.Because("a synchronous relationship over an async-initialized service is AWT119 for injection too, so the remedy names the Task form");
		}

		[Test]
		public async Task DoesNotReportTheKeyedTaskFormsTheRemedyInjects()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Threading;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }
			                                       public sealed class Database : IInitializable
			                                       {
			                                       	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }
			                                       public sealed class Report
			                                       {
			                                       	public Report(
			                                       		[FromKey("main")] Func<Task<Database>> func,
			                                       		[FromKey("main")] Lazy<Task<Database>> lazy,
			                                       		[FromKey("main")] Task<Owned<Database>> owned,
			                                       		[FromKey("main")] Func<Task<Owned<Database>>> owner) { }
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<Database>(Key = "main")]
			                                       [Transient<Report>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("[FromKey] injection builds the Task form of every relationship over an async-initialized keyed service, which the AWT218 remedy points to");
		}

		[Test]
		public async Task ReportsAKeyedParameterizedServiceWithItsFactoryForInjection()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using Awaiten;
			                                                           using System;

			                                                           namespace MyCode;

			                                                           public sealed class Cup { public Cup([Arg] int size) { } }
			                                                           public sealed class Counter
			                                                           {
			                                                           	public Counter([FromKey("large")] Func<int, Cup> cups, [FromKey("large")] Func<int, Owned<Cup>> owned) { }
			                                                           }

			                                                           [Container]
			                                                           [Transient<Cup>(Key = "large")]
			                                                           [Transient<Counter>]
			                                                           public static partial class MyContainer;

			                                                           public static class Usage
			                                                           {
			                                                           	public static void Use()
			                                                           	{
			                                                           		using MyContainer.Root root = new();
			                                                           		root.Resolve<Cup>("large");
			                                                           		root.Resolve<Owned<Cup>>("large");
			                                                           		root.Resolve<Func<int, Cup>>("large");
			                                                           	}
			                                                           }
			                                                           """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasCount(3);
			await That(reports[2]).Contains("*has no registration under the key \"large\"*inject its factory 'System.Func<int, MyCode.Cup>' through a [FromKey] parameter or property instead").AsWildcard()
				.Because("the keyed by-type dispatch does not serve the factory itself either");
			await That(reports[0]).Contains("*'MyCode.Cup' is built from runtime arguments, and a keyed resolve serves only plain services, not their factories; inject its factory 'System.Func<int, MyCode.Cup>' through a [FromKey] parameter or property instead").AsWildcard()
				.Because("the keyed by-type dispatch serves neither the service nor its factory, while [FromKey] injection, as Counter shows, does");
			await That(reports[1]).Contains("*inject its factory 'System.Func<int, Awaiten.Owned<MyCode.Cup>>' through a [FromKey] parameter or property instead").AsWildcard()
				.Because("an Owned request names the factory of the handle");
		}

		[Test]
		public async Task ReportsAnUnregisteredKeyOnEveryKeyedOverload()
		{
			string[] diagnostics = await Run("""
			                                 		await root.ResolveAsync<IMilk>("Soy");
			                                 		root.Resolve(typeof(IMilk), "Soy");
			                                 		await root.ResolveAsync(typeof(IMilk), "Soy");
			                                 		scope.Resolve<IMilk>("Soy");
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasCount(4)
				.Because("every keyed resolve overload on the Root or a Scope dispatches through the same keyed table");
			await That(reports.Any(report => report.Contains("this Resolve(typeof(MyCode.IMilk), \"Soy\") throws"))).IsTrue()
				.Because("a Type overload is named with its typeof and key arguments");
		}

		[Test]
		public async Task ReportsAnUnregisteredEnumKey()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IPayment>(PaymentKind.Cash);
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasCount(1)
				.Because("only PaymentKind.Card is registered");
			await That(reports[0]).Contains("*under the key MyCode.PaymentKind.Cash*").AsWildcard()
				.Because("an enum key is named by its member");
		}

		[Test]
		public async Task ReportsAKeyOfAnotherKindWithTheSameText()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IPayment>("Card");
			                                 		root.Resolve<ISyrup>("MyCode.Vanilla");
			                                 """);

			await That(diagnostics.Count(d => d.Contains("AWT218"))).IsEqualTo(2)
				.Because("the keyed table compares keys with object.Equals, so a string never matches an enum or a Type key");
		}

		[Test]
		public async Task DoesNotReportANonConstantKey()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IMilk>(runtimeKey);
			                                 		root.Resolve(typeof(IMilk), runtimeKey);
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("a key only known at run time stays a run-time check");
		}

		[Test]
		public async Task DoesNotReportKeyedDictionaries()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IReadOnlyDictionary<string, IMilk>>();
			                                 		root.Resolve<IReadOnlyDictionary<PaymentKind, IPayment>>();
			                                 		root.Resolve(typeof(IReadOnlyDictionary<string, IMilk>));
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("the container synthesizes a dictionary over the keyed registrations of a service");
		}

		[Test]
		public async Task DoesNotReportKeyedTryResolve()
		{
			string[] diagnostics = await Run("""
			                                 		root.TryResolve<IMilk>("Soy", out IMilk? _);
			                                 		root.TryResolve(typeof(IMilk), "Soy", out object? _);
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("TryResolve reports absence by returning false, which is its purpose rather than a failure");
		}

		[Test]
		public async Task DoesNotReportAnImportedService()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<IClock>("utc");
			                                 		root.Resolve(typeof(IClock), "utc");
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("an imported service comes from the host, which the generator cannot see");
		}

		[Test]
		public async Task ReportsAKeyedResolveUnderImportServices()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                          using Awaiten;

			                                                          namespace MyCode;

			                                                          public interface IClock;

			                                                          [Container]
			                                                          [ImportServices]
			                                                          public static partial class HostedContainer;

			                                                          public static class Usage
			                                                          {
			                                                          	public static void Use()
			                                                          	{
			                                                          		using HostedContainer.Root root = new();
			                                                          		root.Resolve<IClock>();
			                                                          		root.Resolve<IClock>("utc");
			                                                          	}
			                                                          }
			                                                          """);

			await That(diagnostics.Count(d => d.Contains("AWT218"))).IsEqualTo(1)
				.Because("[ImportServices] routes only unkeyed requests to the host, so only the keyed resolve throws");
		}

		[Test]
		public async Task ReportsASynchronousKeyedResolveOfAnAsyncInitializedService()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<Database>("main");
			                                 		await root.ResolveAsync<Database>("main");
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT219")).ToArray();
			await That(reports).HasCount(1)
				.Because("only the synchronous resolve of the async-initialized service throws");
			await That(reports[0]).Contains("*Resolve<MyCode.Database>(\"main\")*requires asynchronous initialization*").AsWildcard()
				.Because("the warning carries the same guidance the run-time exception would");
		}

		[Test]
		public async Task ReportsAKeyedResolveOfARootWithheldTransientOnlyOnTheRoot()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve<Lease>("short");
			                                 		scope.Resolve<Lease>("short");
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT219")).ToArray();
			await That(reports).HasCount(1)
				.Because("strict lifetime safety withholds a disposable transient from the Root, while a child scope serves it");
			await That(reports[0]).Contains("*withheld*Owned<MyCode.Lease>*").AsWildcard()
				.Because("the guidance names the leak-free alternatives");
		}

		[Test]
		public async Task ReportsAKeyedResolveAsyncOfARootWithheldAsyncTransientOnlyOnTheRoot()
		{
			string[] diagnostics = await Run("""
			                                 		await root.ResolveAsync<AsyncLease>("short");
			                                 		await scope.ResolveAsync<AsyncLease>("short");
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT219")).ToArray();
			await That(reports).HasCount(1)
				.Because("the keyed async arm throws on the Root for a disposable async transient, while a child scope serves it");
			await That(reports[0]).Contains("*ResolveAsync<MyCode.AsyncLease>(\"short\")*").AsWildcard()
				.Because("the warning names the keyed call");
		}

		[Test]
		public async Task DoesNotReportWhenSuppressedInSource()
		{
			string[] diagnostics = await Run("""
			                                 #pragma warning disable AWT218
			                                 		root.Resolve<IMilk>("Soy");
			                                 #pragma warning restore AWT218
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("the warning is reported by the analyzer at the call, so a deliberate call can be suppressed in source");
		}
	}

	public class Awt218Awt219TypeOfResolve
	{
		private const string Container = """
		                                 using Awaiten;
		                                 using System;
		                                 using System.Threading;
		                                 using System.Threading.Tasks;

		                                 namespace MyCode;

		                                 public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }

		                                 public interface IGen<T>;
		                                 public sealed class Gen<T> : IGen<T>;
		                                 public interface IClock;
		                                 public sealed class Consumer { public Consumer(IGen<int> numbers, IClock clock) { } }
		                                 public sealed class Database : IInitializable
		                                 {
		                                 	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                 }

		                                 [Container]
		                                 [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
		                                 [Transient(typeof(Gen<>), typeof(IGen<>))]
		                                 [Transient<Consumer>]
		                                 [Singleton<Database>]
		                                 [ImportService<IClock>]
		                                 public static partial class MyContainer;

		                                 """;

		private static Task<string[]> Run(string body)
			=> Analyzer.Run<AwaitenAnalyzer>(Container + $$"""
			                                               public static class Usage
			                                               {
			                                               	public static async Task Use(Type runtimeType)
			                                               	{
			                                               		using MyContainer.Root root = new();
			                                               {{body}}
			                                               	}
			                                               }
			                                               """);

		[Test]
		public async Task DoesNotReportAProvidedService()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve(typeof(IGen<int>));
			                                 		root.Resolve(typeof(Func<Consumer>));
			                                 		await root.ResolveAsync(typeof(Database));
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218") || d.Contains("AWT219"))).IsFalse()
				.Because("the container provides each of these types through that call");
		}

		[Test]
		public async Task ReportsAnUnprovidedService()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve(typeof(IGen<string>));
			                                 """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasCount(1)
				.Because("the open registration is closed only over int, so resolving the string closing throws at run time");
			await That(reports[0]).Contains("*'MyCode.IGen<string>' has no unkeyed registration*Resolve(typeof(MyCode.IGen<string>))*[Transient<MyCode.Gen<string>, MyCode.IGen<string>>]*").AsWildcard()
				.Because("the warning names the service, the call and the closed registration that fixes it");
		}

		[Test]
		public async Task ReportsAnUnprovidedServiceOnResolveAsync()
		{
			string[] diagnostics = await Run("""
			                                 		await root.ResolveAsync(typeof(IGen<string>));
			                                 """);

			await That(diagnostics.Count(d => d.Contains("AWT218"))).IsEqualTo(1)
				.Because("ResolveAsync(Type) throws for an unregistered service just like Resolve(Type)");
		}

		[Test]
		public async Task ReportsASynchronousResolveOfAnAsyncInitializedService()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve(typeof(Database));
			                                 """);

			await That(diagnostics.Count(d => d.Contains("AWT219"))).IsEqualTo(1)
				.Because("an async-initialized service has no synchronous resolution in the strict default");
		}

		[Test]
		public async Task DoesNotReportATypeOtherThanATypeOfArgument()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve(runtimeType);
			                                 		root.Resolve(typeof(IGen<string>).GetGenericTypeDefinition());
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("only a typeof argument names the service in source");
		}

		[Test]
		public async Task DoesNotReportAnImportedService()
		{
			string[] diagnostics = await Run("""
			                                 		root.Resolve(typeof(IClock));
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("an imported service comes from the host, which the generator cannot see");
		}

		[Test]
		public async Task DoesNotReportWhenSuppressedInSource()
		{
			string[] diagnostics = await Run("""
			                                 #pragma warning disable AWT218
			                                 		root.Resolve(typeof(IGen<string>));
			                                 #pragma warning restore AWT218
			                                 """);

			await That(diagnostics.Any(d => d.Contains("AWT218"))).IsFalse()
				.Because("the warning is reported by the analyzer at the call, so a deliberate call can be suppressed in source");
		}
	}
}
