using System;
using System.Threading.Tasks;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>What happened to an internal service its module produces through a private member.</summary>
public interface IFriendPrivateMemberState
{
	/// <summary>Whether the module's activation hook ran on the service.</summary>
	bool Activated { get; }

	/// <summary>Whether the module's async initializer convention ran on the service.</summary>
	bool Started { get; }

	/// <summary>Whether the module's release hook ran on the service.</summary>
	bool Released { get; }

	/// <summary>Whether the container disposed the service.</summary>
	bool Disposed { get; }

	/// <summary>The kettle a cup was produced with, or <see langword="null" />.</summary>
	IFriendPrivateMemberState? Kettle { get; }
}

/// <summary>A barista that takes the internal services its module produces through private members.</summary>
public interface IFriendPrivateMemberBarista
{
	/// <summary>The singleton kettle, from a private async factory with hooks.</summary>
	IFriendPrivateMemberState Kettle { get; }

	/// <summary>The scoped cup, from a private factory that takes the kettle.</summary>
	IFriendPrivateMemberState Cup { get; }

	/// <summary>A transient scale, from a private factory.</summary>
	IFriendPrivateMemberState Scale { get; }

	/// <summary>A second transient scale.</summary>
	IFriendPrivateMemberState OtherScale { get; }

	/// <summary>The shelf, a private instance.</summary>
	IFriendPrivateMemberState Shelf { get; }

	/// <summary>The singleton heater, from a private async factory returning a type private to the module.</summary>
	IFriendPrivateMemberState Heater { get; }

	/// <summary>The rack, a private instance of a type private to the module.</summary>
	IFriendPrivateMemberState Rack { get; }

	/// <summary>The singleton pot, from a private factory returning a type a convention initializes but its service does not.</summary>
	IFriendPrivateMemberState Pot { get; }

	/// <summary>A transient spoon, from an internal factory beside a public overload of another type.</summary>
	IFriendPrivateMemberSpoon Spoon { get; }
}

/// <summary>A public service the module produces through an internal factory.</summary>
public interface IFriendPrivateMemberSpoon : IFriendPrivateMemberState;

internal interface IFriendPrivateMemberStartable
{
	Task StartAsync();
}

internal interface IFriendPrivateMemberHeater : IFriendPrivateMemberState;

internal interface IFriendPrivateMemberRack : IFriendPrivateMemberState;

internal interface IFriendPrivateMemberPot : IFriendPrivateMemberState;

internal interface IFriendPrivateMemberUnused;

internal sealed class FriendPrivateMemberPot : FriendPrivateMemberPart, IFriendPrivateMemberPot
{
	public Task WarmAsync()
	{
		Started = true;
		return Task.CompletedTask;
	}
}

internal sealed class FriendPrivateMemberSpoon : FriendPrivateMemberPart, IFriendPrivateMemberSpoon;

internal interface IFriendPrivateMemberKettle : IFriendPrivateMemberState, IFriendPrivateMemberStartable;

internal interface IFriendPrivateMemberCup : IFriendPrivateMemberState;

internal interface IFriendPrivateMemberScale : IFriendPrivateMemberState;

internal interface IFriendPrivateMemberShelf : IFriendPrivateMemberState;

internal class FriendPrivateMemberPart : IFriendPrivateMemberCup, IFriendPrivateMemberScale, IFriendPrivateMemberShelf, IDisposable
{
	public bool Activated { get; set; }

	public bool Started { get; protected set; }

	public bool Released { get; set; }

	public bool Disposed { get; private set; }

	public IFriendPrivateMemberState? Kettle { get; set; }

	public void Dispose() => Disposed = true;
}

internal sealed class FriendPrivateMemberKettlePart : FriendPrivateMemberPart, IFriendPrivateMemberKettle
{
	public Task StartAsync()
	{
		Started = true;
		return Task.CompletedTask;
	}
}

internal sealed class FriendPrivateMemberBarista(
	IFriendPrivateMemberKettle kettle,
	IFriendPrivateMemberCup cup,
	IFriendPrivateMemberScale scale,
	IFriendPrivateMemberScale otherScale,
	IFriendPrivateMemberShelf shelf,
	IFriendPrivateMemberHeater heater,
	IFriendPrivateMemberRack rack,
	IFriendPrivateMemberPot pot,
	IFriendPrivateMemberSpoon spoon) : IFriendPrivateMemberBarista
{
	public IFriendPrivateMemberState Kettle => kettle;

	public IFriendPrivateMemberState Cup => cup;

	public IFriendPrivateMemberState Scale => scale;

	public IFriendPrivateMemberState OtherScale => otherScale;

	public IFriendPrivateMemberState Shelf => shelf;

	public IFriendPrivateMemberState Heater => heater;

	public IFriendPrivateMemberState Rack => rack;

	public IFriendPrivateMemberState Pot => pot;

	public IFriendPrivateMemberSpoon Spoon => spoon;
}

