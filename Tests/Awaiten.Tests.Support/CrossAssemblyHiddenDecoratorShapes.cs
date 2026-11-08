using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if CONTAINER_SUPPORT
using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;
#else

namespace Awaiten.Tests.Support;

/// <summary>A link of a decorated ledger, seen through the public base of the internal service.</summary>
public interface IShapeLedger
{
	/// <summary>Whether the container disposed this link.</summary>
	bool Disposed { get; }

	/// <summary>The chain from this link inwards, for example <c>audit(base(ledger))</c>.</summary>
	string Describe();

	/// <summary>The links from this one inwards, outermost first.</summary>
	IReadOnlyList<IShapeLedger> Links();
}

/// <summary>A public service of the implementation of the internal ledger, decorated on its own.</summary>
public interface IShapeLedgerView : IShapeLedger;

/// <summary>A scoped reader of the internal ledger, its keyed registration and its public view.</summary>
public interface IShapeLedgerReader
{
	/// <summary>The unkeyed internal ledger.</summary>
	IShapeLedger Ledger { get; }

	/// <summary>The internal ledger under the key <c>audit</c>.</summary>
	IShapeLedger AuditLedger { get; }

	/// <summary>The public view of the ledger.</summary>
	IShapeLedgerView View { get; }
}

/// <summary>A channel, the public base of an internal service that is composed and decorated.</summary>
public interface IShapeChannel
{
	/// <summary>The name of the channel and everything it wraps.</summary>
	string Name { get; }
}

/// <summary>A notifier that takes the composed internal channel and the collection of its members.</summary>
public interface IShapeChannelNotifier
{
	/// <summary>The composite channel.</summary>
	IShapeChannel Channel { get; }

	/// <summary>The members of the internal channel's collection.</summary>
	IReadOnlyList<IShapeChannel> Members { get; }
}

/// <summary>A reader of an internal till that two modules decorate.</summary>
public interface IShapeTillReader
{
	/// <summary>The chain of the till, outermost link first.</summary>
	string Till { get; }
}
#endif

internal interface IShapeStartable
{
	Task StartAsync(CancellationToken cancellationToken);
}

internal interface IHiddenShapeLedger : IShapeLedger;

internal abstract class ShapeLedgerLink(IShapeLedger? inner) : IShapeLedger, IDisposable
{
	protected abstract string Name { get; }

	public bool Disposed { get; private set; }

	public string Describe() => inner is null ? Name : Name + "(" + inner.Describe() + ")";

	public IReadOnlyList<IShapeLedger> Links() => new IShapeLedger[] { this, }.Concat(inner?.Links() ?? []).ToArray();

	public void Dispose() => Disposed = true;
}

internal sealed class ShapeLedger() : ShapeLedgerLink(null), IHiddenShapeLedger, IShapeLedgerView
{
	protected override string Name => "ledger";
}

internal sealed class BaseInnerLedger(IShapeLedger inner) : ShapeLedgerLink(inner), IHiddenShapeLedger, IShapeStartable
{
	private bool _started;

	protected override string Name => _started ? "base" : "unstarted";

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_started = true;
		return Task.CompletedTask;
	}
}

internal sealed class AuditLedger(IHiddenShapeLedger inner) : ShapeLedgerLink(inner), IHiddenShapeLedger
{
	protected override string Name => "audit";
}

internal sealed class ViewLedger(IShapeLedgerView inner) : ShapeLedgerLink(inner), IShapeLedgerView
{
	protected override string Name => "view";
}

internal sealed class ShapeLedgerReader(IHiddenShapeLedger ledger, [FromKey("audit")] IHiddenShapeLedger auditLedger, IShapeLedgerView view) : IShapeLedgerReader
{
	public IShapeLedger Ledger => ledger;

	public IShapeLedger AuditLedger => auditLedger;

	public IShapeLedgerView View => view;
}

