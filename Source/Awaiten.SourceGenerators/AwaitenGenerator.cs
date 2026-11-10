using System.Collections.Immutable;
using System.Text;
using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Awaiten.SourceGenerators;

/// <summary>
///     The Awaiten incremental source generator. Discovers <c>[Container]</c> partial classes,
///     collects their registration attributes, resolves the object graph at compile time and emits
///     the container implementation (resolution, scopes and disposal). Invalid wiring is reported as
///     a diagnostic.
/// </summary>
/// <remarks>
///     Assumptions: a container is a non-generic <c>partial class</c> (nested is allowed if every
///     enclosing type is partial and non-generic); each constructed type has a single accessible
///     constructor (else the one with the most resolvable parameters). Registrations of the same
///     implementation are coalesced into a single shared instance.
/// </remarks>
[Generator]
public sealed partial class AwaitenGenerator : IIncrementalGenerator
{
	private const string ContainerAttributeName = "Awaiten.ContainerAttribute";
	private const string ModuleAttributeName = "Awaiten.ModuleAttribute";
	private const string InjectAttributeName = "Awaiten.InjectAttribute";
	private const string AttributeNamespace = "Awaiten";
	private const string InternalsNamespace = "Awaiten.Internals";

	/// <summary>
	///     The oldest C# version the generated code compiles under: it uses <c>not</c> patterns and static lambdas.
	/// </summary>
	internal const LanguageVersion MinimumLanguageVersion = LanguageVersion.CSharp9;

