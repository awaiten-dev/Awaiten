namespace Awaiten.Tests.Support;

/// <summary>A clock service that <see cref="CrossLibraryClockModule" /> registers its public clock under.</summary>
public interface ILibraryClock;

/// <summary>A timer service that a module of Awaiten.Tests.ContainerSupport registers the same public clock under.</summary>
public interface ILibraryTimer;

/// <summary>A public clock that modules of two libraries register.</summary>
public sealed class LibraryClock : ILibraryClock, ILibraryTimer;

/// <summary>A <c>[Module]</c> that registers the public clock under its clock service.</summary>
[Module]
[Singleton<LibraryClock, ILibraryClock>]
public static partial class CrossLibraryClockModule;

/// <summary>A public clock that <see cref="CrossLibraryHandledClockModule" /> builds through a generated handle.</summary>
public sealed class HandledLibraryClock : IHandledLibraryClock;

/// <summary>A user of the clock the handle of <see cref="CrossLibraryHandledClockModule" /> holds.</summary>
public interface IHandledLibraryClockUser
{
	/// <summary>The clock the user received.</summary>
	object Clock { get; }
}

internal interface IHandledLibraryClock;

internal sealed class HandledLibraryClockUser(IHandledLibraryClock clock) : IHandledLibraryClockUser
{
	public object Clock => clock;
}

/// <summary>A <c>[Module]</c> that registers the public clock under an internal service, so it builds it through a handle.</summary>
[Module]
[Singleton<HandledLibraryClock, IHandledLibraryClock>]
[Singleton<HandledLibraryClockUser, IHandledLibraryClockUser>]
public static partial class CrossLibraryHandledClockModule;
