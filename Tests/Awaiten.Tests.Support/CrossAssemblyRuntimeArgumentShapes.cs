using System;
#if CONTAINER_SUPPORT
using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;
#else

namespace Awaiten.Tests.Support;

/// <summary>An order a brew is built from at run time.</summary>
public sealed class ArgumentOrder(string drink)
{
	/// <summary>The ordered drink.</summary>
	public string Drink { get; } = drink;
}

/// <summary>A brew built from the runtime arguments of a factory, seen through the public base of the internal service.</summary>
public interface IArgumentBrew
{
	/// <summary>The order the brew was built from.</summary>
	ArgumentOrder Order { get; }

	/// <summary>The shots the brew was built with.</summary>
	int Shots { get; }

	/// <summary>The implementation that built the brew.</summary>
	string Kind { get; }

	/// <summary>Whether the container disposed the brew.</summary>
	bool Disposed { get; }
}

/// <summary>A barista that takes factories of an internal brew with runtime arguments.</summary>
public interface IArgumentBarista
{
	/// <summary>A brew of the unkeyed registration.</summary>
	IArgumentBrew Brew(ArgumentOrder order, int shots);

	/// <summary>A brew of the registration under the key <c>decaf</c>.</summary>
	IArgumentBrew Decaf(ArgumentOrder order, int shots);

	/// <summary>A brew of the registration under the key <c>member</c>, which a factory member produces.</summary>
	IArgumentBrew Member(ArgumentOrder order, int shots);

	/// <summary>A brew of the unkeyed registration and the owner that disposes it.</summary>
	(IArgumentBrew Brew, IDisposable Owner) Owned(ArgumentOrder order, int shots);
}

/// <summary>A transient till that takes a factory with runtime arguments of an internal brew.</summary>
public interface IArgumentTill
{
	/// <summary>A brew of the unkeyed registration.</summary>
	IArgumentBrew Ring(ArgumentOrder order, int shots);
}

/// <summary>A receipt of an order, one of two public services of an internal implementation built from runtime arguments.</summary>
public interface IArgumentReceipt
{
	/// <summary>The order the receipt was built from.</summary>
	ArgumentOrder Order { get; }
}

/// <summary>A ticket of an order, the other public service of the implementation of <see cref="IArgumentReceipt" />.</summary>
public interface IArgumentTicket
{
	/// <summary>The order the ticket was built from.</summary>
	ArgumentOrder Order { get; }
}

/// <summary>A receipt of an order whose internal implementation is async-initialized and built from runtime arguments.</summary>
public interface IArgumentStartedReceipt
{
	/// <summary>The order the receipt was built from.</summary>
	ArgumentOrder Order { get; }

	/// <summary>Whether the container awaited the initialization of the receipt.</summary>
	bool Started { get; }
}

/// <summary>A desk a container registers, which takes an async factory of <see cref="IArgumentStartedReceipt" />.</summary>
public sealed class ArgumentReceiptDesk(Func<ArgumentOrder, System.Threading.Tasks.Task<IArgumentStartedReceipt>> receipts)
{
	/// <summary>A receipt of <paramref name="order" />, once initialized.</summary>
	public System.Threading.Tasks.Task<IArgumentStartedReceipt> Issue(ArgumentOrder order) => receipts(order);
}
#endif

internal interface IHiddenArgumentBrew : IArgumentBrew;

internal abstract class ArgumentBrewBase(ArgumentOrder order, int shots) : IHiddenArgumentBrew, IDisposable
{
	public ArgumentOrder Order { get; } = order;

	public int Shots { get; } = shots;

	public abstract string Kind { get; }

	public bool Disposed { get; private set; }

	public void Dispose() => Disposed = true;
}

internal sealed class ArgumentBrew([Arg] ArgumentOrder order, [Arg] int shots) : ArgumentBrewBase(order, shots)
{
	public override string Kind => "regular";
}

