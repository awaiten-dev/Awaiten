global using static Snippets.Prelude;
using Microsoft.Extensions.DependencyInjection;

namespace Snippets;

// The host's variables, which the page takes from the surrounding startup code.
public static class Prelude
{
	public static string[] args = [];
	public static IServiceCollection services = default!;
	public static IServiceProvider provider = default!;
	public static Microsoft.AspNetCore.Builder.WebApplicationBuilder builder = default!;
	public static IExternalResolver myResolver = default!;
}
