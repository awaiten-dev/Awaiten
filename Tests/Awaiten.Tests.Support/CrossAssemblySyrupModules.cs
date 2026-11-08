namespace Awaiten.Tests.Support;

/// <summary>A syrup that several modules of the support assembly each contribute an implementation of.</summary>
public interface ILibrarySyrup;

/// <summary>A public syrup a container in another assembly constructs itself.</summary>
public sealed class LibraryVanilla : ILibrarySyrup;

internal sealed class LibraryCaramel : ILibrarySyrup;

/// <summary>A <c>[Module]</c> that contributes the public syrup.</summary>
[Module]
[Singleton<LibraryVanilla, ILibrarySyrup>]
public static partial class CrossAssemblyVanillaModule;

/// <summary>A <c>[Module]</c> that contributes an internal syrup, which it builds through a generated factory.</summary>
[Module]
[Singleton<LibraryCaramel, ILibrarySyrup>]
public static partial class CrossAssemblyCaramelModule;

/// <summary>A <c>[Module]</c> that contributes the public syrup as an overridable default.</summary>
[Module]
[Singleton<LibraryVanilla, ILibrarySyrup>(Fallback = Fallback.Silent)]
public static partial class CrossAssemblyVanillaDefaultModule;

/// <summary>A <c>[Module]</c> that contributes the internal syrup as an overridable default.</summary>
[Module]
[Singleton<LibraryCaramel, ILibrarySyrup>(Fallback = Fallback.Silent)]
public static partial class CrossAssemblyCaramelDefaultModule;
