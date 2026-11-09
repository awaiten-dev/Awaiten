using System.Globalization;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     The services a module another assembly can name registers under internal types, which a container in another
	///     assembly cannot name, in the module's own build. Each such service gets a generated public handle (see
	///     <see cref="HiddenHandle" />) when something a consumer can reach needs it, so a consumer depends on the handle
	///     instead, and each implementation the module builds through a handle gets an instance handle of its own, which
	///     holds the one instance every service of the implementation is served from. <see cref="Services" /> maps each
	///     internal service to the implementations registered under it, <see cref="Implementations" /> holds each
	///     implementation the module builds through a handle, and <see cref="Used" /> the services whose handle the module
	///     emits, in the order they were first needed.
	/// </summary>
	private sealed class HiddenServices
	{
		private readonly HashSet<INamedTypeSymbol> _used = new(SymbolEqualityComparer.Default);
		private readonly Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>? _buildable;
		private readonly CancellationToken _cancellationToken;

		public HiddenServices(INamedTypeSymbol module, bool friends, Compilation compilation, Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>? buildable, CancellationToken cancellationToken)
		{
			Module = module;
			Friends = friends;
			Compilation = compilation;
			_buildable = buildable;
			_cancellationToken = cancellationToken;
			Assembly = AssemblyModuleIndex.Of(compilation);
		}

		public INamedTypeSymbol Module { get; }

		public Compilation Compilation { get; }

		public CancellationToken CancellationToken => _cancellationToken;

		/// <summary>The modules of the module's assembly and their registrations.</summary>
		public AssemblyModuleIndex Assembly { get; }

		/// <summary>Whether the assembly has more than one module that receives generated members, so another one may share a handle of this module.</summary>
		public bool SeveralModules => Assembly.Modules.Count > 1;

		/// <summary>
		///     The module that declares the handle of <paramref name="service" />, which every module of the assembly uses
		///     (see <see cref="AssemblyModuleIndex.ServiceProviders" />), or <see langword="null" /> when none does.
		/// </summary>
		public INamedTypeSymbol? Provider(INamedTypeSymbol service)
			=> Assembly.ServiceProviders.TryGetValue(service, out INamedTypeSymbol? provider) ? provider : null;

		/// <summary>
		///     The module that declares the instance handle of <paramref name="implementation" />, which every module of the
		///     assembly builds it into (see <see cref="AssemblyModuleIndex.InstanceProviders" />), or <see langword="null" />
		///     when none does.
		/// </summary>
		public INamedTypeSymbol? InstanceProvider(INamedTypeSymbol implementation)
			=> Assembly.InstanceProviders.TryGetValue(implementation, out INamedTypeSymbol? provider) ? provider : null;

		/// <summary>
		///     Whether another module of the assembly builds <paramref name="implementation" /> through a handle, so this
		///     module builds it into the same instance handle, even when it registers it under accessible services only,
		///     and a consuming container keeps one instance, as a container in the assembly does.
		/// </summary>
		public bool HandledElsewhere(INamedTypeSymbol implementation)
			=> Assembly.HandledBy.TryGetValue(implementation, out List<INamedTypeSymbol>? modules)
			   && modules.Any(other => !SymbolEqualityComparer.Default.Equals(other, Module));

		/// <summary>
		///     Whether a member of this module may take <paramref name="service" /> through the handle that another module
		///     declares (see <see cref="Provider" />): the module does not register the service itself, and another module
		///     that registers it can build it, with the services of further modules too (see
		///     <see cref="AssemblyModuleIndex.Buildable" />). That module serves the handle to a consuming container that
		///     imports it.
		/// </summary>
		public bool ProvidedElsewhere(INamedTypeSymbol service)
			=> !Services.ContainsKey(service)
			   && IsHiddenService(service, Module, Compilation)
			   && Provider(service) is not null
			   && Assembly.Modules.Any(other => !SymbolEqualityComparer.Default.Equals(other, Module)
			                                    && (_buildable ?? Assembly.Buildable(_cancellationToken)).TryGetValue(other, out HashSet<INamedTypeSymbol>? services)
			                                    && services.Contains(service));

		/// <summary>
		///     Why the module cannot build <paramref name="service" />: the first rejection of an implementation registered
		///     under it, or <see langword="null" /> when it can or does not register it.
		/// </summary>
		public DiagnosticInfo? Rejection(INamedTypeSymbol service)
			=> NonViable.Contains(service)
			   && Services.TryGetValue(service, out List<HiddenImplementation>? implementations)
			   && implementations.Find(implementation => implementation.Rejections.Count > 0) is { } rejected
				? rejected.Rejections[0]
				: null;

		/// <summary>
		///     The first other module of the assembly that registers <paramref name="service" /> but cannot build it, with
		///     why (see <see cref="Rejection" />), or <see langword="null" />. Its own build reports that only once something
		///     it emits needs the service, so a member of this module that takes the service names it. Judging another module
		///     collects its services anew, so this is only asked once a member failed. A judgement of
		///     <see cref="AssemblyModuleIndex.Buildable" /> judges the other module against the same services, since asking
		///     for them again would start that judgement over.
		/// </summary>
		public (INamedTypeSymbol Module, DiagnosticInfo Rejection)? RejectedElsewhere(INamedTypeSymbol service)
		{
			foreach (INamedTypeSymbol other in Assembly.Modules)
			{
				if (!SymbolEqualityComparer.Default.Equals(other, Module)
				    && Assembly.RegistrationsOf(other).Exists(registration => SymbolEqualityComparer.Default.Equals(registration.Service, service))
				    && CollectHiddenServices(other, Compilation, _cancellationToken, _buildable)?.Rejection(service) is { } rejection)
				{
					return (other, rejection);
				}
			}

			return null;
		}

		/// <summary>
		///     Whether another module of the assembly that another assembly can import registers <paramref name="service" />
		///     too, so that a consumer may take the service from it. A registration of the service this module cannot build
		///     would then vanish from a consuming container, which would serve the other module's implementation where a
		///     container in the module's own assembly picks this one, so the module reports why it cannot build it.
		/// </summary>
		public bool RegisteredElsewhere(INamedTypeSymbol service)
			=> Assembly.Registrations.Any(other => !SymbolEqualityComparer.Default.Equals(other.Key, Module)
			                                       && IsExternallyAccessible(other.Key)
			                                       && other.Value.Any(registration => SymbolEqualityComparer.Default.Equals(registration.Service, service)));

		/// <summary>
		///     Whether another module of the assembly that another assembly can import registers
		///     <paramref name="implementation" /> too. A container in the module's own assembly coalesces both into one
		///     instance with the directives of the first, so a consuming container needs the instance this module builds
		///     through a handle, even when nothing takes its internal services.
		/// </summary>
		public bool ImplementedElsewhere(INamedTypeSymbol implementation)
			=> Assembly.Registrations.Any(other => !SymbolEqualityComparer.Default.Equals(other.Key, Module)
			                                       && IsExternallyAccessible(other.Key)
			                                       && other.Value.Any(registration => SymbolEqualityComparer.Default.Equals(registration.Implementation, implementation)));

		/// <summary>Whether the module's assembly grants <c>InternalsVisibleTo</c>, so a failure may be left to such an assembly.</summary>
		public bool Friends { get; }

		public List<HiddenImplementation> Implementations { get; } = new();

		public Dictionary<INamedTypeSymbol, List<HiddenImplementation>> Services { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The internal services a factory cannot take, because an implementation registered under them cannot be built.</summary>
		public HashSet<INamedTypeSymbol> NonViable { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The non-viable services something the module emits needed, whose cause the module reports.</summary>
		public HashSet<INamedTypeSymbol> NeededNonViable { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>
		///     The rejections of a parameter or injected property that takes a <see cref="NonViable" /> service, whose own
		///     cause the module reports instead, each with the reason a consumer reports for what takes it.
		/// </summary>
		public Dictionary<DiagnosticInfo, string> UnbuildableNeeds { get; } = new();

		/// <summary>The internal services a factory being built for viability takes, or <see langword="null" /> when none is.</summary>
		public HashSet<INamedTypeSymbol>? Referenced { get; set; }

		public List<INamedTypeSymbol> Used { get; } = new();

		/// <summary>The disposal traits of each decorator the module builds into the handle of an internal service, which the handle needs variants for.</summary>
		public List<(INamedTypeSymbol Service, ImplementationTraits Traits)> DecoratorTraits { get; } = new();

		/// <summary>The initializer wrappers of the decorators the module builds into handles.</summary>
		public List<HiddenInitializer> DecoratorInitializers { get; } = new();

		/// <summary>Whether <see cref="Use" /> records the services a mirrored member needs; off while viability is decided.</summary>
		public bool Tracking { get; set; }

		/// <summary>The implementations <see cref="CompleteHiddenServices" /> built through a handle, in the order it built them.</summary>
		public List<HiddenImplementation> Built { get; } = new();

		/// <summary>The implementations of other modules whose instance handle an alias of the module's scans serves (see <see cref="ScanAliases" />).</summary>
		public HashSet<INamedTypeSymbol> ScanAliased { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>
		///     What the modules of the assembly need of each other's handles while <see cref="AssemblyModuleIndex.Demand" />
		///     computes it, or <see langword="null" /> to read it from the index.
		/// </summary>
		public HandleDemand? Demand { get; set; }

		/// <summary>What the other modules of the assembly need of this module's handles, nothing for the only module.</summary>
		public HandleDemand Demanded => !SeveralModules ? HandleDemand.None : Demand ?? Assembly.Demand(_cancellationToken);

		public void Use(INamedTypeSymbol service)
		{
			if (Tracking && _used.Add(service))
			{
				Used.Add(service);
			}
		}

		/// <summary>What <see cref="Use" /> and <see cref="NeededNonViable" /> recorded so far, which <see cref="Restore" /> returns to.</summary>
		public (int Used, INamedTypeSymbol[] NeededNonViable) Snapshot()
			=> (Used.Count, NeededNonViable.ToArray());

		/// <summary>Forgets what a mirrored member recorded since <paramref name="snapshot" />, for a member the module does not emit.</summary>
		public void Restore((int Used, INamedTypeSymbol[] NeededNonViable) snapshot)
		{
			foreach (INamedTypeSymbol service in Used.Skip(snapshot.Used))
			{
				_used.Remove(service);
			}

			Used.RemoveRange(snapshot.Used, Used.Count - snapshot.Used);
			NeededNonViable.IntersectWith(snapshot.NeededNonViable);
		}

		/// <summary>The fully qualified name of the handle of <paramref name="service" />.</summary>
		public string HandleType(INamedTypeSymbol service)
			=> $"{(Provider(service) ?? Module).ToDisplayString(FullyQualified)}.{InternalsClass}.{HiddenHandleName(service)}";

		/// <summary>Whether this module declares the handle of <paramref name="service" /> (see <see cref="Provider" />).</summary>
		public bool Declares(INamedTypeSymbol service)
			=> SymbolEqualityComparer.Default.Equals(Provider(service) ?? Module, Module);

		/// <summary>The fully qualified name of the instance handle of <paramref name="implementation" />, or of its <paramref name="producer" />.</summary>
		public string InstanceHandleType(INamedTypeSymbol implementation, string? producer = null)
			=> $"{(InstanceProvider(implementation) ?? Module).ToDisplayString(FullyQualified)}.{InternalsClass}.{HiddenInstanceName(implementation, producer)}";

		/// <summary>Whether this module declares the instance handle of <paramref name="implementation" /> (see <see cref="InstanceProvider" />).</summary>
		public bool DeclaresInstance(INamedTypeSymbol implementation)
			=> SymbolEqualityComparer.Default.Equals(InstanceProvider(implementation) ?? Module, Module);

		/// <summary>Whether the module builds <paramref name="implementation" /> through a handle instead of a factory of its own.</summary>
		public bool Handles(INamedTypeSymbol implementation)
			=> Implementations.Any(candidate => candidate.Viable && SymbolEqualityComparer.Default.Equals(candidate.Implementation, implementation));
	}

	/// <summary>
	///     The <c>[Module]</c>s of a compilation's assembly and what every module's build asks about them, read once per
	///     compilation and shared by the builds of all its modules, since the assembly walk would otherwise repeat for
	///     each. It holds symbols only while the compilation lives and never reaches a model, so incremental caching is
	///     unaffected.
	/// </summary>
	private sealed class AssemblyModuleIndex
	{
		private static readonly ConditionalWeakTable<Compilation, AssemblyModuleIndex> Cache = new();

		private readonly Compilation _compilation;
		private readonly Lazy<HashSet<INamedTypeSymbol>> _imported;
		private readonly Lazy<List<INamedTypeSymbol>> _classes;
		private readonly Lazy<bool> _scansOrOpenRegistrations;
		private Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>? _buildable;
		private HandleDemand? _demand;

		private AssemblyModuleIndex(Compilation compilation)
		{
			_compilation = compilation;
			_imported = new Lazy<HashSet<INamedTypeSymbol>>(() => new HashSet<INamedTypeSymbol>(
				TypesWith(compilation.Assembly.GlobalNamespace, "ContainerAttribute")
					.SelectMany(container => CollectImportedModules(container, new List<DiagnosticInfo>()))
					.Select(import => import.Symbol),
				SymbolEqualityComparer.Default));
			_classes = new Lazy<List<INamedTypeSymbol>>(() => ConcreteClasses(compilation.Assembly.GlobalNamespace).ToList());
			_scansOrOpenRegistrations = new Lazy<bool>(() => Registrations.Keys.Any(module => module.GetAttributes().Any(attribute =>
				attribute.AttributeClass is { Name: "ScanAttribute", } or { Name: "SingletonAttribute" or "TransientAttribute" or "ScopedAttribute", IsGenericType: false, })));
			foreach (INamedTypeSymbol module in TypesWith(compilation.Assembly.GlobalNamespace, "ModuleAttribute"))
			{
				Registrations[module] = ConstructedModuleRegistrations(module, compilation, members: true).ToList();
			}

			foreach (IGrouping<INamedTypeSymbol, (INamedTypeSymbol Module, ModuleRegistration Registration)> produced in Registrations
				         .SelectMany(entry => entry.Value.Select(registration => (Module: entry.Key, Registration: registration)))
				         .GroupBy<(INamedTypeSymbol Module, ModuleRegistration Registration), INamedTypeSymbol>(entry => entry.Registration.Implementation, SymbolEqualityComparer.Default))
			{
				if (produced.All(entry => NamesOnlyItsService(entry.Registration))
				    && produced.Select(entry => $"{entry.Module.ToDisplayString(FullyQualified)}.{entry.Registration.Member}").Distinct(StringComparer.Ordinal).Skip(1).Any())
				{
					Producers.Add(produced.Key);
				}
			}

			Modules = Registrations.Keys
				.Where(module => IsExternallyAccessible(module) && ReceivesGeneratedMembers(module))
				.OrderBy(module => module.ToDisplayString(FullyQualified), StringComparer.Ordinal)
				.ToList();
			foreach (INamedTypeSymbol module in Modules)
			{
				foreach (ModuleRegistration registration in Registrations[module])
				{
					if (!ServiceProviders.ContainsKey(registration.Service))
					{
						ServiceProviders[registration.Service] = module;
					}

					if (!InstanceProviders.ContainsKey(registration.Implementation) && !Producers.Contains(registration.Implementation))
					{
						InstanceProviders[registration.Implementation] = module;
					}
				}

				foreach (IGrouping<INamedTypeSymbol, ModuleRegistration> group in Registrations[module].GroupBy<ModuleRegistration, INamedTypeSymbol>(registration => registration.Implementation, SymbolEqualityComparer.Default))
				{
					if (!Producers.Contains(group.Key) && HandledShape(module, group.Key, HandledTogether(group), compilation))
					{
						if (!HandledBy.TryGetValue(group.Key, out List<INamedTypeSymbol>? handling))
						{
							HandledBy[group.Key] = handling = new List<INamedTypeSymbol>();
						}

						handling.Add(module);
					}
				}
			}

			// An implementation that several modules construct for a consumer that cannot is one instance in the modules'
			// own assembly, so each of them builds it into the shared instance handle instead of a factory of its own.
			foreach (IGrouping<INamedTypeSymbol, INamedTypeSymbol> shared in Modules
				         .SelectMany(module => Registrations[module]
					         .GroupBy<ModuleRegistration, INamedTypeSymbol>(registration => registration.Implementation, SymbolEqualityComparer.Default)
					         .Where(group => SelfCompiledTogether(module, group.Key, HandledTogether(group), compilation))
					         .Select(group => (Implementation: group.Key, Module: module)))
				         .GroupBy<(INamedTypeSymbol Implementation, INamedTypeSymbol Module), INamedTypeSymbol, INamedTypeSymbol>(entry => entry.Implementation, entry => entry.Module, SymbolEqualityComparer.Default)
				         .Where(group => group.Count() > 1))
			{
				if (!HandledBy.TryGetValue(shared.Key, out List<INamedTypeSymbol>? handling))
				{
					HandledBy[shared.Key] = handling = new List<INamedTypeSymbol>();
				}

				handling.AddRange(shared.Where(module => !handling.Contains(module, SymbolEqualityComparer.Default)).ToList());
			}
		}

		/// <summary>Every <c>[Module]</c> of the assembly with its closed lifetime registrations, members included.</summary>
		public Dictionary<INamedTypeSymbol, List<ModuleRegistration>> Registrations { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The modules another assembly can name that receive generated members, so they can provide handles, by fully qualified name.</summary>
		public List<INamedTypeSymbol> Modules { get; }

		/// <summary>
		///     The module that declares the handle of each internal service, which every module of the assembly uses: the
		///     first of <see cref="Modules" /> that registers the service. One handle type per service lets a module take a
		///     service another module registers.
		/// </summary>
		public Dictionary<INamedTypeSymbol, INamedTypeSymbol> ServiceProviders { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>
		///     The module that declares the instance handle of each implementation: the first of <see cref="Modules" /> that
		///     registers it. One instance handle per implementation lets a consuming container share one instance when
		///     several modules register the implementation, under whichever services.
		/// </summary>
		public Dictionary<INamedTypeSymbol, INamedTypeSymbol> InstanceProviders { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The modules that build each implementation through a handle on their own account (see <see cref="HandledShape" />).</summary>
		public Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> HandledBy { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>
		///     The services that several <c>Factory</c> or <c>Instance</c> members of the assembly's modules produce, where
		///     every registration of the type names only its service (see <see cref="NamesOnlyItsService(ModuleRegistration)" />).
		///     A container in the modules' own assembly keeps each member as an implementation of its own, so each member a
		///     module builds through a handle gets handles of its own, named after it (see <see cref="HiddenImplementation.Producer" />),
		///     which no other module shares.
		/// </summary>
		public HashSet<INamedTypeSymbol> Producers { get; } = new(SymbolEqualityComparer.Default);

		public static AssemblyModuleIndex Of(Compilation compilation)
			=> Cache.GetValue(compilation, static created => new AssemblyModuleIndex(created));

		/// <summary>
		///     Whether a module of the assembly may provide <paramref name="service" /> under <paramref name="key" />: a
		///     closed registration of it, an import from the host, or, through a <c>[Scan]</c> or an open generic
		///     registration, a class of the assembly that converts to it.
		/// </summary>
		public bool MayProvide(ITypeSymbol service, string? key)
		{
			string serviceType = service.ToDisplayString(FullyQualified);
			return Registrations.Any(entry =>
			{
				ImmutableArray<AttributeData> attributes = entry.Key.GetAttributes();
				return HasAwaitenAttribute(attributes, "ImportServicesAttribute")
				       || CollectExternalServiceTypes(entry.Key, new List<ImportedModule>(), _compilation, new List<DiagnosticInfo>()).Routes(serviceType)
				       || entry.Value.Any(registration => registration.Service.ToDisplayString(FullyQualified) == serviceType && NamedKeyArgument(registration.Attribute) == key);
			}) || (_scansOrOpenRegistrations.Value && _classes.Value.Any(type => Provides(type, service)));
		}

		/// <summary>Whether the class <paramref name="type" />, or a closing of it, converts to <paramref name="service" />.</summary>
		private bool Provides(INamedTypeSymbol type, ITypeSymbol service)
			=> _compilation.HasImplicitConversion(type, service)
			   || (type.IsGenericType
			       && service is INamedTypeSymbol { IsGenericType: true, } generic
			       && (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, generic.OriginalDefinition)
			           || type.AllInterfaces.Any(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, generic.OriginalDefinition))));

		/// <summary>The concrete classes declared in <paramref name="container" /> and the namespaces and types it contains.</summary>
		private static IEnumerable<INamedTypeSymbol> ConcreteClasses(INamespaceOrTypeSymbol container)
		{
			foreach (ISymbol member in container.GetMembers())
			{
				if (member is INamespaceOrTypeSymbol nested)
				{
					if (nested is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, } type)
					{
						yield return type;
					}

					foreach (INamedTypeSymbol found in ConcreteClasses(nested))
					{
						yield return found;
					}
				}
			}
		}

		/// <summary>Whether a <c>[Container]</c> of the assembly imports <paramref name="module" />, so it reports what its coalescing sees itself.</summary>
		public bool ImportedByContainer(INamedTypeSymbol module)
			=> _imported.Value.Contains(module);

		/// <summary>The registrations of <paramref name="module" />, read once when it belongs to the assembly.</summary>
		public List<ModuleRegistration> RegistrationsOf(INamedTypeSymbol module)
			=> Registrations.TryGetValue(module, out List<ModuleRegistration>? registrations)
				? registrations
				: ConstructedModuleRegistrations(module, _compilation, members: true).ToList();

		/// <summary>
		///     The internal services each module of <see cref="Modules" /> can build, with the services the other modules
		///     can build. Modules may take each other's services, so every module starts out building every internal service
		///     it registers, and each one is judged again against the others until no module loses a service. A cycle
		///     between modules thus stays buildable unless one of its services cannot be built for a reason of its own,
		///     and the verdict depends neither on the order the modules are judged in nor on whether they form a cycle.
		///     Like the module's own build, a judgement rejects an implementation one of the module's scans matches too.
		/// </summary>
		/// <remarks>A cancelled judgement caches nothing, so a later build of the same compilation judges again with its own token.</remarks>
		public Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> Buildable(CancellationToken cancellationToken)
		{
			if (Volatile.Read(ref _buildable) is { } known)
			{
				return known;
			}

			Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> buildable = new(SymbolEqualityComparer.Default);
			foreach (INamedTypeSymbol module in Modules)
			{
				HashSet<INamedTypeSymbol> services = new(Registrations[module].Select(registration => registration.Service).Where(service => IsHiddenService(service, module, _compilation)), SymbolEqualityComparer.Default);
				if (services.Count > 0)
				{
					buildable[module] = services;
				}
			}

			List<INamedTypeSymbol> judged = Modules.Where(buildable.ContainsKey).ToList();
			bool changed = true;
			while (changed)
			{
				changed = false;
				foreach (INamedTypeSymbol module in judged)
				{
					cancellationToken.ThrowIfCancellationRequested();
					HiddenServices? judgement = Judge(module, buildable, cancellationToken);
					int before = buildable[module].Count;
					buildable[module].RemoveWhere(service => judgement is null || !judgement.Services.ContainsKey(service) || judgement.NonViable.Contains(service));
					changed |= buildable[module].Count != before;
				}
			}

			return Interlocked.CompareExchange(ref _buildable, buildable, null) ?? buildable;
		}

		/// <summary>
		///     What the modules of <see cref="Modules" /> need of each other's handles. A container in another assembly may
		///     import any of them, so a module emits a handle, an instance handle or a factory only when the members some
		///     module emits take it. Each module's generated members are expanded as its own build expands them, given what
		///     the others need so far, and expanded again only when the demand for a service or implementation it registers
		///     grows. The demand only grows, so the expansion ends, and the result depends neither on the order of the
		///     modules nor on cycles between them.
		/// </summary>
		/// <remarks>A cancelled computation caches nothing, so a later build of the same compilation computes it again with its own token.</remarks>
		public HandleDemand Demand(CancellationToken cancellationToken)
		{
			if (Volatile.Read(ref _demand) is { } known)
			{
				return known;
			}

			HandleDemand demand = new();
			Dictionary<INamedTypeSymbol, HandleDemand> contributions = new(SymbolEqualityComparer.Default);
			List<INamedTypeSymbol> pending = Modules;
			while (pending.Count > 0)
			{
				foreach (INamedTypeSymbol module in pending)
				{
					cancellationToken.ThrowIfCancellationRequested();
					HandleDemand contribution = new();
					ExpandForDemand(module, _compilation, demand, contribution, cancellationToken);
					contributions[module] = contribution;
				}

				HandleDemand next = new();
				next.Add(demand);
				foreach (INamedTypeSymbol module in Modules.Where(contributions.ContainsKey))
				{
					next.Add(contributions[module]);
				}

				HashSet<INamedTypeSymbol> changed = next.ChangedSince(demand);
				demand = next;
				pending = Modules.Where(module => Registrations[module].Any(registration => changed.Contains(registration.Service) || changed.Contains(registration.Implementation))).ToList();
			}

			return Interlocked.CompareExchange(ref _demand, demand, null) ?? demand;
		}

		private HiddenServices? Judge(INamedTypeSymbol module, Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> buildable, CancellationToken cancellationToken)
		{
			HiddenServices? judgement = CollectHiddenServices(module, _compilation, cancellationToken, buildable);
			if (judgement is not null && HasAwaitenAttribute(module.GetAttributes(), "ScanAttribute"))
			{
				List<ModuleFactory> scanned = CollectModuleScanFactories(module, _compilation, new List<DiagnosticInfo>(), cancellationToken, judgement.Friends ? null : judgement)
					.Select(expansion => expansion.Factory)
					.ToList();
				RejectScannedHiddenImplementations(judgement, scanned, new List<DiagnosticInfo>());
			}

			return judgement;
		}
	}

	/// <summary>
	///     What the generated members of the modules of an assembly take of each other's handles (see
	///     <see cref="AssemblyModuleIndex.Demand" />). It holds symbols only while the compilation lives and never reaches a
	///     model.
	/// </summary>
	private sealed class HandleDemand
	{
		public static readonly HandleDemand None = new();

		/// <summary>The internal services whose handle a generated member takes, which every module that registers one builds.</summary>
		public HashSet<INamedTypeSymbol> Services { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The implementations a scan alias serves from their instance handle, which every module that handles one builds.</summary>
		public HashSet<INamedTypeSymbol> Scanned { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The implementations some module builds into their instance handle, with how each module produces them.</summary>
		public Dictionary<INamedTypeSymbol, HashSet<ProductionKind>> Built { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>The disposal traits of each decorator or composite a module builds into the handle of an internal service.</summary>
		public List<(INamedTypeSymbol Service, ImplementationTraits Traits)> DecoratorTraits { get; } = new();

		/// <summary>The modules that emit a handle, factory, alias or initializer.</summary>
		public HashSet<INamedTypeSymbol> Emitting { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>Adds everything <paramref name="other" /> needs.</summary>
		public void Add(HandleDemand other)
		{
			Services.UnionWith(other.Services);
			Scanned.UnionWith(other.Scanned);
			Emitting.UnionWith(other.Emitting);
			DecoratorTraits.AddRange(other.DecoratorTraits.Where(decorator => !DecoratorTraits.Contains(decorator)));
			foreach (KeyValuePair<INamedTypeSymbol, HashSet<ProductionKind>> built in other.Built)
			{
				if (!Built.TryGetValue(built.Key, out HashSet<ProductionKind>? productions))
				{
					Built[built.Key] = productions = new HashSet<ProductionKind>();
				}

				productions.UnionWith(built.Value);
			}
		}

		/// <summary>
		///     The services and implementations whose demand grew since <paramref name="earlier" />, which only the modules
		///     that register them expand differently for.
		/// </summary>
		public HashSet<INamedTypeSymbol> ChangedSince(HandleDemand earlier)
		{
			HashSet<INamedTypeSymbol> changed = new(Services.Where(service => !earlier.Services.Contains(service)), SymbolEqualityComparer.Default);
			changed.UnionWith(Scanned.Where(implementation => !earlier.Scanned.Contains(implementation)));
			changed.UnionWith(DecoratorTraits.Where(decorator => !earlier.DecoratorTraits.Contains(decorator)).Select(decorator => decorator.Service));
			changed.UnionWith(Built.Where(built => !earlier.Built.TryGetValue(built.Key, out HashSet<ProductionKind>? productions) || productions.Count != built.Value.Count).Select(built => built.Key));
			return changed;
		}
	}

	/// <summary>
	///     Expands the generated members of <paramref name="module" /> as its own build does (see <c>BuildModuleModel</c>),
	///     given what the modules of its assembly need so far (<paramref name="demand" />), and adds what its members take to
	///     <paramref name="next" />. The diagnostics are the module build's to report.
	/// </summary>
	private static void ExpandForDemand(INamedTypeSymbol module, Compilation compilation, HandleDemand demand, HandleDemand next, CancellationToken cancellationToken)
	{
		List<DiagnosticInfo> diagnostics = new();
		List<SkippedRegistration> skipped = new();
		if (CollectHiddenServices(module, compilation, cancellationToken) is not { } hidden)
		{
			return;
		}

		hidden.Demand = demand;
		List<MemberWrapper> wrappers = CollectModuleMemberWrappers(module, compilation, diagnostics, skipped, hidden);
		List<RegistrationFactory> contributions = CollectModuleContributionFactories(module, compilation, hidden, diagnostics);
		ModuleExpansion expansion = ExpandModule(module, compilation, hidden, wrappers, skipped, contributions, diagnostics, cancellationToken);
		next.Services.UnionWith(hidden.Used);
		next.Scanned.UnionWith(hidden.ScanAliased);
		next.DecoratorTraits.AddRange(hidden.DecoratorTraits);
		foreach (HiddenImplementation built in hidden.Built)
		{
			if (!next.Built.TryGetValue(built.Implementation, out HashSet<ProductionKind>? productions))
			{
				next.Built[built.Implementation] = productions = new HashSet<ProductionKind>();
			}

			productions.Add(built.Production);
		}

		if (expansion.Registrations.Count + expansion.Handles.Count + expansion.Aliases.Count + expansion.Initializers.Count > 0)
		{
			next.Emitting.Add(module);
		}
	}

	/// <summary>
	///     One implementation a module builds through a handle: its registrations in declaration order. Its factory wraps
	///     the instance in the instance handle of the implementation, and every service it is registered under is served
	///     from that handle through an alias.
	/// </summary>
	private sealed class HiddenImplementation(INamedTypeSymbol implementation, List<ModuleRegistration> registrations)
	{
		public INamedTypeSymbol Implementation { get; } = implementation;

		public List<ModuleRegistration> Registrations { get; } = registrations;

		/// <summary>How the instance is produced: through the constructor, or the module's <c>Factory</c> or <c>Instance</c> <see cref="Member" />.</summary>
		public ProductionKind Production => Registrations[0].Production;

		public string? Member => Registrations[0].Member;

		/// <summary>
		///     The member when it is one of several producers of a service that names no implementation (see
		///     <see cref="AssemblyModuleIndex.Producers" />), which then has a handle, an instance handle and aliases of its
		///     own, named after it, or <see langword="null" />.
		/// </summary>
		public string? Producer { get; init; }

		/// <summary>Whether the implementation is registered under accessible services only.</summary>
		public bool Shared { get; init; }

		public bool Viable { get; set; } = true;

		public List<DiagnosticInfo> Rejections { get; set; } = new();

		/// <summary>The internal services its factory took when its viability was last judged, so it is judged again only when one of them is rejected.</summary>
		public HashSet<INamedTypeSymbol> Needs { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>Every internal service the implementation is registered under.</summary>
		public IEnumerable<INamedTypeSymbol> HiddenServices(INamedTypeSymbol module, Compilation compilation)
			=> Registrations.Select(registration => registration.Service).Where(service => IsHiddenService(service, module, compilation))
				.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);
	}

	/// <summary>
	///     A parameter mirrored onto a generated member through the handle of an internal service, or the reason it
	///     cannot be.
	/// </summary>
	private sealed record HiddenMirror(FactoryParameter Parameter, PendingDiagnostic? Error);

	/// <summary>A diagnostic about a mirrored member, completed with its subject, the member and its location once known.</summary>
	private sealed record PendingDiagnostic(DiagnosticDescriptor Descriptor, string[] Arguments)
	{
		public DiagnosticInfo WithLocation(Location? location, SelfCompiledSubject subject, string member)
			=> new(Descriptor, LocationInfo.From(location), new EquatableArray<string>([subject.Subject, member, ..Arguments,]));
	}

	/// <summary>How a member takes an internal service, each of which the generated member can pass on from its handle.</summary>
	private enum HiddenShape
	{
		Direct,
		Func,
		Lazy,
		Owned,
		FuncOwned,
		FuncWithArguments,
		FuncWithArgumentsOwned,
		Collection,
	}

	/// <summary>
	///     The registrations of one implementation that one handle builds: all of them when they are produced the same
	///     way (the constructor, or one <c>Factory</c> or <c>Instance</c> member), else only the constructed ones, since
	///     a container keeps a constructor and a member of one implementation apart (AWT111). The module's build and a
	///     consuming container ask this same question.
	/// </summary>
	private static List<ModuleRegistration> HandledTogether(IEnumerable<ModuleRegistration> registrations)
	{
		List<ModuleRegistration> all = registrations.ToList();
		return all.All(registration => registration.Production == all[0].Production && registration.Member == all[0].Member)
			? all
			: all.Where(registration => registration.Production == ProductionKind.Constructor).ToList();
	}

	/// <summary>
	///     Whether <paramref name="registration" /> is a <c>Factory</c> or <c>Instance</c> registration that names only its
	///     service, as a consuming container judges its own (see <see cref="NamesOnlyItsService(RawRegistration, Compilation)" />).
	/// </summary>
	private static bool NamesOnlyItsService(ModuleRegistration registration)
		=> registration is { Production: not ProductionKind.Constructor, Member: not null, }
		   && SymbolEqualityComparer.Default.Equals(registration.Implementation, registration.Service);

	/// <summary>
	///     Whether anything the module declares could be built through a handle, in its own module or another: a
	///     <c>[Scan]</c>, whose matches may take internal services, or a registration, decorator or composite that names an
	///     internal service, or a type a container in another assembly cannot construct or that converts to an internal
	///     type, or whose <c>Factory</c> member or hook takes a parameter of a type that is not public, which its wrapper
	///     takes through a handle. A module that declares none of these never reads the modules of its assembly.
	/// </summary>
	private static bool MayBuildThroughHandles(INamedTypeSymbol module, Compilation compilation)
	{
		ImmutableArray<AttributeData> attributes = module.GetAttributes();
		return HasAwaitenAttribute(attributes, "ScanAttribute")
		       || ConstructedModuleRegistrations(module, compilation, members: true).Any(registration => !IsExternallyAccessible(registration.Service) || Concealed(registration.Implementation) || TakesConcealedParameter(registration))
		       || attributes.Any(attribute => attribute.AttributeClass is { Name: "DecorateAttribute" or "CompositeAttribute", } attributeClass
		                                      && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace
		                                      && ContributedPair(attribute, attributeClass) is ({ } type, { } service)
		                                      && (!IsExternallyAccessible(service) || Concealed(type)));

		static bool Concealed(INamedTypeSymbol type)
			=> !ConstructibleEverywhere(type) || ServableTypes(type).Any(servable => !IsExternallyAccessible(servable));

		bool TakesConcealedParameter(ModuleRegistration registration)
			=> (registration is { Production: ProductionKind.Factory, Member: { } member, } ? FindFactoryCandidates(module, member, registration.Implementation, compilation) : [])
				.Concat(new[] { "OnActivated", "OnRelease", }
					.Select(slot => NamedArgument(registration.Attribute, slot))
					.OfType<string>()
					.SelectMany(hook => AccessibleMembers(module, hook, compilation).OfType<IMethodSymbol>()))
				.Any(method => method.Parameters.Any(parameter => !IsExternallyAccessible(parameter.Type)));
	}

	/// <summary>
	///     Whether the module can build <paramref name="implementation" /> from <paramref name="registrations" /> (see
	///     <see cref="HandledTogether" />): a closed type, and for a constructed one a concrete class with a constructor
	///     the module can reach.
	/// </summary>
	private static bool Buildable(INamedTypeSymbol module, INamedTypeSymbol implementation, List<ModuleRegistration> registrations, Compilation compilation)
		=> registrations.Count > 0
		   && !implementation.IsUnboundGenericType
		   && (registrations[0].Production != ProductionKind.Constructor
		       || (implementation is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, }
		           && implementation.InstanceConstructors.Any(constructor => compilation.IsSymbolAccessibleWithin(constructor, module))));

	/// <summary>
	///     Whether the module builds <paramref name="implementation" /> through a handle on its own account: it registers
	///     it under an internal service (see <see cref="IsHiddenService" />), constructs it and registers it under
	///     several accessible services that no accessible type covers, which one instance handle shares, or produces it
	///     through a member that is not public and that no public wrapper can serve: one returning an implementation
	///     another assembly cannot name under several services, or a factory taking a parameter of an internal type.
	/// </summary>
	private static bool HandledShape(INamedTypeSymbol module, INamedTypeSymbol implementation, List<ModuleRegistration> registrations, Compilation compilation)
	{
		if (!Buildable(module, implementation, registrations, compilation))
		{
			return false;
		}

		if (registrations.Any(registration => IsHiddenService(registration.Service, module, compilation)))
		{
			return true;
		}

		List<INamedTypeSymbol> services = registrations.Select(registration => registration.Service).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToList();
		if (registrations[0].Member is { } member)
		{
			List<ISymbol> producers = ProductionMemberCandidates(module, registrations[0].Production, member, implementation, compilation).ToList();
			return !IsHiddenInModule(implementation, module, compilation)
			       && producers.Count == 1
			       && producers[0].DeclaredAccessibility != Accessibility.Public
			       && ((services.Count >= 2 && !IsExternallyAccessible(implementation))
			           || (producers[0] is IMethodSymbol method && method.Parameters.Any(parameter => !IsExternallyAccessible(parameter.Type) && TypeHiddenInModule(parameter.Type, module, compilation) is null)));
		}

		return registrations[0].Production == ProductionKind.Constructor
		       && services.Count >= 2
		       && !IsHiddenInModule(implementation, module, compilation)
		       && registrations.All(registration => SelfCompilesRegistration(module, implementation, registration.Service, compilation))
		       && FactoryReturnType(implementation, services, new ModuleScanContext(module, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal))) is null;
	}

	/// <summary>
	///     Whether the module constructs <paramref name="implementation" /> for a consuming container that cannot: every
	///     one of its <paramref name="registrations" /> is self-compiled (see <see cref="SelfCompilesRegistration" />), and
	///     the module can build it through its constructor.
	/// </summary>
	private static bool SelfCompiledTogether(INamedTypeSymbol module, INamedTypeSymbol implementation, List<ModuleRegistration> registrations, Compilation compilation)
		=> Buildable(module, implementation, registrations, compilation)
		   && registrations[0].Production == ProductionKind.Constructor
		   && !IsHiddenInModule(implementation, module, compilation)
		   && registrations.All(registration => SelfCompilesRegistration(module, implementation, registration.Service, compilation));

	/// <summary>
	///     Whether <paramref name="service" /> is a service type no other assembly can name but the module's own
	///     assembly can, so a container in another assembly reaches it only through a handle.
	/// </summary>
	private static bool IsHiddenService(INamedTypeSymbol service, INamedTypeSymbol module, Compilation compilation)
		=> !service.IsUnboundGenericType
		   && !IsExternallyAccessible(service)
		   && compilation.IsSymbolAccessibleWithin(service, module.ContainingAssembly);

	/// <summary>
	///     The implementations of a module another assembly can name that it builds through a handle, and which of
	///     them can be built: those it builds through a handle on its own account (see <see cref="HandledShape" />), and
	///     in an assembly with several modules those another module builds through a handle, so all of them share one
	///     instance handle. The module drops such an implementation again when one of its scans matches it too. One private
	///     to the module is built through its handle for a container in another assembly, while a container in the
	///     module's own assembly, or in one it grants <c>InternalsVisibleTo</c>, calls its internal factory (see
	///     <see cref="CollectModuleRegistrationFactories" />). An implementation whose generated
	///     factory would fail is not viable, and nor is any internal service it is registered under, so a member that
	///     needs such a service falls back to what it did before (AWT195). Returns <see langword="null" /> for a module
	///     no other assembly can name.
	/// </summary>
	private static HiddenServices? CollectHiddenServices(INamedTypeSymbol module, Compilation compilation, CancellationToken cancellationToken, Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>? buildable = null)
	{
		if (!IsExternallyAccessible(module) || !MayBuildThroughHandles(module, compilation))
		{
			return null;
		}

		HiddenServices hidden = new(module, GrantsInternalsVisibleTo(compilation.Assembly), compilation, buildable, cancellationToken);
		ModuleScanContext context = new(module, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal), hidden);
		foreach ((INamedTypeSymbol implementation, List<ModuleRegistration> registrations, string? producer) in hidden.Assembly.RegistrationsOf(module)
			         .GroupBy<ModuleRegistration, INamedTypeSymbol>(registration => registration.Implementation, SymbolEqualityComparer.Default)
			         .SelectMany(group => hidden.Assembly.Producers.Contains(group.Key)
				         ? group.GroupBy(registration => registration.Member, StringComparer.Ordinal).Select(produced => (group.Key, produced.ToList(), produced.Key))
				         : [(group.Key, HandledTogether(group), null),]))
		{
			if (!Buildable(module, implementation, registrations, compilation)
			    || (!HandledShape(module, implementation, registrations, compilation) && !(hidden.SeveralModules && hidden.HandledElsewhere(implementation))))
			{
				continue;
			}

			HiddenImplementation candidate = new(implementation, registrations) { Shared = !registrations.Any(registration => IsHiddenService(registration.Service, module, compilation)), Producer = producer, };
			hidden.Implementations.Add(candidate);
			foreach (INamedTypeSymbol service in registrations.Select(registration => registration.Service).Where(service => IsHiddenService(service, module, compilation)))
			{
				if (!hidden.Services.TryGetValue(service, out List<HiddenImplementation>? implementations))
				{
					hidden.Services[service] = implementations = new List<HiddenImplementation>();
				}

				if (!implementations.Contains(candidate))
				{
					implementations.Add(candidate);
				}
			}
		}

		List<HiddenImplementation> pending = hidden.Implementations.ToList();
		while (pending.Count > 0)
		{
			HashSet<INamedTypeSymbol> rejected = new(SymbolEqualityComparer.Default);
			foreach (HiddenImplementation candidate in pending.Where(candidate => candidate.Viable))
			{
				List<DiagnosticInfo> rejections = new();
				candidate.Needs.Clear();
				hidden.Referenced = candidate.Needs;
				if (BuildHiddenFactory(candidate, context with { Diagnostics = rejections, FailedHooks = new HashSet<string>(StringComparer.Ordinal), }, new List<HiddenInitializer>(), null) is null
				    || rejections.Count > 0)
				{
					candidate.Viable = false;
					candidate.Rejections = rejections;
					hidden.NonViable.UnionWith(candidate.HiddenServices(module, compilation));
					rejected.UnionWith(candidate.HiddenServices(module, compilation));
				}
			}

			hidden.Referenced = null;
			pending = hidden.Implementations.Where(candidate => candidate.Viable && candidate.Needs.Overlaps(rejected)).ToList();
		}

		hidden.NeededNonViable.Clear();
		hidden.Tracking = true;
		return hidden;
	}

	/// <summary>
	///     Whether the build of <paramref name="module" /> may report <see cref="Diagnostics.NullableProduct">AWT245</see>
	///     for a member it wraps for a handle: a static member of it may produce <see langword="null" />, other assemblies can
	///     name it, and no container of its assembly imports it, which would report the member itself.
	/// </summary>
	internal static bool MayReportNullableProduct(INamedTypeSymbol module, Compilation compilation)
		=> module.GetMembers().Any(member => member.IsStatic && member switch
		   {
			   IMethodSymbol { MethodKind: MethodKind.Ordinary, } or IFieldSymbol or IPropertySymbol => MayProduceNull(ProductOf(member, compilation)!, member),
			   _ => false,
		   })
		   && IsExternallyAccessible(module)
		   && !AssemblyModuleIndex.Of(compilation).ImportedByContainer(module);

	/// <summary>
	///     Completes the handles of a module once its scans and registrations were mirrored, which recorded the internal
	///     services they need: builds every public implementation, which a consumer may register itself and then shares,
	///     every implementation registered under accessible services too, every eager or
	///     async-initialized singleton, and in an assembly with several modules every implementation another module
	///     registers too, serves through a scan alias, or takes an internal service of (see
	///     <see cref="AssemblyModuleIndex.Demand" />), then follows the services each built implementation needs. Declares
	///     the handles and instance handles that it or another module takes, with a variant for every decorator of any
	///     module. Returns the factories, handles, aliases and initializer wrappers to emit. Reports why a needed service
	///     cannot be built, unless an assembly granted <c>InternalsVisibleTo</c> builds what needs it instead, and in place of a
	///     rejection that only takes another service the module cannot build (see <see cref="HiddenServices.UnbuildableNeeds" />)
	///     why that service cannot be built.
	/// </summary>
	private static (List<RegistrationFactory> Factories, List<HiddenHandle> Handles, List<HiddenAlias> Aliases, List<HiddenInitializer> Initializers) CompleteHiddenServices(
		HiddenServices hidden,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics)
	{
		INamedTypeSymbol module = hidden.Module;
		foreach (HiddenImplementation candidate in hidden.Implementations)
		{
			ReportHandledConflicts(candidate.Implementation, candidate.Producer, module, compilation, diagnostics);
		}

		List<AsyncInitializerConvention> conventions = DeclaredAsyncInitializers(module, compilation, null);
		HandleDemand demand = hidden.Demanded;
		List<HiddenImplementation> pending = hidden.Implementations
			.Where(candidate => candidate.Viable
			                    && (IsExternallyAccessible(candidate.Implementation)
			                        || candidate.Registrations.Any(registration => !IsHiddenService(registration.Service, module, compilation))
			                        || (candidate.Registrations[0].Attribute.AttributeClass?.Name == "SingletonAttribute" && (candidate.Registrations.Any(registration => NamedFlag(registration.Attribute, "Eager")) || IsHiddenAsyncInitialized(candidate.Implementation, candidate.Registrations, module, compilation, conventions)))
			                        || demand.Scanned.Contains(candidate.Implementation)
			                        || candidate.HiddenServices(module, compilation).Any(demand.Services.Contains)
			                        || (hidden.SeveralModules && hidden.ImplementedElsewhere(candidate.Implementation))))
			.ToList();

		ModuleScanContext context = new(module, compilation, diagnostics, new HashSet<string>(StringComparer.Ordinal), hidden);
		List<RegistrationFactory> factories = new();
		List<HiddenAlias> aliases = new();
		List<HiddenInitializer> initializers = new(hidden.DecoratorInitializers);
		List<HiddenImplementation> built = hidden.Built;
		List<DiagnosticInfo>? warnings = AssemblyModuleIndex.Of(compilation).ImportedByContainer(module) ? null : diagnostics;
		int used = 0;
		while (true)
		{
			foreach (HiddenImplementation candidate in pending.Where(candidate => !built.Contains(candidate)).Distinct().ToList())
			{
				built.Add(candidate);
				RegistrationFactory? factory = BuildHiddenFactory(candidate, context, initializers, warnings);
				if (factory is not null)
				{
					factories.Add(factory);
				}

				string[] arguments = factory?.Parameters.AsArray().Where(parameter => parameter.Attributes == ArgAttribute).Select(parameter => parameter.Type).ToArray() ?? [];
				aliases.AddRange(HiddenAliases(candidate, hidden, compilation, arguments));
			}

			pending.Clear();
			for (; used < hidden.Used.Count; used++)
			{
				if (hidden.Services.TryGetValue(hidden.Used[used], out List<HiddenImplementation>? serving))
				{
					pending.AddRange(serving.Where(candidate => candidate.Viable && !built.Contains(candidate)));
				}
			}

			if (pending.Count == 0)
			{
				break;
			}
		}

		List<HiddenImplementation> reported = hidden.Implementations
			.Where(candidate => !candidate.Viable && !hidden.Friends
			                    && candidate.HiddenServices(module, compilation).Any(service => hidden.NeededNonViable.Contains(service) || hidden.RegisteredElsewhere(service)))
			.ToList();
		for (int index = 0; index < reported.Count; index++)
		{
			HiddenImplementation candidate = reported[index];
			diagnostics.AddRange(candidate.Rejections.Where(rejection => !hidden.UnbuildableNeeds.ContainsKey(rejection) && !diagnostics.Contains(rejection)));
			if (candidate.Rejections.Any(hidden.UnbuildableNeeds.ContainsKey))
			{
				reported.AddRange(hidden.Implementations.Where(other => !other.Viable && !reported.Contains(other) && other.HiddenServices(module, compilation).Any(candidate.Needs.Contains)));
			}
		}

		IEnumerable<INamedTypeSymbol> declared = hidden.Used.Concat(Declared(demand.Services, hidden.Provider));
		List<HiddenHandle> handles = declared
			.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
			.Where(hidden.Declares)
			.Select(service =>
			{
				ImplementationTraits[] traits = hidden.DecoratorTraits
					.Concat(demand.DecoratorTraits)
					.Where(decorator => SymbolEqualityComparer.Default.Equals(decorator.Service, service))
					.Select(decorator => decorator.Traits)
					.ToArray();
				return new HiddenHandle(
					HiddenHandleName(service),
					service.ToDisplayString(FullyQualified),
					traits.Any(trait => trait is { Disposable: true, AsyncDisposable: false, }),
					traits.Any(trait => trait is { Disposable: false, AsyncDisposable: true, }),
					traits.Any(trait => trait is { Disposable: true, AsyncDisposable: true, }));
			})
			.ToList();

		IEnumerable<INamedTypeSymbol> instances = built.Where(candidate => candidate.Producer is null).Select(candidate => candidate.Implementation)
			.Concat(Declared(demand.Built.Keys.Concat(demand.Scanned), hidden.InstanceProvider));
		foreach (INamedTypeSymbol implementation in instances.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Where(hidden.DeclaresInstance))
		{
			(bool constructed, bool wrap) = InstanceHandleNeeds(implementation, hidden);
			ImplementationTraits traits = TraitsOf(implementation, []);
			handles.Add(new HiddenHandle(
				HiddenInstanceName(implementation),
				InstanceValueType(implementation, compilation),
				wrap || (constructed && traits is { Disposable: true, AsyncDisposable: false, }),
				wrap || (constructed && traits is { Disposable: false, AsyncDisposable: true, }),
				wrap || (constructed && traits is { Disposable: true, AsyncDisposable: true, }),
				wrap));
		}

		foreach (HiddenImplementation producer in built.Where(candidate => candidate.Producer is not null))
		{
			bool wrap = producer.Production == ProductionKind.Factory;
			handles.Add(new HiddenHandle(HiddenInstanceName(producer.Implementation, producer.Producer), InstanceValueType(producer.Implementation, compilation), wrap, wrap, wrap, wrap));
		}

		return (factories, handles, aliases, initializers);

		IEnumerable<INamedTypeSymbol> Declared(IEnumerable<INamedTypeSymbol> needed, Func<INamedTypeSymbol, INamedTypeSymbol?> provider)
			=> needed
				.Where(type => provider(type) is { } declaring && SymbolEqualityComparer.Default.Equals(declaring, module))
				.OrderBy(type => type.ToDisplayString(FullyQualified), StringComparer.Ordinal);
	}

	/// <summary>
	///     Whether a convention of the module initializes <paramref name="implementation" /> (see
	///     <see cref="HiddenInitializedType" />), or one of its <paramref name="registrations" /> names an asynchronous
	///     <c>OnActivated</c> hook, so a container initializes a singleton of it though nothing depends on it.
	/// </summary>
	private static bool IsHiddenAsyncInitialized(
		INamedTypeSymbol implementation,
		IEnumerable<ModuleRegistration> registrations,
		INamedTypeSymbol module,
		Compilation compilation,
		List<AsyncInitializerConvention> conventions)
		=> registrations.Any(registration => HiddenInitializedType(module, registration.Production, registration.Member, implementation, compilation) is var initialized
		                                     && MatchingAsyncInitializers(conventions, initialized, convention => IsOfConventionType(initialized, convention.Type)).Count > 0)
		   || registrations.Any(registration => NamedArgument(registration.Attribute, "OnActivated") is { } hook
		                                        && AccessibleMembers(module, hook, compilation).OfType<IMethodSymbol>().Any(method => IsAsyncHookReturn(method.ReturnType, compilation)));

	/// <summary>
	///     The first singleton a module another assembly can name registers under an internal service that a container
	///     builds or initializes though nothing depends on it, because it is eager or async-initialized, and whether it
	///     is eager, or <see langword="null" />. The module needs its handle even without any other generated member.
	/// </summary>
	private static (ModuleRegistration Registration, bool Eager)? StartedHiddenSingleton(INamedTypeSymbol module, Compilation compilation)
	{
		if (!IsExternallyAccessible(module))
		{
			return null;
		}

		List<AsyncInitializerConvention>? conventions = null;
		foreach (ModuleRegistration registration in ConstructedModuleRegistrations(module, compilation, members: true))
		{
			if (registration.Attribute.AttributeClass?.Name != "SingletonAttribute" || !IsHiddenService(registration.Service, module, compilation))
			{
				continue;
			}

			if (NamedFlag(registration.Attribute, "Eager"))
			{
				return (registration, true);
			}

			conventions ??= DeclaredAsyncInitializers(module, compilation, null);
			if (IsHiddenAsyncInitialized(registration.Implementation, [registration,], module, compilation, conventions))
			{
				return (registration, false);
			}
		}

		return null;
	}

	/// <summary>
	///     Reports what a container in the module's own assembly reports when the registrations of an implementation the
	///     module builds through a handle contradict each other: a different lifetime (AWT107), a different production
	///     (AWT111), or a later registration that sets a directive the first one does not (AWT166). A container in another
	///     assembly serves every registration from one instance handle with the lifetime and directives of the first one,
	///     so it would silently differ instead. An overridable default is left out, since only the importing container
	///     knows whether it yields. A container of the assembly that imports the module reports the same conflicts at
	///     the same places, so the module leaves them to it. Of one of several producers of a service, only the registrations
	///     of the <paramref name="producer" /> are one instance.
	/// </summary>
	private static void ReportHandledConflicts(INamedTypeSymbol implementation, string? producer, INamedTypeSymbol module, Compilation compilation, List<DiagnosticInfo> diagnostics)
	{
		List<ModuleRegistration> registrations = AssemblyModuleIndex.Of(compilation).RegistrationsOf(module)
			.Where(registration => SymbolEqualityComparer.Default.Equals(registration.Implementation, implementation) && (producer is null || registration.Member == producer) && NamedFallback(registration.Attribute) == 0)
			.ToList();
		if (registrations.Count < 2 || AssemblyModuleIndex.Of(compilation).ImportedByContainer(module))
		{
			return;
		}

		ModuleRegistration first = registrations[0];
		string subject = Display(implementation.ToDisplayString(FullyQualified));
		bool lifetimeReported = false;
		bool productionReported = false;
		HashSet<string> directivesReported = new(StringComparer.Ordinal);
		foreach (ModuleRegistration registration in registrations.Skip(1))
		{
			if (!lifetimeReported && LifetimeOf(registration) != LifetimeOf(first))
			{
				lifetimeReported = true;
				Report(Diagnostics.ConflictingLifetime, registration, LifetimeOf(first), LifetimeOf(registration));
			}

			if (!productionReported && (registration.Production != first.Production || registration.Member != first.Member))
			{
				productionReported = true;
				Report(Diagnostics.ConflictingProduction, registration, DescribeProduction(first.Production, first.Member, module), DescribeProduction(registration.Production, registration.Member, module));
			}

			foreach (string hook in new[] { "OnActivated", "OnRelease", })
			{
				if (NamedArgument(registration.Attribute, hook) is { } named && named != NamedArgument(first.Attribute, hook) && directivesReported.Add(hook))
				{
					Report(Diagnostics.ConflictingLifecycleDirectives, registration, hook, DescribeHook(NamedArgument(first.Attribute, hook)), DescribeHook(named));
				}
			}

			foreach (string flag in new[] { "Eager", "SuppressDisposal", })
			{
				if (NamedFlag(registration.Attribute, flag) && !NamedFlag(first.Attribute, flag) && directivesReported.Add(flag))
				{
					Report(Diagnostics.ConflictingLifecycleDirectives, registration, flag, "false", "true");
				}
			}
		}

		void Report(DiagnosticDescriptor descriptor, ModuleRegistration registration, params string[] arguments)
			=> diagnostics.Add(new DiagnosticInfo(descriptor, LocationInfo.From(registration.Location), new EquatableArray<string>([subject, ..arguments,])));

		string DescribeHook(string? hook)
			=> hook is null ? "unset" : $"'{Display(QualifiedMember(module, hook))}'";

		static string LifetimeOf(ModuleRegistration registration)
			=> registration.Attribute.AttributeClass!.Name.Substring(0, registration.Attribute.AttributeClass.Name.Length - "Attribute".Length);
	}

	/// <summary>
	///     Whether the instance handle of <paramref name="implementation" />, which every module of the assembly that builds
	///     the implementation builds it into, needs the variants of a constructed implementation, and whether it needs to
	///     wrap the result of a <c>Factory</c> member, whose disposal shows only at run time.
	/// </summary>
	private static (bool Constructed, bool Wrap) InstanceHandleNeeds(INamedTypeSymbol implementation, HiddenServices hidden)
	{
		HashSet<ProductionKind> all = new(hidden.Built.Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.Implementation, implementation)).Select(candidate => candidate.Production));
		if (hidden.Demanded.Built.TryGetValue(implementation, out HashSet<ProductionKind>? elsewhere))
		{
			all.UnionWith(elsewhere);
		}

		return (all.Contains(ProductionKind.Constructor), all.Contains(ProductionKind.Factory));
	}

	/// <summary>
	///     The factory of an implementation the module builds through a handle: it constructs the implementation like the
	///     factory of a self-compiled registration, taking each internal service it needs through its handle, and wraps it
	///     in its instance handle, in the variant that implements exactly the disposal interfaces of the implementation.
	///     Each hook its registrations name gets a wrapper that takes the handle, and each convention of the module that
	///     initializes it gets an initializer wrapper (added to <paramref name="initializers" />). Returns
	///     <see langword="null" /> after reporting into the context why it cannot be built. A warning that does not keep it
	///     from being built goes to <paramref name="warnings" />, which a trial build leaves out, and so does a module a
	///     container of its assembly imports, since that container reports the same warning at the same place.
	/// </summary>
	private static RegistrationFactory? BuildHiddenFactory(HiddenImplementation candidate, ModuleScanContext context, List<HiddenInitializer> initializers, List<DiagnosticInfo>? warnings)
	{
		INamedTypeSymbol implementation = candidate.Implementation;
		HiddenServices hidden = context.Hidden!;
		string typeName = implementation.ToDisplayString(FullyQualified);
		SelfCompiledSubject subject = HiddenSubject(candidate, context.Module, context.Compilation);
		Location? location = candidate.Registrations[0].Location;
		EquatableArray<FactoryParameter> parameters;
		MirroredMembers members = default;
		string? awaited = null;
		ISymbol? producer = null;
		bool instance = candidate.Production == ProductionKind.Instance;
		bool built = candidate.Production == ProductionKind.Constructor
			? TryMirrorFactoryParameters(implementation, context, location, subject, out parameters, mirrorProperties: true)
			  && TryMirrorInjectedProperties(implementation, context, location, subject, ref parameters, out members)
			: TryMirrorProducingMember(candidate, context, location, subject, out parameters, out awaited, out producer);
		if (!built)
		{
			return null;
		}

		string handle = hidden.InstanceHandleType(implementation, candidate.Producer);
		List<RegistrationHook> hooks = new();
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (ModuleRegistration registration in instance ? [] : candidate.Registrations)
		{
			foreach (bool release in new[] { false, true, })
			{
				if (NamedArgument(registration.Attribute, release ? "OnRelease" : "OnActivated") is not { } hookName
				    || !seen.Add((release ? "OnRelease\0" : "OnActivated\0") + hookName))
				{
					continue;
				}

				ModuleHookRequest request = new(implementation, hookName, registration.Location, release, HiddenHookName(implementation, release, hookName, candidate.Producer), subject);
				if (ResolveRegistrationHook(request, context) is not { } resolved)
				{
					return null;
				}

				hooks.Add(new RegistrationHook(resolved.Wrapper, release));
			}
		}

		if (!instance && !TryAddHiddenInitializers(implementation, handle, context, location, subject, initializers, HiddenInitializedType(context.Module, candidate.Production, candidate.Member, implementation, context.Compilation), candidate.Producer))
		{
			return null;
		}

		if (candidate.Production != ProductionKind.Constructor)
		{
			return new RegistrationFactory(
				HiddenFactoryName(implementation, candidate.Producer),
				handle,
				typeName,
				parameters,
				new EquatableArray<RegistrationHook>(hooks.ToArray()),
				default,
				instance ? $"new {handle}" : $"{handle}.Wrap",
				candidate.Member,
				awaited,
				instance,
				NullableProduct: IsNullableProduct(
					ProductOf(producer!, context.Compilation)!, producer!, candidate.Production, candidate.Member!, candidate.Registrations[0].Service.ToDisplayString(FullyQualified), LocationInfo.From(location), warnings));
		}

		return new RegistrationFactory(
			HiddenFactoryName(implementation),
			handle,
			typeName,
			parameters,
			new EquatableArray<RegistrationHook>(hooks.ToArray()),
			members.Required,
			handle + HandleVariant(TraitsOf(implementation, [])),
			OptionalMembers: members.Optional,
			DeferredMembers: members.Deferred,
			InitOnlyMembers: members.InitOnly,
			OptionalDeferredMembers: members.OptionalDeferred);
	}

	/// <summary>How a diagnostic about an implementation the module builds through a handle names it.</summary>
	private static SelfCompiledSubject HiddenSubject(HiddenImplementation candidate, INamedTypeSymbol module, Compilation compilation)
	{
		string typeName = candidate.Implementation.ToDisplayString(FullyQualified);
		return candidate.HiddenServices(module, compilation).FirstOrDefault() is { } service
			? new SelfCompiledSubject(typeName, $"'{Display(typeName)}' is registered under the internal service '{Display(service.ToDisplayString(FullyQualified))}', which a consuming container reaches through a generated handle", "register it in the module")
			: new SelfCompiledSubject(typeName, $"'{Display(typeName)}' is registered under services whose one instance a consuming container shares through a generated handle", "register it in the module");
	}

	/// <summary>
	///     Rejects every implementation the module builds through a handle that one of its scans matches too, since the
	///     scan's factory would build a second instance of what the handle shares (AWT197). One registered under
	///     accessible services only leaves the handles to the self-compiled registrations, which report it themselves.
	///     One under an internal service is no longer viable, so nothing takes its handle; it is reported unless the
	///     module's assembly grants <c>InternalsVisibleTo</c>, whose assemblies construct it once for both, or unless a
	///     self-compiled registration of it reports it already.
	/// </summary>
	private static void RejectScannedHiddenImplementations(HiddenServices hidden, List<ModuleFactory> scanFactories, List<DiagnosticInfo> diagnostics)
	{
		INamedTypeSymbol module = hidden.Module;
		Compilation compilation = hidden.Compilation;
		hidden.Implementations.RemoveAll(candidate => candidate.Shared && scanFactories.Any(factory => factory.ImplementationType == candidate.Implementation.ToDisplayString(FullyQualified)));
		foreach (HiddenImplementation candidate in hidden.Implementations.Where(candidate => candidate.Viable))
		{
			if (scanFactories.FirstOrDefault(factory => factory.ImplementationType == candidate.Implementation.ToDisplayString(FullyQualified)) is not { } scanned)
			{
				continue;
			}

			INamedTypeSymbol service = candidate.HiddenServices(module, compilation).First();
			DiagnosticInfo rejection = new(
				Diagnostics.ModuleScanMultipleExposures,
				LocationInfo.From(candidate.Registrations[0].Location),
				new EquatableArray<string>([
					HiddenSubject(candidate, module, compilation).Subject,
					$"{Display(scanned.ServiceType)}, {Display(service.ToDisplayString(FullyQualified))}",
					"remove the registration or exclude the type from the scan",
				]));
			candidate.Viable = false;
			candidate.Rejections = [rejection,];
			hidden.NonViable.UnionWith(candidate.HiddenServices(module, compilation));
			if (!hidden.Friends && !candidate.Registrations.Any(registration => SelfCompilesRegistration(module, candidate.Implementation, registration.Service, compilation)))
			{
				diagnostics.Add(rejection);
			}
		}
	}

	/// <summary>The nested handle variant that implements exactly the disposal interfaces of <paramref name="traits" />.</summary>
	private static string HandleVariant(ImplementationTraits traits)
		=> (traits.Disposable, traits.AsyncDisposable) switch
		{
			(true, true) => ".BothDisposable",
			(true, false) => ".Disposable",
			(false, true) => ".AsyncDisposable",
			_ => string.Empty,
		};

	/// <summary>
	///     Adds an initializer wrapper to <paramref name="initializers" /> for each convention of the module that
	///     initializes <paramref name="implementation" />, judged by <paramref name="initialized" /> (the implementation
	///     itself unless a <c>Factory</c> member produces it, see <see cref="HiddenInitializedType" />), which takes its
	///     <paramref name="handle" /> and runs the convention on the instance it holds. Returns <see langword="false" />
	///     after reporting a convention parameter the wrapper cannot mirror.
	/// </summary>
	private static bool TryAddHiddenInitializers(
		INamedTypeSymbol implementation,
		string handle,
		ModuleScanContext context,
		Location? location,
		SelfCompiledSubject subject,
		List<HiddenInitializer> initializers,
		ITypeSymbol? initialized = null,
		string? producer = null)
	{
		initialized ??= implementation;
		foreach (AsyncInitializerConvention convention in MatchingAsyncInitializers(DeclaredAsyncInitializers(context.Module, context.Compilation, null), initialized, convention => IsOfConventionType(initialized, convention.Type)))
		{
			List<FactoryParameter> mirrored = new();
			foreach (IParameterSymbol parameter in convention.Method.Parameters)
			{
				if (MirrorHiddenService(parameter.Type, parameter.Name, parameter.GetAttributes(), context, out _) is { } mirror)
				{
					if (mirror.Error is { } error)
					{
						context.Diagnostics.Add(error.WithLocation(location, subject, $"the parameter '{parameter.Name}' of its async initializer {convention.Display}"));
						return false;
					}

					mirrored.Add(mirror.Parameter);
				}
				else if (!FitsFactorySignature(parameter.Type, context))
				{
					context.Diagnostics.Add(new DiagnosticInfo(
						Diagnostics.InaccessibleAsyncInitializer,
						LocationInfo.From(location),
						new EquatableArray<string>([
							subject.Subject,
							Display(convention.Type.ToDisplayString(FullyQualified)),
							convention.Method.Name,
							$"its parameter of type '{Display(parameter.Type.ToDisplayString(FullyQualified))}' is not accessible outside the module's assembly, so a consumer in another assembly cannot resolve it",
							"widen the parameter type's accessibility or register it in the module",
						])));
					return false;
				}
				else
				{
					mirrored.Add(new FactoryParameter(parameter.Type.ToDisplayString(FullyQualifiedWithNullability), parameter.Name));
				}
			}

			initializers.Add(new HiddenInitializer(
				HiddenInitializerName(implementation, convention, producer),
				handle,
				convention.Type.ToDisplayString(FullyQualified),
				convention.Method.Name,
				convention.Method.ReturnType.ToDisplayString(FullyQualified),
				new EquatableArray<FactoryParameter>(mirrored.ToArray())));
		}

		return true;
	}

	/// <summary>
	///     The factory of a decorator or composite the module declares over an internal service it registers, or that
	///     another module of its assembly provides (see <see cref="HiddenServices.ProvidedElsewhere" />), or
	///     <see langword="null" /> when it cannot be built, so a consuming container reports it as before (AWT230). It
	///     mirrors the constructor like any decorator or composite factory, so the inner parameter of a decorator takes
	///     the service's handle and the collection of a composite the handles, and wraps the decorator or composite in a
	///     handle of the same service, in the variant that matches its disposal. A consumer then keeps each link of a
	///     chain as an instance of its own, with its own disposal, and fronts the handle with the composite.
	/// </summary>
	private static RegistrationFactory? HiddenContributionFactory(INamedTypeSymbol decorator, INamedTypeSymbol service, string name, bool composite, ModuleScanContext context)
	{
		HiddenServices hidden = context.Hidden!;
		if (hidden.NonViable.Contains(service)
		    || decorator is not { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsUnboundGenericType: false, })
		{
			return null;
		}

		string typeName = decorator.ToDisplayString(FullyQualified);
		SelfCompiledSubject subject = new(typeName, $"'{Display(typeName)}' {(composite ? "composes" : "decorates")} the internal service '{Display(service.ToDisplayString(FullyQualified))}', which a consuming container reaches through a generated handle", "register it in the module");
		string handle = hidden.HandleType(service);
		List<HiddenInitializer> initializers = new();
		if (!TryMirrorFactoryParameters(decorator, context, null, subject, out EquatableArray<FactoryParameter> parameters, mirrorProperties: true, decorated: composite ? null : service, emptyCollectionsOf: IsHiddenInModule(decorator, context.Module, context.Compilation) ? null : service)
		    || !TryMirrorInjectedProperties(decorator, context, null, subject, ref parameters, out MirroredMembers members)
		    || !TryAddHiddenInitializers(decorator, handle, context, null, subject, initializers))
		{
			return null;
		}

		ImplementationTraits traits = TraitsOf(decorator, []);
		hidden.Use(service);
		hidden.DecoratorTraits.Add((service, traits));
		hidden.DecoratorInitializers.AddRange(initializers);
		return new RegistrationFactory(name, handle, typeName, parameters, default, members.Required, handle + HandleVariant(traits), OptionalMembers: members.Optional, DeferredMembers: members.Deferred, InitOnlyMembers: members.InitOnly, OptionalDeferredMembers: members.OptionalDeferred);
	}

	/// <summary>
	///     What a container in the module's own assembly reports for a decorator or composite of an internal service whose
	///     factory <paramref name="name" /> the module cannot build, recorded so a container in another assembly reports
	///     the same instead of AWT230: the collection element of a composite that is a base type of the composed service
	///     (AWT133, see <see cref="CompositeCollectionMarkerName" />), or an internal constructor parameter type, directly
	///     or behind a relationship such as <c>Lazy&lt;T&gt;</c>, that no module of the assembly may provide under the
	///     parameter's key (AWT101, see <see cref="UnregisteredDependencyMarkerName" />). Any other cause records nothing.
	/// </summary>
	private static SkippedRegistration? ContributionFault(INamedTypeSymbol type, INamedTypeSymbol service, string name, bool composite, ModuleScanContext context)
	{
		Compilation compilation = context.Compilation;
		IMethodSymbol? constructor = SelectConstructor(
			type, context.Module, compilation, Array.Empty<string>(), new ExternalSurface(false, new ExternalServiceTypes()), _ => true);
		if (constructor is null)
		{
			return null;
		}

		IParameterSymbol? skippedParameter = composite ? null : InnerParameter(constructor, service, compilation);
		if (composite)
		{
			ITypeSymbol? related = null;
			foreach (IParameterSymbol parameter in constructor.Parameters)
			{
				if (!TryGetCompositeCollectionElement(parameter.Type, out ITypeSymbol? element))
				{
					continue;
				}

				if (SymbolEqualityComparer.Default.Equals(element, service))
				{
					skippedParameter = parameter;
					related = null;
					break;
				}

				related ??= compilation.HasImplicitConversion(service, element!) ? element : null;
			}

			if (related is not null)
			{
				return new SkippedRegistration(CompositeCollectionMarkerName(name), Display(related.ToDisplayString(FullyQualified)), Contribution: true);
			}
		}

		AssemblyModuleIndex assembly = AssemblyModuleIndex.Of(compilation);
		foreach (IParameterSymbol parameter in constructor.Parameters)
		{
			if (SymbolEqualityComparer.Default.Equals(parameter, skippedParameter)
			    || parameter.HasExplicitDefaultValue
			    || parameter.Type is not INamedTypeSymbol dependency
			    || TryGetCompositeCollectionElement(dependency, out _)
			    || FitsFactorySignature(dependency, context)
			    || TypeHiddenInModule(dependency, context.Module, compilation) is not null)
			{
				continue;
			}

			ParameterModel model = ClassifyParameter(parameter, false, new ExternalServiceTypes());
			if (model.Kind is DependencyKind.Direct or DependencyKind.Func or DependencyKind.Lazy or DependencyKind.Owned or DependencyKind.Task or DependencyKind.FuncTask or DependencyKind.LazyTask
			    && TypeNamed(dependency, model.ServiceType) is { } provided
			    && !assembly.MayProvide(provided, model.Key))
			{
				return new SkippedRegistration(UnregisteredDependencyMarkerName(name), DisplayKeyed(model.ServiceType, model.Key), Contribution: true);
			}
		}

		return null;
	}

	/// <summary><paramref name="type" /> or the type among its type arguments, at any depth, whose fully qualified name is <paramref name="display" />, or <see langword="null" />.</summary>
	private static ITypeSymbol? TypeNamed(ITypeSymbol type, string display)
		=> type.ToDisplayString(FullyQualified) == display
			? type
			: (type as INamedTypeSymbol)?.TypeArguments.Select(argument => TypeNamed(argument, display)).FirstOrDefault(found => found is not null);

	/// <summary>The name of the constant that records the collection element of a composite a consumer reports as AWT133 (see <see cref="ContributionFault" />).</summary>
	private static string CompositeCollectionMarkerName(string factoryName) => "Collection" + factoryName;

	/// <summary>The name of the constant that records the dependency of a decorator or composite a consumer reports as AWT101 (see <see cref="ContributionFault" />).</summary>
	private static string UnregisteredDependencyMarkerName(string factoryName) => "Unregistered" + factoryName;

	/// <summary>
	///     The diagnostic the imported module of another assembly recorded for its decorator or composite
	///     <paramref name="type" /> of <paramref name="service" /> whose factory <paramref name="name" /> it could not build
	///     (see <see cref="ContributionFault" />), or <see langword="null" /> when it recorded none.
	/// </summary>
	private static DiagnosticInfo? RecordedContributionFault(INamedTypeSymbol module, string name, INamedTypeSymbol type, INamedTypeSymbol service, Location? location)
	{
		string typeName = Display(type.ToDisplayString(FullyQualified));
		string serviceName = Display(service.ToDisplayString(FullyQualified));
		if (RecordedConstant(module, CompositeCollectionMarkerName(name)) is { } element)
		{
			return new DiagnosticInfo(Diagnostics.CompositeCollectionNotOfComposedService, LocationInfo.From(location), new EquatableArray<string>([typeName, element, serviceName,]));
		}

		return RecordedConstant(module, UnregisteredDependencyMarkerName(name)) is { } dependency
			? new DiagnosticInfo(Diagnostics.MissingDependency, LocationInfo.From(location), new EquatableArray<string>([serviceName, typeName, dependency,]))
			: null;

		static string? RecordedConstant(INamedTypeSymbol module, string constant)
			=> InternalsMembers(module, constant).OfType<IFieldSymbol>().Select(field => field.ConstantValue as string).FirstOrDefault(value => value is not null);
	}

	/// <summary>
	///     Mirrors the <c>Factory</c> or <c>Instance</c> member that produces <paramref name="candidate" /> onto the
	///     wrapper that puts its result into the handle: a static field or property converting to the implementation, or
	///     one static, non-generic method, whose parameters the wrapper mirrors like a constructor's, and which may return
	///     <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> (<paramref name="awaited" />), as
	///     <paramref name="producer" />. Returns <see langword="false" /> for a member the wrapper cannot call, after
	///     reporting why a parameter cannot be mirrored.
	/// </summary>
	private static bool TryMirrorProducingMember(
		HiddenImplementation candidate,
		ModuleScanContext context,
		Location? location,
		SelfCompiledSubject subject,
		out EquatableArray<FactoryParameter> parameters,
		out string? awaited,
		out ISymbol? producer)
	{
		parameters = default;
		awaited = null;
		producer = null;
		INamedTypeSymbol module = context.Module;
		Compilation compilation = context.Compilation;
		string member = candidate.Member!;
		if (candidate.Production == ProductionKind.Instance)
		{
			producer = AccessibleMembers(module, member, compilation)
				.FirstOrDefault(symbol => symbol is IFieldSymbol { IsStatic: true, } or IPropertySymbol { IsStatic: true, GetMethod: not null, }
				                          && compilation.HasImplicitConversion(ProductOf(symbol, compilation), candidate.Implementation));
			return producer is not null;
		}

		if (ProducingMethod(module, member, candidate.Implementation, compilation) is not { } factory || factory.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
		{
			return false;
		}

		if (IsAsyncFactoryReturn(factory.ReturnType, compilation, out _))
		{
			awaited = ((INamedTypeSymbol)factory.ReturnType).ConstructedFrom.ContainingNamespace.ToDisplayString(FullyQualified) + "." + factory.ReturnType.Name;
		}

		producer = factory;
		List<FactoryParameter> mirrored = new();
		foreach (IParameterSymbol parameter in factory.Parameters)
		{
			if (!TryMirrorParameter(parameter, context, location, subject, $"the parameter '{parameter.Name}' of its factory '{member}'", out FactoryParameter mirror))
			{
				return false;
			}

			mirrored.Add(mirror);
		}

		parameters = new EquatableArray<FactoryParameter>(mirrored.ToArray());
		return true;
	}

	/// <summary>The one static, non-generic <c>Factory</c> member of <paramref name="module" /> that produces <paramref name="produced" />, or <see langword="null" />.</summary>
	private static IMethodSymbol? ProducingMethod(INamedTypeSymbol module, string member, INamedTypeSymbol produced, Compilation compilation)
	{
		List<IMethodSymbol> methods = FindFactoryCandidates(module, member, produced, compilation)
			.Where(method => compilation.IsSymbolAccessibleWithin(method, module))
			.ToList();
		return methods.Count == 1 && methods[0] is { IsStatic: true, IsGenericMethod: false, } method ? method : null;
	}

	/// <summary>
	///     The type whose conventions initialize an implementation produced by <paramref name="production" />: what a
	///     <c>Factory</c> member returns, awaited for a <c>Task</c> or <c>ValueTask</c>, as for any factory, unless that is
	///     private to the module, which no container can name, so the registered type counts instead, as the member's
	///     internal wrapper returns it (see <see cref="CollectModuleMemberWrappers" />). Otherwise the registered type.
	///     Every container decides alike: the module through it, and a container in the module's assembly or a friend
	///     through the wrapper's return type.
	/// </summary>
	private static ITypeSymbol HiddenInitializedType(INamedTypeSymbol module, ProductionKind production, string? member, INamedTypeSymbol registered, Compilation compilation)
		=> production == ProductionKind.Factory && member is not null && ProducingMethod(module, member, registered, compilation) is { } factory
			? OwnedMemberType(ProducedType(factory.ReturnType, compilation), registered, module, compilation)
			: registered;

	/// <summary>
	///     The type a container sees a module member produce: <paramref name="declared" />, or <paramref name="registered" />
	///     when <paramref name="declared" /> is private to the module.
	/// </summary>
	private static ITypeSymbol OwnedMemberType(ITypeSymbol declared, INamedTypeSymbol registered, INamedTypeSymbol module, Compilation compilation)
		=> compilation.IsSymbolAccessibleWithin(declared, module.ContainingAssembly) ? declared : registered;

	/// <summary>
	///     The aliases of an implementation the module builds through a handle: one per service it is registered under,
	///     returning its instance from its instance handle, as the accessible service or wrapped in the handle of the
	///     internal service. A public implementation gets one under its own type too, through which a container in another
	///     assembly serves its own registration of the implementation from the same instance. For an implementation built
	///     from the runtime <paramref name="arguments" /> of its <c>[Arg]</c> parameters, each alias takes them and a factory
	///     of the instance handle, since the container reaches such a service only through a factory, and an async
	///     counterpart awaits an async factory of the handle, which a container takes when the handle is async-tainted.
	/// </summary>
	private static IEnumerable<HiddenAlias> HiddenAliases(HiddenImplementation candidate, HiddenServices hidden, Compilation compilation, string[] arguments)
	{
		INamedTypeSymbol implementation = candidate.Implementation;
		IEnumerable<INamedTypeSymbol> services = candidate.Registrations.Select(registration => registration.Service);
		if (IsExternallyAccessible(implementation))
		{
			services = services.Concat([implementation,]);
		}

		foreach (INamedTypeSymbol service in services.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
		{
			bool internalService = IsHiddenService(service, hidden.Module, compilation);
			if (internalService)
			{
				hidden.Use(service);
			}

			string returnType = internalService ? hidden.HandleType(service) : service.ToDisplayString(FullyQualified);
			string serviceType = service.ToDisplayString(FullyQualified);
			yield return AliasOf(HiddenAliasName(implementation, service, candidate.Producer), returnType, hidden.InstanceHandleType(implementation, candidate.Producer), arguments, handle =>
			{
				string value = InstanceValue(implementation, serviceType, compilation, handle);
				return internalService ? $"new {returnType}({value})" : value;
			});
		}
	}

	/// <summary>
	///     An alias named <paramref name="name" /> that returns <paramref name="body" /> of the instance handle it takes, or,
	///     for an implementation built from runtime <paramref name="arguments" />, of the handle a factory of it builds from
	///     them, with the async counterpart that awaits an async factory of the handle.
	/// </summary>
	private static HiddenAlias AliasOf(string name, string returnType, string handleType, string[] arguments, Func<string, string> body)
	{
		string call = $"@awaiten__handle({string.Join(", ", arguments.Select((_, index) => $"@awaiten__arg{index + 1}"))})";
		return arguments.Length == 0
			? new HiddenAlias(name, returnType, handleType, body("@awaiten__handle"))
			: new HiddenAlias(name, returnType, handleType, body(call), new EquatableArray<string>(arguments), body($"(await {call}.ConfigureAwait(false))"));
	}

	/// <summary>
	///     The type the instance handle of <paramref name="implementation" /> holds its instance as: the implementation,
	///     or <see cref="object" /> for one the rest of its assembly cannot name, such as a type private to the module,
	///     which no member of the public handle may expose (see <see cref="InstanceValue" />).
	/// </summary>
	private static string InstanceValueType(INamedTypeSymbol implementation, Compilation compilation)
		=> compilation.IsSymbolAccessibleWithin(implementation, compilation.Assembly) ? implementation.ToDisplayString(FullyQualified) : "object";

	/// <summary>
	///     The instance an alias reads from the instance handle of <paramref name="implementation" /> as
	///     <paramref name="service" />, cast when the handle holds it as <see cref="object" /> (see <see cref="InstanceValueType" />).
	/// </summary>
	private static string InstanceValue(INamedTypeSymbol implementation, string service, Compilation compilation, string handle = "@awaiten__handle")
		=> compilation.IsSymbolAccessibleWithin(implementation, compilation.Assembly) ? $"{handle}.Value" : $"(({service}){handle}.Value)";

	/// <summary>
	///     The aliases of the matches of the module's scans whose implementation only other modules of the assembly
	///     build through a handle: one per match, returning the instance from the implementation's instance handle as the
	///     match's service, through which a consuming container serves the match from the handle instead of building a
	///     second instance (see <c>ShareHandledInstances</c>), as a container in the module's own assembly shares one. A
	///     match built from runtime arguments takes them and a factory of the handle, like the aliases of the handle's module.
	/// </summary>
	private static IEnumerable<HiddenAlias> ScanAliases(HiddenServices hidden, List<ModuleFactory> scanFactories)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		foreach (ModuleFactory factory in scanFactories.Where(factory => factory.Skip is null))
		{
			if (hidden.Assembly.HandledBy.Keys.FirstOrDefault(candidate => candidate.ToDisplayString(FullyQualified) == factory.ImplementationType) is not { } implementation
			    || !hidden.HandledElsewhere(implementation)
			    || hidden.Assembly.RegistrationsOf(hidden.Module).Any(registration => SymbolEqualityComparer.Default.Equals(registration.Implementation, implementation))
			    || !names.Add(HiddenAliasName(implementation, factory.ServiceType)))
			{
				continue;
			}

			hidden.ScanAliased.Add(implementation);
			string[] arguments = factory.Parameters.AsArray().Where(parameter => parameter.Attributes == ArgAttribute).Select(parameter => parameter.Type).ToArray();
			yield return AliasOf(HiddenAliasName(implementation, factory.ServiceType), factory.ServiceType, hidden.InstanceHandleType(implementation), arguments,
				handle => InstanceValue(implementation, factory.ServiceType, hidden.Compilation, handle));
		}
	}

	/// <summary>
	///     Mirrors a member that takes an internal service the module registers onto a generated member through the
	///     service's handle: the parameter takes the handle in the same shape (see <see cref="HiddenShape" />) and the
	///     generated member passes the instance on. Reports AWT237 for a shape it cannot pass on, and returns
	///     <see langword="null" /> when the member takes no such service, or one that cannot be built, which is then
	///     recorded so its cause is reported and returned as <paramref name="unbuildable" />, while the member falls back
	///     to AWT195. A factory with a runtime argument another assembly cannot name falls back to AWT195 too. Only in the
	///     module's own build.
	/// </summary>
	private static HiddenMirror? MirrorHiddenService(ITypeSymbol type, string name, ImmutableArray<AttributeData> attributes, ModuleScanContext context, out INamedTypeSymbol? unbuildable)
	{
		unbuildable = null;
		if (context.Hidden is not { } hidden || HasArgAttribute(attributes) || HasRequestingType(attributes))
		{
			return null;
		}

		(HiddenShape? shape, ITypeSymbol inner) = HiddenShapeOf(type);
		ITypeSymbol[] arguments = shape is HiddenShape.FuncWithArguments or HiddenShape.FuncWithArgumentsOwned
			? ((INamedTypeSymbol)type).TypeArguments.Take(((INamedTypeSymbol)type).TypeArguments.Length - 1).ToArray()
			: [];
		if (shape is { } known && inner is INamedTypeSymbol service && (hidden.Services.ContainsKey(service) || hidden.ProvidedElsewhere(service))
		    && arguments.All(argument => FitsFactorySignature(argument, context)))
		{
			hidden.Referenced?.Add(service);
			if (hidden.NonViable.Contains(service))
			{
				hidden.NeededNonViable.Add(service);
				unbuildable = service;
				return null;
			}

			hidden.Use(service);
			string handle = hidden.HandleType(service);
			string value = service.ToDisplayString(FullyQualified);
			const string unwrap = "awaiten__handle => awaiten__handle.Value";
			string argumentTypes = string.Concat(arguments.Select(argument => argument.ToDisplayString(FullyQualifiedWithNullability) + ", "));
			string lambdaArguments = string.Join(", ", arguments.Select((_, index) => $"awaiten__arg{index + 1}"));
			(string parameterType, string argument) = known switch
			{
				HiddenShape.Func => ($"global::System.Func<{handle}>", $"() => @{name}().Value"),
				HiddenShape.Lazy => ($"global::System.Lazy<{handle}>", $"new global::System.Lazy<{value}>(() => @{name}.Value.Value)"),
				HiddenShape.Owned => ($"global::Awaiten.Owned<{handle}>", $"global::Awaiten.Internals.AwaitenOwnedProjection.Map(@{name}, {unwrap})"),
				HiddenShape.FuncOwned => ($"global::System.Func<global::Awaiten.Owned<{handle}>>", $"() => global::Awaiten.Internals.AwaitenOwnedProjection.Map(@{name}(), {unwrap})"),
				HiddenShape.FuncWithArguments => ($"global::System.Func<{argumentTypes}{handle}>", $"({lambdaArguments}) => @{name}({lambdaArguments}).Value"),
				HiddenShape.FuncWithArgumentsOwned => ($"global::System.Func<{argumentTypes}global::Awaiten.Owned<{handle}>>",
					$"({lambdaArguments}) => global::Awaiten.Internals.AwaitenOwnedProjection.Map(@{name}({lambdaArguments}), {unwrap})"),
				HiddenShape.Collection => (type is IArrayTypeSymbol ? $"{handle}[]" : $"global::System.Collections.Generic.{type.Name}<{handle}>",
					$"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(@{name}, {unwrap}))"),
				_ => (handle, $"@{name}.Value"),
			};
			return new HiddenMirror(new FactoryParameter(parameterType, name, KeyAttribute(FromKey(attributes)), argument), null);
		}

		return ReferencedHiddenService(type, hidden) is { } referenced && (shape is null || !SymbolEqualityComparer.Default.Equals(inner, referenced))
			? new HiddenMirror(default, new PendingDiagnostic(Diagnostics.ModuleHiddenServiceShape, [Display(referenced.ToDisplayString(FullyQualified)), Display(type.ToDisplayString(FullyQualified)),]))
			: null;
	}

	/// <summary>
	///     The reason a consumer reports for an implementation whose <paramref name="member" /> takes the internal
	///     <paramref name="service" /> the module cannot build (see <see cref="HiddenServices.UnbuildableNeeds" />), with
	///     the module's <paramref name="rejection" /> of it when given, for a member whose cause the module's build may not
	///     report.
	/// </summary>
	private static string UnbuildableNeed(string member, INamedTypeSymbol service, DiagnosticInfo? rejection = null)
		=> $"{member} needs the internal service '{Display(service.ToDisplayString(FullyQualified))}', {(rejection is null ? "which the module cannot build" : CannotBuild("the module", rejection))}";

	/// <summary>
	///     Like the overload without <paramref name="builder" />, for an internal service only
	///     <paramref name="builder" />, another module of the assembly, registers, named with its
	///     <paramref name="rejection" />, which neither module reports (see <see cref="HiddenServices.RejectedElsewhere" />).
	/// </summary>
	private static string UnbuildableNeed(string member, INamedTypeSymbol service, INamedTypeSymbol builder, DiagnosticInfo rejection)
		=> $"{member} needs the internal service '{Display(service.ToDisplayString(FullyQualified))}', {CannotBuild($"the module '{Display(builder.ToDisplayString(FullyQualified))}'", rejection)}";

	/// <summary>Why <paramref name="builder" /> cannot build an internal service, as a clause naming its <paramref name="rejection" />.</summary>
	private static string CannotBuild(string builder, DiagnosticInfo rejection)
		=> $"which {builder} cannot build ({rejection.Descriptor.Id}: {rejection.ToDiagnostic().GetMessage(CultureInfo.InvariantCulture)})";

	/// <summary>
	///     The shape in which <paramref name="type" /> takes a service and that service, or a <see langword="null" />
	///     shape for a relationship or collection type whose service a generated member cannot pass on from a handle.
	/// </summary>
	private static (HiddenShape? Shape, ITypeSymbol Inner) HiddenShapeOf(ITypeSymbol type)
	{
		if (type is IArrayTypeSymbol array)
		{
			return array.Rank == 1 ? (HiddenShape.Collection, array.ElementType) : (null, type);
		}

		if (type is not INamedTypeSymbol { IsGenericType: true, } named)
		{
			return (HiddenShape.Direct, type);
		}

		string? ns = named.ContainingNamespace?.ToDisplayString();
		ITypeSymbol argument = named.TypeArguments[named.TypeArguments.Length - 1];
		if (named.TypeArguments.Length > 1)
		{
			return (ns, named.Name) is ("System", "Func")
				? IsOwned(argument, out ITypeSymbol ownedResult) ? (HiddenShape.FuncWithArgumentsOwned, ownedResult) : (HiddenShape.FuncWithArguments, argument)
				: (HiddenShape.Direct, type);
		}

		return (ns, named.Name) switch
		{
			("System", "Func") => IsOwned(argument, out ITypeSymbol owned) ? (HiddenShape.FuncOwned, owned) : (HiddenShape.Func, argument),
			("System", "Lazy") => (HiddenShape.Lazy, argument),
			(AttributeNamespace, "Owned") => (HiddenShape.Owned, argument),
			("System.Collections.Generic", "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "IList" or "ICollection") => (HiddenShape.Collection, argument),
			_ => (HiddenShape.Direct, type),
		};
	}

	/// <summary>
	///     An internal service with a handle, which the module or another module of its assembly registers, that
	///     <paramref name="type" /> carries as a type argument or array element, or <see langword="null" />.
	/// </summary>
	private static INamedTypeSymbol? ReferencedHiddenService(ITypeSymbol type, HiddenServices hidden)
		=> type switch
		{
			IArrayTypeSymbol array => ReferencedHiddenService(array.ElementType, hidden),
			INamedTypeSymbol named => hidden.Services.ContainsKey(named) || (IsHiddenService(named, hidden.Module, hidden.Compilation) && hidden.Provider(named) is not null)
				? named
				: named.TypeArguments.Select(argument => ReferencedHiddenService(argument, hidden)).FirstOrDefault(service => service is not null),
			_ => null,
		};

	/// <summary>
	///     AWT236 when a member of the module takes an internal service that the module does not register but other
	///     modules of its assembly do, none of which can share its handle (see <see cref="HiddenServices.ProvidedElsewhere" />).
	///     The message names every such module. Only in the module's own build.
	/// </summary>
	private static PendingDiagnostic? InternalServiceOfAnotherModule(ITypeSymbol type, ModuleScanContext context)
	{
		if (context.Hidden is not { } hidden
		    || HiddenShapeOf(type) is not ({ }, INamedTypeSymbol service)
		    || !IsHiddenService(service, context.Module, context.Compilation))
		{
			return null;
		}

		List<string> others = hidden.Assembly.Registrations
			.Where(other => !SymbolEqualityComparer.Default.Equals(other.Key, context.Module)
			                && other.Value.Any(registration => SymbolEqualityComparer.Default.Equals(registration.Service, service)))
			.Select(other => $"'{Display(other.Key.ToDisplayString(FullyQualified))}'")
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToList();
		return others.Count == 0
			? null
			: new PendingDiagnostic(Diagnostics.ModuleHiddenServiceInOtherModule, [
				Display(service.ToDisplayString(FullyQualified)),
				$"{(others.Count == 1 ? "the module" : "the modules")} {string.Join(", ", others)}",
				Display(context.Module.ToDisplayString(FullyQualified)),
			]);
	}

	/// <summary>
	///     AWT195 when a member of the module takes an internal service that the module does not register and that only
	///     other modules of its assembly register, the first of which cannot build it (see
	///     <see cref="HiddenServices.RejectedElsewhere" />), naming that module and why, or <see langword="null" />. Judging
	///     another module judges its services anew, so this waits until the module's own are judged (see
	///     <see cref="HiddenServices.Tracking" />), which keeps that from recursing. Only in the module's own build.
	/// </summary>
	private static DiagnosticInfo? ServiceRejectedElsewhere(ITypeSymbol type, ModuleScanContext context, Location? location, SelfCompiledSubject subject, string member)
		=> context.Hidden is { Tracking: true, } hidden
		   && HiddenShapeOf(type) is ({ }, INamedTypeSymbol service)
		   && !hidden.Services.ContainsKey(service)
		   && IsHiddenService(service, context.Module, context.Compilation)
		   && hidden.RejectedElsewhere(service) is var (builder, rejection)
			? new DiagnosticInfo(
				Diagnostics.ModuleParameterNeedsUnbuildableService,
				LocationInfo.From(location),
				new EquatableArray<string>([subject.Subject, UnbuildableNeed(member, service, builder, rejection),]))
			: null;

	/// <summary>The classes with the Awaiten attribute <paramref name="attribute" /> declared in <paramref name="ns" /> and its nested namespaces and types.</summary>
	private static IEnumerable<INamedTypeSymbol> TypesWith(INamespaceOrTypeSymbol ns, string attribute)
	{
		foreach (ISymbol member in ns.GetMembers())
		{
			if (member is INamespaceSymbol nested)
			{
				foreach (INamedTypeSymbol found in TypesWith(nested, attribute))
				{
					yield return found;
				}
			}
			else if (member is INamedTypeSymbol type)
			{
				if (HasAwaitenAttribute(type.GetAttributes(), attribute))
				{
					yield return type;
				}

				foreach (INamedTypeSymbol found in TypesWith(type, attribute))
				{
					yield return found;
				}
			}
		}
	}

	/// <summary>
	///     A deterministic name for the handle of an internal service, derived from the service's fully-qualified name
	///     only, so the module's build and a consuming container agree on it.
	/// </summary>
	private static string HiddenHandleName(INamedTypeSymbol service)
		=> $"Hidden_{service.Name}_{Fnv1a(service.ToDisplayString(FullyQualified)):x8}";

	/// <summary>
	///     A deterministic name for the instance handle of an implementation, derived from its fully-qualified name only,
	///     so every module of its assembly and a consuming container agree on it, or for the instance handle of one of
	///     several <paramref name="producer" /> members of a service (see <see cref="HiddenImplementation.Producer" />).
	/// </summary>
	private static string HiddenInstanceName(INamedTypeSymbol implementation, string? producer = null)
		=> $"HiddenInstance_{implementation.Name}_{Fnv1a(HiddenKey(implementation, producer)):x8}";

	/// <summary>A deterministic name for the factory that builds an implementation, or one of its <paramref name="producer" /> members, into its handle.</summary>
	private static string HiddenFactoryName(INamedTypeSymbol implementation, string? producer = null)
		=> $"HiddenRegistration_{implementation.Name}_{Fnv1a(HiddenKey(implementation, producer)):x8}";

	/// <summary>A deterministic name for the alias that serves <paramref name="service" /> from the handle of <paramref name="implementation" />, or of its <paramref name="producer" />.</summary>
	private static string HiddenAliasName(INamedTypeSymbol implementation, INamedTypeSymbol service, string? producer = null)
		=> HiddenAliasName(implementation, service.ToDisplayString(FullyQualified), producer);

	/// <inheritdoc cref="HiddenAliasName(INamedTypeSymbol, INamedTypeSymbol, string?)" />
	private static string HiddenAliasName(INamedTypeSymbol implementation, string service, string? producer = null)
		=> $"Alias_{implementation.Name}_{Fnv1a($"{HiddenKey(implementation, producer)}\0{service}"):x8}";

	/// <summary>A deterministic name for the wrapper of a hook that takes the handle of <paramref name="implementation" />, or of its <paramref name="producer" />.</summary>
	private static string HiddenHookName(INamedTypeSymbol implementation, bool release, string hookName, string? producer = null)
		=> producer is null
			? "Hidden" + RegistrationHookWrapperName(implementation, release, hookName)
			: "Hidden" + RegistrationHookWrapperName(implementation, release, $"{producer}\0{hookName}");

	/// <summary>A deterministic name for the wrapper that runs <paramref name="convention" /> on the handle of <paramref name="implementation" />, or of its <paramref name="producer" />.</summary>
	private static string HiddenInitializerName(INamedTypeSymbol implementation, AsyncInitializerConvention convention, string? producer = null)
		=> $"HiddenInitializer_{implementation.Name}_{Fnv1a($"{HiddenKey(implementation, producer)}\0{convention.Type.ToDisplayString(FullyQualified)}\0{convention.Method.Name}"):x8}";

	/// <summary>What the names of the handles of <paramref name="implementation" />, or of its <paramref name="producer" />, are derived from.</summary>
	private static string HiddenKey(INamedTypeSymbol implementation, string? producer)
		=> producer is null ? implementation.ToDisplayString(FullyQualified) : $"{implementation.ToDisplayString(FullyQualified)}\0{producer}";
}
