using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A container-side [InjectProperty] entry on a public implementation that a module of Awaiten.Tests.Support builds
///     through a generated handle, which the container sets through the module's alias on the instance the handle holds.
/// </summary>
public class ModuleHandleInjectPropertyTests
{
	[Test]
	public async Task EntryOnAnImplementationBuiltThroughAHandle_IsSetBeforeTheModulesHookAndInitializerRun()
	{
		using HandleEntryContainer.Root container = new();

		HandledBrewer brewer = (await container.ResolveAsync<IHandledBrewerUser>(TestContext.Current!.Execution.CancellationToken)).Brewer;

		await That(brewer.Clock).IsExactly<HandleEntryClock>()
			.Because("the container sets its entry on the instance the module's handle built");
		await That(brewer.ClockSetWhenActivated).IsTrue()
			.Because("the entry is set before the module's OnActivated hook runs, as a container of the module's own assembly sets it while constructing the brewer");
		await That(brewer.ClockSetWhenStarted).IsTrue()
			.Because("the entry is set before the module's async initializer runs");
	}
}

/// <summary>The clock <see cref="HandleEntryContainer" /> injects into the brewer.</summary>
public sealed class HandleEntryClock : IBrewerClock;

[Container]
[Import(typeof(CrossAssemblyHandleEntryModule))]
[Singleton<HandleEntryClock, IBrewerClock>]
[InjectProperty<HandledBrewer>(nameof(HandledBrewer.Clock))]
public static partial class HandleEntryContainer;
