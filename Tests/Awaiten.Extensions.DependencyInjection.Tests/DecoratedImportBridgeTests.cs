using Microsoft.Extensions.DependencyInjection;

namespace Awaiten.Extensions.DependencyInjection.Tests;

/// <summary>
///     A decorator over an imported service under the MS.DI bridge: the Awaiten graph receives the decorator over the
///     host's instance, while the host's own registration of the service stays in place.
/// </summary>
public sealed partial class DecoratedImportBridgeTests
{
	public interface IGrinder;

	public sealed class HostGrinder : IGrinder, IDisposable
	{
		public bool IsDisposed { get; private set; }

		public void Dispose() => IsDisposed = true;
	}

	public sealed class LoggingGrinder(IGrinder inner) : IGrinder, IDisposable
	{
		public IGrinder Inner { get; } = inner;

		public bool IsDisposed { get; private set; }

		public void Dispose() => IsDisposed = true;
	}

	public sealed class Barista(IGrinder grinder)
	{
		public IGrinder Grinder { get; } = grinder;
	}

	[Container]
	[ImportService<IGrinder>]
	[Scoped<Barista>]
	[Decorate<LoggingGrinder, IGrinder>]
	public static partial class Shop;

	[Test]
	public async Task ProjectedConsumer_ReceivesTheDecoratorOverTheHostInstance()
	{
		ServiceCollection services = new();
		services.AddSingleton<IGrinder, HostGrinder>();
		services.AddAwaitenContainer<Shop.Root>();
		using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
		using IServiceScope scope = provider.CreateScope();

		Barista barista = scope.ServiceProvider.GetRequiredService<Barista>();

		await That(barista.Grinder).Is<LoggingGrinder>();
		await That(((LoggingGrinder)barista.Grinder).Inner).IsSameAs(provider.GetRequiredService<IGrinder>())
			.Because("the host keeps its own registration, which the decorator wraps instead of replacing it");
	}

	[Test]
	public async Task DisposingTheScope_DisposesTheDecorator_ButNotTheHostInstance()
	{
		ServiceCollection services = new();
		services.AddSingleton<IGrinder, HostGrinder>();
		services.AddAwaitenContainer<Shop.Root>();
		using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
		IServiceScope scope = provider.CreateScope();
		LoggingGrinder grinder = (LoggingGrinder)scope.ServiceProvider.GetRequiredService<Barista>().Grinder;

		scope.Dispose();

		await That(grinder.IsDisposed).IsTrue()
			.Because("the Awaiten scope aligned to the MS.DI scope built the decorator");
		await That(((HostGrinder)grinder.Inner).IsDisposed).IsFalse()
			.Because("the host owns its instance and disposes it with its provider");
	}

	[Test]
	public async Task ProviderFactory_ResolvesTheDecoratorFromTheContainer_AndTheHostInstanceFromTheHost()
	{
		ServiceCollection services = new();
		services.AddSingleton<IGrinder, HostGrinder>();
		AwaitenServiceProviderFactory<Shop.Root> factory = new();
		Shop.Root root = factory.CreateBuilder(services);
		using ServiceProvider provider = (ServiceProvider)factory.CreateServiceProvider(root);
		using IAwaitenScope scope = root.CreateScope();

		IGrinder grinder = scope.Resolve<IGrinder>();

		await That(grinder).Is<LoggingGrinder>()
			.Because("a direct resolve from the container reaches the decorator");
		await That(((LoggingGrinder)grinder).Inner).IsSameAs(provider.GetRequiredService<IGrinder>())
			.Because("the host's registration of the import is not replaced by the container");
	}
}
