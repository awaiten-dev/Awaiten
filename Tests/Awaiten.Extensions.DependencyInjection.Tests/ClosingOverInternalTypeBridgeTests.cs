using System.Linq;
using Awaiten.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Awaiten.Extensions.DependencyInjection.Tests;

/// <summary>
///     An MS.DI host provides a generic service, like <c>ILogger&lt;T&gt;</c>, closed over an internal implementation of a
///     module in another assembly, which the container cannot name: the container asks the host for the closed type the
///     module recorded, and never projects the generated handle.
/// </summary>
public sealed partial class ClosingOverInternalTypeBridgeTests
{
	[Container]
	[Import(typeof(CrossAssemblyImportedClosingModule))]
	[ImportService(typeof(IClosingLog<>))]
	public static partial class ImportedClosingContainer;

	[Container]
	[Import(typeof(CrossAssemblyClosingModule))]
	public static partial class RegisteredClosingContainer;

	private static ServiceProvider BuildProvider(bool registerLog = true)
	{
		ServiceCollection services = new();
		if (registerLog)
		{
			services.AddSingleton(typeof(IClosingLog<>), typeof(ClosingLog<>));
		}

		services.AddAwaitenContainer<ImportedClosingContainer.Root>();
		return services.BuildServiceProvider(validateScopes: true);
	}

	[Test]
	public async Task TheHostsOpenRegistration_ServesTheClosingOverTheInternalImplementation()
	{
		IClosingLog log;
		using (ServiceProvider provider = BuildProvider())
		{
			log = provider.GetRequiredService<IImportedClosingTea>().Log;

			await That(log.Category.Name).IsEqualTo("ImportedClosingTea")
				.Because("the host closes its open registration over the type the module recorded");
			await That(provider.GetRequiredService<IImportedClosingTea>().Log).IsSameAs(log)
				.Because("the host's singleton is passed on as it is");
		}

		await That(log.Disposed).IsTrue()
			.Because("the host disposes its own singleton, which the container never owned");
	}

	[Test]
	public async Task Verification_AcceptsAHostThatProvidesTheClosing()
	{
		using ServiceProvider provider = BuildProvider();

		await That(() => provider.VerifyAwaitenContainers()).DoesNotThrow();
	}

	[Test]
	public async Task Verification_NamesTheMissingClosing()
	{
		using ServiceProvider provider = BuildProvider(registerLog: false);

		await That(() => provider.VerifyAwaitenContainers()).Throws<InvalidOperationException>()
			.WithMessage("*IClosingLog*ImportedClosingTea*").AsWildcard()
			.Because("the container advertises the closing it asks the host for");
	}

	[Test]
	public async Task Projection_LeavesTheHandleOut()
	{
		ServiceCollection services = new();
		services.AddAwaitenContainer<ImportedClosingContainer.Root>();

		await That(services.Select(descriptor => descriptor.ServiceType.Name)).DoesNotContain(name => name.StartsWith("Closed_", StringComparison.Ordinal))
			.Because("a closing handle is no registration, so the projection does not add it to the service collection");
		await That(services.Select(descriptor => descriptor.ServiceType)).Contains(typeof(IImportedClosingTea));
	}

	[Test]
	public async Task Projection_LeavesTheHandleOfTheModulesRegistrationOut()
	{
		ServiceCollection services = new();
		services.AddAwaitenContainer<RegisteredClosingContainer.Root>();
		using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

		await That(services.Select(descriptor => descriptor.ServiceType.Name)).DoesNotContain(name => name.StartsWith("Closed_", StringComparison.Ordinal))
			.Because("the handle the module's open registration builds is a dependency-only service, which the projection does not add");
		await That(provider.GetRequiredService<IClosingBrew>().Log.Category.Name).IsEqualTo("ClosingBrew");
	}
}
