using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Awaiten.Extensions.DependencyInjection.Tests;

public sealed partial class TaskOfServiceBridgeTests
{
	public interface IStore
	{
		bool Initialized { get; }
	}

	public sealed class Store : IStore
	{
		public bool Initialized { get; set; }
	}

	public sealed class Consumer(Task<IStore> task, Func<Task<IStore>> factory)
	{
		public Task<IStore> Task => task;

		public Func<Task<IStore>> Factory => factory;
	}

	[Container]
	[Scoped<Store, IStore>(OnActivated = nameof(Initialize))]
	[Transient<Consumer>]
	public static partial class Shop
	{
		private static async Task Initialize(Store store, CancellationToken cancellationToken)
		{
			await Task.Yield();
			store.Initialized = true;
		}
	}

	[Test]
	public async Task AddAwaitenContainer_ResolvesAConsumerOfTheServiceTask()
	{
		ServiceCollection services = new();
		services.AddAwaitenContainer<Shop.Root>();
		await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
		await using AsyncServiceScope scope = provider.CreateAsyncScope();

		Consumer consumer = scope.ServiceProvider.GetRequiredService<Consumer>();
		IStore store = await consumer.Task;

		await That(store.Initialized).IsTrue()
			.Because("the consumer's task adapts the initialized store to the task of its service");
		await That(await consumer.Factory()).IsSameAs(store);
		await That(await scope.ServiceProvider.GetRequiredService<Task<IStore>>()).IsSameAs(store)
			.Because("the host's projection of the service task resolves the same scoped instance");
	}

	public sealed class Bean;

	[Container]
	[Singleton<IReadOnlyList<Bean>>(Factory = nameof(CreateBeansAsync))]
	public static partial class Pantry
	{
		private static async Task<IReadOnlyList<Bean>> CreateBeansAsync()
		{
			await Task.Yield();
			return [new Bean()];
		}
	}

	[Test]
	public async Task AwaitenServiceProvider_ServesTheTaskOfAnAsyncServiceRegisteredAsACollection()
	{
		await using Pantry.Root pantry = new();
		using AwaitenServiceProvider provider = new(pantry);

		IReadOnlyList<Bean> beans = await (Task<IReadOnlyList<Bean>>)provider.GetService(typeof(Task<IReadOnlyList<Bean>>))!;

		await That(beans).IsSameAs(await pantry.ResolveAsync<IReadOnlyList<Bean>>())
			.Because("the task is over the registered service, whose type happens to be a collection");
		await That(provider.IsService(typeof(Task<IReadOnlyList<Bean>>))).IsTrue();
	}
}
