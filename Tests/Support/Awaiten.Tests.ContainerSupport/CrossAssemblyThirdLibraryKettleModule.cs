using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>
///     A <c>[Module]</c> of a library that Awaiten.Tests.Support grants nothing, which registers <see cref="InternalConstructorKettle" />
///     though it cannot construct it. A container that imports <see cref="CrossAssemblyInternalConstructorModule" /> too
///     builds the kettle through that module's generated factory.
/// </summary>
[Module]
[Singleton<InternalConstructorKettle>]
public static partial class CrossAssemblyThirdLibraryKettleModule;

/// <summary>A container in a third assembly that another assembly extends, inheriting its registration of <see cref="InternalConstructorKettle" />.</summary>
[Container]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
[Singleton<InternalConstructorKettle>]
public static partial class CrossAssemblyThirdLibraryKettleBaseContainer;
