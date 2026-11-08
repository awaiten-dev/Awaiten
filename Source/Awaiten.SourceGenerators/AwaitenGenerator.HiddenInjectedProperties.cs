using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     The <c>[Inject]</c> properties a container cannot see on a type it constructs (AWT244): declared in a referenced
	///     assembly as internal or private, which the compiler leaves out of the container's view of that metadata. They are
	///     looked up in a second view of the same metadata that imports every member, created at most once per container
	///     build and only for a type that inherits from an assembly referenced as metadata (a project reference in the IDE
	///     keeps its internal members visible) that references Awaiten, the only kind that can declare an <c>[Inject]</c>
	///     property, and grants the container's assembly no <c>InternalsVisibleTo</c>.
	/// </summary>
	private sealed class HiddenInjectedProperties
	{
		private readonly Compilation _compilation;
		private Compilation? _fullView;

		public HiddenInjectedProperties(Compilation compilation) => _compilation = compilation;

		/// <summary>The <c>[Inject]</c> properties of <paramref name="type" /> the container's view of it lacks.</summary>
		public List<IPropertySymbol> Of(INamedTypeSymbol type)
		{
			List<IPropertySymbol> hidden = new();
			if (!InheritsFromHidingAssembly(type) || FullView(type.OriginalDefinition) is not { } full)
			{
				return hidden;
			}

			HashSet<string> visible = new(InjectedProperties(type).Select(property => property.Name), StringComparer.Ordinal);
			hidden.AddRange(InjectedProperties(full).Where(property => !visible.Contains(property.Name)));
			return hidden;
		}

		private bool InheritsFromHidingAssembly(INamedTypeSymbol type)
		{
			for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
			{
				IAssemblySymbol assembly = current.ContainingAssembly;
				if (_compilation.GetMetadataReference(assembly) is PortableExecutableReference
				    && !assembly.GivesAccessTo(_compilation.Assembly)
				    && assembly.Modules.Any(module => module.ReferencedAssemblies.Any(identity => identity.Name == "Awaiten")))
				{
					return true;
				}
			}

			return false;
		}

		private INamedTypeSymbol? FullView(INamedTypeSymbol definition)
		{
			_fullView ??= _compilation.WithOptions(_compilation.Options.WithMetadataImportOptions(MetadataImportOptions.All));
			IAssemblySymbol? assembly = SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, _compilation.Assembly)
				? _fullView.Assembly
				: _compilation.GetMetadataReference(definition.ContainingAssembly) is { } reference
					? _fullView.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol
					: null;
			return assembly?.GetTypeByMetadataName(MetadataName(definition));
		}

		private static string MetadataName(INamedTypeSymbol type)
			=> type.ContainingType is { } outer
				? $"{MetadataName(outer)}+{type.MetadataName}"
				: type.ContainingNamespace is { IsGlobalNamespace: false, } ns
					? $"{ns.ToDisplayString()}.{type.MetadataName}"
					: type.MetadataName;
	}

	/// <summary>
	///     Reports AWT244 for each <c>[Inject]</c> property of an implementation the container constructs that is
	///     declared in another assembly and not accessible from the container's, which it would otherwise leave unset.
	/// </summary>
	private static void ReportHiddenInjectedProperties(ImplInfo info, BuildContext context)
	{
		foreach (IPropertySymbol property in context.HiddenInjectedProperties.Of(info.Symbol))
		{
			context.Diagnostics.Add(HiddenInjectedPropertyDiagnostic(context.ContainerSymbol, info, property));
		}
	}

	private static DiagnosticInfo HiddenInjectedPropertyDiagnostic(INamedTypeSymbol container, ImplInfo info, IPropertySymbol property)
		=> new(
			Diagnostics.HiddenInjectedProperty,
			info.Location,
			new EquatableArray<string>([
				Display(container.ToDisplayString(FullyQualified)),
				DisplayInstance(info.ImplementationType),
				property.Name,
				property.ContainingAssembly.Name,
			]));
}
