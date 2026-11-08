using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Modules of two libraries, Awaiten.Tests.Support and Awaiten.Tests.ContainerSupport, that each register the same
///     public implementation of the first. A container that imports both builds one instance of it, as it does for two
///     modules of one assembly.
/// </summary>
public partial class CrossLibraryCoalescingTests
{
	[Test]
	public async Task PublicImplementationModulesOfTwoLibrariesRegister_IsOneInstance()
	{
		using ClockContainer.Root container = new();

		await That((object)container.Resolve<ILibraryClock>()).IsSameAs(container.Resolve<ILibraryTimer>())
			.Because("both libraries register the same implementation, so the container coalesces their registrations into one instance");
	}

	[Test]
	public async Task PublicImplementationTheOtherLibraryBuildsThroughAHandle_IsOneInstance()
	{
		using HandledClockContainer.Root container = new();

		await That(container.Resolve<HandledLibraryClock>()).IsSameAs(container.Resolve<IHandledLibraryClockUser>().Clock)
			.Because("the registration of the second library serves the instance the first library's handle holds");
	}

	[Container]
	[Import(typeof(CrossLibraryClockModule))]
	[Import(typeof(CrossLibraryTimerModule))]
	public static partial class ClockContainer;

	[Container]
	[Import(typeof(CrossLibraryHandledClockModule))]
	[Import(typeof(CrossLibraryHandledTimerModule))]
	public static partial class HandledClockContainer;
}
