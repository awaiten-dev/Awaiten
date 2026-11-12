using Awaiten.SourceGenerators.Entities;
using Awaiten.SourceGenerators.Internals;

namespace Awaiten.SourceGenerators;

partial class AwaitenGenerator
{
	/// <summary>
	///     Applies the <c>ConsumerOwned</c> option of each consumer: marks every <c>Func</c>, async <c>Func</c> and dispatch
	///     dictionary over a listed build-on-demand service, so the emitter hands what it builds over untracked and the
	///     analyzer skips AWT118 for it. Reports AWT261 where building such a service leaves more on the owner than the
	///     instance, and AWT262 for an entry that marks nothing. A singleton or scoped target is shared rather than built
	///     per call, so the consumer never owns it.
	/// </summary>
	private static void ApplyConsumerOwned(
		List<InstanceModel> instances,
		List<LocationInfo?> instanceLocations,
		Dictionary<ServiceKey, string> serviceToImpl,
		Dictionary<string, int> implToIndex,
		CollectionMembership membership,
		List<DiagnosticInfo> diagnostics)
	{
		Dictionary<ServiceKey, int> serviceToIndex = new();
		foreach (KeyValuePair<ServiceKey, string> entry in serviceToImpl)
		{
			if (implToIndex.TryGetValue(entry.Value, out int index))
			{
				serviceToIndex[entry.Key] = index;
			}
		}

		for (int i = 0; i < instances.Count; i++)
		{
			InstanceModel consumer = instances[i];
			if (consumer.ConsumerOwned.Count == 0)
			{
				continue;
			}

			string consumerDisplay = DisplayInstance(consumer.ImplementationType);
			HashSet<string> listed = new(consumer.ConsumerOwned.AsArray(), StringComparer.Ordinal);
			HashSet<string> applied = new(StringComparer.Ordinal);
			HashSet<string> reported = new(StringComparer.Ordinal);

			instances[i] = consumer with
			{
				ConstructorParameters = Marked(consumer.ConstructorParameters),
				ActivationParameters = Marked(consumer.ActivationParameters),
				ReleaseParameters = Marked(consumer.ReleaseParameters),
				InjectedMembers = new EquatableArray<MemberModel>(consumer.InjectedMembers.AsArray()
					.Select(member => member with { Dependency = Mark(member.Dependency), })
					.ToArray()),
			};

			foreach (string service in consumer.ConsumerOwned.AsArray().Where(service => !applied.Contains(service)).Distinct(StringComparer.Ordinal))
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.ConsumerOwnedNeverApplies,
					instanceLocations[i],
					new EquatableArray<string>([consumerDisplay, Display(service),])));
			}

			EquatableArray<ParameterModel> Marked(EquatableArray<ParameterModel> parameters)
				=> new(parameters.AsArray().Select(Mark).ToArray());

			ParameterModel Mark(ParameterModel parameter)
			{
				if (parameter.Kind is not (DependencyKind.Func or DependencyKind.FuncTask or DependencyKind.TypeDispatch)
				    || parameter.ProducesOwned
				    || !listed.Contains(parameter.ServiceType))
				{
					return parameter;
				}

				int[] targets = (parameter.Kind == DependencyKind.TypeDispatch
						? parameter.DispatchMembers.AsArray().Select(member => new ServiceKey(member.Service, null))
						: [new ServiceKey(parameter.ServiceType, parameter.Key),])
					.Select(key => serviceToIndex.TryGetValue(key, out int index) ? index : -1)
					.Where(index => index >= 0 && (instances[index].Lifetime == Lifetime.Transient || instances[index].IsParameterized))
					.ToArray();
				if (targets.Length == 0)
				{
					return parameter;
				}

				applied.Add(parameter.ServiceType);
				foreach (int target in targets)
				{
					if (ConsumerOwnedFault(instances, serviceToIndex, membership, target) is { } fault
					    && reported.Add($"{parameter.ServiceType}|{target}"))
					{
						diagnostics.Add(new DiagnosticInfo(
							Diagnostics.ConsumerOwnedBuildsMore,
							parameter.Location ?? instanceLocations[i],
							new EquatableArray<string>([
								consumerDisplay,
								Display(parameter.ServiceType),
								DisplayInstance(instances[target].ImplementationType),
								fault,
							])));
					}
				}

				return parameter with { ConsumerOwned = true, };
			}
		}
	}
}
