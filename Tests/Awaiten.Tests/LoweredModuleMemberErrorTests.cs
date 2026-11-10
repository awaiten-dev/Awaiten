namespace Awaiten.Tests;

/// <summary>
///     A module of the container's assembly whose private <c>Factory</c> member no container can call reports it in its
///     own build (AWT153). While a <c>#pragma</c> disables that error, a container of the assembly leaves the registration
///     out, as if it were never declared, and wires the rest of the graph.
/// </summary>
public sealed partial class LoweredModuleMemberErrorTests
{
	[Test]
	public async Task UnservedRegistrations_AreLeftOutAndTheRestOfTheGraphResolves()
	{
		using LoweredMemberContainer.Root container = new();

		await That(container.Resolve<LoweredMemberBarista>().Clock).IsNotNull()
			.Because("the graph that does not need the left out registrations is wired");
		await That(() => container.Resolve(typeof(ILoweredMemberRepository<LoweredMemberBarista>))).Throws<InvalidOperationException>()
			.WithMessage("*No registration*").AsWildcard()
			.Because("the open registration whose generic member is private is left out");
		await That(() => container.Resolve(typeof(ILoweredMemberGrinder))).Throws<InvalidOperationException>()
			.WithMessage("*No registration*").AsWildcard()
			.Because("the closed registration whose member takes a ref parameter is left out");
	}

	public interface ILoweredMemberRepository<T>;

	public sealed class LoweredMemberRepository<T> : ILoweredMemberRepository<T>;

	public interface ILoweredMemberGrinder;

	public sealed class LoweredMemberGrinder : ILoweredMemberGrinder;

	public sealed class LoweredMemberClock;

	public sealed class LoweredMemberBarista(LoweredMemberClock clock)
	{
		public LoweredMemberClock Clock { get; } = clock;
	}

	[Module]
	[Singleton<LoweredMemberClock>]
#pragma warning disable AWT153
	[Singleton(typeof(ILoweredMemberRepository<>), Factory = nameof(MakeRepository))]
	[Singleton<LoweredMemberGrinder, ILoweredMemberGrinder>(Factory = nameof(MakeGrinder))]
#pragma warning restore AWT153
	public static partial class LoweredMemberModule
	{
		private static LoweredMemberRepository<T> MakeRepository<T>() => new();

		private static LoweredMemberGrinder MakeGrinder(ref int count) => new();
	}

	[Container]
	[Import(typeof(LoweredMemberModule))]
	[Transient<LoweredMemberBarista>]
	public static partial class LoweredMemberContainer;
}
