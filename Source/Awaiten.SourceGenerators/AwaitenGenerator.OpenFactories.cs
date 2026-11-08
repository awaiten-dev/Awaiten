using System.Globalization;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     One open generic <c>typeof</c> registration, decorator or composite an owner (a module or a base container)
	///     declares: the attribute, the open definition it constructs (<c>Repository&lt;T&gt;</c>), the open service
	///     constructed with the definition's own type parameters (<c>IRepository&lt;T&gt;</c>) and the name of the generic
	///     factory the owner generates for it.
	/// </summary>
	private sealed record OpenContribution(AttributeData Attribute, string AttributeName, INamedTypeSymbol Definition, INamedTypeSymbol Service, string FactoryName, Location? Location)
	{
		/// <summary>The name of the constant that records why the owner generates no factory for it.</summary>
		public string SkippedName => OpenSkippedName(FactoryName);
	}

	/// <summary>
	///     The name of the constant that records why an owner generates no factory for an open contribution, derived from
	///     the factory's name (<c>Registration_Repository_…</c> to <c>RegistrationSkipped_Repository_…</c>).
	/// </summary>
	private static string OpenSkippedName(string factoryName)
		=> factoryName.Insert(factoryName.IndexOf('_'), "Skipped");

	/// <summary>
	///     The name of the generic factory of an open contribution: the registration's for a lifetime attribute, else the
	///     decorator's or composite's for the service constructed with the definition's type parameters.
	/// </summary>
	private static string OpenFactoryName(string attributeName, INamedTypeSymbol definition, INamedTypeSymbol service)
		=> attributeName is "DecorateAttribute" or "CompositeAttribute"
			? ContributionFactoryName(attributeName, definition, service)
			: RegistrationFactoryName(definition);

	/// <summary>
	///     The open generic contributions of <paramref name="owner" /> a generated factory could build: well-formed open
	///     <c>typeof</c> lifetime registrations that construct their implementation, decorators and composites. The
	///     malformed ones are left to the container, which reports them.
	/// </summary>
	private static IEnumerable<OpenContribution> OpenContributions(INamedTypeSymbol owner)
	{
		foreach (AttributeData attribute in owner.GetAttributes())
		{
			if (attribute.AttributeClass is not { IsGenericType: false, } attributeClass
			    || attributeClass.ContainingNamespace?.ToDisplayString() != AttributeNamespace
			    || attributeClass.Name is not ("SingletonAttribute" or "TransientAttribute" or "ScopedAttribute" or "DecorateAttribute" or "CompositeAttribute")
			    || (attributeClass.Name is not ("DecorateAttribute" or "CompositeAttribute") && ReadProduction(attribute).Production != ProductionKind.Constructor)
			    || OpenContributionPair(attribute) is not ({ } definition, { } service))
			{
				continue;
			}

			yield return new OpenContribution(
				attribute, attributeClass.Name, definition, service, OpenFactoryName(attributeClass.Name, definition, service),
				attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation());
		}
	}

	/// <summary>
	///     The open definition (<c>Repository&lt;T&gt;</c>) and the service constructed with its type parameters
	///     (<c>IRepository&lt;T&gt;</c>) of a well-formed open <c>typeof</c> pair, or <see langword="null" />.
	/// </summary>
	private static (INamedTypeSymbol Definition, INamedTypeSymbol Service)? OpenContributionPair(AttributeData attribute)
	{
		if (attribute.ConstructorArguments.Length != 2
		    || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol { IsUnboundGenericType: true, } mapped
		    || attribute.ConstructorArguments[1].Value is not INamedTypeSymbol { IsUnboundGenericType: true, } service
		    || TryReadOpenGenericPair(mapped, service, null, new List<DiagnosticInfo>()) is not { } pair)
		{
			return null;
		}

		INamedTypeSymbol definition = pair.Mapped.OriginalDefinition;
		return (definition, pair.Service.OriginalDefinition.Construct(definition.TypeParameters.Cast<ITypeSymbol>().ToArray()));
	}

	/// <summary>
	///     How the container builds the closings of an open <c>[Decorate]</c> or <c>[Composite]</c> of
	///     <paramref name="module" /> (see <see cref="ResolveOpenVia" />), reporting AWT226 for one private to an extended
	///     base that it cannot build. A container's own, a closed one and a malformed one need no decision.
	/// </summary>
	private static OpenViaOutcome ResolveOpenContributionVia(AttributeData attribute, INamedTypeSymbol attributeClass, INamedTypeSymbol? module, ContributionContext context, Location? importLocation, bool inherited)
	{
		if (module is null || attributeClass.IsGenericType || OpenContributionPair(attribute) is not ({ } definition, { } service))
		{
			return default;
		}

		bool composite = attributeClass.Name == "CompositeAttribute";
		OpenViaOutcome outcome = ResolveOpenVia(new OpenViaRequest(
			module, definition, service, OpenFactoryName(attributeClass.Name, definition, service), composite ? "the composite" : "the decorator", context.Location, importLocation,
			context.Compilation, context.Modules, context.Inheritance, inherited, context.Diagnostics));
		if (outcome.BaseFault is { } fault)
		{
			context.Inheritance!.ReportPrivate($"The {(composite ? "composite" : "decorator")} '{Display(definition.ToDisplayString(FullyQualified))}'", fault, context.Diagnostics);
			return outcome with { Dropped = true, };
		}

		return outcome;
	}

	/// <summary>
	///     The generic factories a module generates for its open contributions whose type another assembly cannot
	///     construct (see <see cref="SelfCompilesRegistration" />, asked of the open definition), and a skip record with
	///     the reason for each it cannot generate, which a consumer that cannot construct the type reports (AWT231). The
	///     module's own build reports nothing for them: before modules generated these factories the module built and
	///     a consumer reported AWT230, so a library whose containers all live in its own assembly keeps building.
	/// </summary>
	private static (List<RegistrationFactory> Factories, List<SkippedRegistration> Skipped) CollectModuleOpenFactories(INamedTypeSymbol module, Compilation compilation)
	{
		List<RegistrationFactory> factories = new();
		List<SkippedRegistration> skipped = new();
		foreach (OpenContribution contribution in OpenContributions(module))
		{
			if (!SelfCompilesRegistration(module, contribution.Definition, contribution.Service, compilation)
			    || factories.Any(factory => factory.FactoryName == contribution.FactoryName)
			    || skipped.Any(entry => entry.MarkerName == contribution.SkippedName))
			{
				continue;
			}

			ModuleScanContext context = new(module, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal));
			OpenFactory built = BuildOpenFactory(contribution, ServicesOf(contribution, module), context, module: true);
			if (built.Factory is { IsInternal: true, } && !IsHiddenInModule(contribution.Definition, module, compilation))
			{
				// Every assembly that could call an internal factory constructs the type itself.
				built = new OpenFactory(null, $"its factory would have to return '{Display(built.Factory.ReturnType)}', which is not accessible outside the module's assembly");
			}

			if (built.Factory is { } factory)
			{
				factories.Add(factory);
			}
			else
			{
				skipped.Add(new SkippedRegistration(contribution.SkippedName, built.Reason!, Open: true));
			}
		}

		return (factories, skipped);
	}

	/// <summary>
	///     Records an <c>internal</c> generic factory for each open contribution of a container without <c>Extends</c> whose
	///     type is private to it, or to a class containing it, so a container that extends it builds every closing through
	///     it (see <see cref="RecordBaseFactory" /> for the closed ones). One it cannot build is recorded as a fault, which
	///     an extending container reports as AWT226.
	/// </summary>
	private static void RecordBaseOpenFactories(INamedTypeSymbol container, Compilation compilation, BaseFactories factories)
	{
		foreach (OpenContribution contribution in OpenContributions(container))
		{
			if (!IsHiddenInModule(contribution.Definition, container, compilation)
			    || factories.Factories.Any(factory => factory.FactoryName == contribution.FactoryName)
			    || factories.Faults.ContainsKey(contribution.FactoryName))
			{
				continue;
			}

			ModuleScanContext context = new(container, compilation, new List<DiagnosticInfo>(), new HashSet<string>(StringComparer.Ordinal), Internal: true);
			OpenFactory built = BuildOpenFactory(contribution, ServicesOf(contribution, container), context, module: false);
			if (built.Factory is { } factory)
			{
				factories.Factories.Add(factory with { IsInternal = true, });
			}
			else
			{
				factories.Faults[contribution.FactoryName] = new FactoryFault(built.Reason!, $"make '{Display(contribution.Definition.ToDisplayString(FullyQualified))}' internal");
			}
		}
	}

	/// <summary>
	///     The open services the factory of <paramref name="contribution" /> serves: every service <paramref name="owner" />
	///     registers its open definition under, since the factory builds the one instance a container shares between them,
	///     or the one service of a decorator or composite.
	/// </summary>
	private static List<INamedTypeSymbol> ServicesOf(OpenContribution contribution, INamedTypeSymbol owner)
		=> contribution.AttributeName is "DecorateAttribute" or "CompositeAttribute"
			? [contribution.Service,]
			: OpenContributions(owner)
				.Where(other => other.FactoryName == contribution.FactoryName)
				.Select(other => other.Service)
				.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
				.ToList();

	/// <summary>The generic factory of an open contribution, or the reason the owner cannot generate one.</summary>
	private readonly record struct OpenFactory(RegistrationFactory? Factory, string? Reason);

	/// <summary>
	///     Builds the generic factory of an open contribution: a method with the definition's type parameters and their
	///     constraints, closed by each consumer per use, that mirrors the constructor and injected properties like the
	///     factory of a closed registration and returns the definition when a consumer can name it, else the service,
	///     both of which close positionally with the consumer's type arguments. A factory of a module that could return
	///     only an internal type is internal. What it cannot express gets no factory but a reason: a lifecycle hook,
	///     whose wrapper would have to bind the hook's type arguments without a closing, a constraint or parameter type a
	///     caller cannot name, a type parameter named like a parameter, or, for a <paramref name="module" />, a convention
	///     of the module a consumer cannot call.
	/// </summary>
	private static OpenFactory BuildOpenFactory(OpenContribution contribution, List<INamedTypeSymbol> services, ModuleScanContext context, bool module)
	{
		INamedTypeSymbol definition = contribution.Definition;
		string typeName = definition.ToDisplayString(FullyQualified);
		if ((NamedArgument(contribution.Attribute, "OnActivated") ?? NamedArgument(contribution.Attribute, "OnRelease")) is { } hook)
		{
			return new OpenFactory(null, $"it names the lifecycle hook '{hook}', which a generated factory of an open generic registration does not run");
		}

		for (INamedTypeSymbol? containing = definition.ContainingType; containing is not null; containing = containing.ContainingType)
		{
			if (containing.IsGenericType)
			{
				return new OpenFactory(null, $"it is nested in the generic type '{Display(containing.ToDisplayString(FullyQualified))}', whose type parameters a closing of the service does not supply");
			}
		}

		// One instance serves every open service of the definition, so the factory returns a type each of them converts from.
		INamedTypeSymbol? returnType = Fits(definition, context)
			? definition
			: services.FirstOrDefault(candidate => Fits(candidate, context with { Internal = true, }) && services.All(service => context.Compilation.HasImplicitConversion(candidate, service)));
		if (returnType is null)
		{
			string owner = Display(context.Module.ToDisplayString(FullyQualified));
			return new OpenFactory(null, services.Count > 1
				? $"it is registered under several services ({string.Join(", ", services.Select(service => Display(service.ToDisplayString(FullyQualified))))}), none of which covers the others and is accessible outside '{owner}'"
				: $"its service '{Display(contribution.Service.ToDisplayString(FullyQualified))}' is private to '{owner}'");
		}

		ModuleScanContext signature = context with { Internal = context.Internal || !FitsFactorySignature(returnType, context), };

		foreach (ITypeParameterSymbol parameter in definition.TypeParameters)
		{
			foreach (ITypeSymbol constraint in parameter.ConstraintTypes)
			{
				if (!FitsFactorySignature(constraint, signature) || TypeHiddenInModule(constraint, context.Module, context.Compilation) is not null)
				{
					return new OpenFactory(null, $"its type parameter '{parameter.Name}' is constrained to '{Display(constraint.ToDisplayString(FullyQualified))}', which is not accessible outside the module's assembly");
				}
			}
		}

		SelfCompiledSubject subject = RegistrationSubject(typeName);
		List<DiagnosticInfo> rejections = new();
		MirroredMembers members = default;
		if (module && ModuleAsyncInitializerFault(definition, signature, subject, contribution.Location) is { } initializerFault)
		{
			rejections.Add(initializerFault);
		}

		if (rejections.Count > 0
		    || !TryMirrorFactoryParameters(definition, signature with { Diagnostics = rejections, }, contribution.Location, subject, out EquatableArray<FactoryParameter> parameters, mirrorProperties: true)
		    || !TryMirrorInjectedProperties(definition, signature with { Diagnostics = rejections, }, contribution.Location, subject, ref parameters, out members))
		{
			return new OpenFactory(null, rejections.Count > 0 ? OpenSkipReason(rejections[0]) : $"'{Display(typeName)}' cannot be built through a generated factory");
		}

		if (!module && MirrorBaseInjectProperties(definition, signature, ref parameters, ref members) is { } entryReason)
		{
			return new OpenFactory(null, entryReason);
		}

		HashSet<string> typeParameters = new(definition.TypeParameters.Select(parameter => parameter.Name), StringComparer.Ordinal);
		if (parameters.AsArray().Concat(members.Deferred.AsArray()).FirstOrDefault(parameter => typeParameters.Contains(parameter.Name)) is { Name: { } clash, })
		{
			return new OpenFactory(null, $"its constructor parameter or injected property '{clash}' has the name of a type parameter");
		}

		return new OpenFactory(
			new RegistrationFactory(
				contribution.FactoryName,
				returnType.ToDisplayString(FullyQualified),
				typeName,
				parameters,
				default,
				members.Required,
				OptionalMembers: members.Optional,
				DeferredMembers: members.Deferred,
				IsInternal: signature.Internal,
				InitOnlyMembers: members.InitOnly,
				OptionalDeferredMembers: members.OptionalDeferred,
				TypeParameters: $"<{string.Join(", ", definition.TypeParameters.Select(parameter => EscapedName(parameter.Name)))}>",
				Constraints: TypeParameterConstraints(definition)),
			null);

		bool Fits(ITypeSymbol type, ModuleScanContext scope)
			=> FitsFactorySignature(type, scope) && TypeHiddenInModule(type, scope.Module, scope.Compilation) is null;
	}

	/// <summary>
	///     Appends a parameter per <c>[InjectProperty(typeof(Definition&lt;&gt;), name)]</c> entry of the base to the
	///     mirrored <paramref name="parameters" />, after the required <c>[Inject]</c> ones, since an extending container
	///     fills the property through the factory: it passes the value by name in the base's assembly and resolves the
	///     parameter in another. Returns why it cannot instead, as for a closed type (see <see cref="BaseFactoryFault" />):
	///     an optional or deferred entry, a property the factory cannot set or that has the name of another parameter, or
	///     an entry for one closing, which one generic factory for every closing cannot fill.
	/// </summary>
	private static string? MirrorBaseInjectProperties(INamedTypeSymbol definition, ModuleScanContext context, ref EquatableArray<FactoryParameter> parameters, ref MirroredMembers members)
	{
		foreach (AttributeData attribute in context.Module.GetAttributes())
		{
			if (TryReadInjectProperty(attribute, out ITypeSymbol? target, out _, out _)
			    && target is INamedTypeSymbol { IsUnboundGenericType: false, } closing
			    && SymbolEqualityComparer.Default.Equals(closing.OriginalDefinition, definition))
			{
				return $"the [InjectProperty] entry for its closing '{Display(closing.ToDisplayString(FullyQualified))}' would need a factory of that closing";
			}
		}

		if (!CollectInjectProperties(context.Module, context.Compilation, new List<DiagnosticInfo>()).TryGetValue(TypeIdentity(definition, context.Compilation), out List<InjectPropertyEntry>? entries))
		{
			return null;
		}

		List<FactoryParameter> mirrored = new(parameters.AsArray());
		List<string> required = new(members.Required.AsArray());
		foreach (InjectPropertyEntry entry in entries)
		{
			if (entry.Optional || entry.Deferred)
			{
				return $"its property '{entry.PropertyName}' is injected {(entry.Optional ? "only when its service is registered" : "after construction")}";
			}

			if (mirrored.Concat(members.Deferred.AsArray()).Any(parameter => parameter.Name == entry.PropertyName))
			{
				return $"its injected property '{entry.PropertyName}' has the name of a constructor parameter or of another injected property";
			}

			if (ResolveNamedInstanceMember(definition, entry.PropertyName) is not IPropertySymbol { SetMethod: { } setter, } property
			    || !context.Compilation.IsSymbolAccessibleWithin(setter, context.Module)
			    || !FitsFactorySignature(property.Type, context)
			    || TypeHiddenInModule(property.Type, context.Module, context.Compilation) is not null)
			{
				return $"its injected property '{entry.PropertyName}' cannot be set by a generated factory";
			}

			mirrored.Insert(mirrored.Count - members.Optional.Count, new FactoryParameter(property.Type.ToDisplayString(FullyQualifiedWithNullability), entry.PropertyName, KeyAttribute(entry.Key)));
			required.Add(entry.PropertyName);
		}

		parameters = new EquatableArray<FactoryParameter>(mirrored.ToArray());
		members = members with { Required = new EquatableArray<string>(required.ToArray()), };
		return null;
	}

	/// <summary>The reason recorded for an open contribution whose factory would fail with <paramref name="rejection" />.</summary>
	private static string OpenSkipReason(DiagnosticInfo rejection)
		=> SkipReason(rejection, [])
		   ?? $"its generated factory would fail with {rejection.Descriptor.Id}: {rejection.ToDiagnostic().GetMessage(CultureInfo.InvariantCulture)}";

	/// <summary><paramref name="name" /> as an identifier, prefixed with <c>@</c> when it is a keyword.</summary>
	private static string EscapedName(string name)
		=> SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

	/// <summary>
	///     The constraint clauses of the type parameters of <paramref name="definition" />, fully qualified, for a generic
	///     factory that takes them over: the primary constraint (<c>class</c>, <c>class?</c>, <c>struct</c>,
	///     <c>unmanaged</c> or <c>notnull</c>), the constraint types with their nullability and <c>new()</c>.
	/// </summary>
	private static string TypeParameterConstraints(INamedTypeSymbol definition)
	{
		List<string> clauses = new();
		foreach (ITypeParameterSymbol parameter in definition.TypeParameters)
		{
			List<string> constraints = new();
			if (parameter.HasUnmanagedTypeConstraint)
			{
				constraints.Add("unmanaged");
			}
			else if (parameter.HasValueTypeConstraint)
			{
				constraints.Add("struct");
			}
			else if (parameter.HasReferenceTypeConstraint)
			{
				constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
			}
			else if (parameter.HasNotNullConstraint)
			{
				constraints.Add("notnull");
			}

			constraints.AddRange(parameter.ConstraintTypes.Select(constraint => constraint.ToDisplayString(FullyQualifiedWithNullability)));
			if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint)
			{
				constraints.Add("new()");
			}

			if (constraints.Count > 0)
			{
				clauses.Add($" where {EscapedName(parameter.Name)} : {string.Join(", ", constraints)}");
			}
		}

		return string.Concat(clauses);
	}

	/// <summary>
	///     The generic factory named <paramref name="name" /> with <paramref name="arity" /> type parameters that the
	///     container can see on <paramref name="owner" /> in another assembly, or <see langword="null" />.
	/// </summary>
	private static IMethodSymbol? GeneratedOpenFactory(INamedTypeSymbol owner, string name, int arity, Compilation compilation)
		=> InternalsMembers(owner, name)
			.OfType<IMethodSymbol>()
			.FirstOrDefault(method => method is { IsStatic: true, ReturnType: INamedTypeSymbol, } && method.Arity == arity && compilation.IsSymbolAccessibleWithin(method, compilation.Assembly));

	/// <summary>
	///     What a container needs to decide how it builds the closings of an open contribution of <see cref="Owner" />: the
	///     open <see cref="Definition" />, the name of its generic factory, how a diagnostic names it (<see cref="Kind" />,
	///     as AWT230 does), where to report, and whether the owner is inherited through <c>Extends</c>.
	/// </summary>
	private readonly record struct OpenViaRequest(
		INamedTypeSymbol Owner,
		INamedTypeSymbol Definition,
		INamedTypeSymbol Service,
		string FactoryName,
		string Kind,
		Location? Location,
		Location? ImportLocation,
		Compilation Compilation,
		SameCompilationModules Modules,
		Inheritance? Inheritance,
		bool Inherited,
		List<DiagnosticInfo> Diagnostics);

	/// <summary>
	///     How a container builds the closings of an open contribution: through <see cref="Via" />, the owner's generic
	///     factory, or itself when both are <see langword="null" />. <see cref="BaseFault" /> explains why an extending
	///     container cannot build a contribution private to its base, which the caller reports as AWT226. <see cref="Dropped" />
	///     marks a contribution left out after reporting AWT230 or AWT231.
	/// </summary>
	private readonly record struct OpenViaOutcome(OpenOwnerFactory? Via, FactoryFault? BaseFault, bool Dropped);

	/// <summary>
	///     Decides how the container builds the closings of an open contribution of a module or base container. A type it
	///     can construct it builds itself, unless a public type has an <c>[Inject]</c> property this container does not see,
	///     whose factory the owner generated. A type private to an owner in the container's own assembly is built through
	///     the generic factory the owner generates in the same run, by its deterministic name. A type of another assembly
	///     the container cannot construct is built through the generic factory read from the owner's metadata. Without
	///     one, a module's contribution is reported (AWT231 with the reason the module recorded, else AWT230) and left out,
	///     a base's private one carries the fault for AWT226, and one of a module the base imports is left to the
	///     inheritance, which reports it as inaccessible.
	/// </summary>
	private static OpenViaOutcome ResolveOpenVia(OpenViaRequest request)
	{
		INamedTypeSymbol definition = request.Definition;
		INamedTypeSymbol owner = request.Owner;
		Compilation compilation = request.Compilation;
		if (definition is not { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, })
		{
			return default;
		}

		if (request.Inheritance is { } inheritance && SymbolEqualityComparer.Default.Equals(owner, inheritance.Base))
		{
			if (!inheritance.HiddenInBase(definition))
			{
				return default;
			}

			OpenOwnerFactory? fromBase = inheritance.ThroughBaseOpenFactory(request.FactoryName, definition, out FactoryFault fault);
			return fromBase is null ? new OpenViaOutcome(null, fault, false) : new OpenViaOutcome(fromBase, null, false);
		}

		if (SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly))
		{
			return IsHiddenInModule(definition, owner, compilation)
			       && HasAwaitenAttribute(owner.GetAttributes(), "ModuleAttribute")
			       && request.Modules.OpenFactory(owner, request.FactoryName) is { } generated
				? new OpenViaOutcome(new OpenOwnerFactory(owner, request.FactoryName, null, generated), null, false)
				: default;
		}

		IMethodSymbol? method = GeneratedOpenFactory(owner, request.FactoryName, definition.Arity, compilation) is { } generic
		                        && compilation.HasImplicitConversion(generic.Construct(definition.TypeParameters.Cast<ITypeSymbol>().ToArray()).ReturnType, request.Service)
			? generic
			: null;
		bool hiddenMembers = !owner.ContainingAssembly.GivesAccessTo(compilation.Assembly)
		                     && (method is not null || !InternalsMembers(owner, OpenSkippedName(request.FactoryName)).IsEmpty);
		if (CanConstruct(definition, compilation) && !hiddenMembers)
		{
			return default;
		}

		if (method is not null)
		{
			return new OpenViaOutcome(new OpenOwnerFactory(owner, request.FactoryName, method, null), null, false);
		}

		if (request.Inherited)
		{
			return default;
		}

		string type = Display(definition.ToDisplayString(FullyQualified));
		DiagnosticInfo diagnostic = SkippedOpenReason(owner, request.FactoryName) is { } reason
			? new DiagnosticInfo(
				Diagnostics.ModuleRegistrationNotSelfCompiled,
				LocationInfo.From(request.ImportLocation ?? request.Location),
				new EquatableArray<string>([Display(owner.ToDisplayString(FullyQualified)), type, "factory", reason, $"make '{type}', its constructor and the constructor's parameter types public",]))
			: new DiagnosticInfo(
				Diagnostics.ModuleContributionNotSelfCompiled,
				LocationInfo.From(request.Location),
				new EquatableArray<string>([Display(owner.ToDisplayString(FullyQualified)), request.Kind, type,]));
		if (!request.Diagnostics.Contains(diagnostic))
		{
			request.Diagnostics.Add(diagnostic);
		}

		return new OpenViaOutcome(null, null, true);
	}

	/// <summary>
	///     Lets a closed registration and the closing of an open registration of the same type, which the container builds
	///     through two different generated factories of one module, share the one instance coalescing makes of them, as
	///     a container that constructs the type does: every registration of the type takes the factory whose return type
	///     converts to all of their services. When none does, the one instance cannot be held as a type that serves them
	///     all, so the container reports why (AWT231, or AWT216 for a type private to a module of its own assembly), keeps
	///     the first registration and leaves the others out.
	/// </summary>
	private static void ShareOwnerFactoryInstances(List<RawRegistration> raw, Compilation compilation, HashSet<string> constraintRejected, List<DiagnosticInfo> diagnostics)
	{
		HashSet<int> dropped = new();
		foreach (List<int> group in Enumerable.Range(0, raw.Count)
			         .Where(index => raw[index] is { Origin: not null, ServiceSymbol: not null, OnActivated: null, OnRelease: null, } && OwnerFactoryOf(raw[index]) is not null)
			         .GroupBy(index => raw[index].ImplementationType)
			         .Select(indices => indices.ToList()))
		{
			List<RawRegistration> registrations = group.Select(index => raw[index]).ToList();
			if (!registrations.Any(registration => registration.IsSynthesized)
			    || registrations.Select(OwnerFactoryOf).Distinct(StringComparer.Ordinal).Count() < 2
			    || registrations.Select(registration => registration.Origin).Distinct<INamedTypeSymbol?>(SymbolEqualityComparer.Default).Count() > 1)
			{
				continue;
			}

			if (registrations.FirstOrDefault(candidate => OwnerFactoryProduct(candidate) is { } product
			                                              && registrations.All(registration => compilation.HasImplicitConversion(product, registration.ServiceSymbol))) is { } winner)
			{
				foreach (int index in group)
				{
					raw[index] = raw[index] with
					{
						Implementation = winner.Implementation,
						Production = winner.Production,
						ProductionMember = winner.ProductionMember,
						GenericFactory = winner.GenericFactory,
						Traits = winner.Traits,
						TraitsExact = winner.TraitsExact,
						ConstructVia = winner.ConstructVia,
						ConstructViaType = winner.ConstructViaType,
						Constructor = winner.Constructor,
					};
				}

				continue;
			}

			RawRegistration first = registrations[0];
			string type = DisplayInstance(first.ImplementationType);
			string services = string.Join(", ", registrations.Select(registration => Display(registration.ServiceType)).Distinct(StringComparer.Ordinal));
			diagnostics.Add(first.ConstructVia is null
				? new DiagnosticInfo(
					Diagnostics.ModuleRegistrationNotSelfCompiled,
					LocationInfo.From(first.Location),
					new EquatableArray<string>([
						Display(first.Origin!.ToDisplayString(FullyQualified)), type, "factory",
						$"it is registered closed and as an open generic under services ({services}) that no single one of its generated factories covers",
						"register it under services one of which covers the others",
					]))
				: new DiagnosticInfo(
					Diagnostics.RegisteredTypeLessAccessible,
					LocationInfo.From(first.Location),
					new EquatableArray<string>([
						type, type,
						$"no accessible type it converts to covers all of its services ({services})",
						"register it under services one of which covers the others, or make it internal",
					])));
			foreach (int index in group.Skip(1))
			{
				dropped.Add(index);
				constraintRejected.Add(raw[index].ServiceType);
			}
		}

		for (int index = raw.Count - 1; index >= 0; index--)
		{
			if (dropped.Contains(index))
			{
				raw.RemoveAt(index);
			}
		}

		static string? OwnerFactoryOf(RawRegistration registration)
			=> registration.ConstructVia
			   ?? (registration.Production == ProductionKind.Factory && registration.ProductionMember?.StartsWith(InternalsClass + ".", StringComparison.Ordinal) == true
				   ? registration.ProductionMember
				   : null);

		static INamedTypeSymbol? OwnerFactoryProduct(RawRegistration registration)
			=> registration.ConstructVia is null
				? registration.Implementation
				: new[] { registration.Implementation, }.Concat(registration.Implementation.AllInterfaces).Concat(BaseTypes(registration.Implementation))
					.FirstOrDefault(candidate => candidate.ToDisplayString(FullyQualified) == registration.ConstructViaType);
	}

	/// <summary>
	///     Why the owner of another assembly generated no factory for an open contribution, or <see langword="null" />: the
	///     reason it recorded, or why it can carry no generated member at all (see <see cref="ModuleShapeReason" />).
	/// </summary>
	private static string? SkippedOpenReason(INamedTypeSymbol owner, string factoryName)
		=> InternalsMembers(owner, OpenSkippedName(factoryName))
			   .OfType<IFieldSymbol>()
			   .Select(field => field.ConstantValue as string)
			   .FirstOrDefault(reason => reason is not null)
		   ?? ModuleShapeReason(owner);
}

