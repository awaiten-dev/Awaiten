namespace Awaiten.AotTests.Domain;

/// <summary>
///     A service built from a runtime argument, whose by-type miss inspects the requested type to name its factory.
/// </summary>
public sealed class Ticket([Arg] int number)
{
	public int Number { get; } = number;
}
