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
			string[] listed = consumer.ConsumerOwned.AsArray().Distinct(StringComparer.Ordinal).ToArray();
			HashSet<string> applied = new(StringComparer.Ordinal);
			HashSet<string> reported = new(StringComparer.Ordinal);

			instances[i] = consumer with
			{
				ConstructorParameters = Marked(consumer.ConstructorParameters),
				ActivationParameters = Marked(consumer.ActivationParameters),
				ReleaseParameters = Marked(consumer.ReleaseParameters),
				AsyncInitializers = new EquatableArray<AsyncInitializerModel>(consumer.AsyncInitializers.AsArray()
					.Select(initializer => initializer with { Parameters = Marked(initializer.Parameters), })
					.ToArray()),
				InjectedMembers = new EquatableArray<MemberModel>(consumer.InjectedMembers.AsArray()
					.Select(member => member with { Dependency = Mark(member.Dependency), })
					.ToArray()),
			};

			foreach (string service in listed.Where(service => !applied.Contains(service)))
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
				string[] entries = listed.Where(entry => Lists(entry, parameter.ServiceType)).ToArray();
				if (parameter.Kind is not (DependencyKind.Func or DependencyKind.FuncTask or DependencyKind.TypeDispatch)
				    || parameter.ProducesOwned
				    || entries.Length == 0)
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

				applied.UnionWith(entries);
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

	/// <summary>
	///     Whether the <c>ConsumerOwned</c> <paramref name="entry" /> names <paramref name="service" />: the type itself, or
	///     for a generic type definition such as <c>typeof(Repository&lt;&gt;)</c> each of its closings.
	/// </summary>
	private static bool Lists(string entry, string service)
	{
		if (entry == service)
		{
			return true;
		}

		int open = entry.IndexOf('<');
		return open > 0
		       && entry.Substring(open).Trim('<', '>').All(c => c == ',')
		       && service.Length > open && service[open] == '<'
		       && string.CompareOrdinal(entry, 0, service, 0, open) == 0
		       && TopLevelArity(service, open) == entry.Length - open - 1;

		static int TopLevelArity(string type, int open)
		{
			int depth = 0, arity = 1;
			for (int i = open; i < type.Length; i++)
			{
				switch (type[i])
				{
					case '<':
						depth++;
						break;
					case '>':
						if (--depth == 0)
						{
							return i == type.Length - 1 ? arity : -1;
						}

						break;
					case ',' when depth == 1:
						arity++;
						break;
				}
			}

			return -1;
		}
	}
}
