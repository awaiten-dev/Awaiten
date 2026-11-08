namespace Awaiten.ExampleTests;

/// <summary>
///     The example of "Apply a decorator conditionally" on the "Decorators" page: the payment terminal is wrapped in
///     the logging decorator only in a run that switches the audit on before the root is created, and the audit
///     settings a host provides decide the same through a predicate that takes them.
/// </summary>
[NotInParallel(nameof(ConditionalDecoratorsExample))]
public partial class ConditionalDecoratorsExample
{
	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task Switch_DecidesWhetherTheTerminalIsLogged(bool audit)
	{
		ShopSwitches.AuditPayments = audit;
		using CoffeeShop.Root shop = new();

		IPaymentTerminal terminal = shop.Resolve<IPaymentTerminal>();

		await That(terminal is LoggingTerminal).IsEqualTo(audit);
	}

	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task Settings_DecideWhetherTheTerminalIsLogged(bool audit)
	{
		using SettingsCoffeeShop.Root shop = new(new SettingsResolver(new AuditSettings { Enabled = audit, }));

		IPaymentTerminal terminal = shop.Resolve<IPaymentTerminal>();

		await That(terminal is LoggingTerminal).IsEqualTo(audit);
	}

	public static class ShopSwitches
	{
		public static bool AuditPayments { get; set; }
	}

	public sealed class AuditSettings
	{
		public bool Enabled { get; init; }
	}

	public interface IPaymentTerminal;

	public sealed class CardTerminal : IPaymentTerminal;

	public sealed class LoggingTerminal(IPaymentTerminal inner) : IPaymentTerminal
	{
		public IPaymentTerminal Inner { get; } = inner;
	}

	[Container]
	[Singleton<CardTerminal, IPaymentTerminal>]
	[Decorate<LoggingTerminal, IPaymentTerminal>(When = nameof(AuditPayments))]
	public static partial class CoffeeShop
	{
		private static bool AuditPayments => ShopSwitches.AuditPayments;
	}

	[Container]
	[ImportService<AuditSettings>]
	[Singleton<CardTerminal, IPaymentTerminal>]
	[Decorate<LoggingTerminal, IPaymentTerminal>(When = nameof(AuditEnabled))]
	public static partial class SettingsCoffeeShop
	{
		private static bool AuditEnabled(AuditSettings settings) => settings.Enabled;
	}

	private sealed class SettingsResolver(AuditSettings settings) : IExternalResolver
	{
		public bool TryResolve(Type serviceType, object? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? instance)
		{
			instance = serviceType == typeof(AuditSettings) ? settings : null;
			return instance is not null;
		}
	}
}
