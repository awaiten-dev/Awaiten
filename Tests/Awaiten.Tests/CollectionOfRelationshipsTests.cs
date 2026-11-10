using System.Collections.Generic;

namespace Awaiten.Tests;

/// <summary>
///     A collection of relationships, such as <c>IEnumerable&lt;Lazy&lt;T&gt;&gt;</c>, is never handed out empty: injected it
///     is AWT254, and by type it is not registered, unless the relationship type is registered as a service of its own.
/// </summary>
public sealed partial class CollectionOfRelationshipsTests
{
	[Test]
	[Arguments(typeof(IEnumerable<Lazy<IClock>>))]
	[Arguments(typeof(IEnumerable<Func<IClock>>))]
	[Arguments(typeof(IEnumerable<Owned<IClock>>))]
	[Arguments(typeof(Lazy<IClock>[]))]
	[Arguments(typeof(IReadOnlyList<Func<IClock>>))]
	[Arguments(typeof(IAsyncEnumerable<Lazy<IClock>>))]
	public async Task ByType_IsNotRegistered(Type shape)
	{
		using ClockContainer.Root root = new();

		await That(root.GetResolvability(shape, null)).IsEqualTo(default(AwaitenResolvability))
			.Because("the container builds no collection of relationships, so it does not claim the shape, and the MS.DI bridge answers null for it");
		await That(() => root.Resolve(shape)).Throws<InvalidOperationException>()
			.WithMessage("*No registration*").AsWildcard()
			.Because("resolving the shape throws instead of handing back an empty collection while IClock is registered");
	}

	[Test]
	[Arguments(typeof(IEnumerable<Lazy<IClock>>), "System.Lazy`1[Awaiten.Tests.CollectionOfRelationshipsTests+IClock]")]
	[Arguments(typeof(Func<IClock>[]), "System.Func`1[Awaiten.Tests.CollectionOfRelationshipsTests+IClock]")]
	[Arguments(typeof(IReadOnlyList<Owned<IClock>>), "Awaiten.Owned`1[Awaiten.Tests.CollectionOfRelationshipsTests+IClock]")]
	[Arguments(typeof(IAsyncEnumerable<Func<string, IClock>>), "System.Func`2[System.String,Awaiten.Tests.CollectionOfRelationshipsTests+IClock]")]
	[Arguments(typeof(Task<IReadOnlyList<Lazy<Task<IClock>>>>), "System.Lazy`1[System.Threading.Tasks.Task`1[Awaiten.Tests.CollectionOfRelationshipsTests+IClock]]")]
	[Arguments(typeof(IReadOnlyDictionary<string, Func<IClock>>), "System.Func`1[Awaiten.Tests.CollectionOfRelationshipsTests+IClock]")]
	public async Task ByType_MissNamesTheCollectionOfTheService(Type shape, string element)
	{
		using ClockContainer.Root root = new();

		await That(() => root.Resolve(shape)).Throws<InvalidOperationException>()
			.WithMessage($"*its element '{element}' is a relationship*resolve the same collection over 'Awaiten.Tests.CollectionOfRelationshipsTests+IClock' to get every registration, or '{element}' to get a single one.").AsWildcard()
			.Because("the miss names the collection of the wrapped service as the remedy, as AWT254 does for injection");
		await That(() => root.ResolveAsync(shape)).Throws<InvalidOperationException>()
			.WithMessage($"*its element '{element}' is a relationship*").AsWildcard()
			.Because("ResolveAsync falls back to the same miss");
	}

	[Test]
	[Arguments(typeof(IEnumerable<IClock[]>))]
	[Arguments(typeof(List<Lazy<IClock>>))]
	[Arguments(typeof(IReadOnlyDictionary<int, Lazy<IClock>>))]
	[Arguments(typeof(Lazy<IClock>[,]))]
	public async Task ByType_MissOfAnotherShapeKeepsThePlainMessage(Type shape)
	{
		using ClockContainer.Root root = new();

		await That(() => root.Resolve(shape)).Throws<InvalidOperationException>()
			.WithMessage($"No registration for type '{shape}' on this container.")
			.Because("only a shape the container synthesizes over a relationship element gets the collection remedy");
	}

	[Test]
	public async Task ARegisteredRelationshipType_IsCollectedLikeAnyService()
	{
		using RegisteredFuncContainer.Root root = new();

		ClockReader reader = root.Resolve<ClockReader>();

		await That(reader.Clocks).HasSingle().Which.Satisfies(factory => factory() is SystemClock)
			.Because("a Func<IClock> registered as a service is a member of its own collection");
	}

	public interface IClock;

	public sealed class SystemClock : IClock;

	public sealed class ClockReader(IEnumerable<Func<IClock>> clocks)
	{
		public IReadOnlyList<Func<IClock>> Clocks { get; } = new List<Func<IClock>>(clocks);
	}

	[Container]
	[Singleton<SystemClock, IClock>]
	public static partial class ClockContainer;

	[Container]
	[Singleton<Func<IClock>>(Factory = nameof(CreateClockFactory))]
	[Singleton<ClockReader>]
	public static partial class RegisteredFuncContainer
	{
		private static Func<IClock> CreateClockFactory() => () => new SystemClock();
	}
}
