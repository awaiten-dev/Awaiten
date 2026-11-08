using System.Linq;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of internal services that pass through three modules of Awaiten.Tests.Support: the user takes
///     the kettle of a second module, which takes the bean of a third. Each module builds its service through the
///     handles of the others, so a container here that imports all three resolves the chain, in any order.
/// </summary>
public partial class ModuleHandleChainTests
{
	[Test]
	public async Task ServiceOfAThirdModule_ReachesTheUserThroughTheHandles()
	{
		using ChainContainer.Root container = new();

		await That(container.Resolve<IChainUser>().Bean.GetType().Name).IsEqualTo("ChainBean")
			.Because("the kettle module builds the kettle from the bean the third module shares through its handle");
	}

	[Test]
	public async Task ImportedUserFirst_ServiceOfAThirdModuleReachesTheUser()
	{
		using UserFirstChainContainer.Root container = new();

		await That(container.Resolve<IChainUser>().Bean.GetType().Name).IsEqualTo("ChainBean")
			.Because("the handles do not depend on the order in which a container imports the modules");
	}

	[Test]
	public async Task InternalServiceNothingTakes_GetsNoHandle()
	{
		string members = string.Join(", ", typeof(CrossAssemblyChainBeanModule).GetNestedType("Internals")!.GetMembers().Select(member => member.Name));

		await That(members).Contains("Hidden_ChainBean_")
			.And.DoesNotContain("ChainSpareBean")
			.Because("no generated member of any module of the assembly takes the spare bean, so its module emits nothing for it");
	}

	[Container]
	[Import(typeof(CrossAssemblyChainKettleModule))]
	[Import(typeof(CrossAssemblyChainUserModule))]
	[Import(typeof(CrossAssemblyChainBeanModule))]
	public static partial class ChainContainer;

	[Container]
	[Import(typeof(CrossAssemblyChainUserModule))]
	[Import(typeof(CrossAssemblyChainKettleModule))]
	[Import(typeof(CrossAssemblyChainBeanModule))]
	public static partial class UserFirstChainContainer;
}
