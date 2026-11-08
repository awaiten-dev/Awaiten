using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	private const string HiddenInjectedPropertyAttributeName = "GeneratedHiddenInjectedPropertyAttribute";

	/// <summary>
	///     The <c>[GeneratedHiddenInjectedProperty]</c> entry of an <c>[Inject]</c> property other assemblies cannot see,
	///     or <see langword="null" /> for one they can: a private, private protected or internal property, which the
	///     compiler leaves out of another assembly's view of the metadata and a reference assembly leaves out entirely.
	/// </summary>
	private static (string Type, string Property, bool Assignable)? HiddenInjectedPropertyEntry(GeneratorAttributeSyntaxContext context)
		=> context.TargetSymbol is IPropertySymbol
		{
			IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Private or Accessibility.ProtectedAndInternal or Accessibility.Internal,
		} property
			? (MetadataNameOf(property.ContainingType.OriginalDefinition), property.MetadataName,
				property is { DeclaredAccessibility: Accessibility.Internal, SetMethod.DeclaredAccessibility: Accessibility.Internal, })
			: null;

	/// <summary>
	///     The <c>[Inject]</c> properties a container cannot see on a type it constructs: declared in another assembly as
	///     private, private protected or internal, which the compiler leaves out of the container's view of that metadata.
	///     The generator lists them in that assembly as <c>[GeneratedHiddenInjectedProperty]</c> attributes, read once per
	///     assembly. An assembly compiled without that marker reports nothing.
	/// </summary>
	private sealed class HiddenInjectedProperties
	{
		private readonly Compilation _compilation;
		private readonly Dictionary<IAssemblySymbol, ILookup<string, (string Property, bool Assignable)>> _markers = new(SymbolEqualityComparer.Default);

		public HiddenInjectedProperties(Compilation compilation) => _compilation = compilation;

		/// <summary>
		///     The hidden <c>[Inject]</c> properties of <paramref name="type" /> and its base types, each with the assembly
		///     that declares it, which the container's view of the type lacks and no more derived property of the same name
		///     shadows.
		/// </summary>
		public List<(string Property, bool Assignable, IAssemblySymbol Assembly)> Of(INamedTypeSymbol type)
		{
			List<(string, bool, IAssemblySymbol)> hidden = new();
			HashSet<string> seen = new(StringComparer.Ordinal);
			for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
			{
				seen.UnionWith(current.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic).Select(property => property.MetadataName));
				IAssemblySymbol assembly = current.ContainingAssembly;
				if (SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly))
				{
					continue;
				}

				hidden.AddRange(Markers(assembly)[MetadataNameOf(current.OriginalDefinition)]
					.Where(marker => seen.Add(marker.Property))
					.Select(marker => (marker.Property, marker.Assignable, assembly)));
			}

			return hidden;
		}

		private ILookup<string, (string Property, bool Assignable)> Markers(IAssemblySymbol assembly)
		{
			if (!_markers.TryGetValue(assembly, out ILookup<string, (string Property, bool Assignable)>? markers))
			{
				markers = assembly.GetAttributes()
					.Where(attribute => attribute.AttributeClass is { Name: HiddenInjectedPropertyAttributeName, ContainingNamespace.Name: AttributeNamespace, }
					                    && attribute.ConstructorArguments.Length == 3
					                    && attribute.ConstructorArguments[0].Value is string
					                    && attribute.ConstructorArguments[1].Value is string
					                    && attribute.ConstructorArguments[2].Value is bool)
					.ToLookup(
						attribute => (string)attribute.ConstructorArguments[0].Value!,
						attribute => ((string)attribute.ConstructorArguments[1].Value!, (bool)attribute.ConstructorArguments[2].Value!),
						StringComparer.Ordinal);
				_markers.Add(assembly, markers);
			}

			return markers;
		}
	}

	/// <summary>
	///     Reports each hidden <c>[Inject]</c> property of an implementation the container constructs, which it would
	///     otherwise leave unset: AWT244 for one a container in its own assembly could assign, AWT136, as a container
	///     there reports it, for one no container can.
	/// </summary>
	private static void ReportHiddenInjectedProperties(ImplInfo info, BuildContext context)
	{
		foreach ((string property, bool assignable, IAssemblySymbol assembly) in context.HiddenInjectedProperties.Of(info.Symbol))
		{
			context.Diagnostics.Add(assignable
				? HiddenInjectedPropertyDiagnostic(context.ContainerSymbol, info, property, assembly)
				: new DiagnosticInfo(Diagnostics.InjectedPropertyNotSettable, info.Location, new EquatableArray<string>([property, DisplayInstance(info.ImplementationType),])));
		}
	}

	private static DiagnosticInfo HiddenInjectedPropertyDiagnostic(INamedTypeSymbol container, ImplInfo info, string property, IAssemblySymbol assembly)
		=> new(
			Diagnostics.HiddenInjectedProperty,
			info.Location,
			new EquatableArray<string>([
				Display(container.ToDisplayString(FullyQualified)),
				DisplayInstance(info.ImplementationType),
				property,
				assembly.Name,
			]));
}
