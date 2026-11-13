using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     Reads the predicate of an attribute (see <see cref="PredicateArgument" />), the <c>When</c> of a
	///     <c>[Decorate]</c> or the <c>EagerWhen</c> of a <c>[Singleton]</c>: a static <see langword="bool" /> method,
	///     property or field of <paramref name="owner" />, the type that declares the attribute. Reports AWT246 when it
	///     names no such member and AWT247 when the container cannot reach it, and returns <see langword="false" /> for both.
	/// </summary>
	private static bool TryReadPredicate(
		AttributeData attribute,
		INamedTypeSymbol owner,
		INamedTypeSymbol containerSymbol,
		Compilation compilation,
		Location? location,
		List<DiagnosticInfo> diagnostics,
		out DecoratorPredicate? predicate)
	{
		predicate = null;
		if (PredicateArgument(attribute) is not { } argument || NamedArgument(attribute, argument) is not { } name)
		{
			return true;
		}

		bool own = SymbolEqualityComparer.Default.Equals(owner, containerSymbol);
		bool foreign = !SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly);
		string display = $"{Display(owner.ToDisplayString(FullyQualified))}.{name}";
		List<ISymbol> candidates = owner.GetMembers(name).ToList();
		if (candidates.Count == 0 && foreign && !own)
		{
			return ThroughWrapper(out predicate);
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
				new EquatableArray<string>([display, reason, $"[{attribute.AttributeClass!.Name.Substring(0, attribute.AttributeClass.Name.Length - "Attribute".Length)}]",])));
			return false;
		}

		if (!own && !compilation.IsSymbolAccessibleWithin(usable[0], containerSymbol))
		{
			if (usable[0].DeclaredAccessibility == Accessibility.Private)
			{
				ReportInaccessiblePredicate(display, owner, false, location, diagnostics);
				return false;
			}

			return ThroughWrapper(out predicate);
		}

		predicate = new DecoratorPredicate($"{owner.ToDisplayString(FullyQualified)}.{name}", usable[0] as IMethodSymbol, location);
		return true;

		// Only an owner in another assembly carries the wrapper: one in this assembly generates it in this same run.
		bool ThroughWrapper(out DecoratorPredicate? wrapped)
		{
			wrapped = null;
			string wrapperName = PredicateWrapperName(name);
			if (!foreign
			    || InternalsMembers(owner, wrapperName).FirstOrDefault(member => compilation.IsSymbolAccessibleWithin(member, containerSymbol) && PredicateShapeProblem(member, compilation) is null) is not { } wrapper)
			{
				ReportInaccessiblePredicate(display, owner, foreign, location, diagnostics, name);
				return false;
			}

			string ownerName = owner.ToDisplayString(FullyQualified);
			wrapped = new DecoratorPredicate($"{ownerName}.{InInternals(wrapperName)}", wrapper as IMethodSymbol, location, $"{ownerName}.{name}");
			return true;
		}
	}

	/// <summary>
	///     The named argument of <paramref name="attribute" /> that names a predicate: <c>When</c> of a <c>[Decorate]</c>,
	///     <c>EagerWhen</c> of a <c>[Singleton]</c>, or <see langword="null" /> for any other attribute.
	/// </summary>
	private static string? PredicateArgument(AttributeData attribute)
		=> attribute.AttributeClass is { } attributeClass && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace
			? attributeClass.Name switch
			{
				"DecorateAttribute" => "When",
				"SingletonAttribute" => "EagerWhen",
				_ => null,
			}
			: null;

	/// <summary>
	///     The predicate the <c>When</c> of a <c>[Decorate]</c> or the <c>EagerWhen</c> of a <c>[Singleton]</c> of
	///     <paramref name="owner" /> names when it is internal, which a public owner wraps for containers in other
	///     assemblies (see <see cref="PredicateWrapperName" />), or <see langword="null" />. A private predicate gets no
	///     wrapper: a container in the owner's assembly could not call it either.
	/// </summary>
	private static ISymbol? InternalPredicate(AttributeData attribute, INamedTypeSymbol owner, Compilation compilation)
	{
		if (PredicateArgument(attribute) is not { } argument || NamedArgument(attribute, argument) is not { } name)
		{
			return null;
		}

		List<ISymbol> usable = owner.GetMembers(name).Where(member => PredicateShapeProblem(member, compilation) is null).ToList();
		return usable.Count == 1 && usable[0].DeclaredAccessibility == Accessibility.Internal ? usable[0] : null;
	}

	/// <summary>
	///     The name of the public wrapper a module or base container generates in its <see cref="InternalsClass" /> for its
	///     internal predicate <paramref name="name" />, which is unique among its predicates (AWT246).
	/// </summary>
	private static string PredicateWrapperName(string name) => "Predicate_" + name;

	/// <summary>The name of the constant that records why a module generated no wrapper for its internal predicate <paramref name="name" />.</summary>
	private static string PredicateSkippedName(string name) => "PredicateSkipped_" + name;

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

	/// <summary>
	///     Reports AWT247: the predicate is declared on a module or base container the container cannot reach it on. For an
	///     owner in another assembly that generated no wrapper for the predicate <paramref name="name" />, the cause names
	///     why when the owner's metadata tells: a module that cannot receive generated members, or the reason it recorded.
	/// </summary>
	private static void ReportInaccessiblePredicate(string display, INamedTypeSymbol owner, bool foreign, Location? location, List<DiagnosticInfo> diagnostics, string? name = null)
	{
		string grant = $"grant this assembly InternalsVisibleTo in the {(HasAwaitenAttribute(owner.GetAttributes(), "ContainerAttribute") ? "container" : "module")}'s assembly";
		string other = $"{DescribePredicateOwner(owner)} is in another assembly, whose non-public members the container cannot call";
		string? unwrapped = name is null
			? null
			: ModuleShapeReason(owner)
			  ?? InternalsMembers(owner, PredicateSkippedName(name)).OfType<IFieldSymbol>().Select(field => field.ConstantValue as string).FirstOrDefault(reason => reason is not null);
		diagnostics.Add(new DiagnosticInfo(
			Diagnostics.InaccessibleDecoratorPredicate,
			LocationInfo.From(location),
			new EquatableArray<string>([
				display,
				!foreign ? $"it is private to {DescribePredicateOwner(owner)}; make it internal or public"
				: unwrapped is not null ? $"{other}, and it generated no public wrapper for it: {unwrapped}; make the predicate and its parameter types public, or {grant}"
				: $"{other}; make it public, or {grant}",
			])));
	}

	/// <summary>
	///     Reports, in the module's own build, the <c>When</c> predicates of its <c>[Decorate]</c> attributes and the
	///     <c>EagerWhen</c> predicates of its <c>[Singleton]</c> attributes that no container importing it can use, so
	///     the library that declares the module sees them: one that is unusable (AWT246)
	///     or private (AWT247), unless a container of its assembly imports the module and reports it itself. An internal
	///     predicate is not reported: a container in the module's assembly or in a friend calls it, and one in another
	///     assembly calls the module's wrapper or reports why the module generated none (see <see cref="CollectModuleMemberWrappers" />).
	/// </summary>
	private static void ReportModuleDecoratorPredicates(INamedTypeSymbol module, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		if (AssemblyModuleIndex.Of(compilation).ImportedByContainer(module))
		{
			return;
		}

		foreach (AttributeData attribute in module.GetAttributes())
		{
			if (PredicateArgument(attribute) is null)
			{
				continue;
			}

			Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
			if (TryReadPredicate(attribute, module, module, compilation, location, diagnostics, out DecoratorPredicate? predicate)
			    && predicate is not null
			    && module.GetMembers(predicate.Member.Substring(predicate.Member.LastIndexOf('.') + 1)).First(member => PredicateShapeProblem(member, compilation) is null).DeclaredAccessibility == Accessibility.Private)
			{
				ReportInaccessiblePredicate(Display(predicate.Member), module, false, location, diagnostics);
			}
		}
	}

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
		=> info.When is { Predicate: var predicate, Service: var service, } ? BuildPredicate(predicate, service, context) : null;

	/// <summary>
	///     The predicate model of the <c>EagerWhen</c> of a singleton, or <see langword="null" />. It shares the model, and
	///     so the root's one evaluation, with every decorator and singleton that names the same predicate.
	/// </summary>
	private static PredicateModel? BuildEagerPredicate(ImplInfo info, BuildContext context)
		=> info.EagerWhen is { } predicate && info.Lifetime == Lifetime.Singleton
			? BuildPredicate(predicate, info.OwningServiceOrImpl, context)
			: null;

	private static PredicateModel BuildPredicate(DecoratorPredicate predicate, string service, BuildContext context)
	{
		if (!context.Predicates.TryGetValue(predicate.Member, out PredicateModel? model))
		{
			model = ClassifyPredicateParameters(predicate, service, context);
			context.Predicates.Add(predicate.Member, model);
		}

		return model;
	}

	/// <summary>
	///     Classifies the parameters of a predicate method into the services the root resolves before it evaluates it:
	///     each must be a plain or keyed service, or an imported one. A runtime <c>[Arg]</c>, a parameter passed by
	///     reference, a relationship or a collection is AWT248, an unregistered service AWT101; each is left out, so the
	///     emitted call and every later pass stay coherent while the error fails the build. A parameter without a source
	///     location (a module in another assembly) is located at the attribute.
	/// </summary>
	private static PredicateModel ClassifyPredicateParameters(DecoratorPredicate predicate, string service, BuildContext context)
	{
		if (predicate.Method is not { Parameters.Length: > 0, } method)
		{
			return new PredicateModel(predicate.Member, predicate.Method is not null, default, Declared: predicate.Declared);
		}

		string display = Display(predicate.Declared ?? predicate.Member);
		LocationInfo? attributeLocation = LocationInfo.From(predicate.Location);
		List<ParameterModel> parameters = new();
		List<string> declaredTypes = new();
		foreach (IParameterSymbol parameter in method.Parameters)
		{
			ParameterModel model = ClassifyParameter(parameter, asyncFactory: false, context.External.ServiceTypes);
			model = model with { Location = model.Location ?? attributeLocation, };
			if (ReportMisplacedInherited(parameter, attributeLocation, context.Diagnostics))
			{
				continue;
			}

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
			model = RedirectVariance(model, parameter.Type, context.ServiceToImpl, context.Variance);
			if (context.External.ImportServices && model is { Kind: DependencyKind.Direct, Key: null, } && !context.ServiceToImpl.ContainsKey(KeyOf(model)))
			{
				model = model with { Kind = DependencyKind.External, };
			}

			if (model.Kind == DependencyKind.Direct && !context.ServiceToImpl.ContainsKey(KeyOf(model)))
			{
				context.Diagnostics.Add(model.ClosingHandle
					? UnservedClosing(model, parameter.Type, Display(service), display, model.Location, context.ClosingFaults)
					: new DiagnosticInfo(
						Diagnostics.MissingDependency,
						model.Location,
						new EquatableArray<string>([Display(service), display, DisplayKeyed(model.ServiceType, model.Key),])));
				continue;
			}

			parameters.Add(model);
			declaredTypes.Add(parameter.Type.ToDisplayString(FullyQualified));
		}

		return new PredicateModel(predicate.Member, true, new EquatableArray<ParameterModel>(parameters.ToArray()), new EquatableArray<string>(declaredTypes.ToArray()), predicate.Declared);
	}

	/// <summary>
	///     AWT248 for each service a predicate takes that the root does not share as one instance it can build
	///     synchronously: a scoped or transient service, one built per consumer, or one initialized asynchronously. Run
	///     once the async taint is known; each predicate is checked once.
	/// </summary>
	private static void ValidateDecoratorPredicates(GraphModel graph, List<DiagnosticInfo> diagnostics)
	{
		HashSet<string> checkedPredicates = new(StringComparer.Ordinal);
		foreach (PredicateModel predicate in graph.Instances.SelectMany(instance => new[] { instance.When, instance.EagerWhen, }).OfType<PredicateModel>())
		{
			if (!predicate.IsLazy || !checkedPredicates.Add(predicate.Member))
			{
				continue;
			}

			ParameterModel[] parameters = predicate.Parameters.AsArray();
			for (int p = 0; p < parameters.Length; p++)
			{
				ParameterModel parameter = parameters[p];
				if (parameter.Kind != DependencyKind.Direct
				    || !graph.ServiceToImpl.TryGetValue(KeyOf(parameter), out string? implementation)
				    || !graph.ImplToIndex.TryGetValue(implementation, out int index))
				{
					continue;
				}

				InstanceModel target = graph.Instances[index];
				string? problem = target.IsRequestingTypeFactory ? "it is built for each consumer"
					: target.Lifetime != Lifetime.Singleton && target.Production != ProductionKind.Instance ? $"it is {target.Lifetime.ToString().ToLowerInvariant()}"
					: target.IsAsyncSource ? "it is initialized asynchronously, but a predicate is evaluated synchronously"
					: target.IsAsyncTainted ? $"it depends on the async-initialized service '{DisplayInstance(graph.Instances[AsyncSourceOf(index, graph)].ImplementationType)}', but a predicate is evaluated synchronously"
					: null;
				if (problem is not null)
				{
					diagnostics.Add(new DiagnosticInfo(
						Diagnostics.InvalidDecoratorPredicateParameter,
						parameter.Location,
						new EquatableArray<string>([Display(predicate.DeclaredMember), DisplayKeyed(predicate.DeclaredTypes.AsArray()[p], parameter.Key), problem,])));
				}
			}
		}
	}

	/// <summary>
	///     The index of the first async-initialized instance (see <see cref="InstanceModel.IsAsyncSource" />) the
	///     async-tainted instance at <paramref name="start" /> reaches through the dependencies that carry the taint.
	/// </summary>
	private static int AsyncSourceOf(int start, GraphModel graph)
	{
		HashSet<int> visited = [start,];
		Queue<int> pending = new([start,]);
		while (pending.Count > 0)
		{
			int index = pending.Dequeue();
			if (graph.Instances[index].IsAsyncSource)
			{
				return index;
			}

			foreach (int next in graph.Dependencies[index].Where(next => graph.Instances[next].IsAsyncTainted && visited.Add(next)))
			{
				pending.Enqueue(next);
			}
		}

		return start;
	}
}
