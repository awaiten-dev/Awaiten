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

		await That(root.GetResolvability(shape, null).Status).IsEqualTo(AwaitenResolvabilityStatus.NotRegistered)
			.Because("the container builds no collection of relationships, so it does not claim the shape");
		await That(() => root.Resolve(shape)).Throws<InvalidOperationException>()
			.WithMessage("*No registration*").AsWildcard()
			.Because("resolving the shape throws instead of handing back an empty collection while IClock is registered");
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