internal sealed class DecafArgumentBrew([Arg] ArgumentOrder order, [Arg] int shots) : ArgumentBrewBase(order, shots)
{
	public override string Kind => "decaf";
}

internal sealed class MemberArgumentBrew(ArgumentOrder order, int shots) : ArgumentBrewBase(order, shots)
{
	public override string Kind => "member";
}

internal sealed class ArgumentReceipt([Arg] ArgumentOrder order) : IArgumentReceipt, IArgumentTicket
{
	public ArgumentOrder Order { get; } = order;
}

internal interface IHiddenStartedReceipt;

internal sealed class StartedArgumentReceipt([Arg] ArgumentOrder order) : IArgumentStartedReceipt, IHiddenStartedReceipt
{
	public ArgumentOrder Order { get; } = order;

	public bool Started { get; private set; }

	public async System.Threading.Tasks.Task StartAsync()
	{
		await System.Threading.Tasks.Task.Yield();
		Started = true;
	}
}

internal sealed class ArgumentTill(Func<ArgumentOrder, int, IHiddenArgumentBrew> brew) : IArgumentTill
{
	public IArgumentBrew Ring(ArgumentOrder order, int shots) => brew(order, shots);
}

internal sealed class ArgumentBarista(
	Func<ArgumentOrder, int, IHiddenArgumentBrew> brew,
	[FromKey("decaf")] Func<ArgumentOrder, int, IHiddenArgumentBrew> decaf,
	[FromKey("member")] Func<ArgumentOrder, int, IHiddenArgumentBrew> member,
	Func<ArgumentOrder, int, Owned<IHiddenArgumentBrew>> owned) : IArgumentBarista
{
	public IArgumentBrew Brew(ArgumentOrder order, int shots) => brew(order, shots);

	public IArgumentBrew Decaf(ArgumentOrder order, int shots) => decaf(order, shots);

	public IArgumentBrew Member(ArgumentOrder order, int shots) => member(order, shots);

	public (IArgumentBrew Brew, IDisposable Owner) Owned(ArgumentOrder order, int shots)
	{
		Owned<IHiddenArgumentBrew> handle = owned(order, shots);
		return (handle.Value, handle);
	}
}

/// <summary>
///     A <c>[Module]</c> whose public barista takes factories with runtime arguments of an internal brew: constructed,
///     under a key, and produced by a private factory member, and one that hands out an owned brew, and a transient till. An internal receipt
///     built from a runtime argument is registered under two public services, and an async-initialized one under a public
///     and an internal service.
/// </summary>
[Module]
[AsyncInitializer<StartedArgumentReceipt>(nameof(StartedArgumentReceipt.StartAsync))]
[Transient<StartedArgumentReceipt, IArgumentStartedReceipt>]
[Transient<StartedArgumentReceipt, IHiddenStartedReceipt>]
[Transient<ArgumentBrew, IHiddenArgumentBrew>]
[Transient<DecafArgumentBrew, IHiddenArgumentBrew>(Key = "decaf")]
[Transient<IHiddenArgumentBrew>(Key = "member", Factory = nameof(MakeBrew))]
[Scoped<ArgumentBarista, IArgumentBarista>]
[Transient<ArgumentTill, IArgumentTill>]
[Transient<ArgumentReceipt, IArgumentReceipt>]
[Transient<ArgumentReceipt, IArgumentTicket>]
#if CONTAINER_SUPPORT
public static partial class FriendHiddenArgumentModule
#else
public static partial class HiddenArgumentModule
#endif
{
	private static IHiddenArgumentBrew MakeBrew([Arg] ArgumentOrder order, [Arg] int shots) => new MemberArgumentBrew(order, shots);
}
#if CONTAINER_SUPPORT

/// <summary>A container in the module's own assembly that imports <see cref="FriendHiddenArgumentModule" />.</summary>
[Container]
[Import(typeof(FriendHiddenArgumentModule))]
[Transient<ArgumentReceiptDesk>]
public static partial class LocalArgumentShapeContainer;
#endif
