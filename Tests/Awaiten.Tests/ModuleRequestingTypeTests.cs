using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     The requesting type a <c>[RequestingType]</c> factory receives for the internal implementations
///     <see cref="RequestingTypeModule" /> builds through its generated factories. The module lives in the referenced
///     Awaiten.Tests.Support assembly, which grants this assembly no <c>InternalsVisibleTo</c>, yet each factory
///     receives the implementation, as in a container that constructs it itself.
/// </summary>
public partial class ModuleRequestingTypeTests
{
	[Test]
	public async Task InternalImplementation_PresentsItselfAsTheRequestingType()
	{
		using RequestingTypeContainer.Root container = new();

		IRequestingTypeKettle kettle = container.Resolve<IRequestingTypeKettle>();

		await That(kettle.Log.Category).IsEqualTo("RequestingTypeKettle")
			.Because("the module's factory names the internal implementation it builds, not the service it returns");
	}

	[Test]
	public async Task InternalDecorator_PresentsItselfAsTheRequestingType()
	{
		using RequestingTypeContainer.Root container = new();

		IRequestingTypeGrinder grinder = container.Resolve<IRequestingTypeGrinder>();

		await That(grinder.Log.Category).IsEqualTo("LoggingRequestingTypeGrinder")
			.Because("the module's factory names the internal decorator it builds, not the decorated service");
	}

	[Test]
	public async Task ScanMatch_PresentsItselfAsTheRequestingType()
	{
		using RequestingTypeContainer.Root container = new();

		IRequestingTypePlugin plugin = container.Resolve<IRequestingTypePlugin>();

		await That(plugin.Log.Category).IsEqualTo("RequestingTypePlugin")
			.Because("the module's scan factory names the internal match it builds, not the interface it exposes");
	}

	[Container]
	[Import(typeof(RequestingTypeModule))]
	public static partial class RequestingTypeContainer;
}
