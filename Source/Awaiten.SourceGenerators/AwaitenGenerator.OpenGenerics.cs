using System.Collections.Immutable;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     The deepest open generic expansion nests a closed type argument before it is treated as an unbounded
	///     recursion (AWT129) rather than a real dependency. Well beyond any hand-written generic graph, but far
	///     below the point where the synthesized registrations would exhaust memory.
	/// </summary>
	private const int MaxExpansionDepth = 100;

	/// <summary>
	///     A hard ceiling on the total number of closed implementations expansion may synthesize. The depth limit
	///     bounds a linear recursion (<c>Node&lt;T&gt;</c> -&gt; <c>Node&lt;List&lt;T&gt;&gt;</c> -&gt; ...); this also
	///     bounds a branching one that would otherwise explode across breadth before ever reaching that depth.
	/// </summary>
	private const int MaxExpansionCount = 10_000;

	/// <summary>How a diagnostic about a module's open generic registration names it, before its type.</summary>
	private const string OpenRegistrationKind = "the open generic registration of";

	/// <summary>
	///     Reads the <c>typeof</c> arguments of a non-generic lifetime attribute: the implementation and the service,
	///     which defaults to the implementation. Returns <see langword="false" /> when an argument is missing or does
	///     not bind, which the compiler already reports.
	/// </summary>
	private static bool TryReadTypeofPair(AttributeData attribute, out INamedTypeSymbol implementation, out INamedTypeSymbol service)
	{
		implementation = null!;
		service = null!;
		if (attribute.ConstructorArguments.Length < 1
		    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol { TypeKind: not TypeKind.Error, } declaredImplementation)
		{
			return false;
		}

		implementation = declaredImplementation;
		if (attribute.ConstructorArguments.Length < 2)
		{
			service = implementation;
			return true;
		}

		if (attribute.ConstructorArguments[1].Value is not INamedTypeSymbol { TypeKind: not TypeKind.Error, } declaredService)
		{
			return false;
		}

		service = declaredService;
		return true;
	}

	/// <summary>
	///     Whether a closed <c>typeof</c> pair satisfies what the generic form's <c>where TImplementation : class, TService</c>
	///     constraint enforces: a reference type with an identity or implicit reference conversion to the service.
	/// </summary>
	private static bool IsAssignableRegistration(INamedTypeSymbol implementation, INamedTypeSymbol service, Compilation compilation)
	{
		if (!implementation.IsReferenceType)
		{
			return false;
		}

		Conversion conversion = ((CSharpCompilation)compilation).ClassifyConversion(implementation, service);
		return conversion.IsIdentity || (conversion.IsImplicit && conversion.IsReference);
	}

	/// <summary>
	///     Reads an open <c>[Singleton(typeof(Repository&lt;&gt;), typeof(IRepository&lt;&gt;))]</c> registration into an
	///     <see cref="OpenRegistration" /> carrying every option the generic form has that can hold per closing. Reports
	///     AWT125/AWT127/AWT128 for a malformed pair, AWT207 for <c>Eager</c>, <c>EagerWhen</c> or <c>Instance</c> (each needs a closed
	///     type, and the option is ignored), and AWT208 when the owner declares a method named by <c>Factory</c> but none
	///     with the service's arity (the registration is dropped).
	/// </summary>
	private static void CollectOpenRegistration(
		AttributeData attribute,
		Lifetime lifetime,
		INamedTypeSymbol implementation,
		INamedTypeSymbol service,
		LifetimeCollectionContext context)
	{
		List<DiagnosticInfo> diagnostics = context.Diagnostics;
		Location? liveLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? context.FallbackLocation;
		LocationInfo? location = LocationInfo.From(liveLocation);

		if (TryReadOpenGenericPair(implementation, service, location, diagnostics) is not { } pair)
		{
			return;
		}

		string display = Display(pair.Mapped.ToDisplayString(FullyQualified));
		if (NamedFlag(attribute, "Eager"))
		{
			ReportNeedsClosedType("Eager");
		}

		if (NamedArgument(attribute, "EagerWhen") is not null)
		{
			ReportNeedsClosedType("EagerWhen");
		}

		if (NamedArgument(attribute, "Instance") is not null)
		{
			ReportNeedsClosedType("Instance");
		}

		string? factory = NamedArgument(attribute, "Factory");
		List<IMethodSymbol> factoryMethods = new();
		if (factory is not null)
		{
			List<IMethodSymbol> named = context.Owner.GetMembers(factory)
				.OfType<IMethodSymbol>()
				.Where(method => method.MethodKind == MethodKind.Ordinary)
				.ToList();
			factoryMethods = named.Where(method => method.Arity == pair.Service.Arity).ToList();
			if (named.Count > 0 && factoryMethods.Count == 0)
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.GenericFactoryArityMismatch,
					location,
					new EquatableArray<string>([display, factory, DescribeOwner(context.Origin), pair.Service.Arity.ToString(),])));
				return;
			}

			// AWT245 is reported once for the open registration; its closings only forgive the result.
			if (factoryMethods.FirstOrDefault(method => MayProduceNull(ProducedType(method.ReturnType, context.Compilation), method)) is { } nullable)
			{
				IsNullableProduct(
					ProducedType(nullable.ReturnType, context.Compilation), nullable, ProductionKind.Factory, factory,
					pair.Service.OriginalDefinition.ToDisplayString(FullyQualified), location, diagnostics);
			}
		}

		OpenViaOutcome via = default;
		if (factory is null && NamedArgument(attribute, "Instance") is null && context.Origin is { } owner)
		{
			INamedTypeSymbol definition = pair.Mapped.OriginalDefinition;
			via = ResolveOpenVia(new OpenViaRequest(
				owner, definition, pair.Service.OriginalDefinition.Construct(definition.TypeParameters.Cast<ITypeSymbol>().ToArray()), RegistrationFactoryName(definition), OpenRegistrationKind, liveLocation, context.FallbackLocation,
				context.Compilation, context.Modules, context.Inheritance, context.Inherited, diagnostics));
			if (via.Dropped)
			{
				context.Open.Add(new OpenRegistration(pair.Service, pair.Mapped, lifetime, NamedKeyArgument(attribute), location, context.Origin) { Dropped = true, });
				return;
			}
		}

		string? key = NamedKeyArgument(attribute);
		string? whenInjectedInto = NamedTypeArgument(attribute, "WhenInjectedInto", context.Compilation);
		ReportContextualBindingWithKey(key, whenInjectedInto, pair.Mapped, liveLocation, diagnostics);
		ReportUnsupportedRegistrationKey(attribute, location, display, diagnostics);

		int fallback = NamedFallback(attribute);
		context.Open.Add(new OpenRegistration(pair.Service, pair.Mapped, lifetime, key, location, context.Origin)
		{
			LiveLocation = liveLocation,
			Factory = via.Via?.Method is not null ? InInternals(via.Via.Name) : factory,
			FactoryMethods = via.Via?.Method is { } method ? [method,] : factoryMethods,
			Via = via.Via,
			ViaFault = via.BaseFault,
			Weak = fallback != 0,
			IsDefault = fallback == 1,
			WhenInjectedInto = whenInjectedInto,
			OnActivated = NamedArgument(attribute, "OnActivated"),
			OnRelease = NamedArgument(attribute, "OnRelease"),
			SuppressDisposal = NamedFlag(attribute, "SuppressDisposal"),
			ConsumerOwned = NamedTypeArrayArgument(attribute, "ConsumerOwned"),
			Inheritance = context.Inherited ? context.Inheritance : null,
		});

		void ReportNeedsClosedType(string option)
			=> diagnostics.Add(new DiagnosticInfo(
				Diagnostics.OpenGenericNeedsClosedType,
				location,
				new EquatableArray<string>([option, display,])));
	}

	/// <summary>
	///     Validates and unbinds an open generic <c>typeof</c> pair shared by the open lifetime, decorator and
	///     composite registrations: the mapped type (the implementation, decorator or composite) and the service it
	///     maps onto. Both must be unbound generics (AWT127); after reducing to their unbound definitions their arity
	///     must match (AWT125); and the mapped type must expose the service with its type parameters in declaration
	///     order (AWT128), so a closed service's type arguments apply onto the mapped type by position. Returns the
	///     two unbound definitions, or <see langword="null" /> when any check fails (having reported it).
	/// </summary>
	private static (INamedTypeSymbol Mapped, INamedTypeSymbol Service)? TryReadOpenGenericPair(
		INamedTypeSymbol mapped,
		INamedTypeSymbol service,
		LocationInfo? location,
		List<DiagnosticInfo> diagnostics)
	{
		// AWT127: the typeof-ctor form exists for open generics and must receive unbound generics
		// (typeof(Repository<>)). A closed generic (typeof(Repository<int>)) would otherwise be silently reduced
		// to its open definition by ConstructedFrom below, dropping the type arguments, and a non-generic type
		// would match no closed service, so reject both and point at the generic attribute form.
		if (!mapped.IsUnboundGenericType || !service.IsUnboundGenericType)
		{
			INamedTypeSymbol offending = mapped.IsUnboundGenericType ? service : mapped;
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.OpenGenericNotUnbound,
				location,
				new EquatableArray<string>([
					AwaitenGenerator.Display(offending.ToDisplayString(FullyQualified)),
				])));
			return null;
		}

		// The original, unbound definition (typeof(Repository<>) is the unbound generic).
		mapped = mapped.ConstructedFrom;
		service = service.ConstructedFrom;

		foreach (INamedTypeSymbol nested in new[] { mapped, service, })
		{
			if (GenericContainingType(nested) is { } containing)
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.OpenGenericNestedInGenericType,
					location,
					new EquatableArray<string>([
						AwaitenGenerator.Display(mapped.OriginalDefinition.ToDisplayString(FullyQualified)),
						AwaitenGenerator.Display(service.OriginalDefinition.ToDisplayString(FullyQualified)),
						AwaitenGenerator.Display(nested.OriginalDefinition.ToDisplayString(FullyQualified)),
						AwaitenGenerator.Display(containing.OriginalDefinition.ToDisplayString(FullyQualified)),
					])));
				return null;
			}
		}

		// AWT125: the mapped type's type parameters must line up with the service's, so a closed service
		// can be re-mapped onto it. v1 matches the open form exactly, so the arities must be equal.
		if (mapped.Arity != service.Arity)
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.OpenGenericArityMismatch,
				location,
				new EquatableArray<string>([
					AwaitenGenerator.Display(mapped.ToDisplayString(FullyQualified)),
					AwaitenGenerator.Display(service.ToDisplayString(FullyQualified)),
					mapped.Arity.ToString(),
					service.Arity.ToString(),
				])));
			return null;
		}

		// AWT128: expansion maps the closed service's type arguments onto the mapped type's type parameters
		// positionally, which is only correct when the mapped type exposes the service with its own type
		// parameters in declaration order (Repository<T> : IRepository<T>). A reordered or remapped
		// type (Repository<TKey, TValue> : IRepository<TValue, TKey>) would construct a closed type
		// that does not satisfy the requested service, so reject it rather than emit a broken registration.
		if (!SymbolEqualityComparer.Default.Equals(service, mapped)
		    && !ExposesServiceInOrder(mapped, service))
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.OpenGenericServiceRemapped,
				location,
				new EquatableArray<string>([
					AwaitenGenerator.Display(mapped.ToDisplayString(FullyQualified)),
					AwaitenGenerator.Display(service.ToDisplayString(FullyQualified)),
				])));
			return null;
		}

		return (mapped, service);
	}

	/// <summary>
	///     True when <paramref name="implementation" /> declares <paramref name="service" /> (as an interface or
	///     a base type) with the implementation's own type parameters as the service's type arguments, in
	///     declaration order (<c>Repository&lt;T&gt; : IRepository&lt;T&gt;</c>). This is the shape v1's positional
	///     type-argument mapping requires; a reordered, partially-closed, or otherwise remapped declaration
	///     returns <see langword="false" />.
	/// </summary>
	private static bool ExposesServiceInOrder(INamedTypeSymbol implementation, INamedTypeSymbol service)
	{
		ImmutableArray<ITypeParameterSymbol> parameters = implementation.TypeParameters;
		return implementation.AllInterfaces.Any(@interface => MapsInOrder(@interface, service, parameters))
		       || BaseTypes(implementation).Any(baseType => MapsInOrder(baseType, service, parameters));
	}

	/// <summary>The innermost generic type that contains <paramref name="type" />, or <see langword="null" />.</summary>
	private static INamedTypeSymbol? GenericContainingType(INamedTypeSymbol type)
	{
		for (INamedTypeSymbol? containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
		{
			if (containing.IsGenericType)
			{
				return containing;
			}
		}

		return null;
	}

	/// <summary>The base types of <paramref name="type" />, from its direct base up to (but excluding) <c>object</c>.</summary>
	internal static IEnumerable<INamedTypeSymbol> BaseTypes(ITypeSymbol type)
	{
		for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
		{
			yield return current;
		}
	}

	private static bool MapsInOrder(
		INamedTypeSymbol candidate,
		INamedTypeSymbol service,
		ImmutableArray<ITypeParameterSymbol> parameters)
	{
		if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, service.OriginalDefinition))
		{
			return false;
		}

		ImmutableArray<ITypeSymbol> arguments = candidate.TypeArguments;
		if (arguments.Length != parameters.Length)
		{
			return false;
		}

		for (int i = 0; i < arguments.Length; i++)
		{
			if (!SymbolEqualityComparer.Default.Equals(arguments[i], parameters[i]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	///     Expands open generic registrations to a fixpoint. Seeds from every closed generic service required by a
	///     constructed implementation's constructor and injected members (<c>[Inject]</c> and <c>[InjectProperty]</c>),
	///     by the parameters of a <c>Factory</c> and by those of its lifecycle hooks, each including a collection element,
	///     and likewise by every decorator and composite: a closed one directly, an open one per closing it expands onto.
	///     For each, it expands every matching open registration in declaration order, constructs the closed
	///     implementation through Roslyn, verifies its constraints (AWT126), and synthesizes a concrete
	///     <see cref="RawRegistration" />. Synthesized registrations coalesce like hand-written duplicates, and each
	///     synthesized impl's own dependencies, and those of the open decorators and composites closed onto it, expand in
	///     turn.
	/// </summary>
	private static void ExpandOpenGenerics(
		List<RawRegistration> raw,
		List<OpenRegistration> open,
		ExpansionInputs inputs,
		List<DiagnosticInfo> diagnostics,
		HashSet<string> constraintRejected,
		Dictionary<ServiceKey, List<DiagnosticInfo>> deferredRejections)
	{
		// Closed services already expanded from the open registrations, expanded once since a single visit
		// synthesizes every matching open registration. An explicitly-registered concrete closed service is
		// not blocked here: its open-expanded siblings coexist as additional collection members, joining the
		// explicit one through ordinary coalescing.
		HashSet<string> expandedServices = new(StringComparer.Ordinal);

		// The open service definitions, so constructor selection recognizes a parameter whose closed generic is
		// expandable on demand as satisfiable. Otherwise the seed would pass over the constructor the emitted
		// container actually resolves (its closed generic is not yet in raw) and never expand that dependency.
		HashSet<INamedTypeSymbol> openServices = new(SymbolEqualityComparer.Default);
		foreach (OpenRegistration registration in open)
		{
			openServices.Add(registration.Service);
		}

		// An overridable default (Fallback.Warn/Silent) whose service key a registration without Fallback claims
		// is dropped in full by coalescing (not built, not a collection member), so it must not seed the
		// expansion either; otherwise an overridden default's constructor would synthesize (and the container
		// would emit) closed registrations nothing in the surviving graph needs. The drop set is computed by the
		// same precedence encoding coalescing runs on (see DroppedOverridableDefaults); a losing strong or scan
		// registration still seeds, since it stays a collection member and is built.
		HashSet<int> droppedWeak = DroppedOverridableDefaults(raw);

		// The constructors, members, factories and hooks to scan for closed generic dependencies, each carried with
		// the expansion depth that reached it. Seeded from every known surviving registration, decorator and composite
		// at depth 0; grows as closed impls are synthesized, each one depth deeper than the closed service that
		// produced it.
		Queue<ExpansionWork> worklist = new();
		ExpansionContext context = new(raw, open, openServices, worklist, new HashSet<ISymbol>(SymbolEqualityComparer.Default), inputs, diagnostics, constraintRejected, deferredRejections);
		SeedExpansionWorklist(droppedWeak, context);

		while (worklist.Count > 0)
		{
			ExpansionWork work = worklist.Dequeue();
			INamedTypeSymbol impl = work.Implementation;
			int depth = work.Depth;

			// AWT129: a self-growing registration (Node<T> depending on Node<List<T>>) synthesizes an
			// ever-larger closed implementation at every step and would never converge. Once the total count
			// runs away (a branching recursion), abandon expansion entirely; when a single chain is merely too
			// deep, skip that branch but let shallower ones continue, so the generator terminates rather than
			// looping until it exhausts memory.
			if (context.Synthesized > MaxExpansionCount)
			{
				ReportExpansionTooDeep(impl, depth, context);
				break;
			}

			if (depth > MaxExpansionDepth)
			{
				ReportExpansionTooDeep(impl, depth, context);
				continue;
			}

			foreach (ITypeSymbol required in ScannedServiceTypes(work, context))
			{
				if (required is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false, } closed
				    && expandedServices.Add(closed.ToDisplayString(FullyQualified)))
				{
					ExpandClosedService(closed, depth, context);
				}
			}
		}
	}

	/// <summary>
	///     Seeds the expansion worklist at depth 0 from every surviving registration, deduped: an overridable
	///     default that coalescing drops in full (see <see cref="DroppedOverridableDefaults" />) is skipped, so
	///     its producer and hooks never synthesize closed registrations the surviving graph does not need. Each
	///     registration is scanned through what produces it: a constructed implementation through its constructor and
	///     injected members (or through the constructor a generated factory mirrors, when that is fixed), a <c>Factory</c> through the factory's parameters, and an <c>Instance</c> not at all. Every
	///     closed decorator and composite is seeded too, and each registration closes the open ones that apply to it.
	/// </summary>
	private static void SeedExpansionWorklist(HashSet<int> droppedWeak, ExpansionContext context)
	{
		for (int index = 0; index < context.Raw.Count; index++)
		{
			if (droppedWeak.Contains(index))
			{
				continue;
			}

			RawRegistration registration = context.Raw[index];
			if (registration.Constructor is { } constructor)
			{
				Enqueue(new ExpansionWork(registration.Implementation, constructor, 0, ExpansionScan.Factory), context);
			}
			else if (registration.Production == ProductionKind.Constructor)
			{
				Enqueue(new ExpansionWork(registration.Implementation, null, 0, ExpansionScan.ConstructedInstance), context);
			}
			else if (registration.Production == ProductionKind.Factory && SeedFactory(registration, context.Inputs) is { } factory)
			{
				Enqueue(new ExpansionWork(registration.Implementation, factory, 0, ExpansionScan.Factory), context);
			}

			EnqueueHooks(registration, registration.Implementation, 0, context);
			EnqueuePredicate(registration.Implementation, registration.EagerWhen, 0, context);
			EnqueueOpenWrappers(registration, 0, context);
		}

		foreach (DecorateRegistration decorator in context.Inputs.Wrappers.Decorators)
		{
			EnqueueDecorator(decorator.Decorator, decorator.ServiceSymbol, 0, context, decorator.Via?.Producer);
			EnqueuePredicate(decorator.Decorator, decorator.When, 0, context);
		}

		foreach (CompositeRegistration composite in context.Inputs.Wrappers.Composites)
		{
			Enqueue(new ExpansionWork(composite.Composite, composite.Via?.Producer, 0, ExpansionScan.ConstructedInstance), context);
		}
	}

	/// <summary>
	///     Enqueues the closing of every open decorator and composite that <see cref="ExpandOpenDecorators" /> and
	///     <see cref="ExpandOpenComposites" /> will expand onto <paramref name="registration" />'s closed service: the
	///     same open service, a key the decorator selects or the composite's key, and type arguments that satisfy its
	///     constraints (otherwise AWT171 skips the closing). A contextual registration is reached only from its consumer,
	///     so it closes none.
	/// </summary>
	private static void EnqueueOpenWrappers(RawRegistration registration, int depth, ExpansionContext context)
	{
		if (registration is not { ServiceSymbol: { IsGenericType: true, IsUnboundGenericType: false, } closed, WhenInjectedInto: null, })
		{
			return;
		}

		WrapperRegistrations wrappers = context.Inputs.Wrappers;
		ITypeSymbol[] typeArguments = closed.TypeArguments.ToArray();
		foreach (OpenDecorateRegistration decorator in wrappers.OpenDecorators)
		{
			if (SymbolEqualityComparer.Default.Equals(decorator.Service, closed.ConstructedFrom)
			    && (decorator.Keys is null || decorator.Keys.Contains(registration.Key))
			    && ConstraintsSatisfied(decorator.Decorator, typeArguments, context.Inputs.Compilation))
			{
				INamedTypeSymbol closedDecorator = decorator.Decorator.Construct(typeArguments);
				EnqueueDecorator(closedDecorator, closed, depth, context, decorator.Via?.Close(typeArguments, closedDecorator, closed)?.Producer);
				EnqueuePredicate(closedDecorator, decorator.When, depth, context);
			}
		}

		foreach (OpenCompositeRegistration composite in wrappers.OpenComposites)
		{
			if (SymbolEqualityComparer.Default.Equals(composite.Service, closed.ConstructedFrom)
			    && composite.Key == registration.Key
			    && ConstraintsSatisfied(composite.Composite, typeArguments, context.Inputs.Compilation))
			{
				INamedTypeSymbol closedComposite = composite.Composite.Construct(typeArguments);
				Enqueue(new ExpansionWork(closedComposite, composite.Via?.Close(typeArguments, closedComposite, closed)?.Producer, depth, ExpansionScan.ConstructedInstance), context);
			}
		}
	}

	/// <summary>
	///     Enqueues a decorator of <paramref name="service" /> through the constructor the container builds it with, or the
	///     <paramref name="producer" /> of the owner's factory that builds it for the container, unless it has no single
	///     inner parameter (AWT124), which leaves the whole chain unbuilt.
	/// </summary>
	private static void EnqueueDecorator(INamedTypeSymbol decorator, INamedTypeSymbol service, int depth, ExpansionContext context, IMethodSymbol? producer = null)
	{
		if ((producer ?? SelectExpansionConstructor(decorator, context)) is { } constructor
		    && InnerParameter(constructor, service, context.Inputs.Compilation) is { } inner)
		{
			Enqueue(new ExpansionWork(decorator, constructor, depth, ExpansionScan.Decorator, inner), context);
		}
	}

	/// <summary>
	///     Enqueues the method of a decorator's <c>When</c> or a singleton's <c>EagerWhen</c> predicate, so the services it
	///     takes seed expansion like a factory's parameters. A property, a field or an unusable predicate (AWT246, AWT247) takes none.
	/// </summary>
	private static void EnqueuePredicate(INamedTypeSymbol decorator, DecoratorPredicate? when, int depth, ExpansionContext context)
	{
		if (when?.Method is { } method)
		{
			Enqueue(new ExpansionWork(decorator, method, depth, ExpansionScan.Factory), context);
		}
	}

	/// <summary>
	///     The factory method the container calls for an explicit <c>Factory</c> registration, picked like
	///     <see cref="ResolveFactory" /> does, or <see langword="null" /> when that reports it missing, hidden from the
	///     container or ambiguous (AWT108, AWT153, AWT112), which needs no closing.
	/// </summary>
	private static IMethodSymbol? SeedFactory(RawRegistration registration, ExpansionInputs inputs)
	{
		if (registration.ProductionMember is null)
		{
			return null;
		}

		List<IMethodSymbol> candidates = FindFactoryCandidates(
				registration.Origin ?? inputs.ContainerSymbol, registration.ProductionMember, registration.Implementation, inputs.Compilation)
			.Where(candidate => registration.Origin is null || inputs.Compilation.IsSymbolAccessibleWithin(candidate, inputs.ContainerSymbol))
			.ToList();
		return candidates.Count == 1 ? candidates[0] : null;
	}

	/// <summary>Enqueues <paramref name="work" /> unless its constructor, factory or hook was already enqueued.</summary>
	private static void Enqueue(ExpansionWork work, ExpansionContext context)
	{
		if (context.Seen.Add(work.Method ?? (ISymbol)work.Implementation))
		{
			context.Worklist.Enqueue(work);
		}
	}

	/// <summary>
	///     Enqueues the lifecycle hooks <paramref name="registration" /> names, so their parameters after the instance
	///     seed expansion like a constructor's. Only the overload <see cref="ResolveHook" /> dispatches is scanned: an
	///     unusable or ambiguous hook is an error (AWT164, AWT190, AWT198) that needs no closing. The work belongs to
	///     <paramref name="implementation" />, the closed implementation even when a generic factory of another assembly
	///     produces another type, so a report about it finds the open registration.
	/// </summary>
	private static void EnqueueHooks(RawRegistration registration, INamedTypeSymbol implementation, int depth, ExpansionContext context)
	{
		EnqueueHook(registration, implementation, registration.OnActivated, registration.OnActivatedMarkers, depth, context);
		EnqueueHook(registration, implementation, registration.OnRelease, registration.OnReleaseMarkers, depth, context);
	}

	private static void EnqueueHook(
		RawRegistration registration,
		INamedTypeSymbol implementation,
		string? hookName,
		IReadOnlyList<INamedTypeSymbol>? closedMarkers,
		int depth,
		ExpansionContext context)
	{
		if (hookName is null)
		{
			return;
		}

		// A closing built through the owner's generic factory in the same compilation runs the owner's hook through the
		// wrapper, so it seeds from the hook the owner binds, which the container itself may not reach.
		if (registration is { ConstructVia: { } via, Origin: { } origin, } && via.IndexOf('<') >= 0)
		{
			if (registration.ServiceSymbol is { } closed && OwnerBoundHook(registration.Implementation, hookName, closed, origin, context.Inputs.Compilation) is { } hook)
			{
				Enqueue(new ExpansionWork(implementation, hook, depth, ExpansionScan.Hook), context);
			}

			return;
		}

		(List<IMethodSymbol> matches, List<IReadOnlyList<INamedTypeSymbol>> ambiguousMethods) = CollectHookOverloads(
			registration.Implementation,
			hookName,
			registration.Origin,
			closedMarkers ?? [],
			context.Inputs.ContainerSymbol,
			context.Inputs.Compilation);
		if (matches.Count == 1 && ambiguousMethods.Count == 0)
		{
			Enqueue(new ExpansionWork(implementation, matches[0], depth, ExpansionScan.Hook), context);
		}
	}

	/// <summary>
	///     The hook the generic wrapper of <paramref name="owner" /> runs for a closing: the overload the owner bound for
	///     the open definition with the service constructed with its type parameters (see <see cref="OpenHookWrappers" />),
	///     closed with the type arguments of <paramref name="closedService" /> like the wrapper, or <see langword="null" />
	///     when the owner bound none.
	/// </summary>
	private static IMethodSymbol? OwnerBoundHook(INamedTypeSymbol closedImplementation, string hookName, INamedTypeSymbol closedService, INamedTypeSymbol owner, Compilation compilation)
	{
		INamedTypeSymbol definition = closedImplementation.OriginalDefinition;
		INamedTypeSymbol marker = closedService.OriginalDefinition.Construct(definition.TypeParameters.Cast<ITypeSymbol>().ToArray());
		List<IMethodSymbol> matches = CollectModuleHookOverloads(
			definition, hookName, [marker,], new ModuleScanContext(owner, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal))).Matches;
		if (matches.Count != 1)
		{
			return null;
		}

		return matches[0].Arity == 0 ? matches[0] : matches[0].OriginalDefinition.Construct(closedService.TypeArguments.ToArray());
	}

	/// <summary>
	///     Synthesizes a closed <see cref="RawRegistration" /> for every open registration whose open form matches
	///     <paramref name="closed" />'s open form (exact arity, v1) and that can build the closing, in declaration order.
	///     A single match yields the single-dispatch registration; several yield the members of a closed-generic
	///     collection. A registration whose constraints reject the closing is skipped, and reported (AWT126, AWT209) when
	///     no registration of the closed service accepts it; otherwise the report waits for a request under the skipped
	///     registration's key or context that nothing serves (<see cref="ExpansionContext.DeferredRejections" />). Two closings of one implementation are left to
	///     coalescing, which shares or rejects them as it does closed registrations. Each synthesized implementation is
	///     enqueued one depth deeper so its own closed generic dependencies expand in turn.
	/// </summary>
	private static void ExpandClosedService(INamedTypeSymbol closed, int depth, ExpansionContext context)
	{
		INamedTypeSymbol openForm = closed.ConstructedFrom;
		string closedService = closed.ToDisplayString(FullyQualified);
		ITypeSymbol[] typeArguments = closed.TypeArguments.ToArray();
		// An unkeyed closed registration that variance converts to the closing serves it like an exact one.
		bool registeredClosed = context.Raw.Any(registration =>
			registration.ServiceType == closedService
			|| (registration is { Key: null, WhenInjectedInto: null, ServiceSymbol: { TypeKind: TypeKind.Interface, } service, }
			    && VarianceCompatible(service, closed, context.Inputs.Compilation)));

		// Strong templates first (OrderBy is stable, so declaration order holds within each group), so a Fallback
		// template can tell below whether a stronger registration already claims this closing.
		List<OpenRegistration> candidates = context.Open
			.Where(c => SymbolEqualityComparer.Default.Equals(c.Service, openForm))
			.OrderBy(c => c.Weak)
			.ToList();
		List<(OpenRegistration Candidate, List<IMethodSymbol> Factories)> accepted = new();
		List<(OpenRegistration Candidate, ClosingRejection Rejection)> rejected = new();
		foreach (OpenRegistration candidate in candidates)
		{
			ClosingRejection rejection = AcceptClosing(candidate, closed, context, out List<IMethodSymbol> factories);
			if (rejection == ClosingRejection.None)
			{
				accepted.Add((candidate, factories));
			}
			else
			{
				rejected.Add((candidate, rejection));
			}
		}

		bool served = accepted.Count > 0 || registeredClosed;
		List<OpenRegistration> rejectedCandidates = rejected.Select(entry => entry.Candidate).ToList();
		foreach ((OpenRegistration candidate, ClosingRejection rejection) in rejected)
		{
			DiagnosticInfo? diagnostic = RejectedClosingDiagnostic(candidate, rejection, closed, closedService, rejectedCandidates, context);
			if (!served)
			{
				RecordRejected(context.ConstraintRejectedServices, closedService, candidate.Key);
				if (diagnostic is not null)
				{
					context.Diagnostics.Add(diagnostic);
				}

				continue;
			}

			ServiceKey key = new(closedService, EffectiveKey(candidate));
			if (!context.DeferredRejections.TryGetValue(key, out List<DiagnosticInfo>? deferred))
			{
				deferred = new List<DiagnosticInfo>();
				context.DeferredRejections[key] = deferred;
			}

			if (diagnostic is not null)
			{
				deferred.Add(diagnostic);
			}
		}

		foreach ((OpenRegistration candidate, List<IMethodSymbol> factories) in accepted)
		{
			// Map the closed service's type arguments onto the implementation's type parameters and
			// construct the closed implementation (e.g. Repository<> + [Order] -> Repository<Order>).
			INamedTypeSymbol closedImpl = candidate.Implementation.Construct(typeArguments);

			// A Fallback closing that a registration without Fallback already claims is dropped by coalescing,
			// so it must not seed expansion either (see DroppedOverridableDefaults for the explicit ones).
			bool dropped = candidate.Weak && IsClaimedByStrongerRegistration(candidate, closedService, context.Raw);

			RawRegistration registration = SynthesizeClosedRegistration(candidate, closed, closedService, closedImpl, TypeIdentity(closedImpl, context.Inputs.Compilation));
			if (candidate.Via is not null && !dropped)
			{
				registration = WithoutAmbiguousOpenHooks(registration, candidate, closedImpl.ToDisplayString(FullyQualified), context);
			}

			if (candidate.Via is { Method: null, } generated)
			{
				OwnerFactoryCall? call = generated.Close(typeArguments, closedImpl, closed);
				registration = registration with { ConstructVia = call?.Call, ConstructViaType = call?.ReturnType, Constructor = call?.Producer, };
			}
			else if (candidate.Via is not null)
			{
				registration = ThroughOpenFactory(registration, candidate.Via.Owner, factories[0].Construct(typeArguments), context.Inputs.Compilation);
			}
			else if (candidate.Factory is null)
			{
				registration = ThroughOpenHookWrappers(registration, candidate, dropped, context);
			}

			context.Raw.Add(registration);
			context.Synthesized++;

			if (dropped)
			{
				continue;
			}

			if (candidate.Factory is null)
			{
				Enqueue(new ExpansionWork(closedImpl, registration.Constructor, depth + 1, ExpansionScan.ConstructedInstance), context);
			}

			foreach (IMethodSymbol factory in factories)
			{
				Enqueue(new ExpansionWork(closedImpl, factory.Construct(closed.TypeArguments.ToArray()), depth + 1, ExpansionScan.Factory), context);
			}

			EnqueueHooks(registration, closedImpl, depth + 1, context);
			EnqueueOpenWrappers(registration, depth + 1, context);
		}
	}

	/// <summary>
	///     Checks that <paramref name="closed" />'s type arguments satisfy the constraints of the candidate's
	///     implementation and of at least one of its generic factories, returning the factories that accept them, or why
	///     the candidate cannot build the closing.
	/// </summary>
	private static ClosingRejection AcceptClosing(
		OpenRegistration candidate,
		INamedTypeSymbol closed,
		ExpansionContext context,
		out List<IMethodSymbol> factories)
	{
		factories = [];
		if (candidate.ViaFault is not null || candidate.Dropped)
		{
			return ClosingRejection.Unbuildable;
		}

		if (!ConstraintsSatisfied(candidate.Implementation, closed.TypeArguments.ToArray(), context.Inputs.Compilation))
		{
			return ClosingRejection.Implementation;
		}

		// A generic factory is called with the closing's type arguments, so they must satisfy its constraints too.
		factories = candidate.FactoryMethods
			.Where(method => SatisfiesConstraints(method, closed.TypeArguments, context.Inputs.Compilation))
			.ToList();
		return candidate.FactoryMethods.Count > 0 && factories.Count == 0 ? ClosingRejection.Factory : ClosingRejection.None;
	}

	/// <summary>
	///     The diagnostic for a candidate that cannot build a closing: AWT126 for the implementation's constraints, naming
	///     the other rejected <paramref name="candidates" />, and AWT209 for its factory's, or <see langword="null" /> when
	///     it was reported already. A <see cref="ClosingRejection.Unbuildable" /> registration was reported once for itself
	///     (AWT226, AWT230, AWT231).
	/// </summary>
	private static DiagnosticInfo? RejectedClosingDiagnostic(
		OpenRegistration candidate,
		ClosingRejection rejection,
		INamedTypeSymbol closed,
		string closedService,
		List<OpenRegistration> candidates,
		ExpansionContext context)
		=> rejection switch
		{
			ClosingRejection.Implementation => ConstraintViolation(candidate, closed, closedService, candidates, context),
			ClosingRejection.Factory => FactoryConstraintViolation(candidate, closedService, context),
			_ => null,
		};

	/// <summary>The key coalescing files a closing of <paramref name="candidate" /> under: its consumer's context key, or its own key.</summary>
	private static string? EffectiveKey(OpenRegistration candidate)
		=> candidate.WhenInjectedInto is { } consumer ? ContextKey(consumer) : candidate.Key;

	private static bool IsClaimedByStrongerRegistration(OpenRegistration candidate, string closedService, List<RawRegistration> raw)
		=> raw.Any(registration =>
			registration is { IsScan: false, Weak: false, }
			&& registration.ServiceType == closedService
			&& registration.Key == candidate.Key
			&& registration.WhenInjectedInto == candidate.WhenInjectedInto);

	private static RawRegistration SynthesizeClosedRegistration(
		OpenRegistration candidate,
		INamedTypeSymbol closed,
		string closedService,
		INamedTypeSymbol closedImpl,
		string closedImplIdentity)
	{
		IReadOnlyList<INamedTypeSymbol> markers = [closed,];
		return new RawRegistration(
			closedService,
			closedImplIdentity,
			candidate.Lifetime,
			closedImpl,
			candidate.LiveLocation,
			candidate.Factory is null ? ProductionKind.Constructor : ProductionKind.Factory,
			candidate.Factory,
			Key: candidate.Key,
			ServiceSymbol: closed,
			Weak: candidate.Weak,
			IsDefault: candidate.IsDefault,
			Origin: candidate.Origin,
			IsSynthesized: true,
			OnActivated: candidate.OnActivated,
			OnRelease: candidate.OnRelease,
			SuppressDisposal: candidate.SuppressDisposal,
			WhenInjectedInto: candidate.WhenInjectedInto,
			OnActivatedMarkers: candidate.OnActivated is null ? null : markers,
			OnReleaseMarkers: candidate.OnRelease is null ? null : markers,
			GenericFactory: candidate.Factory is not null,
			ConsumerOwned: candidate.ConsumerOwned);
	}

	/// <summary>
	///     A closing a container in another assembly builds through the generic factory of the module or base
	///     <paramref name="owner" />, closed as <paramref name="factory" />: it produces the accessible type the factory
	///     returns, while the closed implementation keeps the registration's identity and gives its initialization and
	///     disposal, read off its metadata with the owner's conventions, as <see cref="ThroughGeneratedFactory" /> does for a
	///     closed registration. Its hooks run through the generic wrappers the owner generated beside the factory, which
	///     the ordinary hook binding closes with the closing's marker.
	/// </summary>
	private static RawRegistration ThroughOpenFactory(RawRegistration registration, INamedTypeSymbol owner, IMethodSymbol factory, Compilation compilation)
	{
		INamedTypeSymbol definition = registration.Implementation.OriginalDefinition;
		return registration with
		{
			Implementation = (INamedTypeSymbol)factory.ReturnType,
			Traits = TraitsOf(registration.Implementation, DeclaredAsyncInitializers(owner, compilation, null)),
			TraitsExact = true,
			OnActivated = registration.OnActivated is { } activation ? InInternals(RegistrationHookWrapperName(definition, release: false, activation)) : null,
			OnRelease = registration.OnRelease is { } release ? InInternals(RegistrationHookWrapperName(definition, release: true, release)) : null,
		};
	}

	/// <summary>
	///     A closing the container constructs itself of an open registration of a module in another assembly that does not
	///     grant it access, rewritten to run each hook it cannot call through the generic wrapper the module generated for
	///     it, which the ordinary hook binding closes with the closing's marker. A hook the module recorded a skip for,
	///     because an overload this compilation does not see accepts some closing too, is left out with AWT231 unless the
	///     closing is <paramref name="dropped" />.
	/// </summary>
	private static RawRegistration ThroughOpenHookWrappers(RawRegistration registration, OpenRegistration candidate, bool dropped, ExpansionContext context)
	{
		Compilation compilation = context.Inputs.Compilation;
		if (registration.Origin is not { } owner
		    || SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly)
		    || owner.ContainingAssembly.GivesAccessTo(compilation.Assembly))
		{
			return registration;
		}

		INamedTypeSymbol definition = registration.Implementation.OriginalDefinition;
		return registration with
		{
			OnActivated = Wrapped(registration.OnActivated, release: false),
			OnRelease = Wrapped(registration.OnRelease, release: true),
		};

		string? Wrapped(string? hook, bool release)
		{
			if (hook is null)
			{
				return null;
			}

			if (!dropped && SkippedHookReason(owner, definition, release, hook) is { } skipped)
			{
				ReportUnseenHookOverload(owner, definition.ToDisplayString(FullyQualified), release, hook, skipped, candidate, context);
				return null;
			}

			string wrapper = InInternals(RegistrationHookWrapperName(definition, release, hook));
			return MembersNamed(owner, wrapper).Any(member => member is IMethodSymbol { IsGenericMethod: true, } && compilation.IsSymbolAccessibleWithin(member, compilation.Assembly)) ? wrapper : hook;
		}
	}

	/// <summary>
	///     Leaves out each hook of a closing built through the owner's generic factory that more than one overload of the
	///     owner accepts for this closing, reporting AWT190 as for a closing the container builds itself: the owner bound
	///     the wrapper for the open definition, which an overload for one closing does not accept. The bound overload
	///     counts even where this compilation does not see it. A hook the owner of another assembly recorded a skip for,
	///     because an overload this compilation does not see may accept the closing too, is left out with AWT231, or AWT226
	///     for an extended base.
	/// </summary>
	private static RawRegistration WithoutAmbiguousOpenHooks(RawRegistration registration, OpenRegistration candidate, string closedImplName, ExpansionContext context)
	{
		return registration with
		{
			OnActivated = Unambiguous(registration.OnActivated, release: false),
			OnRelease = Unambiguous(registration.OnRelease, release: true),
		};

		string? Unambiguous(string? hookName, bool release)
		{
			if (hookName is null)
			{
				return null;
			}

			INamedTypeSymbol owner = candidate.Via!.Owner;
			Compilation compilation = context.Inputs.Compilation;
			if (!SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly)
			    && SkippedHookReason(owner, registration.Implementation.OriginalDefinition, release, hookName) is { } skipped)
			{
				ReportUnseenHookOverload(owner, closedImplName, release, hookName, skipped, candidate, context);
				return null;
			}

			(List<IMethodSymbol> matches, List<IReadOnlyList<INamedTypeSymbol>> ambiguous) = CollectModuleHookOverloads(
				registration.Implementation, hookName, [registration.ServiceSymbol!,], new ModuleScanContext(owner, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal)));

			// Another assembly may not see the overload the owner bound, which accepts every closing, beside one it sees.
			int unseen = OwnerBoundHook(registration.Implementation, hookName, registration.ServiceSymbol!, owner, compilation) is null ? 1 : 0;
			if (matches.Count + ambiguous.Count + unseen < 2)
			{
				return hookName;
			}

			if (context.ReportedConstraints.Add("hook:" + closedImplName + "\0" + hookName + "\0" + release))
			{
				context.Diagnostics.Add(new DiagnosticInfo(
					Diagnostics.AmbiguousLifecycleHook,
					candidate.Location,
					new EquatableArray<string>([AwaitenGenerator.Display(closedImplName), hookName, DescribeOwner(owner),])));
			}

			return null;
		}
	}

	/// <summary>
	///     Reports a hook of an open registration of <paramref name="owner" /> in another assembly that this container does
	///     not run, because the owner recorded another overload of it this container does not see that accepts some
	///     closing too (<paramref name="reason" />): AWT226 for an extended base or a module inherited from a base in
	///     another assembly, else AWT231.
	/// </summary>
	private static void ReportUnseenHookOverload(INamedTypeSymbol owner, string implementation, bool release, string hookName, string reason, OpenRegistration candidate, ExpansionContext context)
	{
		bool fromBase = HasAwaitenAttribute(owner.GetAttributes(), "ContainerAttribute");

		// The extending container checks the base's graph from its own view, which reports the same AWT231, so only AWT226
		// tells it apart from what the base reports.
		if (!fromBase && candidate.Inheritance is { } inheritance && !SymbolEqualityComparer.Default.Equals(inheritance.Base.ContainingAssembly, context.Inputs.Compilation.Assembly))
		{
			inheritance.ReportPrivate($"The type '{AwaitenGenerator.Display(implementation)}'", new FactoryFault(reason, "give the hook methods distinct names"), context.Diagnostics);
			return;
		}

		LocationInfo? location = candidate.Location;
		DiagnosticInfo diagnostic = fromBase
			? new DiagnosticInfo(
				Diagnostics.InheritedPrivateImplementation,
				location,
				new EquatableArray<string>([
					$"The type '{AwaitenGenerator.Display(implementation)}'", AwaitenGenerator.Display(context.Inputs.ContainerSymbol.ToDisplayString(FullyQualified)),
					AwaitenGenerator.Display(owner.ToDisplayString(FullyQualified)), reason, "give the hook methods distinct names",
				]))
			: new DiagnosticInfo(
				Diagnostics.ModuleHookOverloadUnseen,
				location,
				new EquatableArray<string>([
					AwaitenGenerator.Display(owner.ToDisplayString(FullyQualified)), AwaitenGenerator.Display(implementation), release ? "OnRelease" : "OnActivated", hookName, reason,
				]));
		if (!context.Diagnostics.Contains(diagnostic))
		{
			context.Diagnostics.Add(diagnostic);
		}
	}

	private static DiagnosticInfo? FactoryConstraintViolation(OpenRegistration candidate, string closedService, ExpansionContext context)
	{
		string factory = QualifiedMember(candidate.Origin ?? context.Inputs.ContainerSymbol, candidate.Factory);
		if (!context.ReportedConstraints.Add("factory:" + closedService + "\0" + EffectiveKey(candidate) + "\0" + factory))
		{
			return null;
		}

		return new DiagnosticInfo(
			Diagnostics.GenericFactoryConstraintViolation,
			candidate.Location,
			new EquatableArray<string>([AwaitenGenerator.Display(closedService), AwaitenGenerator.Display(factory),]));
	}

	private static DiagnosticInfo? ConstraintViolation(
		OpenRegistration candidate,
		INamedTypeSymbol closed,
		string closedService,
		List<OpenRegistration> candidates,
		ExpansionContext context)
	{
		string closedImplName = candidate.Implementation.Construct(closed.TypeArguments.ToArray()).ToDisplayString(FullyQualified);
		if (!context.ReportedConstraints.Add(closedImplName + "\0" + closedService + "\0" + EffectiveKey(candidate)))
		{
			return null;
		}

		string implementation = AwaitenGenerator.Display(candidate.Implementation.ToDisplayString(FullyQualified));
		List<string> others = candidates
			.Select(other => AwaitenGenerator.Display(other.Implementation.ToDisplayString(FullyQualified)))
			.Where(other => other != implementation)
			.Distinct(StringComparer.Ordinal)
			.Select(other => "'" + other + "'")
			.ToList();
		return new DiagnosticInfo(
			Diagnostics.OpenGenericConstraintViolation,
			candidate.Location,
			new EquatableArray<string>([
				AwaitenGenerator.Display(closedImplName),
				implementation,
				AwaitenGenerator.DisplayKeyed(closedService, candidate.Key),
				others.Count == 0 ? "" : " (also rejected: " + string.Join(", ", others) + ")",
			]));
	}

	private static void ReportExpansionTooDeep(INamedTypeSymbol impl, int depth, ExpansionContext context)
	{
		string implName = impl.ToDisplayString(FullyQualified);
		if (!context.ReportedConstraints.Add("depth:" + implName))
		{
			return;
		}

		context.Diagnostics.Add(new DiagnosticInfo(
			Diagnostics.OpenGenericExpansionTooDeep,
			context.Open.FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Implementation, impl.OriginalDefinition))?.Location,
			new EquatableArray<string>([
				AwaitenGenerator.Display(implName),
				depth.ToString(),
			])));
	}

	/// <summary>
	///     The service types one unit of expansion work depends on: a factory's parameters and those of the setter
	///     wrappers a module generated next to it for its deferred properties, a hook's parameters after
	///     the instance, a decorator's constructor parameters but the inner one plus its <c>[Inject]</c> properties
	///     (an <c>[InjectProperty]</c> entry does not reach a chain link), or the constructor and injected members of an
	///     implementation the container constructs.
	/// </summary>
	private static IEnumerable<ITypeSymbol> ScannedServiceTypes(ExpansionWork work, ExpansionContext context)
		=> work.Scan switch
		{
			ExpansionScan.Factory => ParameterServiceTypes(work.Method!.Parameters.Concat(DeferredSetters(work.Method).Select(setter => setter.Parameters[1]))),
			ExpansionScan.Hook => ParameterServiceTypes(work.Method!.Parameters.Skip(1)),
			ExpansionScan.Decorator => ParameterServiceTypes(work.Method!.Parameters.Where(parameter => !SymbolEqualityComparer.Default.Equals(parameter, work.Inner)))
				.Concat(InjectedProperties(work.Implementation).Select(property => RequiredServiceType(property.Type))),
			_ => ((work.Method ?? SelectExpansionConstructor(work.Implementation, context)) is { } constructor ? ParameterServiceTypes(constructor.Parameters) : [])
				.Concat(InjectedMemberServiceTypes(work.Implementation, context.Inputs)),
		};

	/// <summary>
	///     The service types <paramref name="parameters" /> resolve from the graph. A runtime argument (<c>[Arg]</c>) is
	///     supplied by the caller, so it needs no closing.
	/// </summary>
	private static IEnumerable<ITypeSymbol> ParameterServiceTypes(IEnumerable<IParameterSymbol> parameters)
		=> parameters
			.Where(parameter => !HasArgAttribute(parameter.GetAttributes()))
			.Select(parameter => RequiredServiceType(parameter.Type));

	/// <summary>
	///     The service types the injected members of a container-constructed implementation depend on: its
	///     <c>[Inject]</c> properties and the <c>[InjectProperty]</c> entries targeting it or its generic definition,
	///     unwrapped like constructor parameters. A property named by both is scanned once, and an entry naming no
	///     settable property (AWT177) contributes nothing.
	/// </summary>
	private static IEnumerable<ITypeSymbol> InjectedMemberServiceTypes(
		INamedTypeSymbol implementation,
		ExpansionInputs inputs)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (IPropertySymbol property in InjectedProperties(implementation))
		{
			names.Add(property.Name);
			yield return RequiredServiceType(property.Type);
		}

		foreach (string propertyName in InjectPropertyNames(implementation, inputs))
		{
			if (names.Add(propertyName)
			    && ResolveNamedInstanceMember(implementation, propertyName) is IPropertySymbol { IsIndexer: false, SetMethod: not null, } property)
			{
				yield return RequiredServiceType(property.Type);
			}
		}
	}

	/// <summary>
	///     The property names of the <c>[InjectProperty]</c> entries that apply to <paramref name="implementation" />:
	///     those keyed by its own type and, for a closing, those keyed by its generic definition.
	/// </summary>
	private static IEnumerable<string> InjectPropertyNames(
		INamedTypeSymbol implementation,
		ExpansionInputs inputs)
		=> new[] { TypeIdentity(implementation, inputs.Compilation), TypeIdentity(implementation.OriginalDefinition, inputs.Compilation), }
			.Distinct(StringComparer.Ordinal)
			.SelectMany(key => inputs.InjectProperties.TryGetValue(key, out List<InjectPropertyEntry>? entries) ? entries : [])
			.Select(entry => entry.PropertyName);

	/// <summary>
	///     The constructor the container builds <paramref name="implementation" /> through, picked by the same
	///     <see cref="AwaitenGenerator.SelectConstructor" /> but treating an open-generic-satisfiable parameter as
	///     satisfiable before expansion, so the seed scans exactly the parameters the emitted container resolves.
	/// </summary>
	private static IMethodSymbol? SelectExpansionConstructor(INamedTypeSymbol implementation, ExpansionContext context)
		=> AwaitenGenerator.SelectConstructor(
			implementation,
			context.Inputs.ContainerSymbol,
			context.Inputs.Compilation,
			context.Raw.Select(r => r.ServiceType),
			context.Inputs.External,
			p => IsOpenGenericSatisfiable(p.Type, context.OpenServices));

	/// <summary>
	///     The single service type a constructor parameter resolves, unwrapping one <c>Task</c>/<c>ValueTask</c>
	///     layer, an <c>Owned&lt;T&gt;</c> handle, a collection element (<c>T[]</c> / <c>IEnumerable&lt;T&gt;</c> and
	///     friends, <c>IAsyncEnumerable&lt;T&gt;</c>, a keyed <c>IReadOnlyDictionary&lt;TKey, T&gt;</c>), and a
	///     <c>Lazy&lt;T&gt;</c> / <c>Func&lt;TArg…, T&gt;</c> relationship, so a closed generic reached through
	///     any of them still seeds open generic expansion, and constructor selection sees the same underlying
	///     service the seed scans.
	/// </summary>
	private static ITypeSymbol RequiredServiceType(ITypeSymbol type)
	{
		ITypeSymbol awaited = UnwrapAwaitable(type);
		return IsOwned(awaited, out ITypeSymbol owned) ? owned : UnwrapElementOrRelationship(awaited);
	}

	/// <summary>
	///     Unwraps one <c>Task&lt;…&gt;</c>/<c>ValueTask&lt;…&gt;</c> layer, so an awaited single service
	///     (<c>Task&lt;IHandler&lt;T&gt;&gt;</c>) or awaited collection (<c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c>)
	///     seeds expansion through the inner type.
	/// </summary>
	private static ITypeSymbol UnwrapAwaitable(ITypeSymbol type)
		=> type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1, } awaitable
		   && awaitable.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
		   && awaitable.Name is "Task" or "ValueTask"
			? awaitable.TypeArguments[0]
			: type;

	/// <summary>
	///     Unwraps a collection element (<c>T[]</c> / <c>IEnumerable&lt;T&gt;</c> and friends,
	///     <c>IAsyncEnumerable&lt;T&gt;</c>, the value of a keyed <c>IReadOnlyDictionary&lt;TKey, T&gt;</c>) or a
	///     <c>Lazy&lt;T&gt;</c> / <c>Func&lt;TArg…, T&gt;</c> relationship, including the <c>Task&lt;T&gt;</c> and
	///     <c>Owned&lt;T&gt;</c> forms a relationship produces, so a closed generic reached through any of them still
	///     seeds open generic expansion.
	/// </summary>
	private static ITypeSymbol UnwrapElementOrRelationship(ITypeSymbol type)
	{
		if (type is IArrayTypeSymbol array)
		{
			return array.ElementType;
		}

		if (type is not INamedTypeSymbol { IsGenericType: true, } generic)
		{
			return type;
		}

		string? container = generic.ContainingNamespace?.ToDisplayString();
		if (container == "System.Collections.Generic"
		    && generic.TypeArguments.Length == 1
		    && generic.Name is "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "IList" or "ICollection" or "IAsyncEnumerable")
		{
			return generic.TypeArguments[0];
		}

		if (container == "System.Collections.Generic" && generic.Name == "IReadOnlyDictionary" && generic.TypeArguments.Length == 2)
		{
			return generic.TypeArguments[1];
		}

		if (container == "System" && generic.Name == "Lazy" && generic.TypeArguments.Length == 1)
		{
			return UnwrapProduced(generic.TypeArguments[0]);
		}

		// A Func<TArg…, T> resolves its last type argument.
		if (container == "System" && generic.Name == "Func" && generic.TypeArguments.Length >= 1)
		{
			return UnwrapProduced(generic.TypeArguments[generic.TypeArguments.Length - 1]);
		}

		return type;

		static ITypeSymbol UnwrapProduced(ITypeSymbol produced)
		{
			ITypeSymbol awaited = UnwrapAwaitable(produced);
			return IsOwned(awaited, out ITypeSymbol owned) ? owned : awaited;
		}
	}

	/// <summary>
	///     Whether a constructor parameter is satisfied by an open generic registration: its underlying service
	///     (through the same unwrapping the seed scans) is a closed generic whose open form is registered, so the
	///     container can construct it on demand. Lets constructor selection prefer the constructor the container
	///     resolves before the closed generic has been expanded into <c>raw</c>.
	/// </summary>
	private static bool IsOpenGenericSatisfiable(ITypeSymbol parameterType, HashSet<INamedTypeSymbol> openServices)
		=> RequiredServiceType(parameterType) is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false, } closed
		   && openServices.Contains(closed.ConstructedFrom);

	/// <summary>
	///     Verifies that <paramref name="typeArguments" /> satisfy the type-parameter constraints of open generic
	///     <paramref name="definition" /> (reference/value-type/unmanaged kinds, <c>new()</c>, and each base/interface
	///     constraint, by the conversions of <see cref="SatisfiesConstraintType" />), so the closed construction is legal
	///     C#. A constraint mentioning a type parameter is checked after substitution; one that cannot be represented
	///     after substitution is skipped rather than failed.
	/// </summary>
	private static bool ConstraintsSatisfied(INamedTypeSymbol definition, ITypeSymbol[] typeArguments, Compilation compilation)
	{
		ImmutableArray<ITypeParameterSymbol> parameters = definition.TypeParameters;
		int count = Math.Min(parameters.Length, typeArguments.Length);

		Dictionary<ITypeParameterSymbol, ITypeSymbol> substitution = new(SymbolEqualityComparer.Default);
		for (int i = 0; i < count; i++)
		{
			substitution[parameters[i]] = typeArguments[i];
		}

		for (int i = 0; i < count; i++)
		{
			if (!ParameterConstraintSatisfied(parameters[i], typeArguments[i], substitution, compilation))
			{
				return false;
			}
		}

		return true;
	}

	private static bool ParameterConstraintSatisfied(
		ITypeParameterSymbol parameter,
		ITypeSymbol argument,
		Dictionary<ITypeParameterSymbol, ITypeSymbol> substitution,
		Compilation compilation)
	{
		if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType)
		{
			return false;
		}

		if (parameter.HasValueTypeConstraint && (!argument.IsValueType || IsNullableValueType(argument)))
		{
			return false;
		}

		if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType)
		{
			return false;
		}

		if (parameter.HasConstructorConstraint && !HasAccessibleParameterlessConstructor(argument))
		{
			return false;
		}

		return parameter.ConstraintTypes
			.Select(constraintType => SubstituteConstraint(constraintType, substitution, compilation))
			.All(substituted => substituted is null || SatisfiesConstraintType(argument, substituted, compilation));
	}

	/// <summary>
	///     Rewrites a constraint type by replacing each type parameter with the closed type argument mapped in
	///     <paramref name="substitution" />, so <c>IComparable&lt;T&gt;</c> becomes <c>IComparable&lt;Order&gt;</c>
	///     and a bare <c>U</c> becomes its argument. Returns <see langword="null" /> when the constraint cannot be
	///     reconstructed from symbols alone (an unmapped type parameter, or a pointer type argument), so the caller
	///     skips that constraint rather than checking a wrong type.
	/// </summary>
	private static ITypeSymbol? SubstituteConstraint(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> substitution, Compilation compilation)
	{
		switch (type)
		{
			case ITypeParameterSymbol parameter:
				return substitution.TryGetValue(parameter, out ITypeSymbol? argument) ? argument : null;

			case IArrayTypeSymbol array:
				return SubstituteConstraint(array.ElementType, substitution, compilation) is { } element
					? compilation.CreateArrayTypeSymbol(element, array.Rank)
					: null;

			case INamedTypeSymbol { IsGenericType: true, } generic:
			{
				INamedTypeSymbol definition = generic.OriginalDefinition;
				if (definition.Arity != generic.TypeArguments.Length)
				{
					return null;
				}

				ITypeSymbol[] arguments = new ITypeSymbol[generic.TypeArguments.Length];
				for (int i = 0; i < arguments.Length; i++)
				{
					ITypeSymbol? substituted = SubstituteConstraint(generic.TypeArguments[i], substitution, compilation);
					if (substituted is null)
					{
						return null;
					}

					arguments[i] = substituted;
				}

				return definition.Construct(arguments);
			}

			case INamedTypeSymbol named:
				return named;

			default:
				return null;
		}
	}

	private static bool IsNullableValueType(ITypeSymbol type)
		=> type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, };

	private static bool HasAccessibleParameterlessConstructor(ITypeSymbol type)
	{
		if (type is not INamedTypeSymbol named)
		{
			return type.IsValueType;
		}

		if (named.IsValueType)
		{
			return true;
		}

		if (named.IsAbstract)
		{
			return false;
		}

		return named.InstanceConstructors.Any(constructor =>
			constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public);
	}

	/// <summary>
	///     An open generic registration template
	///     (<c>[Transient(typeof(Repository&lt;&gt;), typeof(IRepository&lt;&gt;))]</c>) holding the unbound
	///     service and implementation definitions. Not an instance itself; expanded into concrete closed
	///     <see cref="RawRegistration" />s on demand by <see cref="ExpandOpenGenerics" />. <see cref="Origin" />
	///     is the imported module that declared the template (<see langword="null" /> for the container's own),
	///     stamped onto every closed registration expanded from it, like the per-closing options below.
	/// </summary>
	private sealed record OpenRegistration(INamedTypeSymbol Service, INamedTypeSymbol Implementation, Lifetime Lifetime, string? Key, LocationInfo? Location, INamedTypeSymbol? Origin)
	{
		/// <summary>The name of the generic factory method closed per closing, or <see langword="null" /> to construct.</summary>
		public string? Factory { get; init; }

		/// <summary>
		///     The owner's methods named <see cref="Factory" /> with the service's arity, checked against each closing's
		///     type arguments (AWT209) before the closing is synthesized. Empty when no such method exists, which
		///     <c>ResolveFactory</c> then reports per closing (AWT108).
		/// </summary>
		public IReadOnlyList<IMethodSymbol> FactoryMethods { get; init; } = [];

		/// <summary>An overridable default (<c>Fallback.Warn</c>/<c>Fallback.Silent</c>), applying per closing.</summary>
		public bool Weak { get; init; }

		/// <summary><c>Fallback.Warn</c>, which reports AWT148 when two defaults collide on a closing.</summary>
		public bool IsDefault { get; init; }

		public string? WhenInjectedInto { get; init; }

		public string? OnActivated { get; init; }

		public string? OnRelease { get; init; }

		public bool SuppressDisposal { get; init; }

		public IReadOnlyList<string>? ConsumerOwned { get; init; }

		/// <summary>
		///     The live <see cref="Location" />, which every closing takes, so a diagnostic about one points at the open
		///     registration and an analyzer can suppress it there.
		/// </summary>
		public Location? LiveLocation { get; init; }

		/// <summary>
		///     The generic factory of the module or base that builds every closing, because the container cannot construct
		///     the implementation (see <see cref="ResolveOpenVia" />), or <see langword="null" />.
		/// </summary>
		public OpenOwnerFactory? Via { get; init; }

		/// <summary>
		///     Why an extending container cannot build this registration private to its base, reported as AWT226 once
		///     the container's own registrations replaced the inherited ones (see <see cref="ReportUnbuildableInheritedOpen" />).
		///     None of its closings is synthesized.
		/// </summary>
		public FactoryFault? ViaFault { get; init; }

		/// <summary>The inheritance of a module imported by an extended base, which reports what this container cannot build of it (AWT226).</summary>
		public Inheritance? Inheritance { get; init; }

		/// <summary>
		///     Left out after AWT230 or AWT231 (see <see cref="ResolveOpenVia" />): none of its closings is synthesized, and
		///     their consumers do not also report them missing (AWT101).
		/// </summary>
		public bool Dropped { get; init; }
	}

	/// <summary>Why an open registration cannot build a closing of its service.</summary>
	private enum ClosingRejection
	{
		/// <summary>The registration builds the closing.</summary>
		None,

		/// <summary>The registration builds none of its closings, as reported for itself (AWT226, AWT230, AWT231).</summary>
		Unbuildable,

		/// <summary>The type arguments violate the implementation's constraints (AWT126).</summary>
		Implementation,

		/// <summary>The type arguments violate the constraints of every generic factory (AWT209).</summary>
		Factory,
	}

	/// <summary>What one unit of expansion work scans for the closed generic services it requires.</summary>
	private enum ExpansionScan
	{
		/// <summary>The constructor and the injected members of an implementation the container constructs.</summary>
		ConstructedInstance,

		/// <summary>The parameters of the factory that produces the implementation.</summary>
		Factory,

		/// <summary>The parameters after the instance of a lifecycle hook.</summary>
		Hook,

		/// <summary>The constructor parameters but the inner one and the <c>[Inject]</c> properties of a decorator.</summary>
		Decorator,
	}

	/// <summary>
	///     One unit of expansion work: the implementation it belongs to, the factory, hook or decorator constructor that
	///     <see cref="Scan" /> names (<see langword="null" /> for a constructed implementation), the expansion depth that
	///     reached it and, for a decorator, the inner parameter that receives the decorated service and closes nothing.
	/// </summary>
	private readonly record struct ExpansionWork(INamedTypeSymbol Implementation, IMethodSymbol? Method, int Depth, ExpansionScan Scan, IParameterSymbol? Inner = null);

	/// <summary>
	///     The container-wide inputs open generic expansion reads but never changes: the container, the compilation,
	///     the external-resolution surface constructor selection honours, the <c>[InjectProperty]</c> entries whose
	///     properties seed expansion like constructor parameters, and the decorators and composites whose dependencies
	///     seed it too.
	/// </summary>
	private sealed record ExpansionInputs(
		INamedTypeSymbol ContainerSymbol,
		Compilation Compilation,
		ExternalSurface External,
		Dictionary<string, List<InjectPropertyEntry>> InjectProperties,
		WrapperRegistrations Wrappers);

	/// <summary>The closed and open <c>[Decorate]</c> and <c>[Composite]</c> registrations of a container and its modules.</summary>
	private sealed record WrapperRegistrations(
		List<DecorateRegistration> Decorators,
		List<OpenDecorateRegistration> OpenDecorators,
		List<CompositeRegistration> Composites,
		List<OpenCompositeRegistration> OpenComposites);

	/// <summary>
	///     The mutable state threaded through open generic expansion: the growing registration list and worklist,
	///     the open service definitions, the constructors, factories and hooks already enqueued, and the diagnostics
	///     sink. <see cref="ReportedConstraints" /> also dedups the depth-limit report (AWT129) under a <c>depth:</c>
	///     prefix so each cause is reported once. <see cref="DeferredRejections" /> holds, per closed service and the key
	///     or context of a registration skipped for it, the constraint diagnostics a request there reports in place of
	///     AWT101 when another key or context serves the closing.
	/// </summary>
	private sealed record ExpansionContext(
		List<RawRegistration> Raw,
		List<OpenRegistration> Open,
		HashSet<INamedTypeSymbol> OpenServices,
		Queue<ExpansionWork> Worklist,
		HashSet<ISymbol> Seen,
		ExpansionInputs Inputs,
		List<DiagnosticInfo> Diagnostics,
		HashSet<string> ConstraintRejectedServices,
		Dictionary<ServiceKey, List<DiagnosticInfo>> DeferredRejections)
	{
		public HashSet<string> ReportedConstraints { get; } = new(StringComparer.Ordinal);

		/// <summary>The number of closed implementations synthesized so far, bounded by <c>MaxExpansionCount</c>.</summary>
		public int Synthesized { get; set; }
	}
}
