using System.Collections.Generic;

namespace Awaiten.Tests.Support;

/// <summary>A public service the module produces through a private factory.</summary>
public interface IPrivateFactoryKettle;

/// <summary>A public service the module provides through a private instance.</summary>
public interface IPrivateInstanceShelf;

/// <summary>A public service a decorator wraps, which also takes a collection of an internal type nothing registers.</summary>
public interface ILeftoverGrinder
{
	/// <summary>How many elements the decorator received in its collection of the internal type.</summary>
	int Secrets { get; }
}

internal interface ILeftoverSecret;

internal sealed class LeftoverKettle : IPrivateFactoryKettle;

internal sealed class LeftoverShelf : IPrivateInstanceShelf;

internal sealed class LeftoverGrinder : ILeftoverGrinder
{
	public int Secrets => -1;
}

internal sealed class CountingGrinder(ILeftoverGrinder inner, IEnumerable<ILeftoverSecret> secrets) : ILeftoverGrinder
{
	public ILeftoverGrinder Inner { get; } = inner;

	public int Secrets { get; } = new List<ILeftoverSecret>(secrets).Count;
}

/// <summary>
///     A <c>[Module]</c> with a private factory and a private instance of public services, which every container calls
///     through the wrappers the module generates, and a decorator whose collection of an internal type nothing
///     registers the module's factory passes empty.
/// </summary>
[Module]
[Singleton<IPrivateFactoryKettle>(Factory = nameof(MakeKettle))]
[Singleton<IPrivateInstanceShelf>(Instance = nameof(Shelf))]
[Singleton<LeftoverGrinder, ILeftoverGrinder>]
[Decorate<CountingGrinder, ILeftoverGrinder>]
public static partial class CrossAssemblyLeftoverShapesModule
{
	private static IPrivateInstanceShelf Shelf { get; } = new LeftoverShelf();

	/// <summary>The shelf the module provides, for a test to compare against.</summary>
	public static IPrivateInstanceShelf ExpectedShelf => Shelf;

#pragma warning disable CA1859 // the factory returns the registered service, which is the shape under test
	private static IPrivateFactoryKettle MakeKettle() => new LeftoverKettle();
#pragma warning restore CA1859
}
