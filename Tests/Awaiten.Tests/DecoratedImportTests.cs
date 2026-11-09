using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of a <c>[Decorate]</c> over an <c>[ImportService&lt;T&gt;]</c>: the decorator chain is transient and
///     owned by the container, while the instance it wraps comes from the host on each resolve and stays the host's.
/// </summary>
public partial class DecoratedImportTests
{
	public interface IGrinder;

	public sealed class HostGrinder : IGrinder, IDisposable
	{
		public bool IsDisposed { get; private set; }

		public void Dispose() => IsDisposed = true;
	}

	public sealed class LoggingGrinder(IGrinder inner) : IGrinder, IDisposable
	{
		public IGrinder Inner { get; } = inner;

		public bool IsDisposed { get; private set; }

		public void Dispose() => IsDisposed = true;
	}

	public sealed class TracingGrinder(IGrinder inner) : IGrinder
	{
		public IGrinder Inner { get; } = inner;
	}

	public sealed class AuditGrinder(IGrinder inner) : IGrinder
	{
		public IGrinder Inner { get; } = inner;
	}

	public sealed class WarmGrinder(IGrinder inner) : IGrinder, IInitializable
	{
		public IGrinder Inner { get; } = inner;

		public bool IsWarm { get; private set; }

		public Task InitializeAsync(CancellationToken cancellationToken)
		{
			IsWarm = true;
			return Task.CompletedTask;
		}
	}

	public sealed class Barista(IGrinder grinder)
	{
		public IGrinder Grinder { get; } = grinder;
	}

	public sealed class KeyedBarista([FromKey("burr")] IGrinder burr, IGrinder plain)
	{
		public IGrinder Burr { get; } = burr;

		public IGrinder Plain { get; } = plain;
	}

	public sealed class LazyBarista(Func<IGrinder> grinder)
	{
		public Func<IGrinder> Grinder { get; } = grinder;
	}

	public sealed class GrindSettings
	{
		public bool Logging { get; init; } = true;
	}

	public sealed class Grind;

	public interface IFeed<T>;

	public sealed class AuditedFeed<T>(IFeed<T> inner) : IFeed<T>
	{
		public IFeed<T> Inner { get; } = inner;
	}

	public sealed class HostFeed<T> : IFeed<T>;

	public sealed class FeedReader(IFeed<Grind> feed)
	{
		public IFeed<Grind> Feed { get; } = feed;
	}

	[Container]
	[ImportService<IGrinder>]
	[Transient<Barista>]
	[Transient<LazyBarista>]
	[Decorate<LoggingGrinder, IGrinder>]
	public static partial class Shop;

	[Container]
	[ImportService<IGrinder>]
	[Singleton<Barista>]
	[Decorate<AuditGrinder, IGrinder>]
	[Decorate<TracingGrinder, IGrinder>(Order = 1)]
	public static partial class SingletonShop;

	[Container]
	[ImportService<IGrinder>]
	[Transient<KeyedBarista>]
	[Decorate<LoggingGrinder, IGrinder>(Keys = ["burr"])]
	public static partial class KeyedShop;

	[Container]
	[ImportService<IGrinder>]
	[Singleton<GrindSettings>(Instance = nameof(Settings))]
	[Decorate<TracingGrinder, IGrinder>(When = nameof(IsLogged))]
	public static partial class ConditionalShop
	{
		internal static GrindSettings Settings = new();

		public static bool IsLogged(GrindSettings settings) => settings.Logging;
	}

