using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>A <c>[Module]</c> that registers the public clock of Awaiten.Tests.Support, which a module there registers too.</summary>
[Module]
[Singleton<LibraryClock, ILibraryClock>]
[Singleton<LibraryClock, ILibraryTimer>]
public static partial class CrossLibraryTimerModule;

/// <summary>A <c>[Module]</c> that registers the public clock a module of Awaiten.Tests.Support builds through a handle.</summary>
[Module]
[Singleton<HandledLibraryClock>]
public static partial class CrossLibraryHandledTimerModule;