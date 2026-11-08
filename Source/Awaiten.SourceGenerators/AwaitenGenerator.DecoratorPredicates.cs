using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     Reads the <c>When</c> predicate of a <c>[Decorate]</c>: a static <see langword="bool" /> method, property or
	///     field of <paramref name="owner" />, the type that declares the attribute. Reports AWT246 when it names no such
	///     member and AWT247 when the container cannot reach it, and returns <see langword="false" /> for both.
	/// </summary>
	private static bool TryReadDecoratorPredicate(
		AttributeData attribute,
		INamedTypeSymbol owner,
		INamedTypeSymbol containerSymbol,
		Compilation compilation,
		Location? location,
		List<DiagnosticInfo> diagnostics,
		out DecoratorPredicate? predicate)
	{
		predicate = null;
		if (attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "When").Value.Value is not string name)
		{
			return true;
		}

		bool own = SymbolEqualityComparer.Default.Equals(owner, containerSymbol);
		bool foreign = !SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly);
		string display = $"{Display(owner.ToDisplayString(FullyQualified))}.{name}";
		List<ISymbol> candidates = owner.GetMembers(name).ToList();
		if (candidates.Count == 0 && foreign && !own)
		{
			ReportInaccessiblePredicate(display, owner, foreign, location, diagnostics);
			return false;
		}

		List<ISymbol> usable = candidates.Where(candidate => PredicateShapeProblem(candidate, compilation) is null).ToList();
		if (usable.Count != 1)
		{
			string reason = usable.Count > 1
				? $"{DescribePredicateOwner(owner)} has several '{name}' methods returning bool"
				: candidates.Count == 0
					? $"{DescribePredicateOwner(owner)} has no member '{name}'"
					: PredicateShapeProblem(candidates[0], compilation)!;
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.InvalidDecoratorPredicate,
				LocationInfo.From(location),
				new EquatableArray<string>([display, reason,])));
			return false;
		}

		if (!own && !compilation.IsSymbolAccessibleWithin(usable[0], containerSymbol))
		{
			ReportInaccessiblePredicate(display, owner, foreign, location, diagnostics);
			return false;
		}

		predicate = new DecoratorPredicate($"{owner.ToDisplayString(FullyQualified)}.{name}", usable[0] as IMethodSymbol, location);
		return true;
	}

	/// <summary>
	///     Why <paramref name="member" /> cannot be a decorator predicate, or <see langword="null" /> when it is a static
	///     non-generic method, a static readable property or a static field of type <see langword="bool" />.
	/// </summary>
	private static string? PredicateShapeProblem(ISymbol member, Compilation compilation)
	{
		(ITypeSymbol? type, string? problem) = member switch
		{
			IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: true, IsGenericMethod: true, } => (null, $"'{member.Name}' is generic"),
			IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: true, } method => (method.ReturnType, null),
			IPropertySymbol { IsStatic: true, IsIndexer: false, GetMethod: null, } => (null, $"'{member.Name}' has no getter"),
			IPropertySymbol { IsStatic: true, IsIndexer: false, } property => (property.Type, null),
			IFieldSymbol { IsStatic: true, } field => ((ITypeSymbol?)field.Type, (string?)null),
			_ => (null, $"'{member.Name}' is not a static method, property or field"),
		};

		if (problem is not null || type!.SpecialType == SpecialType.System_Boolean)
		{
			return problem;
		}

		return IsAsyncFactoryReturn(type, compilation, out _)
			? $"'{member.Name}' returns '{Display(type.ToDisplayString(FullyQualified))}', but a predicate is evaluated synchronously; compute the value before creating the root"
			: $"'{member.Name}' returns '{Display(type.ToDisplayString(FullyQualified))}', not bool";
	}

	/// <summary>Reports AWT247: the predicate is declared on a module or base container the container cannot reach it on.</summary>
	private static void ReportInaccessiblePredicate(string display, INamedTypeSymbol owner, bool foreign, Location? location, List<DiagnosticInfo> diagnostics)
		=> diagnostics.Add(new DiagnosticInfo(
			Diagnostics.InaccessibleDecoratorPredicate,
			LocationInfo.From(location),
			new EquatableArray<string>([
				display,
				foreign
					? $"{DescribePredicateOwner(owner)} is in another assembly, whose non-public members the container cannot call; make it public"
					: $"it is private to {DescribePredicateOwner(owner)}; make it internal or public",
			])));

	/// <summary>Names the type that declares a predicate in a diagnostic: the module, or the (extended) container.</summary>
	private static string DescribePredicateOwner(INamedTypeSymbol owner)
		=> HasAwaitenAttribute(owner.GetAttributes(), "ContainerAttribute")
			? $"the container '{Display(owner.ToDisplayString(FullyQualified))}'"
			: $"the module '{Display(owner.ToDisplayString(FullyQualified))}'";

	/// <summary>
	///     The predicate model of a conditional decorator chain link, or <see langword="null" /> for any other instance. The
	///     parameters of a predicate are classified once, for its first link, so a predicate shared by several links
	///     reports each problem once.
	/// </summary>
	private static PredicateModel? BuildPredicate(ImplInfo info, BuildContext context)
	{
		if (info.When is not { Predicate: var predicate, Service: var service, })
		{
			return null;
		}

		if (!context.Predicates.TryGetValue(predicate.Member, out EquatableArray<ParameterModel> parameters))
		{
			parameters = ClassifyPredicateParameters(predicate, service, context);
			context.Predicates.Add(predicate.Member, parameters);
		}

		return new PredicateModel(predicate.Member, predicate.Method is not null, parameters);
	}

	/// <summary>
	///     Classifies the parameters of a predicate method into the services the root resolves before it evaluates it:
	///     each must be a plain or keyed service, or an imported one. A runtime <c>[Arg]</c>, a parameter passed by
	///     reference, a relationship or a collection is AWT248, an unregistered service AWT101; each is left out, so the
	///     emitted call and every later pass stay coherent while the error fails the build. A parameter without a source
	///     location (a module in another assembly) is located at the attribute.
	/// </summary>
	private static EquatableArray<ParameterModel> ClassifyPredicateParameters(DecoratorPredicate predicate, string service, BuildContext context)
	{
		if (predicate.Method is not { Parameters.Length: > 0, } method)
		{
			return default;
		}

		string display = Display(predicate.Member);
		LocationInfo? attributeLocation = LocationInfo.From(predicate.Location);
		List<ParameterModel> parameters = new();
		foreach (IParameterSymbol parameter in method.Parameters)
		{
			ParameterModel model = ClassifyParameter(parameter, asyncFactory: false, context.External.ServiceTypes);
			model = model with { Location = model.Location ?? attributeLocation, };
			string? problem = parameter.RefKind != RefKind.None ? "it is passed by reference"
				: model.Kind == DependencyKind.Arg ? "it is a runtime [Arg], which no caller supplies"
				: model.Kind is not (DependencyKind.Direct or DependencyKind.External) ? "it is a relationship or a collection"
				: null;
			if (problem is not null)
			{
				context.Diagnostics.Add(new DiagnosticInfo(
					Diagnostics.InvalidDecoratorPredicateParameter,
					model.Location,
					new EquatableArray<string>([display, Display(parameter.Type.ToDisplayString(FullyQualified)), problem,])));
				continue;
			}

			ReportUnsupportedFromKey(parameter.GetAttributes(), model.Location, display, context.Diagnostics);
			model = RedirectVariance(model, parameter, context.ServiceToImpl, context.Variance);
			if (context.External.ImportServices && model is { Kind: DependencyKind.Direct, Key: null, } && !context.ServiceToImpl.ContainsKey(KeyOf(model)))
			{
				model = model with { Kind = DependencyKind.External, };
			}

			if (model.Kind == DependencyKind.Direct && !context.ServiceToImpl.ContainsKey(KeyOf(model)))
			{
				context.Diagnostics.Add(new DiagnosticInfo(
					Diagnostics.MissingDependency,
					model.Location,
					new EquatableArray<string>([Display(service), display, DisplayKeyed(model.ServiceType, model.Key),])));
				continue;
			}

			parameters.Add(model);
		}

		return new EquatableArray<ParameterModel>(parameters.ToArray());
	}

	/// <summary>
	///     AWT248 for each service a predicate takes that the root does not share as one instance it can build
	///     synchronously: a scoped or transient service, one built per consumer, or one initialized asynchronously. Run
	///     once the async taint is known; each predicate is checked once.
	/// </summary>
	private static void ValidateDecoratorPredicates(GraphModel graph, List<DiagnosticInfo> diagnostics)
	{
		HashSet<string> checkedPredicates = new(StringComparer.Ordinal);
		foreach (InstanceModel instance in graph.Instances)
		{
			if (instance.When is not { IsLazy: true, } predicate || !checkedPredicates.Add(predicate.Member))
			{
				continue;
			}

			foreach (ParameterModel parameter in predicate.Parameters.AsArray())
			{
				if (parameter.Kind != DependencyKind.Direct
				    || !graph.ServiceToImpl.TryGetValue(KeyOf(parameter), out string? implementation)
				    || !graph.ImplToIndex.TryGetValue(implementation, out int index))
				{
					continue;
				}

				InstanceModel target = graph.Instances[index];
				string? problem = target.IsRequestingTypeFactory ? "it is built for each consumer"
					: target.Lifetime != Lifetime.Singleton && target.Production != ProductionKind.Instance ? $"it is {target.Lifetime.ToString().ToLowerInvariant()}"
					: target.IsAsyncTainted ? "it is initialized asynchronously, but a predicate is evaluated synchronously"
					: null;
				if (problem is not null)
				{
					diagnostics.Add(new DiagnosticInfo(
						Diagnostics.InvalidDecoratorPredicateParameter,
						parameter.Location,
						new EquatableArray<string>([Display(predicate.Member), DisplayKeyed(parameter.ServiceType, parameter.Key), problem,])));
				}
			}
		}
	}
}
