using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>The cause of AWT264 for an <c>[Inherited]</c> parameter that is not one of a replacement's factory.</summary>
	private const string InheritedPlacement = "[Inherited] applies only to a parameter of a Factory member that a registration declared on a container with Extends names";

	/// <summary>Whether <paramref name="parameter" /> is marked <c>[Inherited]</c>.</summary>
	private static bool IsInheritedParameter(IParameterSymbol parameter) => HasAwaitenAttribute(parameter.GetAttributes(), "InheritedAttribute");

	/// <summary>
	///     The dependency of an <c>[Inherited]</c> parameter of the producer of <paramref name="info" />: the registration of
	///     the base its registration replaces, which <see cref="ReplaceInherited" /> keeps under its
	///     <see cref="InheritedKey" />. Anything else is AWT264, and the parameter receives nothing
	///     (<see cref="DependencyKind.Absent" />), so no further diagnostic follows from it.
	/// </summary>
	private static ParameterModel InheritedDependency(IParameterSymbol parameter, ParameterModel model, ImplInfo info, BuildContext context)
	{
		ParameterModel absent = model with { Kind = DependencyKind.Absent, Key = null, };
		string service = Display(model.ServiceType);
		if (model.Kind == DependencyKind.Arg || HasAwaitenAttribute(parameter.GetAttributes(), "FromKeyAttribute"))
		{
			string other = model.Kind == DependencyKind.Arg ? "[Arg]" : "[FromKey]";
			ReportInvalidInherited(parameter, $"it also carries {other}", $"remove {other}, since [Inherited] takes the key of the registration it replaces", context.Diagnostics);
			return absent;
		}

		if (info.Production != ProductionKind.Factory || info.Origin is not null || info.DeclaredBy is not null || ReadExtends(context.ContainerSymbol) is not { } baseContainer)
		{
			ReportInvalidInherited(parameter, InheritedPlacement, "remove [Inherited]", context.Diagnostics);
			return absent;
		}

		if (!IsInheritedShape(model))
		{
			ReportInvalidInherited(
				parameter,
				$"it takes '{Display(parameter.Type.ToDisplayString(FullyQualified))}'",
				$"take '{service}' as itself, Lazy<T>, Func<T>, Task<T>, Lazy<Task<T>> or Func<Task<T>>",
				context.Diagnostics);
			return absent;
		}

		// A decorated replacement serves its keys through its outermost decorators, which took them over.
		List<ServiceKey> served = info.Services
			.Where(serviceKey => serviceKey.Service == model.ServiceType && serviceKey.Key?.StartsWith(DecoratorKeyPrefix, StringComparison.Ordinal) != true)
			.Concat(context.ServiceToImpl
				.Where(entry => entry.Key.Service == model.ServiceType && info.OutermostDecorators.Contains(entry.Value))
				.Select(entry => entry.Key))
			.Distinct()
			.ToList();
		if (served.Count == 0)
		{
			ReportInvalidInherited(
				parameter,
				$"its registration serves '{Display(info.OwningServiceOrImpl)}', not '{service}'",
				$"take '{Display(info.OwningServiceOrImpl)}', the service the registration replaces",
				context.Diagnostics);
			return absent;
		}

		List<ServiceKey> kept = served
			.Select(serviceKey => new ServiceKey(model.ServiceType, InheritedKey(serviceKey.Key)))
			.Where(context.ServiceToImpl.ContainsKey)
			.ToList();
		if (kept.Count == 1)
		{
			return model with { Key = kept[0].Key, };
		}

		if (kept.Count > 1)
		{
			ReportInvalidInherited(parameter, $"its registration replaces '{service}' under several keys", "register a factory per key", context.Diagnostics);
		}
		else if (!served.Any(serviceKey => context.ConstraintRejected.Contains($"{model.ServiceType}{RejectedKeySeparator}{InheritedKey(serviceKey.Key)}")))
		{
			// A replaced registration the extending container cannot build is already reported (AWT226) and dropped.
			ReportInvalidInherited(
				parameter,
				$"the base '{Display(baseContainer.ToDisplayString(FullyQualified))}' has no registration of '{service}' that its registration replaces",
				"remove [Inherited], or take the service from a registration the base declares under the same key",
				context.Diagnostics);
		}

		return absent;
	}

	/// <summary>
	///     Reports AWT264 for an <c>[Inherited]</c> parameter outside a replacement's factory (a constructor, a lifecycle
	///     hook, an async initializer or a decorator predicate) and returns <see langword="true" /> when it is one.
	/// </summary>
	private static bool ReportMisplacedInherited(IParameterSymbol parameter, List<DiagnosticInfo> diagnostics)
	{
		if (!IsInheritedParameter(parameter))
		{
			return false;
		}

		ReportInvalidInherited(parameter, InheritedPlacement, "remove [Inherited]", diagnostics);
		return true;
	}

	/// <summary>
	///     Whether an <c>[Inherited]</c> parameter takes the service in a shape it supports: the service, <c>Lazy</c>,
	///     <c>Func</c>, <c>Task</c> or their async forms, without runtime arguments or an <c>Owned</c> handle.
	/// </summary>
	private static bool IsInheritedShape(ParameterModel model)
		=> model.Kind is DependencyKind.Direct or DependencyKind.Lazy or DependencyKind.LazyTask
		   || (model.Kind is DependencyKind.Func or DependencyKind.FuncTask or DependencyKind.Task && model.FuncArgTypes.AsArray().Length == 0 &&!model.ProducesOwned);

	private static void ReportInvalidInherited(IParameterSymbol parameter, string cause, string remedy, List<DiagnosticInfo> diagnostics)
	{
		ISymbol owner = parameter.ContainingSymbol;
		string ownerDisplay = owner is IMethodSymbol { MethodKind: MethodKind.Constructor, } constructor
			? Display(constructor.ContainingType.ToDisplayString(FullyQualified))
			: $"{Display(owner.ContainingType.ToDisplayString(FullyQualified))}.{owner.Name}";
		DiagnosticInfo diagnostic = new(
			Diagnostics.InvalidInheritedParameter,
			LocationInfo.From(parameter.Locations.FirstOrDefault()),
			new EquatableArray<string>([parameter.Name, ownerDisplay, cause, remedy,]));
		// One cause per parameter: a Fallback registration is reported before coalescing, which may still classify it.
		if (!diagnostics.Any(reported => reported.Descriptor == Diagnostics.InvalidInheritedParameter && Equals(reported.Location, diagnostic.Location)))
		{
			diagnostics.Add(diagnostic);
		}
	}

	/// <summary>
	///     Reports AWT264 for each <c>[Inherited]</c> parameter of the factory of a <c>Fallback</c> registration declared on
	///     the extending container: it replaces nothing, and coalescing may drop it before its parameters are classified.
	/// </summary>
	private static void ReportFallbackInherited(INamedTypeSymbol container, List<RawRegistration> own, List<DiagnosticInfo> diagnostics)
	{
		foreach (RawRegistration registration in own.Where(registration => registration is { Weak: true, Production: ProductionKind.Factory, ProductionMember: not null, }))
		{
			foreach (IParameterSymbol parameter in MembersNamed(container, registration.ProductionMember!).OfType<IMethodSymbol>()
				         .SelectMany(method => method.Parameters)
				         .Where(IsInheritedParameter))
			{
				ReportInvalidInherited(parameter, "its registration has Fallback, so it replaces nothing", "remove Fallback, or remove [Inherited]", diagnostics);
			}
		}
	}
}
