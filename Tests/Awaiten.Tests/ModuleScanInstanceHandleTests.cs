using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A module scan match exposed under several interfaces shares one instance per lifetime across them, in a container
///     of another assembly through the module's generated instance handle and in a container of the module's own
///     assembly through its own construction.
/// </summary>
public partial class ModuleScanInstanceHandleTests
{
	public partial class AcrossAssemblies
	{
		[Test]
		public async Task ASingletonMatch_IsOneInstanceUnderEveryExposure()
		{
			await using MultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			IMultiRoaster roaster = container.Resolve<IMultiRoaster>();

			await That((object)container.Resolve<IMultiPlugin>()).IsSameAs(roaster)
				.Because("both exposures are served from the one instance the handle holds");
			await That(container.Resolve<IEnumerable<IMultiPlugin>>().ToArray()).HasSingle().Which.IsSameAs(roaster);
			await That(container.Resolve<IEnumerable<IMultiRoaster>>().ToArray()).HasSingle().Which.IsSameAs(roaster);
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				await That((object)scope.Resolve<IMultiPlugin>()).IsSameAs(roaster);
			}
		}

		[Test]
		public async Task ASingletonMatch_RunsItsHookAndInitializerOnce()
		{
			await using MultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			IMultiRoaster roaster = container.Resolve<IMultiRoaster>();
			_ = container.Resolve<IMultiPlugin>();

			await That(roaster.Activations).IsEqualTo(1)
				.Because("the hook runs once on the shared instance, not once per exposure");
			await That(roaster.Warmings).IsEqualTo(1)
				.Because("the module's convention runs once on the shared instance through its generated wrapper");
		}

		[Test]
		public async Task ASingletonMatch_IsReleasedAndDisposedOnce()
		{
			IMultiRoaster roaster;
			await using (MultiExposureContainer.Root container = new())
			{
				await container.InitializeAsync();
				roaster = container.Resolve<IMultiRoaster>();
				_ = container.Resolve<IMultiPlugin>();
			}

			await That(roaster.Disposals).IsEqualTo(1)
				.Because("the container owns the handle, which forwards the disposal once, and the aliases own nothing");
			await That(roaster.Releases).IsEqualTo(1);
		}

		[Test]
		public async Task AScopedMatch_IsOneInstancePerScopeUnderEveryExposure()
		{
			await using MultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			IMultiScopedRoaster first;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = scope.Resolve<IMultiScopedRoaster>();
				await That((object)scope.Resolve<IMultiScopedPlugin>()).IsSameAs(first);
			}

