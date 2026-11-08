using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Decorators and composites of internal services, declared once and compiled into Awaiten.Tests.Support, which
///     grants no <c>InternalsVisibleTo</c>, and into Awaiten.Tests.ContainerSupport, which grants it to this assembly.
///     A container in the modules' own assembly, one granted <c>InternalsVisibleTo</c> and one without it build the
///     same graph.
/// </summary>
public class ModuleHiddenDecoratorShapeTests
{
	public enum Kind
	{
		SameAssembly,
		Friend,
		External,
	}

	private static IAwaitenRoot Ledger(Kind kind) => kind switch
	{
		Kind.SameAssembly => new LocalLedgerShapeContainer.Root(),
		Kind.Friend => new FriendLedgerShapeContainer.Root(),
		_ => new ExternalLedgerShapeContainer.Root(),
	};

	private static IAwaitenRoot Channel(Kind kind) => kind switch
	{
		Kind.SameAssembly => new LocalChannelShapeContainer.Root(),
		Kind.Friend => new FriendChannelShapeContainer.Root(),
		_ => new ExternalChannelShapeContainer.Root(),
	};

	private static IAwaitenRoot Till(Kind kind, bool reversed) => (kind, reversed) switch
	{
		(Kind.SameAssembly, false) => new LocalTillShapeContainer.Root(),
		(Kind.SameAssembly, true) => new LocalReversedTillShapeContainer.Root(),
		(Kind.Friend, false) => new FriendTillShapeContainer.Root(),
		(Kind.Friend, true) => new FriendReversedTillShapeContainer.Root(),
		(_, false) => new ExternalTillShapeContainer.Root(),
		_ => new ExternalReversedTillShapeContainer.Root(),
	};

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task DecoratorWithABaseTypeInnerParameter_DecoratesEveryKeyAndIsInitialized(Kind kind)
	{
		await using IAwaitenRoot container = Ledger(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();

		IShapeLedgerReader reader = await scope.ResolveAsync<IShapeLedgerReader>();

		await That(reader.Ledger.Describe()).IsEqualTo("base(ledger)")
			.Because("the decorator takes the internal service as its public base, and the convention initializes it");
		await That(reader.AuditLedger.Describe()).IsEqualTo("audit(base(ledger))")
			.Because("each key gets its own chain, in declaration order");
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task PublicServiceOfTheImplementation_IsDecoratedByItsOwnChainAroundTheSameInstance(Kind kind)
	{
		await using IAwaitenRoot container = Ledger(kind);
		await using IAwaitenScope scope = await container.CreateScopeAsync();

		IShapeLedgerReader reader = await scope.ResolveAsync<IShapeLedgerReader>();

		await That(reader.View.Describe()).IsEqualTo("view(ledger)")
			.Because("the public service is decorated by its own chain, never by the chain of the internal service");
		await That(Innermost(reader.View)).IsSameAs(Innermost(reader.Ledger))
			.And.IsSameAs(Innermost(reader.AuditLedger))
			.Because("every chain wraps the one instance of the implementation the scope holds");
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task ScopedChains_AreBuiltPerScopeAndDisposedLinkByLink(Kind kind)
	{
		await using IAwaitenRoot container = Ledger(kind);
		IShapeLedgerReader first;
		await using (IAwaitenScope scope = await container.CreateScopeAsync())
		{
			first = await scope.ResolveAsync<IShapeLedgerReader>();
			await using IAwaitenScope other = await container.CreateScopeAsync();
			IShapeLedgerReader second = await other.ResolveAsync<IShapeLedgerReader>();

			await That(Innermost(second.Ledger)).IsNotSameAs(Innermost(first.Ledger))
				.Because("a scoped implementation is built once per scope");
			await That(AllLinks(first).Select(link => link.Disposed)).All().AreEqualTo(false);
		}

		await That(AllLinks(first).Select(link => link.Disposed)).All().AreEqualTo(true)
			.Because("the scope disposes every link of every chain as an instance of its own");
	}

	[Test]
	[Arguments(Kind.SameAssembly)]
	[Arguments(Kind.Friend)]
	[Arguments(Kind.External)]
	public async Task CompositeOfADecoratedInternalService_FrontsTheDecoratedMembers(Kind kind)
	{
		await using IAwaitenRoot container = Channel(kind);

		IShapeChannelNotifier notifier = container.Resolve<IShapeChannelNotifier>();

		await That(notifier.Channel.Name).IsEqualTo("traced email, traced sms")
			.Because("the composite fronts the members, each decorated through the service's public base");
		await That(notifier.Members.Select(member => member.Name)).IsEqualTo(["traced email", "traced sms",])
			.Because("the collection of the service holds the decorated members without the composite");
	}

	[Test]
	[Arguments(Kind.SameAssembly, false, "second(first(till))")]
	[Arguments(Kind.Friend, false, "second(first(till))")]
	[Arguments(Kind.External, false, "second(first(till))")]
	[Arguments(Kind.SameAssembly, true, "first(second(till))")]
	[Arguments(Kind.Friend, true, "first(second(till))")]
	[Arguments(Kind.External, true, "first(second(till))")]
	public async Task DecoratorsOfTwoModules_ChainInImportOrder(Kind kind, bool reversed, string expected)
	{
		await using IAwaitenRoot container = Till(kind, reversed);

		await That(container.Resolve<IShapeTillReader>().Till).IsEqualTo(expected)
			.Because("the decorators of the modules chain in the order the container imports the modules, the last outermost");
	}

	private static IEnumerable<IShapeLedger> AllLinks(IShapeLedgerReader reader)
		=> reader.Ledger.Links().Concat(reader.AuditLedger.Links()).Concat(reader.View.Links());

	private static IShapeLedger Innermost(IShapeLedger ledger)
	{
		IReadOnlyList<IShapeLedger> links = ledger.Links();
		return links[links.Count - 1];
	}
}

[Container]
[Import(typeof(Support.HiddenLedgerShapeModule))]
public static partial class ExternalLedgerShapeContainer;

[Container]
[Import(typeof(ContainerSupport.HiddenLedgerShapeModule))]
public static partial class FriendLedgerShapeContainer;

[Container]
[Import(typeof(Support.HiddenChannelShapeModule))]
public static partial class ExternalChannelShapeContainer;

[Container]
[Import(typeof(ContainerSupport.HiddenChannelShapeModule))]
public static partial class FriendChannelShapeContainer;

[Container]
[Import(typeof(Support.HiddenTillShapeModule))]
[Import(typeof(Support.HiddenTillDecoratorShapeModule))]
public static partial class ExternalTillShapeContainer;

[Container]
[Import(typeof(Support.HiddenTillDecoratorShapeModule))]
[Import(typeof(Support.HiddenTillShapeModule))]
public static partial class ExternalReversedTillShapeContainer;

[Container]
[Import(typeof(ContainerSupport.HiddenTillShapeModule))]
[Import(typeof(ContainerSupport.HiddenTillDecoratorShapeModule))]
public static partial class FriendTillShapeContainer;

[Container]
[Import(typeof(ContainerSupport.HiddenTillDecoratorShapeModule))]
[Import(typeof(ContainerSupport.HiddenTillShapeModule))]
public static partial class FriendReversedTillShapeContainer;