/// <summary>
///     A <c>[Module]</c> that decorates an internal service through a decorator whose inner parameter is the service's
///     public base, under each key, adds a keyed decorator, and decorates a public service of the same implementation
///     with a chain of its own. The decorator with the base-type inner parameter is started by the module's convention.
/// </summary>
[Module]
[AsyncInitializer<IShapeStartable>(nameof(IShapeStartable.StartAsync))]
[Scoped<ShapeLedger, IHiddenShapeLedger>]
[Scoped<ShapeLedger, IHiddenShapeLedger>(Key = "audit")]
[Scoped<ShapeLedger, IShapeLedgerView>]
[Scoped<ShapeLedgerReader, IShapeLedgerReader>]
[Decorate<BaseInnerLedger, IHiddenShapeLedger>(AllKeys = true)]
[Decorate<AuditLedger, IHiddenShapeLedger>(Keys = new object?[] { "audit", })]
[Decorate<ViewLedger, IShapeLedgerView>]
public static partial class HiddenLedgerShapeModule;

internal interface IHiddenShapeChannel : IShapeChannel;

internal sealed class EmailChannel : IHiddenShapeChannel
{
	public string Name => "email";
}

internal sealed class SmsChannel : IHiddenShapeChannel
{
	public string Name => "sms";
}

internal sealed class TracedChannel(IShapeChannel inner) : IHiddenShapeChannel
{
	public string Name => "traced " + inner.Name;
}

internal sealed class AllChannels(IReadOnlyList<IHiddenShapeChannel> channels) : IHiddenShapeChannel
{
	public string Name => string.Join(", ", channels.Select(channel => channel.Name));
}

internal sealed class ShapeChannelNotifier(IHiddenShapeChannel channel, IEnumerable<IHiddenShapeChannel> members) : IShapeChannelNotifier
{
	public IShapeChannel Channel => channel;

	public IReadOnlyList<IShapeChannel> Members => members.ToArray();
}

/// <summary>A <c>[Module]</c> that composes an internal service and decorates its members through the service's public base.</summary>
[Module]
[Singleton<EmailChannel, IHiddenShapeChannel>]
[Transient<SmsChannel, IHiddenShapeChannel>]
[Singleton<ShapeChannelNotifier, IShapeChannelNotifier>]
[Composite<AllChannels, IHiddenShapeChannel>]
[Decorate<TracedChannel, IHiddenShapeChannel>]
public static partial class HiddenChannelShapeModule;

internal interface IHiddenShapeTill
{
	string Describe();
}

internal sealed class ShapeTill : IHiddenShapeTill
{
	public string Describe() => "till";
}

internal sealed class FirstTill(IHiddenShapeTill inner) : IHiddenShapeTill
{
	public string Describe() => "first(" + inner.Describe() + ")";
}

internal sealed class SecondTill(IHiddenShapeTill inner) : IHiddenShapeTill
{
	public string Describe() => "second(" + inner.Describe() + ")";
}

internal sealed class ShapeTillReader(IHiddenShapeTill till) : IShapeTillReader
{
	public string Till => till.Describe();
}

/// <summary>A <c>[Module]</c> that registers an internal till and decorates it.</summary>
[Module]
[Singleton<ShapeTill, IHiddenShapeTill>]
[Singleton<ShapeTillReader, IShapeTillReader>]
[Decorate<FirstTill, IHiddenShapeTill>]
public static partial class HiddenTillShapeModule;

/// <summary>A <c>[Module]</c> that decorates the internal till <see cref="HiddenTillShapeModule" /> registers.</summary>
[Module]
[Decorate<SecondTill, IHiddenShapeTill>]
public static partial class HiddenTillDecoratorShapeModule;
#if CONTAINER_SUPPORT

/// <summary>A container in the modules' own assembly that imports <see cref="HiddenLedgerShapeModule" />.</summary>
[Container]
[Import(typeof(HiddenLedgerShapeModule))]
public static partial class LocalLedgerShapeContainer;

/// <summary>A container in the modules' own assembly that imports <see cref="HiddenChannelShapeModule" />.</summary>
[Container]
[Import(typeof(HiddenChannelShapeModule))]
public static partial class LocalChannelShapeContainer;

/// <summary>A container in the modules' own assembly that imports the till modules, registering one first.</summary>
[Container]
[Import(typeof(HiddenTillShapeModule))]
[Import(typeof(HiddenTillDecoratorShapeModule))]
public static partial class LocalTillShapeContainer;

/// <summary>A container in the modules' own assembly that imports the till modules, decorating one first.</summary>
[Container]
[Import(typeof(HiddenTillDecoratorShapeModule))]
[Import(typeof(HiddenTillShapeModule))]
public static partial class LocalReversedTillShapeContainer;
#endif
