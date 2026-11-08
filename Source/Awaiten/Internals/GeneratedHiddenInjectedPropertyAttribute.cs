using System;
using System.ComponentModel;

namespace Awaiten.Internals;

/// <summary>
///     Infrastructure for generated code. Emitted by the source generator into every assembly that declares an
///     <c>[Inject]</c> property other assemblies cannot see, one per property, and read back by a consuming container.
///     The compiler shows another assembly neither the private nor, without <c>InternalsVisibleTo</c>, the internal
///     members of a type, and a reference assembly leaves them out entirely, so a container that constructs the type
///     itself would build it without the property; this marker lets it report the property instead.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedHiddenInjectedPropertyAttribute : Attribute
{
	/// <summary>Records the hidden <c>[Inject]</c> property <paramref name="property" /> of <paramref name="type" />.</summary>
	/// <param name="type">The metadata name of the type that declares the property.</param>
	/// <param name="property">
	///     The metadata name of the property, which for an explicit interface implementation is qualified with the
	///     interface.
	/// </param>
	/// <param name="assignable">
	///     Whether a container in the declaring assembly can assign the property, because it and its setter are internal.
	/// </param>
	public GeneratedHiddenInjectedPropertyAttribute(string type, string property, bool assignable)
	{
		Type = type;
		Property = property;
		Assignable = assignable;
	}

	/// <summary>The metadata name of the type that declares the property.</summary>
	public string Type { get; }

	/// <summary>The metadata name of the property.</summary>
	public string Property { get; }

	/// <summary>
	///     Whether a container in the declaring assembly can assign the property: then a module of that assembly, or
	///     <c>InternalsVisibleTo</c>, lets a container in another assembly fill it too (AWT244). Otherwise no container
	///     can assign it (AWT136).
	/// </summary>
	public bool Assignable { get; }
}
