namespace Awaiten.Tests.ContainerSupport;

/// <summary>A service the production container builds from a private implementation.</summary>
public interface ISupportPrivateTool
{
	/// <summary>Set by the base's private activation hook.</summary>
	bool Activated { get; }

	/// <summary>Set when the container disposes the private implementation.</summary>
	bool Disposed { get; }

	/// <summary>Filled by the base's <c>[InjectProperty]</c> entry.</summary>
	ISupportPrivateLabel? Label { get; }
}

/// <summary>A label the production container fills into its private tool.</summary>
public interface ISupportPrivateLabel;

/// <summary>The label <see cref="SupportPrivateBaseContainer" /> registers.</summary>
public sealed class SupportPrivateLabel : ISupportPrivateLabel;

/// <summary>Marker for the private scan match of <see cref="SupportPrivateBaseContainer" />.</summary>
public interface ISupportPrivatePlugin;

/// <summary>
///     A production container in a referenced assembly whose implementations are private to it, so a test container in
///     another assembly builds them through the factories the base generates.
/// </summary>
[Container]
[Singleton<Tool, ISupportPrivateTool>(OnActivated = nameof(Activate))]
[Singleton<SupportPrivateLabel, ISupportPrivateLabel>]
[InjectProperty<Tool>(nameof(Tool.Label))]
[Scan<ISupportPrivatePlugin>(As = ScanAs.Marker)]
public static partial class SupportPrivateBaseContainer
{
	private static void Activate(Tool tool) => tool.Activated = true;

	private sealed class Tool : ISupportPrivateTool, System.IDisposable
	{
		public bool Activated { get; set; }

		public bool Disposed { get; private set; }

		public ISupportPrivateLabel? Label { get; set; }

		public void Dispose() => Disposed = true;
	}

	private sealed class Plugin : ISupportPrivatePlugin;
}
