using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Awaiten.Tests;

/// <summary>
///     A consumer whose dependency carries a nullable annotation that the registration does not, nested in the service
///     type (<c>IRepo&lt;string?&gt;</c> against <c>IRepo&lt;&gt;</c>) or in a collection (<c>Task&lt;IStore?[]&gt;</c>,
///     <c>IReadOnlyDictionary&lt;string, IStore?&gt;</c>). The registration is found without the annotation, and the
///     consumer gets the registered instances.
/// </summary>
public sealed partial class NullableAnnotationTests
{
	public interface IInitializable
	{
		Task InitializeAsync(CancellationToken cancellationToken);
	}

	public interface IRepo<T>
	{
		bool Initialized { get; }
	}

	public sealed class Repo<T> : IRepo<T>, IInitializable
	{
		public bool Initialized { get; private set; }

		public async Task InitializeAsync(CancellationToken cancellationToken)
		{
			await Task.Yield();
			Initialized = true;
		}
	}

	public interface IStore
	{
		string Name { get; }
	}

	public sealed class MainStore : IStore
	{
		public string Name => "main";
	}

	public sealed class BackupStore : IStore
	{
		public string Name => "backup";
	}

	public sealed class Clock<T>
	{
	}

	public sealed class Label([Arg] string? text, [Arg] List<string?> tags)
	{
		public string? Text => text;

		public List<string?> Tags => tags;
	}

	public sealed class Consumer(
		IRepo<string?> repo,
		Task<IRepo<string?>> repoTask,
		Func<Task<IRepo<string?>>> repoFactory,
		Task<IStore?[]> stores,
		IReadOnlyDictionary<string, IStore?> keyed,
		Task<IReadOnlyDictionary<string, IStore?>> keyedTask,
		Clock<string> clock,
		Func<string?, List<string?>, Label> labels)
	{
		public Func<string?, List<string?>, Label> Labels => labels;

		public IRepo<string?> Repo => repo;

		public Task<IRepo<string?>> RepoTask => repoTask;

		public Func<Task<IRepo<string?>>> RepoFactory => repoFactory;

		public Task<IStore?[]> Stores => stores;

		public IReadOnlyDictionary<string, IStore?> Keyed => keyed;

		public Task<IReadOnlyDictionary<string, IStore?>> KeyedTask => keyedTask;

		public Clock<string> Clock => clock;
	}

	[Test]
	public async Task NestedAnnotations_ResolveTheRegisteredInstances()
	{
		await using NullableShop.Root shop = new();
		await using NullableShop.Scope scope = shop.CreateScope();

		Consumer consumer = await scope.ResolveAsync<Consumer>();

		await That(consumer.Repo.Initialized).IsTrue()
			.Because("IRepo<string?> resolves the open generic registration IRepo<>, initialized before it is injected");
		await That(await consumer.RepoTask).IsSameAs(consumer.Repo);
		await That(await consumer.RepoFactory()).IsSameAs(consumer.Repo);
		await That((await consumer.Stores).Select(store => store?.Name)).IsEqualTo(["main",])
			.Because("the awaited collection holds the unkeyed registration");
		await That(consumer.Keyed.Keys).IsEqualTo(["main", "backup",]).InAnyOrder();
		await That((await consumer.KeyedTask)["backup"]).IsSameAs(consumer.Keyed["backup"]);
		await That(consumer.Clock).IsSameAs(await shop.ResolveAsync<Clock<string>>())
			.Because("a factory returning Clock<string?> produces the singleton registered as Clock<string>");
	}

	[Test]
	public async Task NullableRuntimeArguments_ReachTheArgParameters()
	{
		await using NullableShop.Root shop = new();
		await using NullableShop.Scope scope = shop.CreateScope();
		Consumer consumer = await scope.ResolveAsync<Consumer>();

		List<string?> tags = [null,];
		Label label = consumer.Labels(null, tags);

		await That(label.Text).IsNull()
			.Because("Func<string?, …> hands its null argument to the [Arg] string? parameter");
		await That(label.Tags).IsSameAs(tags);
	}

	[Test]
	public async Task NullableFactoryAndInstance_ProduceTheirValues()
	{
		await using NullableProductShop.Root shop = new();
		await using NullableProductShop.Scope scope = shop.CreateScope();

		await That(scope.Resolve<IStore>()).IsSameAs(shop.Resolve<IStore>())
			.Because("the singleton a factory declared as IStore? produces is shared like any other");
		await That(await scope.ResolveAsync<Clock<string>>()).IsSameAs(NullableProductShop.Clock);
	}

	[Container]
#pragma warning disable AWT245
	[Singleton<IStore>(Factory = nameof(CreateStore))]
	[Singleton<Clock<string>>(Instance = nameof(Clock))]
#pragma warning restore AWT245
	public static partial class NullableProductShop
	{
		internal static Clock<string>? Clock { get; } = new();

		private static IStore? CreateStore() => new MainStore();
	}

	[Test]
	public async Task NullableFactoryThatReturnsNull_RunsAgainOnEachSynchronousResolve()
	{
		using NullResultShop.Root shop = new();

		object? first = shop.Resolve(typeof(IStore));
		object? second = shop.Resolve(typeof(IStore));

		await That(first).IsNull();
		await That(second).IsNull();
		await That(NullResultShop.Calls).IsEqualTo(2)
			.Because("a null singleton counts as not built yet, as the AWT245 documentation says");
	}

	[Container]
#pragma warning disable AWT245
	[Singleton<IStore>(Factory = nameof(CreateStore))]
#pragma warning restore AWT245
	public static partial class NullResultShop
	{
		public static int Calls { get; private set; }

		private static IStore? CreateStore()
		{
			Calls++;
			return null;
		}
	}

	[Container]
	[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	[Scoped(typeof(Repo<>), typeof(IRepo<>))]
	[Scoped<MainStore, IStore>]
	[Scoped<MainStore, IStore>(Key = "main")]
	[Scoped<BackupStore, IStore>(Key = "backup")]
	[Singleton<Clock<string>>(Factory = nameof(CreateClock))]
	[Transient<Label>]
	[Transient<Consumer>]
	public static partial class NullableShop
	{
		private static Clock<string?> CreateClock() => new();
	}
}
