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
	///     is reported once, where it first enters the graph.
	/// </summary>
	/// <remarks>Lives only inside one graph build and never reaches a model.</remarks>
	internal sealed class AmbiguousTypeNames
	{
		private readonly Compilation _compilation;
		private readonly List<DiagnosticInfo> _diagnostics;
		private readonly Dictionary<INamedTypeSymbol, string[]?> _assemblies = new(SymbolEqualityComparer.Default);
		private readonly HashSet<INamedTypeSymbol> _reported = new(SymbolEqualityComparer.Default);

		public AmbiguousTypeNames(Compilation compilation, List<DiagnosticInfo> diagnostics)
		{
			_compilation = compilation;
			_diagnostics = diagnostics;
		}

		/// <summary>
		///     Reports each type within <paramref name="type" /> (itself, its type arguments, element types and containing types) that
		///     the generated code cannot name, as needed by <paramref name="needer" />, which <paramref name="verb" /> it.
		/// </summary>
		public void Report(ITypeSymbol type, ISymbol needer, string verb, LocationInfo? location)
		{
			foreach (INamedTypeSymbol definition in OutermostDefinitions(type))
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
				string name = names.Pop();
				resolved = resolved.GetNamespaceMembers().FirstOrDefault(member => member.Name == name);
			}

			return resolved;
		}

		/// <summary>The top-level type definitions <paramref name="type" /> spells, each of which <c>global::</c> looks up by its full name.</summary>
		private static IEnumerable<INamedTypeSymbol> OutermostDefinitions(ITypeSymbol type)
		{
			switch (type)
			{
				case IArrayTypeSymbol array:
					return OutermostDefinitions(array.ElementType);
				case IPointerTypeSymbol pointer:
					return OutermostDefinitions(pointer.PointedAtType);
				case INamedTypeSymbol named:
					List<INamedTypeSymbol> definitions = new();
					INamedTypeSymbol outermost = named;
					for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
					{
						outermost = current;
						foreach (ITypeSymbol argument in current.TypeArguments)
						{
							definitions.AddRange(OutermostDefinitions(argument));
						}
					}

					definitions.Insert(0, outermost.OriginalDefinition);
					return definitions;
				default:
					return [];
			}
		}

		/// <summary>
		///     Reports the service of each registration in <paramref name="raw" /> that joins the graph, which the container names in
		///     its resolvers, as declared by the container or module the registration comes from. A default another registration
		///     replaces never joins it.
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
					Report(service, registration.Declarer ?? registration.Origin ?? containerSymbol, "registers", LocationInfo.From(registration.Location));
				}
			}
		}

		private static string Enumerate(string[] assemblies)
			=> $"{string.Join(", ", assemblies.Take(assemblies.Length - 1).Select(name => $"'{name}'"))} and '{assemblies[assemblies.Length - 1]}'";
	}
}
