using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Factories with runtime arguments of an internal service, declared once and compiled into Awaiten.Tests.Support,
///     which grants no <c>InternalsVisibleTo</c>, and into Awaiten.Tests.ContainerSupport, which grants it to this
///     assembly. A container in the module's own assembly, one granted <c>InternalsVisibleTo</c> and one without it
///     build the same graph.
/// </summary>
public class ModuleHiddenRuntimeArgumentTests
{
	public enum Kind
	{
		SameAssembly,
		Friend,
		External,
	}

	private static IAwaitenRoot Container(Kind kind) => kind switch
	{
		Kind.SameAssembly => new LocalArgumentShapeContainer.Root(),
		Kind.Friend => new FriendArgumentShapeContainer.Root(),
		_ => new ExternalArgumentShapeContainer.Root(),
	};

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task Factory_PassesTheArgumentsThroughAndBuildsANewInstancePerCall(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		IArgumentBarista barista = await scope.ResolveAsync<IArgumentBarista>();
		ArgumentOrder order = new("latte");

		IArgumentBrew first = barista.Brew(order, 2);
		IArgumentBrew second = barista.Brew(new ArgumentOrder("espresso"), 1);

		await That(first.Order).IsSameAs(order)
			.Because("the factory passes the runtime argument on to the [Arg] parameter behind the handle");
		await That(first.Shots).IsEqualTo(2);
		await That(first.Kind).IsEqualTo("regular");
		await That(second.Order.Drink).IsEqualTo("espresso");
		await That(second.Shots).IsEqualTo(1);
		await That(second).IsNotSameAs(first)
			.Because("a transient service built from runtime arguments is built anew on every call");
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task KeyedFactory_BuildsTheRegistrationOfItsKey(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		IArgumentBarista barista = await scope.ResolveAsync<IArgumentBarista>();

		IArgumentBrew decaf = barista.Decaf(new ArgumentOrder("flat white"), 3);

		await That(decaf.Kind).IsEqualTo("decaf")
			.Because("[FromKey] selects the keyed registration behind the handle");
		await That(decaf.Order.Drink).IsEqualTo("flat white");
		await That(decaf.Shots).IsEqualTo(3);
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task FactoryOverAFactoryMember_PassesTheArgumentsToTheMember(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		IArgumentBarista barista = await scope.ResolveAsync<IArgumentBarista>();

		IArgumentBrew brew = barista.Member(new ArgumentOrder("mocha"), 4);

		await That(brew.Kind).IsEqualTo("member")
			.Because("the wrapper of the private factory member forwards the runtime arguments to it");
		await That(brew.Order.Drink).IsEqualTo("mocha");
		await That(brew.Shots).IsEqualTo(4);
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task Brews_AreDisposedWithTheScopeThatBuiltThem(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		IArgumentBrew brew;
		IArgumentBrew decaf;
		IArgumentBrew member;
		await using (IAwaitenScope scope = await container.CreateScopeAsync())
		{
			IArgumentBarista barista = await scope.ResolveAsync<IArgumentBarista>();
			brew = barista.Brew(new ArgumentOrder("latte"), 1);
			decaf = barista.Decaf(new ArgumentOrder("latte"), 1);
			member = barista.Member(new ArgumentOrder("latte"), 1);

			await That(brew.Disposed).IsFalse();
		}

		await That(brew.Disposed).IsTrue()
			.Because("the scope tracks each disposable brew a factory builds and disposes it with the scope");
		await That(decaf.Disposed).IsTrue();
		await That(member.Disposed).IsTrue();
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task OwnedFactory_HandsTheDisposalToTheCaller(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		IArgumentBarista barista = await scope.ResolveAsync<IArgumentBarista>();

		(IArgumentBrew brew, IDisposable owner) = barista.Owned(new ArgumentOrder("cortado"), 2);

		await That(brew.Order.Drink).IsEqualTo("cortado");
		await That(brew.Shots).IsEqualTo(2);
		await That(brew.Disposed).IsFalse();
		owner.Dispose();
		await That(brew.Disposed).IsTrue()
			.Because("disposing the owned brew disposes the instance behind the handle, before its scope ends");
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task TransientTillOnTheRoot_BuildsBrewsTheRootDisposes(Kind kind)
	{
		IArgumentBrew brew;
		await using (IAwaitenRoot container = Container(kind))
		{
			IArgumentTill till = await container.ResolveAsync<IArgumentTill>();
			brew = till.Ring(new ArgumentOrder("ristretto"), 1);

			await That(brew.Order.Drink).IsEqualTo("ristretto")
				.Because("a factory injected into a transient on the root builds the brew there, as in the module's own assembly");
			await That(brew.Disposed).IsFalse();
		}

		await That(brew.Disposed).IsTrue();
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task ImplementationUnderTwoPublicServices_IsBuiltFromTheArgumentsOfEitherFactory(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		ArgumentOrder order = new("americano");

		Func<ArgumentOrder, IArgumentReceipt> receipts = await scope.ResolveAsync<Func<ArgumentOrder, IArgumentReceipt>>();
		Func<ArgumentOrder, IArgumentTicket> tickets = await scope.ResolveAsync<Func<ArgumentOrder, IArgumentTicket>>();
		IArgumentReceipt receipt = receipts(order);
		IArgumentTicket ticket = tickets(order);

		await That(receipt.Order).IsSameAs(order)
			.Because("each public service forwards the runtime argument to the instance handle of the implementation");
		await That(ticket.Order).IsSameAs(order);
		await That(receipts(order)).IsNotSameAs(receipt)
			.Because("every call builds a new instance");
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task AsyncFactoryOfAnAsyncInitializedImplementation_AwaitsTheInitialization(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		ArgumentOrder order = new("cappuccino");

		ArgumentReceiptDesk desk = await scope.ResolveAsync<ArgumentReceiptDesk>();
		IArgumentStartedReceipt receipt = await desk.Issue(order);

		await That(receipt.Order).IsSameAs(order);
		await That(receipt.Started).IsTrue()
			.Because("the async factory awaits the initialization of the instance behind the handle, as in the module's own assembly");
		await That(await desk.Issue(order)).IsNotSameAs(receipt);
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task Barista_IsSharedWithinAScope(Kind kind)
	{
		await using IAwaitenRoot container = Container(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();
		await using IAwaitenScope other = await container.CreateScopeAsync();

		IArgumentBarista first = await scope.ResolveAsync<IArgumentBarista>();
		IArgumentBarista again = await scope.ResolveAsync<IArgumentBarista>();
		IArgumentBarista elsewhere = await other.ResolveAsync<IArgumentBarista>();

		await That(again).IsSameAs(first)
			.Because("the barista that holds the factories keeps its scoped lifetime");
		await That(elsewhere).IsNotSameAs(first);
	}
}

[Container]
[Import(typeof(Support.HiddenArgumentModule))]
[Transient<ArgumentReceiptDesk>]
public static partial class ExternalArgumentShapeContainer;

[Container]
[Import(typeof(ContainerSupport.FriendHiddenArgumentModule))]
[Transient<ArgumentReceiptDesk>]
public static partial class FriendArgumentShapeContainer;
