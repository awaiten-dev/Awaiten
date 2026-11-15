using System.Collections.Immutable;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     Reports <see cref="Diagnostics.AmbiguousTypeName">AWT260</see> for a type the container's graph needs that its generated
	///     code cannot name: another type of the same full name that the container's assembly can access, declared by another
	///     referenced assembly or by the project itself, makes <c>global::</c> name both (CS0433) or the project's own. Each such type
	///     is reported once, where it first enters the graph. The generated code names an imported module or the extended base
	///     container only to call into it, or to name a type nested in it, which reports it at its import or the <c>Extends</c>.
	/// </summary>
	/// <remarks>Lives only inside one graph build and never reaches a model.</remarks>
	internal sealed class AmbiguousTypeNames
	{
		private readonly Compilation _compilation;
		private readonly List<DiagnosticInfo> _diagnostics;
		private readonly INamedTypeSymbol _container;
		private readonly List<ImportedModule> _modules;
		private readonly Dictionary<INamedTypeSymbol, string[]?> _assemblies = new(SymbolEqualityComparer.Default);
		private readonly HashSet<INamedTypeSymbol> _reported = new(SymbolEqualityComparer.Default);

		public AmbiguousTypeNames(Compilation compilation, List<DiagnosticInfo> diagnostics, INamedTypeSymbol container, List<ImportedModule> modules)
		{
			_compilation = compilation;
			_diagnostics = diagnostics;
			_container = container;
			_modules = modules;
		}

		/// <summary>
		///     Reports each type within <paramref name="type" /> (itself, its type arguments, element types and containing types) that
		///     the generated code cannot name, as needed by <paramref name="needer" />, which <paramref name="verb" /> it. A generated
		///     <c>Internals</c> class stands for the type the user wrote it for.
		/// </summary>
		public void Report(ITypeSymbol type, ISymbol needer, string verb, LocationInfo? location)
			=> ReportWithin(type, type, needer is INamedTypeSymbol { Name: InternalsClass, ContainingType: { } owner, } ? owner : needer, verb, location);

		/// <summary>
		///     Reports the imported module or extended base container that <paramref name="owner" /> is or is nested in, whose member
		///     the generated code calls, such as a handle, a factory, a hook, a predicate or a wrapper it generates.
		/// </summary>
		public void ReportOwner(INamedTypeSymbol? owner)
		{
			if (owner is not null && ImportOf(owner) is { } module)
			{
				ReportModule(module);
			}
		}

		/// <summary>The imported module or extended base container that <paramref name="type" /> is or is nested in, or <see langword="null" />.</summary>
		private ImportedModule? ImportOf(INamedTypeSymbol type)
		{
			for (INamedTypeSymbol? current = type.OriginalDefinition; current is not null; current = current.ContainingType)
			{
				if (_modules.FirstOrDefault(module => SymbolEqualityComparer.Default.Equals(module.Symbol, current)) is { } module)
				{
					return module;
				}
			}

			return null;
		}

		/// <summary>
		///     Reports <paramref name="module" /> as imported by the container, or by the base container for a module it inherits, or
		///     extended for the base itself, at that import or the <c>Extends</c>.
		/// </summary>
		private void ReportModule(ImportedModule module)
		{
			ISymbol needer = module is { Inherited: true, IsBase: false, }
				? _modules.FirstOrDefault(candidate => candidate.IsBase)?.Symbol ?? _container
				: _container;
			ReportDefinition(Outermost(module.Symbol).OriginalDefinition, module.Symbol, needer, module.IsBase ? "extends" : "imports", LocationInfo.From(module.ImportLocation));
		}

		private static INamedTypeSymbol Outermost(INamedTypeSymbol type)
		{
			while (type.ContainingType is { } containing)
			{
				type = containing;
			}

			return type;
		}

		/// <summary>
		///     Reports each top-level type definition <paramref name="part" /> of <paramref name="type" /> spells, which <c>global::</c>
		///     looks up by its full name.
		/// </summary>
		private void ReportWithin(ITypeSymbol part, ITypeSymbol type, ISymbol needer, string verb, LocationInfo? location)
		{
			switch (part)
			{
				case IArrayTypeSymbol array:
					ReportWithin(array.ElementType, type, needer, verb, location);
					break;
				case IPointerTypeSymbol pointer:
					ReportWithin(pointer.PointedAtType, type, needer, verb, location);
					break;
				case INamedTypeSymbol named:
					if (ImportOf(named) is { } module)
					{
						ReportModule(module);
					}
					else
					{
						ReportDefinition(Outermost(named).OriginalDefinition, type, needer, verb, location);
					}

					for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
					{
						foreach (ITypeSymbol argument in current.TypeArguments)
						{
							ReportWithin(argument, type, needer, verb, location);
						}
					}

					break;
			}
		}

		private void ReportDefinition(INamedTypeSymbol definition, ITypeSymbol type, ISymbol needer, string verb, LocationInfo? location)
		{
			if (AssembliesDeclaring(definition) is { } assemblies && _reported.Add(definition))
			{
				_diagnostics.Add(new DiagnosticInfo(
					Diagnostics.AmbiguousTypeName,
					location,
					new EquatableArray<string>([
						Display(type.ToDisplayString(FullyQualified)),
						Display(needer.ToDisplayString(FullyQualified)),
						verb,
						Enumerate(assemblies),
						Display(definition.ToDisplayString(FullyQualified)),
					])));
			}
		}

		/// <summary>
		///     Reports the type a key spells in the generated code, an enum or the operand of a <c>typeof(...)</c> (see
		///     <see cref="KeyType" />), as used by <paramref name="needer" />.
		/// </summary>
		public void ReportKey(ITypeSymbol? keyType, ISymbol needer, LocationInfo? location)
		{
			if (keyType is not null)
			{
				Report(keyType, needer, "uses as a key", location);
			}
		}

		/// <summary>Reports a dependency of <paramref name="type" /> that <paramref name="needer" /> takes, and the key of its <c>[FromKey]</c>.</summary>
		public void ReportDependency(ITypeSymbol type, ImmutableArray<AttributeData> attributes, ISymbol needer, LocationInfo? location)
		{
			Report(type, needer, "takes", location);
			ReportKey(FromKeyType(attributes), needer, location);
		}

		/// <summary>
		///     The names of the assemblies that declare a type of <paramref name="definition" />'s full name the compilation's
		///     assembly can access, when there is more than one and <paramref name="definition" /> is not the project's own, which
		///     <c>global::</c> then names; else <see langword="null" />. A type the compilation's assembly cannot access is never
		///     named by the generated code, and does not get in the way of the one it names. A type spelled by its keyword, such as
		///     <c>string</c>, always names the core library's.
		/// </summary>
		private string[]? AssembliesDeclaring(INamedTypeSymbol definition)
		{
			if (_assemblies.TryGetValue(definition, out string[]? cached))
			{
				return cached;
			}

			string[]? assemblies = null;
			if (definition is { TypeKind: not TypeKind.Error, ContainingAssembly: { } assembly, }
			    && definition.ToDisplayString(FullyQualified).StartsWith("global::", StringComparison.Ordinal)
			    && !SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly)
			    && _compilation.IsSymbolAccessibleWithin(definition, _compilation.Assembly)
			    && NamespaceOf(definition) is { } ns)
			{
				string[] others = ns.GetTypeMembers(definition.Name, definition.Arity)
					.Where(candidate => !SymbolEqualityComparer.Default.Equals(candidate, definition)
					                    && _compilation.IsSymbolAccessibleWithin(candidate, _compilation.Assembly))
					.Select(candidate => candidate.ContainingAssembly.Name)
					.ToArray();
				if (others.Length > 0)
				{
					assemblies = others.Append(assembly.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();
				}
			}

			_assemblies[definition] = assemblies;
			return assemblies;
		}

		/// <summary>The namespace of the compilation that <c>global::</c> resolves the namespace of <paramref name="definition" /> to.</summary>
		private INamespaceSymbol? NamespaceOf(INamedTypeSymbol definition)
		{
			Stack<string> names = new();
			for (INamespaceSymbol? ns = definition.ContainingNamespace; ns is { IsGlobalNamespace: false, }; ns = ns.ContainingNamespace)
			{
				names.Push(ns.Name);
			}

			INamespaceSymbol? resolved = _compilation.GlobalNamespace;
			while (resolved is not null && names.Count > 0)
			{
				resolved = resolved.GetMembers(names.Pop()).OfType<INamespaceSymbol>().FirstOrDefault();
			}

			return resolved;
		}

		/// <summary>
		///     Reports the service and the key of each registration in <paramref name="raw" /> that joins the graph, which the container
		///     names in its resolvers and keyed dispatch, as declared by the container or module the registration comes from. A default
		///     another registration replaces never joins it.
		/// </summary>
		public void ReportServices(List<RawRegistration> raw, Dictionary<ServiceKey, List<string>> serviceMembers, INamedTypeSymbol containerSymbol)
		{
			foreach (RawRegistration registration in raw)
			{
				ServiceKey key = new(registration.ServiceType, registration.WhenInjectedInto is { } consumer ? ContextKey(consumer) : registration.Key);
				if (registration.ServiceSymbol is { } service
				    && serviceMembers.TryGetValue(key, out List<string>? members)
				    && members.Contains(registration.ImplementationType))
				{
					ISymbol declarer = registration.Declarer ?? registration.Origin ?? containerSymbol;
					LocationInfo? location = LocationInfo.From(registration.Location);
					Report(service, declarer, "registers", location);
					ReportKey(registration.KeyType, declarer, location);
				}
			}
		}

		private static string Enumerate(string[] assemblies)
			=> $"{string.Join(", ", assemblies.Take(assemblies.Length - 1).Select(name => $"'{name}'"))} and '{assemblies[assemblies.Length - 1]}'";
	}
}
