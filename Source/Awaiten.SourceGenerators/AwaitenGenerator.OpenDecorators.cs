using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     Expands each open generic <c>[Decorate(typeof(D&lt;&gt;), typeof(IService&lt;&gt;))]</c> into a closed
	///     <see cref="DecorateRegistration" /> for every closing of its service already present in the coalesced graph
	///     under a key the decorator selects (however it was registered - explicit, scanned or open generic),
	///     constructing the matching closing of the decorator. The synthesized registrations carry the open form's
	///     <c>Order</c>, declaration order and the keys present for that closing, so the existing
	///     <see cref="DecoratorChainBuilder" /> interleaves them with explicit closed decorators of the same closing.
	///     Warns AWT171 and skips a closing whose type arguments violate the decorator's constraints; reports AWT210 for a
	///     selected key that no closing has, unless a closing of its service was dropped under that key for a reported
	///     reason (<paramref name="rejected" />). Like an open composite, an unkeyed open decorator that matches no closing
	///     decorates nothing; one the container declares itself warns AWT249 (see
	///     <see cref="ReportOpenContributionWithoutClosing" />). It also closes over each declared closed import of its
	///     service, under every key it selects, since the host decides the keys; the closings of an open import are known
	///     only from the dependencies, so an open decorator over one reports AWT123.
	/// </summary>
	private static void ExpandOpenDecorators(
		List<DecorateRegistration> decorators,
		List<OpenDecorateRegistration> open,
		List<(string Display, INamedTypeSymbol Symbol)> closedServices,
		CoalescedGraph graph,
		HashSet<string> rejected,
		ExternalServiceTypes external,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics)
	{
		foreach (OpenDecorateRegistration decorator in open)
		{
			List<INamedTypeSymbol> closings = MatchingClosings(decorator.Service, closedServices,
				display => decorator.Keys is not { } keys
					? PresentUnderAnyKey(graph, display)
					: keys.Any(key => PresentUnderKey(graph, display, key)));
			List<INamedTypeSymbol> importedClosings = MatchingClosings(decorator.Service,
				external.ClosedGenerics.Select(service => (service.ToDisplayString(FullyQualified), service)).ToList(),
				display => external.Closed.Contains(display) && !PresentUnderAnyKey(graph, display))
				.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToList();
			bool openImport = external.Open.Contains(UnboundDisplay(decorator.Service));
			if (openImport)
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.DecoratedOpenImport,
					LocationInfo.From(decorator.Location),
					new EquatableArray<string>([Display(decorator.Service.ToDisplayString(FullyQualified)), Display(decorator.Decorator.ToDisplayString(FullyQualified)),])));
			}

			if (importedClosings.Count == 0)
			{
				ReportUnmatchedOpenDecorator(decorator, closings, graph, rejected, diagnostics);
			}

			if (decorator is { Owner: null, Inherited: false, DecoratesHandles: false, }
			    && (decorator.Keys is null || (decorator.Keys.Count == 1 && decorator.Keys[0] is null))
			    && closings.Count == 0 && importedClosings.Count == 0 && !openImport)
			{
				ReportOpenContributionWithoutClosing("decorator", decorator.Decorator, decorator.Service, decorator.Location, rejected, diagnostics);
			}

			foreach (INamedTypeSymbol closed in closings.Concat(importedClosings))
			{
				ITypeSymbol[] typeArguments = closed.TypeArguments.ToArray();

				// AWT171: the closing's type arguments must satisfy the decorator's type-parameter constraints.
				if (!ConstraintsSatisfied(decorator.Decorator, typeArguments, compilation))
				{
					ReportClosingConstraintViolation(decorator.Decorator, typeArguments, decorator.Location, diagnostics);
					continue;
				}

				// Only the keys this closing is registered under, so a key another closing lacks is no AWT210 here. An
				// imported closing takes every key the decorator selects.
				string display = closed.ToDisplayString(FullyQualified);
				IReadOnlyList<string?>? keys = importedClosings.Contains(closed, SymbolEqualityComparer.Default)
					? decorator.Keys
					: decorator.Keys?.Where(key => PresentUnderKey(graph, display, key)).ToList();
				INamedTypeSymbol closedDecorator = decorator.Decorator.Construct(typeArguments);
				decorators.Add(new DecorateRegistration(
					display,
					closed,
					closedDecorator,
					decorator.Order,
					decorator.DeclarationOrder,
					decorator.Location,
					keys,
					decorator.Inherited,
					decorator.Via?.Close(typeArguments, closedDecorator, closed),
					decorator.Owner,
					When: decorator.When));
			}
		}
	}

	/// <summary>
	///     Reports AWT249 for an unkeyed open <paramref name="kind" /> the container declares itself that applies to no
	///     closing of <paramref name="service" />, unless a closing of it was dropped for a reported reason
	///     (<paramref name="rejected" />), which explains it. The analyzer reports it at the attribute; one a module or an
	///     extended base declares is left alone, since its consumers register the closings.
	/// </summary>
	private static void ReportOpenContributionWithoutClosing(string kind, INamedTypeSymbol type, INamedTypeSymbol service, Location? location, HashSet<string> rejected, List<DiagnosticInfo> diagnostics)
	{
		if (!rejected.Any(entry => NamesClosingOf(entry, service)))
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.OpenContributionWithoutClosing,
				LocationInfo.From(location),
				new EquatableArray<string>([kind, Display(type.ToDisplayString(FullyQualified)), Display(service.ToDisplayString(FullyQualified)),])));
		}
	}

	/// <summary>
	///     Records that the registration of <paramref name="service" /> under <paramref name="key" /> was dropped for a
	///     reported reason: the service, which the checks of missing dependencies look up, and the service with its key,
	///     which an open decorator selecting that key looks up (see <see cref="RejectedClosingUnderKey" />). No fully
	///     qualified type name contains the separator, so the two never collide.
	/// </summary>
	private static void RecordRejected(HashSet<string> rejected, string service, string? key)
	{
		rejected.Add(service);
		rejected.Add($"{service}{RejectedKeySeparator}{key ?? RejectedUnkeyed}");
	}

	private const char RejectedKeySeparator = '\0';

	private const string RejectedUnkeyed = "\u0001";

	/// <summary>
	///     Whether the <paramref name="entry" /> of <see cref="RecordRejected" /> names a closing of
	///     <paramref name="open" /> dropped under <paramref name="key" />.
	/// </summary>
	private static bool RejectedClosingUnderKey(string entry, INamedTypeSymbol open, string? key)
	{
		int separator = entry.IndexOf(RejectedKeySeparator);
		return separator >= 0
		       && string.Equals(entry.Substring(separator + 1), key ?? RejectedUnkeyed, StringComparison.Ordinal)
		       && NamesClosingOf(entry.Substring(0, separator), open);
	}

	/// <summary>
	///     Whether the fully qualified <paramref name="closed" /> names a closing of the generic definition of
	///     <paramref name="open" />, with as many type arguments. A definition nested in a generic type is never matched.
	/// </summary>
	private static bool NamesClosingOf(string closed, INamedTypeSymbol open)
	{
		string definition = open.OriginalDefinition.ToDisplayString(FullyQualified);
		int bracket = definition.IndexOf('<');
		if (bracket < 0 || open.ContainingType is { IsGenericType: true, } || !closed.StartsWith(definition.Substring(0, bracket + 1), StringComparison.Ordinal) || closed[closed.Length - 1] != '>')
		{
			return false;
		}

		int depth = 0;
		int arguments = 1;
		for (int index = bracket + 1; index < closed.Length - 1; index++)
		{
			switch (closed[index])
			{
				case '<' or '(' or '[':
					depth++;
					break;
				case '>' or ')' or ']':
					if (--depth < 0)
					{
						return false;
					}

					break;
				case ',' when depth == 0:
					arguments++;
					break;
			}
		}

		return depth == 0 && arguments == open.Arity;
	}

	/// <summary>
	///     Reports AWT210 for each key an open decorator selects that none of its <paramref name="closings" /> is
	///     registered under, unless a closing of its service was dropped under that key for a reported reason (see
	///     <see cref="RecordRejected" />).
	/// </summary>
	private static void ReportUnmatchedOpenDecorator(
		OpenDecorateRegistration decorator,
		List<INamedTypeSymbol> closings,
		CoalescedGraph graph,
		HashSet<string> rejected,
		List<DiagnosticInfo> diagnostics)
	{
		// AWT210: a selected key that no closing of the service is registered under.
		if (decorator.Keys is { } selected && !(selected.Count == 1 && selected[0] is null))
		{
			foreach (string? key in selected.Where(key => !closings.Any(closed => PresentUnderKey(graph, closed.ToDisplayString(FullyQualified), key))
			                                             && !rejected.Any(entry => RejectedClosingUnderKey(entry, decorator.Service, key))))
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.DecoratedKeyNotRegistered,
					LocationInfo.From(decorator.Location),
					new EquatableArray<string>([
						Display(decorator.Service.ToDisplayString(FullyQualified)),
						Display(decorator.Decorator.ToDisplayString(FullyQualified)),
						key is null ? "null" : KeyDisplay(key),
					])));
			}
		}
	}

	/// <summary>
	///     Expands each open generic <c>[Composite(typeof(C&lt;&gt;), typeof(IService&lt;&gt;))]</c> into a closed
	///     <see cref="CompositeRegistration" /> for every closing of its service present in the coalesced graph under the
	///     composite's key, constructing the matching closing of the composite. Each synthesized composite then follows
	///     the closed <c>[Composite]</c> rules in <see cref="BuildComposites" />. Warns AWT171 and skips a closing whose
	///     type arguments violate the composite's constraints; reports AWT212 for a key that no closing has, and warns
	///     AWT249 for an unkeyed one the container declares itself that has no closing (see
	///     <see cref="ReportOpenContributionWithoutClosing" />).
	/// </summary>
	private static void ExpandOpenComposites(
		List<CompositeRegistration> composites,
		List<OpenCompositeRegistration> open,
		List<(string Display, INamedTypeSymbol Symbol)> closedServices,
		CoalescedGraph graph,
		HashSet<string> rejected,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics)
	{
		foreach (OpenCompositeRegistration composite in open)
		{
			List<INamedTypeSymbol> closings = MatchingClosings(composite.Service, closedServices,
				display => PresentUnderKey(graph, display, composite.Key));

			// AWT212: a keyed open composite whose key no closing of the service is registered under.
			if (composite.Key is not null && closings.Count == 0 && !composite.ComposesHandles)
			{
				ReportCompositeKeyNotRegistered(
					composite.Composite.ToDisplayString(FullyQualified), composite.Service.ToDisplayString(FullyQualified),
					composite.Key, composite.Location, diagnostics);
				continue;
			}

			if (composite is { Key: null, Owner: null, Inherited: false, ComposesHandles: false, } && closings.Count == 0)
			{
				ReportOpenContributionWithoutClosing("composite", composite.Composite, composite.Service, composite.Location, rejected, diagnostics);
			}

			foreach (INamedTypeSymbol closed in closings)
			{
				ITypeSymbol[] typeArguments = closed.TypeArguments.ToArray();

				// AWT171: the closing's type arguments must satisfy the composite's type-parameter constraints.
				if (!ConstraintsSatisfied(composite.Composite, typeArguments, compilation))
				{
					ReportClosingConstraintViolation(composite.Composite, typeArguments, composite.Location, diagnostics);
					continue;
				}

				INamedTypeSymbol closedComposite = composite.Composite.Construct(typeArguments);
				composites.Add(new CompositeRegistration(
					closed.ToDisplayString(FullyQualified),
					closed,
					closedComposite,
					composite.Lifetime,
					composite.Location,
					composite.Key,
					composite.Via?.Close(typeArguments, closedComposite, closed),
					composite.Owner));
			}
		}
	}

	/// <summary>
	///     The closed generic service symbols whose open form is <paramref name="openService" /> and which
	///     <paramref name="present" /> finds in the coalesced graph, in first-seen registration order. Only the keys
	///     the decorator/composite selects count, matching the closed forms: a closing registered only under other
	///     keys is left alone rather than reported.
	/// </summary>
	private static List<INamedTypeSymbol> MatchingClosings(
		INamedTypeSymbol openService,
		List<(string Display, INamedTypeSymbol Symbol)> closedServices,
		Func<string, bool> present)
	{
		List<INamedTypeSymbol> closings = new();
		foreach ((string display, INamedTypeSymbol symbol) in closedServices)
		{
			if (symbol is { IsGenericType: true, }
			    && SymbolEqualityComparer.Default.Equals(symbol.ConstructedFrom, openService)
			    && present(display))
			{
				closings.Add(symbol);
			}
		}

		return closings;
	}

	/// <summary>
	///     Whether the coalesced graph holds <paramref name="service" /> under <paramref name="key" /> (the unkeyed
	///     resolution key when <see langword="null" />), either as the single-dispatch winner or as a non-empty
	///     collection membership.
	/// </summary>
	private static bool PresentUnderKey(CoalescedGraph graph, string service, string? key)
	{
		ServiceKey serviceKey = new(service, key);
		return graph.ServiceToImpl.ContainsKey(serviceKey)
		       || (graph.ServiceMembers.TryGetValue(serviceKey, out List<string>? members) && members.Count > 0);
	}

	/// <summary>Whether the coalesced graph holds <paramref name="service" /> unkeyed or under any user key.</summary>
	private static bool PresentUnderAnyKey(CoalescedGraph graph, string service)
		=> PresentUnderKey(graph, service, null)
		   || graph.ServiceToImpl.Keys.Any(key => key.Service == service && IsUserKey(key.Key));

	/// <summary>
	///     Reports AWT171 (a warning) for a closing (of an open decorator or composite) whose type arguments violate
	///     the open definition's type-parameter constraints, naming the would-be closed type and the open definition.
	///     Unlike the open generic registration's AWT126 error, the base service still resolves - only this closing is
	///     left as-is - so a broadly-applied decorator/composite whose constraints exclude some closings degrades
	///     gracefully instead of failing the whole compilation.
	/// </summary>
	private static void ReportClosingConstraintViolation(
		INamedTypeSymbol openDefinition,
		ITypeSymbol[] typeArguments,
		Location? location,
		List<DiagnosticInfo> diagnostics)
		=> diagnostics.Add(new DiagnosticInfo(
			Diagnostics.DecoratorClosingConstraintViolation,
			LocationInfo.From(location),
			new EquatableArray<string>([
				Display(openDefinition.Construct(typeArguments).ToDisplayString(FullyQualified)),
				Display(openDefinition.ToDisplayString(FullyQualified)),
			])));

	/// <summary>
	///     The distinct closed generic service symbols across the raw registrations (explicit, scanned or expanded
	///     from open generic registrations), in first-seen order, deduped by display string. The symbol source for
	///     open decorator/composite expansion, since the coalesced graph keys its services only by string.
	/// </summary>
	private static List<(string Display, INamedTypeSymbol Symbol)> ClosedGenericServices(List<RawRegistration> raw)
	{
		List<(string, INamedTypeSymbol)> closedServices = new();
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (RawRegistration registration in raw)
		{
			if (registration.ServiceSymbol is { IsGenericType: true, IsUnboundGenericType: false, } closed
			    && seen.Add(registration.ServiceType))
			{
				closedServices.Add((registration.ServiceType, closed));
			}
		}

		return closedServices;
	}
}
