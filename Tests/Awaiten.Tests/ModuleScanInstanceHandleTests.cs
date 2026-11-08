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

			await That(container.Resolve<IMultiTransientRoaster>()).IsNotSameAs(container.Resolve<IMultiTransientRoaster>());
			await That((object)container.Resolve<IMultiTransientPlugin>()).IsNotSameAs(container.Resolve<IMultiTransientRoaster>());
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

		[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
		[Import(typeof(MultiExposureScanModule))]
		public static partial class MultiExposureContainer;

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

		[Container]
		[Import(typeof(LocalMultiExposureScanModule))]
		public static partial class LocalMultiExposureContainer;
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
