using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     One usable <c>[AsyncInitializer&lt;T&gt;(method)]</c> of a container or module: the convention
	///     <see cref="Type" /> and the instance <see cref="Method" /> of it named <see cref="Name" /> that initializes an
	///     implementation, declared on <see cref="Owner" />. <see cref="Method" /> is <see langword="null" /> for a
	///     convention of another assembly whose method this compilation does not see because it is not public, which a
	///     container runs only through the wrapper its owner generates for it (see <see cref="ConventionWrapperName" />).
	/// </summary>
	private sealed record AsyncInitializerConvention(INamedTypeSymbol Owner, INamedTypeSymbol Type, IMethodSymbol? Method, string Name)
	{
		/// <summary>The convention as a diagnostic names it, <c>Type.Method</c>.</summary>
		public string Display => $"{AwaitenGenerator.Display(Type.ToDisplayString(FullyQualified))}.{Name}";
	}

	/// <summary>
	///     The conventions of each declaring scope met in one graph build (see <see cref="ScopeAsyncInitializers" />),
	///     resolved once per scope.
	/// </summary>
	private sealed class AsyncInitializerScopes
	{
		private readonly Compilation _compilation;
		private readonly Dictionary<INamedTypeSymbol, List<AsyncInitializerConvention>> _scopes = new(SymbolEqualityComparer.Default);

		public AsyncInitializerScopes(Compilation compilation) => _compilation = compilation;

		/// <summary>The conventions that judge the registrations <paramref name="scope" /> declares.</summary>
		public List<AsyncInitializerConvention> Of(INamedTypeSymbol scope)
		{
			if (!_scopes.TryGetValue(scope, out List<AsyncInitializerConvention>? conventions))
			{
				_scopes[scope] = conventions = ScopeAsyncInitializers(scope, _compilation);
			}

			return conventions;
		}
	}

	/// <summary>
	///     The conventions that judge the registrations a container or module declares: a module's own, and a container's
	///     own after those of the base it extends. Unusable conventions are left out; their owner's build reports them
	///     (AWT233).
	/// </summary>
	private static List<AsyncInitializerConvention> ScopeAsyncInitializers(INamedTypeSymbol scope, Compilation compilation)
	{
		List<AsyncInitializerConvention> conventions = HasAwaitenAttribute(scope.GetAttributes(), "ContainerAttribute") && ReadExtends(scope) is { } baseContainer
			? DeclaredAsyncInitializers(baseContainer, compilation, null)
			: new List<AsyncInitializerConvention>();
		conventions.AddRange(DeclaredAsyncInitializers(scope, compilation, null));
		return conventions;
	}

	/// <summary>
	///     The usable <c>[AsyncInitializer&lt;T&gt;(method)]</c> conventions declared directly on <paramref name="owner" />,
	///     in declaration order, reporting each unusable one as <see cref="Diagnostics.InvalidAsyncInitializer">AWT233</see>
	///     when <paramref name="diagnostics" /> is given. The build of an owner in another assembly reported its unusable
	///     conventions already, so one whose method this compilation does not see at all is kept without it: its method
	///     is not public, and metadata leaves such members out.
	/// </summary>
	private static List<AsyncInitializerConvention> DeclaredAsyncInitializers(INamedTypeSymbol owner, Compilation compilation, List<DiagnosticInfo>? diagnostics)
	{
		List<AsyncInitializerConvention> conventions = new();
		foreach (AttributeData attribute in owner.GetAttributes())
		{
			if (attribute.AttributeClass is not { Name: "AsyncInitializerAttribute", IsGenericType: true, TypeArguments.Length: 1, } attributeClass
			    || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace
			    || attributeClass.TypeArguments[0] is not INamedTypeSymbol { TypeKind: not TypeKind.Error, } type
			    || attribute.ConstructorArguments.Length != 1
			    || attribute.ConstructorArguments[0].Value is not string name)
			{
				continue;
			}

			if (!SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly)
			    && !ConventionTypeAndBases(type).Any(declaring => declaring.GetMembers(name).OfType<IMethodSymbol>().Any()))
			{
				conventions.Add(new AsyncInitializerConvention(owner, type, null, name));
				continue;
			}

			if (AsyncInitializerFault(type, name, owner, compilation, out IMethodSymbol? method) is { } reason)
			{
				diagnostics?.Add(new DiagnosticInfo(
					Diagnostics.InvalidAsyncInitializer,
					LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? owner.Locations.FirstOrDefault()),
					new EquatableArray<string>([Display(type.ToDisplayString(FullyQualified)), name, reason,])));
				continue;
			}

			conventions.Add(new AsyncInitializerConvention(owner, type, method!, name));
		}

		return conventions;
	}

	/// <summary>
	///     Why the method <paramref name="name" /> of <paramref name="type" /> cannot initialize an implementation, or
	///     <see langword="null" /> with the <paramref name="method" /> when it can: the only method of that name on the type
	///     or its base types and interfaces (an override counts as the method it overrides), an instance method that is not
	///     generic, accessible from <paramref name="owner" />, returning <c>Task</c> or <c>ValueTask</c>, whose parameters
	///     are passed by value and carry none of the attributes a hook would use to put Awaiten into the domain type.
	/// </summary>
	private static string? AsyncInitializerFault(INamedTypeSymbol type, string name, INamedTypeSymbol owner, Compilation compilation, out IMethodSymbol? method)
	{
		method = null;
		List<IMethodSymbol> candidates = new();
		foreach (INamedTypeSymbol declaring in ConventionTypeAndBases(type))
		{
			foreach (IMethodSymbol candidate in declaring.GetMembers(name).OfType<IMethodSymbol>())
			{
				if (candidate.MethodKind == MethodKind.Ordinary && !candidates.Any(known => Overrides(known, candidate)))
				{
					candidates.Add(candidate);
				}
			}
		}

		if (candidates.Count == 0)
		{
			return $"'{Display(type.ToDisplayString(FullyQualified))}' has no method '{name}'";
		}

		if (candidates.Count > 1)
		{
			return "the method is overloaded, so there is no way to choose which one to call; give the overloads different names";
		}

		IMethodSymbol found = candidates[0];
		if (found.IsStatic)
		{
			return "the method is static, but an async initializer is called on the instance";
		}

		if (found.IsGenericMethod)
		{
			return "the method is generic, so there is no way to choose its type arguments";
		}

		if (!compilation.IsSymbolAccessibleWithin(found, owner))
		{
			return $"the method is not accessible from '{Display(owner.ToDisplayString(FullyQualified))}'";
		}

		if (!IsAsyncHookReturn(found.ReturnType, compilation))
		{
			return $"it returns '{Display(found.ReturnType.ToDisplayString(FullyQualified))}'; return Task or ValueTask instead";
		}

		foreach (IParameterSymbol parameter in found.Parameters)
		{
			if (parameter.RefKind != RefKind.None)
			{
				return $"its parameter '{parameter.Name}' is passed by reference";
			}

			if (MirroredAwayAttribute(parameter.GetAttributes()) is { } attribute)
			{
				return $"its parameter '{parameter.Name}' is marked {attribute}, which puts Awaiten into the domain type; use an OnActivated hook instead";
			}
		}

		method = found;
		return null;

		static bool Overrides(IMethodSymbol derived, IMethodSymbol candidate)
		{
			for (IMethodSymbol? overridden = derived.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
			{
				if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, candidate.OriginalDefinition))
				{
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>
	///     The types a convention's method can be declared on: an interface and the interfaces it extends, or a class and
	///     its base classes.
	/// </summary>
	private static IEnumerable<INamedTypeSymbol> ConventionTypeAndBases(INamedTypeSymbol type)
	{
		if (type.TypeKind == TypeKind.Interface)
		{
			return new[] { type, }.Concat(type.AllInterfaces);
		}

		List<INamedTypeSymbol> types = new();
		for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
		{
			types.Add(current);
		}

		return types;
	}

	/// <summary>
	///     Whether a value of <paramref name="type" /> is a <paramref name="conventionType" />: the type itself, an
	///     implementation of the interface, or a class derived from the class.
	/// </summary>
	private static bool IsOfConventionType(ITypeSymbol type, INamedTypeSymbol conventionType)
	{
		if (SymbolEqualityComparer.Default.Equals(type, conventionType))
		{
			return true;
		}

		if (conventionType.TypeKind == TypeKind.Interface)
		{
			return type.AllInterfaces.Contains(conventionType, SymbolEqualityComparer.Default);
		}

		for (ITypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
		{
			if (SymbolEqualityComparer.Default.Equals(current, conventionType))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	///     The conventions that run on an implementation, in declaration order: those <paramref name="matches" /> selects,
	///     each method of <paramref name="implementation" /> once even when several conventions reach it, at the position of
	///     the first. Without the implementation (one hidden behind a generated factory) each convention method runs once.
	///     A method this compilation does not see is the one of an earlier convention of the same name on the same type
	///     or on a class in the same class hierarchy, since the owner's build rejects a name that resolves to more than
	///     one method there (AWT233).
	/// </summary>
	private static List<AsyncInitializerConvention> MatchingAsyncInitializers(IEnumerable<AsyncInitializerConvention> conventions, ITypeSymbol? implementation, Func<AsyncInitializerConvention, bool> matches)
	{
		List<AsyncInitializerConvention> matched = new();
		HashSet<IMethodSymbol> methods = new(SymbolEqualityComparer.Default);
		foreach (AsyncInitializerConvention convention in conventions)
		{
			if (matches(convention)
			    && (convention.Method is { } method
				    ? methods.Add(ImplementingMethod(method, implementation).OriginalDefinition)
				    : !matched.Exists(known => known.Name == convention.Name && SameClassHierarchy(known.Type, convention.Type))))
			{
				matched.Add(convention);
			}
		}

		return matched;

		static bool SameClassHierarchy(INamedTypeSymbol first, INamedTypeSymbol second)
			=> SymbolEqualityComparer.Default.Equals(first, second)
			   || (first.TypeKind == TypeKind.Class && second.TypeKind == TypeKind.Class && (IsOfConventionType(first, second) || IsOfConventionType(second, first)));
	}

	/// <summary>
	///     The method a call of the convention <paramref name="method" /> runs on <paramref name="implementation" />: the
	///     implementation of an interface method, or the most derived override of a class method. An explicit interface
	///     implementation is a method of its own. Falls back to <paramref name="method" /> itself.
	/// </summary>
	private static IMethodSymbol ImplementingMethod(IMethodSymbol method, ITypeSymbol? implementation)
	{
		if (implementation is null)
		{
			return method;
		}

		if (method.ContainingType.TypeKind == TypeKind.Interface)
		{
			return implementation.FindImplementationForInterfaceMember(method) as IMethodSymbol ?? method;
		}

		for (ITypeSymbol? current = implementation; current is not null; current = current.BaseType)
		{
			foreach (IMethodSymbol candidate in current.GetMembers(method.Name).OfType<IMethodSymbol>())
			{
				for (IMethodSymbol? overridden = candidate; overridden is not null; overridden = overridden.OverriddenMethod)
				{
					if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, method.OriginalDefinition))
					{
						return candidate;
					}
				}
			}
		}

		return method;
	}

	/// <summary>
	///     The async initializers of an instance: the conventions of the scope that declares its winning registration,
	///     decorator or composite (the module or base container it comes from, else the container) that match its owned
	///     type, or, for a self-compiled
	///     implementation behind a generated factory or an implementation a module's public <c>Factory</c> wrapper returns
	///     as its public service, the conventions its owner recorded. Their parameters are classified
	///     like those of an asynchronous <c>OnActivated</c> hook, so they take part in every graph check. A convention the
	///     container cannot call runs through the wrapper its owner, a module or base container in another assembly,
	///     generates for it (see <see cref="CollectConventionWrappers" />); without one it is reported as
	///     <see cref="Diagnostics.InaccessibleAsyncInitializer">AWT234</see> with the remedy for the cause (see
	///     <see cref="UnwrappedConventionFault" />) and left out.
	/// </summary>
	private static EquatableArray<AsyncInitializerModel> BuildAsyncInitializers(ImplInfo info, ITypeSymbol ownedType, BuildContext context)
	{
		if (info is { Hidden: { } hidden, Origin: { } module, })
		{
			return HiddenAsyncInitializers(info, hidden, module, context);
		}

		INamedTypeSymbol scope = info.ConventionScope(context.ContainerSymbol);
		List<AsyncInitializerConvention> conventions = context.AsyncInitializers.Of(scope);
		if (conventions.Count == 0)
		{
			return default;
		}

		ImplementationTraits? traits = info.Traits;
		Func<AsyncInitializerConvention, bool>? recorded = RecordedMemberInitializers(info, conventions);
		List<AsyncInitializerModel> models = new();
		foreach (AsyncInitializerConvention convention in MatchingAsyncInitializers(conventions, traits is null && recorded is null ? ownedType : null, convention => recorded?.Invoke(convention)
			         ?? (traits is { } known
				         ? known.Runs(convention.Type.ToDisplayString(FullyQualified), convention.Name)
				         : IsOfConventionType(ownedType, convention.Type))))
		{
			if (InaccessibleAsyncInitializerPart(convention, context.ContainerSymbol, context.Compilation) is { } reason)
			{
				INamedTypeSymbol owner = convention.Owner;
				if (InternalsMembers(owner, ConventionWrapperName(convention)).OfType<IMethodSymbol>()
					    .FirstOrDefault(method => method.Parameters.Length > 0 && context.Compilation.IsSymbolAccessibleWithin(method, context.Compilation.Assembly)) is { } wrapper)
				{
					models.Add(new AsyncInitializerModel(
						$"{owner.ToDisplayString(FullyQualified)}.{InternalsClass}",
						wrapper.Name,
						ClassifyHookParameterSymbols(wrapper.Parameters.Skip(1), asynchronous: true, release: false, info, context),
						Wrapper: true));
					continue;
				}

				context.Diagnostics.Add(new DiagnosticInfo(
					Diagnostics.InaccessibleAsyncInitializer,
					info.Location,
					new EquatableArray<string>([
						$"'{DisplayInstance(info.ImplementationType)}'",
						Display(convention.Type.ToDisplayString(FullyQualified)),
						convention.Name,
						..UnwrappedConventionFault(convention, reason, context.Compilation),
					])));
				continue;
			}

			models.Add(new AsyncInitializerModel(
				convention.Type.ToDisplayString(FullyQualified),
				convention.Name,
				ClassifyHookParameterSymbols(convention.Method!.Parameters, asynchronous: true, release: false, info, context)));
		}

		return new EquatableArray<AsyncInitializerModel>(models.ToArray());
	}

	/// <summary>
	///     The <paramref name="reason" /> and remedy of <see cref="Diagnostics.InaccessibleAsyncInitializer">AWT234</see>
	///     for a <paramref name="convention" /> a container cannot call and whose owner generated no wrapper for it. An owner
	///     in the container's assembly hides the convention from it, so it has to be internal or public. The build of an
	///     owner in another assembly records why it generated no wrapper and what lets it (see
	///     <see cref="CollectConventionWrappers" />), a module that is not partial records only that, and an owner built
	///     without the generator records nothing.
	/// </summary>
	private static string[] UnwrappedConventionFault(AsyncInitializerConvention convention, string reason, Compilation compilation)
	{
		INamedTypeSymbol owner = convention.Owner;
		if (SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly))
		{
			return [reason, "make it internal or public",];
		}

		bool module = HasAwaitenAttribute(owner.GetAttributes(), "ModuleAttribute");
		string? cause = Recorded(ConventionSkippedName(convention));
		string? remedy = cause is null ? null : Recorded(ConventionRemedyName(convention));
		if (cause is null && module && ModuleShapeReason(owner) is { } shape)
		{
			cause = shape;
			remedy = shape == ModuleNotPartialReason ? "make the module and every class that contains it partial" : null;
		}

		string kind = module ? "module" : "container";
		return
		[
			$"{reason}, and the {kind} '{Display(owner.ToDisplayString(FullyQualified))}' generated no wrapper for it" + (cause is null ? string.Empty : $" because {cause}"),
			remedy is null ? "make it public, or grant this assembly InternalsVisibleTo in its assembly" : $"{remedy}, or grant this assembly InternalsVisibleTo in the {kind}'s assembly",
		];

		string? Recorded(string constant)
			=> InternalsMembers(owner, constant).OfType<IFieldSymbol>().Select(field => field.ConstantValue as string).FirstOrDefault(value => value is not null);
	}

	/// <summary>
	///     The async initializers of the handle of an internal service a module of another assembly registers: the
	///     wrapper the module generates for each of its conventions that initializes the implementation, which runs the
	///     convention on the instance the handle holds, so neither the service nor the convention has to be accessible.
	///     The module decides which conventions apply (see <see cref="HiddenInitializedType" />), since only it sees the
	///     return type of a private <c>Factory</c> member, so every convention whose wrapper it generated runs. The handle
	///     of one of several producers of a service (see <see cref="ThroughHandle" />) has wrappers named after its member.
	/// </summary>
	private static EquatableArray<AsyncInitializerModel> HiddenAsyncInitializers(ImplInfo info, INamedTypeSymbol implementation, INamedTypeSymbol module, BuildContext context)
	{
		List<AsyncInitializerModel> models = new();
		HashSet<string> names = new(StringComparer.Ordinal);
		List<AsyncInitializerConvention> conventions = DeclaredAsyncInitializers(module, context.Compilation, null);
		string? producer = conventions.Count == 0
			? null
			: ConstructedModuleRegistrations(module, context.Compilation, members: true)
				.Select(registration => registration.Member)
				.FirstOrDefault(member => member is not null && info.ProductionMember == InInternals(HiddenFactoryName(implementation, member)));
		foreach (AsyncInitializerConvention convention in conventions)
		{
			string name = HiddenInitializerName(implementation, convention, producer);
			if (names.Add(name)
			    && InternalsMembers(module, name).OfType<IMethodSymbol>().FirstOrDefault(method => context.Compilation.IsSymbolAccessibleWithin(method, context.Compilation.Assembly)) is { } wrapper)
			{
				models.Add(new AsyncInitializerModel(
					$"{module.ToDisplayString(FullyQualified)}.{InternalsClass}",
					name,
					ClassifyHookParameterSymbols(wrapper.Parameters.Skip(1), asynchronous: true, release: false, info, context),
					Wrapper: true));
			}
		}

		return new EquatableArray<AsyncInitializerModel>(models.ToArray());
	}

	/// <summary>
	///     Why the generated <paramref name="container" /> cannot call <paramref name="convention" />, or
	///     <see langword="null" />: its type or its method is not accessible from it, as with an internal convention of a
	///     module in another assembly that does not grant this one <c>InternalsVisibleTo</c>, whose method this compilation
	///     may not even see. A parameter type is at least as accessible as the method, so it needs no check of its own.
	/// </summary>
	private static string? InaccessibleAsyncInitializerPart(AsyncInitializerConvention convention, INamedTypeSymbol container, Compilation compilation)
	{
		string where = $"is not accessible from the container '{Display(container.ToDisplayString(FullyQualified))}'";
		if (!compilation.IsSymbolAccessibleWithin(convention.Type, container))
		{
			return $"'{Display(convention.Type.ToDisplayString(FullyQualified))}' {where}";
		}

		return convention.Method is { } method && compilation.IsSymbolAccessibleWithin(method, container) ? null : $"the method {where}";
	}

	/// <summary>
	///     <see cref="Diagnostics.InaccessibleAsyncInitializer">AWT234</see> for an implementation the module builds for
	///     its consumers that one of the module's conventions initializes, when a consumer could not run that convention:
	///     its type is private to the module, its method is not accessible in the module's assembly, or a consumer in
	///     another assembly needs the convention's wrapper (see <see cref="CollectConventionWrappers" />), which the module
	///     cannot generate. An assembly granted <c>InternalsVisibleTo</c> calls the convention itself, so like AWT195 the
	///     failure may be left to such an assembly (see <see cref="SkipReason" />).
	/// </summary>
	private static DiagnosticInfo? ModuleAsyncInitializerFault(INamedTypeSymbol implementation, ModuleScanContext context, SelfCompiledSubject subject, Location? location)
	{
		List<AsyncInitializerConvention> conventions = DeclaredAsyncInitializers(context.Module, context.Compilation, null);
		foreach (AsyncInitializerConvention convention in MatchingAsyncInitializers(conventions, implementation, convention => IsOfConventionType(implementation, convention.Type)))
		{
			IMethodSymbol method = convention.Method!;
			string conventionType = Display(convention.Type.ToDisplayString(FullyQualified));
			(string Reason, string Remedy)? fault = null;
			if (TypeHiddenInModule(convention.Type, context.Module, context.Compilation) is not null)
			{
				fault = ($"'{conventionType}' is private to the module, so no container can cast to it", $"make '{conventionType}' internal or public");
			}
			else if (!context.Compilation.IsSymbolAccessibleWithin(method, context.Module.ContainingAssembly))
			{
				fault = ("the method is not accessible in the module's assembly, so no container can call it", "make the method internal or public");
			}
			else if (NeedsConventionWrapper(convention, context) && MirrorConventionParameters(method, context, out _) is { } parameter)
			{
				fault = ($"{parameter.Reason}, so the module can generate no wrapper through which a container in another assembly runs it", parameter.Remedy);
			}

			if (fault is { } found)
			{
				return new DiagnosticInfo(
					Diagnostics.InaccessibleAsyncInitializer,
					LocationInfo.From(location),
					new EquatableArray<string>([
						$"'{Display(subject.TypeName)}'",
						conventionType,
						convention.Name,
						found.Reason,
						found.Remedy,
					]));
			}
		}

		return null;
	}

	/// <summary>
	///     Whether a container in another assembly runs <paramref name="convention" /> of a module it builds an
	///     implementation for, or of a base container it extends, only through the owner's wrapper: the owner is public,
	///     the generated member is public (not one of <see cref="ModuleScanContext.Internal" />), and the convention's type
	///     or method is not.
	/// </summary>
	private static bool NeedsConventionWrapper(AsyncInitializerConvention convention, ModuleScanContext context)
		=> !context.Internal
		   && IsExternallyAccessible(context.Module)
		   && (!IsExternallyAccessible(convention.Type) || convention.Method!.DeclaredAccessibility != Accessibility.Public);

	/// <summary>
	///     The parameters of the public wrapper of a convention <paramref name="method" /> (see
	///     <see cref="CollectConventionWrappers" />), or why its owner cannot generate the wrapper and what lets it. A
	///     parameter of a public type is passed on as it is, and one that takes an internal service the module or another
	///     module of its assembly registers takes the service's handle (see <see cref="MirrorHiddenService" />), as the
	///     wrappers of hooks and of the conventions of handles do. When the wrapper cannot be generated, nothing the
	///     parameters before recorded of the module's handles is kept.
	/// </summary>
	private static (string Reason, string Remedy)? MirrorConventionParameters(IMethodSymbol method, ModuleScanContext context, out FactoryParameter[] parameters)
	{
		parameters = [];
		(int, INamedTypeSymbol[])? snapshot = context.Hidden?.Snapshot();
		List<FactoryParameter> mirrored = new();
		foreach (IParameterSymbol parameter in method.Parameters)
		{
			HiddenMirror? handle = MirrorHiddenService(parameter.Type, parameter.Name, parameter.GetAttributes(), context, out INamedTypeSymbol? unbuildable);
			if (handle is { Error: null, })
			{
				mirrored.Add(handle.Parameter);
				continue;
			}

			if (handle is null && IsExternallyAccessible(parameter.Type))
			{
				mirrored.Add(new FactoryParameter(parameter.Type.ToDisplayString(FullyQualifiedWithNullability), parameter.Name));
				continue;
			}

			if (snapshot is { } taken)
			{
				context.Hidden!.Restore(taken);
			}

			string member = $"its parameter '{parameter.Name}'";
			if (handle?.Error is { } shape)
			{
				return ($"{member} takes the internal service '{shape.Arguments[0]}' as '{shape.Arguments[1]}', which the wrapper cannot pass on",
					$"take '{shape.Arguments[0]}' directly, or as Func<T>, Lazy<T>, Owned<T>, Func<Owned<T>>, Func<TArg..., T>, Func<TArg..., Owned<T>> or a collection of T");
			}

			if (unbuildable is not null)
			{
				return (UnbuildableNeed(member, unbuildable, context.Hidden!.Rejection(unbuildable)),
					$"let the module build '{Display(unbuildable.ToDisplayString(FullyQualified))}', or make the parameter type public");
			}

			return ($"{member} has the type '{Display(parameter.Type.ToDisplayString(FullyQualified))}', which is not public",
				HasAwaitenAttribute(context.Module.GetAttributes(), "ModuleAttribute") ? "make the parameter type public or register it in the module" : "make the parameter type public");
		}

		parameters = mirrored.ToArray();
		return null;
	}

	/// <summary>
	///     The public wrappers a module or a container another assembly can name generates for its conventions that such
	///     an assembly cannot call, because their type or method is not public: each takes the instance as
	///     <c>object</c>, casts it to the convention's type and runs the method, so a container in another assembly
	///     initializes everything the module registers, scans, decorates or builds through a generated factory, or
	///     everything it inherits from the container, without naming the convention. The instance parameter takes any
	///     instance, so one wrapper serves every implementation the convention initializes. A parameter that takes an
	///     internal service of the module goes through the service's handle (see <see cref="MirrorConventionParameters" />),
	///     whose use <paramref name="hidden" /> records. A convention whose type is private to the owner or whose
	///     parameter the wrapper cannot take gets a record of why and of what lets it instead (see
	///     <see cref="ConventionSkippedName" /> and <see cref="ConventionRemedyName" />); a module reports it for what it
	///     builds itself (see <see cref="ModuleAsyncInitializerFault" />), and a container in another assembly for what it
	///     constructs, with that reason. Only the owner's own build generates them; a base container of another assembly
	///     is analyzed by an extending container's build too, which emits none of them.
	/// </summary>
	private static List<ConventionWrapper> CollectConventionWrappers(INamedTypeSymbol owner, Compilation compilation, HiddenServices? hidden = null)
	{
		List<ConventionWrapper> wrappers = new();
		if (!SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly))
		{
			return wrappers;
		}

		ModuleScanContext context = new(owner, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal), hidden);
		string kind = HasAwaitenAttribute(owner.GetAttributes(), "ModuleAttribute") ? "module" : "container";
		foreach (AsyncInitializerConvention convention in DeclaredAsyncInitializers(owner, compilation, null))
		{
			string name = ConventionWrapperName(convention);
			if (!NeedsConventionWrapper(convention, context) || wrappers.Exists(wrapper => wrapper.Name == name || wrapper.Name == ConventionSkippedName(convention)))
			{
				continue;
			}

			IMethodSymbol method = convention.Method!;
			string conventionType = Display(convention.Type.ToDisplayString(FullyQualified));
			FactoryParameter[] parameters = [];
			(string Reason, string Remedy)? fault = TypeHiddenInModule(convention.Type, owner, compilation) is not null
				? ($"'{conventionType}' is private to the {kind}", $"make '{conventionType}' internal or public")
				: MirrorConventionParameters(method, context, out parameters);
			wrappers.Add(fault is not { } skipped
				? new ConventionWrapper(
					name,
					convention.Type.ToDisplayString(FullyQualified),
					convention.Name,
					method.ReturnType.ToDisplayString(FullyQualified),
					new EquatableArray<FactoryParameter>(parameters))
				: new ConventionWrapper(ConventionSkippedName(convention), string.Empty, convention.Name, string.Empty, default, skipped.Reason, ConventionRemedyName(convention), skipped.Remedy));
		}

		return wrappers;
	}

	/// <summary>
	///     Whether a convention of <paramref name="module" /> needs a wrapper (see <see cref="CollectConventionWrappers" />),
	///     for a module that cannot receive generated members, which then records why it has none.
	/// </summary>
	private static bool HasConventionNeedingWrapper(INamedTypeSymbol module, Compilation compilation)
	{
		ModuleScanContext context = new(module, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal));
		return DeclaredAsyncInitializers(module, compilation, null).Exists(convention => NeedsConventionWrapper(convention, context));
	}

	/// <summary>
	///     A deterministic name for the wrapper of a convention (see <see cref="CollectConventionWrappers" />), derived from
	///     its type and method name only, so the module's build and a consuming container agree on it.
	/// </summary>
	private static string ConventionWrapperName(AsyncInitializerConvention convention)
		=> $"Initializer_{convention.Type.Name}_{Fnv1a($"{convention.Type.ToDisplayString(FullyQualified)}\0{convention.Name}"):x8}";

	/// <summary>
	///     A deterministic name for the constant that records why the module or container generates no wrapper for a
	///     convention, derived like <see cref="ConventionWrapperName" />.
	/// </summary>
	private static string ConventionSkippedName(AsyncInitializerConvention convention)
		=> "InitializerSkipped_" + ConventionWrapperName(convention).Substring("Initializer_".Length);

	/// <summary>
	///     A deterministic name for the constant that records that <paramref name="convention" /> initializes what the
	///     <c>Factory</c> wrapper <paramref name="wrapper" /> returns (see <see cref="MemberWrapper.Initializers" />),
	///     derived like <see cref="ConventionWrapperName" />.
	/// </summary>
	private static string MemberInitializerName(string wrapper, AsyncInitializerConvention convention)
		=> $"Initializes_{wrapper}_{ConventionWrapperName(convention).Substring("Initializer_".Length)}";

	/// <summary>
	///     The conventions among <paramref name="conventions" /> that the module of <paramref name="info" />, in another
	///     assembly, recorded as initializing what the public <c>Factory</c> wrapper the container calls returns, because the
	///     wrapper returns the public service in place of an implementation the container cannot see (see
	///     <see cref="MemberWrapper.Initializers" />), or <see langword="null" /> when it recorded none.
	/// </summary>
	private static Func<AsyncInitializerConvention, bool>? RecordedMemberInitializers(ImplInfo info, List<AsyncInitializerConvention> conventions)
	{
		if (info is not { Production: ProductionKind.Factory, Origin: { } owner, ProductionMember: { } member, }
		    || !member.StartsWith(InInternals("Factory_"), StringComparison.Ordinal))
		{
			return null;
		}

		string wrapper = member.Substring(InternalsClass.Length + 1);
		bool Recorded(AsyncInitializerConvention convention) => !InternalsMembers(owner, MemberInitializerName(wrapper, convention)).IsEmpty;
		return conventions.Exists(Recorded) ? Recorded : null;
	}

	/// <summary>
	///     A deterministic name for the constant that records what lets the module or container generate the wrapper of a
	///     convention it generates none for (see <see cref="ConventionSkippedName" />).
	/// </summary>
	private static string ConventionRemedyName(AsyncInitializerConvention convention)
		=> "InitializerRemedy_" + ConventionWrapperName(convention).Substring("Initializer_".Length);
}
