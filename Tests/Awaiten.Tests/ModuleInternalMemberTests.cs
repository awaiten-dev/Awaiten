using System.Collections.Generic;
using System.Threading;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of the internal <c>Factory</c> members and lifecycle hooks of a public module: a container in
///     another assembly reaches them through what the module generates, one in the module's assembly calls them directly.
/// </summary>
public sealed partial class ModuleInternalMemberTests
{
	public sealed class InternalPlugin
	{
		public List<string> Steps { get; } = new();

		public object? Store { get; set; }
	}

	internal sealed class LocalStore;

	[Module]
	[Singleton<LocalStore>]
	[Transient<InternalPlugin>(OnActivated = nameof(Activate), OnRelease = nameof(Release))]
	public static partial class LocalModule
	{
		internal static void Activate(InternalPlugin plugin, LocalStore store)
		{
			plugin.Steps.Add("activated");
			plugin.Store = store;
		}

		internal static void Release(InternalPlugin plugin) => plugin.Steps.Add("released");
	}

	[Container]
	[Import(typeof(LocalModule))]
	public static partial class LocalContainer;

	[Container]
	[Import(typeof(InternalMemberModule))]
	public static partial class ImportingContainer;

	public sealed class ModuleInAnotherAssembly
	{
		[Test]
		public async Task TheInternalHooks_RunOncePerInstanceThroughTheirWrappers()
		{
			InternalHookedPlugin first;
			InternalHookedPlugin second;
			using ImportingContainer.Root container = new();
			using (IAwaitenScope scope = container.CreateScope())
			{
				first = scope.Resolve<InternalHookedPlugin>();
				second = scope.Resolve<InternalHookedPlugin>();

				await That(first.Steps).IsEqualTo(["activated",])
					.Because("the container runs the internal activation hook through the module's wrapper once per instance");
				await That(second.Steps).IsEqualTo(["activated",]);
			}

			await That(first.Steps).IsEqualTo(["activated", "released",])
				.Because("disposing the scope runs the internal release hook through its wrapper on each instance");
			await That(second.Steps).IsEqualTo(["activated", "released",]);
		}

		[Test]
		public async Task TheInternalHook_AndTheInternalFactory_ReceiveTheOneInternalSingleton()
		{
			using ImportingContainer.Root container = new();
			using IAwaitenScope scope = container.CreateScope();

			object? store = scope.Resolve<InternalHookedPlugin>().Store;

			await That(store).IsNotNull();
			await That(container.Resolve<IInternalStoreGrinder>().Store).IsSameAs(store)
				.Because("the hook wrapper and the factory's handle both take the internal singleton through its handle, which the container builds once");
		}

		[Test]
		public async Task TheInternalAsyncHook_IsAwaitedThroughItsWrapperWithTheResolveToken()
		{
			using CancellationTokenSource cancellation = new();
			await using ImportingContainer.Root container = new();

			IInternalHookedRouter router = await container.ResolveAsync<IInternalHookedRouter>(cancellation.Token);

			await That(router.Token).IsEqualTo(cancellation.Token)
				.Because("the wrapper returns the hook's Task and forwards the resolve-time token");
			await That(container.GetResolvability<IInternalHookedRouter>().Status).IsEqualTo(AwaitenResolvabilityStatus.AsyncOnly)
				.Because("the wrapped asynchronous hook makes the router async-initialized");
		}
	}

	public sealed class ModuleInTheSameAssembly
	{
		[Test]
		public async Task TheInternalHooks_RunOncePerInstance()
		{
			InternalPlugin plugin;
			using LocalContainer.Root container = new();
			using (IAwaitenScope scope = container.CreateScope())
			{
				plugin = scope.Resolve<InternalPlugin>();

				await That(plugin.Steps).IsEqualTo(["activated",]);
				await That(plugin.Store).IsNotNull()
					.Because("a container in the module's assembly calls the internal hook directly and passes it the internal service");
			}

			await That(plugin.Steps).IsEqualTo(["activated", "released",]);
		}
	}
}
