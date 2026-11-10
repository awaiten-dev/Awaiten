using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     Adds <see cref="Diagnostics.GeneratorInconsistency">AWT255</see> at <paramref name="location" /> to a model without
	///     errors when a graph dependency of one of its instances resolves to no instance, which only a bug of the generator
	///     leaves unreported, so the container emits only its shell instead of failing the emission.
	/// </summary>
	internal static ContainerModel VerifyDependencies(ContainerModel model, LocationInfo? location)
	{
		InstanceModel[] instances = model.Instances.AsArray();
		HashSet<ServiceKey> services = new(instances.SelectMany(instance => instance.Services.AsArray()));
		foreach (InstanceModel instance in instances)
		{
			foreach (ParameterModel dependency in instance.WalkedDependencies())
			{
				if (UnresolvedService(dependency, services) is { } service)
				{
					DiagnosticInfo inconsistency = new(Diagnostics.GeneratorInconsistency, location, new EquatableArray<string>([
						ContainerName(model),
						$"the dependency '{DisplayKeyed(service.Service, service.Key)}' of '{DisplayInstance(instance.ImplementationType)}' resolves to no registration",
					]));
					return model with { Diagnostics = new EquatableArray<DiagnosticInfo>([..model.Diagnostics.AsArray(), inconsistency,]), };
				}
			}
		}

		return model;
	}

	/// <summary>
	///     The generated source of <paramref name="model" />. When the emission throws, which only a bug of the generator
	///     causes, it reports <see cref="Diagnostics.GeneratorInconsistency">AWT255</see> with the exception and returns only
	///     the shell, since the compiler would turn the exception into warning CS8785 and leave the container out.
	/// </summary>
	internal static string EmitContainer(ContainerModel model, Action<Diagnostic> report)
	{
		try
		{
			return Sources.Emit(model);
		}
		catch (Exception exception)
		{
			report(Diagnostic.Create(Diagnostics.GeneratorInconsistency, Location.None, ContainerName(model),
				$"emitting it threw {exception.GetType().FullName}: {exception.Message.TrimEnd('.')}"));
			return Sources.Emit(model with { ErrorsElsewhere = true, });
		}
	}

	/// <summary>
	///     The service a dependency that the emission looks up among the instances names when no instance provides it, else
	///     <see langword="null" />. Collections, imports, runtime arguments and the other kinds the emission supplies without
	///     the lookup have none.
	/// </summary>
	private static ServiceKey? UnresolvedService(ParameterModel dependency, HashSet<ServiceKey> services)
	{
		switch (dependency.Kind)
		{
			case DependencyKind.Direct or DependencyKind.Func or DependencyKind.Lazy or DependencyKind.Owned
				or DependencyKind.Task or DependencyKind.FuncTask or DependencyKind.LazyTask:
				ServiceKey service = new(dependency.ServiceType, dependency.Key);
				return services.Contains(service) ? null : service;
			case DependencyKind.TypeDispatch:
				foreach (DispatchMember member in dependency.DispatchMembers.AsArray())
				{
					if (!services.Contains(new ServiceKey(member.Service, null)))
					{
						return new ServiceKey(member.Service, null);
					}
				}

				return null;
			default:
				return null;
		}
	}

	/// <summary>The full name of the container of <paramref name="model" />, without <c>global::</c>.</summary>
	private static string ContainerName(ContainerModel model)
		=> string.Join(".", model.ContainingTypes.AsArray().Select(type => type.Name).Prepend(model.Namespace).Append(model.TypeName)
			.Where(part => !string.IsNullOrEmpty(part)));
}