			await That(first.Disposals).IsEqualTo(1)
				.Because("the scope disposes the shared instance once");
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				await That(scope.Resolve<IMultiScopedRoaster>()).IsNotSameAs(first);
			}
		}

		[Test]
		public async Task ATransientMatch_IsANewInstancePerResolve()
		{
			await using MultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			await using IAwaitenScope scope = await container.CreateScopeAsync();

			await That(scope.Resolve<IMultiTransientRoaster>()).IsNotSameAs(scope.Resolve<IMultiTransientRoaster>());
			await That((object)scope.Resolve<IMultiTransientPlugin>()).IsNotSameAs(scope.Resolve<IMultiTransientRoaster>());
		}

		[Test]
		public async Task AScopedMatch_RunsItsHooksOncePerScope()
		{
			await using MultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			IMultiScopedRoaster roaster;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				roaster = scope.Resolve<IMultiScopedRoaster>();
				_ = scope.Resolve<IMultiScopedPlugin>();
				await That(roaster.Activations).IsEqualTo(1)
					.Because("the hook runs once on the instance the scope shares across both exposures");
				await That(roaster.Releases).IsEqualTo(0);
			}

			await That(roaster.Releases).IsEqualTo(1)
				.Because("the scope releases the shared instance once");
			await That(roaster.Disposals).IsEqualTo(1);
		}

		[Test]
		public async Task ATransientMatch_RunsItsHooksOncePerInstance()
		{
			await using MultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			IMultiTransientRoaster first;
			IMultiTransientRoaster second;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = scope.Resolve<IMultiTransientRoaster>();
				second = (IMultiTransientRoaster)scope.Resolve<IMultiTransientPlugin>();
				await That(second).IsNotSameAs(first);
				await That(first.Activations).IsEqualTo(1);
				await That(second.Activations).IsEqualTo(1)
					.Because("each resolution builds a new instance, on which the hook runs once");
			}

			await That(first.Releases).IsEqualTo(1)
				.Because("the scope releases each transient instance it tracked once");
			await That(second.Releases).IsEqualTo(1);
			await That(first.Disposals).IsEqualTo(1);
			await That(second.Disposals).IsEqualTo(1);
		}

		[Test]
		public async Task AnAsyncOnlyDisposableMatch_IsDisposedOnceAsynchronously()
		{
			IMultiAsyncRoaster roaster;
			await using (MultiExposureContainer.Root container = new())
			{
				await container.InitializeAsync();
				roaster = container.Resolve<IMultiAsyncRoaster>();
				await That((object)container.Resolve<IMultiAsyncPlugin>()).IsSameAs(roaster);
			}

			await That(roaster.Disposals).IsEqualTo(1)
				.Because("the handle forwards only the asynchronous disposal of the instance, once");
		}

		[Test]
		public async Task ACollection_MixesTheHandledMatchWithTheOthersInScanOrder()
		{
			await using MixedExposureContainer.Root container = new();

			string[] plugins = container.Resolve<IEnumerable<IMixedPlugin>>().Select(plugin => plugin.GetType().Name).ToArray();

			await That(plugins).IsEqualTo(["MixedAlpha", "MixedBeta", "MixedGamma",])
				.Because("the match served from a handle keeps its place among the matches the container builds directly");
			await That((object)container.Resolve<IMixedBeta>()).IsSameAs(container.Resolve<IEnumerable<IMixedPlugin>>().ElementAt(1));
		}

		[Test]
		public async Task ADecorator_WrapsOnlyItsExposure()
		{
			await using DecoratedMultiExposureContainer.Root container = new();
			await container.InitializeAsync();

			IMultiRoaster decorated = container.Resolve<IMultiRoaster>();

			await That(decorated).IsExactly<CountingRoaster>();
			await That((object)container.Resolve<IMultiPlugin>()).IsSameAs(((CountingRoaster)decorated).Inner)
				.Because("the decorator applies to its own service, while the other exposure serves the shared instance itself");
		}

		[Test]
		public async Task ARegistrationOfAPublicMatch_GivesTheSharedInstanceItsLifetime()
		{
			await using ScopedPublicMatchContainer.Root container = new();
			await container.InitializeAsync();

			IPublicMultiRoaster first;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = scope.Resolve<IPublicMultiRoaster>();
				await That((object)scope.Resolve<IPublicMultiPlugin>()).IsSameAs(first)
					.Because("the scan's exposure shares the instance of the explicit registration, which the scan yields to");
				await That(first.Activations).IsEqualTo(0)
					.Because("the explicit registration sets no hook, so the scan's hook yields with the scan, as in the module's assembly");
			}

			await That(first.Disposals).IsEqualTo(1)
				.Because("the scoped instance is disposed once with its scope");
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				await That((object)scope.Resolve<IPublicMultiPlugin>()).IsNotSameAs(first)
					.Because("the shared instance takes the scoped lifetime of the explicit registration, not the scan's singleton");
			}
		}

		[Test]
		public async Task ARegistrationOfAPublicMatchWithSuppressDisposal_LeavesTheSharedInstanceUndisposed()
		{
			IPublicMultiRoaster roaster;
			await using (UndisposedPublicMatchContainer.Root container = new())
			{
				await container.InitializeAsync();
				roaster = container.Resolve<IPublicMultiRoaster>();
				await That((object)container.Resolve<IPublicMultiPlugin>()).IsSameAs(roaster);
			}

			await That(roaster.Disposals).IsEqualTo(0)
				.Because("the explicit registration suppresses the disposal of the instance it shares with the scan");
		}

		[Test]
		public async Task AMatchBuiltFromARuntimeArgument_IsBuiltFromTheArgumentOfEitherFactory()
		{
			await using ArgumentMultiExposureContainer.Root container = new();
			await using IAwaitenScope scope = await container.CreateScopeAsync();

			Func<string, IArgumentRoaster> roasters = await scope.ResolveAsync<Func<string, IArgumentRoaster>>();
			Func<string, IArgumentPlugin> plugins = await scope.ResolveAsync<Func<string, IArgumentPlugin>>();
			IArgumentRoaster roaster = roasters("latte");

			await That(roaster.Order).IsEqualTo("latte")
				.Because("each exposure forwards the runtime argument to a factory of the instance handle");
			await That(plugins("mocha").Order).IsEqualTo("mocha");
			await That(roasters("latte")).IsNotSameAs(roaster)
				.Because("every call builds a new instance");
		}

		[Test]
		public async Task AnAsyncInitializedMatchBuiltFromARuntimeArgument_AwaitsTheInitialization()
		{
			await using ArgumentMultiExposureContainer.Root container = new();
			await using IAwaitenScope scope = await container.CreateScopeAsync();

			RoasterDesk desk = await scope.ResolveAsync<RoasterDesk>();
			IAsyncArgumentRoaster roaster = await desk.Roast("cappuccino");

			await That(roaster.Order).IsEqualTo("cappuccino");
			await That(roaster.Warmed).IsTrue()
				.Because("the async alias awaits the async factory of the handle, which runs the module's convention");
		}

		[Container]
		[Import(typeof(ArgumentMultiExposureScanModule))]
		[Transient<RoasterDesk>]
		public static partial class ArgumentMultiExposureContainer;

		public sealed class RoasterDesk(Func<string, Task<IAsyncArgumentRoaster>> roasters)
		{
			public Task<IAsyncArgumentRoaster> Roast(string order) => roasters(order);
		}

		[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
		[Import(typeof(PublicMultiExposureScanModule))]
		[Scoped<PublicMultiRoaster, IPublicMultiRoaster>]
		public static partial class ScopedPublicMatchContainer;

		[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
		[Import(typeof(PublicMultiExposureScanModule))]
		[Singleton<PublicMultiRoaster, IPublicMultiRoaster>(SuppressDisposal = true)]
		public static partial class UndisposedPublicMatchContainer;

		[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
		[Import(typeof(MultiExposureScanModule))]
		public static partial class MultiExposureContainer;

		[Container]
		[Import(typeof(MixedExposureScanModule))]
		public static partial class MixedExposureContainer;

		[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
		[Import(typeof(MultiExposureScanModule))]
		[Decorate<CountingRoaster, IMultiRoaster>]
		public static partial class DecoratedMultiExposureContainer;
	}

	public partial class InTheModulesAssembly
	{
		[Test]
		public async Task ASingletonMatch_IsOneInstanceUnderEveryExposure()
		{
			await using LocalMultiExposureContainer.Root container = new();

			ILocalRoaster roaster = container.Resolve<ILocalRoaster>();

			await That((object)container.Resolve<ILocalPlugin>()).IsSameAs(roaster);
			await That(roaster.Activations).IsEqualTo(1);
		}

		[Test]
		public async Task ARegistrationOfTheMatch_GivesTheSharedInstanceItsLifetime()
		{
			await using ScopedLocalMatchContainer.Root container = new();

			ILocalRoaster first;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				first = scope.Resolve<ILocalRoaster>();
				await That((object)scope.Resolve<ILocalPlugin>()).IsSameAs(first);
				await That(first.Activations).IsEqualTo(0)
					.Because("the scan yields to the explicit registration, hooks included");
			}

			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				await That((object)scope.Resolve<ILocalPlugin>()).IsNotSameAs(first);
			}
		}

		[Container]
		[Import(typeof(LocalMultiExposureScanModule))]
		public static partial class LocalMultiExposureContainer;

		[Container]
		[Import(typeof(LocalMultiExposureScanModule))]
		[Scoped<LocalRoaster, ILocalRoaster>]
		internal static partial class ScopedLocalMatchContainer;
	}

	public sealed class CountingRoaster(IMultiRoaster inner) : IMultiRoaster
	{
		public IMultiRoaster Inner => inner;

		public int Activations => inner.Activations;

		public int Releases => inner.Releases;

		public int Warmings => inner.Warmings;

		public int Disposals => inner.Disposals;
	}
}

/// <summary>The marker of the match of <see cref="LocalMultiExposureScanModule" />.</summary>
public interface ILocalPlugin;

/// <summary>The matching interface of the match of <see cref="LocalMultiExposureScanModule" />.</summary>
public interface ILocalRoaster
{
	/// <summary>How often the module's hook ran on the instance.</summary>
	int Activations { get; }
}

internal sealed class LocalRoaster : ILocalPlugin, ILocalRoaster
{
	public int Activations { get; set; }
}

/// <summary>A <c>[Module]</c> of this assembly whose scan exposes its internal match under two interfaces.</summary>
[Module]
[Scan<ILocalPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]
public static partial class LocalMultiExposureScanModule
{
	internal static void Activate(LocalRoaster roaster) => roaster.Activations++;
}
