using System;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>What happened to an internal service its module produces through a private member.</summary>
public interface IPrivateMemberState
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
	IPrivateMemberState? Kettle { get; }
}

/// <summary>A barista that takes the internal services its module produces through private members.</summary>
public interface IPrivateMemberBarista
{
	/// <summary>The singleton kettle, from a private async factory with hooks.</summary>
	IPrivateMemberState Kettle { get; }

	/// <summary>The scoped cup, from a private factory that takes the kettle.</summary>
	IPrivateMemberState Cup { get; }

	/// <summary>A transient scale, from a private factory.</summary>
	IPrivateMemberState Scale { get; }

	/// <summary>A second transient scale.</summary>
	IPrivateMemberState OtherScale { get; }

	/// <summary>The shelf, a private instance.</summary>
	IPrivateMemberState Shelf { get; }

	/// <summary>The singleton heater, from a private async factory returning a type private to the module.</summary>
	IPrivateMemberState Heater { get; }

	/// <summary>The rack, a private instance of a type private to the module.</summary>
	IPrivateMemberState Rack { get; }

	/// <summary>The singleton pot, from a private factory returning a type a convention initializes but its service does not.</summary>
	IPrivateMemberState Pot { get; }

	/// <summary>A transient spoon, from an internal factory beside a public overload of another type.</summary>
	IPrivateMemberSpoon Spoon { get; }
}

/// <summary>A public service the module produces through an internal factory.</summary>
public interface IPrivateMemberSpoon : IPrivateMemberState;

internal interface IPrivateMemberStartable
{
	Task StartAsync();
}

internal interface IPrivateMemberHeater : IPrivateMemberState;

internal interface IPrivateMemberRack : IPrivateMemberState;

internal interface IPrivateMemberPot : IPrivateMemberState;

internal interface IPrivateMemberUnused;

internal sealed class PrivateMemberPot : PrivateMemberPart, IPrivateMemberPot
{
	public Task WarmAsync()
	{
		Started = true;
		return Task.CompletedTask;
	}
}

internal sealed class PrivateMemberSpoon : PrivateMemberPart, IPrivateMemberSpoon;

internal interface IPrivateMemberKettle : IPrivateMemberState, IPrivateMemberStartable;

internal interface IPrivateMemberCup : IPrivateMemberState;

internal interface IPrivateMemberScale : IPrivateMemberState;

internal interface IPrivateMemberShelf : IPrivateMemberState;

internal class PrivateMemberPart : IPrivateMemberCup, IPrivateMemberScale, IPrivateMemberShelf, IDisposable
{
	public bool Activated { get; set; }

	public bool Started { get; protected set; }

	public bool Released { get; set; }

	public bool Disposed { get; private set; }

	public IPrivateMemberState? Kettle { get; set; }

	public void Dispose() => Disposed = true;
}

internal sealed class PrivateMemberKettlePart : PrivateMemberPart, IPrivateMemberKettle
{
	public Task StartAsync()
	{
		Started = true;
		return Task.CompletedTask;
	}
}

internal sealed class PrivateMemberBarista(
	IPrivateMemberKettle kettle,
	IPrivateMemberCup cup,
	IPrivateMemberScale scale,
	IPrivateMemberScale otherScale,
	IPrivateMemberShelf shelf,
	IPrivateMemberHeater heater,
	IPrivateMemberRack rack,
	IPrivateMemberPot pot,
	IPrivateMemberSpoon spoon) : IPrivateMemberBarista
{
	public IPrivateMemberState Kettle => kettle;

	public IPrivateMemberState Cup => cup;

	public IPrivateMemberState Scale => scale;

	public IPrivateMemberState OtherScale => otherScale;

	public IPrivateMemberState Shelf => shelf;

	public IPrivateMemberState Heater => heater;

	public IPrivateMemberState Rack => rack;

	public IPrivateMemberState Pot => pot;

	public IPrivateMemberSpoon Spoon => spoon;
}

/// <summary>
///     A <c>[Module]</c> that produces internal services through private <c>Factory</c> and <c>Instance</c> members, with
///     hooks, an async initializer convention and disposal, members returning types private to the module, an unconsumed
///     internal factory, and an internal factory and hook beside public overloads of other types. A container in another
///     assembly takes the internal services through their handles.
/// </summary>
[Module]
[AsyncInitializer<IPrivateMemberStartable>(nameof(IPrivateMemberStartable.StartAsync))]
[AsyncInitializer<PrivateMemberPot>(nameof(PrivateMemberPot.WarmAsync))]
[Singleton<IPrivateMemberKettle>(Factory = nameof(MakeKettleAsync), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
[Scoped<IPrivateMemberCup>(Factory = nameof(MakeCup))]
[Transient<IPrivateMemberScale>(Factory = nameof(MakeScale))]
[Singleton<IPrivateMemberShelf>(Instance = nameof(Shelf))]
[Singleton<IPrivateMemberHeater>(Factory = nameof(MakeHeaterAsync))]
[Singleton<IPrivateMemberRack>(Instance = nameof(Rack))]
[Singleton<IPrivateMemberPot>(Factory = nameof(MakePot))]
[Singleton<IPrivateMemberUnused>(Factory = nameof(MakeUnused))]
[Transient<IPrivateMemberSpoon>(Factory = nameof(MakeSpoon), OnActivated = nameof(Stir))]
[Scoped<PrivateMemberBarista, IPrivateMemberBarista>]
public static partial class CrossAssemblyPrivateMemberModule
{
	private static readonly IPrivateMemberShelf Shelf = new PrivateMemberPart();

	private static readonly PrivateRack Rack = new();

	private sealed class PrivateHeater : PrivateMemberPart, IPrivateMemberHeater;

	private sealed class PrivateRack : PrivateMemberPart, IPrivateMemberRack;

	private static async Task<PrivateHeater> MakeHeaterAsync()
	{
		await Task.Yield();
		return new PrivateHeater();
	}

	private static PrivateMemberPot MakePot() => new();

	internal static IPrivateMemberUnused MakeUnused(IPrivateMemberKettle kettle) => throw new InvalidOperationException("Nothing consumes it.");

	internal static IPrivateMemberSpoon MakeSpoon() => new PrivateMemberSpoon();

	/// <summary>An overload of another type, which leaves the internal factory the only one producing the spoon.</summary>
	public static IPrivateMemberState MakeSpoon(IPrivateMemberSpoon spoon) => spoon;

	internal static void Stir(IPrivateMemberSpoon spoon) => ((PrivateMemberPart)spoon).Activated = true;

	/// <summary>An overload of another type, which leaves the internal hook the only one for the spoon.</summary>
	public static void Stir(IPrivateMemberBarista barista) { }

	internal static void Activate(IPrivateMemberKettle kettle) => ((PrivateMemberPart)kettle).Activated = true;

	internal static void Release(IPrivateMemberKettle kettle) => ((PrivateMemberPart)kettle).Released = true;

	private static async Task<IPrivateMemberKettle> MakeKettleAsync()
	{
		await Task.Yield();
		return new PrivateMemberKettlePart();
	}

#pragma warning disable CA1859 // the factory returns the registered service, which is the shape under test
	private static IPrivateMemberCup MakeCup(IPrivateMemberKettle kettle) => new PrivateMemberPart { Kettle = kettle, };

	private static IPrivateMemberScale MakeScale() => new PrivateMemberPart();
#pragma warning restore CA1859
}
