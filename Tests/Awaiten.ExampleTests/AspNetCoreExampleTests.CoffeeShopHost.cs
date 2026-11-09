#if NET
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using Awaiten.Extensions.DependencyInjection;
using CoffeeShop;
using CoffeeShop.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Awaiten.ExampleTests;

public partial class AspNetCoreExampleTests
{
	/// <summary>
	///     With <c>AsyncSafety.SyncAfterInit</c> and <c>WithInitialization()</c>, the host warms the async-initialized
	///     <see cref="PriceList" /> before it serves, so ASP.NET Core can bind it to a handler parameter synchronously.
	///     Its prices come from the host's <see cref="IConfiguration" />, which the container imports.
	/// </summary>
	[Test]
	public async Task AspNetCore_BindsWarmedAsyncServiceSynchronously()
	{
		WebApplicationBuilder builder = CreateCoffeeShopBuilder();
		builder.Host.UseServiceProviderFactory(new AwaitenServiceProviderFactory<CoffeeShopHost.Root>(c => c.WithInitialization()));
		await using WebApplication app = builder.Build();

		app.MapGet("/price/{drink}", (string drink, PriceList prices) => prices.IsWarm ? Format(prices.PriceOf(drink)) : "cold");
		await app.StartAsync();

		using HttpClient client = ClientFor(app);
		string body = await client.GetStringAsync("/price/Latte");

		await That(body).IsEqualTo("3.80");
	}

	/// <summary>
	///     A keyed Awaiten registration is projected under its key, so <c>[FromKeyedServices]</c> reaches it.
	/// </summary>
	[Test]
	public async Task AspNetCore_ReachesKeyedRegistrationsThroughFromKeyedServices()
	{
		WebApplicationBuilder builder = CreateCoffeeShopBuilder();
		builder.Host.UseServiceProviderFactory(new AwaitenServiceProviderFactory<CoffeeShopHost.Root>(c => c.WithInitialization()));
		await using WebApplication app = builder.Build();

		app.MapGet("/receipt", (
			[FromKeyedServices("text")] IReceiptFormatter text,
			[FromKeyedServices("json")] IReceiptFormatter json) => $"{text.Format(new Receipt(3.80m))} | {json.Format(new Receipt(3.80m))}");
		await app.StartAsync();

		using HttpClient client = ClientFor(app);
		string body = await client.GetStringAsync("/receipt");

		await That(body).IsEqualTo("Paid 3.80 | {\"amount\":3.80}");
	}

	/// <summary>
	///     The container imports the domain's <see cref="PaymentModule" />, so a request charges through the domain's
	///     internal payment gateway, which neither the container nor the host can name.
	/// </summary>
	[Test]
	public async Task AspNetCore_ChargesThroughTheDomainsModule()
	{
		WebApplicationBuilder builder = CreateCoffeeShopBuilder();
		builder.Host.UseServiceProviderFactory(new AwaitenServiceProviderFactory<CoffeeShopHost.Root>(c => c.WithInitialization()));
		await using WebApplication app = builder.Build();

		app.MapGet("/serve/{drink}", (string drink, Barista barista, PriceList prices) => Format(barista.Serve(prices.PriceOf(drink)).Amount));
		await app.StartAsync();

		using HttpClient client = ClientFor(app);
		string body = await client.GetStringAsync("/serve/Espresso");

		await That(body).IsEqualTo("2.40");
	}

	/// <summary>
	///     <c>VerifyAwaitenContainers</c> is the run-time counterpart of the compile-time missing-dependency check across
	///     the host boundary: it names every <c>[ImportService&lt;T&gt;]</c> the provider does not register.
	/// </summary>
	[Test]
	public async Task Verify_NamesAMissingImportedService()
	{
		ServiceCollection services = new();
		services.AddAwaitenContainer<CoffeeShopHost.Root>();
		await using ServiceProvider provider = services.BuildServiceProvider();

		await That(() => provider.VerifyAwaitenContainers())
			.Throws<InvalidOperationException>()
			.WithMessage("*IConfiguration*").AsWildcard();
	}

	private static WebApplicationBuilder CreateCoffeeShopBuilder()
	{
		WebApplicationBuilder builder = CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Menu:Espresso"] = "2.40",
			["Menu:Latte"] = "3.80",
		});
		return builder;
	}

	private static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

	/// <summary>
	///     A coffee shop hosted in ASP.NET Core: an async-initialized price list read from the host's configuration,
	///     keyed receipt formatters, and the barista with the domain's payment gateway from its module.
	/// </summary>
	[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
	[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
	[ImportService<IConfiguration>]
	[Import(typeof(PaymentModule))]
	[Singleton<PriceList>]
	[Transient<Barista>]
	[Singleton<TextReceiptFormatter, IReceiptFormatter>(Key = "text")]
	[Singleton<JsonReceiptFormatter, IReceiptFormatter>(Key = "json")]
	public static partial class CoffeeShopHost;

	public interface IWarmUp
	{
		Task WarmUpAsync(CancellationToken cancellationToken);
	}

	/// <summary>Async-initialized: it reads its prices only when warmed, as a real one would load them from a store.</summary>
	public sealed class PriceList(IConfiguration configuration) : IWarmUp
	{
		private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);

		public bool IsWarm { get; private set; }

		public async Task WarmUpAsync(CancellationToken cancellationToken)
		{
			await Task.Yield();
			foreach (IConfigurationSection section in configuration.GetSection("Menu").GetChildren())
			{
				_prices[section.Key] = decimal.Parse(section.Value!, CultureInfo.InvariantCulture);
			}

			IsWarm = true;
		}

		public decimal PriceOf(string drink) => _prices[drink];
	}

	public interface IReceiptFormatter
	{
		string Format(Receipt receipt);
	}

	public sealed class TextReceiptFormatter : IReceiptFormatter
	{
		public string Format(Receipt receipt) => $"Paid {AspNetCoreExampleTests.Format(receipt.Amount)}";
	}

	public sealed class JsonReceiptFormatter : IReceiptFormatter
	{
		public string Format(Receipt receipt) => $"{{\"amount\":{AspNetCoreExampleTests.Format(receipt.Amount)}}}";
	}
}
#endif
