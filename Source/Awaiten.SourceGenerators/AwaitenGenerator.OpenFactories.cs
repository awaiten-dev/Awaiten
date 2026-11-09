using System.Collections.Immutable;
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
	///     a consumer reported AWT230, so a library whose containers all live in its own assembly keeps building. A
	///     factory whose hook wrapper only a container in the module's assembly may run (see
	///     <see cref="OpenFactory.ForeignReason" />) is internal and kept only for a type private to the module, whose
	///     closings that container builds through it, and carries the skip record too, which a container in another
	///     assembly reports, a friend only for a hook it records a skip of its own for.
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

			if (built is { Factory: { } assemblyOnly, ForeignReason: { } foreign, })
			{
				if (IsHiddenInModule(contribution.Definition, module, compilation))
				{
					factories.Add(assemblyOnly with { IsInternal = true, });
					skipped.AddRange(built.HookSkips!);
				}

				skipped.Add(new SkippedRegistration(contribution.SkippedName, foreign, Open: true));
			}
			else if (built.Factory is { } factory)
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

	/// <summary>
	///     The generic factory of an open contribution, or the reason the owner cannot generate one. A
	///     <see cref="ForeignReason" /> tells why only a container in the owner's assembly may run a hook wrapper of the
	///     factory, and <see cref="HookSkips" /> record that for the hooks a friend cannot judge either.
	/// </summary>
	private readonly record struct OpenFactory(RegistrationFactory? Factory, string? Reason, string? ForeignReason = null, List<SkippedRegistration>? HookSkips = null);

	/// <summary>
	///     Builds the generic factory of an open contribution: a method with the definition's type parameters and their
	///     constraints, closed by each consumer per use, that mirrors the constructor and injected properties like the
	///     factory of a closed registration and returns the definition when a consumer can name it, else the service,
	///     both of which close positionally with the consumer's type arguments, plus a generic wrapper per lifecycle hook
	///     (see <see cref="OpenHookWrappers" />). A factory of a module that could return only an internal type is
	///     internal. What it cannot express gets no factory but a reason: a lifecycle hook it cannot wrap, a constraint or
	///     parameter type a caller cannot name, a type parameter named like a parameter, or, for a
	///     <paramref name="module" />, a convention of the module a consumer cannot call.
	/// </summary>
	private static OpenFactory BuildOpenFactory(OpenContribution contribution, List<INamedTypeSymbol> services, ModuleScanContext context, bool module)
	{
		INamedTypeSymbol definition = contribution.Definition;
		string typeName = definition.ToDisplayString(FullyQualified);
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

		SelfCompiledSubject subject = RegistrationSubject(definition);
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

		string typeParameterList = $"<{string.Join(", ", definition.TypeParameters.Select(parameter => EscapedName(parameter.Name)))}>";
		string constraints = TypeParameterConstraints(definition);
		List<SkippedRegistration> hookSkips = new();
		if (OpenHookWrappers(contribution, subject, signature, typeParameters, typeParameterList, constraints, out string? hookReason, out string? foreignReason, hookSkips) is not { } hooks)
		{
			return new OpenFactory(null, hookReason);
		}

		return new OpenFactory(
			new RegistrationFactory(
				contribution.FactoryName,
				returnType.ToDisplayString(FullyQualified),
				typeName,
				parameters,
				new EquatableArray<RegistrationHook>(hooks.ToArray()),
				members.Required,
				OptionalMembers: members.Optional,
				DeferredMembers: members.Deferred,
				IsInternal: signature.Internal,
				InitOnlyMembers: members.InitOnly,
				OptionalDeferredMembers: members.OptionalDeferred,
				TypeParameters: typeParameterList,
				Constraints: constraints),
			null,
			foreignReason,
			hookSkips);

		bool Fits(ITypeSymbol type, ModuleScanContext scope)
			=> FitsFactorySignature(type, scope) && TypeHiddenInModule(type, scope.Module, scope.Compilation) is null;
	}

	/// <summary>
	///     The generic wrappers of the lifecycle hooks the owner's registrations of the definition name, which a consumer
	///     closes with each closing like the factory: each hook is bound as a container binds it per closing, with the
	///     open service constructed with the definition's own type parameters as the marker, so a generic hook is called
	///     with those type parameters and a parameter after the instance may use them. Returns <see langword="null" /> and
	///     the <paramref name="reason" /> when a hook cannot be wrapped (unusable, ambiguous, a parameter another assembly
	///     cannot name or carrying <c>[Arg]</c>, <c>[FromKey]</c> or <c>[Inject]</c>, constraints the definition's type
	///     parameters do not satisfy, or a parameter named like one of them). A wrapper runs its hook for every closing,
	///     so when another overload accepts some closing too (see <see cref="ClosingOnlyHookOverload" />), for which a
	///     container in the owner's assembly reports AWT190, the <paramref name="foreignReason" /> tells why a container
	///     in another assembly, which does not see that overload when it is not public, may not run the wrapper, and a
	///     private one, which a friend does not see either, is recorded in <paramref name="hookSkips" />.
	/// </summary>
	private static List<RegistrationHook>? OpenHookWrappers(
		OpenContribution contribution,
		SelfCompiledSubject subject,
		ModuleScanContext context,
		HashSet<string> typeParameters,
		string typeParameterList,
		string constraints,
		out string? reason,
		out string? foreignReason,
		List<SkippedRegistration> hookSkips)
	{
		reason = null;
		foreignReason = null;
		List<RegistrationHook> hooks = new();
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (OpenContribution registration in OpenContributions(context.Module).Where(other => other.FactoryName == contribution.FactoryName))
		{
			foreach (bool release in new[] { false, true, })
			{
				string slot = release ? "OnRelease" : "OnActivated";
				if (NamedArgument(registration.Attribute, slot) is not { } hookName || !seen.Add(slot + "\0" + hookName))
				{
					continue;
				}

				List<DiagnosticInfo> rejections = new();
				ResolvedModuleHook? resolved = ResolveModuleHookCore(
					new ModuleHookRequest(registration.Definition, hookName, registration.Location, release, RegistrationHookWrapperName(registration.Definition, release, hookName), subject),
					[registration.Service,],
					context with { Diagnostics = rejections, Hidden = null, });
				string prefix = $"its {slot} hook '{hookName}' cannot be run through a generated wrapper";
				if (resolved is null)
				{
					reason = rejections.Count == 0
						? prefix
						: $"{prefix}: {(rejections[0].Descriptor == Diagnostics.InvalidLifecycleHook ? UnboundGenericHook(registration, hookName, context) : null) ?? rejections[0].ToDiagnostic().GetMessage(CultureInfo.InvariantCulture)}";
					return null;
				}

				if (resolved.Wrapper.Parameters.AsArray().FirstOrDefault(parameter => typeParameters.Contains(parameter.Name)) is { Name: { } clash, })
				{
					reason = $"{prefix}, because its parameter '{clash}' has the name of a type parameter";
					return null;
				}

				IMethodSymbol bound = CollectModuleHookOverloads(registration.Definition, hookName, [registration.Service,], context).Matches[0];
				if (ClosingOnlyHookOverload(registration.Definition, hookName, bound, context) is ({ } overload, { } closings))
				{
					string foreign = $"its {slot} hook '{hookName}' has another overload, taking '{Display(overload.Parameters[0].Type.ToDisplayString(FullyQualified))}', that accepts {closings} too, so a generated wrapper would run the wrong overload where a container in the module's assembly reports AWT190";
					foreignReason ??= foreign;
					if (overload.DeclaredAccessibility == Accessibility.Private)
					{
						hookSkips.Add(new SkippedRegistration(RegistrationHookSkippedName(registration.Definition, release, hookName), foreign, Hook: true));
					}
				}

				hooks.Add(new RegistrationHook(resolved.Wrapper with { TypeParameters = typeParameterList, Constraints = constraints, }, release));
			}
		}

		return hooks;
	}

	/// <summary>
	///     Why a generic overload of the hook does not bind for the open definition of <paramref name="registration" />,
	///     although it may for a closing, or <see langword="null" /> when the hook has no such overload: its constraints
	///     do not hold for the definition's type parameters, or it does not take them in their order.
	/// </summary>
	private static string? UnboundGenericHook(OpenContribution registration, string hookName, ModuleScanContext context)
	{
		INamedTypeSymbol definition = registration.Definition;
		string type = Display(definition.ToDisplayString(FullyQualified));
		ImmutableArray<ITypeSymbol> typeParameters = registration.Service.TypeArguments;
		HashSet<INamedTypeSymbol> shapes = new(new[] { definition, registration.Service.OriginalDefinition, }
			.Concat(definition.AllInterfaces.Select(type => type.OriginalDefinition))
			.Concat(BaseTypes(definition).Select(type => type.OriginalDefinition)), SymbolEqualityComparer.Default);
		foreach (IMethodSymbol method in context.Module.GetMembers(hookName).OfType<IMethodSymbol>())
		{
			if (method is not { MethodKind: MethodKind.Ordinary, IsStatic: true, Parameters.Length: >= 1, } || method.Arity != typeParameters.Length)
			{
				continue;
			}

			if (context.Compilation.HasImplicitConversion(definition, method.Construct(typeParameters.ToArray()).Parameters[0].Type))
			{
				if (!SatisfiesConstraints(method, typeParameters, context.Compilation))
				{
					return $"the constraints of its generic overload do not hold for every closing of '{type}'; constrain the type parameters of '{type}' alike";
				}
			}
			else if (method.Parameters[0].Type is INamedTypeSymbol instance && shapes.Contains(instance.OriginalDefinition))
			{
				return $"its generic overload does not take the type parameters of '{type}' in their order; declare them in the same order on the hook";
			}
		}

		return null;
	}

	/// <summary>
	///     An overload of the hook other than <paramref name="bound" />, the one bound for the open
	///     <paramref name="definition" />, that a container in the module's assembly also finds for some closing of it, with
	///     a description of those closings, or <see langword="null" />: a non-generic overload whose instance parameter a
	///     closing converts to, or a generic one whose constraints keep it from binding for the definition's own type
	///     parameters but not for every closing.
	/// </summary>
	private static (IMethodSymbol Overload, string Closings)? ClosingOnlyHookOverload(INamedTypeSymbol definition, string hookName, IMethodSymbol bound, ModuleScanContext context)
	{
		Compilation compilation = context.Compilation;
		bool wrappedEverywhere = IsHiddenInModule(definition, context.Module, compilation);
		ITypeSymbol[] typeParameters = definition.TypeParameters.Cast<ITypeSymbol>().ToArray();
		foreach (ISymbol member in AccessibleMembers(context.Module, hookName, compilation))
		{
			if (!IsModuleHookCandidate(member, wrappedEverywhere, context, out IMethodSymbol method)
			    || SymbolEqualityComparer.Default.Equals(method, bound.OriginalDefinition))
			{
				continue;
			}

			if (method.Arity > 0)
			{
				if (method.Arity == typeParameters.Length && compilation.HasImplicitConversion(definition, method.Construct(typeParameters).Parameters[0].Type))
				{
					return (method, $"the closings of '{Display(definition.ToDisplayString(FullyQualified))}' its constraints admit");
				}

				continue;
			}

			ITypeSymbol instance = method.Parameters[0].Type;
			foreach (INamedTypeSymbol shape in new[] { definition, }.Concat(BaseTypes(definition)).Concat(definition.AllInterfaces))
			{
				Dictionary<ITypeParameterSymbol, ITypeSymbol> bindings = new(SymbolEqualityComparer.Default);
				if (!Unifies(shape, instance, bindings))
				{
					continue;
				}

				ITypeSymbol[] arguments = definition.TypeParameters.Select(parameter => bindings.TryGetValue(parameter, out ITypeSymbol? argument) ? argument : parameter).ToArray();
				INamedTypeSymbol closing = definition.Construct(arguments);
				if (compilation.HasImplicitConversion(closing, instance) && ConstraintsSatisfied(definition, arguments, compilation))
				{
					return (method, $"'{Display(closing.ToDisplayString(FullyQualified))}'");
				}
			}
		}

		return null;

		// Binds the definition's type parameters in a type it converts to so that it becomes the instance parameter's type,
		// trying a supertype of a variant type argument where the plain one does not fit.
		bool Unifies(ITypeSymbol pattern, ITypeSymbol target, Dictionary<ITypeParameterSymbol, ITypeSymbol> bindings)
		{
			if (pattern is ITypeParameterSymbol parameter && definition.TypeParameters.Contains(parameter, SymbolEqualityComparer.Default))
			{
				if (bindings.TryGetValue(parameter, out ITypeSymbol? known))
				{
					return SymbolEqualityComparer.Default.Equals(known, target);
				}

				bindings[parameter] = target;
				return true;
			}

			if (pattern is IArrayTypeSymbol patternArray && target is IArrayTypeSymbol targetArray)
			{
				return patternArray.Rank == targetArray.Rank && Unifies(patternArray.ElementType, targetArray.ElementType, bindings);
			}

			if (pattern is not INamedTypeSymbol { IsGenericType: true, } named
			    || target is not INamedTypeSymbol targetNamed
			    || !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, targetNamed.OriginalDefinition))
			{
				return SymbolEqualityComparer.Default.Equals(pattern, target);
			}

			for (int index = 0; index < named.TypeArguments.Length; index++)
			{
				ITypeSymbol argument = named.TypeArguments[index];
				ITypeSymbol targetArgument = targetNamed.TypeArguments[index];
				IEnumerable<(ITypeSymbol Pattern, ITypeSymbol Target)> attempts = named.OriginalDefinition.TypeParameters[index].Variance switch
				{
					VarianceKind.Out => new[] { (argument, targetArgument), }.Concat(BaseTypes(argument).Concat(argument.AllInterfaces).Select(supertype => ((ITypeSymbol)supertype, targetArgument))),
					VarianceKind.In => new[] { (argument, targetArgument), }.Concat(BaseTypes(targetArgument).Concat(targetArgument.AllInterfaces).Select(supertype => (argument, (ITypeSymbol)supertype))),
					_ => new[] { (argument, targetArgument), },
				};
				if (!attempts.Any(attempt => TryUnify(attempt.Pattern, attempt.Target, bindings)))
				{
					return false;
				}
			}

			return true;
		}

		bool TryUnify(ITypeSymbol pattern, ITypeSymbol target, Dictionary<ITypeParameterSymbol, ITypeSymbol> bindings)
		{
			Dictionary<ITypeParameterSymbol, ITypeSymbol> attempt = new(bindings, SymbolEqualityComparer.Default);
			if (!Unifies(pattern, target, attempt))
			{
				return false;
			}

			foreach (KeyValuePair<ITypeParameterSymbol, ITypeSymbol> binding in attempt)
			{
				bindings[binding.Key] = binding.Value;
			}

			return true;
		}
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
				RecordRejected(constraintRejected, raw[index].ServiceType, raw[index].Key);
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
