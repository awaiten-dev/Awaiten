namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt262ConsumerOwnedNeverApplies
	{
		private static GeneratorResult Run(string consumer, string registrations)
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;

			                   namespace MyCode;

			                   public sealed class Debouncer : IDisposable { public void Dispose() { } }
			                   {{consumer}}

			                   [Container]
			                   {{registrations}}
			                   [Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]
			                   public static partial class MyContainer;
			                   """);

		[Test]
		public async Task ReportsAServiceTheConsumerDoesNotTakeThroughAFunc()
		{
			GeneratorResult result = Run("public sealed class StorageFactory { public StorageFactory(Lazy<Debouncer> debouncer) { } }", "[Transient<Debouncer>]");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(11,2): warning AWT262: 'MyCode.StorageFactory' lists 'MyCode.Debouncer' in ConsumerOwned, but takes no Func or dispatch dictionary over a transient or parameterized 'MyCode.Debouncer' that the container disposes, so the entry has no effect; remove it")
				.Because("only a Func or a dispatch dictionary builds instances the consumer could own");
		}

		[Test]
		public async Task ReportsASharedService()
		{
			GeneratorResult result = Run("public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }", "[Singleton<Debouncer>]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*warning AWT262*").AsWildcard()
				.Because("a Func over a singleton returns the shared instance, which the container keeps owning");
		}

		[Test]
		public async Task DoesNotReportAFuncOverATransient()
		{
			GeneratorResult result = Run("public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }", "[Transient<Debouncer>]");

			await That(result.Diagnostics).IsEmpty()
				.Because("the consumer takes a Func over the transient debouncer");
		}

		[Test]
		[Arguments("[Transient<Debouncer>(SuppressDisposal = true)]")]
		[Arguments("[Transient<Debouncer>(Factory = nameof(Make), SuppressDisposal = true)]")]
		public async Task ReportsAServiceTheContainerNeverDisposes(string registration)
		{
			GeneratorResult result = Generator.Run($$"""
			                                        using Awaiten;
			                                        using System;

			                                        namespace MyCode;

			                                        public sealed class Debouncer : IDisposable { public void Dispose() { } }
			                                        public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }

			                                        [Container]
			                                        {{registration}}
			                                        [Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]
			                                        public static partial class MyContainer
			                                        {
			                                            private static Debouncer Make() => new();
			                                        }
			                                        """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*warning AWT262*").AsWildcard()
				.Because("the container never disposes the debouncer, so there is nothing for the consumer to take over");
		}

		[Test]
		public async Task ReportsANonDisposableService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public sealed class Ticket { }
			                                       public sealed class Counter { public Counter(Func<Ticket> tickets) { } }

			                                       [Container]
			                                       [Transient<Ticket>]
			                                       [Singleton<Counter>(ConsumerOwned = [typeof(Ticket)])]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*warning AWT262*'MyCode.Counter' lists 'MyCode.Ticket'*").AsWildcard()
				.Because("a ticket is neither disposable nor builds anything the owner tracks, so the entry changes nothing");
		}

		[Test]
		public async Task DoesNotReportAnEntryThatAnotherClosingOfTheConsumerUses()
		{
			GeneratorResult result = RunOpenConsumer("public sealed class Shop { public Shop(Shelf<Debouncer> debouncers, Shelf<Timer> timers) { } }");

			await That(result.Diagnostics).IsEmpty()
				.Because("the entry applies to Shelf<Debouncer>, so removing it would change that closing");
		}

		[Test]
		public async Task ReportsAnEntryNoClosingUsesOnce()
		{
			GeneratorResult result = RunOpenConsumer("public sealed class Shop { public Shop(Shelf<Timer> timers, Shelf<Clock> clocks) { } }");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*warning AWT262*'MyCode.Shelf<>' lists 'MyCode.Debouncer'*").AsWildcard()
				.Because("the closings share one registration, which is reported once and named by its definition");
		}

		private static GeneratorResult RunOpenConsumer(string shop)
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;

			                   namespace MyCode;

			                   public sealed class Debouncer : IDisposable { public void Dispose() { } }
			                   public sealed class Timer : IDisposable { public void Dispose() { } }
			                   public sealed class Clock : IDisposable { public void Dispose() { } }
			                   public sealed class Shelf<T> { public Shelf(Func<T> items) { } }
			                   {{shop}}

			                   [Container]
			                   [Transient<Debouncer>]
			                   [Transient<Timer>]
			                   [Transient<Clock>]
			                   [Scoped(typeof(Shelf<>), ConsumerOwned = [typeof(Debouncer)])]
			                   [Scoped<Shop>]
			                   public static partial class MyContainer;
			                   """);
	}
}
