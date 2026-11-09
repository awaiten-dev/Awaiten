using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

public partial class ConditionalDecoratorTests
{
	[NotInParallel(nameof(ConditionalDecoratorTests))]
	public class InternalPredicate
	{
		[Test]
		[Arguments(false, true, false)]
		[Arguments(false, false, true)]
		[Arguments(true, true, false)]
		[Arguments(true, false, true)]
		public async Task ModuleOfAnotherAssembly_EvaluatesEachPredicateOncePerRoot(bool friend, bool traced, bool audited)
		{
			InternalPredicateSwitch.Reset(traced, audited);
			string expected = Chain(traced, audited);

			using (IAwaitenRoot root = friend ? new FriendInternalPredicateShop.Root() : new InternalPredicateShop.Root())
			{
				await That(root.Resolve<IPredicateGateway>().Describe()).IsEqualTo(expected);
				InternalPredicateSwitch.Traced = !traced;
				InternalPredicateSwitch.Audited = !audited;
				await That(root.Resolve<IPredicateGateway>().Describe()).IsEqualTo(expected)
					.Because("the property predicate is read when the root is created and the method predicate on first use, so the root keeps their results");
				using IAwaitenScope scope = root.CreateScope();
				await That(scope.Resolve<IPredicateGateway>().Describe()).IsEqualTo(expected);
			}

			await That(InternalPredicateSwitch.TracedEvaluations).IsEqualTo(1);
			await That(InternalPredicateSwitch.AuditedEvaluations).IsEqualTo(1)
				.Because("each predicate decides once per root, whether the container calls it directly or through the module's wrapper");

			using IAwaitenRoot next = friend ? new FriendInternalPredicateShop.Root() : new InternalPredicateShop.Root();
			await That(next.Resolve<IPredicateGateway>().Describe()).IsEqualTo(Chain(!traced, !audited))
				.Because("a new root evaluates the predicates again");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task BaseOfAnotherAssembly_ExtendingContainerSwitchesTheInheritedDecoratorThroughTheService(bool enabled)
		{
			using IAwaitenRoot root = enabled ? new InternalPredicateBaseShop.Root() : new UnmonitoredPredicateShop.Root();

			await That(root.Resolve<IPredicateTerminal>().Describe()).IsEqualTo(enabled ? "monitored(terminal)" : "terminal")
				.Because("the extending container calls the base's internal predicate through its wrapper, with the service it registers in place of the base's");
		}

		private static string Chain(bool traced, bool audited)
		{
			string chain = traced ? "traced(gateway)" : "gateway";
			return audited ? "audited(" + chain + ")" : chain;
		}
	}

	[Container]
	[Import(typeof(InternalPredicateModule))]
	public static partial class InternalPredicateShop;

	[Container]
	[Import(typeof(FriendInternalPredicateModule))]
	public static partial class FriendInternalPredicateShop;

	[Container(Extends = typeof(InternalPredicateBaseContainer))]
	public static partial class InternalPredicateBaseShop;

	[Container(Extends = typeof(InternalPredicateBaseContainer))]
	[Singleton<PredicateMonitoring>(Instance = nameof(Off))]
	public static partial class UnmonitoredPredicateShop
	{
		private static PredicateMonitoring Off { get; } = new() { Enabled = false, };
	}
}
