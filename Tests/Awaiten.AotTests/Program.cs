using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Awaiten.AotTests.Domain;
using Awaiten.Extensions.DependencyInjection;
using CoffeeShop;
using Microsoft.Extensions.DependencyInjection;

namespace Awaiten.AotTests;

public static class Program
{
	public static async Task<int> Main()
	{
		ServiceCollection services = new();
		services.AddSingleton<Banner>();
		services.AddAwaitenContainer<SampleContainer.Root>();

		using ServiceProvider provider = services.BuildServiceProvider(true);
		provider.VerifyAwaitenContainers();

		using IServiceScope scope = provider.CreateScope();
		Report report = scope.ServiceProvider.GetRequiredService<Report>();
		string rendered = report.Render();
		Console.WriteLine(rendered);

		// Async path: an async-initialized service is bridged as Task<T> and initialized once. This is what
		// previously forced runtime MakeGenericType/MakeGenericMethod.
		Warmup warmup = await scope.ServiceProvider.GetRequiredService<Task<Warmup>>();
		Console.WriteLine($"warmup ready: {warmup.Ready}");

		string reply = scope.ServiceProvider.GetRequiredService<Mediator>().Send(new Ping());
		Console.WriteLine($"dispatched by runtime type: {reply}");

		// A module of another assembly builds its internal implementation through the factory it generated.
		Receipt receipt = scope.ServiceProvider.GetRequiredService<Barista>().Serve(2.40m);
		Console.WriteLine($"charged through the domain module: {receipt.Amount}");

		// The provider-replacement path's empty-collection answer, and the one place the bridge constructs a type
		// at run time: the T[] backing IEnumerable<T> for an element type the container never saw. Native AOT
		// generates that array type on demand for a reference element type, which is why the bridge suppresses the
		// dynamic-code warning there, and running it here is what keeps that suppression honest rather than
		// asserted.
		SampleContainer.Root root = provider.GetRequiredService<SampleContainer.Root>();
		using AwaitenServiceProvider replacement = new(root, false);
		IEnumerable<Unmentioned>? unmentioned =
			(IEnumerable<Unmentioned>?)replacement.GetService(typeof(IEnumerable<Unmentioned>));
		bool emptySequence = unmentioned is not null && !unmentioned.Any();
		Console.WriteLine($"empty sequence for an unmentioned element type: {emptySequence}");

		// A by-type miss over a service with a runtime argument unwraps the requested relationship to name its factory.
		// The type travels in a variable, as it does for a caller that only knows it at run time, where AWT218 cannot see it.
		Type requested = typeof(Lazy<Ticket>);
		string guidance = string.Empty;
		try
		{
			root.Resolve(requested);
		}
		catch (InvalidOperationException exception)
		{
			guidance = exception.Message;
		}

		bool namesFactory = guidance.Contains("System.Func<int, Awaiten.AotTests.Domain.Ticket>");
		Console.WriteLine($"miss names the factory: {namesFactory}");

		return rendered == "Awaiten on AOT @ 2026-06-24" && warmup.Ready && reply == "pong" && receipt.Amount == 2.40m && emptySequence && namesFactory ? 0 : 1;
	}
}
