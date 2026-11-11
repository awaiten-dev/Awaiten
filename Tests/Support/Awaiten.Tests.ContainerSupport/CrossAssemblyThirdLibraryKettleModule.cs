using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>
///     A <c>[Module]</c> of a library that Awaiten.Tests.Support grants nothing, which registers <see cref="InternalConstructorKettle" />
///     and a closing of <see cref="InternalConstructorRepository{T}" /> though it cannot construct them. A container that
///     imports <see cref="CrossAssemblyInternalConstructorModule" /> too builds them through that module's generated factories.
/// </summary>
[Module]
[Singleton<InternalConstructorKettle>]
[Singleton<InternalConstructorRepository<long>>]
public static partial class CrossAssemblyThirdLibraryKettleModule;

/// <summary>A container in a third assembly that another assembly extends, inheriting its registrations of <see cref="InternalConstructorKettle" /> and a closing of <see cref="InternalConstructorRepository{T}" />.</summary>
[Container]
[Import(typeof(CrossAssemblyInternalConstructorModule))]
[Singleton<InternalConstructorKettle>]
[Singleton<InternalConstructorRepository<long>>]
public static partial class CrossAssemblyThirdLibraryKettleBaseContainer;
