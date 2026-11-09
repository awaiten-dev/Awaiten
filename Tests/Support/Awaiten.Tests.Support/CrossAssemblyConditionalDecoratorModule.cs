namespace Awaiten.Tests.Support;

/// <summary>The switch the predicate of <see cref="CrossAssemblyConditionalDecoratorModule" /> reads.</summary>
public static class ConditionalModuleSwitch
{
	/// <summary>Whether the module's decorators apply to the next root.</summary>
	public static bool Enabled { get; set; }
}

/// <summary>A gateway the module decorates when its switch is on.</summary>
public interface IConditionalGateway
{
	/// <summary>The chain from this link inwards, for example <c>traced(gateway)</c>.</summary>
	string Describe();
}

/// <summary>A reader of the internal gateway the module decorates when its switch is on.</summary>
public interface IConditionalGatewayReader
{
	/// <summary>The chain of the internal gateway the reader received.</summary>
	string Gateway { get; }
}

internal interface IHiddenConditionalGateway
{
	string Describe();
}

internal sealed class ConditionalGateway : IConditionalGateway, IHiddenConditionalGateway
{
	public string Describe() => "gateway";
}

internal sealed class TracedConditionalGateway(IConditionalGateway inner) : IConditionalGateway
{
	public string Describe() => "traced(" + inner.Describe() + ")";
}

internal sealed class AuditedHiddenGateway(IHiddenConditionalGateway inner) : IHiddenConditionalGateway
{
	public string Describe() => "audited(" + inner.Describe() + ")";
}

internal sealed class ConditionalGatewayReader(IHiddenConditionalGateway gateway) : IConditionalGatewayReader
{
	public string Gateway => gateway.Describe();
}

/// <summary>
///     A <c>[Module]</c> that decorates a public service and an internal one through its handle, each only when its
///     public predicate is <see langword="true" />.
/// </summary>
[Module]
[Singleton<ConditionalGateway, IConditionalGateway>]
[Singleton<ConditionalGateway, IHiddenConditionalGateway>]
[Singleton<ConditionalGatewayReader, IConditionalGatewayReader>]
[Decorate<TracedConditionalGateway, IConditionalGateway>(When = nameof(Enabled))]
[Decorate<AuditedHiddenGateway, IHiddenConditionalGateway>(When = nameof(Enabled))]
public static partial class CrossAssemblyConditionalDecoratorModule
{
	/// <summary>Whether the decorators apply, read from <see cref="ConditionalModuleSwitch" />.</summary>
	public static bool Enabled => ConditionalModuleSwitch.Enabled;
}