	/// <summary>
	///     The oldest C# version the generated partial of a module that declares a <c>[Scan]</c> compiles under: it
	///     carries a generic <c>[GeneratedScanRegistration&lt;TService&gt;]</c> per match.
	/// </summary>
	private const LanguageVersion ModuleScanLanguageVersion = LanguageVersion.CSharp11;

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<ContainerModel> models = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				ContainerAttributeName,
				static (node, _) => node is ClassDeclarationSyntax,
				static (ctx, ct) => BuildModel(ctx, ct))
			.Where(static model => model is not null)
			.Select(static (model, _) => model!);

		context.RegisterSourceOutput(models, static (spc, model) =>
		{
			if (!model.SkipsGeneration)
			{
				spc.AddSource(model.HintName, SourceText.From(EmitContainer(model, spc.ReportDiagnostic), Encoding.UTF8));
			}
		});
		ReportDiagnostics(context, models.Select(static (model, _) => model.Diagnostics));

		// A second root: a [Module] that declares a [Scan] self-compiles it, emitting a factory + registration per
		// match into its own partial, and a factory per registration another assembly cannot construct
		// (BuildModuleModel returns null for a plain module, which emits nothing).
		IncrementalValuesProvider<ModuleScanModel> moduleModels = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				ModuleAttributeName,
				static (node, _) => node is ClassDeclarationSyntax,
				static (ctx, ct) => RequireLanguageVersion(BuildModuleModel(ctx, ct), ctx))
			.Where(static model => model is not null)
			.Select(static (model, _) => model!);

		context.RegisterSourceOutput(moduleModels, static (spc, model) =>
		{
			// An expanded module always emits, even with zero factories: the partial then carries just the
			// [GeneratedScanExpansion] marker, which a consuming container needs to tell "the scan matched
			// nothing" from "the scan was never expanded" (AWT154). A module rejected before expansion
			// (AWT152/AWT194/AWT201) emits nothing; its error already fails the build.
			if (model.Expanded)
			{
				spc.AddSource(model.HintName, SourceText.From(Sources.EmitModule(model), Encoding.UTF8));
			}
			else if (model.NotPartialMarker is not null)
			{
				spc.AddSource(model.HintName, SourceText.From(Sources.EmitModuleNotPartialMarker(model), Encoding.UTF8));
			}

			foreach (string marker in model.DecoratorMistakesMarkers)
			{
				spc.AddSource($"Awaiten.{marker}.g.cs", SourceText.From(Sources.EmitModuleDecoratorMistakesMarker(model, marker), Encoding.UTF8));
			}
		});
		// AWT245 is reported by AwaitenAnalyzer from its own module build.
		ReportDiagnostics(context, moduleModels.Select(static (model, _) => new EquatableArray<DiagnosticInfo>(
			model.Diagnostics.AsArray().Where(diagnostic => !ReferenceEquals(diagnostic.Descriptor, Diagnostics.NullableProduct)).ToArray())));

		// A third root: the [Inject] properties another assembly cannot see, which a container there reads to report them (AWT244, AWT136).
		IncrementalValueProvider<ImmutableArray<(string Type, string Property, bool Assignable)>> hiddenInjectedProperties = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				InjectAttributeName,
				static (node, _) => node is PropertyDeclarationSyntax,
				static (ctx, _) => HiddenInjectedPropertyEntry(ctx))
			.Where(static entry => entry is not null)
			.Select(static (entry, _) => entry!.Value)
			.Collect();

		context.RegisterSourceOutput(hiddenInjectedProperties, static (spc, entries) =>
		{
			if (entries.Length > 0)
			{
				spc.AddSource("Awaiten.HiddenInjectedProperties.g.cs", SourceText.From(
					Sources.EmitHiddenInjectedProperties(entries.OrderBy(entry => entry.Type, StringComparer.Ordinal).ThenBy(entry => entry.Property, StringComparer.Ordinal)),
					Encoding.UTF8));
			}
		});
	}

	/// <summary>
	///     Reports <paramref name="diagnostics" /> located in the syntax trees of the current compilation, where the
	///     <c>.editorconfig</c> severities and <c>#pragma warning</c> directives apply. This output reruns whenever the compilation changes,
	///     so it is kept apart from the emitted sources, which stay cached while their model is unchanged.
	/// </summary>
	private static void ReportDiagnostics(IncrementalGeneratorInitializationContext context, IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> diagnostics)
		=> context.RegisterSourceOutput(
			diagnostics.Where(static reported => reported.Count > 0).Combine(context.CompilationProvider),
			static (spc, input) =>
			{
				foreach (DiagnosticInfo diagnostic in input.Left.AsArray())
				{
					spc.ReportDiagnostic(diagnostic.ToDiagnostic(input.Right, spc.CancellationToken));
				}
			});

	/// <summary>
	///     Builds the <see cref="ModuleScanModel" /> for a <c>[Module]</c> that declares a <c>[Scan]</c>, a
	///     self-compiled registration (see <see cref="SelfCompilesRegistration" />), an eager or async-initialized
	///     singleton under an internal type (see <see cref="StartedHiddenSingleton" />), or other generated members, returning
	///     <see langword="null" /> for any other module, which self-compiles nothing. The module must be
	///     non-generic (AWT201) and <c>partial</c>, as must every type enclosing a nested module, to receive the
	///     generated factories and registration attributes (AWT194), unless it declares no <c>[Scan]</c> and its
	///     assembly grants <c>InternalsVisibleTo</c> (see <see cref="FriendsConstructEveryRegistration" />); when it is, its scans and registrations are
	///     expanded into factories in its own build (see <see cref="CollectModuleScanFactories" /> and
	///     <see cref="CollectModuleRegistrationFactories" />). A module that self-compiles nothing still gets a model
	///     when one of its <c>[AsyncInitializer&lt;T&gt;]</c> conventions is unusable (AWT233) or a hook gets no wrapper
	///     (AWT164, AWT190, AWT203, AWT204), which carries only those diagnostics, or when a <c>[Decorate]</c> of it is wrong
	///     for every importer (see <see cref="ReportModuleDecoratorMistakes" />), which also emits the markers of
	///     <see cref="DecoratorMistakesMarkerName" /> and <see cref="MissingInnerMarkerName" />. A public module that is not
	///     <c>partial</c> but names a member only a wrapper would serve (see <see cref="HasMemberNeedingWrapper" />), has
	///     a contribution only a generated factory would build (see <see cref="HasContributionNeedingFactory" />), or a
	///     convention only a wrapper would run (see <see cref="HasConventionNeedingWrapper" />), gets a model that emits
	///     just the marker of <see cref="ModuleNotPartialName" />.
	/// </summary>
	private static ModuleScanModel? BuildModuleModel(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
		=> context.TargetSymbol is INamedTypeSymbol moduleSymbol
			? BuildModuleModel(moduleSymbol, context.TargetNode, context.SemanticModel.Compilation, cancellationToken)
			: null;

	/// <summary>
	///     The <see cref="ModuleScanModel" /> of <paramref name="moduleSymbol" />, declared by <paramref name="declaration" />.
	///     Shared by the generator and <see cref="AwaitenAnalyzer" /> (AWT245).
	/// </summary>
	internal static ModuleScanModel? BuildModuleModel(INamedTypeSymbol moduleSymbol, SyntaxNode declaration, Compilation compilation, CancellationToken cancellationToken)
	{
		// A module that declares nothing a container in another assembly cannot build carries no generated code, and one
		// that declares nothing internal never reads the other modules of its assembly (see MayBuildThroughHandles).

		// The module's [AsyncInitializer<T>] conventions are reported here (AWT233), once, rather than by every
		// container that imports the module.
		List<DiagnosticInfo> diagnostics = new();
		DeclaredAsyncInitializers(moduleSymbol, compilation, diagnostics);
		ReportModuleProductionMembers(moduleSymbol, compilation, diagnostics);
		ReportModuleEagerAsyncSingletons(moduleSymbol, compilation, diagnostics);
		ReportModuleDecoratorPredicates(moduleSymbol, compilation, diagnostics);
		EquatableArray<string> decoratorMistakesMarkers = new(ReportModuleDecoratorMistakes(moduleSymbol, compilation, diagnostics).ToArray());

		if (ReportReservedInternals(moduleSymbol, diagnostics))
		{
			return new ModuleScanModel(null, default, moduleSymbol.Name, string.Empty, false, default, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()), false, default, default, null, DecoratorMistakesMarkers: decoratorMistakesMarkers);
		}

		bool declaresScan = HasAwaitenAttribute(moduleSymbol.GetAttributes(), "ScanAttribute");
		ModuleRegistration? selfCompiled = declaresScan ? null : FirstSelfCompiledRegistration(moduleSymbol, compilation);
		(ModuleRegistration Registration, bool Eager)? started = declaresScan || selfCompiled is not null ? null : StartedHiddenSingleton(moduleSymbol, compilation);

		// Member wrappers and the factories of decorators are generated only into a module that can receive them, so a
		// module that cannot keeps building without them, as it did before they existed.
		bool receives = ReceivesGeneratedMembers(moduleSymbol);
		if (!receives)
		{
			ReportNotPartialPrivateMember(moduleSymbol, compilation, diagnostics);
		}

		List<SkippedRegistration> skipped = new();
		HiddenServices? hidden = receives ? CollectHiddenServices(moduleSymbol, compilation, cancellationToken) : null;
		List<MemberWrapper> wrappers = receives ? CollectModuleMemberWrappers(moduleSymbol, compilation, diagnostics, skipped, hidden) : new();
		if (receives)
		{
			ReportUnwrappedPrivateMembers(moduleSymbol, compilation, wrappers, diagnostics);
		}

		List<RegistrationFactory> contributions = receives ? CollectModuleContributionFactories(moduleSymbol, compilation, hidden, diagnostics, skipped) : new();
		(List<RegistrationFactory> openFactories, List<SkippedRegistration> openSkipped) = receives ? CollectModuleOpenFactories(moduleSymbol, compilation, hidden) : (new(), new());
		contributions.AddRange(openFactories);
		skipped.AddRange(openSkipped);
		List<ConventionWrapper> conventionWrappers = receives ? CollectConventionWrappers(moduleSymbol, compilation, hidden) : new();

		// A container in another assembly that misses a wrapper or a factory cannot see that the module is not partial, so the module records it (AWT108, AWT109, AWT164, AWT231, AWT234).
		bool recordsNotPartial = !receives && moduleSymbol.IsStatic && !HasOpenTypeParameters(moduleSymbol) && IsExternallyAccessible(moduleSymbol)
		                         && (HasMemberNeedingWrapper(moduleSymbol, compilation) || HasContributionNeedingFactory(moduleSymbol, compilation) || HasConventionNeedingWrapper(moduleSymbol, compilation));
		if (!declaresScan && selfCompiled is null && started is null && wrappers.Count == 0 && contributions.Count == 0 && skipped.Count == 0 && conventionWrappers.Count == 0 && !recordsNotPartial
		    && !(hidden is { SeveralModules: true, } && hidden.Demanded.Emitting.Contains(moduleSymbol))
		    && hidden?.Implementations.Any(candidate => candidate.Viable && (candidate.Shared || IsExternallyAccessible(candidate.Implementation))) != true)
		{
			return diagnostics.Count == 0
				? null
				: new ModuleScanModel(null, default, moduleSymbol.Name, string.Empty, false, default, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()), false, default, default, null, DecoratorMistakesMarkers: decoratorMistakesMarkers);
		}

		// Under a grant a registration-only module builds anyway, so the message says why a scan module does not.
		string reason = (selfCompiled, started) switch
		{
			({ } registration, _) when ConstructibleEverywhere(registration.Implementation) => $"registers '{Display(registration.Implementation.ToDisplayString(FullyQualified))}', whose [Inject] property another assembly cannot set,",
			({ } registration, _) => $"registers '{Display(registration.Implementation.ToDisplayString(FullyQualified))}', which another assembly cannot construct,",
			(null, { Eager: true, } singleton) => $"registers the eager internal singleton '{Display(singleton.Registration.Implementation.ToDisplayString(FullyQualified))}', which a container in another assembly builds through a generated handle,",
			(null, { } singleton) => $"registers the async-initialized internal singleton '{Display(singleton.Registration.Implementation.ToDisplayString(FullyQualified))}', which a container in another assembly initializes through a generated handle,",
			_ => GrantsInternalsVisibleTo(compilation.Assembly)
				? "declares a [Scan], whose matches a consumer reads from generated metadata even when the assembly grants InternalsVisibleTo,"
				: "declares a [Scan]",
		};

		// The module must be partial for the generator to add the factories and registration attributes. Every part
		// of a partial type carries the partial modifier, so the declaration bearing the [Module] attribute suffices.
		// A nested module's containing types are re-opened by the generated partial too, so they must all be
		// partial as well, else the emission would surface as a raw CS0260 on the outer type.
		bool isPartial = declaration is ClassDeclarationSyntax classDeclaration
		                 && classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword)
		                 && ContainingTypesArePartial(moduleSymbol);

		List<ModuleFactory> factories = new();
		List<RegistrationFactory> registrations = new();
		List<HiddenHandle> handles = new();
		List<HiddenAlias> aliases = new();
		List<HiddenInitializer> initializers = new();
		bool expanded = false;
		string? notPartialMarker = null;
		if ((selfCompiled is not null || started is not null) && moduleSymbol.IsStatic && (HasOpenTypeParameters(moduleSymbol) || !isPartial) && FriendsConstructEveryRegistration(moduleSymbol, compilation))
		{
			// A registration-only module that cannot receive the factories leaves construction to the assemblies granted
			// InternalsVisibleTo, as before modules generated factories. A consumer sees a generic module from metadata,
			// but not a missing partial modifier, so that is recorded in an internal type, which metadata carries too.
			notPartialMarker = HasOpenTypeParameters(moduleSymbol) ? null : ModuleNotPartialName(moduleSymbol);
		}
		else if (HasOpenTypeParameters(moduleSymbol))
		{
			// AWT201: a generic module (or one nested in a generic type) has no single closed type a consumer could
			// import, and re-opening it as a bare-named partial would emit an unrelated non-generic class instead.
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.GenericModuleScan,
				LocationInfo.From(moduleSymbol.Locations.FirstOrDefault()),
				new EquatableArray<string>([Display(moduleSymbol.ToDisplayString(FullyQualified)), reason,])));
		}
		else if (!moduleSymbol.IsStatic)
		{
			// AWT152, reported here in the module's own build (the import-side check only reaches a module some
			// container in the same solution imports): the generated partial re-opens the module as static, so
			// emitting into a non-static class would surface as a raw partial-modifier compiler error instead.
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.NonStaticModule,
				LocationInfo.From(moduleSymbol.Locations.FirstOrDefault()),
				new EquatableArray<string>([Display(moduleSymbol.ToDisplayString(FullyQualified)),])));
		}
		else if (recordsNotPartial && !declaresScan && selfCompiled is null && started is null)
		{
			notPartialMarker = ModuleNotPartialName(moduleSymbol);
		}
		else if (!isPartial)
		{
			diagnostics.RemoveAll(diagnostic => diagnostic.Descriptor == Diagnostics.NonPartialModuleScan);
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.NonPartialModuleScan,
				LocationInfo.From(moduleSymbol.Locations.FirstOrDefault()),
				new EquatableArray<string>([Display(moduleSymbol.ToDisplayString(FullyQualified)), reason,])));
		}
		else
		{
			(factories, registrations, handles, aliases, initializers) = ExpandModule(moduleSymbol, compilation, hidden, wrappers, skipped, contributions, diagnostics, cancellationToken);
			RecordEmptyCollections(registrations, skipped);
			expanded = true;
		}

		string? moduleNamespace = moduleSymbol.ContainingNamespace is { IsGlobalNamespace: false, } ns
			? ns.ToDisplayString()
			: null;

		List<TypeDeclaration> containingTypes = new();
		for (INamedTypeSymbol? outer = moduleSymbol.ContainingType; outer is not null; outer = outer.ContainingType)
		{
			containingTypes.Insert(0, new TypeDeclaration(KeywordOf(outer), outer.Name));
		}

		string typePath = containingTypes.Count > 0
			? $"{string.Join("+", containingTypes.Select(t => t.Name))}+{moduleSymbol.Name}"
			: moduleSymbol.Name;
		string hintName = moduleNamespace is null
			? $"Awaiten.ModuleScan.{typePath}.g.cs"
			: $"Awaiten.ModuleScan.{moduleNamespace}.{typePath}.g.cs";

		return new ModuleScanModel(
			moduleNamespace,
			new EquatableArray<TypeDeclaration>(containingTypes.ToArray()),
			moduleSymbol.Name,
			hintName,
			expanded,
			new EquatableArray<ModuleFactory>(factories.ToArray()),
			new EquatableArray<DiagnosticInfo>(DistinctModuleDiagnostics(diagnostics).ToArray()),
			declaresScan,
			new EquatableArray<RegistrationFactory>(registrations.ToArray()),
			new EquatableArray<SkippedRegistration>(skipped.ToArray()),
			notPartialMarker,
			new EquatableArray<MemberWrapper>(expanded ? wrappers.ToArray() : []),
			new EquatableArray<HiddenHandle>(handles.ToArray()),
			new EquatableArray<HiddenAlias>(aliases.ToArray()),
			new EquatableArray<HiddenInitializer>(initializers.ToArray()),
			new EquatableArray<ConventionWrapper>(expanded ? conventionWrappers.ToArray() : []),
			new EquatableArray<ClosingHandle>(CompleteClosings(
				GeneratedParameters(factories, registrations, expanded ? wrappers : [], initializers, expanded ? conventionWrappers : [])
					.Select(parameter => parameter.Closing)
					.OfType<ClosingHandle>(),
				moduleSymbol,
				compilation).ToArray()),
			decoratorMistakesMarkers);
	}

	/// <summary>The parameters of every member a module generates, which may take a closing handle (see <see cref="ClosingHandle" />).</summary>
	private static IEnumerable<FactoryParameter> GeneratedParameters(
		List<ModuleFactory> factories,
		List<RegistrationFactory> registrations,
		List<MemberWrapper> wrappers,
		List<HiddenInitializer> initializers,
		List<ConventionWrapper> conventionWrappers)
		=> factories.SelectMany(factory => factory.Parameters.AsArray()
				.Concat(new[] { factory.OnActivated, factory.OnRelease, }.SelectMany(hook => hook?.Parameters.AsArray() ?? []))
				.Concat(factory.Shared?.Initializers.AsArray().SelectMany(initializer => initializer.Parameters.AsArray()) ?? []))
			.Concat(registrations.SelectMany(registration => registration.Parameters.AsArray()
				.Concat(registration.DeferredMembers.AsArray())
				.Concat(registration.Hooks.AsArray().SelectMany(hook => hook.Hook.Parameters.AsArray()))))
			.Concat(wrappers.SelectMany(wrapper => wrapper.Parameters.AsArray()))
			.Concat(initializers.SelectMany(initializer => initializer.Parameters.AsArray()))
			.Concat(conventionWrappers.SelectMany(wrapper => wrapper.Parameters.AsArray()));

	/// <summary>
	///     The distinct <paramref name="diagnostics" /> of a module, keeping only the first report of a parameter type
	///     private to the module (AWT195) at one place: the module's factory of a type and the handle of its service, or
	///     the module's report of a <c>Factory</c> member and the member's handle, reject the same parameter.
	/// </summary>
	private static IEnumerable<DiagnosticInfo> DistinctModuleDiagnostics(List<DiagnosticInfo> diagnostics)
	{
		HashSet<(LocationInfo?, string)> hiddenParameters = new();
		return diagnostics.Distinct().Where(diagnostic => !ReferenceEquals(diagnostic.Descriptor, Diagnostics.ModuleParameterHiddenInModule)
		                                                  || hiddenParameters.Add((diagnostic.Location, diagnostic.MessageArgs.AsArray()[2])));
	}

	/// <summary>
	///     Whether <paramref name="diagnostic" />, an error by default, still fails the build of
	///     <paramref name="compilation" /> once the severity the user sets for its id applies: in an <c>.editorconfig</c> or
	///     global config, or through <c>NoWarn</c> and <c>WarningsAsErrors</c>, and unless a <c>#pragma warning disable</c>
	///     suppresses it where it is reported. A warning fails it under <c>TreatWarningsAsErrors</c>.
	/// </summary>
	/// <remarks>
	///     A <c>[SuppressMessage]</c> is not taken into account: the build passes, but the container keeps only its shell.
	/// </remarks>
	private static bool FailsBuild(DiagnosticInfo diagnostic, Compilation compilation, CancellationToken cancellationToken)
	{
		if (diagnostic.EffectiveSeverity != DiagnosticSeverity.Error)
		{
			return false;
		}

		CompilationOptions options = compilation.Options;
		string id = diagnostic.Descriptor.Id;
		SyntaxTree? tree = diagnostic.Location?.TreeIn(compilation, cancellationToken);
		ReportDiagnostic report = ReportDiagnostic.Default;
		_ = (tree is not null && options.SyntaxTreeOptionsProvider?.TryGetDiagnosticValue(tree, id, cancellationToken, out report) == true)
		    || options.SyntaxTreeOptionsProvider?.TryGetGlobalDiagnosticValue(id, cancellationToken, out report) == true
		    || options.SpecificDiagnosticOptions.TryGetValue(id, out report);
		bool fails = report switch
		{
			ReportDiagnostic.Default or ReportDiagnostic.Error => true,
			ReportDiagnostic.Warn => options.GeneralDiagnosticOption == ReportDiagnostic.Error,
			_ => false,
		};
		return fails && !(tree is not null && DisabledByPragma(tree, id, diagnostic.Location!.TextSpan.Start, cancellationToken));
	}

	/// <summary>
	///     Whether an error that <paramref name="module" />, of the container's own assembly, reports in its own build about a
	///     member producing a type private to it, or about a private member no wrapper serves, still fails the build (see
	///     <see cref="FailsBuild" />).
	/// </summary>
	private static bool ModuleErrorsFailBuild(INamedTypeSymbol module, Compilation compilation, CancellationToken cancellationToken)
	{
		List<DiagnosticInfo> moduleDiagnostics = new();
		ReportModuleProductionMembers(module, compilation, moduleDiagnostics, lintBodies: false);
		ReportPrivateMembersWithoutWrapper(module, compilation, moduleDiagnostics);
		return moduleDiagnostics.Exists(diagnostic => FailsBuild(diagnostic, compilation, cancellationToken));
	}

	/// <summary>
	///     Whether the <c>#pragma warning</c> directives of <paramref name="tree" /> before <paramref name="position" /> disable
	///     <paramref name="id" /> there, as the compiler reads them: the last <c>disable</c> or <c>restore</c> that names the id,
	///     or names no id and so applies to all, decides. Roslyn exposes no API for this state.
	/// </summary>
	private static bool DisabledByPragma(SyntaxTree tree, string id, int position, CancellationToken cancellationToken)
	{
		SyntaxNode root = tree.GetRoot(cancellationToken);
		if (!root.ContainsDirectives)
		{
			return false;
		}

		bool disabled = false;
		foreach (SyntaxTrivia trivia in root.DescendantTrivia())
		{
			if (trivia.Span.End > position)
			{
				break;
			}

			// The compiler ignores a directive it could not parse, such as a #pragma warning without disable or restore.
			if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax { IsActive: true, WarningKeyword.IsMissing: false, DisableOrRestoreKeyword.IsMissing: false, } pragma)
			{
				continue;
			}

			if (pragma.ErrorCodes.Count == 0 || pragma.ErrorCodes.Any(code => code is IdentifierNameSyntax name && name.Identifier.ValueText == id))
			{
				disabled = pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword);
			}
		}

		return disabled;
	}

	/// <summary>
	///     Replaces the model of a module that generates code with one that carries only AWT243 when the project's
	///     language version is older than that code needs, or with one that generates nothing when AWT243 skips a
	///     module of its assembly (see <see cref="SkipsAScanModule" />).
	/// </summary>
	private static ModuleScanModel? RequireLanguageVersion(ModuleScanModel? model, GeneratorAttributeSyntaxContext context)
	{
		if (model is not ({ Expanded: true, } or { NotPartialMarker: not null, }))
		{
			return model;
		}

		LanguageVersion required = model.DeclaresScan ? ModuleScanLanguageVersion : MinimumLanguageVersion;
		if (UnsupportedLanguageVersion(context, required) is { } diagnostic)
		{
			return new ModuleScanModel(null, default, model.TypeName, string.Empty, false, default, new EquatableArray<DiagnosticInfo>([diagnostic,]), false, default, default, null);
		}

		return SkipsAScanModule(context.SemanticModel.Compilation)
			? new ModuleScanModel(null, default, model.TypeName, string.Empty, false, default, model.Diagnostics, false, default, default, null)
			: model;
	}

	/// <summary>
	///     Whether the project compiles below C# 11 and a module of its assembly declares a <c>[Scan]</c>, so AWT243 skips
	///     that module. The code generated for the other containers and modules of the assembly may refer to its members,
	///     so they generate nothing either (a container emits only its shell), and AWT243 stays the only error.
	///     <see cref="AwaitenAnalyzer" /> then checks no container either.
	/// </summary>
	internal static bool SkipsAScanModule(Compilation compilation)
		=> compilation is CSharpCompilation { LanguageVersion: < ModuleScanLanguageVersion, }
		   && TypesWith(compilation.Assembly.GlobalNamespace, "ModuleAttribute")
			   .Any(module => HasAwaitenAttribute(module.GetAttributes(), "ScanAttribute"));

	/// <summary>
	///     AWT243 when the project compiles with a C# version older than <paramref name="required" />, otherwise
	///     <see langword="null" />. Every syntax tree of a compilation has the same language version, so the tree of the
	///     attributed type tells, and a changed version re-parses every tree, which re-runs the transform.
	/// </summary>
	private static DiagnosticInfo? UnsupportedLanguageVersion(GeneratorAttributeSyntaxContext context, LanguageVersion required)
	{
		LanguageVersion version = ((CSharpParseOptions)context.TargetNode.SyntaxTree.Options).LanguageVersion;
		return version >= required
			? null
			: new DiagnosticInfo(
				Diagnostics.UnsupportedLanguageVersion,
				LocationInfo.From(context.TargetSymbol.Locations.FirstOrDefault()),
				new EquatableArray<string>([Display(context.TargetSymbol.ToDisplayString(FullyQualified)), required.ToDisplayString(), version.ToDisplayString(),]));
	}

	/// <summary>
	///     Expands the scans, registrations and handles of a module that receives generated members into what it emits,
	///     which <see cref="ExpandForDemand" /> repeats for every module of the assembly to learn what they take of each
	///     other's handles. Drops the member <paramref name="wrappers" /> a registration factory's hooks replace and the
	///     duplicates among <paramref name="skipped" />.
	/// </summary>
	private static ModuleExpansion ExpandModule(
		INamedTypeSymbol module,
		Compilation compilation,
		HiddenServices? hidden,
		List<MemberWrapper> wrappers,
		List<SkippedRegistration> skipped,
		List<RegistrationFactory> contributions,
		List<DiagnosticInfo> diagnostics,
		CancellationToken cancellationToken)
	{
		List<ModuleFactory> factories = new();
		if (HasAwaitenAttribute(module.GetAttributes(), "ScanAttribute"))
		{
			factories = CollectModuleScanFactories(module, compilation, diagnostics, cancellationToken, hidden)
				.Select(expansion => expansion.Factory)
				.ToList();
		}

		if (hidden is not null)
		{
			RejectScannedHiddenImplementations(hidden, factories, diagnostics);
		}

		List<RegistrationFactory> registrations = CollectModuleRegistrationFactories(module, compilation, diagnostics, factories, skipped, hidden);
		wrappers.RemoveAll(wrapper => registrations.Any(registration => registration.Hooks.AsArray().Any(hook => hook.Hook.WrapperName == wrapper.Name)));
		List<SkippedRegistration> distinct = skipped.GroupBy(entry => entry.MarkerName, StringComparer.Ordinal).Select(group => group.First()).ToList();
		skipped.Clear();
		skipped.AddRange(distinct);
		registrations.AddRange(contributions);
		bool friends = hidden is { Friends: true, };
		if (factories.Any(factory => (factory.Skip is null && TakesHandles(factory.Parameters)) || (friends && HooksTakeHandles(factory))))
		{
			registrations.AddRange(InternalScanMembers(module, compilation, factories, friends, cancellationToken));
		}

		if (hidden is null)
		{
			return new ModuleExpansion(factories, registrations, new List<HiddenHandle>(), new List<HiddenAlias>(), new List<HiddenInitializer>());
		}

		(List<RegistrationFactory> hiddenFactories, List<HiddenHandle> handles, List<HiddenAlias> aliases, List<HiddenInitializer> initializers) = CompleteHiddenServices(hidden, compilation, diagnostics);
		registrations.AddRange(hiddenFactories);
		aliases.AddRange(ScanAliases(hidden, factories));
		return new ModuleExpansion(factories, registrations, handles, aliases, initializers);
	}

	/// <summary>What a module that receives generated members emits (see <see cref="ExpandModule" />).</summary>
	private sealed record ModuleExpansion(List<ModuleFactory> Factories, List<RegistrationFactory> Registrations, List<HiddenHandle> Handles, List<HiddenAlias> Aliases, List<HiddenInitializer> Initializers);

	/// <summary>
	///     Adds to <paramref name="skipped" /> one record per element type and key whose collection a factory among
	///     <paramref name="registrations" /> passes empty, holding the first type that receives it and, on the next line,
	///     the documentation id of the element type, by which a consumer finds it again (AWT252).
	/// </summary>
	private static void RecordEmptyCollections(List<RegistrationFactory> registrations, List<SkippedRegistration> skipped)
	{
		foreach (RegistrationFactory registration in registrations)
		{
			foreach (FactoryParameter parameter in registration.Parameters.AsArray())
			{
				if (parameter is { EmptyCollectionRecord: { } record, EmptyCollectionElement: { } element, } && !skipped.Exists(entry => entry.MarkerName == record))
				{
					skipped.Add(new SkippedRegistration(record, $"{Display(registration.ImplementationType)}\n{element}", EmptyCollection: true));
				}
			}
		}
	}

	/// <summary>
	///     Reports AWT235 when <paramref name="type" />, a module or container, declares a member named like the
	///     generated <see cref="InternalsClass" />, and returns whether it did.
	/// </summary>
	private static bool ReportReservedInternals(INamedTypeSymbol type, List<DiagnosticInfo> diagnostics)
	{
		if (UserInternalsDeclarations(type).FirstOrDefault() is not { } location)
		{
			return false;
		}

		diagnostics.Add(new DiagnosticInfo(
			Diagnostics.ReservedInternalsMember,
			LocationInfo.From(location),
			new EquatableArray<string>([Display(type.ToDisplayString(FullyQualified)),])));
		return true;
	}

	/// <summary>
	///     Whether every type enclosing a nested module is declared <c>partial</c>, so the generated partial can
	///     re-open the whole nesting chain. A type with several declarations is necessarily partial; a single
	///     declaration must carry the modifier itself.
	/// </summary>
	private static bool ContainingTypesArePartial(INamedTypeSymbol module)
	{
		for (INamedTypeSymbol? outer = module.ContainingType; outer is not null; outer = outer.ContainingType)
		{
			bool isPartial = outer.DeclaringSyntaxReferences
				.Select(reference => reference.GetSyntax())
				.OfType<TypeDeclarationSyntax>()
				.Any(syntax => syntax.Modifiers.Any(SyntaxKind.PartialKeyword));
			if (!isPartial)
			{
				return false;
			}
		}

		return true;
	}

	private static ContainerModel? BuildModel(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
	{
		if (context.TargetSymbol is not INamedTypeSymbol containerSymbol)
		{
			return null;
		}

		if (UnsupportedLanguageVersion(context, MinimumLanguageVersion) is { } unsupported)
		{
			return new ContainerModel(null, default, containerSymbol.Name, string.Empty, default, new EquatableArray<DiagnosticInfo>([unsupported,]), false, false, false, false, SkipsGeneration: true);
		}

		Compilation compilation = context.SemanticModel.Compilation;

		// Strict lifetime safety (the default) withholds a disposable build-on-demand service from by-type
		// resolution; Loose relaxes that for MS.DI interop. AWT118 is reported by AwaitenAnalyzer, not here.
		bool strict = ReadStrict(containerSymbol);

		// When set, async-tainted services may also be resolved synchronously after InitializeAsync warms
		// them, and the AWT119/AWT120 sync-resolution diagnostics are not reported.
		bool syncAfterInit = ReadSyncAfterInit(containerSymbol);

		// IAsyncDisposable support is emitted whenever the compilation can see it (see AsyncDisposableSupport).
		// When absent the container is sync-dispose only.
		bool hasAsyncDisposable = AsyncDisposableSupport(compilation) is not null;
		bool declaresNotNullWhen = DeclaresNotNullWhen(compilation);
		bool hasNotNullWhen = declaresNotNullWhen || NotNullWhenSupport(compilation);

		string? containerNamespace = containerSymbol.ContainingNamespace is { IsGlobalNamespace: false, } ns
			? ns.ToDisplayString()
			: null;

		// Walk the enclosing types (outermost first) so the container can be a nested type.
		List<TypeDeclaration> containingTypes = new();
		for (INamedTypeSymbol? outer = containerSymbol.ContainingType; outer is not null; outer = outer.ContainingType)
		{
			containingTypes.Insert(0, new TypeDeclaration(KeywordOf(outer), outer.Name));
		}

		// Qualify the hint name with namespace and enclosing types so containers sharing a simple name do not
		// collide. Nested types use '+' so 'Outer+Inner' cannot collide with a namespaced 'Outer.Inner'.
		string typePath = containingTypes.Count > 0
			? $"{string.Join("+", containingTypes.Select(t => t.Name))}+{containerSymbol.Name}"
			: containerSymbol.Name;
		string hintName = containerNamespace is null
			? $"Awaiten.{typePath}.g.cs"
			: $"Awaiten.{containerNamespace}.{typePath}.g.cs";

		if (SkipsAScanModule(compilation))
		{
			return new ContainerModel(containerNamespace, new EquatableArray<TypeDeclaration>(containingTypes.ToArray()), containerSymbol.Name, hintName, default, default, strict, syncAfterInit, hasAsyncDisposable, hasNotNullWhen, ErrorsElsewhere: true, DeclaresNotNullWhen: declaresNotNullWhen);
		}

		(GraphModel graph, List<DiagnosticInfo> diagnostics) = Analyze(containerSymbol, compilation, cancellationToken);

		// A base reports its own problems; the extending container re-checks the whole graph but reports only what it
		// changes, so an unchanged base problem is not reported twice. A base with errors leaves the inherited graph
		// broken, so the extending container then emits only its shell, like a container with errors of its own. A base
		// in a referenced assembly compiled anyway, so the errors its check finds stay reported here (see RemoveBaseDiagnostics).
		bool hasBaseErrors = false;
		if (ReadExtends(containerSymbol) is { } baseContainer)
		{
			List<DiagnosticInfo> baseDiagnostics = Analyze(baseContainer, compilation, cancellationToken).Diagnostics;
			hasBaseErrors = SymbolEqualityComparer.Default.Equals(baseContainer.ContainingAssembly, containerSymbol.ContainingAssembly)
			                && baseDiagnostics.Exists(diagnostic => diagnostic.EffectiveSeverity == DiagnosticSeverity.Error);
			RemoveBaseDiagnostics(diagnostics, baseDiagnostics, containerSymbol, baseContainer);
		}

		// A module of this assembly reports a member producing a type private to it, or a private member no wrapper serves,
		// in its own build, which leaves the registration out of the graph here (see BuildInstance, ReportedByModule and
		// SkipUnservedModuleMembers), so only the shell is emitted while that fails the build. A base's imports count too,
		// since the container inherits their registrations (see InheritBase).
		hasBaseErrors |= CollectImportedModules(containerSymbol, new List<DiagnosticInfo>())
			.Concat(ReadExtends(containerSymbol) is { } extended ? CollectImportedModules(extended, new List<DiagnosticInfo>()) : [])
			.Select(module => module.Symbol)
			.Where(module => SymbolEqualityComparer.Default.Equals(module.ContainingAssembly, compilation.Assembly))
			.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
			.Any(module => ModuleErrorsFailBuild(module, compilation, cancellationToken));

		ContainerModel model = new(
			containerNamespace,
			new EquatableArray<TypeDeclaration>(containingTypes.ToArray()),
			containerSymbol.Name,
			hintName,
			new EquatableArray<InstanceModel>(graph.Instances.ToArray()),
			new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()),
			strict,
			syncAfterInit,
			hasAsyncDisposable,
			hasNotNullWhen,
			new EquatableArray<ServiceMembers>(graph.Collections.ToArray()),
			new EquatableArray<string>(graph.VarianceCandidates.ToArray()),
			new EquatableArray<KeyedServiceMembers>(graph.KeyedCollections.ToArray()),
			hasBaseErrors,
			graph.External.ImportServices,
			Dispatchable(graph.External.ServiceTypes, graph.External.ServiceTypes.Closed),
			Dispatchable(graph.External.ServiceTypes, graph.External.ServiceTypes.Open),
			new EquatableArray<RegistrationFactory>(graph.Factories.Factories.ToArray()),
			new EquatableArray<MemberWrapper>(graph.Factories.Wrappers.ToArray()),
			RecordedFaults(graph.Factories, compilation),
			new EquatableArray<SkippedRegistration>(graph.Factories.HiddenProperties.ToArray()),
			GrantsInternalsVisibleTo(compilation.Assembly) ? new EquatableArray<SkippedRegistration>(graph.Factories.HookSkips.ToArray()) : default,
			DeclaresNotNullWhen: declaresNotNullWhen,
			ConventionWrappers: new EquatableArray<ConventionWrapper>(graph.Factories.ConventionWrappers.ToArray()));
		return model.HasErrors ? model : VerifyDependencies(model, LocationInfo.From(containerSymbol.Locations.FirstOrDefault()));
	}

	/// <summary>
	///     The faults of <paramref name="factories" /> the container records for an extending container in another assembly,
	///     which reads them through <c>InternalsVisibleTo</c>, so a container whose assembly grants none records none.
	/// </summary>
	private static EquatableArray<RecordedFactoryFault> RecordedFaults(BaseFactories factories, Compilation compilation)
		=> GrantsInternalsVisibleTo(compilation.Assembly)
			? new EquatableArray<RecordedFactoryFault>(factories.Faults
				.OrderBy(fault => fault.Key, StringComparer.Ordinal)
				.Select(fault => new RecordedFactoryFault(fault.Key, fault.Value.Reason, fault.Value.Remedy))
				.ToArray())
			: default;

	/// <summary>
	///     Builds the container's graph and runs every check the generator reports on it.
	/// </summary>
	private static (GraphModel Graph, List<DiagnosticInfo> Diagnostics) Analyze(
		INamedTypeSymbol containerSymbol,
		Compilation compilation,
		CancellationToken cancellationToken)
	{
		bool syncAfterInit = ReadSyncAfterInit(containerSymbol);
		List<DiagnosticInfo> diagnostics = new();

		// The container must be a static class (a pure definition). A non-static class is AWT116; the emitter
		// still emits a throwing Root so consumers fail on that rather than a cascade of missing-member errors.
		if (!containerSymbol.IsStatic)
		{
			diagnostics.Add(new DiagnosticInfo(
				Diagnostics.NonStaticContainer,
				LocationInfo.From(containerSymbol.Locations.FirstOrDefault()),
				new EquatableArray<string>([Display(containerSymbol.ToDisplayString(FullyQualified)),])));
		}

		ReportReservedInternals(containerSymbol, diagnostics);

		GraphModel graph = BuildGraph(containerSymbol, compilation, diagnostics, cancellationToken);

		// AWT179, AWT158, AWT245 and AWT249 are reported by AwaitenAnalyzer from its own graph build.
		diagnostics.RemoveAll(diagnostic => ReferenceEquals(diagnostic.Descriptor, Diagnostics.DuplicateInjectProperty)
		                                    || ReferenceEquals(diagnostic.Descriptor, Diagnostics.OptionalPropertyIsInitOnly)
		                                    || ReferenceEquals(diagnostic.Descriptor, Diagnostics.NullableProduct)
		                                    || ReferenceEquals(diagnostic.Descriptor, Diagnostics.OpenContributionWithoutClosing));

		ValidateRuntimeArguments(graph.Instances, graph.InstanceLocations, graph.ServiceToImpl, graph.ImplToIndex, diagnostics);
		ValidateDecoratorPredicates(graph, diagnostics);

		LocationInfo? containerLocation = LocationInfo.From(containerSymbol.Locations.FirstOrDefault());
		DetectCycles(graph.Instances, graph.ConstructionDependencies, graph.ServiceToImpl, graph.ImplToIndex, containerLocation, diagnostics);
		DetectCaptiveDependencies(graph.Instances, graph.Dependencies, graph.InstanceLocations, diagnostics);
		DetectNonTerminatingDeferredCycles(graph.Instances, graph.ConstructionDependencies, graph.CombinedDependencies, containerLocation, diagnostics);

		// AWT186: an Owned<T> relationship over a requesting-type factory has no owner scope to build into, so the
		// combination is unsupported. Reported regardless of lifetime safety - it is a structural incompatibility,
		// not an async concern - so it is outside the AsyncSafety.SyncAfterInit gate below.
		DetectOwnedOverRequestingTypeFactory(
			graph.Instances, graph.ServiceToImpl, graph.ImplToIndex, graph.InstanceLocations, diagnostics);

		// AWT119/AWT120 (strict only): a synchronous Func<T>/Lazy<T>/Owned<T> relationship resolves its target
		// without awaiting initialization, so it may not target an async-tainted service. AsyncSafety.SyncAfterInit
		// allows it and so is not reported.
		if (!syncAfterInit)
		{
			DetectSynchronousAsyncResolution(
				graph.Instances, graph.Dependencies, graph.ServiceToImpl, graph.ImplToIndex, graph.InstanceLocations, diagnostics);
			DetectSynchronousAsyncCollection(
				graph.Instances, graph.Collections, graph.KeyedCollections, graph.ServiceToImpl, graph.ImplToIndex, graph.InstanceLocations, diagnostics);

			// AWT161 (strict only): an async-tainted Eager singleton has no synchronous construction path, so it
			// cannot be built in the root's synchronous constructor. AsyncSafety.SyncAfterInit allows it (blocking resolver).
			DetectEagerAsyncSingletons(graph.Instances, graph.InstanceLocations, diagnostics);
		}

		NameHiddenServices(containerSymbol, compilation, CalledMembers(graph.Instances), diagnostics, cancellationToken);
		return (graph, diagnostics);
	}

	private static EquatableArray<string> Dispatchable(ExternalServiceTypes serviceTypes, HashSet<string> types)
		=> new(types.Where(type => !serviceTypes.Inaccessible.Contains(type)).OrderBy(type => type, StringComparer.Ordinal).ToArray());

	/// <summary>
	///     The framework interface symbols an instance is matched against, looked up once:
	///     <c>System.IDisposable</c> and the async-disposal symbol (non-null only when the runtime exposes it).
	///     Either may be <see langword="null" />.
	/// </summary>
	private sealed record WellKnownTypes(
		INamedTypeSymbol? Disposable,
		INamedTypeSymbol? AsyncDisposable);

	/// <summary>
	///     The container's external-resolution surface, consulted during constructor selection and open-generic/scan
	///     seeding: the blanket <c>[ImportServices]</c> fall-through (<see cref="ImportServices" />) plus the per-type
	///     <c>[ImportService]</c> declarations (<see cref="ServiceTypes" />). The two are computed together
	///     and bundled so they travel as one through the coalescing-phase helpers that decide whether a constructor
	///     parameter is satisfiable, keeping their parameter lists short.
	/// </summary>
	internal readonly record struct ExternalSurface(bool ImportServices, ExternalServiceTypes ServiceTypes);

	/// <summary>
	///     The service types declared external with <c>[ImportService&lt;T&gt;]</c> or <c>[ImportService(typeof(…))]</c>:
	///     the closed types (<see cref="Closed" />) and the unbound generic definitions (<see cref="Open" />), each by its
	///     fully qualified display (<c>global::Ns.ILogger&lt;&gt;</c> for a definition).
	/// </summary>
	/// <remarks>
	///     Lives only inside one graph build and never reaches a model. <see cref="Closings" /> is filled while
	///     dependencies are classified, because the model's service type strings no longer carry the generic definition
	///     that AWT175 matches the closings of an open import by.
	/// </remarks>
	internal sealed class ExternalServiceTypes
	{
		/// <summary>The closed service types, routed only when a dependency names exactly that type.</summary>
		public HashSet<string> Closed { get; } = new(StringComparer.Ordinal);

		/// <summary>The declared closed generic types, which an open decorator closes over; only those still in <see cref="Closed" /> count.</summary>
		public List<INamedTypeSymbol> ClosedGenerics { get; } = new();

		/// <summary>The unbound generic definitions, routing every closing a dependency names.</summary>
		public HashSet<string> Open { get; } = new(StringComparer.Ordinal);

		/// <summary>
		///     The <see cref="Closed" /> types and <see cref="Open" /> definitions the container cannot name, such as an
		///     internal type an imported module from another assembly declares, which its by-type dispatch therefore
		///     cannot route.
		/// </summary>
		public HashSet<string> Inaccessible { get; } = new(StringComparer.Ordinal);

		/// <summary>Each closing of an <see cref="Open" /> definition met during classification, mapped to that definition.</summary>
		public Dictionary<string, string> Closings { get; } = new(StringComparer.Ordinal);

		/// <summary>
		///     The imported services and keys a decorator chain wraps: a direct dependency of one resolves the chain, whose
		///     innermost link alone asks the host.
		/// </summary>
		public HashSet<ServiceKey> Decorated { get; } = new();

		/// <summary>
		///     Each closing handle of an imported module whose <see cref="Open" /> definition the container also registers,
		///     mapped to the closing it holds, which AWT175 names when the handle is routed (see <c>RegisterClosings</c>).
		/// </summary>
		public Dictionary<string, string> RegisteredClosingHandles { get; } = new(StringComparer.Ordinal);

		/// <summary>Whether a direct dependency of <paramref name="type" /> (displayed as <paramref name="serviceType" />) is routed externally.</summary>
		public bool Routes(ITypeSymbol type, string serviceType)
			=> Closed.Contains(serviceType) || (Open.Count > 0 && OpenDefinitionOf(type) is { } definition && Open.Contains(definition));

		/// <summary>
		///     Whether a direct dependency of <paramref name="serviceType" /> would be routed externally, for a service
		///     type already met during classification (so a closing of an <see cref="Open" /> definition is in
		///     <see cref="Closings" />).
		/// </summary>
		public bool Routes(string serviceType)
			=> Closed.Contains(serviceType) || Closings.ContainsKey(serviceType);

		/// <summary>
		///     Records every closing of an <see cref="Open" /> definition within <paramref name="type" /> (the type itself
		///     and its type arguments, recursively).
		/// </summary>
		public void RecordClosings(ITypeSymbol type)
		{
			if (Open.Count == 0 || type is not INamedTypeSymbol named)
			{
				return;
			}

			if (OpenDefinitionOf(named) is { } definition && Open.Contains(definition))
			{
				Closings[named.ToDisplayString(FullyQualified)] = definition;
			}

			foreach (ITypeSymbol argument in named.TypeArguments)
			{
				RecordClosings(argument);
			}
		}

		/// <summary>The display of the unbound generic definition <paramref name="type" /> closes, or <see langword="null" /> when it is not a closed generic.</summary>
		private static string? OpenDefinitionOf(ITypeSymbol type)
			=> type is INamedTypeSymbol { Arity: > 0, IsUnboundGenericType: false, } named
				? UnboundDisplay(named)
				: null;
	}

	/// <summary>The fully qualified display of the unbound generic definition of <paramref name="type" />, e.g. <c>global::Ns.ILogger&lt;&gt;</c>.</summary>
	internal static string UnboundDisplay(INamedTypeSymbol type)
		=> type.OriginalDefinition.ConstructUnboundGenericType().ToDisplayString(FullyQualified);

	/// <summary>
	///     The container-wide inputs threaded to each per-implementation <see cref="BuildInstance" />, passed
	///     together to avoid a long parameter list.
	/// </summary>
	private sealed record BuildContext(
		INamedTypeSymbol ContainerSymbol,
		Compilation Compilation,
		Dictionary<ServiceKey, string> ServiceToImpl,
		Dictionary<string, DecoratorInner> DecoratorInner,
		Dictionary<string, ServiceKey> CompositeCollections,
		WellKnownTypes WellKnown,
		HashSet<string> ConstraintRejected,
		Dictionary<ServiceKey, List<DiagnosticInfo>> DeferredRejections,
		ExternalSurface External,
		VarianceState Variance,
		HashSet<ServiceKey> ConsumedConditionals,
		Dictionary<string, List<InjectPropertyEntry>> InjectProperties,
		List<(string ServiceType, INamedTypeSymbol Symbol)> DispatchCandidates,
		List<DiagnosticInfo> Diagnostics,
		ServedTypes ServedTypes,
		BaseFactories? Factories,
		AsyncInitializerScopes AsyncInitializers,
		SameCompilationModules Modules,
		HiddenInjectedProperties HiddenInjectedProperties,
		Dictionary<string, PredicateModel> Predicates,
		Dictionary<string, string> ClosingFaults,
		HashSet<InjectPropertyEntry> ReportedHandleEntries,
		Dictionary<ServiceKey, (string Module, string Reason)> SkippedModuleRegistrations);

	/// <summary>
	///     The mutable coalesced graph state the decorator-chain and composite builders rewrite in place: the
	///     single-dispatch winner per key, the implementations in declaration order, and collection membership.
	/// </summary>
	private sealed record CoalescedGraph(
		Dictionary<ServiceKey, string> ServiceToImpl,
		List<ImplInfo> ImplOrder,
		Dictionary<ServiceKey, List<string>> ServiceMembers);

	/// <summary>
	///     Resolves the container's object graph: coalesces registrations, builds an <see cref="InstanceModel" />
	///     per implementation and computes the dependency edges. Registration faults (AWT101/103/104/107-112) go
	///     to <paramref name="diagnostics" />. Shared by the generator and <see cref="AwaitenAnalyzer" /> (AWT118, AWT179).
	/// </summary>
	internal static GraphModel BuildGraph(
		INamedTypeSymbol containerSymbol,
		Compilation compilation,
		List<DiagnosticInfo> diagnostics,
		CancellationToken cancellationToken)
	{
		// An instance is marked IsAsyncDisposable only when the emitter will emit the async-disposal surface
		// (see AsyncDisposableSupport).
		WellKnownTypes wellKnown = new(
			compilation.GetTypeByMetadataName("System.IDisposable"),
			AsyncDisposableSupport(compilation));

		// The container's own [AsyncInitializer<T>] conventions are its to report (AWT233); a base or module reports
		// its own in its own build.
		DeclaredAsyncInitializers(containerSymbol, compilation, diagnostics);

		// The imported modules, resolved once. Import validation (AWT149-152) is reported while collecting.
		List<ImportedModule> modules = CollectImportedModules(containerSymbol, diagnostics);

		// [Container(Extends = typeof(Base))]: the base and its imports join as inherited contributions, read like
		// modules qualified on their owner, and the container's own registrations replace theirs (see ReplaceInherited).
		Inheritance? inheritance = null;
		if (ResolveExtends(containerSymbol, diagnostics, cancellationToken) is { } extended)
		{
			modules = InheritBase(extended, modules);
			inheritance = new Inheritance(containerSymbol, extended, modules, compilation, cancellationToken);
		}

		ReportEmptyCollectionsOfModules(containerSymbol, modules, compilation, diagnostics);

		// Only a container that extends none can be extended, so only such a container builds factories for its private
		// implementations (see RecordBaseFactory).
		BaseFactories? factories = ExtendsTarget(containerSymbol, out _) is null ? new BaseFactories() : null;
		if (factories is not null)
		{
			RecordBaseOpenFactories(containerSymbol, compilation, factories);
			RecordBasePredicateWrappers(containerSymbol, compilation, factories);
			factories.ConventionWrappers.AddRange(CollectConventionWrappers(containerSymbol, compilation));
		}

		// [ImportServices]: any otherwise-unresolved direct dependency falls through to the external provider
		// instead of AWT101. Computed up front because it widens constructor selection everywhere. A module can
		// contribute it too.
		bool importServices = ContainerImportsServices(containerSymbol)
		                      || modules.Any(module => HasAwaitenAttribute(module.Symbol.GetAttributes(), "ImportServicesAttribute"));

		// [ImportService<T>] / [ImportService(typeof(...))]: the typed counterpart of the blanket flag above, honored on
		// the container and any imported module. Each declared type (or every closing of a declared open generic
		// definition) routes every dependency of that type - keyed or not, whether a constructor parameter, factory
		// parameter or [Inject] property - to the external provider, while every other unresolved dependency keeps the
		// AWT101 check. Bundled with the blanket flag as the container's external-resolution surface, threaded through
		// the coalescing phase that decides constructor satisfiability.
		ExternalServiceTypes externalServiceTypes = CollectExternalServiceTypes(containerSymbol, modules, compilation, diagnostics);

		// An extending container's own registration replaces an inherited [ImportService<T>] of that type, as it
		// replaces an inherited registration (the reverse is in ReplaceInherited).
		if (inheritance is not null)
		{
			ReplaceInheritedImportServices(containerSymbol, externalServiceTypes);
		}

		ExternalSurface external = new(importServices, externalServiceTypes);

		// Container-side property injection: [InjectProperty<TImpl>] entries keyed by implementation type, so
		// BuildInstance fills them wherever that implementation is constructed (including [Scan]-registered types).
		// Read before Collect, since an injected property seeds open generic expansion like a constructor parameter.
		Dictionary<string, List<InjectPropertyEntry>> injectProperties = CollectInjectProperties(containerSymbol, compilation, diagnostics);
		if (inheritance is not null)
		{
			InheritInjectProperties(inheritance, injectProperties, compilation);
		}

		// Decorators and composites are read before Collect, since their parameters seed open generic expansion;
		// their diagnostics still follow the registrations'.
		List<DiagnosticInfo> wrapperDiagnostics = new();
		(List<DecorateRegistration> decorators, List<OpenDecorateRegistration> openDecorators) =
			CollectDecorators(containerSymbol, modules, inheritance, compilation, wrapperDiagnostics);
		(List<CompositeRegistration> composites, List<OpenCompositeRegistration> openComposites) =
			CollectComposites(containerSymbol, modules, inheritance, compilation, wrapperDiagnostics);

		// Collect also expands [Scan]s into overridable registrations (IsScan), ordered after the explicit ones
		// so an explicit registration wins single resolution while every match still joins its collection.
		(List<RawRegistration> raw, HashSet<string> constraintRejected, Dictionary<ServiceKey, List<DiagnosticInfo>> deferredRejections, List<OpenRegistration> open, Dictionary<string, string> closingFaults) = Collect(
			containerSymbol, modules, compilation, external, injectProperties, inheritance, new WrapperRegistrations(decorators, openDecorators, composites, openComposites), diagnostics);
		Dictionary<ServiceKey, (string Module, string Reason)> skippedModuleRegistrations = SkipUnservedModuleMembers(raw, containerSymbol, compilation, cancellationToken);

		if (factories is not null)
		{
			RecordHiddenInjectedProperties(containerSymbol, compilation, injectProperties, open, openDecorators, openComposites, factories);
		}

		// A scan match a module's generated factory builds shares the instance of every other registration of its
		// implementation, as it would in the module's own assembly.
		HashSet<string> checkedHandles = ShareHandledInstances(raw, containerSymbol, compilation, diagnostics);
		ShareGeneratedRegistrationFactories(raw, open, containerSymbol, compilation);
		List<string> sharedScanImplementations = ShareScannedInstances(raw, compilation);

		// A scanned private nested type the resolvers cannot stand in for is skipped with AWT193, not the AWT216 error,
		// and so is a scanned type of another assembly whose constructors the container cannot call, not AWT104.
		DropUnsupportedPrivateScanMatches(raw, containerSymbol, compilation, diagnostics);
		DropScanMatchesWithoutAccessibleConstructor(raw, containerSymbol, compilation, diagnostics);

		// [Scan(SkipUnconstructable = true)] trades the AWT101 error for a skip-with-warning (AWT141) on matches
		// the container cannot construct. Scans without the opt-in, and explicit registrations, keep the error.
		PruneUnconstructableScanMatches(raw, containerSymbol, compilation, external, constraintRejected, diagnostics);

		// After the pruning, so a pruned match reports only AWT141. A dropped match can leave another opted-in match
		// unconstructable, so the pruning runs again.
		if (DropUnsharedScanMatches(raw, sharedScanImplementations, compilation, diagnostics))
		{
			PruneUnconstructableScanMatches(raw, containerSymbol, compilation, external, constraintRejected, diagnostics);
		}

		diagnostics.AddRange(wrapperDiagnostics);

		// Coalesce registrations by (service type, key): first per key wins, and registrations of the same
		// implementation share one instance. Conflicting lifetimes are AWT107; a duplicate key is AWT117.
		SettleHandleDirectives(raw, containerSymbol, diagnostics);
		ReportProducersBesideSharedHandles(raw, containerSymbol, diagnostics);
		AssignMemberIdentities(raw, containerSymbol, compilation);
		List<CrossModuleDuplicate> duplicates = new();
		(List<ImplInfo> implOrder, Dictionary<ServiceKey, string> serviceToImpl, Dictionary<ServiceKey, List<string>> serviceMembers, List<ServiceKey> serviceMemberOrder, List<(string ServiceType, INamedTypeSymbol Symbol)> varianceCandidates, Dictionary<string, List<KeyedMember>> keyedMembers, List<string> keyedMemberOrder) =
			CoalesceByImplementation(raw, containerSymbol, compilation, duplicates, checkedHandles, diagnostics);
		CoalescedGraph graph = new(serviceToImpl, implOrder, serviceMembers);

		// The closed generic service symbols the coalesced graph holds (recovered from the raw registrations,
		// which carry each closing's symbol), so an open decorator/composite can find every closing to expand onto.
		List<(string Display, INamedTypeSymbol Symbol)> closedServices = openDecorators.Count > 0 || openComposites.Count > 0
			? ClosedGenericServices(raw)
			: new List<(string, INamedTypeSymbol)>();

		// Open generic decorators: synthesize a closed [Decorate] per matching closing before building the chains,
		// so they flow through the same DecoratorChainBuilder as the closed form and interleave with it. A no-op
		// when there are none (closedServices is then empty and the expansion has nothing to iterate).
		ExpandOpenDecorators(decorators, openDecorators, closedServices, graph, constraintRejected, externalServiceTypes, compilation, diagnostics);

		// Decorator chains: for each [Decorate]d service, move the base impl(s) onto a synthetic key and register
		// each decorator as a chain link whose inner parameter redirects to the next-lower key. decoratorInner
		// keys each link's inner parameter to the link it wraps (consumed by ClassifyParameters).
		Dictionary<string, DecoratorInner> decoratorInner = new(StringComparer.Ordinal);
		if (decorators.Count > 0)
		{
			new DecoratorChainBuilder(containerSymbol, compilation, graph, keyedMembers, decoratorInner, external, constraintRejected, diagnostics)
				.Build(decorators);
		}

		// Open generic composites: synthesize a closed [Composite] per matching closing before building the
		// composites, so they flow through the same BuildComposites as the closed form (fronting the decorated
		// members). A no-op when there are none, as with the open decorators above.
		ExpandOpenComposites(composites, openComposites, closedServices, graph, constraintRejected, compilation, diagnostics);

		// Composites: each [Composite<TComposite, TService>] registers the composite and makes it the public
		// winner for TService, excluded from its own collection so its collection parameter fans out to the
		// others. Runs after decorator chains so a composite fronts the decorated members.
		Dictionary<string, ServiceKey> compositeCollections = new(StringComparer.Ordinal);
		if (composites.Count > 0)
		{
			BuildComposites(composites, compilation, containerSymbol, graph, external, compositeCollections, diagnostics);
		}

		List<InstanceModel> instances = new();
		List<LocationInfo?> instanceLocations = new();
		Dictionary<string, int> implToIndex = new(StringComparer.Ordinal);

		// Variance state: the candidate registrations plus the accumulators BuildInstance fills as it redirects
		// consumer parameters, drained after the instance loop below.
		VarianceState variance = new(varianceCandidates, compilation);

		// Contextual (WhenInjectedInto) bindings recorded up front, plus the set of context keys a consumer
		// dependency actually redirects to. A binding whose key stays absent is reported (AWT167) below, unless it is
		// inherited: the base reports its own, and one whose consumer the extending container replaced falls away.
		List<ConditionalRegistration> conditionals = CollectConditionalRegistrations(
			inheritance is null ? raw : raw.FindAll(registration => !inheritance.Owns(registration.Declarer ?? registration.Origin)));
		HashSet<ServiceKey> consumedConditionals = new();

		// AWT175: a closed type declared [ImportService] must not also be registered on the container - it is either
		// host-owned or Awaiten-owned, not both. Reported now that the coalesced service->impl map is known. The
		// closings of an open import are checked once the instances are built (ReportOpenExternalServices).
		ReportContradictingExternalServices(externalServiceTypes, serviceToImpl, containerSymbol, diagnostics);

		BuildContext buildContext = new(containerSymbol, compilation, serviceToImpl, decoratorInner, compositeCollections, wellKnown, constraintRejected, deferredRejections, external, variance, consumedConditionals, injectProperties, CollectDispatchCandidates(raw, serviceToImpl), diagnostics, ServedTypesOf(raw, serviceMembers, keyedMembers), factories, new AsyncInitializerScopes(compilation), new SameCompilationModules(compilation), new HiddenInjectedProperties(compilation), new Dictionary<string, PredicateModel>(StringComparer.Ordinal), closingFaults, new HashSet<InjectPropertyEntry>(), skippedModuleRegistrations);

		// Validate each implementation, select its constructor and build the instance.
		foreach (ImplInfo info in implOrder)
		{
			cancellationToken.ThrowIfCancellationRequested();
			InstanceModel? instance = BuildInstance(info, buildContext);
			if (instance is not null)
			{
				implToIndex[info.ImplementationType] = instances.Count;
				instances.Add(instance);
				instanceLocations.Add(info.Location);
			}
		}

		LinkOutermostDecorators(implOrder, instances, implToIndex);
		MarkAliasesOfTrackedHandles(instances, serviceToImpl, implToIndex);

		// AWT167: a contextual registration whose named consumer never redirected to its context key (no unkeyed
		// direct dependency on the service) is never reached, so report it now that every instance is built.
		ReportUnappliedContextualBindings(conditionals, consumedConditionals, diagnostics);

		// AWT155 once every instance is built, so it is known whether anything resolves the service singly.
		Dictionary<string, CrossModuleDuplicate> unresolvedDuplicates = ReportCrossModuleDuplicates(duplicates, instances, composites, variance, diagnostics);

		// AWT175 for an open import: each routed closing the container also registers is contradictory. Reported once
		// every instance is built, so every dependency has had the chance to route.
		ReportOpenExternalServices(externalServiceTypes, serviceToImpl, RoutedExternal(instances), containerSymbol, diagnostics);

		// AWT180: an [InjectProperty<TImpl>] entry whose implementation type matches no registration is never
		// applied, reported now that every registered implementation has been seen.
		ReportUnmatchedInjectProperties(injectProperties, implOrder, buildContext.ReportedHandleEntries, compilation, diagnostics);

		// Variance for collections (Part C): union every variance-compatible registration's members into each
		// requested closed-generic collection. Before the parameterized prune and edge building so the unioned
		// members drive analysis.
		UnionVarianceCollectionMembers(variance, serviceMembers, serviceMemberOrder);

		// Variance for top-level dispatch (Part B): expose each variance-redirected closed generic type as a
		// dispatch alias on its target instance, so an imperative Resolve routes it like the consumer parameter.
		ApplyVarianceDispatchAliases(variance, instances, implToIndex, serviceToImpl);

		// A parameterized ([Arg]) service is reachable only through its Func<TArg…, T> factory, never as a
		// collection member. Prune such implementations before they drive collection edges, AWT122 and emission.
		PruneParameterizedMembers(instances, serviceMembers, keyedMembers);

		Dictionary<int, List<int>> dependencies = BuildDependencyGraph(instances, serviceToImpl, implToIndex, serviceMembers, keyedMembers);
		Dictionary<int, List<int>> constructionDependencies = BuildConstructionGraph(instances, serviceToImpl, implToIndex, serviceMembers, keyedMembers);

		// The combined construction-plus-deferred graph vets deferred cycles (AWT145-147). With no deferred member
		// it IS the construction graph, so the extra pass is skipped.
		Dictionary<int, List<int>> combinedDependencies = AnyDeferredMember(instances)
			? BuildCombinedGraph(instances, serviceToImpl, implToIndex, serviceMembers, keyedMembers)
			: constructionDependencies;

		// Async taint: an instance is tainted if async-initialized, or if it reaches one through non-deferred
		// (Direct) edges. Relationship types launder the taint, so they contribute no edges to the dependency graph.
		bool[] tainted = PropagateAsyncTaint(instances, dependencies);
		if (AwaitAsyncHandlesOfAliases(instances, tainted, serviceToImpl, implToIndex))
		{
			tainted = PropagateAsyncTaint(instances, dependencies);
		}

		for (int i = 0; i < instances.Count; i++)
		{
			if (tainted[i])
			{
				instances[i] = instances[i] with { IsAsyncTainted = true, };
			}
		}

		// The collection-resolvable membership, in first-seen service order, keeping only members that built
		// (a failed member is absent from implToIndex and dropped rather than emitted as a dangling call).
		List<ServiceMembers> collections = new();
		foreach (ServiceKey service in serviceMemberOrder)
		{
			string[] members = serviceMembers[service].Where(implToIndex.ContainsKey).ToArray();
			if (members.Length > 0)
			{
				collections.Add(new ServiceMembers(service.Service, service.Key, new EquatableArray<string>(members)));
			}
		}

		List<KeyedServiceMembers> keyedCollections = BuiltKeyedCollections(keyedMemberOrder, keyedMembers, implToIndex);
		ReportEmptyImportedCollections(instances, instanceLocations, collections, keyedCollections, externalServiceTypes, diagnostics);
		ReportCollectionsMissingHandledRegistrations(raw, instances, instanceLocations, implToIndex, variance, modules, diagnostics);

		// The variance candidates' service types (registration order), for the emitter's runtime variance
		// fallback: an imperative Resolve of a differently-closed generic no consumer requested is matched here.
		List<string> varianceCandidateTypes = new(variance.Candidates.Count);
		foreach ((string serviceType, INamedTypeSymbol _) in variance.Candidates)
		{
			varianceCandidateTypes.Add(serviceType);
		}

		return new GraphModel(instances, dependencies, constructionDependencies, combinedDependencies, serviceToImpl, implToIndex, instanceLocations, collections, varianceCandidateTypes, keyedCollections, external, OpenServices(open), factories ?? new BaseFactories(), unresolvedDuplicates);
	}

	/// <summary>
	///     Records on each decorated instance the indices of the outermost decorator links that wrap it, once every
	///     instance has its index. A link that failed to build is left out.
	/// </summary>
	private static void LinkOutermostDecorators(
		List<ImplInfo> implOrder,
		List<InstanceModel> instances,
		Dictionary<string, int> implToIndex)
	{
		foreach (ImplInfo info in implOrder)
		{
			if (info.OutermostDecorators.Count > 0 && implToIndex.TryGetValue(info.ImplementationType, out int index))
			{
				int[] decorators = info.OutermostDecorators
					.Where(implToIndex.ContainsKey)
					.Select(identity => implToIndex[identity])
					.ToArray();
				instances[index] = instances[index] with { OutermostDecorators = new EquatableArray<int>(decorators), };
			}
		}
	}

	/// <summary>
	///     The keyed-collection membership, in first-seen service order, keeping only members that built (a
	///     failed member is absent from <paramref name="implToIndex" />). A service with none is absent, and
	///     injecting its dictionary yields an empty one.
	/// </summary>
	private static List<KeyedServiceMembers> BuiltKeyedCollections(
		List<string> keyedMemberOrder,
		Dictionary<string, List<KeyedMember>> keyedMembers,
		Dictionary<string, int> implToIndex)
	{
		List<KeyedServiceMembers> keyedCollections = new();
		foreach (string service in keyedMemberOrder)
		{
			KeyedMember[] members = keyedMembers[service].Where(member => implToIndex.ContainsKey(member.Implementation)).ToArray();
			if (members.Length > 0)
			{
				keyedCollections.Add(new KeyedServiceMembers(service, new EquatableArray<KeyedMember>(members), DictionaryKeyType(members)));
			}
		}

		return keyedCollections;
	}
}
