using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     The marker inside a self-compiled module-scan match's synthetic implementation identity
	///     (<c>&lt;service&gt;@scan:&lt;qualified factory&gt;</c>, see <see cref="CollectGeneratedScanRegistration" />).
	///     <c>DisplayInstance</c> keys off it to trim the synthetic suffix from diagnostics.
	/// </summary>
	private const string ScanKeyMarker = "@scan:";

	private static (List<RawRegistration> Raw, HashSet<string> ConstraintRejectedServices, Dictionary<ServiceKey, List<DiagnosticInfo>> DeferredRejections, List<OpenRegistration> Open, Dictionary<string, string> ClosingFaults) Collect(
		INamedTypeSymbol containerSymbol,
		List<ImportedModule> modules,
		Compilation compilation,
		ExternalSurface external,
		Dictionary<string, List<InjectPropertyEntry>> injectProperties,
		Inheritance? inheritance,
		WrapperRegistrations wrappers,
		List<DiagnosticInfo> diagnostics)
	{
		List<RawRegistration> result = new();

		// Closed services an open registration could not produce because the closed type arguments violate the
		// implementation's constraints (AWT126). The emitter suppresses the AWT101 these would also raise.
		HashSet<string> constraintRejected = new(StringComparer.Ordinal);
		Dictionary<ServiceKey, List<DiagnosticInfo>> deferredRejections = new();

		// Open generic registrations are kept apart: not instances themselves, but templates expanded into
		// concrete closed registrations on demand (see ExpandOpenGenerics).
		List<OpenRegistration> open = new();

		SameCompilationModules sameCompilationModules = new(compilation);
		CollectLifetimeRegistrations(new LifetimeCollectionContext(containerSymbol, null, null, compilation, open, diagnostics, sameCompilationModules, Container: containerSymbol), result);

		// The container's own registrations, which replace the inherited ones of an extended base (see ReplaceInherited).
		List<RawRegistration> own = inheritance is null ? [] : new(result);
		List<OpenRegistration> ownOpen = inheritance is null ? [] : new(open);

		// [Import(typeof(Module))] pulls a module's registrations in after the container's own, so the container
		// wins ties and a module's overridable defaults (Fallback.Warn/Silent) only fill the gaps it leaves. Resolved
		// one level deep; a module's own [Import] is not followed. An extended base and its imports come first among
		// the modules (see InheritBase), read like modules qualified on their own type; what is wrong with one of their
		// attributes is the base's to report. A same-compilation module's [GeneratedScanRegistration] attributes are
		// skipped: the generator itself never sees them (its own output), but an analyzer running over the
		// post-generation compilation does, and collecting them alongside the direct scan expansion below would
		// double every match in its graph.
		foreach (ImportedModule module in modules)
		{
			bool sameCompilation = SymbolEqualityComparer.Default.Equals(module.Symbol.ContainingAssembly, containerSymbol.ContainingAssembly);
			List<DiagnosticInfo> moduleDiagnostics = module.Inherited ? new List<DiagnosticInfo>() : diagnostics;
			CollectLifetimeRegistrations(
				new LifetimeCollectionContext(module.Symbol, module.Symbol, module.ImportLocation, compilation, open, moduleDiagnostics, sameCompilationModules, module.Inherited, inheritance, modules, containerSymbol),
				result,
				skipGeneratedScanRegistrations: sameCompilation);

			// The base sees the [Inject] property of a type this container cannot build without the module's factory, and
			// an EagerWhen predicate is reported like the When of an inherited decorator.
			if (module.Inherited)
			{
				diagnostics.AddRange(moduleDiagnostics.Where(diagnostic => diagnostic.Descriptor == Diagnostics.ModuleRegistrationNotSelfCompiled || diagnostic.Descriptor == Diagnostics.ModuleRegistrationPropertyNotSet || diagnostic.Descriptor == Diagnostics.InheritedPrivateImplementation
				                                                           || diagnostic.Descriptor == Diagnostics.InvalidDecoratorPredicate || diagnostic.Descriptor == Diagnostics.InaccessibleDecoratorPredicate));
			}
		}

		// The container's own scan matches must outrank a module's within the scan tier (the container wins ties
		// there like everywhere else), so the module-collected [GeneratedScanRegistration] matches are pulled out
		// and re-added after the container's scan expansion; the same-compilation expansions below append later
		// still, giving both module forms the same rank relative to the container's own scans.
		List<RawRegistration> moduleScanRegistrations = result.FindAll(registration => registration.IsScan);
		result.RemoveAll(registration => registration.IsScan);

		// Assembly scanning contributes overridable registrations for every concrete type assignable to a
		// [Scan] marker. Appended before open generic expansion so scanned implementations seed it: their
		// constructors may require closed generics only an open registration can provide.
		List<RawRegistration> scans = CollectScans(containerSymbol, compilation, diagnostics);
		result.AddRange(scans);

		// An extended base's [Scan] keeps its container semantics: it sweeps the base's own assembly and matches what
		// the base sees, so a match the extending container cannot access is reported below rather than silently
		// missing. It ranks after the container's own scans and is owned by the base, so its hooks bind there.
		if (inheritance is not null)
		{
			result.AddRange(CollectScans(inheritance.Base, compilation, new List<DiagnosticInfo>())
				.Select(registration => registration with { Origin = inheritance.Base, }));
		}

		result.AddRange(moduleScanRegistrations);

		CollectSameCompilationModuleScans(containerSymbol, modules, sameCompilationModules, compilation, result);

		if (inheritance is not null)
		{
			ReplaceInherited(inheritance, own, ownOpen, result, open);
			ReportFallbackInherited(containerSymbol, own, diagnostics);
			BuildThroughBaseFactories(inheritance, result, constraintRejected, diagnostics);
			ReportUnbuildableInheritedOpen(inheritance, open, diagnostics);
		}

		// Before the expansion, so the dependencies of a closing a module builds for the container seed it too.
		Dictionary<string, string> closingFaults = RegisterClosings(result, open, modules, containerSymbol, compilation, external, wrappers);

		// Expand open generic registrations: for every closed generic service required from the graph
		// whose open form is registered but which has no concrete registration, synthesize the closed
		// implementation (iterating to a fixpoint over its own generic dependencies).
		if (open.Count > 0)
		{
			ExpandOpenGenerics(result, open, new ExpansionInputs(containerSymbol, compilation, external, injectProperties, wrappers), diagnostics, constraintRejected, deferredRejections);
			ShareOwnerFactoryInstances(result, compilation, constraintRejected, diagnostics);
		}

		if (inheritance is not null)
		{
			DropInaccessibleInherited(inheritance, result, open, compilation, constraintRejected, diagnostics);
		}

		// ...then moved back to the end: coalescing is first-wins per service, so the explicit registrations and
		// the closed registrations expanded from them must precede the overridable scan ones: the container's own
		// [Scan] matches and a module's self-compiled [GeneratedScanRegistration] matches alike, both IsScan.
		List<RawRegistration> scanMatches = result.FindAll(registration => registration.IsScan);
		if (scanMatches.Count > 0)
		{
			result.RemoveAll(registration => registration.IsScan);
			result.AddRange(scanMatches);
		}

		return (result, constraintRejected, deferredRejections, open, closingFaults);
	}

	/// <summary>
	///     Each unkeyed open generic service definition (<c>global::Ns.IRepository&lt;&gt;</c>) mapped to the lifetime
	///     and implementation definition of its first registration, the implementation <see langword="null" /> for a
	///     generic factory, so <see cref="AwaitenAnalyzer" /> can name the explicit closed registration a closing the
	///     graph never asks for needs (AWT218).
	/// </summary>
	private static Dictionary<string, (Lifetime Lifetime, string? Implementation)> OpenServices(List<OpenRegistration> open)
	{
		Dictionary<string, (Lifetime, string?)> services = new(StringComparer.Ordinal);
		foreach (OpenRegistration registration in open.Where(registration => registration.Key is null))
		{
			string service = UnboundDisplay(registration.Service);
			if (!services.ContainsKey(service))
			{
				services[service] = (registration.Lifetime, registration.Factory is null ? UnboundDisplay(registration.Implementation) : null);
			}
		}

		return services;
	}

	/// <summary>
	///     Expands a same-compilation module's <c>[Scan]</c> into <paramref name="result" />: the generator cannot
	///     see its own output, so the module's self-compiled <c>[GeneratedScanRegistration]</c> attributes are
	///     invisible within the compilation that declares the module. The expansion runs the module pipeline's own
	///     logic (<see cref="CollectModuleScanFactories" />), so a scan means exactly the same thing wherever the
	///     module lives - one accessible exposure per match, matches the module build skipped (AWT139, AWT182, AWT196) stay skipped,
	///     and construction goes through the greediest accessible constructor, the one the generated factory mirrors -
	///     except that with no assembly boundary the container constructs the match directly instead of through the
	///     factory it cannot resolve. A match hidden in the module it cannot construct either, so it calls the
	///     factory the module generates in the same run, by its deterministic name, and runs the hooks through the
	///     generated wrappers; a module that cannot receive the factory (AWT194) contributes no such match. A match the
	///     module generates no factory for, or builds through an instance handle, is registered under every service its
	///     scans expose it as, all constructing the one implementation, so they share its instance. A
	///     referenced-assembly module is excluded: its metadata carries the self-compiled
	///     expansion instead, and re-running its scan here would double-register every match. The expansion's
	///     diagnostics are discarded: the module pipeline (BuildModuleModel) already reports on the same <c>[Scan]</c>
	///     at the same location, and reporting here too would double every scan-level warning.
	/// </summary>
	private static void CollectSameCompilationModuleScans(
		INamedTypeSymbol containerSymbol,
		List<ImportedModule> modules,
		SameCompilationModules sameCompilationModules,
		Compilation compilation,
		List<RawRegistration> result)
	{
		foreach (INamedTypeSymbol moduleSymbol in modules.Where(module => !module.IsBase).Select(module => module.Symbol))
		{
			if (!SymbolEqualityComparer.Default.Equals(moduleSymbol.ContainingAssembly, containerSymbol.ContainingAssembly))
			{
				continue;
			}

			foreach (ModuleScanExpansion expansion in sameCompilationModules.Scans(moduleSymbol))
			{
				if ((expansion.SkippedServices ?? expansion.Exposures) is { } services)
				{
					result.AddRange(services.Select(service => SameCompilationScanRegistration(expansion, moduleSymbol) with
					{
						ServiceType = service.ToDisplayString(FullyQualified),
						ServiceSymbol = service,
					}));
				}
				else if (!IsHiddenInModule(expansion.Implementation, moduleSymbol, compilation))
				{
					result.Add(SameCompilationScanRegistration(expansion, moduleSymbol));
				}
				else if (ReceivesGeneratedMembers(moduleSymbol))
				{
					result.Add(SameCompilationScanRegistration(expansion, moduleSymbol) with
					{
						ConstructVia = $"{moduleSymbol.ToDisplayString(FullyQualified)}.{InInternals(expansion.Factory.FactoryName)}",
						ConstructViaType = expansion.Factory.ServiceType,
					});
				}
			}
		}
	}

	/// <summary>
	///     The registration one same-compilation module-scan expansion contributes (see
	///     <see cref="CollectSameCompilationModuleScans" />). With no assembly boundary the container binds the
	///     module's own (possibly internal) hook directly rather than the generated wrapper it cannot see, so each
	///     hook resolves against the module - the coalescer keeps the origin per hook slot - through the ordinary
	///     pipeline. The user hook names travel on the expansion, but a slot whose hook failed to resolve keeps its
	///     claimed name with no wrapper on the factory (see <c>MergeModuleScanHookSlot</c>), so wiring is gated on
	///     the wrapper: an invalid hook - already reported at the module's build - is not wired and re-reported
	///     here. Each wired slot's closed marker forms bind a generic hook's type argument.
	/// </summary>
	private static RawRegistration SameCompilationScanRegistration(ModuleScanExpansion expansion, INamedTypeSymbol moduleSymbol)
		=> new(
			expansion.Factory.ServiceType,
			expansion.Factory.ImplementationType,
			expansion.Factory.Lifetime,
			expansion.Implementation,
			expansion.Location,
			ServiceSymbol: expansion.Service,
			IsScan: true,
			ScanSkipsUnconstructable: expansion.Factory.SkipUnconstructable,
			GreedyConstructor: true,
			Origin: moduleSymbol,
			OnActivated: expansion.Factory.OnActivated is null ? null : expansion.OnActivated,
			OnRelease: expansion.Factory.OnRelease is null ? null : expansion.OnRelease,
			OnActivatedMarkers: expansion.Factory.OnActivated is null ? null : expansion.OnActivatedMarkers,
			OnReleaseMarkers: expansion.Factory.OnRelease is null ? null : expansion.OnReleaseMarkers);

	/// <summary>
	///     The state shared while reading the lifetime registrations of one container or imported module.
	///     <see cref="Owner" /> declares the attributes. <see cref="Origin" /> is the imported module being read
	///     (<see langword="null" /> for the container), recorded so a module's <c>Factory</c>/<c>Instance</c> member
	///     resolves against the module. <see cref="FallbackLocation" /> is the container's <c>[Import]</c> location,
	///     used for a referenced-assembly module whose attributes have no syntax. The open generic <c>typeof</c>-form
	///     registrations go into <see cref="Open" />. <see cref="Inherited" /> marks a module or base inherited through
	///     <c>Extends</c>, whose inaccessible declarations the inheritance reports (AWT226). <see cref="Modules" /> holds
	///     what the modules of the container's own assembly generate. <see cref="Imports" /> are the modules the container
	///     imports, which decide how a module of another assembly is built through its handles (see <see cref="ThroughHandle" />).
	/// </summary>
	private readonly record struct LifetimeCollectionContext(
		INamedTypeSymbol Owner,
		INamedTypeSymbol? Origin,
		Location? FallbackLocation,
		Compilation Compilation,
		List<OpenRegistration> Open,
		List<DiagnosticInfo> Diagnostics,
		SameCompilationModules Modules,
		bool Inherited = false,
		Inheritance? Inheritance = null,
		List<ImportedModule>? Imports = null,
		INamedTypeSymbol? Container = null);

	/// <summary>
	///     Reads the <c>[Singleton]</c>/<c>[Transient]</c>/<c>[Scoped]</c> registrations declared on the context's
	///     owner (a container or imported module) into <paramref name="result" />, and the open generic
	///     <c>typeof</c>-form ones into the context's open registrations; a closed <c>typeof</c> pair registers like
	///     the generic form. A <c>Factory</c> or <c>Instance</c> registration of an imported module in another assembly
	///     whose service and member this container cannot reach, and which the module builds through no handle, is
	///     dropped like a constructed one (see <see cref="SelfCompiledRegistration" />).
	/// </summary>
	private static void CollectLifetimeRegistrations(
		LifetimeCollectionContext context,
		List<RawRegistration> result,
		bool skipGeneratedScanRegistrations = false)
	{
		INamedTypeSymbol? origin = context.Origin;
		Location? fallbackLocation = context.FallbackLocation;
		List<DiagnosticInfo> diagnostics = context.Diagnostics;
		bool reportedNotSelfCompiled = false;
		HashSet<string> implicitHandles = new(StringComparer.Ordinal);
		bool fromModule = origin is not null && HasAwaitenAttribute(context.Owner.GetAttributes(), "ModuleAttribute");
		foreach (AttributeData attribute in context.Owner.GetAttributes())
		{
			if (attribute.AttributeClass is not { } attributeClass)
			{
				continue;
			}

			// A [GeneratedScanRegistration<TService>(factory, Lifetime = …)] is what a [Module] self-compiled from
			// its own [Scan] (one per match), read back and consumed there.
			if (CollectGeneratedScanRegistration(attribute, attributeClass, result, context, skipGeneratedScanRegistrations))
			{
				continue;
			}

			if (attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace)
			{
				continue;
			}

			Lifetime? lifetime = attributeClass.Name switch
			{
				"SingletonAttribute" => Lifetime.Singleton,
				"TransientAttribute" => Lifetime.Transient,
				"ScopedAttribute" => Lifetime.Scoped,
				_ => null,
			};
			if (lifetime is null)
			{
				continue;
			}

			if (!TryReadRegistrationTypes(attribute, attributeClass, lifetime.Value, context, out ITypeSymbol? implementation, out ITypeSymbol? service))
			{
				continue;
			}

			(ProductionKind production, string? productionMember, bool conflictingDirectives) =
				ReadProduction(attribute);

			// Both overridable-default modes only fill a gap, so both are weak and yield to a stronger or earlier
			// registration. The Warn mode additionally opts into the AWT148 ambiguity warning when two collide
			// with nothing stronger to resolve them; None is a normal strong registration.
			int fallback = NamedFallback(attribute);
			bool weak = fallback != 0;
			bool isDefault = fallback == 1;

			string? key = NamedKeyArgument(attribute);

			// A [Singleton<…>(WhenInjectedInto = typeof(Consumer))] contextual binding, resolved to the consumer's
			// context key.
			string? whenInjectedInto = NamedTypeArgument(attribute, "WhenInjectedInto", context.Compilation);

			Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallbackLocation;

			ReportContextualBindingWithKey(key, whenInjectedInto, implementation, location, diagnostics);
			ReportUnsupportedRegistrationKey(attribute, LocationInfo.From(location), Display(implementation.ToDisplayString(FullyQualified)), diagnostics);

			// EagerWhen with Eager = true is AWT263; the singleton stays eager, which the predicate could only narrow.
			DecoratorPredicate? eagerWhen = null;
			if (NamedFlag(attribute, "Eager") && NamedArgument(attribute, "EagerWhen") is not null)
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.EagerWithEagerWhen,
					LocationInfo.From(location),
					new EquatableArray<string>([Display(implementation.ToDisplayString(FullyQualified)),])));
			}
			else
			{
				TryReadPredicate(attribute, context.Owner, context.Container ?? context.Owner, context.Compilation, location, diagnostics, out eagerWhen);
			}

			RawRegistration registration = new(
				service.ToDisplayString(FullyQualified),
				TypeIdentity((INamedTypeSymbol)implementation, context.Compilation),
				lifetime.Value,
				(INamedTypeSymbol)implementation,
				location,
				production,
				productionMember,
				conflictingDirectives,
				key,
				service as INamedTypeSymbol,
				Weak: weak,
				IsDefault: isDefault,
				Origin: origin,
				// Eager is exposed on [Singleton] alone; a Transient/Scoped attribute has no such property, so
				// this reads false there. BuildInstance honors it only for a singleton lifetime.
				Eager: NamedFlag(attribute, "Eager"),
				OnActivated: NamedArgument(attribute, "OnActivated"),
				OnRelease: NamedArgument(attribute, "OnRelease"),
				SuppressDisposal: NamedFlag(attribute, "SuppressDisposal"),
				WhenInjectedInto: whenInjectedInto,
				ConsumerOwned: NamedTypeArrayArgument(attribute, "ConsumerOwned"),
				EagerWhen: eagerWhen);

			if (fromModule && !conflictingDirectives && !skipGeneratedScanRegistrations
			    && ThroughHandle(registration, context.Compilation, implicitHandles, context.Imports ?? []) is { } handled)
			{
				result.AddRange(handled);
				continue;
			}

			if (fromModule && !conflictingDirectives && !skipGeneratedScanRegistrations && !context.Inherited
			    && productionMember is not null
			    && service is INamedTypeSymbol unnameable
			    && !context.Compilation.IsSymbolAccessibleWithin(unnameable, context.Compilation.Assembly)
			    && !ReachesProductionMember(context.Owner, production, productionMember, registration.Implementation, context.Compilation.Assembly, context.Compilation))
			{
				ReportStartedSingletonWithoutHandle(registration, context, unnameable);
				continue;
			}

			RawRegistration? collected = fromModule && production == ProductionKind.Constructor && !conflictingDirectives
				? SelfCompiledRegistration(registration, context, skipGeneratedScanRegistrations, ref reportedNotSelfCompiled)
				: WithCallableHooks(ThroughMemberWrapper(registration, origin, context.Compilation, !skipGeneratedScanRegistrations), context, fromModule && !skipGeneratedScanRegistrations);
			if (collected is not null)
			{
				result.Add(collected);
			}
		}
	}

	/// <summary>
	///     Applies self-compilation (see <see cref="SelfCompilesRegistration" />) to a constructed registration read
	///     off an imported module. A same-compilation container constructs the implementation directly, through the
	///     greediest constructor the module's generated factory mirrors, so the module registers the same thing
	///     wherever it lives. A container in another assembly that constructs the implementation itself runs a hook it
	///     cannot reach through the module's generated wrapper (see <see cref="ThroughMemberWrapper" />), or reports why
	///     the module generated none (see <see cref="WithCallableHooks" />). An implementation hidden in the module it cannot construct either, so it calls
	///     the factory the module generates in the same run, found by its deterministic name, and runs the hooks through
	///     the generated wrappers; a hook the module's own build rejected (and reports) is dropped. A container in
	///     another assembly that cannot construct the implementation calls the generated factory instead and runs the
	///     generated hook wrappers, keeping every other option of the registration, and reads the implementation's
	///     traits off its metadata, so it initializes, disposes and shares it exactly as it would a type it constructs.
	///     So does a container the module's assembly grants no <c>InternalsVisibleTo</c> for an implementation it could
	///     construct, but for which the module generated a factory or recorded a skip: the implementation has an
	///     <c>[Inject]</c> property this container does not see. A registration whose service this container cannot
	///     name is dropped, also of an implementation it could construct: nothing here can resolve or depend on it (a
	///     self-compiled constructor parameter is accessible, AWT195). Returns <see langword="null" /> for a dropped registration, after reporting
	///     AWT231 once per implementation when the module recorded why it generated no factory for it or cannot carry
	///     one (see <see cref="ModuleShapeReason" />), else AWT154
	///     once per module when the module carries no generated factory for it, unless another import builds it (see
	///     <see cref="BuiltByAnotherImport" />). A module inherited through
	///     <c>Extends</c> keeps such a registration instead, which the inheritance reports as inaccessible (AWT226),
	///     except one of an implementation with an <c>[Inject]</c> property this container does not see, which it builds
	///     through the factory like an imported module's or reports with AWT231.
	/// </summary>
	private static RawRegistration? SelfCompiledRegistration(
		RawRegistration registration,
		LifetimeCollectionContext context,
		bool sameCompilation,
		ref bool reportedNotSelfCompiled)
	{
		INamedTypeSymbol module = registration.Origin!;
		INamedTypeSymbol implementation = registration.Implementation;
		Compilation compilation = context.Compilation;

		// A container that the module's assembly grants no InternalsVisibleTo does not see an internal [Inject] property
		// in metadata, so it cannot tell that the module self-compiles the implementation, but finds what the module generated for it.
		bool hiddenMembers = !sameCompilation
		                     && !module.ContainingAssembly.GivesAccessTo(compilation.Assembly)
		                     && (GeneratedFactory(module, implementation, compilation) is not null || SkippedRegistrationReason(module, implementation) is not null);
		if (registration.ServiceSymbol is not { } service || (!SelfCompilesRegistration(module, implementation, service, compilation) && !hiddenMembers))
		{
			if (!sameCompilation
			    && !context.Inherited
			    && registration.ServiceSymbol is { } inaccessible
			    && !compilation.IsSymbolAccessibleWithin(inaccessible, compilation.Assembly)
			    && implementation is { TypeKind: TypeKind.Class, IsAbstract: false, })
			{
				ReportStartedSingletonWithoutHandle(registration, context, inaccessible);
				return null;
			}

			return WithCallableHooks(ThroughMemberWrapper(registration, module, compilation, !sameCompilation), context, !sameCompilation);
		}

		if (sameCompilation && IsHiddenInModule(implementation, module, compilation))
		{
			return context.Modules.Registration(module, implementation) is { } generated
				? ThroughSameCompilationFactory(registration, module, generated) with { GreedyConstructor = true, }
				: null;
		}

		if (sameCompilation)
		{
			return registration with { GreedyConstructor = true, };
		}

		if (CanConstruct(implementation, compilation) && !hiddenMembers)
		{
			return registration;
		}

		// A container granted InternalsVisibleTo builds the internal services itself and registers no handles, so it
		// calls the internal factory that takes them directly, which the module generates when the public one takes handles.
		if ((GeneratedFactoryNamed(module, InternalRegistrationFactoryName(implementation), compilation) ?? GeneratedFactory(module, implementation, compilation)) is not { } factory)
		{
			if ((context.Inherited && !hiddenMembers) || BuiltByAnotherImport(registration, context))
			{
				return registration;
			}

			if (SkippedRegistrationReason(module, implementation) is { } reason)
			{
				if (hiddenMembers && CanConstruct(implementation, compilation))
				{
					ReportNotSelfCompiled(context, module, implementation, "factory", reason, $"make the [Inject] properties of '{DisplayInstance(registration.ImplementationType)}', their setters and their types public", Diagnostics.ModuleRegistrationPropertyNotSet);
				}
				else
				{
					ReportNotSelfCompiled(context, module, implementation, "factory", reason, $"make '{DisplayInstance(registration.ImplementationType)}', its constructor and the constructor's parameter types public");
				}
			}
			else if (ModuleShapeReason(module) is { } shape)
			{
				ReportNotSelfCompiled(context, module, implementation, "factory", shape, $"make '{DisplayInstance(registration.ImplementationType)}', its constructor and the constructor's parameter types public");
			}
			else if (!reportedNotSelfCompiled)
			{
				reportedNotSelfCompiled = true;
				context.Diagnostics.Add(new DiagnosticInfo(
					Diagnostics.ModuleScanNotExpanded,
					LocationInfo.From(context.FallbackLocation ?? registration.Location),
					new EquatableArray<string>([
						Display(module.ToDisplayString(FullyQualified)),
						$"registers '{DisplayInstance(registration.ImplementationType)}', which this container cannot construct",
					])));
			}

			return null;
		}

		foreach ((string? hook, bool release) in new[] { (registration.OnActivated, false), (registration.OnRelease, true), })
		{
			if (hook is not null
			    && InternalsMembers(module, RegistrationHookWrapperName(implementation, release, hook)).IsEmpty
			    && SkippedHookReason(module, implementation, release, hook) is { } hookReason)
			{
				if (context.Inherited && !hiddenMembers)
				{
					return registration;
				}

				string slot = release ? "OnRelease" : "OnActivated";
				ReportNotSelfCompiled(context, module, implementation, $"{slot} hook wrapper", hookReason, $"make '{DisplayInstance(registration.ImplementationType)}', its constructor and its {slot} hook public", member: $"the {slot} hook '{hook}'");
				return null;
			}
		}

		return ThroughGeneratedFactory(registration, factory, compilation);
	}

	/// <summary>
	///     Whether another imported module of another assembly generated a registration factory for the implementation of
	///     <paramref name="registration" />, which has no hooks, or a generic factory for its open definition, so
	///     <see cref="ShareGeneratedRegistrationFactories" /> builds it through that factory although its own module
	///     generated none.
	/// </summary>
	private static bool BuiltByAnotherImport(RawRegistration registration, LifetimeCollectionContext context)
		=> registration is { OnActivated: null, OnRelease: null, }
		   && context.Imports is { } imports
		   && imports.Exists(import => !SymbolEqualityComparer.Default.Equals(import.Symbol, registration.Origin)
		                               && !SymbolEqualityComparer.Default.Equals(import.Symbol.ContainingAssembly, context.Compilation.Assembly)
		                               && GeneratedFactoryOrOpen(import.Symbol, registration.Implementation, context.Compilation) is not null);

	/// <summary>
	///     Reports AWT231 for a registration of an imported module in another assembly that this container drops because
	///     it can neither name its internal service nor build it, when it is an eager or async-initialized singleton
	///     that a container starts though nothing depends on it, so dropping it would otherwise go unnoticed. A service
	///     private to the module is the cause whatever the module's assembly grants, since no container can name it.
	/// </summary>
	private static void ReportStartedSingletonWithoutHandle(RawRegistration registration, LifetimeCollectionContext context, INamedTypeSymbol service)
	{
		INamedTypeSymbol module = registration.Origin!;
		Compilation compilation = context.Compilation;
		if (registration.Lifetime == Lifetime.Singleton
		    && (registration.Eager || registration.EagerWhen is not null || TraitsOf(registration.Implementation, DeclaredAsyncInitializers(module, compilation, null)).AsyncInitializers.Count > 0))
		{
			INamedTypeSymbol? hiddenService = TypeHiddenInModule(service, module, compilation);
			DiagnosticInfo diagnostic = new(
				Diagnostics.ModuleRegistrationNotSelfCompiled,
				LocationInfo.From(context.FallbackLocation),
				new EquatableArray<string>([
					Display(module.ToDisplayString(FullyQualified)),
					Display(registration.Implementation.ToDisplayString(FullyQualified)),
					"handle",
					hiddenService is not null
						? $"the service it is registered under, '{Display(service.ToDisplayString(FullyQualified))}', is private to the module, so no container can name it"
						: ModuleShapeReason(module)
						  ?? (GrantsInternalsVisibleTo(module.ContainingAssembly)
							  ? "it leaves the singleton to the assemblies it grants InternalsVisibleTo"
							  : "its build reported an error for the module or ran without the Awaiten generator"),
					NotSelfCompiledRemedy(module, hiddenService ?? (ITypeSymbol)registration.Implementation, compilation, $"make '{Display(registration.ImplementationType)}' and the service it is registered under public"),
				]));
			if (!context.Diagnostics.Contains(diagnostic))
			{
				context.Diagnostics.Add(diagnostic);
			}
		}
	}

	/// <summary>
	///     Reports AWT231 at the consumer's <c>[Import]</c>, once per implementation and missing piece: the imported
	///     module generated no <paramref name="missing" /> for <paramref name="implementation" /> for the recorded
	///     <paramref name="reason" />, and this container cannot construct it either.
	///     <paramref name="descriptor" /> picks the form for a type the container could construct but whose <c>[Inject]</c>
	///     property it does not see.
	/// </summary>
	private static void ReportNotSelfCompiled(LifetimeCollectionContext context, INamedTypeSymbol module, INamedTypeSymbol implementation, string missing, string reason, string remedy, DiagnosticDescriptor? descriptor = null, string? member = null)
	{
		DiagnosticInfo diagnostic = new(
			descriptor ?? Diagnostics.ModuleRegistrationNotSelfCompiled,
			LocationInfo.From(context.FallbackLocation),
			new EquatableArray<string>([
				Display(module.ToDisplayString(FullyQualified)),
				Display(implementation.ToDisplayString(FullyQualified)),
				missing,
				reason,
				NotSelfCompiledRemedy(module, implementation, context.Compilation, remedy, member),
			]));
		if (!context.Diagnostics.Contains(diagnostic))
		{
			context.Diagnostics.Add(diagnostic);
		}
	}

	/// <summary>
	///     The remedy an AWT231 for <paramref name="type" /> of the imported <paramref name="module" /> ends with, chosen by
	///     the cause this container sees. No grant reaches a type private to the module, so that is made internal or
	///     public. A class of a third assembly without a constructor this container can reach needs the grant of that
	///     assembly instead, unless the module produces it through a member (<paramref name="constructed" /> false). A
	///     container whose assembly the module's assembly grants <c>InternalsVisibleTo</c> is told what the
	///     grant does not reach (the type, <paramref name="member" /> or the constructors) before
	///     <paramref name="remedy" />, and any other container is offered the grant beside it.
	/// </summary>
	private static string NotSelfCompiledRemedy(INamedTypeSymbol module, ITypeSymbol type, Compilation compilation, string remedy, string? member = null, bool constructed = true)
	{
		bool granted = module.ContainingAssembly.GivesAccessTo(compilation.Assembly);
		if (TypeHiddenInModule(type.OriginalDefinition, module, compilation) is { } hidden)
		{
			string name = Display(hidden.OriginalDefinition.ToDisplayString(FullyQualified));
			return granted
				? $"the InternalsVisibleTo grant to this assembly reaches only internal members, not '{name}', which is private to the module; make it internal or public"
				: $"make '{name}', which is private to the module, internal or public";
		}

		string typeName = Display(type.OriginalDefinition.ToDisplayString(FullyQualified));

		// Only the implementation's own assembly can grant access to its constructors, whatever the module's assembly grants.
		if (constructed
		    && type.OriginalDefinition is INamedTypeSymbol { TypeKind: TypeKind.Class, ContainingAssembly: { } owner, } foreign
		    && !SymbolEqualityComparer.Default.Equals(owner, module.ContainingAssembly)
		    && !SymbolEqualityComparer.Default.Equals(owner, compilation.Assembly)
		    && !CanConstruct(foreign, compilation))
		{
			return $"grant this assembly InternalsVisibleTo in '{owner.Name}', the assembly of '{typeName}', or {remedy}";
		}

		if (!granted)
		{
			return $"grant this assembly InternalsVisibleTo in the module's assembly, or {remedy}";
		}

		string? ungranted = !compilation.IsSymbolAccessibleWithin(type.OriginalDefinition, compilation.Assembly)
			? $"'{typeName}'"
			: member ?? (constructed && type.OriginalDefinition is INamedTypeSymbol { TypeKind: TypeKind.Class, } named && !CanConstruct(named, compilation) ? $"the constructors of '{typeName}'" : null);
		return ungranted is null ? remedy : $"the InternalsVisibleTo grant to this assembly reaches only internal members, not {ungranted}; {remedy}";
	}

	/// <summary>
	///     Reports AWT154 once per record for a <c>[GeneratedScanRegistration]</c> of <paramref name="module" /> that
	///     this container cannot read, because another version of the generator wrote it.
	/// </summary>
	private static void ReportUnreadableScanRecord(LifetimeCollectionContext context, INamedTypeSymbol module, ITypeSymbol? service, string? factory, Location? location)
	{
		DiagnosticInfo diagnostic = new(
			Diagnostics.ModuleScanRecordUnreadable,
			LocationInfo.From(location),
			new EquatableArray<string>([
				Display(module.ToDisplayString(FullyQualified)),
				Display(service?.ToDisplayString(FullyQualified) ?? "?"),
				factory ?? "null",
			]));
		if (!context.Diagnostics.Contains(diagnostic))
		{
			context.Diagnostics.Add(diagnostic);
		}
	}

	/// <summary>
	///     A registration of <paramref name="owner" /> rewritten to call the wrappers the owner generates for its
	///     <c>Factory</c> or <c>Instance</c> member and its lifecycle hooks (see <see cref="CollectModuleMemberWrappers" />
	///     and <see cref="RecordBaseWrappers" />), for each member this compilation cannot reach while it can reach the
	///     wrapper. Only <paramref name="applies" /> to an owner in another assembly, whose generated members this
	///     compilation can see. A wrapper of a member that produces an implementation this compilation cannot name returns
	///     the public service instead, which then stands in for the implementation, while the conventions and disposal
	///     are read off the implementation, as a container in the owner's assembly reads them; a compilation that can name
	///     it calls the wrapper that returns it (see <see cref="ImplementationWrapperName" />). The hooks of an
	///     implementation hidden in its owner are left to its generated factory.
	/// </summary>
	private static RawRegistration ThroughMemberWrapper(RawRegistration registration, INamedTypeSymbol? owner, Compilation compilation, bool applies)
	{
		if (!applies || owner is null || registration.ConflictingDirectives)
		{
			return registration;
		}

		string? member = registration.ProductionMember;
		INamedTypeSymbol implementation = registration.Implementation;
		ImplementationTraits? traits = registration.Traits;
		if (registration.Production is ProductionKind.Factory or ProductionKind.Instance && member is not null)
		{
			string wrapper = MemberWrapperName(registration.Production == ProductionKind.Factory ? "Factory" : "Instance", member, registration.Implementation);
			wrapper = Reachable(InInternals(ImplementationWrapperName(wrapper))) ? InInternals(ImplementationWrapperName(wrapper)) : InInternals(wrapper);
			if (!ReachesProductionMember(owner, registration.Production, member, registration.Implementation, compilation.Assembly, compilation) && Reachable(wrapper))
			{
				member = wrapper;
				if (!compilation.IsSymbolAccessibleWithin(implementation, compilation.Assembly)
				    && MembersNamed(owner, wrapper).Select(candidate => candidate switch
				    {
					    IMethodSymbol method => ProducedType(method.ReturnType, compilation),
					    IPropertySymbol property => property.Type,
					    _ => null,
				    }).FirstOrDefault() is INamedTypeSymbol exposed)
				{
					implementation = (INamedTypeSymbol)exposed.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
					traits = TraitsOf(registration.Implementation, DeclaredAsyncInitializers(owner, compilation, null));
				}
			}
		}

		bool hidden = IsHiddenInModule(registration.Implementation.OriginalDefinition, owner, compilation);
		return registration with
		{
			Implementation = implementation,
			ProductionMember = member,
			Traits = traits,
			OnActivated = hidden || registration.OnActivated is not { } activation ? registration.OnActivated : Wrapped(activation, InInternals(RegistrationHookWrapperName(registration.Implementation, release: false, activation))),
			OnRelease = hidden || registration.OnRelease is not { } release ? registration.OnRelease : Wrapped(release, InInternals(RegistrationHookWrapperName(registration.Implementation, release: true, release))),
		};

		string Wrapped(string hook, string wrapper)
			=> !ReachesHook(owner, hook, registration.Implementation, compilation) && Reachable(wrapper) ? wrapper : hook;

		bool Reachable(string name)
			=> MembersNamed(owner, name).Any(candidate => compilation.IsSymbolAccessibleWithin(candidate, compilation.Assembly));
	}

	/// <summary>
	///     <paramref name="registration" />, or <see langword="null" /> after reporting AWT231 when it names a hook of an
	///     imported module in another assembly that this container cannot call, and the module recorded why it generated
	///     no wrapper for it (see <see cref="CollectModuleMemberWrappers" />). A public hook the module recorded because
	///     another overload this container does not see accepts the type too is left out after reporting AWT231. Only
	///     <paramref name="applies" /> to such a module; one inherited through <c>Extends</c> keeps the registration, and
	///     leaves out such a public hook after reporting AWT226, unless the base shares this container's assembly and
	///     reports it itself.
	/// </summary>
	private static RawRegistration? WithCallableHooks(RawRegistration registration, LifetimeCollectionContext context, bool applies)
	{
		if (!applies || registration.Origin is not { } module)
		{
			return registration;
		}

		Compilation compilation = context.Compilation;
		foreach ((string? hook, bool release) in new[] { (registration.OnActivated, false), (registration.OnRelease, true), })
		{
			// A hook this compilation reaches was recorded because of an overload only the module's assembly and its friends see.
			bool reached = hook is not null && ReachesHook(module, hook, registration.Implementation, compilation);
			if (hook is not null
			    && (reached || !context.Inherited)
			    && (!reached || !module.ContainingAssembly.GivesAccessTo(compilation.Assembly))
			    && SkippedHookReason(module, registration.Implementation, release, hook) is { } reason)
			{
				if (context.Inherited)
				{
					// The extending container checks the base's graph from its own view, which reports AWT231 for this hook too, so
					// only AWT226 tells it apart from what the base reports.
					if (!SymbolEqualityComparer.Default.Equals(context.Inheritance!.Base.ContainingAssembly, compilation.Assembly))
					{
						context.Inheritance.ReportPrivate($"The type '{DisplayInstance(registration.ImplementationType)}'", new FactoryFault(reason, "give the hook methods distinct names"), context.Diagnostics);
					}

					registration = release ? registration with { OnRelease = null, } : registration with { OnActivated = null, };
					continue;
				}

				DiagnosticInfo diagnostic = new(
					reached ? Diagnostics.ModuleHookOverloadUnseen : Diagnostics.ModuleHookNotWrapped,
					LocationInfo.From(context.FallbackLocation),
					new EquatableArray<string>([
						Display(module.ToDisplayString(FullyQualified)),
						DisplayInstance(registration.ImplementationType),
						release ? "OnRelease" : "OnActivated",
						hook,
						reason,
						NotSelfCompiledRemedy(module, registration.Implementation, compilation, "make the hook and its parameter types public", $"the hook '{hook}'"),
					]));
				if (!context.Diagnostics.Contains(diagnostic))
				{
					context.Diagnostics.Add(diagnostic);
				}

				if (!reached)
				{
					return null;
				}

				// The container still constructs the type, so only the hook is left out, which keeps a second error off its consumers.
				registration = release ? registration with { OnRelease = null, } : registration with { OnActivated = null, };
			}
		}

		return registration;
	}

	/// <summary>
	///     Reads the implementation and service of a closed lifetime registration, from the generic form's type
	///     arguments or the <c>typeof</c> form's constructor arguments. Returns <see langword="false" /> when there is
	///     nothing to register: an argument does not bind, an open <c>typeof</c> pair was recorded as an open
	///     registration instead, or a closed pair is not assignable (reported).
	/// </summary>
	private static bool TryReadRegistrationTypes(
		AttributeData attribute,
		INamedTypeSymbol attributeClass,
		Lifetime lifetime,
		LifetimeCollectionContext context,
		[NotNullWhen(true)] out ITypeSymbol? implementation,
		[NotNullWhen(true)] out ITypeSymbol? service)
	{
		if (attributeClass.IsGenericType)
		{
			ImmutableArrayGuard(attributeClass.TypeArguments, out implementation, out service);
			service ??= implementation;
			return implementation is not null;
		}

		implementation = null;
		service = null;

		// The non-generic Type-ctor form, [Transient(typeof(Repository<>), typeof(IRepository<>))], is the only
		// way to name an open generic, which cannot be a type argument. An open pair is recorded as a template
		// expanded into concrete closed registrations on demand; a closed pair registers like the generic form.
		if (!TryReadTypeofPair(attribute, out INamedTypeSymbol typeofImplementation, out INamedTypeSymbol typeofService))
		{
			return false;
		}

		if (typeofImplementation.IsUnboundGenericType || typeofService.IsUnboundGenericType)
		{
			CollectOpenRegistration(attribute, lifetime, typeofImplementation, typeofService, context);
			return false;
		}

		if (!IsAssignableRegistration(typeofImplementation, typeofService, context.Compilation))
		{
			context.Diagnostics.Add(new DiagnosticInfo(
				Diagnostics.TypeofImplementationNotAssignable,
				LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? context.FallbackLocation),
				new EquatableArray<string>([
					Display(typeofImplementation.ToDisplayString(FullyQualified)),
					Display(typeofService.ToDisplayString(FullyQualified)),
				])));
			return false;
		}

		implementation = typeofImplementation;
		service = typeofService;
		return true;
	}

	private static bool IsGeneratedScanRegistration(INamedTypeSymbol attributeClass)
		=> attributeClass is { Name: "GeneratedScanRegistrationAttribute", IsGenericType: true, }
		   && attributeClass.ContainingNamespace?.ToDisplayString() == InternalsNamespace;

	/// <summary>
	///     Reads one <c>[GeneratedScanRegistration&lt;TService&gt;(factory, Lifetime = …)]</c> - what a
	///     <c>[Module]</c> self-compiled from its own <c>[Scan]</c>, one per match - into an <c>IsScan</c> factory
	///     registration, so the consuming container treats it exactly like a container <c>[Scan]</c> match:
	///     collection-eligible (several matches under one interface resolve as an <c>IEnumerable</c>) and overridable
	///     by an explicit registration (scans rank last). Each match gets a synthetic implementation identity keyed
	///     by its module-qualified factory name, distinct from the service's own type so the emitter still constructs
	///     through the accessible service; without it two matches of one interface would collapse into one
	///     implementation and collide as conflicting factories. The module qualification matters: factory names are
	///     only unique per module, so two imported modules whose matches share a simple type name would otherwise
	///     collapse into one member, silently dropping the later module's match from the collection. Returns whether
	///     the attribute was a <c>[GeneratedScanRegistration]</c> (so the caller skips its lifetime handling), even
	///     when <paramref name="skip" /> suppressed the collection for a same-compilation module whose <c>[Scan]</c>
	///     the container expands directly (see <see cref="Collect" />). An attribute that names a match the module
	///     generated no factory for (see <see cref="SkippedScanMatch" />) registers the match itself when this container
	///     can construct it, exactly as a scan would, and is otherwise dropped after reporting AWT231 once per match. An
	///     attribute that names the alias of a match the module builds through an instance handle (see
	///     <see cref="ScanInstanceHandleOf" />) registers the handle once per module, as a dependency-only service built
	///     by the module's scan factory with the scan's lifetime, hooks and traits, and the match as the alias that serves
	///     its service from the handle, as a module registration built through a handle is (see <see cref="ThroughHandle" />). An
	///     attribute of an imported module that is malformed, or that names neither a generated member nor a skipped
	///     match, was written by another version of the generator and is dropped after reporting AWT154.
	/// </summary>
	private static bool CollectGeneratedScanRegistration(
		AttributeData attribute,
		INamedTypeSymbol attributeClass,
		List<RawRegistration> result,
		LifetimeCollectionContext context,
		bool skip)
	{
		INamedTypeSymbol? origin = context.Origin;
		Location? fallbackLocation = context.FallbackLocation;
		if (!IsGeneratedScanRegistration(attributeClass))
		{
			return false;
		}

		if (skip)
		{
			return true;
		}

		Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallbackLocation;
		if (attributeClass.TypeArguments.Length != 1
		    || attributeClass.TypeArguments[0] is not INamedTypeSymbol service
		    || attribute.ConstructorArguments.Length != 1
		    || attribute.ConstructorArguments[0].Value is not string factory)
		{
			if (origin is not null)
			{
				ReportUnreadableScanRecord(context, origin, attributeClass.TypeArguments.FirstOrDefault(), null, location);
			}

			return true;
		}

		Lifetime lifetime = Lifetime.Transient;
		bool skipUnconstructable = false;
		string? onActivated = null;
		string? onRelease = null;
		string? implementationName = null;
		string[] asyncInitializers = [];
		string[] asyncInitializerMethods = [];
		foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
		{
			if (argument.Key == "AsyncInitializers" && argument.Value is { Kind: TypedConstantKind.Array, IsNull: false, } types)
			{
				asyncInitializers = types.Values
					.Select(type => (type.Value as ITypeSymbol)?.ToDisplayString(FullyQualified))
					.OfType<string>()
					.ToArray();
			}
			else if (argument.Key == "AsyncInitializerMethods" && argument.Value is { Kind: TypedConstantKind.Array, IsNull: false, } methods)
			{
				asyncInitializerMethods = methods.Values.Select(method => method.Value).OfType<string>().ToArray();
			}
			else if (argument.Key == "Lifetime" && argument.Value.Value is int value)
			{
				lifetime = (Lifetime)value;
			}
			else if (argument.Key == "SkipUnconstructable" && argument.Value.Value is bool flag)
			{
				skipUnconstructable = flag;
			}
			else if (argument.Key == "OnActivated" && argument.Value.Value is string activation)
			{
				onActivated = activation;
			}
			else if (argument.Key == "OnRelease" && argument.Value.Value is string release)
			{
				onRelease = release;
			}
			else if (argument.Key == "Implementation" && argument.Value.Value is string name)
			{
				implementationName = name;
			}
		}

		// A library built before the generator reported these leaves them all unset: the traits stay unknown then,
		// and the factory's return type decides as for any factory.
		ImplementationTraits traits = new(
			new EquatableArray<string>(asyncInitializers),
			NamedFlag(attribute, "Disposable"),
			NamedFlag(attribute, "AsyncDisposable"),
			new EquatableArray<string>(asyncInitializerMethods));

		string serviceType = service.ToDisplayString(FullyQualified);
		bool friend = origin is not null
		              && !SymbolEqualityComparer.Default.Equals(origin.ContainingAssembly, context.Compilation.Assembly)
		              && origin.ContainingAssembly.GivesAccessTo(context.Compilation.Assembly);
		if (friend)
		{
			onActivated = onActivated is null ? null : FriendScanMember(origin!, onActivated);
			onRelease = onRelease is null ? null : FriendScanMember(origin!, onRelease);
		}

		if (origin is not null && SkippedScanMatch(origin, factory) is var (implementation, reason))
		{
			// A match this container constructs still needs the hook wrapper the skipped match did not get.
			if (!context.Inherited
			    && !friend
			    && !SymbolEqualityComparer.Default.Equals(origin.ContainingAssembly, context.Compilation.Assembly)
			    && CanConstruct(implementation, context.Compilation)
			    && new[] { (Hook: onActivated, Slot: "OnActivated"), (Hook: onRelease, Slot: "OnRelease"), }
				    .FirstOrDefault(candidate => candidate.Hook is { } hook
				                                 && !AccessibleMembers(origin, hook, context.Compilation).OfType<IMethodSymbol>().Any(method => context.Compilation.IsSymbolAccessibleWithin(method, context.Compilation.Assembly))) is { Hook: { } unreached, Slot: { } slot, })
			{
				DiagnosticInfo diagnostic = new(
					Diagnostics.ModuleHookNotWrapped,
					LocationInfo.From(context.FallbackLocation),
					new EquatableArray<string>([
						Display(origin.ToDisplayString(FullyQualified)),
						Display(implementation.ToDisplayString(FullyQualified)),
						slot,
						unreached,
						reason,
						NotSelfCompiledRemedy(origin, implementation, context.Compilation, "make the hook and its parameter types public", $"the hook '{unreached}'"),
					]));
				if (!context.Diagnostics.Contains(diagnostic))
				{
					context.Diagnostics.Add(diagnostic);
				}

				return true;
			}

			if (context.Inherited || CanConstruct(implementation, context.Compilation))
			{
				result.Add(new RawRegistration(
					serviceType,
					TypeIdentity(implementation, context.Compilation),
					lifetime,
					implementation,
					location,
					ServiceSymbol: service,
					IsScan: true,
					ScanSkipsUnconstructable: skipUnconstructable,
					Origin: origin,
					OnActivated: onActivated,
					OnRelease: onRelease,
					GreedyConstructor: true));
			}
			else
			{
				ReportNotSelfCompiled(context, origin, implementation, "factory", reason, reason.StartsWith(SeveralExposuresPrefix, StringComparison.Ordinal)
					? ScanSingleExposureRemedy
					: $"change the module so its [Scan] can generate a factory for '{Display(implementation.ToDisplayString(FullyQualified))}'");
			}

			return true;
		}

		if (origin is not null && MembersNamed(origin, factory).IsEmpty)
		{
			ReportUnreadableScanRecord(context, origin, service, factory, location);
			return true;
		}

		if (friend)
		{
			factory = FriendScanMember(origin!, factory);
		}

		string qualifiedFactory = origin is null ? factory : $"{TypeIdentity(origin, context.Compilation)}.{factory}";

		RawRegistration match = new(
			serviceType,
			$"{serviceType}{ScanKeyMarker}{qualifiedFactory}",
			lifetime,
			service,
			location,
			ProductionKind.Factory,
			factory,
			ServiceSymbol: service,
			IsScan: true,
			ScanSkipsUnconstructable: skipUnconstructable,
			Origin: origin,
			// The hook wrapper names, resolved by the ordinary hook pipeline against the module (Origin): the
			// wrapper is a plain public static method, so ResolveHook binds it directly and ClassifyHookParameters
			// resolves its post-instance parameters from this container's graph.
			OnActivated: onActivated,
			OnRelease: onRelease);
		if (origin is not null && ScanInstanceHandleOf(origin, factory, implementationName, context.Compilation) is var (handled, handle, alias))
		{
			string identity = $"{TypeIdentity(handled, context.Compilation)}{HiddenKeyMarker}{TypeIdentity(origin, context.Compilation)}";
			if (!result.Exists(registration => registration.ImplementationType == identity))
			{
				result.Add(match with
				{
					ServiceType = handle.ReturnType.ToDisplayString(FullyQualified),
					ServiceSymbol = (INamedTypeSymbol)handle.ReturnType,
					ImplementationType = identity,
					Implementation = (INamedTypeSymbol)handle.ReturnType,
					ProductionMember = InInternals(handle.Name),
					IsScan = false,
					HiddenProduction = DescribeProduction(ProductionKind.Constructor, null, origin),
					Traits = traits,
					TraitsExact = true,
					Unlisted = true,
					Hidden = handled,
				});
			}

			result.Add(ThroughAlias(match, origin, handled, alias, context.Compilation) with { Aliased = match, });
			return true;
		}

		result.Add(match with
		{
			Traits = traits.IsEmpty ? null : traits,
			ScanImplementation = origin is null || implementationName is null ? null : origin.ContainingAssembly.GetTypeByMetadataName(implementationName),
		});
		return true;
	}

	/// <summary>
	///     The implementation, its handle factory and the alias that <paramref name="factory" /> names, when a
	///     <c>[GeneratedScanRegistration]</c> of <paramref name="module" /> serves a match the module builds through an
	///     instance handle (see <see cref="ScanInstanceHandle" />): the named member takes the instance handle that the
	///     module's scan factory of <paramref name="implementationName" /> returns, or a factory of it (see
	///     <see cref="TakesHandle" />). Otherwise <see langword="null" />.
	/// </summary>
	private static (INamedTypeSymbol Implementation, IMethodSymbol Handle, IMethodSymbol Alias)? ScanInstanceHandleOf(INamedTypeSymbol module, string factory, string? implementationName, Compilation compilation)
		=> implementationName is not null
		   && module.ContainingAssembly.GetTypeByMetadataName(implementationName) is { } implementation
		   && GeneratedFactoryNamed(module, ModuleFactoryName(implementation), compilation) is { } handle
		   && handle.ReturnType.Name == HiddenInstanceName(implementation)
		   && MembersNamed(module, factory).OfType<IMethodSymbol>().FirstOrDefault(member => TakesHandle(member, handle.ReturnType.ToDisplayString(FullyQualified))) is { } alias
			? (implementation, handle, alias)
			: null;

	/// <summary>
	///     AWT168: a contextual binding is stored under a synthetic per-consumer key (see <c>ContextKey</c>), which
	///     overrides an explicit <c>Key</c> entirely, so the two together silently drop the <c>Key</c>. Report it
	///     rather than let a <c>[FromKey]</c> the author expects to select this registration quietly never match.
	/// </summary>
	private static void ReportContextualBindingWithKey(
		string? key,
		string? whenInjectedInto,
		ITypeSymbol implementation,
		Location? location,
		List<DiagnosticInfo> diagnostics)
	{
		if (key is null || whenInjectedInto is null)
		{
			return;
		}

		diagnostics.Add(new DiagnosticInfo(
			Diagnostics.ContextualBindingWithKey,
			LocationInfo.From(location),
			new EquatableArray<string>([Display(implementation.ToDisplayString(FullyQualified)),])));
	}

	/// <summary>
	///     Reports AWT252 at the import of each module of another assembly whose factory passes an empty collection of an
	///     element type that another of the <paramref name="modules" />, or the container itself, registers under the same
	///     key, or unkeyed under a service that a collection of the element holds through variance, as the module records
	///     it (see <see cref="EmptyCollectionRecords" />), when the module's assembly grants this one no
	///     <c>InternalsVisibleTo</c>, so the container calls that factory instead of building the receiving type itself. A
	///     <c>WhenInjectedInto</c> registration is no member of a collection. A module and a registration both inherited
	///     through <c>Extends</c> are the base's to report.
	/// </summary>
	private static void ReportEmptyCollectionsOfModules(INamedTypeSymbol containerSymbol, List<ImportedModule> modules, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		foreach (ImportedModule receiving in modules)
		{
			INamedTypeSymbol module = receiving.Symbol;
			if (receiving.IsBase
			    || SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly)
			    || module.ContainingAssembly.GivesAccessTo(compilation.Assembly)
			    || EmptyCollectionRecords(module, compilation) is not { Count: > 0, } records)
			{
				continue;
			}

			HashSet<string> reported = new(StringComparer.Ordinal);
			foreach (ImportedModule providing in modules.Append(new ImportedModule(containerSymbol, null)).Where(providing => !(receiving.Inherited && providing.Inherited)))
			{
				foreach (ModuleRegistration registration in ConstructedModuleRegistrations(providing.Symbol, compilation, members: true))
				{
					string? key = NamedKeyArgument(registration.Attribute);
					if (NamedTypeArgument(registration.Attribute, "WhenInjectedInto", compilation) is not null)
					{
						continue;
					}

					foreach ((INamedTypeSymbol element, string name, string receiver) in records)
					{
						if (name != EmptyCollectionRecordName(element, key)
						    || !(SymbolEqualityComparer.Default.Equals(registration.Service, element)
						         || (key is null && element.TypeKind == TypeKind.Interface && VarianceCompatible(registration.Service, element, compilation)))
						    || !reported.Add(name))
						{
							continue;
						}

						string service = element.ToDisplayString(FullyQualified);
						diagnostics.Add(new DiagnosticInfo(
							Diagnostics.CollectionFixedEmptyInModule,
							LocationInfo.From(receiving.ImportLocation),
							new EquatableArray<string>([
								Display(module.ToDisplayString(FullyQualified)),
								receiver,
								DisplayKeyed(service, key),
								Display(providing.Symbol.ToDisplayString(FullyQualified)),
								Display(service),
							])));
					}
				}
			}
		}
	}

	/// <summary>
	///     Reads the modules a container pulls in with <c>[Import(typeof(Module))]</c>, in declaration order,
	///     validating each as it goes: the target must be a <c>[Module]</c> (AWT149, else skipped), a module's
	///     own <c>[Import]</c> is reported as not-followed (AWT150), and a module declaring no registrations is
	///     reported as contributing nothing (AWT151). Only the container's own imports are read.
	/// </summary>
	private static List<ImportedModule> CollectImportedModules(INamedTypeSymbol containerSymbol, List<DiagnosticInfo> diagnostics)
	{
		List<ImportedModule> modules = new();
		HashSet<INamedTypeSymbol> seen = new(SymbolEqualityComparer.Default);
		foreach (AttributeData attribute in containerSymbol.GetAttributes())
		{
			if (attribute.AttributeClass is not { Name: "ImportAttribute", } attributeClass
			    || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace)
			{
				continue;
			}

			// [Import(typeof(Module))] names the module as the single constructor argument. There is deliberately
			// no generic [Import<TModule>] form: a module must be a static class, and C# forbids a static class
			// as a generic type argument (CS0718).
			INamedTypeSymbol? module = attribute.ConstructorArguments.Length == 1
				? attribute.ConstructorArguments[0].Value as INamedTypeSymbol
				: null;
			if (module is null)
			{
				continue;
			}

			// A module imported more than once contributes only once: lifetime registrations coalesce away, but
			// decorators would otherwise double-wrap (the chain builder does not dedup). Skip the redundant
			// import silently, before validation, so its diagnostics are not reported twice either.
			if (!seen.Add(module))
			{
				continue;
			}

			// Every module diagnostic points at the container's [Import], the line the author actually wrote,
			// not the module declaration, which may live in another file (or, for a module compiled into a
			// referenced assembly, in no source at all).
			Location? importLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
			bool sameCompilation = SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, containerSymbol.ContainingAssembly);
			if (ValidateImportedModule(module, importLocation, sameCompilation, diagnostics))
			{
				modules.Add(new ImportedModule(module, importLocation));
			}
		}

		return modules;
	}

	/// <summary>
	///     Validates one <c>[Import(typeof(Module))]</c> target, reporting AWT149-152, and returns whether it is
	///     importable. A non-<c>[Module]</c> target (AWT149) is skipped (<see langword="false" />); the other faults
	///     are reported but still imported (<see langword="true" />): non-static (AWT152), a nested <c>[Import]</c>
	///     (AWT150), or no registrations (AWT151). A module-declared <c>[Scan]</c> is accepted (self-compiled in the
	///     module's own build, AWT154 retired) rather than rejected. <paramref name="sameCompilation" /> marks a
	///     module declared in the container's own compilation, whose <c>[Scan]</c> the container expands directly
	///     (its self-compiled attributes are invisible here), so the scan counts as a registration for AWT151.
	/// </summary>
	private static bool ValidateImportedModule(
		INamedTypeSymbol module,
		Location? importLocation,
		bool sameCompilation,
		List<DiagnosticInfo> diagnostics)
	{
		LocationInfo? location = LocationInfo.From(importLocation);
		ImmutableArray<AttributeData> moduleAttributes = module.GetAttributes();
		string moduleName = Display(module.ToDisplayString(FullyQualified));

		// AWT149: only [Module] types can be imported. A non-module target contributes nothing, so it is
		// skipped and surfaced here rather than as a later cascade of missing dependencies.
		if (!HasAwaitenAttribute(moduleAttributes, "ModuleAttribute"))
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.ImportNotAModule, location, new EquatableArray<string>([moduleName,])));
			return false;
		}

		// AWT152: like a container, a module is a pure definition (registrations plus static factory and
		// instance members), never instantiated, so it must be a static class (mirroring AWT116). Still
		// imported, so its registrations do not additionally cascade as missing.
		if (!module.IsStatic)
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.NonStaticModule, location, new EquatableArray<string>([moduleName,])));
		}

		// AWT150: a module's own [Import] is not followed, so the nested module's registrations are not pulled
		// in transitively. The container must import the nested module directly.
		if (HasAwaitenAttribute(moduleAttributes, "ImportAttribute"))
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.NestedModuleImport, location, new EquatableArray<string>([moduleName,])));
		}

		// A referenced-assembly module's [Scan] is self-compiled in the module's own build (the module emits a
		// generated factory and lifetime registration per match, plus a [GeneratedScanExpansion] marker), which
		// this collection reads like any other module registration. A same-compilation module's [Scan] is instead
		// expanded by the container itself (see Collect); the generator cannot see the module's generated marker
		// there (its own output), so the skew check below excludes it.
		bool declaresScan = HasAwaitenAttribute(moduleAttributes, "ScanAttribute");

		// AWT154: the metadata carries a [Scan] but no expansion marker, so the module assembly was built without
		// the Awaiten generator (or a version predating self-compiled scans) and the scan would silently
		// contribute nothing. The marker is emitted even for a scan that matched nothing, so its absence is
		// conclusive, not a maybe.
		if (!sameCompilation && declaresScan && !moduleAttributes.Any(attribute =>
			    attribute.AttributeClass is { Name: "GeneratedScanExpansionAttribute", } attributeClass
			    && attributeClass.ContainingNamespace?.ToDisplayString() == InternalsNamespace))
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.ModuleScanNotExpanded, location, new EquatableArray<string>([moduleName, "declares a [Scan]",])));
		}

		// AWT151: a module that declares nothing to contribute imports nothing useful. A module with a [Scan]
		// is exempt regardless of what the scan produced: the module's own build reports an empty scan at its
		// declaration (AWT138/AWT184), and an unexpanded one is AWT154 above, so repeating the blame at the
		// consumer's [Import] would point at code the consumer does not own.
		if (!DeclaresAnyContribution(moduleAttributes) && !declaresScan)
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.EmptyModule, location, new EquatableArray<string>([moduleName,])));
		}

		return true;
	}

	/// <summary>
	///     Whether an attribute list carries anything a module contributes to an importing container: a
	///     <c>[Singleton]</c>/<c>[Transient]</c>/<c>[Scoped]</c> lifetime registration (generic or open
	///     <c>typeof</c> form), a <c>[Decorate]</c>, a <c>[Composite]</c>, <c>[ImportServices]</c> or an
	///     <c>[ImportService]</c> (generic or <c>typeof</c> form). Used to detect a module that declares nothing to
	///     import (AWT151). A module-declared <c>[Scan]</c> does not count here on its own: its matches are
	///     self-compiled into <c>[GeneratedScanRegistration]</c> attributes (which do count), so a scan-only module
	///     whose scan matched something carries those generated attributes.
	/// </summary>
	private static bool DeclaresAnyContribution(ImmutableArray<AttributeData> attributes)
	{
		foreach (AttributeData attribute in attributes)
		{
			if (attribute.AttributeClass is { } attributeClass
			    && (IsGeneratedScanRegistration(attributeClass)
			        || (attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace
			            && attributeClass.Name is "SingletonAttribute" or "TransientAttribute" or "ScopedAttribute"
				            or "DecorateAttribute" or "CompositeAttribute" or "ImportServicesAttribute"
				            or "ImportServiceAttribute")))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	///     The attributes of the container followed by those of its imported modules, in import order: the
	///     shared enumeration for readers that accept module contributions (<c>[Decorate]</c>,
	///     <c>[Composite]</c>), so the container's declarations always precede a module's and an earlier
	///     import's precede a later one's. Each attribute is paired with a fallback location (the module's
	///     <c>[Import]</c>) for attributes read from a referenced assembly, which have no syntax of their own, and
	///     whether it is inherited from an extended base (see <see cref="InheritBase" />), and with the module that
	///     declares it (<see langword="null" /> for the container's own).
	/// </summary>
	private static IEnumerable<(AttributeData Attribute, Location? FallbackLocation, bool Inherited, INamedTypeSymbol? Module)> AttributesOf(
		INamedTypeSymbol containerSymbol, List<ImportedModule> modules)
	{
		foreach (AttributeData attribute in containerSymbol.GetAttributes())
		{
			yield return (attribute, null, false, null);
		}

		foreach (ImportedModule module in modules)
		{
			foreach (AttributeData attribute in module.Symbol.GetAttributes())
			{
				yield return (attribute, module.ImportLocation, module.Inherited, module.Symbol);
			}
		}
	}

	/// <summary>
	///     Reports <see cref="Diagnostics.ModuleContributionNotSelfCompiled">AWT230</see> and returns
	///     <see langword="true" /> when a decorator, composite or open generic registration of an imported module of
	///     another assembly names a type this container cannot construct and for which the module generated no factory,
	///     so the contribution is skipped rather than failing as a misleading constructor diagnostic. A module that can carry
	///     no factory at all (see <see cref="ModuleShapeReason" />) is AWT231 with that reason instead.
	/// </summary>
	private static bool IsUnconstructibleModuleContribution(
		INamedTypeSymbol? module,
		INamedTypeSymbol type,
		string kind,
		Location? location,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics)
	{
		if (module is null
		    || SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly)
		    || type is not { TypeKind: TypeKind.Class, IsAbstract: false, }
		    || CanConstruct(type.OriginalDefinition, compilation))
		{
			return false;
		}

		string typeName = Display(type.OriginalDefinition.ToDisplayString(FullyQualified));
		diagnostics.Add(ModuleShapeReason(module) is { } reason
			? ContributionNotSelfCompiled(module, kind, type.OriginalDefinition, reason, location, compilation)
			: new DiagnosticInfo(
				Diagnostics.ModuleContributionNotSelfCompiled,
				LocationInfo.From(location),
				new EquatableArray<string>([Display(module.ToDisplayString(FullyQualified)), kind, typeName,])));
		return true;
	}

	/// <summary>
	///     AWT231 for a decorator, composite or open generic registration of <paramref name="module" />, named
	///     <paramref name="kind" /> as AWT230 names it, for which the module generated no factory because of
	///     <paramref name="reason" />: a module declares a decorator or composite, but registers a type. The remedy is the
	///     <paramref name="recordedRemedy" /> of a fault the module recorded, else chosen by what this container sees (see
	///     <see cref="NotSelfCompiledRemedy" />).
	/// </summary>
	private static DiagnosticInfo ContributionNotSelfCompiled(INamedTypeSymbol module, string kind, INamedTypeSymbol type, string reason, Location? location, Compilation compilation, string? recordedRemedy = null)
	{
		string typeName = Display(type.ToDisplayString(FullyQualified));
		string remedy = recordedRemedy ?? NotSelfCompiledRemedy(module, type, compilation, $"make '{typeName}', its constructor and the constructor's parameter types public");
		return kind == OpenRegistrationKind
			? new DiagnosticInfo(
				Diagnostics.ModuleRegistrationNotSelfCompiled,
				LocationInfo.From(location),
				new EquatableArray<string>([Display(module.ToDisplayString(FullyQualified)), typeName, "factory", reason, remedy,]))
			: new DiagnosticInfo(
				Diagnostics.ModuleContributionFactorySkipped,
				LocationInfo.From(location),
				new EquatableArray<string>([Display(module.ToDisplayString(FullyQualified)), kind, typeName, reason, remedy,]));
	}

	/// <summary>
	///     Where a <c>[Decorate]</c> or <c>[Composite]</c> comes from while the container reads it: the module or base
	///     that declares it (<see langword="null" /> for the container's own), the inheritance when it is inherited
	///     through <c>Extends</c>, and where to report.
	/// </summary>
	private readonly record struct ContributionContext(
		INamedTypeSymbol? Module,
		Inheritance? Inheritance,
		Location? Location,
		Compilation Compilation,
		SameCompilationModules Modules,
		List<DiagnosticInfo> Diagnostics);

	/// <summary>
	///     Whether a decorator or composite that the container cannot construct is left out, after reporting why, or
	///     finds the generated factory that builds it in <paramref name="via" />. A base container's own is resolved by
	///     the inheritance (see <see cref="InaccessibleInheritedType" />). A module's closed one calls the factory the
	///     module generates for it (see <see cref="CollectModuleContributionFactories" />): from metadata in another
	///     assembly, or by its deterministic name in the module's own assembly when the type is private to the module,
	///     preferring the internal factory that takes the internal services instead of their handles where the container
	///     can see it. Without a factory, one whose skip the module recorded is AWT231, which for a type this container
	///     can construct names the <c>[Inject]</c> property it does not see, one inherited through <c>Extends</c> is
	///     AWT226, one whose module recorded what a container in its own assembly reports is that diagnostic (see
	///     <see cref="RecordedContributionFault" />), and any other is AWT230.
	/// </summary>
	private static bool UnavailableContribution(ContributionContext context, AttributeData attribute, INamedTypeSymbol attributeClass, out OwnerFactoryCall? via)
	{
		via = null;
		Inheritance? inheritance = context.Inheritance;
		Compilation compilation = context.Compilation;
		List<DiagnosticInfo> diagnostics = context.Diagnostics;
		if (inheritance is not null && SymbolEqualityComparer.Default.Equals(context.Module, inheritance.Base))
		{
			return InaccessibleInheritedType(attribute, attributeClass, inheritance, diagnostics, out via);
		}

		if (context.Module is not { } module || ContributedType(attribute, attributeClass) is not { } type)
		{
			return false;
		}

		(INamedTypeSymbol? closedType, INamedTypeSymbol? service) = ContributedPair(attribute, attributeClass);
		string? name = closedType is not null && service is not null ? ContributionFactoryName(attributeClass.Name, closedType, service) : null;
		if (SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly))
		{
			if (name is not null
			    && IsHiddenInModule(type, module, compilation)
			    && context.Modules.Contribution(module, name) is { } generated
			    && MirroredConstructor(type, generated) is { } constructor)
			{
				via = new OwnerFactoryCall(module, generated.FactoryName, generated.ReturnType, constructor);
				return false;
			}

			return inheritance is not null && InaccessibleInheritedType(attribute, attributeClass, inheritance, diagnostics, out _);
		}

		// The factory of a type this container can construct sets an internal [Inject] property it does not see, and a
		// recorded skip says the module could not generate one.
		string? skipReason = name is not null ? SkippedContributionReason(module, name) : null;
		bool hiddenMembers = name is not null
		                     && !module.ContainingAssembly.GivesAccessTo(compilation.Assembly)
		                     && (GeneratedFactoryNamed(module, name, compilation) is not null || skipReason is not null);
		if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, } || (CanConstruct(type.OriginalDefinition, compilation) && !hiddenMembers))
		{
			return inheritance is not null && InaccessibleInheritedType(attribute, attributeClass, inheritance, diagnostics, out _);
		}

		// A container granted InternalsVisibleTo builds the internal services itself and registers no handles, so it calls
		// the internal factory that takes them directly, which the module generates when the public one takes handles.
		if (name is not null && (GeneratedFactoryNamed(module, "Internal" + name, compilation) ?? GeneratedFactoryNamed(module, name, compilation)) is { } factory)
		{
			via = new OwnerFactoryCall(module, factory.Name, factory.ReturnType.ToDisplayString(FullyQualified), factory);
			return false;
		}

		if (hiddenMembers && skipReason is not null)
		{
			DiagnosticInfo diagnostic = CanConstruct(type.OriginalDefinition, compilation)
				? new DiagnosticInfo(
					Diagnostics.ModuleRegistrationPropertyNotSet,
					LocationInfo.From(context.Location),
					new EquatableArray<string>([
						Display(module.ToDisplayString(FullyQualified)), Display(type.ToDisplayString(FullyQualified)), "factory", skipReason,
						NotSelfCompiledRemedy(module, type, compilation, $"make the [Inject] properties of '{Display(type.ToDisplayString(FullyQualified))}', their setters and their types public"),
					]))
				: ContributionNotSelfCompiled(module, attributeClass.Name == "CompositeAttribute" ? "the composite" : "the decorator", type, skipReason, context.Location, compilation);
			if (!diagnostics.Contains(diagnostic))
			{
				diagnostics.Add(diagnostic);
			}

			return true;
		}

		if (inheritance is not null)
		{
			return InaccessibleInheritedType(attribute, attributeClass, inheritance, diagnostics, out _);
		}

		if (name is not null && RecordedContributionFault(module, name, closedType!, service!, context.Location) is { } fault)
		{
			diagnostics.Add(fault);
			return true;
		}

		return IsUnconstructibleModuleContribution(module, type, attributeClass.Name == "CompositeAttribute" ? "the composite" : "the decorator", context.Location, compilation, diagnostics);
	}

	/// <summary>
	///     The decorator or composite type a <c>[Decorate]</c>/<c>[Composite]</c> names: the first type argument of the
	///     generic form, or the first <c>typeof</c> argument.
	/// </summary>
	private static INamedTypeSymbol? ContributedType(AttributeData attribute, INamedTypeSymbol attributeClass)
		=> attributeClass.IsGenericType
			? attributeClass.TypeArguments.FirstOrDefault() as INamedTypeSymbol
			: attribute.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;

	/// <summary>
	///     Reads a boolean named argument off an attribute, treating anything but an explicit <c>true</c>
	///     (including its absence) as <see langword="false" />.
	/// </summary>
	private static bool NamedFlag(AttributeData attribute, string name)
	{
		foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
		{
			if (argument.Key == name && argument.Value.Value is true)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	///     Whether a <c>[Singleton]</c> may be built when the root is created though nothing depends on it: it sets
	///     <c>Eager</c>, or names an <c>EagerWhen</c> predicate that may allow it.
	/// </summary>
	private static bool StartsEagerly(AttributeData attribute)
		=> NamedFlag(attribute, "Eager") || NamedArgument(attribute, "EagerWhen") is not null;

	/// <summary>
	///     Reads the <c>Fallback</c> enum named argument off a lifetime attribute as its underlying int
	///     (0 = None, 1 = Warn, 2 = Silent), defaulting to 0 (None, a normal registration) when unset. These
	///     values must stay in step with the <c>Fallback</c> enum, which the generator cannot reference directly.
	/// </summary>
	private static int NamedFallback(AttributeData attribute)
	{
		foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
		{
			if (argument.Key == "Fallback" && argument.Value.Value is int value)
			{
				return value;
			}
		}

		return 0;
	}

	/// <summary>
	///     Reads the <c>[Decorate]</c> registrations on a container and its modules, in declaration order (container
	///     first): the closed <c>[Decorate&lt;TDecorator, TService&gt;]</c> form and the open generic
	///     <c>[Decorate(typeof(D&lt;&gt;), typeof(IService&lt;&gt;))]</c> form. Both share one declaration counter so
	///     that, once the open form is expanded per closing, open and closed decorators of the same closed service
	///     interleave by equal <c>Order</c> then declaration order. Collected apart from lifetime registrations
	///     because a decorator wraps an existing registration after coalescing. A decorator inherited from an extended
	///     base is marked so it stays inside the extending container's own decorators; one the extending container
	///     cannot construct is AWT226 and skipped.
	/// </summary>
	private static (List<DecorateRegistration> Closed, List<OpenDecorateRegistration> Open) CollectDecorators(
		INamedTypeSymbol containerSymbol, List<ImportedModule> modules, Inheritance? inheritance, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		List<DecorateRegistration> closed = new();
		List<OpenDecorateRegistration> open = new();
		SameCompilationModules sameCompilationModules = new(compilation);
		int declarationOrder = 0;
		foreach ((AttributeData attribute, Location? fallbackLocation, bool inherited, INamedTypeSymbol? module) in AttributesOf(containerSymbol, modules))
		{
			if (attribute.AttributeClass is not { Name: "DecorateAttribute", } attributeClass
			    || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace)
			{
				continue;
			}

			Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallbackLocation;
			ContributionContext contribution = new(module, inherited ? inheritance : null, location, compilation, sameCompilationModules, diagnostics);
			OpenViaOutcome openVia = ResolveOpenContributionVia(attribute, attributeClass, module, contribution, fallbackLocation, inherited);
			OwnerFactoryCall? via = null;
			if (openVia.Dropped || (openVia.Via is null && UnavailableContribution(contribution, attribute, attributeClass, out via)))
			{
				declarationOrder++;
				continue;
			}

			int order = 0;
			foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
			{
				if (argument.Key == "Order" && argument.Value.Value is int value)
				{
					order = value;
				}
			}

			// The predicate is read even when the keys skip the decorator, so its own mistakes are reported too.
			bool leftToModule = module is not null && HasAwaitenAttribute(module.GetAttributes(), "ModuleAttribute") && ReportsOwnDecoratorMistakes(module, compilation);
			bool keysRead = TryReadDecoratorKeys(attribute, attributeClass, LocationInfo.From(location), leftToModule ? new List<DiagnosticInfo>() : diagnostics, out IReadOnlyList<string?>? keys);
			if (!TryReadPredicate(attribute, module ?? containerSymbol, containerSymbol, compilation, location, diagnostics, out DecoratorPredicate? when)
			    || !keysRead)
			{
				declarationOrder++;
				continue;
			}

			if (attributeClass.IsGenericType)
			{
				if (ContributedPair(attribute, attributeClass) is ({ } decorator, { } service))
				{
					// A module of another assembly builds a decorator of an internal service into the service's handle, which the chain then decorates.
					INamedTypeSymbol? handle = HandleProducedBy(via, service, compilation);
					INamedTypeSymbol decorated = handle ?? service;
					closed.Add(new DecorateRegistration(
						decorated.ToDisplayString(FullyQualified), decorated, decorator, order, declarationOrder, location, keys, inherited, via, module, handle is not null, when,
						leftToModule && ReportsMissingInner(module!, decorator, service, compilation)));
				}
			}
			else if (attribute.ConstructorArguments.Length == 2
			         && attribute.ConstructorArguments[0].Value is INamedTypeSymbol openDecorator
			         && attribute.ConstructorArguments[1].Value is INamedTypeSymbol openService
			         && TryReadOpenGenericPair(openDecorator, openService, LocationInfo.From(location), diagnostics) is { } pair)
			{
				bool decoratesHandles = false;
				foreach ((INamedTypeSymbol decorator, OwnerFactoryCall handleVia, INamedTypeSymbol handle, _) in HandleClosings(module, inherited, pair.Mapped, pair.Service, attributeClass.Name, compilation))
				{
					closed.Add(new DecorateRegistration(handle.ToDisplayString(FullyQualified), handle, decorator, order, declarationOrder, location, keys, inherited, handleVia, module, ThroughHandle: true, When: when));
					decoratesHandles = true;
				}

				open.Add(new OpenDecorateRegistration(pair.Service, pair.Mapped, order, declarationOrder, location, keys, inherited, module, openVia.Via, decoratesHandles, when));
			}

			// One slot per [Decorate] attribute (well-formed or not), so open and closed decorators keep their
			// relative source order regardless of how many closings the open form later expands to.
			declarationOrder++;
		}

		return (closed, open);
	}

	/// <summary>
	///     The handle of <paramref name="service" /> that the generated factory <paramref name="via" /> of a module of
	///     another assembly builds a decorator or composite into, because this container cannot name the internal service,
	///     or <see langword="null" />.
	/// </summary>
	private static INamedTypeSymbol? HandleProducedBy(OwnerFactoryCall? via, INamedTypeSymbol service, Compilation compilation)
		=> via?.Producer is { MethodKind: MethodKind.Ordinary, ReturnType: INamedTypeSymbol handle, }
		   && handle.Name == HiddenHandleName(service)
		   && !compilation.IsSymbolAccessibleWithin(service, compilation.Assembly)
			? handle
			: null;

	/// <summary>
	///     The closings of an open <c>[Decorate]</c> or <c>[Composite]</c> of a module of another assembly onto the internal
	///     services the module registers, which this container reaches only through their handles: each closed decorator or
	///     composite, the factory the module generates for it (see <see cref="AddHiddenOpenContributionFactories" />), the
	///     handle it decorates or fronts and the internal service the handle holds. This container cannot name such a
	///     closing, so the open form alone would miss it.
	/// </summary>
	private static IEnumerable<(INamedTypeSymbol Type, OwnerFactoryCall Via, INamedTypeSymbol Handle, INamedTypeSymbol Service)> HandleClosings(
		INamedTypeSymbol? module, bool inherited, INamedTypeSymbol definition, INamedTypeSymbol openService, string attributeName, Compilation compilation)
	{
		if (module is null || inherited || SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly))
		{
			yield break;
		}

		HashSet<INamedTypeSymbol> seen = new(SymbolEqualityComparer.Default);
		foreach (ModuleRegistration registration in ConstructedModuleRegistrations(module, compilation, members: true))
		{
			INamedTypeSymbol service = registration.Service;
			if (!ClosesOpenService(service, openService)
			    || compilation.IsSymbolAccessibleWithin(service, compilation.Assembly)
			    || !seen.Add(service)
			    || !ConstraintsSatisfied(definition, service.TypeArguments.ToArray(), compilation))
			{
				continue;
			}

			INamedTypeSymbol closed = definition.Construct(service.TypeArguments.ToArray());
			if (GeneratedFactoryNamed(module, ContributionFactoryName(attributeName, closed, service), compilation) is { } factory)
			{
				OwnerFactoryCall via = new(module, factory.Name, factory.ReturnType.ToDisplayString(FullyQualified), factory);
				if (HandleProducedBy(via, service, compilation) is { } handle)
				{
					yield return (closed, via, handle, service);
				}
			}
		}
	}

	/// <summary>
	///     Reads the <c>Keys</c> and <c>AllKeys</c> named arguments of a <c>[Decorate]</c> as the selected canonical
	///     keys: unset yields the unkeyed registration only (<c>[null]</c>), <c>AllKeys</c> yields
	///     <see langword="null" />. Returns <see langword="false" />, so the decorator is skipped, when both are set
	///     (reported as AWT211) or when <c>Keys</c> selects no key: it is empty (reported as AWT123) or each of its entries
	///     has an unsupported key type (each reported as AWT170).
	/// </summary>
	private static bool TryReadDecoratorKeys(
		AttributeData attribute,
		INamedTypeSymbol attributeClass,
		LocationInfo? location,
		List<DiagnosticInfo> diagnostics,
		out IReadOnlyList<string?>? keys)
	{
		bool allKeys = NamedFlag(attribute, "AllKeys");
		TypedConstant? selected = null;
		foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
		{
			if (argument.Key == "Keys" && !argument.Value.IsNull)
			{
				selected = argument.Value;
			}
		}

		if (selected is null)
		{
			keys = allKeys ? null : new string?[] { null, };
			return true;
		}

		if (allKeys)
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.DecoratorKeysWithAllKeys,
				location,
				new EquatableArray<string>([AttributeTypeArgument(attribute, attributeClass, 0), AttributeTypeArgument(attribute, attributeClass, 1),])));
			keys = null;
			return false;
		}

		if (selected.Value.Values.IsEmpty)
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.DecoratorWithEmptyKeys,
				location,
				new EquatableArray<string>([AttributeTypeArgument(attribute, attributeClass, 1), AttributeTypeArgument(attribute, attributeClass, 0),])));
			keys = null;
			return false;
		}

		List<string?> list = new();
		foreach (TypedConstant element in selected.Value.Values)
		{
			if (IsUnsupportedKeyConstant(element))
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.UnsupportedKeyType, location,
					new EquatableArray<string>([AttributeTypeArgument(attribute, attributeClass, 0), KeyConstantTypeDisplay(element),])));
				continue;
			}

			string? key = EncodeKeyConstant(element);
			if (!list.Contains(key))
			{
				list.Add(key);
			}
		}

		keys = list;
		return list.Count > 0;
	}

	/// <summary>
	///     The display of a <c>[Decorate]</c>/<c>[Composite]</c> type at <paramref name="index" /> (0 the decorator or
	///     composite, 1 the service), read from the generic form's type arguments or the <c>typeof</c> form's
	///     constructor arguments.
	/// </summary>
	private static string AttributeTypeArgument(AttributeData attribute, INamedTypeSymbol attributeClass, int index)
	{
		ITypeSymbol? type = null;
		if (attributeClass.IsGenericType)
		{
			type = attributeClass.TypeArguments[index];
		}
		else if (attribute.ConstructorArguments.Length == 2)
		{
			type = attribute.ConstructorArguments[index].Value as ITypeSymbol;
		}

		return type is null ? "?" : Display(type.ToDisplayString(FullyQualified));
	}

	/// <summary>
	///     Reads the <c>[Composite]</c> registrations on a container and its modules: the closed
	///     <c>[Composite&lt;TComposite, TService&gt;]</c> form and the open generic
	///     <c>[Composite(typeof(C&lt;&gt;), typeof(IService&lt;&gt;))]</c> form, each carrying the composed service,
	///     the composite and its lifetime (default <see cref="Lifetime.Transient" />). Collected apart from lifetime
	///     registrations because a composite fronts an existing service after coalescing. A composite inherited from
	///     an extended base that the extending container cannot construct is AWT226 and skipped.
	/// </summary>
	private static (List<CompositeRegistration> Closed, List<OpenCompositeRegistration> Open) CollectComposites(
		INamedTypeSymbol containerSymbol, List<ImportedModule> modules, Inheritance? inheritance, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		List<CompositeRegistration> closed = new();
		List<OpenCompositeRegistration> open = new();
		SameCompilationModules sameCompilationModules = new(compilation);
		foreach ((AttributeData attribute, Location? fallbackLocation, bool inherited, INamedTypeSymbol? module) in AttributesOf(containerSymbol, modules))
		{
			if (attribute.AttributeClass is not { Name: "CompositeAttribute", } attributeClass
			    || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace)
			{
				continue;
			}

			// The Lifetime named argument is the public Awaiten.Lifetime enum whose members line up with the internal
			// Lifetime enum; a boxed enum surfaces as its underlying int. Absent, the composite defaults to
			// transient so it re-materializes its member array per resolve.
			Lifetime lifetime = Lifetime.Transient;
			foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
			{
				if (argument.Key == "Lifetime" && argument.Value.Value is int value)
				{
					lifetime = (Lifetime)value;
				}
			}

			Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallbackLocation;
			ReportUnsupportedRegistrationKey(attribute, LocationInfo.From(location), AttributeTypeArgument(attribute, attributeClass, 0), diagnostics);
			string? key = NamedKeyArgument(attribute);

			ContributionContext contribution = new(module, inherited ? inheritance : null, location, compilation, sameCompilationModules, diagnostics);
			OpenViaOutcome openVia = ResolveOpenContributionVia(attribute, attributeClass, module, contribution, fallbackLocation, inherited);
			OwnerFactoryCall? via = null;
			if (openVia.Dropped || (openVia.Via is null && UnavailableContribution(contribution, attribute, attributeClass, out via)))
			{
				continue;
			}

			if (attributeClass.IsGenericType)
			{
				if (ContributedPair(attribute, attributeClass) is ({ } composite, { } service))
				{
					// A module of another assembly builds a composite of an internal service into the service's handle, which it then fronts.
					INamedTypeSymbol? handle = HandleProducedBy(via, service, compilation);
					INamedTypeSymbol composed = handle ?? service;
					closed.Add(new CompositeRegistration(
						composed.ToDisplayString(FullyQualified), composed, composite, lifetime, location, key, via, module, handle is not null, handle is not null ? service : null));
				}
			}
			else if (attribute.ConstructorArguments.Length == 2
			         && attribute.ConstructorArguments[0].Value is INamedTypeSymbol openComposite
			         && attribute.ConstructorArguments[1].Value is INamedTypeSymbol openService
			         && TryReadOpenGenericPair(openComposite, openService, LocationInfo.From(location), diagnostics) is { } pair)
			{
				bool composesHandles = false;
				foreach ((INamedTypeSymbol composite, OwnerFactoryCall handleVia, INamedTypeSymbol handle, INamedTypeSymbol service) in HandleClosings(module, inherited, pair.Mapped, pair.Service, attributeClass.Name, compilation))
				{
					closed.Add(new CompositeRegistration(handle.ToDisplayString(FullyQualified), handle, composite, lifetime, location, key, handleVia, module, ThroughHandle: true, HiddenService: service));
					composesHandles = true;
				}

				open.Add(new OpenCompositeRegistration(pair.Service, pair.Mapped, lifetime, location, key, module, openVia.Via, composesHandles, inherited));
			}
		}

		return (closed, open);
	}

	/// <summary>
	///     One <c>[InjectProperty&lt;TImplementation&gt;(name)]</c> entry on the container: the property of the
	///     implementation to fill, plus the same per-property flags an <c>[Inject]</c> attribute carries. The
	///     container-side counterpart of an <c>[Inject]</c> property, matched to a registration by its
	///     implementation type (see <see cref="CollectInjectProperties" />). <see cref="Inherited" /> marks an entry of
	///     an extended base (see <see cref="InheritInjectProperties" />).
	/// </summary>
	private sealed record InjectPropertyEntry(string PropertyName, bool Optional, bool Deferred, string? Key, LocationInfo? Location, bool Inherited = false);

	/// <summary>
	///     Reads the container's <c>[InjectProperty&lt;TImplementation&gt;(name)]</c> and
	///     <c>[InjectProperty(typeof(...), name)]</c> entries into a map from the implementation's fully-qualified
	///     type to its properties to fill, so <see cref="BuildInstance" /> can look them up by
	///     <c>info.ImplementationType</c> - reaching every construction site of that implementation, including one
	///     brought in by a <c>[Scan]</c>. An unbound generic target is keyed by its definition
	///     (<c>Repository&lt;T&gt;</c>), which <see cref="OpenInjectPropertyKey" /> derives from each closing. Reports
	///     <see cref="Diagnostics.TypeofArgumentNotClosedOrOpen">AWT215</see> for a <c>typeof</c> target that is neither
	///     closed nor an unbound generic definition, and
	///     <see cref="Diagnostics.DuplicateInjectProperty">AWT179</see> for a second entry naming the same property of
	///     the same implementation, keeping only the first.
	/// </summary>
	private static Dictionary<string, List<InjectPropertyEntry>> CollectInjectProperties(
		INamedTypeSymbol containerSymbol, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		Dictionary<string, List<InjectPropertyEntry>> map = new(StringComparer.Ordinal);
		foreach (AttributeData attribute in containerSymbol.GetAttributes())
		{
			if (!TryReadInjectPropertyTarget(attribute, compilation, diagnostics, out string? propertyName, out string? implementationType, out LocationInfo? location))
			{
				continue;
			}

			if (!map.TryGetValue(implementationType, out List<InjectPropertyEntry>? entries))
			{
				entries = new List<InjectPropertyEntry>();
				map[implementationType] = entries;
			}

			// AWT170: the Key must be a supported key constant (string, enum or typeof), guarded exactly as on a
			// registration's Key and a [FromKey]; an unsupported one would otherwise be silently dropped to no key.
			ReportUnsupportedRegistrationKey(attribute, location, DisplayInstance(implementationType), diagnostics);

			// AWT179: a property is injected once, so a second entry for it is a copy-paste whose flags would be
			// silently ignored; report it and keep the first.
			if (entries.Any(entry => entry.PropertyName == propertyName))
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.DuplicateInjectProperty,
					location,
					new EquatableArray<string>([propertyName, DisplayInstance(implementationType),])));
				continue;
			}

			entries.Add(new InjectPropertyEntry(
				propertyName,
				NamedFlag(attribute, "Optional"),
				NamedFlag(attribute, "Deferred"),
				NamedKeyArgument(attribute),
				location));
		}

		return map;
	}

	/// <summary>
	///     Reads one <c>[InjectProperty]</c> entry and checks its target: the property name and the key of the
	///     implementation it targets, where an unbound generic target is keyed by its definition. Reports
	///     <see cref="Diagnostics.TypeofArgumentNotClosedOrOpen">AWT215</see> for a <c>typeof</c> target that is neither
	///     closed nor an unbound generic definition, and <see cref="Diagnostics.InjectPropertyNotFound">AWT177</see> for a
	///     name that is no settable property of a generic definition. Returns <see langword="false" /> for a rejected
	///     entry and for an attribute that is not an <c>[InjectProperty]</c>.
	/// </summary>
	private static bool TryReadInjectPropertyTarget(
		AttributeData attribute,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics,
		[NotNullWhen(true)] out string? propertyName,
		[NotNullWhen(true)] out string? implementationType,
		out LocationInfo? location)
	{
		implementationType = null;
		location = null;
		if (!TryReadInjectProperty(attribute, out ITypeSymbol? target, out propertyName, out bool isTypeofForm))
		{
			return false;
		}

		location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation());

		if (isTypeofForm && !IsClosedOrOpenDefinition(target, attribute, "implementation"))
		{
			diagnostics.Add(TypeofArgumentNotClosedOrOpen("InjectProperty", target, location));
			return false;
		}

		if (target is not INamedTypeSymbol implementation)
		{
			return false;
		}

		string type = TypeIdentity(implementation.IsUnboundGenericType ? implementation.OriginalDefinition : implementation, compilation);

		// AWT177 on the definition: every closing shares its members, so a bad name is reported once at the
		// attribute, even when no closing is constructed, instead of once per closing.
		if (implementation.IsUnboundGenericType
		    && !IsSettableProperty(ResolveNamedInstanceMember(implementation.OriginalDefinition, propertyName)))
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.InjectPropertyNotFound,
				location,
				new EquatableArray<string>([propertyName, DisplayInstance(type),])));
			return false;
		}

		implementationType = type;
		return true;
	}

	/// <summary>
	///     Reads the target type and property name of an <c>[InjectProperty&lt;TImplementation&gt;(name)]</c> or an
	///     <c>[InjectProperty(typeof(...), name)]</c>. The target is <see langword="null" /> when the <c>typeof</c>
	///     form passes <see langword="null" />; <paramref name="isTypeofForm" /> tells the forms apart.
	/// </summary>
	private static bool TryReadInjectProperty(
		AttributeData attribute,
		out ITypeSymbol? implementation,
		[NotNullWhen(true)] out string? propertyName,
		out bool isTypeofForm)
	{
		implementation = null;
		propertyName = null;
		isTypeofForm = false;
		if (attribute.AttributeClass is not { Name: "InjectPropertyAttribute", } attributeClass
		    || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace)
		{
			return false;
		}

		if (attributeClass is { IsGenericType: true, TypeArguments.Length: 1, }
		    && attribute.ConstructorArguments.Length == 1)
		{
			implementation = attributeClass.TypeArguments[0];
			propertyName = attribute.ConstructorArguments[0].Value as string;
		}
		else if (!attributeClass.IsGenericType && attribute.ConstructorArguments.Length == 2)
		{
			implementation = attribute.ConstructorArguments[0].Value as ITypeSymbol;
			propertyName = attribute.ConstructorArguments[1].Value as string;
			isTypeofForm = true;
		}

		return propertyName is not null;
	}

	/// <summary>
	///     Whether the <c>typeof</c> argument for <paramref name="parameterName" /> mixes omitted and given type
	///     arguments, like <c>typeof(Outer&lt;int&gt;.Inner&lt;&gt;)</c> or <c>typeof(Outer&lt;&gt;.Inner&lt;int&gt;)</c>.
	///     Read from syntax because C# rejects such a type (CS7003) but Roslyn still binds it to a symbol that cannot be
	///     told apart from a closed type or an unbound definition reliably.
	/// </summary>
	private static bool IsPartiallyOpenTypeof(AttributeData attribute, string parameterName)
	{
		if (attribute.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax { ArgumentList: { } arguments, }
		    || arguments.Arguments
			    .Where(argument => argument.NameEquals is null
			                       && (argument.NameColon is null || argument.NameColon.Name.Identifier.Text == parameterName))
			    .Select(argument => argument.Expression)
			    .FirstOrDefault() is not TypeOfExpressionSyntax typeOf)
		{
			return false;
		}

		List<TypeSyntax> typeArguments = typeOf.Type.DescendantNodesAndSelf()
			.OfType<TypeArgumentListSyntax>()
			.SelectMany(list => list.Arguments)
			.ToList();
		return typeArguments.Any(argument => argument is OmittedTypeArgumentSyntax)
		       && typeArguments.Any(argument => argument is not OmittedTypeArgumentSyntax);
	}

	/// <summary>
	///     Whether <paramref name="member" /> is a property <c>[InjectProperty]</c> can name: a non-indexer property
	///     with a set or init accessor. Whether the container can reach that accessor is checked per closing (AWT136).
	/// </summary>
	private static bool IsSettableProperty([NotNullWhen(true)] ISymbol? member)
		=> member is IPropertySymbol { IsIndexer: false, SetMethod: not null, };

	/// <summary>
	///     Reports <see cref="Diagnostics.InjectPropertyImplementationNotRegistered">AWT180</see> for every
	///     <c>[InjectProperty&lt;TImplementation&gt;]</c> entry whose implementation type matches no registration in
	///     <paramref name="implOrder" />, so the entry is never applied (an unregistered type, or an unexpanded
	///     closed generic). Run once every instance is built, the container-side analogue of
	///     <see cref="ReportUnappliedContextualBindings" />. A Factory/Instance-produced implementation is registered
	///     (present in <paramref name="implOrder" />) and is AWT178 at its <c>BuildInstance</c>, so it is not
	///     reported here. The registered set carries each impl's <em>real</em> type (<c>info.Symbol</c>) as well as
	///     its <c>ImplementationType</c>: a decorator chain link's <c>ImplementationType</c> is a synthetic
	///     <c>Type@__dec:…</c> identity, so keying only on that would misreport an entry targeting the decorator type
	///     as unmatched. (Property injection is not applied to a decorator wrapper; see the <c>[InjectProperty]</c>
	///     XML doc.) It also carries each impl's generic definition, the key of an open-generic entry, so such an
	///     entry is unmatched only when no closing of its definition is constructed. An entry inherited from an
	///     extended base is not reported, but for a <c>Deferred</c> one a handle cannot take (below): the base reports
	///     its own, and one whose implementation the extending container replaced is meant to fall away. An implementation a module of another assembly builds into an
	///     instance handle takes the entries through the handle (see <see cref="WithHandleEntries" />), all but a
	///     <c>Deferred</c> one, which is AWT178, an inherited one too, unless a handle that takes the entries reported it as
	///     AWT181 or AWT177 (<paramref name="reportedHandleEntries" />), as is every entry when the handle cannot take them (see
	///     <see cref="HandleAssignment" />), with the reason the module handles it (see <see cref="InstanceHandleReason" />)
	///     and why the entry cannot reach it. So is an entry for its generic definition that no constructed closing
	///     matches, which then names the closing the handle builds. The handle of one of several producers of a service
	///     is keyed by the service it names, like a container of the module's own assembly keys the producer, so its
	///     entry is the plain AWT178 of a factory-produced type. An alias that serves the handle's instance
	///     (see <see cref="ThroughAlias" />) registers nothing here, although it may return the implementation itself.
	/// </summary>
	private static void ReportUnmatchedInjectProperties(
		Dictionary<string, List<InjectPropertyEntry>> injectProperties,
		List<ImplInfo> implOrder,
		HashSet<InjectPropertyEntry> reportedHandleEntries,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics)
	{
		if (injectProperties.Count == 0)
		{
			return;
		}

		HashSet<string> registered = new(StringComparer.Ordinal);
		Dictionary<string, (INamedTypeSymbol Module, INamedTypeSymbol? Implementation, bool Scan, (IMethodSymbol? Alias, string? Gap) Assignment)> handled = new(StringComparer.Ordinal);
		foreach (ImplInfo info in implOrder.Where(info => !info.ImplementationType.Contains(AliasKeyMarker)))
		{
			registered.Add(info.ImplementationType);
			registered.Add(TypeIdentity(info.Symbol, compilation));
			registered.Add(TypeIdentity(info.Symbol.OriginalDefinition, compilation));
			if (info.Origin is { } module && info.ImplementationType.IndexOf(HiddenKeyMarker, StringComparison.Ordinal) is var marker and > 0)
			{
				bool scan = info.Hidden is { } hidden && info.ProductionMember == InInternals(ModuleFactoryName(hidden));
				(IMethodSymbol? Alias, string? Gap) assignment = HandleAssignment(info, compilation);
				Handle(RegisteredType(info.ImplementationType.Substring(0, marker)), (module, info.Hidden, scan, assignment));
				if (info.Hidden is { IsGenericType: true, } closed)
				{
					Handle(TypeIdentity(closed.OriginalDefinition, compilation), (module, closed, scan, assignment));
				}
			}
		}

		// The closings of a definition may be built by several handles: one that cannot take the entries is not replaced
		// by one that can, so they are reported for it even when another closing's handle sets them.
		void Handle(string key, (INamedTypeSymbol Module, INamedTypeSymbol? Implementation, bool Scan, (IMethodSymbol? Alias, string? Gap) Assignment) owner)
		{
			if (!handled.TryGetValue(key, out (INamedTypeSymbol Module, INamedTypeSymbol? Implementation, bool Scan, (IMethodSymbol? Alias, string? Gap) Assignment) kept)
			    || kept.Assignment.Alias is not null
			    || owner.Assignment.Alias is null)
			{
				handled[key] = owner;
			}
		}

		foreach (KeyValuePair<string, List<InjectPropertyEntry>> implementation in injectProperties)
		{
			if (registered.Contains(implementation.Key))
			{
				continue;
			}

			bool isHandled = handled.TryGetValue(implementation.Key, out (INamedTypeSymbol Module, INamedTypeSymbol? Implementation, bool Scan, (IMethodSymbol? Alias, string? Gap) Assignment) owner);
			// A handle that takes the entries sets them through its alias (see WithHandleEntries), all but a Deferred one,
			// which is AWT178 unless it already reported the entry as AWT181 or AWT177. An entry the handle of one closing
			// reported is still reported for a closing whose handle cannot take the entries.
			bool assigned = isHandled && owner.Assignment.Alias is not null;
			List<ModuleRegistration> registrations = isHandled && !owner.Scan
				? ConstructedModuleRegistrations(owner.Module, compilation, members: true)
					.Where(registration => SymbolEqualityComparer.Default.Equals(registration.Implementation, owner.Implementation))
					.ToList()
				: [];
			string target = isHandled && owner.Implementation is { IsGenericType: true, } closed && TypeIdentity(closed.OriginalDefinition, compilation) == implementation.Key
				? Display(closed.ToDisplayString(FullyQualified))
				: DisplayInstance(implementation.Key);
			foreach (InjectPropertyEntry entry in implementation.Value.Where(entry => assigned
				         ? entry.Deferred && !reportedHandleEntries.Contains(entry)
				         : !entry.Inherited))
			{
				// A Factory or Instance member builds the instance whole in every assembly, so the entry is the plain AWT178
				// a container of the module's own assembly reports, which no InternalsVisibleTo grant would change.
				diagnostics.Add(!isHandled
					? new DiagnosticInfo(
						Diagnostics.InjectPropertyImplementationNotRegistered,
						entry.Location,
						new EquatableArray<string>([entry.PropertyName, DisplayInstance(implementation.Key),]))
					: registrations.Count > 0 && registrations[0].Member is not null
						? new DiagnosticInfo(
							Diagnostics.InjectPropertyOnNonConstructed,
							entry.Location,
							new EquatableArray<string>([entry.PropertyName, target,]))
						: new DiagnosticInfo(
							Diagnostics.InjectPropertyOnOwnerFactory,
							entry.Location,
							new EquatableArray<string>([
								entry.PropertyName, target, Display(owner.Module.ToDisplayString(FullyQualified)),
								Reason(),
							])));
			}

			string Reason()
			{
				string reason = InstanceHandleReason(owner.Module, owner.Implementation, owner.Scan, registrations, compilation);
				return (assigned ? DeferredThroughHandle : owner.Assignment.Gap) is { } gap ? $"{reason}, and {gap}" : reason;
			}
		}
	}

	/// <summary>Why a container sets no <c>Deferred</c> entry on the implementation an instance handle holds, for AWT178.</summary>
	private const string DeferredThroughHandle = "the container sets no Deferred entry on the instance a generated handle holds";

	/// <summary>
	///     Why <paramref name="module" /> of another assembly builds <paramref name="implementation" /> into an instance
	///     handle (see <see cref="HandledShape" />), for AWT178: one of its scans exposes it under several services
	///     (<paramref name="scan" />), one of its <paramref name="registrations" /> names a service this compilation cannot
	///     name, or else other modules of its assembly register it too, so they share one instance. Several services alone
	///     are no reason here, since the module's factory returns an implementation this compilation can name, which serves
	///     them all, and an implementation the module produces through a member is the plain AWT178 instead.
	/// </summary>
	private static string InstanceHandleReason(INamedTypeSymbol module, INamedTypeSymbol? implementation, bool scan, List<ModuleRegistration> registrations, Compilation compilation)
	{
		if (scan)
		{
			return "a scan of the module exposes it under several services, which share one instance of it through a generated handle";
		}

		if (registrations.Find(registration => !compilation.IsSymbolAccessibleWithin(registration.Service, compilation.Assembly)) is { } hidden)
		{
			return $"it is registered under the internal service '{Display(hidden.Service.ToDisplayString(FullyQualified))}', which a container in another assembly reaches only through a generated handle";
		}

		List<string> others = TypesWith(module.ContainingAssembly.GlobalNamespace, "ModuleAttribute")
			.Where(other => !SymbolEqualityComparer.Default.Equals(other, module)
			                && IsExternallyAccessible(other)
			                && ConstructedModuleRegistrations(other, compilation, members: true).Any(registration => SymbolEqualityComparer.Default.Equals(registration.Implementation, implementation)))
			.Select(other => $"'{Display(other.ToDisplayString(FullyQualified))}'")
			.OrderBy(other => other, StringComparer.Ordinal)
			.ToList();
		return others.Count == 0
			? "the modules of its assembly share one instance of it through a generated handle"
			: $"{(others.Count == 1 ? "the module" : "the modules")} {string.Join(", ", others)} of its assembly {(others.Count == 1 ? "registers" : "register")} it too, and the modules share one instance of it through a generated handle";
	}

	/// <summary>
	///     Resolves how a registration produces its instance: a <c>Factory</c> method, a pre-built <c>Instance</c>
	///     member, or a constructor when neither is set. Setting both drives AWT110. An explicit empty string is
	///     kept (not treated as absent) so it surfaces as AWT108/AWT109.
	/// </summary>
	private static (ProductionKind Production, string? Member, bool Conflicting) ReadProduction(AttributeData attribute)
	{
		string? factory = NamedArgument(attribute, "Factory");
		string? instanceMember = NamedArgument(attribute, "Instance");
		if (factory is not null && instanceMember is not null)
		{
			return (ProductionKind.Factory, factory, true);
		}

		if (factory is not null)
		{
			return (ProductionKind.Factory, factory, false);
		}

		if (instanceMember is not null)
		{
			return (ProductionKind.Instance, instanceMember, false);
		}

		return (ProductionKind.Constructor, null, false);
	}
}
