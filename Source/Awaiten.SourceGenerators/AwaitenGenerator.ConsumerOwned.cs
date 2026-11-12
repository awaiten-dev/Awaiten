using System.Text;
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

		// Every consumer is marked before any build is checked, so a Func that a service below the target owns itself
		// counts as owned whatever the registration order.
		List<(int Consumer, ParameterModel Parameter, int[] Targets)> owned = new();
		for (int i = 0; i < instances.Count; i++)
		{
			InstanceModel consumer = instances[i];
			if (consumer.ConsumerOwned.Count == 0)
			{
				continue;
			}

			int index = i;
			string[] listed = consumer.ConsumerOwned.AsArray().Distinct(StringComparer.Ordinal).ToArray();
			HashSet<string> applied = new(StringComparer.Ordinal);

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
					new EquatableArray<string>([DisplayInstance(consumer.ImplementationType), Display(service),])));
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
					.Select(key => serviceToIndex.TryGetValue(key, out int target) ? target : -1)
					.Where(target => target >= 0 && (instances[target].Lifetime == Lifetime.Transient || instances[target].IsParameterized))
					.ToArray();
				if (targets.Length == 0)
				{
					return parameter;
				}

				applied.UnionWith(entries);
				owned.Add((index, parameter, targets));
				return parameter with { ConsumerOwned = true, };
			}
		}

		HashSet<string> reported = new(StringComparer.Ordinal);
		foreach ((int consumer, ParameterModel parameter, int[] targets) in owned)
		{
			foreach (int target in targets)
			{
				if (ConsumerOwnedFault(instances, serviceToIndex, membership, target) is { } fault
				    && reported.Add($"{consumer}|{parameter.ServiceType}|{target}"))
				{
					diagnostics.Add(new DiagnosticInfo(
						Diagnostics.ConsumerOwnedBuildsMore,
						parameter.Location ?? instanceLocations[consumer],
						new EquatableArray<string>([
							DisplayInstance(instances[consumer].ImplementationType),
							Display(parameter.ServiceType),
							DisplayInstance(instances[target].ImplementationType),
							fault,
						])));
				}
			}
		}
	}

	/// <summary>
	///     Whether the <c>ConsumerOwned</c> <paramref name="entry" /> names <paramref name="service" />: the type itself, or
	///     for a generic type definition such as <c>typeof(Repository&lt;&gt;)</c> each of its closings.
	/// </summary>
	private static bool Lists(string entry, string service)
		=> entry == service || (entry.Contains("<>") || entry.Contains("<,")) && entry == Unbound(service);

	/// <summary>
	///     The display of the generic type definition of a closed type such as <c>global::Ns.Outer&lt;int&gt;.Inner&lt;(int, string)&gt;</c>,
	///     as <c>typeof</c> displays it: <c>global::Ns.Outer&lt;&gt;.Inner&lt;&gt;</c>. A comma inside a tuple or an array
	///     rank is no argument separator.
	/// </summary>
	private static string Unbound(string type)
	{
		StringBuilder unbound = new(type.Length);
		int angles = 0, nested = 0;
		foreach (char c in type)
		{
			switch (c)
			{
				case '<':
					if (angles++ == 0)
					{
						unbound.Append('<');
					}

					break;
				case '>':
					if (--angles == 0)
					{
						unbound.Append('>');
					}

					break;
				case '(' or '[' when angles > 0:
					nested++;
					break;
				case ')' or ']' when angles > 0:
					nested--;
					break;
				case ',' when angles == 1 && nested == 0:
					unbound.Append(',');
					break;
				default:
					if (angles == 0)
					{
						unbound.Append(c);
					}

					break;
			}
		}

		return unbound.ToString();
	}
}
