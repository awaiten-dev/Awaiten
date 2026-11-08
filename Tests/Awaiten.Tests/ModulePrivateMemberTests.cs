using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of internal services a <c>[Module]</c> produces through private <c>Factory</c> and
///     <c>Instance</c> members. A container in another assembly takes them through their handles, a container in the
///     module's own assembly or in one granted <c>InternalsVisibleTo</c> builds them itself through internal wrappers of
///     the members, and each shares, hooks, initializes and disposes them alike.
/// </summary>
public partial class ModulePrivateMemberTests
{
	[Test]
	public async Task ContainerInAnotherAssembly_SharesHooksInitializesAndDisposesThem()
	{
		IPrivateMemberBarista first;
		IPrivateMemberBarista second;
		await using (PrivateMemberContainer.Root container = new())
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = await scope.ResolveAsync<IPrivateMemberBarista>();
			}

			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				second = await scope.ResolveAsync<IPrivateMemberBarista>();
			}

			await That(second.Kettle).IsSameAs(first.Kettle)
				.Because("the singleton the private async factory produces is one instance");
			await That(first.Cup.Kettle).IsSameAs(first.Kettle)
				.Because("the private factory of the cup takes the singleton kettle");
			await That(second.Cup).IsNotSameAs(first.Cup)
				.Because("each scope gets its own cup");
			await That(first.OtherScale).IsNotSameAs(first.Scale)
				.Because("each dependency gets its own transient scale");
			await That(second.Shelf).IsSameAs(first.Shelf);
			await That(first.Kettle.Activated && first.Kettle.Started).IsTrue()
				.Because("the module's hook and convention run on the instance the private factory produces");
			await That(first.Cup.Disposed && first.Scale.Disposed && first.OtherScale.Disposed).IsTrue()
				.Because("the scope disposes the scoped and transient services it built");
			await That(first.Kettle.Released || first.Kettle.Disposed).IsFalse();
		}

		await That(first.Kettle.Released && first.Kettle.Disposed).IsTrue()
			.Because("the container releases and disposes the singleton the private factory produced");
		await That(first.Shelf.Disposed).IsFalse()
			.Because("the container never disposes an Instance");
	}

	[Test]
	public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_SharesHooksInitializesAndDisposesThem()
	{
		IFriendPrivateMemberBarista first;
		IFriendPrivateMemberBarista second;
		await using (FriendPrivateMemberContainer.Root container = new())
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = await scope.ResolveAsync<IFriendPrivateMemberBarista>();
			}

			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				second = await scope.ResolveAsync<IFriendPrivateMemberBarista>();
			}

			await That(second.Kettle).IsSameAs(first.Kettle)
				.Because("the singleton the internal wrapper of the private async factory produces is one instance");
			await That(first.Cup.Kettle).IsSameAs(first.Kettle);
			await That(second.Cup).IsNotSameAs(first.Cup);
			await That(first.OtherScale).IsNotSameAs(first.Scale);
			await That(second.Shelf).IsSameAs(first.Shelf);
			await That(first.Kettle.Activated && first.Kettle.Started).IsTrue()
				.Because("the container calls the module's internal hook and runs the convention itself");
			await That(first.Cup.Disposed && first.Scale.Disposed && first.OtherScale.Disposed).IsTrue();
			await That(first.Kettle.Released || first.Kettle.Disposed).IsFalse();
		}

		await That(first.Kettle.Released && first.Kettle.Disposed).IsTrue();
		await That(first.Shelf.Disposed).IsFalse()
			.Because("the container never disposes an Instance");
	}

	[Test]
	public async Task ContainerInTheModulesAssembly_SharesHooksInitializesAndDisposesThem()
	{
		IFriendPrivateMemberBarista first;
		IFriendPrivateMemberBarista second;
		await using (FriendPrivateMemberLocalContainer.Root container = new())
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = await scope.ResolveAsync<IFriendPrivateMemberBarista>();
			}

			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				second = await scope.ResolveAsync<IFriendPrivateMemberBarista>();
			}

			await That(second.Kettle).IsSameAs(first.Kettle)
				.Because("the singleton the internal wrapper of the private async factory produces is one instance");
			await That(first.Cup.Kettle).IsSameAs(first.Kettle);
			await That(second.Cup).IsNotSameAs(first.Cup);
			await That(first.OtherScale).IsNotSameAs(first.Scale);
			await That(second.Shelf).IsSameAs(first.Shelf);
			await That(first.Kettle.Activated && first.Kettle.Started).IsTrue();
			await That(first.Cup.Disposed && first.Scale.Disposed && first.OtherScale.Disposed).IsTrue();
			await That(first.Kettle.Released || first.Kettle.Disposed).IsFalse();
		}

		await That(first.Kettle.Released && first.Kettle.Disposed).IsTrue();
		await That(first.Shelf.Disposed).IsFalse()
			.Because("the container never disposes an Instance");
	}

	[Test]
	public async Task ContainerInAnotherAssembly_ProducesPrivateTypesAndRunsTheMembersOfOverloadedNames()
	{
		IPrivateMemberBarista barista;
		await using (PrivateMemberContainer.Root container = new())
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				barista = await scope.ResolveAsync<IPrivateMemberBarista>();
			}

			await That(barista.Pot.Started).IsTrue()
				.Because("the convention initializes the type the private factory returns, though its service does not implement it");
			await That(barista.Spoon.Activated && barista.Spoon.Disposed).IsTrue()
				.Because("the internal factory and hook run though public overloads of other types share their names");
			await That(barista.Heater.Disposed || barista.Pot.Disposed).IsFalse();
		}

		await That(barista.Heater.Disposed && barista.Pot.Disposed).IsTrue()
			.Because("the container disposes what the private factories produced, a type private to the module included");
		await That(barista.Rack.Disposed).IsFalse()
			.Because("the container never disposes an Instance");
	}

	[Test]
	public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_ProducesPrivateTypesAndRunsTheMembersOfOverloadedNames()
	{
		IFriendPrivateMemberBarista barista;
		await using (FriendPrivateMemberContainer.Root container = new())
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				barista = await scope.ResolveAsync<IFriendPrivateMemberBarista>();
			}

			await That(barista.Pot.Started).IsTrue()
				.Because("the convention initializes the type the private factory returns, as in a container in another assembly");
			await That(barista.Spoon.Activated && barista.Spoon.Disposed).IsTrue();
			await That(barista.Heater.Disposed || barista.Pot.Disposed).IsFalse();
		}

		await That(barista.Heater.Disposed && barista.Pot.Disposed).IsTrue()
			.Because("the internal wrapper returns the service in place of the type private to the module, whose disposal shows at run time");
		await That(barista.Rack.Disposed).IsFalse();
	}

	[Test]
	public async Task ContainerInTheModulesAssembly_ProducesPrivateTypesAndRunsTheMembersOfOverloadedNames()
	{
		IFriendPrivateMemberBarista barista;
		await using (FriendPrivateMemberLocalContainer.Root container = new())
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				barista = await scope.ResolveAsync<IFriendPrivateMemberBarista>();
			}

			await That(barista.Pot.Started).IsTrue();
			await That(barista.Spoon.Activated && barista.Spoon.Disposed).IsTrue();
			await That(barista.Heater.Disposed || barista.Pot.Disposed).IsFalse();
		}

		await That(barista.Heater.Disposed && barista.Pot.Disposed).IsTrue();
		await That(barista.Rack.Disposed).IsFalse();
	}

	[Test]
	public async Task InternalModule_ContainerInAnAssemblyGrantedInternalsVisibleTo_ProducesItsServicesThroughThePrivateMembers()
	{
		IFriendPrivateMemberState heater;
		IFriendPrivateMemberState shelf;
		await using (InternalModuleFriendContainer.Root container = new())
		{
			heater = await container.ResolveAsync<IFriendPrivateMemberState>();
			shelf = await container.ResolveAsync<IInternalModuleShelf>();

			await That(await container.ResolveAsync<IFriendPrivateMemberState>()).IsSameAs(heater)
				.Because("the private factory of the internal module produces one singleton");
		}

		await That(heater.Disposed).IsTrue()
			.Because("the container disposes what the private factory produced");
		await That(shelf.Disposed).IsFalse()
			.Because("the container never disposes an Instance");
	}

	[Test]
	public async Task InternalModule_ContainerInTheModulesAssembly_ProducesItsServicesThroughThePrivateMembers()
	{
		IFriendPrivateMemberState heater;
		IFriendPrivateMemberState shelf;
		await using (InternalModuleLocalContainer.Root container = new())
		{
			heater = await container.ResolveAsync<IFriendPrivateMemberState>();
			shelf = await container.ResolveAsync<IInternalModuleShelf>();

			await That(await container.ResolveAsync<IFriendPrivateMemberState>()).IsSameAs(heater);
		}

		await That(heater.Disposed).IsTrue();
		await That(shelf.Disposed).IsFalse();
	}

	[Container]
	[Import(typeof(InternalPrivateMemberModule))]
	internal static partial class InternalModuleFriendContainer;

	[Container]
	[Import(typeof(CrossAssemblyPrivateMemberModule))]
	public static partial class PrivateMemberContainer;

	[Container]
	[Import(typeof(SupportFriendPrivateMemberModule))]
	public static partial class FriendPrivateMemberContainer;
}
