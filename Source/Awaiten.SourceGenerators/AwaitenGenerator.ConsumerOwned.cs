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
	///     instance, and AWT262 for an entry that has no effect in any closing of the consumer's registration: it marks
	///     nothing, or only targets the consumer cannot own (see <see cref="ConsumerCanOwn" />) whose build leaves nothing
	///     on the owner. A singleton or scoped target is shared rather than built per call, so the consumer never owns it.
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
		List<(int Consumer, ParameterModel Parameter, int[] Targets, string[] Entries)> owned = new();
		// The closings of one open registration share its location, so an entry is reported only when no closing uses it.
		List<(LocationInfo? Location, string Registration, int Index)> registrationOrder = new();
		Dictionary<(LocationInfo? Location, string Registration, int Index), (int Consumer, int Closings, string[] Listed, HashSet<string> Applied)> registrations = new();
		Dictionary<int, (LocationInfo? Location, string Registration, int Index)> registrationOf = new();
		for (int i = 0; i < instances.Count; i++)
		{
			InstanceModel consumer = instances[i];
			if (consumer.ConsumerOwned.Count == 0)
			{
				continue;
			}

			int index = i;
			string[] listed = consumer.ConsumerOwned.AsArray().Distinct(StringComparer.Ordinal).ToArray();
			(LocationInfo? Location, string Registration, int Index) key = instanceLocations[i] is null
				? (null, consumer.ImplementationType, i)
				: (instanceLocations[i], Unbound(consumer.ImplementationType), -1);
			registrationOf[i] = key;
			if (registrations.TryGetValue(key, out (int Consumer, int Closings, string[] Listed, HashSet<string> Applied) registration))
			{
				registrations[key] = registration with { Closings = registration.Closings + 1, };
			}
			else
			{
				registrationOrder.Add(key);
				registrations[key] = (i, 1, listed, new HashSet<string>(StringComparer.Ordinal));
			}

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

				owned.Add((index, parameter, targets, entries));
				return parameter with { ConsumerOwned = true, };
			}
		}

		HashSet<string> reported = new(StringComparer.Ordinal);
		foreach ((int consumer, ParameterModel parameter, int[] targets, string[] entries) in owned)
		{
			// A target the consumer cannot own whose build leaves nothing on the owner makes the entry a no-op (AWT262).
			bool applies = false;
			foreach (int target in targets)
			{
				string? fault = ConsumerOwnedFault(instances, serviceToIndex, membership, target);
				applies |= fault is not null || ConsumerCanOwn(instances[target]);
				if (fault is not null && reported.Add($"{consumer}|{parameter.ServiceType}|{target}"))
				{
					string consumerDisplay = DisplayInstance(instances[consumer].ImplementationType);
					string service = Display(parameter.ServiceType);
					// A scoped or transient consumer is already bounded by its owner, so dropping the entry is enough.
					string remedy = instances[consumer].Lifetime == Lifetime.Singleton
						? $"remove '{service}' from ConsumerOwned and take an Owned<T> handle through the Func, or register '{consumerDisplay}' as Scoped"
						: $"remove '{service}' from ConsumerOwned, so the container disposes what the Func builds, or take an Owned<T> handle through the Func";
					diagnostics.Add(new DiagnosticInfo(
						Diagnostics.ConsumerOwnedBuildsMore,
						parameter.Location ?? instanceLocations[consumer],
						new EquatableArray<string>([
							consumerDisplay,
							service,
							DisplayInstance(instances[target].ImplementationType),
							fault,
							remedy,
						])));
				}
			}

			if (applies)
			{
				registrations[registrationOf[consumer]].Applied.UnionWith(entries);
			}
		}

		foreach ((LocationInfo? Location, string Registration, int Index) key in registrationOrder)
		{
			(int consumer, int closings, string[] listed, HashSet<string> applied) = registrations[key];
			string display = DisplayInstance(closings == 1 ? instances[consumer].ImplementationType : key.Registration);
			foreach (string service in listed.Where(service => !applied.Contains(service)))
			{
				diagnostics.Add(new DiagnosticInfo(
					Diagnostics.ConsumerOwnedNeverApplies,
					key.Location ?? instanceLocations[consumer],
					new EquatableArray<string>([display, Display(service),])));
			}
		}
	}

	/// <summary>
	///     Whether a consumer can own what a <c>Func</c> over <paramref name="target" /> builds: the target is built per
	///     call (transient or parameterized) and its owner would track it for disposal. A shared instance, or one the
	///     owner never disposes (not disposable, or <c>SuppressDisposal</c>), stays as it is.
	/// </summary>
	internal static bool ConsumerCanOwn(InstanceModel target)
		=> (target.Lifetime == Lifetime.Transient || target.IsParameterized)
		   && !target.SuppressDisposal
		   && (target.NeedsDisposal || target.RuntimeDisposalCheck);

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

		string definition = entry.Replace(" ", "");
		return (definition.Contains("<>") || definition.Contains("<,")) && definition == Unbound(service);
	}

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
