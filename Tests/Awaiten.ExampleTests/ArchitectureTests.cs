using aweXpect.Reflection;
using CoffeeShop;

namespace Awaiten.ExampleTests;

/// <summary>
///     Architecture test from the "Design principles &amp; the composition root" docs: domain and application
///     code must never reference Awaiten. Only the composition root is allowed to, and the domain's own module in
///     its <c>CoffeeShop.Composition</c> namespace. This turns the convention into a red build the day someone
///     crosses the line, even where AWT134 cannot see it.
/// </summary>
public class ArchitectureTests
{
	[Test]
	public async Task Domain_has_no_reference_to_Awaiten()
	{
		var domain = In.AssemblyContaining<Barista>().Types()
			.WhichAreNotWithinNamespace("CoffeeShop.Composition");

		await That(domain)
			.DoNotDependOn(Types.InNamespace("Awaiten"));
	}
}
