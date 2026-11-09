using System.Threading;

namespace Awaiten.Tests;

/// <summary>
///     A singleton or scoped service that resolves itself through a <c>Func</c> or <c>Lazy</c> while it is built: the
///     container is still building the one instance it caches, so it throws instead of building it again until the
///     stack overflows. Calling the same <c>Func</c> after the service is built returns the cached instance.
/// </summary>
[NotInParallel(nameof(ReentrantResolveTests))]
public partial class ReentrantResolveTests
{
	private static CancellationToken Ct => TestContext.Current!.Execution.CancellationToken;

	[Test]
	public async Task Singleton_ResolvingItselfThroughAFuncInItsConstructor_Throws_AndALaterResolveRetries()
	{
		Reentry.Reset();
		using SingletonShop.Root root = new();

		await That(() => root.Resolve<FuncSelf>()).Throws<InvalidOperationException>()
			.WithMessage("*'Awaiten.Tests.ReentrantResolveTests.FuncSelf' was resolved again while it was being built*").AsWildcard()
			.Because("the singleton is not cached until its constructor returns, so resolving it again would build it again");
		await That(Reentry.Entries).IsEqualTo(1)
			.Because("the re-entrant resolve throws before it builds a second instance");

		Reentry.Enabled = false;
		FuncSelf self = root.Resolve<FuncSelf>();

		await That(root.Resolve<FuncSelf>()).IsSameAs(self)
			.Because("a failed build leaves nothing marked as being built, so the next resolve builds and caches the singleton");
	}

	[Test]
	public async Task Scoped_ResolvingItselfThroughAFuncInItsConstructor_Throws()
	{
		Reentry.Reset();
		using ScopedShop.Root root = new();
		using ScopedShop.Scope scope = root.CreateScope();

		await That(() => scope.Resolve<FuncSelf>()).Throws<InvalidOperationException>()
			.WithMessage("*FuncSelf' was resolved again while it was being built*").AsWildcard()
			.Because("the scoped instance is not cached on its scope until its constructor returns");
	}

	[Test]
	public async Task Singleton_ReadingALazyOfItselfInItsConstructor_Throws()
	{
		Reentry.Reset();
		using LazyShop.Root root = new();

		await That(() => root.Resolve<LazySelf>()).Throws<InvalidOperationException>()
			.WithMessage("*LazySelf' was resolved again while it was being built*").AsWildcard()
			.Because("reading the Lazy resolves the singleton that is still being built");
	}

	[Test]
	public async Task Singleton_ResolvingAnOwnedHandleOfItselfInItsConstructor_Throws()
	{
		Reentry.Reset();
		using OwnedShop.Root root = new();

		await That(() => root.Resolve<OwnedSelf>()).Throws<InvalidOperationException>()
			.WithMessage("*OwnedSelf' was resolved again while it was being built*").AsWildcard()
			.Because("an Owned handle of a singleton shares the instance the root is still building");
	}

	[Test]
	public async Task Decorator_ResolvingTheServiceItDecoratesThroughAFuncInItsConstructor_Throws()
	{
		Reentry.Reset();
		using DecoratorShop.Root root = new();

		await That(() => root.Resolve<IBrewer>()).Throws<InvalidOperationException>()
			.WithMessage("*'Awaiten.Tests.ReentrantResolveTests.ReenteringBrewer' was resolved again while it was being built*").AsWildcard()
			.Because("the Func resolves the decorated chain whose outermost link is the decorator being built");
	}

	[Test]
	public async Task OnActivated_ResolvingTheActivatedSingleton_Throws()
	{
		Reentry.Reset();
		using ActivationShop.Root root = new();

		await That(() => root.Resolve<ActivatedSelf>()).Throws<InvalidOperationException>()
			.WithMessage("*ActivatedSelf' was resolved again while it was being built*").AsWildcard()
			.Because("the singleton is cached only after its OnActivated hook has run");
	}

	[Test]
	public async Task DeferredService_ResolvingItselfThroughAFuncInItsConstructor_Throws()
	{
		Reentry.Reset();
		using DeferredShop.Root root = new();

		await That(() => root.Resolve<DeferredFuncSelf>()).Throws<InvalidOperationException>()
			.WithMessage("*DeferredFuncSelf' was resolved again while it was being built*").AsWildcard()
			.Because("a service with a deferred member is cached only once its constructor returns, before the member is wired");
	}

	[Test]
	public async Task AsyncSingleton_ResolvingItselfThroughAFuncInItsConstructor_Throws()
	{
		Reentry.Reset();
		using AsyncShop.Root root = new();

		await That(async () => await root.ResolveAsync<AsyncSelf>(Ct)).Throws<InvalidOperationException>()
			.WithMessage("*AsyncSelf' was resolved again while it was being built*").AsWildcard()
			.Because("the singleton's task is not cached until the construction returns it at its first await");

		Reentry.Enabled = false;
		AsyncSelf self = await root.ResolveAsync<AsyncSelf>(Ct);

		await That(await root.ResolveAsync<AsyncSelf>(Ct)).IsSameAs(self)
			.Because("the faulted task is evicted and nothing stays marked as being built, so the next resolve builds the singleton");
	}

