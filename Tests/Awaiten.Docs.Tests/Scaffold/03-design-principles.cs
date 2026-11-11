// The architecture test runs in a TUnit test project.
global using TUnit.Core;
global using System.Security.Principal;

namespace Snippets;

// The ambient logger that the page warns against.
public static class Log
{
	public static void Info(string message)
	{
	}
}

// The plain barista that the module example's composition root builds. The page shows it in several blocks, which
// keeps each of those declarations local to its block.
public sealed class Barista(ITimeSystem timeSystem, IPaymentTerminal terminal);
