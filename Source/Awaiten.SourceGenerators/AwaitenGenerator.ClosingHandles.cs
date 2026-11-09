using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>The prefix of the name of a closing handle (see <see cref="ClosingHandle" />).</summary>
	private const string ClosingHandlePrefix = "Closed_";

	/// <summary>
	///     The closings over an internal type that the generated members of a compilation's modules take, by their fully
	///     qualified name, so a module's build finds the symbol of each closing its members took again. It holds symbols
	///     only while the compilation lives and never reaches a model.
	/// </summary>
	private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<string, INamedTypeSymbol>> ClosingSymbols = new();

	/// <summary>
	///     The closing handle (see <see cref="ClosingHandle" />) of <paramref name="type" /> when it closes a public generic
	///     definition over a type no other assembly can name but the module can, such as <c>ILogger&lt;Brew&gt;</c> for an
	///     internal <c>Brew</c>, or <see langword="null" />. The module cannot know who closes the definition for a container
	///     in another assembly, so it passes the closing on through the handle and leaves that to the container. A
	///     relationship, collection or task over an internal type is no such closing, since the container resolves it from
	///     the service it wraps.
	/// </summary>
	private static ClosingHandle? ClosingOverInternal(ITypeSymbol type, ModuleScanContext context)
	{
		if (type is not INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false, } closing
		    || !IsExternallyAccessible(closing.OriginalDefinition)
		    || closing.ContainingNamespace?.ToDisplayString() is "System" or "System.Collections.Generic" or "System.Threading.Tasks" or AttributeNamespace
		    || !context.Compilation.IsSymbolAccessibleWithin(closing, context.Module)
		    || TypeHiddenInModule(closing, context.Module, context.Compilation) is not null)
		{
			return null;
		}

		string closed = closing.ToDisplayString(FullyQualified);
		ClosingSymbols.GetValue(context.Compilation, static _ => new ConcurrentDictionary<string, INamedTypeSymbol>(StringComparer.Ordinal))[closed] = closing;
		return new ClosingHandle($"{ClosingHandlePrefix}{closing.Name}_{Fnv1a(closed):x8}", closed, UnboundDisplay(closing));
	}

	/// <summary>The fully qualified name of the handle <paramref name="closing" /> declared by <paramref name="module" />.</summary>
	private static string ClosingHandleType(INamedTypeSymbol module, ClosingHandle closing)
		=> $"{module.ToDisplayString(FullyQualified)}.{InternalsClass}.{closing.Name}";

	/// <summary>The name of the generated factory that builds the closing a handle holds from the module's open registration.</summary>
	private static string ClosingRegistrationName(string handle) => "Registration_" + handle;

	/// <summary>
	///     The closing handles <paramref name="module" /> declares: each closing its generated members take, completed with
	///     the factory of the module's own open registration of its definition (see <see cref="WithOpenRegistration" />),
	///     and the closings those factories take in turn.
	/// </summary>
	private static List<ClosingHandle> CompleteClosings(IEnumerable<ClosingHandle> taken, INamedTypeSymbol module, Compilation compilation)
	{
		ConcurrentDictionary<string, INamedTypeSymbol> symbols = ClosingSymbols.GetValue(compilation, static _ => new ConcurrentDictionary<string, INamedTypeSymbol>(StringComparer.Ordinal));
		List<ClosingHandle> completed = new();
		HashSet<string> seen = new(StringComparer.Ordinal);
		Queue<ClosingHandle> pending = new(taken);
		while (pending.Count > 0)
		{
			ClosingHandle closing = pending.Dequeue();
			if (!seen.Add(closing.Name))
			{
				continue;
			}

			ClosingHandle complete = symbols.TryGetValue(closing.ClosedType, out INamedTypeSymbol? symbol) ? WithOpenRegistration(closing, symbol, module, compilation) : closing;
			completed.Add(complete);
			foreach (FactoryParameter parameter in complete.Parameters.AsArray())
			{
				if (parameter.Closing is { } nested)
				{
					pending.Enqueue(nested);
				}
			}
		}

		return completed;
	}

	/// <summary>
	///     <paramref name="closing" /> with the factory that constructs it from the first unkeyed open registration of its
	///     definition <paramref name="module" /> declares, in the order a container picks one (a registration without
	///     <c>Fallback</c> first), or with the reason the module cannot generate one, which a container that keeps the
	///     registration reports. Unchanged when the module registers no such definition. The factory mirrors the closed
	///     implementation's constructor like the factory of a closed registration. A hook, a generic factory, a convention
	///     of the module that initializes the closing, and a decorator or composite of the module over the definition
	///     cannot apply to it, since a container in another assembly builds it only through the handle.
	/// </summary>
	private static ClosingHandle WithOpenRegistration(ClosingHandle closing, INamedTypeSymbol symbol, INamedTypeSymbol module, Compilation compilation)
	{
		INamedTypeSymbol definition = symbol.OriginalDefinition;
		AttributeData? registration = module.GetAttributes()
			.Where(attribute => attribute.AttributeClass is { IsGenericType: false, Name: "SingletonAttribute" or "ScopedAttribute" or "TransientAttribute", } attributeClass
			                    && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace
			                    && OpenContributionPair(attribute) is (_, { } service)
			                    && SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, definition)
			                    && NamedKeyArgument(attribute) is null
			                    && NamedTypeArgument(attribute, "WhenInjectedInto", compilation) is null)
			.OrderBy(attribute => NamedFallback(attribute) != 0)
			.FirstOrDefault();
		if (registration is null)
		{
			return closing;
		}

		INamedTypeSymbol implementation = OpenContributionPair(registration)!.Value.Definition.Construct(symbol.TypeArguments.ToArray());
		string display = Display(definition.ConstructUnboundGenericType().ToDisplayString(FullyQualified));
		string? reason = NamedArgument(registration, "Factory") is { } factory ? $"its registration produces it through the generic factory '{factory}'"
			: NamedArgument(registration, "OnActivated") is { } activated ? $"its registration names the OnActivated hook '{activated}', which the factory cannot run"
			: NamedArgument(registration, "OnRelease") is { } released ? $"its registration names the OnRelease hook '{released}', which the factory cannot run"
			: !ConstraintsSatisfied(implementation.OriginalDefinition, symbol.TypeArguments.ToArray(), compilation) ? $"'{Display(implementation.ToDisplayString(FullyQualified))}' violates the constraints of its definition"
			: MatchingAsyncInitializers(DeclaredAsyncInitializers(module, compilation, null), implementation, convention => IsOfConventionType(implementation, convention.Type)).Count > 0
				? $"an async initializer convention of the module initializes '{Display(implementation.ToDisplayString(FullyQualified))}', which the factory cannot run"
			: module.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "DecorateAttribute" or "CompositeAttribute", } attributeClass
			                                          && attributeClass.ContainingNamespace?.ToDisplayString() == AttributeNamespace
			                                          && ContributedPair(attribute, attributeClass).Service is { } decorated
			                                          && SymbolEqualityComparer.Default.Equals(decorated.OriginalDefinition, definition))
				? $"the module decorates or composes '{display}', which the factory cannot apply"
			: null;
		if (reason is not null)
		{
			return closing with { SkipReason = reason, };
		}

		List<DiagnosticInfo> rejections = new();
		ModuleScanContext context = new(module, compilation, rejections, new HashSet<string>(StringComparer.Ordinal));
		if (!TryMirrorFactoryParameters(implementation, context, null, RegistrationSubject(implementation), out EquatableArray<FactoryParameter> parameters) || rejections.Count > 0)
		{
			return closing with { SkipReason = rejections.Count > 0 ? OpenSkipReason(rejections[0]) : $"'{Display(implementation.ToDisplayString(FullyQualified))}' cannot be built through a generated factory", };
		}

		return closing with
		{
			Implementation = implementation.ToDisplayString(FullyQualified),
			Parameters = parameters,
			Disposable = implementation.AllInterfaces.Any(contract => contract.ToDisplayString(FullyQualified) == "global::System.IDisposable"),
			AsyncDisposable = implementation.AllInterfaces.Any(contract => contract.ToDisplayString(FullyQualified) == "global::System.IAsyncDisposable"),
		};
	}

	/// <summary>
	///     The unbound definition and the closing that the closing handle <paramref name="type" /> records, which a module
	///     of another assembly declares for a closing over a type internal to its assembly (see <see cref="ClosingHandle" />),
	///     or <see langword="null" /> when <paramref name="type" /> is no such handle.
	/// </summary>
	private static (string Definition, string Closing)? ClosingHandleOf(ITypeSymbol type)
		=> type is INamedTypeSymbol { ContainingType: { Name: InternalsClass, ContainingType: not null, }, } handle
		   && handle.Name.StartsWith(ClosingHandlePrefix, StringComparison.Ordinal)
		   && RecordedText(handle, "Definition") is { } definition
		   && RecordedText(handle, "Closing") is { } closing
			? (definition, closing)
			: null;

	/// <summary>The value of the string constant <paramref name="name" /> of <paramref name="type" />, or <see langword="null" />.</summary>
	private static string? RecordedText(INamedTypeSymbol type, string name)
		=> type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(field => field.HasConstantValue)?.ConstantValue as string;

	/// <summary>
	///     Registers, as a dependency-only service, each closing handle of an imported module of another assembly that the
	///     container builds from the module's own open registration of its definition: the registration the container
	///     keeps for the definition (unkeyed, without a context, one without <c>Fallback</c> first) is the module's, the
	///     module generated the factory of the closing, and nothing the container applies to the definition would be lost
	///     on it. The handle is built with the lifetime of that registration and disposes what it holds. A handle whose
	///     definition the container imports from its host is resolved externally instead (see <c>ClassifyDeclaredType</c>).
	///     A container in the module's own assembly, or in one it grants <c>InternalsVisibleTo</c>, constructs what takes
	///     the closing itself and needs no handle.
	///     Returns, for every other handle, why the container cannot build it, which it reports when something takes the
	///     handle (AWT253).
	/// </summary>
	private static Dictionary<string, string> RegisterClosings(
		List<RawRegistration> raw,
		List<OpenRegistration> open,
		List<ImportedModule> modules,
		INamedTypeSymbol container,
		Compilation compilation,
		ExternalSurface external,
		WrapperRegistrations wrappers)
	{
		Dictionary<string, string> faults = new(StringComparer.Ordinal);
		foreach (INamedTypeSymbol module in modules
			         .Select(import => import.Symbol)
			         .Where(module => !module.ContainingAssembly.GivesAccessTo(compilation.Assembly))
			         .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
		{
			foreach (INamedTypeSymbol handle in module.GetTypeMembers(InternalsClass).SelectMany(internals => internals.GetTypeMembers()))
			{
				if (ClosingHandleOf(handle) is not var (definition, _) || external.ServiceTypes.Open.Contains(definition))
				{
					continue;
				}

				string handleType = handle.ToDisplayString(FullyQualified);
				string moduleName = Display(module.ToDisplayString(FullyQualified));
				OpenRegistration? kept = open
					.Where(candidate => candidate is { Dropped: false, Key: null, WhenInjectedInto: null, } && UnboundDisplay(candidate.Service) == definition)
					.OrderBy(candidate => candidate.Weak)
					.FirstOrDefault();
				if (kept is null)
				{
					continue;
				}

				if (!SymbolEqualityComparer.Default.Equals(kept.Origin, module))
				{
					faults[handleType] = $"'{Display(definition)}' is registered by {(kept.Origin is { } origin ? $"the module '{Display(origin.ToDisplayString(FullyQualified))}'" : "the container")}, which cannot close it over that type; register '{Display(definition)}' in the module '{moduleName}' instead, or import it from the host with [ImportService(typeof({Display(definition)}))]";
					continue;
				}

				if (GeneratedFactoryNamed(module, ClosingRegistrationName(handle.Name), compilation) is null)
				{
					faults[handleType] = $"the module registers '{Display(definition)}', but generated no factory that closes it over that type because {RecordedText(handle, "RegistrationSkipped") ?? "its assembly was compiled with another version of the Awaiten source generator"}; import '{Display(definition)}' from the host with [ImportService(typeof({Display(definition)}))]";
					continue;
				}

				if (wrappers.OpenDecorators.Any(decorator => UnboundDisplay(decorator.Service) == definition)
				    || wrappers.OpenComposites.Any(composite => UnboundDisplay(composite.Service) == definition))
				{
					faults[handleType] = $"a decorator or composite of '{Display(definition)}' cannot apply to that closing; remove it, or import '{Display(definition)}' from the host with [ImportService(typeof({Display(definition)}))]";
					continue;
				}

				raw.Add(new RawRegistration(
					handleType,
					TypeIdentity(handle, compilation),
					kept.Lifetime,
					handle,
					kept.LiveLocation,
					ProductionKind.Factory,
					InInternals(ClosingRegistrationName(handle.Name)),
					ServiceSymbol: handle,
					Origin: module,
					SuppressDisposal: kept.SuppressDisposal,
					Traits: TraitsOf(handle, []),
					TraitsExact: true,
					Unlisted: true));
			}
		}

		return faults;
	}

	/// <summary>
	///     AWT253 for a dependency on the closing handle <paramref name="dependency" /> the container neither builds nor
	///     resolves externally, with why from <paramref name="faults" /> (see <see cref="RegisterClosings" />), or because
	///     nothing provides the definition.
	/// </summary>
	private static DiagnosticInfo UnservedClosing(ParameterModel dependency, ITypeSymbol handle, ImplInfo info, Dictionary<string, string> faults)
	{
		(string definition, string closing) = ClosingHandleOf(handle)!.Value;
		string module = Display(handle.ContainingType!.ContainingType!.ToDisplayString(FullyQualified));
		string reason = dependency.Key is null && faults.TryGetValue(dependency.ServiceType, out string? fault)
			? fault
			: $"neither the container nor an imported module registers or imports '{Display(definition)}'; import it from the host with [ImportService(typeof({Display(definition)}))], or register it in the module '{module}'";
		return new DiagnosticInfo(
			Diagnostics.UnservedClosingOverInternalType,
			info.Location,
			new EquatableArray<string>([DisplayInstance(info.OwningServiceOrImpl), DisplayInstance(info.ImplementationType), DisplayKeyed(closing, dependency.Key), module, reason,]));
	}
}