	[Test]
	public async Task Singleton_CallingAFuncOfItselfAfterItIsBuilt_ReturnsTheCachedInstance()
	{
		Reentry.Reset();
		using SingletonShop.Root root = new();
		LaterSelf self = root.Resolve<LaterSelf>();

		await That(self.Again()).IsSameAs(self)
			.Because("once the constructor has returned, the Func reads the cached singleton");
	}

	[Test]
	public async Task Scoped_CallingAFuncOfItselfAfterItIsBuilt_ReturnsTheCachedInstance()
	{
		Reentry.Reset();
		using ScopedShop.Root root = new();
		using ScopedShop.Scope scope = root.CreateScope();
		LaterSelf self = scope.Resolve<LaterSelf>();

		await That(self.Again()).IsSameAs(self)
			.Because("once the constructor has returned, the Func reads the instance cached on the scope");
	}

	[Test]
	public async Task DeferredMember_OfItself_IsWiredWithTheInstanceBeingBuilt()
	{
		Reentry.Reset();
		using DeferredShop.Root root = new();

		DeferredSelf self = root.Resolve<DeferredSelf>();

		await That(self.Me).IsSameAs(self)
			.Because("a deferred member is wired after the instance is cached, so resolving itself there returns that instance");
	}

	/// <summary>
	///     Counts the re-entrant resolves and stops after the second, so that without the guard a test fails on a few
	///     surplus instances instead of overflowing the stack and taking the test host down.
	/// </summary>
	internal static class Reentry
	{
		public static bool Enabled { get; set; }

		public static int Entries { get; private set; }

		public static void Reset()
		{
			Enabled = true;
			Entries = 0;
		}

		public static void Enter(Action resolveItself)
		{
			if (Enabled && ++Entries < 3)
			{
				resolveItself();
			}
		}
	}

	public sealed class FuncSelf
	{
		public FuncSelf(Func<FuncSelf> self) => Reentry.Enter(() => self());
	}

	public sealed class LaterSelf(Func<LaterSelf> self)
	{
		public LaterSelf Again() => self();
	}

	public sealed class LazySelf
	{
		public LazySelf(Lazy<LazySelf> self) => Reentry.Enter(() => _ = self.Value);
	}

	public sealed class OwnedSelf
	{
		public OwnedSelf(Func<Owned<OwnedSelf>> self) => Reentry.Enter(() => self().Dispose());
	}

	public interface IBrewer;

	public sealed class Brewer : IBrewer;

	public sealed class ReenteringBrewer : IBrewer
	{
		public ReenteringBrewer(IBrewer inner, Func<IBrewer> outer) => Reentry.Enter(() => outer());
	}

	public sealed class ActivatedSelf(Func<ActivatedSelf> self)
	{
		public ActivatedSelf Again() => self();
	}

	public sealed class DeferredFuncSelf
	{
		public DeferredFuncSelf(Func<DeferredFuncSelf> self) => Reentry.Enter(() => self());

		[Inject(Deferred = true)]
		public LaterSelf? Later { get; set; }
	}

	public sealed class DeferredSelf
	{
		[Inject(Deferred = true)]
		public DeferredSelf? Me { get; set; }
	}

	public sealed class AsyncSelf : IInitializable
	{
		public AsyncSelf(Func<Task<AsyncSelf>> self) => Reentry.Enter(() => _ = self());

		public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	[Container]
	[Singleton<FuncSelf>]
	[Singleton<LaterSelf>]
	public static partial class SingletonShop;

	[Container]
	[Scoped<FuncSelf>]
	[Scoped<LaterSelf>]
	public static partial class ScopedShop;

	[Container]
	[Singleton<LazySelf>]
	public static partial class LazyShop;

	[Container]
	[Singleton<OwnedSelf>]
	public static partial class OwnedShop;

	[Container]
	[Singleton<Brewer, IBrewer>]
	[Decorate<ReenteringBrewer, IBrewer>]
	public static partial class DecoratorShop;

	[Container]
	[Singleton<ActivatedSelf>(OnActivated = nameof(Activated))]
	public static partial class ActivationShop
	{
		private static void Activated(ActivatedSelf instance) => Reentry.Enter(() => instance.Again());
	}

	[Container]
	[Singleton<DeferredFuncSelf>]
	[Singleton<DeferredSelf>]
	[Singleton<LaterSelf>]
	public static partial class DeferredShop;

	[Container]
	[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	[Singleton<AsyncSelf>]
	public static partial class AsyncShop;
}