	[Container]
	[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	[ImportService<IGrinder>]
	[Decorate<WarmGrinder, IGrinder>]
	public static partial class WarmShop;

	[Container]
	[Singleton<HostGrinder, IGrinder>]
	[Decorate<TracingGrinder, IGrinder>]
	public static partial class ProductionShop;

	[Container(Extends = typeof(ProductionShop))]
	[ImportService<IGrinder>]
	public static partial class TestShop;

	[Container]
	[ImportService<IFeed<Grind>>]
	[Transient<FeedReader>]
	[Decorate(typeof(AuditedFeed<>), typeof(IFeed<>))]
	public static partial class OpenDecoratorShop;

	[Container]
	[ImportService(typeof(IFeed<>))]
	[Transient<FeedReader>]
	[Decorate<AuditedFeed<Grind>, IFeed<Grind>>]
	public static partial class OpenImportShop;

	private sealed class CountingResolver : IExternalResolver
	{
		public List<HostGrinder> Handed { get; } = new();

		public List<object?> Keys { get; } = new();

		public bool TryResolve(Type serviceType, object? key, [NotNullWhen(true)] out object? instance)
		{
			if (serviceType == typeof(IGrinder))
			{
				HostGrinder grinder = new();
				Handed.Add(grinder);
				Keys.Add(key);
				instance = grinder;
				return true;
			}

			instance = serviceType == typeof(IFeed<Grind>) ? new HostFeed<Grind>() : null;
			return instance is not null;
		}
	}

	public class Wrapping
	{
		[Test]
		public async Task Decorator_WrapsTheHostInstance()
		{
			CountingResolver host = new();
			using Shop.Root shop = new(host);
			using IAwaitenScope scope = shop.CreateScope();

			Barista barista = scope.Resolve<Barista>();

			await That(barista.Grinder).Is<LoggingGrinder>()
				.Because("a consumer of the import receives the decorator");
			await That(((LoggingGrinder)barista.Grinder).Inner).IsSameAs(host.Handed[0])
				.Because("the decorator wraps the instance the host hands in");
		}

		[Test]
		public async Task Decorator_IsBuiltPerResolve_AndAsksTheHostEachTime()
		{
			CountingResolver host = new();
			using Shop.Root shop = new(host);
			using IAwaitenScope scope = shop.CreateScope();

			IGrinder first = scope.Resolve<IGrinder>();
			IGrinder second = scope.Resolve<IGrinder>();

			await That(first).IsNotSameAs(second)
				.Because("an import has no lifetime the container could share the decorator by, so the chain is transient");
			await That(host.Handed).HasCount(2)
				.Because("the host is asked on each resolve, as for an undecorated import");
		}

		[Test]
		public async Task DisposingTheScope_DisposesTheDecorator_ButNotTheHostInstance()
		{
			CountingResolver host = new();
			using Shop.Root shop = new(host);
			IAwaitenScope scope = shop.CreateScope();
			LoggingGrinder grinder = (LoggingGrinder)scope.Resolve<IGrinder>();

			scope.Dispose();

			await That(grinder.IsDisposed).IsTrue()
				.Because("the container built the decorator, so its scope disposes it");
			await That(host.Handed[0].IsDisposed).IsFalse()
				.Because("the host owns the instance it hands in");
		}

		[Test]
		public async Task DecoratorsOfTheImport_ApplyInOrder_AndASingletonCapturesOneChain()
		{
			CountingResolver host = new();
			using SingletonShop.Root shop = new(host);

			Barista barista = shop.Resolve<Barista>();

			await That(barista.Grinder).Is<TracingGrinder>()
				.Because("the decorator with the higher Order is outermost");
			await That(((TracingGrinder)barista.Grinder).Inner).Is<AuditGrinder>();
			await That(shop.Resolve<Barista>().Grinder).IsSameAs(barista.Grinder)
				.Because("a singleton consumer captures one chain, as it would capture the bare import");
		}

		[Test]
		public async Task Func_ResolvesANewChainPerCall()
		{
			CountingResolver host = new();
			using Shop.Root shop = new(host);
			using IAwaitenScope scope = shop.CreateScope();

			Func<IGrinder> grinder = scope.Resolve<LazyBarista>().Grinder;

			await That(host.Handed).IsEmpty()
				.Because("the Func defers the resolve, the host's included");
			await That(grinder()).Is<LoggingGrinder>()
				.Because("the decorated import is a registration of the graph, so a relationship over it resolves the chain");
			await That(grinder()).IsNotSameAs(grinder());
		}

		[Test]
		public async Task Owned_DisposesTheDecorator_ButNotTheHostInstance()
		{
			CountingResolver host = new();
			using Shop.Root shop = new(host);
			Owned<IGrinder> owned = shop.Resolve<Owned<IGrinder>>();
			LoggingGrinder grinder = (LoggingGrinder)owned.Value;

			owned.Dispose();

			await That(grinder.IsDisposed).IsTrue()
				.Because("the throwaway scope of the Owned<T> built the decorator");
			await That(host.Handed[0].IsDisposed).IsFalse();
		}
	}

	public class Keys
	{
		[Test]
		public async Task KeyedDecorator_WrapsOnlyTheHostInstanceUnderItsKey()
		{
			CountingResolver host = new();
			using KeyedShop.Root shop = new(host);
			using IAwaitenScope scope = shop.CreateScope();

			KeyedBarista barista = scope.Resolve<KeyedBarista>();

			await That(barista.Burr).Is<LoggingGrinder>()
				.Because("the decorator selects the key 'burr'");
			await That(barista.Plain).Is<HostGrinder>()
				.Because("the unkeyed import is not selected, so it comes from the host undecorated");
			await That(host.Keys).Contains("burr")
				.Because("the decorator asks the host for its inner instance under the key it decorates");
		}

		[Test]
		public async Task KeyedResolve_ReachesTheDecorator()
		{
			CountingResolver host = new();
			using KeyedShop.Root shop = new(host);
			using IAwaitenScope scope = shop.CreateScope();

			await That(scope.Resolve(typeof(IGrinder), "burr")).Is<LoggingGrinder>();
			await That(scope.Resolve(typeof(IGrinder), "other")).Is<HostGrinder>()
				.Because("a key no decorator selects still goes to the host");
		}
	}

	[NotInParallel(nameof(ConditionalShop))]
	public class Conditions
	{
		[Test]
		public async Task When_False_ReturnsTheHostInstance()
		{
			CountingResolver host = new();
			ConditionalShop.Settings = new GrindSettings { Logging = false, };
			try
			{
				using ConditionalShop.Root shop = new(host);

				await That(shop.Resolve<IGrinder>()).IsSameAs(host.Handed[0])
					.Because("a decorator whose predicate is false hands out its inner instance, here the host's");
			}
			finally
			{
				ConditionalShop.Settings = new GrindSettings();
			}
		}

		[Test]
		public async Task When_True_WrapsTheHostInstance()
		{
			CountingResolver host = new();
			using ConditionalShop.Root shop = new(host);

			await That(shop.Resolve<IGrinder>()).Is<TracingGrinder>();
		}
	}

	public class ByType
	{
		[Test]
		public async Task TypedAndUntypedResolve_ReachTheDecorator()
		{
			CountingResolver host = new();
			using SingletonShop.Root shop = new(host);

			await That(shop.Resolve<IGrinder>()).Is<TracingGrinder>();
			await That(shop.Resolve(typeof(IGrinder))).Is<TracingGrinder>();
			await That(shop.TryResolve(typeof(IGrinder), out object? grinder)).IsTrue();
			await That(grinder).Is<TracingGrinder>();
		}

		[Test]
		public async Task GetResolvability_AnswersForTheDecorator()
		{
			using SingletonShop.Root shop = new(new CountingResolver());

			await That(shop.GetResolvability(typeof(IGrinder)).Status).IsEqualTo(AwaitenResolvabilityStatus.Resolvable)
				.Because("the container resolves the decorator, which asks the host only for its inner instance");
		}

		[Test]
		public async Task ExternalDependencies_StillListTheImport()
		{
			using SingletonShop.Root shop = new(new CountingResolver());

			await That(((IAwaitenRoot)shop).ExternalDependencies.Select(dependency => dependency.ServiceType)).IsEqualTo([typeof(IGrinder),])
				.Because("the host still provides the instance the decorator wraps");
			await That(((IAwaitenRoot)shop).Registrations.Select(registration => registration.ServiceType)).DoesNotContain(typeof(IGrinder))
				.Because("projecting the decorator into a host would replace the host's own registration it wraps");
		}
	}

	public class Async
	{
		[Test]
		public async Task AsyncInitializedDecorator_IsResolvedAsynchronously()
		{
			CountingResolver host = new();
			using WarmShop.Root shop = new(host);

			IGrinder grinder = await shop.ResolveAsync<IGrinder>();

			await That(((WarmGrinder)grinder).IsWarm).IsTrue();
			await That(((WarmGrinder)grinder).Inner).IsSameAs(host.Handed[0]);
			await That(shop.GetResolvability(typeof(IGrinder)).Status).IsEqualTo(AwaitenResolvabilityStatus.AsyncOnly)
				.Because("the decorator needs asynchronous initialization, so it is not handed out synchronously");
		}
	}

	public class Extends
	{
		[Test]
		public async Task InheritedDecorator_WrapsTheImportThatReplacesTheRegistration()
		{
			CountingResolver host = new();
			using TestShop.Root shop = new(host);

			IGrinder grinder = shop.Resolve<IGrinder>();

			await That(grinder).Is<TracingGrinder>()
				.Because("the inherited decorators wrap the replacement, also when the host provides it");
			await That(((TracingGrinder)grinder).Inner).IsSameAs(host.Handed[0]);
		}
	}

	public class Generics
	{
		[Test]
		public async Task OpenDecorator_WrapsAClosedImport()
		{
			using OpenDecoratorShop.Root shop = new(new CountingResolver());

			await That(shop.Resolve<FeedReader>().Feed).Is<AuditedFeed<Grind>>()
				.Because("an open decorator closes over each declared closed import of its service");
			await That(((AuditedFeed<Grind>)shop.Resolve<FeedReader>().Feed).Inner).Is<HostFeed<Grind>>();
		}

		[Test]
		public async Task ClosedDecorator_WrapsAClosingOfAnOpenImport()
		{
			using OpenImportShop.Root shop = new(new CountingResolver());

			await That(shop.Resolve<FeedReader>().Feed).Is<AuditedFeed<Grind>>()
				.Because("a closed decorator names the closing of the open import it wraps");
			await That(((AuditedFeed<Grind>)shop.Resolve<FeedReader>().Feed).Inner).Is<HostFeed<Grind>>();
		}
	}
}