/// <summary>
///     The generic factory of a module or base container (<see cref="Owner" />) that builds an open generic registration,
///     decorator or composite the container cannot construct, before a closing: read from metadata as the generic
///     <see cref="Method" />, or, in the owner's own assembly, the <see cref="Generated" /> factory the owner emits in the
///     same run, called through the constructor it mirrors.
/// </summary>
/// <remarks>An intermediate type carrying live Roslyn symbols; see <see cref="RawRegistration" />.</remarks>
internal sealed record OpenOwnerFactory(INamedTypeSymbol Owner, string Name, IMethodSymbol? Method, RegistrationFactory? Generated)
{
	/// <summary>
	///     The call of this factory closed with <paramref name="typeArguments" />, which construct
	///     <paramref name="closedType" /> (the closing of the open definition) and <paramref name="closedService" />, or
	///     <see langword="null" /> when no constructor of <paramref name="closedType" /> matches the generated factory, which
	///     mirrors a constructor of the open definition.
	/// </summary>
	public OwnerFactoryCall? Close(ITypeSymbol[] typeArguments, INamedTypeSymbol closedType, INamedTypeSymbol closedService)
	{
		string name = $"{Name}<{string.Join(", ", typeArguments.Select(argument => argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))}>";
		if (Method is not null)
		{
			IMethodSymbol constructed = Method.Construct(typeArguments);
			return new OwnerFactoryCall(Owner, name, constructed.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), constructed);
		}

		if (AwaitenGenerator.MirroredConstructor(closedType.OriginalDefinition, Generated!) is not { } definitionConstructor)
		{
			return null;
		}

		IMethodSymbol constructor = closedType.InstanceConstructors.First(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, definitionConstructor));

		// The factory returns the definition or one of its services, constructed with the definition's own type parameters.
		INamedTypeSymbol definition = closedType.OriginalDefinition;
		INamedTypeSymbol returned = new[] { definition, }.Concat(definition.AllInterfaces).Concat(AwaitenGenerator.BaseTypes(definition))
			.FirstOrDefault(candidate => candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == Generated!.ReturnType) is { } match
			? match.OriginalDefinition.Construct(typeArguments)
			: closedService;
		return new OwnerFactoryCall(Owner, name, returned.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), constructor);
	}
}