/// <summary>
///     A <c>[Module]</c> that produces internal services through private <c>Factory</c> and <c>Instance</c> members, with
///     hooks, an async initializer convention and disposal, members returning types private to the module, an unconsumed
///     internal factory, and an internal factory and hook beside public overloads of other types. This assembly grants
///     Awaiten.Tests <c>InternalsVisibleTo</c>, so a container there, like one in this assembly, builds the internal
///     services itself through internal wrappers of the private members.
/// </summary>
[Module]
[AsyncInitializer<IFriendPrivateMemberStartable>(nameof(IFriendPrivateMemberStartable.StartAsync))]
[AsyncInitializer<FriendPrivateMemberPot>(nameof(FriendPrivateMemberPot.WarmAsync))]
[Singleton<IFriendPrivateMemberKettle>(Factory = nameof(MakeKettleAsync), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
[Scoped<IFriendPrivateMemberCup>(Factory = nameof(MakeCup))]
[Transient<IFriendPrivateMemberScale>(Factory = nameof(MakeScale))]
[Singleton<IFriendPrivateMemberShelf>(Instance = nameof(Shelf))]
[Singleton<IFriendPrivateMemberHeater>(Factory = nameof(MakeHeaterAsync))]
[Singleton<IFriendPrivateMemberRack>(Instance = nameof(Rack))]
[Singleton<IFriendPrivateMemberPot>(Factory = nameof(MakePot))]
[Singleton<IFriendPrivateMemberUnused>(Factory = nameof(MakeUnused))]
[Transient<IFriendPrivateMemberSpoon>(Factory = nameof(MakeSpoon), OnActivated = nameof(Stir))]
[Scoped<FriendPrivateMemberBarista, IFriendPrivateMemberBarista>]
public static partial class SupportFriendPrivateMemberModule
{
	private static readonly IFriendPrivateMemberShelf Shelf = new FriendPrivateMemberPart();

	private static readonly PrivateRack Rack = new();

	private sealed class PrivateHeater : FriendPrivateMemberPart, IFriendPrivateMemberHeater;

	private sealed class PrivateRack : FriendPrivateMemberPart, IFriendPrivateMemberRack;

	private static async Task<PrivateHeater> MakeHeaterAsync()
	{
		await Task.Yield();
		return new PrivateHeater();
	}

	private static FriendPrivateMemberPot MakePot() => new();

	internal static IFriendPrivateMemberUnused MakeUnused(IFriendPrivateMemberKettle kettle) => throw new InvalidOperationException("Nothing consumes it.");

	internal static IFriendPrivateMemberSpoon MakeSpoon() => new FriendPrivateMemberSpoon();

	/// <summary>An overload of another type, which leaves the internal factory the only one producing the spoon.</summary>
	public static IFriendPrivateMemberState MakeSpoon(IFriendPrivateMemberSpoon spoon) => spoon;

	internal static void Stir(IFriendPrivateMemberSpoon spoon) => ((FriendPrivateMemberPart)spoon).Activated = true;

	/// <summary>An overload of another type, which leaves the internal hook the only one for the spoon.</summary>
	public static void Stir(IFriendPrivateMemberBarista barista) { }

	internal static void Activate(IFriendPrivateMemberKettle kettle) => ((FriendPrivateMemberPart)kettle).Activated = true;

	internal static void Release(IFriendPrivateMemberKettle kettle) => ((FriendPrivateMemberPart)kettle).Released = true;

	private static async Task<IFriendPrivateMemberKettle> MakeKettleAsync()
	{
		await Task.Yield();
		return new FriendPrivateMemberKettlePart();
	}

	private static IFriendPrivateMemberCup MakeCup(IFriendPrivateMemberKettle kettle) => new FriendPrivateMemberPart { Kettle = kettle, };

	private static IFriendPrivateMemberScale MakeScale() => new FriendPrivateMemberPart();

	/// <summary>An overload of another type, which leaves the private factory the only one producing the scale.</summary>
	internal static IFriendPrivateMemberState MakeScale(IFriendPrivateMemberCup cup) => cup;
}

/// <summary>A container in the module's own assembly that imports <see cref="SupportFriendPrivateMemberModule" />.</summary>
[Container]
[Import(typeof(SupportFriendPrivateMemberModule))]
public static partial class FriendPrivateMemberLocalContainer;

internal interface IInternalModuleShelf : IFriendPrivateMemberState;

internal sealed class InternalModuleShelf : FriendPrivateMemberPart, IInternalModuleShelf;

/// <summary>
///     An internal <c>[Module]</c> that produces a public and an internal service through private members, which only this
///     assembly and the assemblies it grants <c>InternalsVisibleTo</c> can import, so each calls internal wrappers.
/// </summary>
[Module]
[Singleton<IFriendPrivateMemberState>(Factory = nameof(MakeHeater))]
[Singleton<IInternalModuleShelf>(Instance = nameof(Shelf))]
internal static partial class InternalPrivateMemberModule
{
	private static readonly IInternalModuleShelf Shelf = new InternalModuleShelf();

	private static IFriendPrivateMemberState MakeHeater() => new FriendPrivateMemberPart();
}

/// <summary>A container in the module's own assembly that imports <see cref="InternalPrivateMemberModule" />.</summary>
[Container]
[Import(typeof(InternalPrivateMemberModule))]
internal static partial class InternalModuleLocalContainer;
