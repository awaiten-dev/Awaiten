using System.Text;
using Awaiten.SourceGenerators.Entities;

namespace Awaiten.SourceGenerators;

internal static partial class Sources
{
	/// <summary>The distinct predicates of the conditional decorator chain links, in order of first use.</summary>
	private static List<PredicateModel> DistinctPredicates(InstanceModel[] instances)
	{
		List<PredicateModel> predicates = new();
		foreach (InstanceModel instance in instances)
		{
			if (instance.When is { } predicate && !predicates.Exists(known => known.Member == predicate.Member))
			{
				predicates.Add(predicate);
			}
		}

		return predicates;
	}

	/// <summary>
	///     Emits the <c>Root</c> field of each predicate: a <c>readonly bool</c> the constructor sets for a parameterless
	///     one, and for one that takes services a volatile state (0 unread, 1 false, 2 true) that
	///     <see cref="EmitLazyPredicates" /> sets on first use.
	/// </summary>
	private static void EmitPredicateFields(StringBuilder builder, int depth, List<PredicateModel> predicates)
	{
		for (int p = 0; p < predicates.Count; p++)
		{
			if (p == 0)
			{
				Separate(builder);
			}

			Indent(builder, depth).Append(predicates[p].IsLazy ? "private volatile int " : "internal readonly bool ").Append(PredicateField(p)).AppendLine(";");
		}
	}

	/// <summary>
	///     Emits the <c>Root</c> constructor's evaluation of each parameterless predicate, before any eager singleton is
	///     built, so every link reads the same value for the lifetime of the root.
	/// </summary>
	private static void EmitPredicateEvaluation(StringBuilder builder, int depth, List<PredicateModel> predicates)
	{
		for (int p = 0; p < predicates.Count; p++)
		{
			if (!predicates[p].IsLazy)
			{
				Indent(builder, depth).Append(PredicateField(p)).Append(" = ").Append(predicates[p].Member).Append(predicates[p].Invoked ? "()" : "").AppendLine(";");
			}
		}
	}

	/// <summary>
	///     Emits the <c>Root</c> method of each predicate that takes services: it resolves them and evaluates the predicate
	///     once, under <c>__gate</c> like a singleton, and afterwards returns the stored result without locking. A predicate
	///     that throws stores nothing, so the next use evaluates it again.
	/// </summary>
	private static void EmitLazyPredicates(StringBuilder builder, int depth, EmitContext context)
	{
		for (int p = 0; p < context.Predicates.Count; p++)
		{
			PredicateModel predicate = context.Predicates[p];
			if (!predicate.IsLazy)
			{
				continue;
			}

			string field = "__s." + PredicateField(p);
			string arguments = string.Join(", ", predicate.Parameters.AsArray()
				.Select(parameter => DependencyValue(parameter, context.Instances, context.Names, context.ServiceToIndex, asynchronous: false)));
			Separate(builder);
			AppendXmlSummary(builder, depth, $"Evaluates the decorator predicate <c>{AwaitenGenerator.Display(predicate.Member)}</c> once per root.");
			Indent(builder, depth).Append("internal static bool ").Append(PredicateMethod(p)).AppendLine("(Root __s)");
			Indent(builder, depth).AppendLine("{");
			Indent(builder, depth + 1).Append("int __state = ").Append(field).AppendLine(";");
			Indent(builder, depth + 1).AppendLine("if (__state == 0)");
			Indent(builder, depth + 1).AppendLine("{");
			Indent(builder, depth + 2).AppendLine("lock (__s.__gate)");
			Indent(builder, depth + 2).AppendLine("{");
			Indent(builder, depth + 3).Append("__state = ").Append(field).AppendLine(";");
			Indent(builder, depth + 3).AppendLine("if (__state == 0)");
			Indent(builder, depth + 3).AppendLine("{");
			Indent(builder, depth + 4).Append("__state = ").Append(predicate.Member).Append('(').Append(arguments).AppendLine(") ? 2 : 1;");
			Indent(builder, depth + 4).Append(field).AppendLine(" = __state;");
			Indent(builder, depth + 3).AppendLine("}");
			Indent(builder, depth + 2).AppendLine("}");
			Indent(builder, depth + 1).AppendLine("}");
			builder.AppendLine();
			Indent(builder, depth + 1).AppendLine("return __state == 2;");
			Indent(builder, depth).AppendLine("}");
		}
	}

	/// <summary>
	///     Emits the guard at the top of a conditional decorator chain link's resolver: when its predicate is
	///     <see langword="false" />, the link returns its inner instance, so the decorator is never built, tracked or
	///     initialized and its own dependencies are never resolved. Emitted nowhere else, so an unconditional link is unchanged.
	/// </summary>
	private static void EmitPredicateGuard(StringBuilder builder, int depth, InstanceModel instance, EmitContext context, bool asynchronous)
	{
		if (instance.When is not { } predicate)
		{
			return;
		}

		int index = context.Predicates.FindIndex(known => known.Member == predicate.Member);
		ParameterModel inner = instance.ConstructorParameters.AsArray()
			.First(parameter => parameter.Key?.StartsWith(AwaitenGenerator.DecoratorKeyPrefix, StringComparison.Ordinal) == true);
		string enabled = predicate.IsLazy ? $"Root.{PredicateMethod(index)}(__s.__root)" : $"__s.__root.{PredicateField(index)}";
		Indent(builder, depth).Append("if (!").Append(enabled).AppendLine(")");
		Indent(builder, depth).AppendLine("{");
		Indent(builder, depth + 1).Append("return ").Append(DependencyValue(inner, context.Instances, context.Names, context.ServiceToIndex, asynchronous)).AppendLine(";");
		Indent(builder, depth).AppendLine("}");
		builder.AppendLine();
	}

	private static string PredicateField(int index) => "__when" + index;

	private static string PredicateMethod(int index) => "__When" + index;
}
