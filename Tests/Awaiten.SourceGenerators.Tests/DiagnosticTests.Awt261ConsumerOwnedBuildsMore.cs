using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt261ConsumerOwnedBuildsMore
	{
		private static GeneratorResult Run(string types, string registrations, string members = "")
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;
			                   using System.Collections.Generic;

			                   namespace MyCode;

			                   {{types}}

			                   [Container]
			                   {{registrations}}
			                   public static partial class MyContainer
			                   {
			                       {{members}}
			                   }
			                   """);

		[Test]
		public async Task ReportsADisposableTransientBelowTheService()
		{
			GeneratorResult result = Run(
				"""
				public sealed class Timer : IDisposable { public void Dispose() { } }
				public sealed class Debouncer : IDisposable { public Debouncer(Timer timer) { } public void Dispose() { } }
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Transient<Timer>]\n[Transient<Debouncer>]\n[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(9,76): error AWT261: 'MyCode.StorageFactory' lists 'MyCode.Debouncer' in ConsumerOwned, but building 'MyCode.Debouncer' also creates the disposable transient 'MyCode.Timer', which the consumer cannot release by disposing the instance it gets; remove 'MyCode.Debouncer' from ConsumerOwned and take an Owned<T> handle through the Func, or register 'MyCode.StorageFactory' as Scoped")
				.Because("the consumer disposes the debouncer it gets, but nothing would dispose the timer built with it");
		}

		[Test]
		public async Task ReportsADisposableDecoratedInstance()
		{
			GeneratorResult result = Run(
				"""
				public interface IGrinder;
				public sealed class Grinder : IGrinder, IDisposable { public void Dispose() { } }
				public sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner) { } }
				public sealed class GrinderFactory { public GrinderFactory(Func<IGrinder> grinders) { } }
				""",
				"[Transient<Grinder, IGrinder>]\n[Decorate<LoggingGrinder, IGrinder>]\n[Singleton<GrinderFactory>(ConsumerOwned = [typeof(IGrinder)])]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*also creates the disposable transient 'MyCode.Grinder'*").AsWildcard()
				.Because("the consumer only gets the outermost decorator, so a disposable inner instance would leak");
		}

		[Test]
		public async Task ReportsAReleaseHook()
		{
			GeneratorResult result = Run(
				"""
				public sealed class Debouncer;
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Transient<Debouncer>(OnRelease = nameof(Release))]\n[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]",
				"private static void Release(Debouncer debouncer) { }");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*queues the OnRelease hook of 'MyCode.Debouncer'*").AsWildcard()
				.Because("the release hook is queued on the owner, which the consumer cannot run");
		}

		[Test]
		public async Task ReportsEachEntryOfADispatchDictionary()
		{
			GeneratorResult result = Run(
				"""
				public interface IHandler;
				public interface IHandler<T> : IHandler;
				public sealed class Timer : IDisposable { public void Dispose() { } }
				public sealed class OrderHandler : IHandler<int> { public OrderHandler(Timer timer) { } }
				public sealed class RefundHandler : IHandler<string>;
				public sealed class Mediator { public Mediator(IReadOnlyDictionary<Type, Func<IHandler>> handlers) { } }
				""",
				"[Transient<Timer>]\n[Transient<OrderHandler, IHandler<int>>]\n[Transient<RefundHandler, IHandler<string>>]\n[Singleton<Mediator>(ConsumerOwned = [typeof(IHandler)])]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*building 'MyCode.OrderHandler' also creates the disposable transient 'MyCode.Timer'*").AsWildcard()
				.Because("every entry of the dictionary is a route to what the consumer owns");
		}

		[Test]
		public async Task ReportsAFuncWithRuntimeArguments()
		{
			GeneratorResult result = Run(
				"""
				public sealed class Timer : IDisposable { public void Dispose() { } }
				public sealed class Cup { public Cup(Timer timer, [Arg] string size) { } }
				public sealed class Counter { public Counter(Func<string, Cup> cups) { } }
				""",
				"[Transient<Timer>]\n[Transient<Cup>]\n[Singleton<Counter>(ConsumerOwned = [typeof(Cup)])]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*").AsWildcard()
				.Because("a Func<TArg, T> reaches the service like a Func<T>");
		}

		[Test]
		public async Task ReportsAnAsyncFunc()
		{
			GeneratorResult result = Run(
				"""
				public sealed class Timer : IDisposable { public void Dispose() { } }
				public sealed class Kettle { public Kettle(Timer timer) { } }
				public sealed class KettleFactory { public KettleFactory(Func<System.Threading.Tasks.Task<Kettle>> kettles) { } }
				""",
				"[Transient<Timer>]\n[Transient<Kettle>]\n[Singleton<KettleFactory>(ConsumerOwned = [typeof(Kettle)])]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*").AsWildcard()
				.Because("a Func<Task<T>> reaches the service like a Func<T>");
		}

		[Test]
		public async Task ReportsADisposableTransientInAnInjectedProperty()
		{
			GeneratorResult result = Run(
				"""
				public sealed class Timer : IDisposable { public void Dispose() { } }
				public sealed class Debouncer { [Inject] public Timer Timer { get; set; } = null!; }
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Transient<Timer>]\n[Transient<Debouncer>]\n[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*also creates the disposable transient 'MyCode.Timer'*").AsWildcard()
				.Because("an injected property is built with the service and tracked on the owner like a constructor parameter");
		}

		[Test]
		public async Task ReportsAFactoryThatMayReturnADisposable()
		{
			GeneratorResult result = Run(
				"""
				public interface ITimer { }
				public sealed class Timer : ITimer, IDisposable { public void Dispose() { } }
				public sealed class Debouncer { public Debouncer(ITimer timer) { } }
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Transient<ITimer>(Factory = nameof(MakeTimer))]\n[Transient<Debouncer>]\n[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]",
				"private static ITimer MakeTimer() => new Timer();");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*whose factory may return a disposable*").AsWildcard()
				.Because("the owner tracks a factory output that turns out to be disposable at run time");
		}

		[Test]
		[Arguments("Lazy<Timer> timer")]
		[Arguments("Func<Timer> timers")]
		public async Task ReportsADeferredDisposableTransientBelowTheService(string parameter)
		{
			GeneratorResult result = Run(
				$$"""
				public sealed class Timer : IDisposable { public void Dispose() { } }
				public sealed class Debouncer { public Debouncer({{parameter}}) { } }
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Transient<Timer>]\n[Transient<Debouncer>]\n[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT261*defers building the disposable transient 'MyCode.Timer'*").AsWildcard()
				.Because("what the deferred relationship builds later is tracked on the owner and never released with the debouncer");
		}

		[Test]
		public async Task DoesNotReportANonDisposableValueType()
		{
			GeneratorResult result = Run(
				"""
				public struct Ticket { }
				public sealed class Counter { public Counter(Func<Ticket> tickets) { } }
				""",
				"[Transient<Ticket>]\n[Singleton<Counter>(ConsumerOwned = [typeof(Ticket)])]");

			await That(result.Diagnostics.Where(diagnostic => diagnostic.Contains("AWT261"))).IsEmpty()
				.Because("the container never tracks a value type that is not disposable");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task DoesNotReportAFuncBelowTheServiceThatItsServiceOwns(bool consumerFirst)
		{
			string barista = "[Singleton<Barista>(ConsumerOwned = [typeof(Session)])]";
			string session = "[Transient<Session>(ConsumerOwned = [typeof(Filter)])]";
			GeneratorResult result = Run(
				"""
				public sealed class Filter : IDisposable { public void Dispose() { } }
				public sealed class Session : IDisposable { public Session(Func<Filter> filters) { } public void Dispose() { } }
				public sealed class Barista { public Barista(Func<Session> sessions) { } }
				""",
				"[Transient<Filter>]\n" + (consumerFirst ? barista + "\n" + session : session + "\n" + barista));

			await That(result.Diagnostics).IsEmpty()
				.Because("the session owns what its Func builds, so the owner tracks nothing besides the session, whatever the registration order");
		}

		[Test]
		public async Task DoesNotReportAFactoryOutputWhoseDisposalIsSuppressed()
		{
			GeneratorResult result = Run(
				"""
				public interface ITimer { }
				public sealed class Timer : ITimer, IDisposable { public void Dispose() { } }
				public sealed class Debouncer { public Debouncer(ITimer timer) { } }
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Transient<ITimer>(Factory = nameof(MakeTimer), SuppressDisposal = true)]\n[Transient<Debouncer>]\n[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]",
				"private static ITimer MakeTimer() => new Timer();");

			await That(result.Diagnostics).IsEmpty()
				.Because("the container never tracks a factory output whose disposal is suppressed");
		}

		[Test]
		[Arguments("(int, string)")]
		[Arguments("int[,]")]
		[Arguments("Dictionary<int, string>")]
		public async Task AppliesAGenericTypeDefinitionToEachClosing(string argument)
		{
			GeneratorResult result = Run(
				$$"""
				public sealed class Bag<T> : IDisposable { public void Dispose() { } }
				public sealed class Shelf<T> { public Shelf(Func<Bag<T>> bags) { } }
				public sealed class Store { public Store(Shelf<{{argument}}> shelf) { } }
				""",
				"[Transient(typeof(Bag<>))]\n[Singleton(typeof(Shelf<>), ConsumerOwned = [typeof(Bag<>)])]\n[Transient<Store>]");

			await That(result.Diagnostics).IsEmpty()
				.Because("typeof(Bag<>) names every closing, whatever commas its type argument contains");
		}

		[Test]
		[Arguments("Map<,>", "Map<int, (int, string)>")]
		[Arguments("Outer<>.Inner<>", "Outer<int>.Inner<string>")]
		[Arguments("Outer<>.Leaf", "Outer<int>.Leaf")]
		public async Task AppliesNestedAndMultiParameterGenericTypeDefinitions(string definition, string closing)
		{
			GeneratorResult result = Run(
				$$"""
				public sealed class Map<TKey, TValue> : IDisposable { public void Dispose() { } }
				public sealed class Outer<T>
				{
				    public sealed class Inner<TInner> : IDisposable { public void Dispose() { } }
				    public sealed class Leaf : IDisposable { public void Dispose() { } }
				}
				public sealed class Shelf { public Shelf(Func<{{closing}}> items) { } }
				""",
				$"[Transient<{closing}>]\n[Singleton<Shelf>(ConsumerOwned = [typeof({definition})])]");

			await That(result.Diagnostics).IsEmpty()
				.Because("the definition names the closing, so the entry applies and the Func is owned");
		}

		[Test]
		public async Task DoesNotReportSharedDisposablesBelowTheService()
		{
			GeneratorResult result = Run(
				"""
				public sealed class Clock : IDisposable { public void Dispose() { } }
				public sealed class Log : IDisposable { public void Dispose() { } }
				public sealed class Debouncer : IDisposable { public Debouncer(Clock clock, Log log) { } public void Dispose() { } }
				public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }
				""",
				"[Singleton<Clock>]\n[Scoped<Log>]\n[Transient<Debouncer>]\n[Scoped<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]");

			await That(result.Diagnostics).IsEmpty()
				.Because("singleton and scoped dependencies belong to their owner and never leak through the consumer");
		}
	}
}
