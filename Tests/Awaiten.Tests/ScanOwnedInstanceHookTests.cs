using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of a <c>[Scan]</c> whose match shares the instance a registration of the type owns: the shared
///     instance runs the registration's hook once, whether the scan names the same hook or none.
/// </summary>
public partial class ScanOwnedInstanceHookTests
{
	[Test]
	public async Task ScanNamingTheRegistrationsHook_RunsItOnce()
	{
		using SameHookContainer.Root container = new();

		SameHookWorker worker = container.Resolve<SameHookWorker>();
		IEnumerable<ISameHookMarker> markers = container.Resolve<IEnumerable<ISameHookMarker>>();

		await That(markers.Single()).IsSameAs(worker)
			.Because("the scan's marker mapping shares the instance the registration owns");
		await That(worker.Activations).IsEqualTo(1)
			.Because("the scan and the registration name one hook, which runs once on the shared instance");
	}

	[Test]
	public async Task ScanWithoutAHook_RunsTheRegistrationsHookOnce()
	{
		using NoScanHookContainer.Root container = new();

		NoScanHookWorker worker = container.Resolve<NoScanHookWorker>();
		INoScanHookMarker marker = container.Resolve<INoScanHookMarker>();

		await That(marker).IsSameAs(worker);
		await That(worker.Activations).IsEqualTo(1)
			.Because("the registration's hook runs once on the instance the scan shares");
	}

	[Test]
	public async Task ModuleOfAnotherAssembly_RunsTheHookItsScanAndRegistrationShareOnce()
	{
		using CrossAssemblyContainer.Root container = new();

		OwnedHookBoiler boiler = container.Resolve<OwnedHookBoiler>();
		IOwnedHookAppliance appliance = container.Resolve<IOwnedHookAppliance>();

		await That(appliance).IsSameAs(boiler);
		await That(boiler.Activations).IsEqualTo(1)
			.Because("the module's registration and scan name one hook, which the container runs once through the generated wrapper");
	}

	[Test]
	public async Task RegistrationOfAnExtendingContainer_ReplacesTheHookOfTheBasesScan()
	{
		using ReplacingContainer.Root container = new();

		ReplacedHookWorker worker = container.Resolve<ReplacedHookWorker>();
		IReplacedHookMarker marker = container.Resolve<IReplacedHookMarker>();

		await That(marker).IsSameAs(worker)
			.Because("the base's scan shares the instance the extending container's registration owns");
		await That(worker.Activations).IsEqualTo(0)
			.Because("the extending container's registration replaces the hook of the inherited scan");
	}

	public interface IReplacedHookMarker;

	public sealed class ReplacedHookWorker : IReplacedHookMarker
	{
		public int Activations { get; set; }
	}

	[Container]
	[Scan<IReplacedHookMarker>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]
	public static partial class ScanningBaseContainer
	{
		internal static void Activate(IReplacedHookMarker marker) => ((ReplacedHookWorker)marker).Activations++;
	}

	[Container(Extends = typeof(ScanningBaseContainer))]
	[Singleton<ReplacedHookWorker>]
	public static partial class ReplacingContainer;

	public interface ISameHookMarker;

	public sealed class SameHookWorker : ISameHookMarker
	{
		public int Activations { get; set; }
	}

	[Container]
	[Singleton<SameHookWorker>(OnActivated = nameof(Activate))]
	[Scan<ISameHookMarker>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]
	public static partial class SameHookContainer
	{
		private static void Activate(ISameHookMarker marker) => ((SameHookWorker)marker).Activations++;
	}

	public interface INoScanHookMarker;

	public sealed class NoScanHookWorker : INoScanHookMarker
	{
		public int Activations { get; set; }
	}

	[Container]
	[Singleton<NoScanHookWorker>(OnActivated = nameof(Activate))]
	[Scan<INoScanHookMarker>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
	public static partial class NoScanHookContainer
	{
		private static void Activate(NoScanHookWorker worker) => worker.Activations++;
	}

	[Container]
	[Import(typeof(OwnedScanHookModule))]
	public static partial class CrossAssemblyContainer;
}
