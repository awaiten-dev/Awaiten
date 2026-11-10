using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of internal implementations of a <c>[Module]</c> in another assembly that take a closing of a
///     public generic service over themselves. <see cref="CrossAssemblyClosingModule" /> and
///     <see cref="CrossAssemblyImportedClosingModule" /> live in the referenced Awaiten.Tests.Support assembly, which grants
///     this assembly no <c>InternalsVisibleTo</c>, so the containers here cannot name the closings and pass them on
///     through the handles the modules generated.
/// </summary>
public partial class ModuleClosingOverInternalTypeTests
{
	[Container]
	[Import(typeof(CrossAssemblyClosingModule))]
	public static partial class RegisteredClosingContainer;

	[Container]
	[Import(typeof(CrossAssemblyImportedClosingModule))]
	[ImportService(typeof(IClosingLog<>))]
	public static partial class ImportedClosingContainer;

	[Container]
	[Import(typeof(CrossAssemblyClosingMembersModule))]
	public static partial class ClosingMembersContainer;

	/// <summary>Serves every closing of <see cref="IClosingLog{T}" /> and records each request.</summary>
	private sealed class LogResolver : IExternalResolver
	{
		public List<Type> Requests { get; } = [];

		public List<IClosingLog> Served { get; } = [];

		public bool TryResolve(Type serviceType, object? key, [NotNullWhen(true)] out object? instance)
		{
			Requests.Add(serviceType);
			IClosingLog log = (IClosingLog)Activator.CreateInstance(typeof(ClosingLog<>).MakeGenericType(serviceType.GetGenericArguments()))!;
			Served.Add(log);
			instance = log;
			return true;
		}
	}

	[Test]
	public async Task TheModulesOpenRegistration_ClosesTheLogOverTheInternalImplementation()
	{
		using RegisteredClosingContainer.Root container = new();

		IClosingBrew brew = container.Resolve<IClosingBrew>();

		await That(brew.Log.Category.Name).IsEqualTo("ClosingBrew")
			.Because("the module closed its open registration over the internal brew, which the container cannot name");
	}

	[Test]
	public async Task TheModulesOpenRegistration_KeepsItsLifetimeAndDisposal()
	{
		IClosingLog log;
		using (RegisteredClosingContainer.Root container = new())
		{
			log = container.Resolve<IClosingBrew>().Log;

			await That(container.Resolve<IClosingCup>().Log).IsSameAs(log)
				.Because("the module registers the log as a singleton, so the cup and the brew share the closing over the brew");
			await That(container.Resolve<IClosingBrew>().Log).IsSameAs(log);
			await That(log.Disposed).IsFalse();
		}

		await That(log.Disposed).IsTrue()
			.Because("the container built the closing, so it owns and disposes it");
	}

	[Test]
	public async Task TheMembersOfTheImplementation_ReceiveTheClosingOverIt()
	{
		await using ClosingMembersContainer.Root container = new();

		IClosingEspresso espresso = await container.ResolveAsync<IClosingEspresso>();

		await That(espresso).IsExactly<DecoratedClosingEspresso>()
			.Because("the predicate received the log closed over the internal espresso, so it applied the decorator");
		await That(espresso.Activated?.Category.Name).IsEqualTo("ClosingEspresso")
			.Because("the hook received the log closed over the internal espresso through the handle");
		await That(espresso.WarmedUp).IsSameAs(espresso.Activated)
			.Because("the async initializer received the same singleton closing");
		await That(espresso.Injected).IsSameAs(espresso.Activated)
			.Because("the injected property received the same singleton closing");
	}

	[Test]
	public async Task AnImportedDefinition_IsAskedOfTheHostForTheClosing()
	{
		LogResolver resolver = new();
		using (ImportedClosingContainer.Root container = new(resolver))
		{
			IImportedClosingTea tea = container.Resolve<IImportedClosingTea>();

			await That(tea.Log).IsSameAs(resolver.Served[0])
				.Because("the container passes the host's instance on through the handle");
			await That(tea.Log.Category.Name).IsEqualTo("ImportedClosingTea");
			await That(resolver.Requests).HasSingle().Which.Satisfies(type => type.GetGenericTypeDefinition() == typeof(IClosingLog<>) && type.GetGenericArguments()[0].Name == "ImportedClosingTea")
				.Because("the container asks its host for the closing over the internal tea, which the module recorded");
		}

		await That(resolver.Served[0].Disposed).IsFalse()
			.Because("the host owns what it provides, so the container never disposes it");
	}

	[Test]
	public async Task AnImportedDefinition_IsAdvertisedAsAnExternalDependency()
	{
		using ImportedClosingContainer.Root container = new(new LogResolver());

		await That(((IAwaitenRoot)container).ExternalDependencies).HasSingle().Which.Satisfies(dependency => dependency.ServiceType.GetGenericArguments()[0].Name == "ImportedClosingTea" && dependency.Key == null)
			.Because("a host verifies that it provides the closing the container asks for");
	}
}
