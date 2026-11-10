using System.Collections.Immutable;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     The marker inside the synthetic implementation identity of an alias registration
	///     (<c>&lt;service&gt;@alias:&lt;implementation&gt;</c>, see <see cref="ThroughHandle" />), which
	///     <c>DisplayInstance</c> trims from diagnostics.
	/// </summary>
	private const string AliasKeyMarker = "@alias:";

	/// <summary>
	///     The marker inside the implementation identity of an instance handle registration
	///     (<c>&lt;implementation&gt;@hidden:&lt;module that declares the handle&gt;</c>), which keeps it apart from a
	///     registration that constructs the implementation, and which <c>DisplayInstance</c> trims from diagnostics.
	/// </summary>
	private const string HiddenKeyMarker = "@hidden:";

	/// <summary>
	///     Rewrites each diagnostic argument that names a generated handle of an imported module of another assembly to the
	///     internal service it holds, as <c>Lib.BeanStore (internal to module Lib.CoffeeModule)</c>, which the message
	///     quotes like any other type, so a message never names a type the user did not write. A handle inside a longer
	///     argument, such as a type argument, is named by the service alone. A generated hook wrapper is named by the
	///     hook the module declares. <paramref name="called" /> holds the members the container's graph calls (see
	///     <see cref="CalledMembers" />).
	/// </summary>
	private static void NameHiddenServices(INamedTypeSymbol container, Compilation compilation, HashSet<string> called, List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
	{
		List<ImportedModule> imported = CollectImportedModules(container, new List<DiagnosticInfo>());
		if (ReadExtends(container) is { } baseContainer)
		{
			// An import of a base read from metadata has no syntax, so it reports at the container's Extends, like InheritBase.
			ExtendsTarget(container, out AttributeData? extends);
			Location? location = NamedArgumentLocation(extends!, "Extends", cancellationToken);
			imported.AddRange(CollectImportedModules(baseContainer, new List<DiagnosticInfo>())
				.Select(module => module with { ImportLocation = module.ImportLocation ?? location, }));
		}

		List<INamedTypeSymbol> modules = imported
			.Select(module => module.Symbol)
			.Where(module => !SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly))
			.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
			.ToList();
		List<INamedTypeSymbol> providers = ReportMissingHandleProviders(imported, modules, compilation, called, diagnostics);
		if (diagnostics.Count == 0)
		{
			return;
		}

		Dictionary<string, (string Service, string Module)> handles = new(StringComparer.Ordinal);
		Dictionary<string, string> hooks = new(StringComparer.Ordinal);
		foreach (INamedTypeSymbol module in modules.Concat(providers))
		{
			string moduleName = Display(module.ToDisplayString(FullyQualified));
			foreach (ModuleRegistration registration in ConstructedModuleRegistrations(module, compilation, members: true))
			{
				foreach (bool release in new[] { false, true, })
				{
					if (NamedArgument(registration.Attribute, release ? "OnRelease" : "OnActivated") is { } hook)
					{
						hooks[$"{moduleName}.{InternalsClass}.{HiddenHookName(registration.Implementation, release, hook)}"] = $"{moduleName}.{hook}";
						hooks[$"{moduleName}.{InternalsClass}.{HiddenHookName(registration.Implementation, release, hook, registration.Member)}"] = $"{moduleName}.{hook}";
						hooks[$"{moduleName}.{InternalsClass}.{RegistrationHookWrapperName(registration.Implementation, release, hook)}"] = $"{moduleName}.{hook}";
					}
				}

				string service = Display(registration.Service.ToDisplayString(FullyQualified));
				handles[$"{moduleName}.{InternalsClass}.{HiddenHandleName(registration.Service)}"] = (service, moduleName);
				if (((registration.Member is { } member ? HiddenProducer(module, HiddenFactoryName(registration.Implementation, member), compilation) : null)
				     ?? HiddenProducer(module, HiddenFactoryName(registration.Implementation), compilation)) is { Handle: { } instance, })
				{
					handles[Display(instance.ToDisplayString(FullyQualified))] = (Display(registration.Implementation.ToDisplayString(FullyQualified)), moduleName);
				}
			}

			foreach (INamedTypeSymbol handle in module.GetTypeMembers(InternalsClass).SelectMany(internals => internals.GetTypeMembers()))
			{
				if (ClosingHandleOf(handle) is var (_, closing))
				{
					handles[Display(handle.ToDisplayString(FullyQualified))] = (Display(closing), moduleName);
				}
			}

			foreach (AttributeData scanned in module.GetAttributes().Where(attribute => attribute.AttributeClass is { Name: "GeneratedScanRegistrationAttribute", }))
			{
				if (scanned.ConstructorArguments.Length == 1
				    && scanned.ConstructorArguments[0].Value is string factory
				    && ScanInstanceHandleOf(module, factory, NamedArgument(scanned, "Implementation"), compilation) is var (implementation, handle, _))
				{
					handles[Display(handle.ReturnType.ToDisplayString(FullyQualified))] = (Display(implementation.ToDisplayString(FullyQualified)), moduleName);
				}
			}
		}

		if (handles.Count == 0 && hooks.Count == 0)
		{
			return;
		}

		for (int index = 0; index < diagnostics.Count; index++)
		{
			DiagnosticInfo diagnostic = diagnostics[index];
			string[] arguments = diagnostic.MessageArgs.AsArray();
			string[] renamed = arguments.Select(argument => Rename(hooks.Where(hook => argument.Contains(hook.Key)).Aggregate(argument, (named, hook) => named.Replace(hook.Key, hook.Value)))).ToArray();
			if (!renamed.SequenceEqual(arguments))
			{
				diagnostics[index] = diagnostic with { MessageArgs = new EquatableArray<string>(renamed), };
			}
		}

		string Rename(string argument)
		{
			foreach (KeyValuePair<string, (string Service, string Module)> handle in handles.Where(handle => argument.Contains(handle.Key)))
			{
				string named = $"{handle.Value.Service} (internal to module {handle.Value.Module})";
				foreach (string variant in new[] { ".BothDisposable", ".AsyncDisposable", ".Disposable", string.Empty, })
				{
					string type = handle.Key + variant;
					argument = argument == type
						? named
						: argument.Replace($"'{type}'", $"'{named}'").Replace(type, handle.Value.Service);
				}
			}

			return argument;
		}
	}

	/// <summary>
	///     Reports AWT238 at the <c>[Import]</c> of a module of another assembly whose generated members take the handle
	///     of an internal service that another module of that assembly declares, when no imported module of that assembly
	///     registers the service and the container does not import that other module, and drops the AWT101, AWT242, AWT123
	///     or AWT210 the missing handle registration causes, since the import is the one fix. Only a member among the
	///     <paramref name="called" /> members of the graph needs its handles, and of the factory of a decorator or composite
	///     the graph leaves out only the handle of the service it returns, whose missing registration left it out. Any
	///     other member the graph never calls asks for no import, such as the factory of a registration the
	///     container replaces, or the factory and hook wrappers of a scan match that shares the instance of another
	///     registration's handle through an alias (see <see cref="ShareHandledInstances" />).
	///     Returns the modules that declare a handle an imported module takes, which name those handles in diagnostics.
	/// </summary>
	private static List<INamedTypeSymbol> ReportMissingHandleProviders(List<ImportedModule> imported, List<INamedTypeSymbol> modules, Compilation compilation, HashSet<string> called, List<DiagnosticInfo> diagnostics)
	{
		List<INamedTypeSymbol> providers = new();
		foreach (ImportedModule import in imported.Where(import => modules.Contains(import.Symbol, SymbolEqualityComparer.Default)))
		{
			INamedTypeSymbol module = import.Symbol;
			string internals = $"{module.ToDisplayString(FullyQualified)}.{InternalsClass}.";
			List<IMethodSymbol> methods = (module.GetTypeMembers(InternalsClass).FirstOrDefault()?.GetMembers() ?? ImmutableArray<ISymbol>.Empty)
				.OfType<IMethodSymbol>()
				.ToList();
			// An uncalled member still names its handles, as in the AWT141 of a match it skips. The graph leaves out a
			// decorator or composite when its service has no registration, which only the missing provider of the
			// handle of that service causes, so of an uncalled one only the service it returns counts.
			HashSet<INamedTypeSymbol> needed = new(
				methods.SelectMany(method => called.Contains(internals + method.Name)
					? TypesOf(method)
					: method.Name.StartsWith("Decorator_", StringComparison.Ordinal) || method.Name.StartsWith("Composite_", StringComparison.Ordinal)
						? TypesIn(method.ReturnType)
						: []),
				SymbolEqualityComparer.Default);
			foreach (INamedTypeSymbol handle in methods.SelectMany(TypesOf).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
			{
				if (handle is not { ContainingType: { Name: InternalsClass, ContainingType: { } provider, }, }
				    || !handle.Name.StartsWith("Hidden_", StringComparison.Ordinal)
				    || SymbolEqualityComparer.Default.Equals(provider, module))
				{
					continue;
				}

				if (!providers.Contains(provider, SymbolEqualityComparer.Default))
				{
					providers.Add(provider);
				}

				if (!needed.Contains(handle)
				    || modules.Contains(provider, SymbolEqualityComparer.Default)
				    || modules.Any(registering => SymbolEqualityComparer.Default.Equals(registering.ContainingAssembly, provider.ContainingAssembly)
				                                  && (ConstructedModuleRegistrations(registering, compilation, members: true).Any(registration => HiddenHandleName(registration.Service) == handle.Name)
				                                      || ComposesHandle(registering, handle))))
				{
					continue;
				}

				string missing = Display(handle.ToDisplayString(FullyQualified));
				diagnostics.RemoveAll(diagnostic => (diagnostic.Descriptor == Diagnostics.MissingDependency
				                                     || diagnostic.Descriptor == Diagnostics.RelationshipOverImportedService
				                                     || diagnostic.Descriptor == Diagnostics.DecoratedServiceNotRegistered
				                                     || diagnostic.Descriptor == Diagnostics.DecoratedServiceNotImported
				                                     || diagnostic.Descriptor == Diagnostics.DecoratedKeyNotRegistered)
				                                    && diagnostic.MessageArgs.AsArray().Any(argument => argument.StartsWith(missing, StringComparison.Ordinal)));
				string service = ConstructedModuleRegistrations(provider, compilation, members: true)
					.Select(registration => registration.Service)
					.FirstOrDefault(candidate => HiddenHandleName(candidate) == handle.Name)?.ToDisplayString(FullyQualified) ?? handle.ToDisplayString(FullyQualified);
				DiagnosticInfo diagnostic = new(
					Diagnostics.ModuleHandleProviderNotImported,
					LocationInfo.From(import.ImportLocation),
					new EquatableArray<string>([Display(module.ToDisplayString(FullyQualified)), Display(service), Display(provider.ToDisplayString(FullyQualified)),]));
				if (!diagnostics.Contains(diagnostic))
				{
					diagnostics.Add(diagnostic);
				}
			}
		}

		return providers;

		// A closed [Composite] of the module registers its service too: its factory builds the handle, fronting no members when no module registers any.
		static bool ComposesHandle(INamedTypeSymbol module, INamedTypeSymbol handle)
			=> module.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "CompositeAttribute", } attributeClass
			                                           && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace
			                                           && ContributedPair(attribute, attributeClass) is (not null, { } service)
			                                           && HiddenHandleName(service) == handle.Name);

		static IEnumerable<INamedTypeSymbol> TypesOf(IMethodSymbol method)
			=> method.Parameters.Select(parameter => parameter.Type).Concat([method.ReturnType,]).SelectMany(TypesIn);

		static IEnumerable<INamedTypeSymbol> TypesIn(ITypeSymbol type)
			=> type switch
			{
				IArrayTypeSymbol array => TypesIn(array.ElementType),
				INamedTypeSymbol named => new[] { named, }.Concat(named.TypeArguments.SelectMany(TypesIn)),
				_ => [],
			};
	}

	/// <summary>
	///     The qualified members the generated code of <paramref name="instances" /> calls, without the type arguments of
	///     a closing: the production members, which include the factories of the decorators and composites it applies,
	///     the lifecycle hooks, the async initializer wrappers, the <c>When</c> predicates and the deferred setters.
	/// </summary>
	private static HashSet<string> CalledMembers(List<InstanceModel> instances)
	{
		HashSet<string> called = new(StringComparer.Ordinal);
		foreach (InstanceModel instance in instances)
		{
			IEnumerable<string?> members = new[] { instance.ProductionMember, instance.OnActivated, instance.OnRelease, instance.When?.Member, }
				.Concat(instance.AsyncInitializers.AsArray().Where(initializer => initializer.Wrapper).Select(initializer => $"{initializer.Type}.{initializer.Method}"))
				.Concat(instance.InjectedMembers.AsArray().Select(member => member.Setter));
			foreach (string member in members.OfType<string>())
			{
				int generic = member.IndexOf('<');
				called.Add(generic < 0 ? member : member.Substring(0, generic));
			}
		}

		return called;
	}

	/// <summary>
	///     Gives each instance handle registration (see <see cref="ThroughHandle" />) the directives of the first
	///     registration of its module that survives coalescing, in precedence order, as a container in the module's own
	///     assembly takes them from the first registration it keeps: an overridable default whose service the container
	///     overrides is dropped and contributes nothing. A handle whose surviving registrations are all overridable
	///     defaults yields to the handle of another module that registers the implementation. A handle with no surviving
	///     registration is dropped with its hooks and initializers, since nothing builds the implementation in the module's
	///     own assembly either, unless no other handle of the implementation is kept and a registration or scan match the
	///     container shares with the handle still takes it.
	///     A later surviving registration that contradicts the first is AWT107 or AWT166 when one of the two is an
	///     overridable default, which the library's build leaves to the container.
	/// </summary>
	private static void SettleHandleDirectives(List<RawRegistration> raw, INamedTypeSymbol container, List<DiagnosticInfo> diagnostics)
	{
		HashSet<int> dropped = DroppedOverridableDefaults(raw);
		List<RawRegistration> aliases = InPrecedenceOrder(raw)
			.Where(entry => entry.Registration.ImplementationType.Contains(AliasKeyMarker) && !dropped.Contains(entry.Index))
			.Select(entry => entry.Registration)
			.ToList();
		List<int> unused = new();
		for (int index = 0; index < raw.Count; index++)
		{
			if (raw[index] is not { Hidden: not null, Origin: { } module, } handle || !handle.ImplementationType.Contains(HiddenKeyMarker))
			{
				continue;
			}

			string marker = AliasKeyMarker + HandledIdentity(handle);
			List<RawRegistration> own = aliases
				.Where(alias => alias.Aliased is not null && SymbolEqualityComparer.Default.Equals(alias.Origin, module) && alias.ImplementationType.EndsWith(marker, StringComparison.Ordinal))
				.Select(alias => alias.Aliased!)
				.ToList();
			if (own.Count == 0)
			{
				unused.Add(index);
				continue;
			}

			HashSet<string> lifetimes = new(StringComparer.Ordinal);
			HashSet<string> productions = new(StringComparer.Ordinal);
			HashSet<string> directives = new(StringComparer.Ordinal);
			foreach (RawRegistration later in own.Skip(1).Where(later => later.Weak || own[0].Weak))
			{
				ReportCoalescingConflicts(NewImplInfo(own[0], container), later, container, lifetimes, productions, directives, diagnostics);
			}

			raw[index] = handle with
			{
				Weak = own.All(registration => registration.Weak),
				Lifetime = own[0].Lifetime,
				Eager = own[0].Eager,
				SuppressDisposal = own[0].SuppressDisposal,
				OnActivated = own[0].OnActivated,
				OnRelease = own[0].OnRelease,
			};
		}

		HashSet<string> kept = new(raw.Where((registration, index) => registration.Hidden is not null && registration.ImplementationType.Contains(HiddenKeyMarker) && !unused.Contains(index))
			.Select(HandledIdentity), StringComparer.Ordinal);
		List<int> removed = new();
		foreach (int index in unused)
		{
			string handled = HandledIdentity(raw[index]);
			if (aliases.Any(alias => alias.ImplementationType.EndsWith(AliasKeyMarker + handled, StringComparison.Ordinal)) && kept.Add(handled))
			{
				raw[index] = raw[index] with { Weak = true, Eager = false, };
			}
			else
			{
				removed.Add(index);
			}
		}

		for (int position = removed.Count - 1; position >= 0; position--)
		{
			raw.RemoveAt(removed[position]);
		}
	}

	/// <summary>
	///     Reports the conflicts a container in the modules' own assembly reports between the producers of one
	///     implementation when a module of another assembly builds its members that name only their service through
	///     handles of their own (see <see cref="ThroughHandle" />) beside the shared handle of the implementation: an
	///     imported module that names the implementation makes every producer of it one instance there (AWT107, AWT111,
	///     AWT166), but a module with several such members has no shared handle to build them into, so the handles do
	///     not coalesce. Each conflict is reported once per implementation, against its first handle in precedence order.
	/// </summary>
	private static void ReportProducersBesideSharedHandles(List<RawRegistration> raw, INamedTypeSymbol container, List<DiagnosticInfo> diagnostics)
	{
		foreach (List<RawRegistration> handles in InPrecedenceOrder(raw)
			         .Select(entry => entry.Registration)
			         .Where(registration => registration.Hidden is not null && registration.ImplementationType.Contains(HiddenKeyMarker) && !IsScanHandle(registration))
			         .GroupBy<RawRegistration, INamedTypeSymbol>(registration => registration.Hidden!, SymbolEqualityComparer.Default)
			         .Select(group => group.ToList())
			         .Where(handles => handles.Exists(IsProducerHandle) && !handles.TrueForAll(IsProducerHandle)))
		{
			string shared = handles.First(handle => !IsProducerHandle(handle)).ImplementationType;
			HashSet<string> lifetimes = new(StringComparer.Ordinal);
			HashSet<string> productions = new(StringComparer.Ordinal);
			HashSet<string> directives = new(StringComparer.Ordinal);
			// Coalescing reports the conflicts between the shared handles, and sharing a registration with a handle reported its own already.
			List<RawRegistration> coalesced = handles.FindAll(handle => handle.ImplementationType == shared);
			foreach (RawRegistration later in coalesced.Skip(1))
			{
				ReportCoalescingConflicts(NewImplInfo(coalesced[0], container), later, container, lifetimes, productions, directives, new List<DiagnosticInfo>());
			}

			if (Reported(Diagnostics.ConflictingLifetime))
			{
				lifetimes.Add(shared);
			}

			if (Reported(Diagnostics.ConflictingProduction))
			{
				productions.Add(shared);
			}

			foreach (RawRegistration later in handles.Skip(1).Where(later => later.ImplementationType != handles[0].ImplementationType))
			{
				ReportCoalescingConflicts(NewImplInfo(handles[0], container), later with { ImplementationType = shared, }, container, lifetimes, productions, directives, diagnostics);
			}

			bool Reported(DiagnosticDescriptor descriptor)
				=> diagnostics.Exists(diagnostic => diagnostic.Descriptor == descriptor && diagnostic.MessageArgs.AsArray()[0] == DisplayInstance(shared));
		}

		static bool IsProducerHandle(RawRegistration handle)
			=> HandledIdentity(handle).Contains(MemberKeyMarker);
	}

	/// <summary>
	///     Marks each alias (see <see cref="ThroughAlias" />) that takes a transient instance handle the container tracks
	///     for teardown, or a factory of one for an implementation with <c>[Arg]</c> parameters
	///     (<see cref="InstanceModel.TracksHandleDisposal" />): building the alias builds and tracks the implementation, so
	///     strict lifetime safety withholds it on the Root, as it withholds the implementation in a container of the
	///     module's own assembly, while the handle keeps owning the disposal.
	/// </summary>
	private static void MarkAliasesOfTrackedHandles(List<InstanceModel> instances, Dictionary<ServiceKey, string> serviceToImpl, Dictionary<string, int> implToIndex)
	{
		for (int index = 0; index < instances.Count; index++)
		{
			if (instances[index].ImplementationType.Contains(AliasKeyMarker)
			    && instances[index].ConstructorParameters.AsArray().Any(parameter => parameter.Kind is DependencyKind.Direct or DependencyKind.Func
			                                                                         && serviceToImpl.TryGetValue(new ServiceKey(parameter.ServiceType, parameter.Key), out string? handle)
			                                                                         && implToIndex.TryGetValue(handle, out int handleIndex)
			                                                                         && instances[handleIndex] is { Lifetime: Lifetime.Transient, } tracked
			                                                                         && (tracked.NeedsDisposal || tracked.HasReleaseHook)))
			{
				instances[index] = instances[index] with { TracksHandleDisposal = true, };
			}
		}
	}

	/// <summary>
	///     Serves each alias (see <see cref="ThroughAlias" />) that builds an async-tainted instance handle through a factory
	///     (<paramref name="tainted" />) from the alias's async counterpart, which awaits an async factory of the handle, so
	///     the alias is async-tainted like the implementation in a container of the module's own assembly. Returns whether
	///     any alias changed, since its taint then has to propagate.
	/// </summary>
	private static bool AwaitAsyncHandlesOfAliases(List<InstanceModel> instances, bool[] tainted, Dictionary<ServiceKey, string> serviceToImpl, Dictionary<string, int> implToIndex)
	{
		bool changed = false;
		for (int index = 0; index < instances.Count; index++)
		{
			ParameterModel[] parameters = instances[index].ConstructorParameters.AsArray();
			int factory = Array.FindIndex(parameters, parameter => parameter.Kind == DependencyKind.Func);
			if (!instances[index].ImplementationType.Contains(AliasKeyMarker)
			    || factory < 0
			    || !serviceToImpl.TryGetValue(new ServiceKey(parameters[factory].ServiceType, parameters[factory].Key), out string? handle)
			    || !implToIndex.TryGetValue(handle, out int handleIndex)
			    || !tainted[handleIndex])
			{
				continue;
			}

			ParameterModel[] awaited = parameters.ToArray();
			awaited[factory] = awaited[factory] with { Kind = DependencyKind.FuncTask, };
			instances[index] = instances[index] with
			{
				ProductionMember = instances[index].ProductionMember + "Async",
				ConstructorParameters = new EquatableArray<ParameterModel>(awaited),
				IsAsyncFactory = true,
			};
			changed = true;
		}

		return changed;
	}

	/// <summary>
	///     The instance handle an alias (see <see cref="ThroughAlias" />) serves its instance from, directly or through a
	///     factory, or <paramref name="index" /> itself for any other instance, so a check of what a relationship reaches
	///     judges and names the implementation, as a container in the module's own assembly does.
	/// </summary>
	private static int AliasedInstance(int index, List<InstanceModel> instances, Dictionary<ServiceKey, string> serviceToImpl, Dictionary<string, int> implToIndex)
		=> instances[index].ImplementationType.Contains(AliasKeyMarker)
		   && instances[index].ConstructorParameters.AsArray().FirstOrDefault(parameter => parameter.Kind is DependencyKind.Direct or DependencyKind.Func or DependencyKind.FuncTask) is { } handle
		   && serviceToImpl.TryGetValue(new ServiceKey(handle.ServiceType, handle.Key), out string? implementation)
		   && implToIndex.TryGetValue(implementation, out int handleIndex)
			? handleIndex
			: index;

	/// <summary>
	///     The identity of what an instance handle registration holds, before its <see cref="HiddenKeyMarker" />, which the
	///     identities of its aliases end with (see <see cref="ThroughAlias" />).
	/// </summary>
	private static string HandledIdentity(RawRegistration handle)
		=> handle.ImplementationType.Substring(0, handle.ImplementationType.IndexOf(HiddenKeyMarker, StringComparison.Ordinal));

	/// <summary>
	///     Whether the implementation identities <paramref name="first" /> and <paramref name="second" /> serve one
	///     instance: they are equal, or both are aliases (see <see cref="ThroughAlias" />) of the instance of one handle,
	///     such as a module's alias under its service and a container's alias under the implementation, which a container
	///     of the module's own assembly coalesces into one registration of the implementation.
	/// </summary>
	private static bool ServeOneInstance(string first, string second)
		=> first == second || (AliasedIdentity(first) is { } aliased && aliased == AliasedIdentity(second));

	/// <summary>The identity of what the alias <paramref name="identity" /> serves (see <see cref="ThroughAlias" />), or <see langword="null" /> for any other identity.</summary>
	private static string? AliasedIdentity(string identity)
	{
		int marker = identity.IndexOf(AliasKeyMarker, StringComparison.Ordinal);
		return marker < 0 ? null : identity.Substring(marker + AliasKeyMarker.Length);
	}

	/// <summary>
	///     <paramref name="registration" /> served through <paramref name="alias" />, the generated member of
	///     <paramref name="module" /> that returns the instance of <paramref name="implementation" /> from its instance
	///     handle. Its identity names the alias's type and the implementation, or the <paramref name="handled" /> identity
	///     of one of several producers of a service (see <see cref="ThroughHandle" />), and it owns nothing.
	/// </summary>
	private static RawRegistration ThroughAlias(RawRegistration registration, INamedTypeSymbol module, INamedTypeSymbol implementation, IMethodSymbol alias, Compilation compilation, string? handled = null)
	{
		INamedTypeSymbol aliasType = (INamedTypeSymbol)alias.ReturnType;
		return registration with
		{
			ImplementationType = $"{TypeIdentity(aliasType, compilation)}{AliasKeyMarker}{handled ?? TypeIdentity(implementation, compilation)}",
			Implementation = aliasType,
			Origin = module,
			Production = ProductionKind.Factory,
			ProductionMember = InInternals(alias.Name),
			Eager = false,
			OnActivated = null,
			OnRelease = null,
			OnActivatedMarkers = null,
			OnReleaseMarkers = null,
			SuppressDisposal = false,
			GreedyConstructor = false,
			GenericFactory = false,
			Traits = new ImplementationTraits(default, false, false),
			TraitsExact = true,
			ConstructVia = null,
			ConstructViaType = null,
			Constructor = null,
			ScanImplementation = null,
			Hidden = null,
			HiddenProduction = null,
		};
	}

	/// <summary>
	///     Lets a registration that constructs or produces an implementation a module of another assembly builds through
	///     a generated handle share the handle's instance, as a container in the module's own assembly coalesces both into
	///     one instance: a registration of the container, or of a module that can name the implementation. It is served
	///     through the alias that returns the implementation from its instance handle, with the handle's lifetime. The two
	///     no longer share an identity, so the checks coalescing would run between them run here (AWT107, AWT111, AWT166).
	///     A scan's handle yields to the registration in <paramref name="yieldedTo" /> (see <see cref="YieldScanHandles" />),
	///     which therefore comes first. A directive of the registration that the handle cannot run, or a missing alias, is AWT241.
	///     A scan match takes the handle's lifetime too, which it decided itself when it precedes the scan of a scan handle
	///     (see <see cref="YieldScanHandles" />), and a hook it sets on a scan handle's instance is AWT241 (see
	///     <see cref="UnappliedScanHooks" />).
	///     The handle of one of several producers of a service (see <see cref="ThroughHandle" />) has an alias named after
	///     its member, which only its own module declares.
	///     A <c>Factory</c> or <c>Instance</c> registration that names only its service shares the handle only when a
	///     registration names that type as its implementation, since <see cref="AssignMemberIdentities" /> otherwise
	///     identifies both by their members, so precedence decides between them as between any two producers of a service.
	///     A handle that runs a module's <c>Factory</c> or <c>Instance</c> member (its traits are not exact, see
	///     <see cref="ThroughHandle" />) shares nothing once every registration of its module that it serves is replaced
	///     or a dropped overridable default, since the registration then stands alone and running the member would build
	///     what the container replaced.
	/// </summary>
	private static void ShareHandleWithRegistrations(List<RawRegistration> raw, List<(RawRegistration Handle, int Index, string Identity)> handles, Dictionary<string, RawRegistration> yieldedTo, INamedTypeSymbol container, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		HashSet<string> lifetimes = new(StringComparer.Ordinal);
		HashSet<string> productions = new(StringComparer.Ordinal);
		HashSet<string> directives = new(StringComparer.Ordinal);
		HashSet<string> implementations = new(
			raw.Where(registration => registration.Hidden is null)
				.Select(registration => registration.Aliased ?? registration)
				.Where(registration => !NamesOnlyItsService(registration, compilation))
				.Select(registration => registration.ImplementationType),
			StringComparer.Ordinal);
		HashSet<int> dropped = DroppedOverridableDefaults(raw);
		for (int index = 0; index < raw.Count; index++)
		{
			RawRegistration registration = raw[index];
			if (registration.Hidden is not null
			    || registration.ScanImplementation is not null
			    || (NamesOnlyItsService(registration, compilation) && !implementations.Contains(registration.ImplementationType))
			    || handles.FirstOrDefault(entry => entry.Identity == registration.ImplementationType) is not ({ } handle, int handleIndex, _)
			    || (!handle.TraitsExact && !raw.Where((alias, aliasIndex) => alias.Aliased is not null
			                                                               && !dropped.Contains(aliasIndex)
			                                                               && SymbolEqualityComparer.Default.Equals(alias.Origin, handle.Origin)
			                                                               && alias.ImplementationType.EndsWith(AliasKeyMarker + HandledIdentity(handle), StringComparison.Ordinal)).Any()))
			{
				continue;
			}

			INamedTypeSymbol implementation = handle.Hidden!;
			bool scan = IsScanHandle(handle);
			if (!registration.IsScan)
			{
				RawRegistration plain = registration with { HiddenProduction = DescribeProduction(registration.Production, registration.ProductionMember, registration.Origin ?? container) };
				bool registrationFirst = scan
					? yieldedTo.TryGetValue(registration.ImplementationType, out RawRegistration? winner) && ReferenceEquals(winner, registration)
					: (PrecedenceRank(registration), index).CompareTo((PrecedenceRank(handle), handleIndex)) < 0;
				ReportCoalescingConflicts(NewImplInfo(registrationFirst ? plain : handle, container), registrationFirst ? handle : plain, container, lifetimes, productions, directives, diagnostics);
				if (registrationFirst)
				{
					HashSet<string> conflicting = new(ConflictingDirectives(NewImplInfo(plain, container), handle, container).Select(directive => directive.Directive), StringComparer.Ordinal);
					foreach ((string directive, string _, string _) in ConflictingDirectives(NewImplInfo(handle, container), registration, container)
						         .Where(directive => directive.Directive != "Eager" && !conflicting.Contains(directive.Directive)))
					{
						Report($"it sets {directive}, which the handle cannot apply to the instance it builds", scan
							? $"remove {directive}, or grant this assembly InternalsVisibleTo in the module's assembly"
							: $"set {directive} on the module's registration, or remove it");
					}
				}
			}
			else
			{
				diagnostics.AddRange(UnappliedScanHooks(registration, handle, yieldedTo, container, compilation));
			}

			string handled = HandledIdentity(handle);
			string? producer = handled.Contains(MemberKeyMarker) ? handled.Substring(handled.LastIndexOf('.') + 1) : null;
			if ((producer is null ? handles.Select(entry => entry.Handle.Origin) : [handle.Origin,]).OfType<INamedTypeSymbol>()
				    .Select(module => (Module: module, Alias: GeneratedFactoryNamed(module, HiddenAliasName(implementation, implementation, producer), compilation)))
				    .FirstOrDefault(candidate => candidate.Alias is not null) is not ({ } owner, { } alias))
			{
				Report("the module generated no member that returns the implementation from its handle", "remove the registration, or grant this assembly InternalsVisibleTo in the module's assembly");
				continue;
			}

			raw[index] = ThroughAlias(registration with { Lifetime = handle.Lifetime, }, owner, implementation, alias, compilation, producer is null ? null : handled) with
			{
				Eager = registration.Eager,
				Declarer = registration.Declarer ?? registration.Origin ?? container,
			};

			void Report(string reason, string remedy)
				=> diagnostics.Add(new DiagnosticInfo(
					Diagnostics.RegistrationNotSharedWithHandle,
					LocationInfo.From(registration.Location),
					new EquatableArray<string>([
						Display(implementation.ToDisplayString(FullyQualified)),
						Display(handle.Origin!.ToDisplayString(FullyQualified)),
						reason,
						remedy,
					])));
		}
	}

	/// <summary>
	///     AWT241 for each hook that the scan match <paramref name="match" /> sets on the implementation of the scan instance
	///     handle <paramref name="handle" /> (see <see cref="IsScanHandle" />), which serves the match through an alias that
	///     runs no hook: a container of the module's own assembly merges the hooks of the scan matches that share the
	///     instance (AWT199 keeps the first of two in one slot), unless an explicit registration wins over the scans (see
	///     <see cref="YieldScanHandles" />). The instance of a registration's handle, or of a scan handle an explicit
	///     registration wins, is owned by that registration, so a hook of the match it does not name too is AWT166 instead,
	///     as in a container of the module's own assembly (see <see cref="ScanHookOnOwnedInstance" />).
	/// </summary>
	private static IEnumerable<DiagnosticInfo> UnappliedScanHooks(RawRegistration match, RawRegistration handle, Dictionary<string, RawRegistration> yieldedTo, INamedTypeSymbol container, Compilation compilation)
	{
		bool explicitWinner = yieldedTo.TryGetValue(TypeIdentity(handle.Hidden!, compilation), out RawRegistration? winner) && !winner.IsScan;
		if (!IsScanHandle(handle) || explicitWinner)
		{
			RawRegistration owner = explicitWinner ? winner! : handle;
			INamedTypeSymbol? origin = explicitWinner ? DeclaringOrigin(owner, container) : owner.Origin;
			INamedTypeSymbol? scanOrigin = DeclaringOrigin(match, container);
			string subject = Display(handle.Hidden!.ToDisplayString(FullyQualified));
			return new[]
				{
					ScanHookOnOwnedInstance(match, scanOrigin, (owner.OnActivated, origin), handle.Hidden!, release: false, subject, container),
					ScanHookOnOwnedInstance(match, scanOrigin, (owner.OnRelease, origin), handle.Hidden!, release: true, subject, container),
				}
				.OfType<DiagnosticInfo>();
		}

		return new[] { (Directive: "OnActivated", Hook: match.OnActivated), (Directive: "OnRelease", Hook: match.OnRelease), }
			.Where(hook => hook.Hook is not null)
			.Select(hook => new DiagnosticInfo(
				Diagnostics.RegistrationNotSharedWithHandle,
				LocationInfo.From(match.Location),
				new EquatableArray<string>([
					Display(handle.Hidden!.ToDisplayString(FullyQualified)),
					Display(handle.Origin!.ToDisplayString(FullyQualified)),
					$"it sets {hook.Directive}, which the handle cannot apply to the instance it builds",
					$"remove {hook.Directive}, or grant this assembly InternalsVisibleTo in the module's assembly",
				])));
	}

	/// <summary>
	///     The generated member named <paramref name="name" /> that builds a handle, a factory method (possibly async) or
	///     the property over an <c>Instance</c>, and the handle it produces, or <see langword="null" />.
	/// </summary>
	private static (string Name, INamedTypeSymbol? Handle)? HiddenProducer(INamedTypeSymbol owner, string name, Compilation compilation)
		=> InternalsMembers(owner, name).FirstOrDefault(member => member.IsStatic && compilation.IsSymbolAccessibleWithin(member, compilation.Assembly)) switch
		{
			IMethodSymbol method => (method.Name, ProducedType(method.ReturnType, compilation) as INamedTypeSymbol),
			IPropertySymbol property => (property.Name, property.Type as INamedTypeSymbol),
			_ => null,
		};

	/// <summary>
	///     Rewrites a registration read off a module of another assembly whose implementation the module builds through
	///     a generated handle (see <see cref="HiddenHandle" />), or returns <see langword="null" /> to leave it to the
	///     other rules: the module has no such handle, or the module's assembly grants this container's assembly
	///     <c>InternalsVisibleTo</c> and the container can name every service of the implementation, so it builds the
	///     internal services itself and registers no handles, as a container in the module's own assembly does, and
	///     reports a member or constructor it cannot reach like that container. The first registration of the implementation also registers its instance
	///     handle, built by the module's factory, as an unkeyed dependency-only service (<see cref="RawRegistration.Unlisted" />)
	///     with the implementation's own disposal and the hooks and async initializers through their generated wrappers.
	///     Its identity names the implementation and the module that declares the handle, so the registrations of every
	///     module of that assembly coalesce into one instance. Every service of the implementation is an alias that
	///     returns the instance from the handle and owns nothing, with an identity of its service and implementation;
	///     one under an internal service is dependency-only too.
	///     A member that names only its service and has handles of its own (see <c>AssemblyModuleIndex.Producers</c>) is
	///     built through them, unless the module also has the shared handle of the implementation and an imported module of
	///     its assembly names the implementation (see <see cref="ImplementedByImport" />), as a container of that assembly
	///     then keeps one instance.
	/// </summary>
	private static List<RawRegistration>? ThroughHandle(RawRegistration registration, Compilation compilation, HashSet<string> implicitHandles, IEnumerable<ImportedModule> imports)
	{
		INamedTypeSymbol module = registration.Origin!;
		INamedTypeSymbol implementation = registration.Implementation;
		string? producer = registration is { Production: not ProductionKind.Constructor, ProductionMember: { } member, }
		                   && HiddenProducer(module, HiddenFactoryName(implementation, member), compilation) is not null
		                   && !(HiddenProducer(module, HiddenFactoryName(implementation), compilation) is not null && ImplementedByImport(implementation, module, imports, compilation))
			? member
			: null;
		if (registration.ServiceSymbol is not { } service
		    || HiddenProducer(module, HiddenFactoryName(implementation, producer), compilation) is not { Handle: { } handle, } factory)
		{
			return null;
		}

		IEnumerable<ModuleRegistration> registered = ConstructedModuleRegistrations(module, compilation, members: true)
			.Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.Implementation, implementation));
		List<ModuleRegistration> registrations = producer is null ? HandledTogether(registered) : registered.Where(candidate => candidate.Member == producer).ToList();
		string identity = producer is null
			? registration.ImplementationType
			: $"{registration.ImplementationType}{MemberKeyMarker}{TypeIdentity(module, compilation)}.{producer}";
		if (registrations.Count == 0
		    || registrations[0].Production != registration.Production
		    || registrations[0].Member != registration.ProductionMember
		    || Alias(service) is not { ReturnType: INamedTypeSymbol aliasType, } alias
		    || (module.ContainingAssembly.GivesAccessTo(compilation.Assembly)
		        && registrations.All(candidate => compilation.IsSymbolAccessibleWithin(candidate.Service, compilation.Assembly))))
		{
			return null;
		}

		List<RawRegistration> handled = new();
		if (implicitHandles.Add(identity))
		{
			handled.Add(HandleRegistration() with { Weak = false, IsDefault = false, WhenInjectedInto = null, });
		}

		handled.Add(ThroughAlias(
			registration with
			{
				ServiceType = aliasType.ToDisplayString(FullyQualified),
				ServiceSymbol = aliasType,
				Unlisted = !compilation.IsSymbolAccessibleWithin(service, compilation.Assembly),
			},
			module,
			implementation,
			alias,
			compilation,
			identity) with
		{
			Aliased = registration with { OnActivated = Hook(registration.OnActivated, release: false), OnRelease = Hook(registration.OnRelease, release: true), },
		});
		return handled;

		IMethodSymbol? Alias(INamedTypeSymbol aliased)
			=> GeneratedFactoryNamed(module, HiddenAliasName(implementation, aliased, producer), compilation);

		RawRegistration HandleRegistration()
			=> registration with
			{
				ImplementationType = $"{identity}{HiddenKeyMarker}{TypeIdentity(handle.ContainingType?.ContainingType ?? module, compilation)}",
				ServiceType = handle.ToDisplayString(FullyQualified),
				ServiceSymbol = handle,
				Implementation = handle,
				Production = registration.Production == ProductionKind.Instance ? ProductionKind.Instance : ProductionKind.Factory,
				ProductionMember = InInternals(factory.Name),
				HiddenProduction = DescribeProduction(registrations[0].Production, registrations[0].Member, module),
				Key = null,
				Eager = NamedFlag(registrations[0].Attribute, "Eager"),
				OnActivated = Hook(NamedArgument(registrations[0].Attribute, "OnActivated"), release: false),
				OnRelease = Hook(NamedArgument(registrations[0].Attribute, "OnRelease"), release: true),
				SuppressDisposal = NamedFlag(registrations[0].Attribute, "SuppressDisposal"),
				// A factory member may return any subtype, which the handle's variant reveals only at run time.
				Traits = registration.Production == ProductionKind.Constructor ? TraitsOf(implementation, DeclaredAsyncInitializers(module, compilation, null)) : null,
				TraitsExact = registration.Production == ProductionKind.Constructor,
				Unlisted = true,
				Hidden = implementation,
			};

		string? Hook(string? hook, bool release)
			=> hook is not null
			   && HiddenHookName(implementation, release, hook, producer) is { } wrapper
			   && !InternalsMembers(module, wrapper).IsEmpty
				? InInternals(wrapper)
				: null;
	}

	/// <summary>
	///     AWT257 for each collection a built instance takes that a registration served through the alias of a generated
	///     handle would join, had the container been able to read the handle: an alias of a module registration (see
	///     <see cref="ThroughHandle" />) whose service the alias type replaced, of the element type under the collection's
	///     key, or unkeyed of a service an unkeyed collection holds through variance, and that is no <c>WhenInjectedInto</c>
	///     registration, nor an overridable default or an inherited registration another registration of its service
	///     replaces. A container that can read the handle registers the service itself, so the collection would hold it.
	///     Reported at the import of the module, or else at the dependency.
	/// </summary>
	private static void ReportCollectionsMissingHandledRegistrations(
		List<RawRegistration> raw,
		List<InstanceModel> instances,
		List<LocationInfo?> instanceLocations,
		Dictionary<string, int> implToIndex,
		VarianceState variance,
		List<ImportedModule> modules,
		List<DiagnosticInfo> diagnostics)
	{
		// A registration without Fallback, other than a scan match or an open generic closing, replaces a default of its
		// service, and one declared on an extending container replaces an inherited registration of its service as well.
		HashSet<INamedTypeSymbol> imported = new(modules.Select(module => module.Symbol), SymbolEqualityComparer.Default);
		HashSet<INamedTypeSymbol> inherited = new(modules.Where(module => module.Inherited).Select(module => module.Symbol), SymbolEqualityComparer.Default);
		List<RawRegistration> replacing = raw
			.Select(registration => registration.Aliased ?? registration)
			.Where(registration => registration is { Weak: false, IsScan: false, IsSynthesized: false, WhenInjectedInto: null, })
			.ToList();
		HashSet<ServiceKey> replacingDefaults = new(replacing.Select(registration => new ServiceKey(registration.ServiceType, registration.Key)));
		HashSet<ServiceKey> replacingInherited = new(replacing
			.Where(registration => (registration.Declarer ?? registration.Origin) is not { } declarer || !imported.Contains(declarer))
			.Select(registration => new ServiceKey(registration.ServiceType, registration.Key)));
		List<RawRegistration> held = raw
			.Where(registration => registration.Aliased is { ServiceSymbol: not null, WhenInjectedInto: null, } aliased
			                       && registration.ServiceType != aliased.ServiceType
			                       && !(aliased.Weak && replacingDefaults.Contains(new ServiceKey(aliased.ServiceType, aliased.Key)))
			                       && !(inherited.Contains(registration.Origin!) && replacingInherited.Contains(new ServiceKey(aliased.ServiceType, aliased.Key)))
			                       && implToIndex.ContainsKey(registration.ImplementationType))
			.ToList();
		if (held.Count == 0)
		{
			return;
		}

		HashSet<DiagnosticInfo> reported = new();
		for (int i = 0; i < instances.Count; i++)
		{
			InstanceModel instance = instances[i];
			foreach (ParameterModel dependency in instance.ConstructorParameters.AsArray()
				         .Concat(instance.InjectedMembers.AsArray().Select(member => member.Dependency))
				         .Concat(instance.HookParameters()))
			{
				bool keyed = dependency.Kind is DependencyKind.KeyedCollection or DependencyKind.AwaitedKeyedCollection;
				if (!keyed && dependency.Kind is not (DependencyKind.Enumerable or DependencyKind.AsyncEnumerable or DependencyKind.AwaitedEnumerable))
				{
					continue;
				}

				INamedTypeSymbol? element = keyed || dependency.Key is not null
					? null
					: variance.CollectionElements.FirstOrDefault(requested => requested.ServiceType == dependency.ServiceType).Symbol;
				foreach (RawRegistration registration in held)
				{
					RawRegistration aliased = registration.Aliased!;
					bool member = keyed
						? aliased.Key is not null && aliased.ServiceType == dependency.ServiceType
						: aliased.Key == dependency.Key
						  && (aliased.ServiceType == dependency.ServiceType
						      || (element is { TypeKind: TypeKind.Interface, } && aliased.Key is null && VarianceCompatible(aliased.ServiceSymbol!, element, variance.Compilation)));
					if (!member)
					{
						continue;
					}

					INamedTypeSymbol module = registration.Origin!;
					DiagnosticInfo diagnostic = new(
						Diagnostics.CollectionMissesHandledRegistration,
						LocationInfo.From(modules.FirstOrDefault(import => SymbolEqualityComparer.Default.Equals(import.Symbol, module))?.ImportLocation) ?? dependency.Location ?? instanceLocations[i],
						new EquatableArray<string>([
							DisplayInstance(instance.ImplementationType),
							DisplayKeyed(dependency.ServiceType, dependency.Key),
							Display(aliased.Implementation.ToDisplayString(FullyQualified)),
							Display(module.ToDisplayString(FullyQualified)),
							Display(aliased.ServiceType),
						]));
					if (reported.Add(diagnostic))
					{
						diagnostics.Add(diagnostic);
					}
				}
			}
		}
	}

	/// <summary>
	///     Whether a module of the assembly of <paramref name="module" /> among the <paramref name="imports" /> registers
	///     <paramref name="implementation" /> other than through a member that names only its service, or scans it, so a
	///     container of that assembly with the same imports keeps one instance of it (see <see cref="AssignMemberIdentities" />).
	/// </summary>
	private static bool ImplementedByImport(INamedTypeSymbol implementation, INamedTypeSymbol module, IEnumerable<ImportedModule> imports, Compilation compilation)
		=> imports.Any(import => SymbolEqualityComparer.Default.Equals(import.Symbol.ContainingAssembly, module.ContainingAssembly)
		                         && (ConstructedModuleRegistrations(import.Symbol, compilation, members: true)
			                             .Any(registration => SymbolEqualityComparer.Default.Equals(registration.Implementation, implementation) && !NamesOnlyItsService(registration))
		                             || import.Symbol.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeClass
		                                                                               && IsGeneratedScanRegistration(attributeClass)
		                                                                               && NamedArgument(attribute, "Implementation") == MetadataNameOf(implementation))));
}
