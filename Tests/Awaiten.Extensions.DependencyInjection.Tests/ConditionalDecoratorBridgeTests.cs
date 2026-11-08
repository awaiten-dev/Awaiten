using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Awaiten.Extensions.DependencyInjection.Tests;

public sealed partial class ConditionalDecoratorBridgeTests
{
	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task PredicateTakingHostOptions_DecidesTheDecoratorTheHostResolves(bool enabled)
	{
		ServiceCollection services = new();
		services.Configure<AuditOptions>(options => options.Enabled = enabled);
		services.AddAwaitenContainer<AuditedContainer.Root>();
		using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

		ILedger ledger = provider.GetRequiredService<ILedger>();

		await That(ledger is AuditedLedger).IsEqualTo(enabled)
			.Because("the predicate reads the options the host binds, through the external resolver the bridge attaches");
		await That(provider.GetRequiredService<ILedger>()).IsSameAs(ledger);
	}

	public sealed class AuditOptions
	{
		public bool Enabled { get; set; }
	}

	public interface ILedger;

	public sealed class Ledger : ILedger;

	public sealed class AuditedLedger(ILedger inner) : ILedger
	{
		public ILedger Inner { get; } = inner;
	}

	[Container]
	[ImportService<IOptions<AuditOptions>>]
	[Singleton<Ledger, ILedger>]
	[Decorate<AuditedLedger, ILedger>(When = nameof(AuditEnabled))]
	public static partial class AuditedContainer
	{
		private static bool AuditEnabled(IOptions<AuditOptions> options) => options.Value.Enabled;
	}
}
