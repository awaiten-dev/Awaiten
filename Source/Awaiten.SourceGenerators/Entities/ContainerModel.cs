using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators.Entities;

/// <summary>
///     The fully-resolved, equatable model of a single <c>[Container]</c> partial class, ready for
///     emission. <see cref="ErrorsElsewhere" /> marks a container whose graph is broken by an error another declaration
///     reports, its extended base, an imported module of its assembly or a module of its assembly that AWT243 skips, so
///     only its shell is emitted.
///     <see cref="ImportServices" />, <see cref="ImportedServices" /> and <see cref="ImportedDefinitions" /> are the types the container leaves to
///     the host: <c>[ImportServices]</c>, the closed <c>[ImportService]</c> types and the unbound generic
///     definitions of the open ones. <see cref="Factories" /> are the factories the container generates for its private
///     implementations, so a container that extends it can build them, <see cref="Wrappers" /> forward to its private
///     members its registrations name, and <see cref="Faults" /> record why it generates no factory for the others.
///     <see cref="HiddenProperties" /> record the public types it constructs whose
///     <c>[Inject]</c> property another assembly cannot see. <see cref="SkipsGeneration" /> marks a container the project's
///     language version is too old for, which only reports its <see cref="Diagnostics" />. <see cref="HasNotNullWhen" />
///     tells whether the project can apply <c>NotNullWhenAttribute</c>, which .NET Framework and netstandard2.0 have only
///     through a polyfill.
/// </summary>
internal sealed record ContainerModel(
	string? Namespace,
	EquatableArray<TypeDeclaration> ContainingTypes,
	string TypeName,
	string HintName,
	EquatableArray<InstanceModel> Instances,
	EquatableArray<DiagnosticInfo> Diagnostics,
	bool Strict,
	bool SyncAfterInit,
	bool HasAsyncDisposable,
	bool HasNotNullWhen,
	EquatableArray<ServiceMembers> Collections = default,
	EquatableArray<string> VarianceCandidates = default,
	EquatableArray<KeyedServiceMembers> KeyedCollections = default,
	bool ErrorsElsewhere = false,
	bool ImportServices = false,
	EquatableArray<string> ImportedServices = default,
	EquatableArray<string> ImportedDefinitions = default,
	EquatableArray<RegistrationFactory> Factories = default,
	EquatableArray<MemberWrapper> Wrappers = default,
	EquatableArray<RecordedFactoryFault> Faults = default,
	EquatableArray<SkippedRegistration> HiddenProperties = default,
	bool SkipsGeneration = false)
{
	public bool Imports => ImportServices || ImportedServices.Count > 0 || ImportedDefinitions.Count > 0;

	public bool HasErrors
	{
		get
		{
			if (ErrorsElsewhere)
			{
				return true;
			}

			foreach (DiagnosticInfo diagnostic in Diagnostics.AsArray())
			{
				if (diagnostic.EffectiveSeverity == DiagnosticSeverity.Error)
				{
					return true;
				}
			}

			return false;
		}
	}
}

/// <summary>
///     Why a container generates no factory for the implementation private to it whose factory would be named
///     <see cref="FactoryName" />, and what the user can do about it, which an extending container in another assembly
///     reports in AWT226.
/// </summary>
internal sealed record RecordedFactoryFault(string FactoryName, string Reason, string Remedy);
