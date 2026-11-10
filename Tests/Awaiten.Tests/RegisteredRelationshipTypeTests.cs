using System.Collections.Generic;

namespace Awaiten.Tests;

/// <summary>
///     A relationship type registered as a service, such as <c>Func&lt;IClock&gt;</c>, is never injected as itself
///     (AWT259), but a typed resolve, a keyed resolve and a collection of it hand out the registration.
/// </summary>
public sealed partial class RegisteredRelationshipTypeTests
{
	[Test]
	public async Task TypedResolve_ServesTheRegistration()
	{
		using RegisteredContainer.Root root = new();

		await That(root.Resolve<Func<IClock>>()()).IsExactly<SpecialClock>()
			.Because("the registration of Func<IClock> wins the dispatch slot over the built-in relationship");
		await That(((Func<IClock>)root.Resolve(typeof(Func<IClock>)))()).IsExactly<SpecialClock>()
			.Because("the untyped resolve dispatches to the same registration");
		await That(root.Resolve<Lazy<IClock>>("special").Value).IsExactly<SpecialClock>()
			.Because("a keyed resolve serves a keyed registration of a relationship type like any other service");
	}

	[Test]
	public async Task Collection_HoldsTheRegistration()
	{
		using RegisteredContainer.Root root = new();

		await That(root.Resolve<ClockReader>().Clocks).HasSingle().Which.Satisfies(factory => factory() is SpecialClock)
			.Because("a collection of the registered type holds that registration");
	}

	public interface IClock;

	public sealed class SystemClock : IClock;

	public sealed class SpecialClock : IClock;

	public sealed class ClockReader(IEnumerable<Func<IClock>> clocks)
	{
		public IReadOnlyList<Func<IClock>> Clocks { get; } = new List<Func<IClock>>(clocks);
	}

	[Container]
	[Singleton<SystemClock, IClock>]
	[Singleton<Func<IClock>>(Factory = nameof(CreateClockFactory))]
	[Singleton<Lazy<IClock>>(Key = "special", Factory = nameof(CreateLazyClock))]
	[Singleton<ClockReader>]
	public static partial class RegisteredContainer
	{
		private static Func<IClock> CreateClockFactory() => () => new SpecialClock();

		private static Lazy<IClock> CreateLazyClock() => new(() => new SpecialClock());
	}
}
