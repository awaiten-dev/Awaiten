using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The generated code compiles under the oldest language version the generator accepts and under every
///     project-level nullable context, since it enables nullable annotations only locally.
/// </summary>
public class LanguageVersionTests
{
	private const string Shop = """
		using System;
		using System.Collections.Generic;
		using System.Threading;
		using System.Threading.Tasks;
		using Awaiten;

		namespace Shop
		{
		    public interface IClock { DateTime Now { get; } }
		    public sealed class Clock : IClock { public DateTime Now => DateTime.UtcNow; }

		    public interface IRepository<T> { }
		    public sealed class Repository<T> : IRepository<T> { }

		    public sealed class LoggingRepository<T> : IRepository<T> { public LoggingRepository(IRepository<T> inner) { } }

		    public interface INotifier<T> { }
		    public sealed class EmailNotifier<T> : INotifier<T> { }
		    public sealed class SmsNotifier<T> : INotifier<T> { }
		    public sealed class CompositeNotifier<T> : INotifier<T> { public CompositeNotifier(IEnumerable<INotifier<T>> notifiers) { } }

		    public interface IBrewer { string Brew(); }
		    public sealed class Brewer : IBrewer { public string Brew() => "coffee"; }

		    public interface IGrinder { }
		    public sealed class FineGrinder : IGrinder { }
		    public sealed class CoarseGrinder : IGrinder { }

		    public interface ISyrup { }
		    public sealed class Vanilla : ISyrup { }
		    public sealed class Caramel : ISyrup { }

		    public interface IPaymentTerminal { }
		    public sealed class PaymentTerminal : IPaymentTerminal, IAsyncDisposable { public ValueTask DisposeAsync() => default; }

		    public interface IHandler { }
		    public sealed class OrderHandler : IHandler { }
		    public sealed class RefundHandler : IHandler { }

		    public interface IListener<in T> { }
		    public sealed class ObjectListener : IListener<object> { }

		    public sealed class Ticket { public Ticket([Arg] string name, IClock clock) { } }
		    public sealed class Counter : IDisposable { public void Dispose() { } }
		    public sealed class Oven { public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
		    public interface ILogger { }

		    public sealed class Barista
		    {
		        public Barista(
		            IBrewer brewer,
		            IEnumerable<IGrinder> grinders,
		            INotifier<Ticket> notifier,
		            IRepository<Counter> repository,
		            Func<IClock> clock,
		            Lazy<IClock> lazyClock,
		            Owned<Counter> counter,
		            Task<IPaymentTerminal> terminal,
		            Task<Oven> oven,
		            Func<string, Ticket> ticket,
		            [FromKey("vanilla")] ISyrup syrup,
		            IReadOnlyDictionary<string, ISyrup> syrups,
		            IEnumerable<IHandler> handlers,
		            IListener<string> listener,
		            ILogger logger) { }

		        [Inject] public IClock Clock { get; set; } = null!;
		    }

		    public sealed class Menu { public Menu(IBrewer brewer) { } }

		    [Module]
		    [Singleton(typeof(Clock), typeof(IClock), Eager = true)]
		    public static class ClockModule { }

		    [Container]
		    [Import(typeof(ClockModule))]
		    [ImportService(typeof(ILogger))]
		    [Scoped(typeof(Repository<>), typeof(IRepository<>))]
		    [Singleton(typeof(Brewer), typeof(IBrewer))]
		    [Decorate(typeof(LoggingRepository<>), typeof(IRepository<>))]
		    [Singleton(typeof(FineGrinder), typeof(IGrinder))]
		    [Singleton(typeof(CoarseGrinder), typeof(IGrinder))]
		    [Singleton(typeof(EmailNotifier<>), typeof(INotifier<>))]
		    [Singleton(typeof(SmsNotifier<>), typeof(INotifier<>))]
		    [Composite(typeof(CompositeNotifier<>), typeof(INotifier<>))]
		    [Singleton(typeof(Vanilla), typeof(ISyrup), Key = "vanilla")]
		    [Singleton(typeof(Caramel), typeof(ISyrup), Key = "caramel")]
		    [Singleton(typeof(IPaymentTerminal), Factory = nameof(ConnectAsync))]
		    [Singleton(typeof(Oven), OnActivated = nameof(WarmAsync))]
		    [Scan(typeof(IHandler), As = ScanAs.Marker)]
		    [Singleton(typeof(ObjectListener), typeof(IListener<object>))]
		    [Transient(typeof(Ticket))]
		    [Scoped(typeof(Counter), OnRelease = nameof(Release))]
		    [Transient(typeof(Barista))]
		    public static partial class CoffeeShop
		    {
		        internal static async Task<IPaymentTerminal> ConnectAsync(CancellationToken cancellationToken)
		        {
		            await Task.Yield();
		            return new PaymentTerminal();
		        }

		        internal static Task WarmAsync(Oven oven, CancellationToken cancellationToken) => oven.WarmAsync(cancellationToken);

		        internal static void Release(Counter counter) { }
		    }

		    [Container(Extends = typeof(CoffeeShop))]
		    [Transient(typeof(Menu))]
		    public static partial class Franchise { }

		    public static class Usage
		    {
		        public static async Task<Barista> ServeAsync(CancellationToken cancellationToken)
		        {
		            await using (CoffeeShop.Root shop = new CoffeeShop.Root())
		            {
		                await using (IAwaitenScope scope = await shop.CreateScopeAsync(cancellationToken))
		                {
		                    return await scope.ResolveAsync<Barista>(cancellationToken);
		                }
		            }
		        }
		    }
		}
		""";

	[Test]
	public async Task ABroadContainer_CompilesAtCSharp9()
	{
		GeneratorResult result = Generator.Run(LanguageVersion.CSharp9, Shop);

		await That(result.Diagnostics).IsEmpty()
			.Because("C# 9 is the oldest version the generator emits container code for");
		await That(result.Sources).IsNotEmpty();
	}

	[Test]
	public async Task ABroadContainer_ReportsOnlyAwt243AtCSharp8()
	{
		GeneratorResult result = Generator.Run(LanguageVersion.CSharp8, Shop);

		await That(result.Diagnostics.Where(diagnostic => !diagnostic.Contains("AWT243") && !diagnostic.Contains("CS0426"))).IsEmpty()
			.Because("besides the diagnostic, only the usage of the Root that was not generated fails");
		await That(result.Diagnostics).Contains("*AWT243*Shop.CoffeeShop*").AsWildcard();
		await That(result.Diagnostics).Contains("*AWT243*Shop.Franchise*").AsWildcard();
	}

	[Test]
	[Arguments(NullableContextOptions.Disable)]
	[Arguments(NullableContextOptions.Warnings)]
	[Arguments(NullableContextOptions.Annotations)]
	[Arguments(NullableContextOptions.Enable)]
	public async Task ABroadContainer_CompilesWithoutWarningsUnderEveryNullableContext(NullableContextOptions nullable)
	{
		GeneratorResult result = Generator.Run(LanguageVersion.Latest, Shop, nullable);

		await That(result.Diagnostics).IsEmpty()
			.Because("the generated code enables nullable annotations itself, whatever the project sets");
	}

	[Test]
	public async Task AModuleWithInternalServicesInAnotherAssembly_CompilesAtCSharp9()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
			LanguageVersion.CSharp9,
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib
			{
			    public interface IGrinder { }
			    internal sealed class BeanStore { public Task FillAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
			    internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			    [Module]
			    [Singleton(typeof(BeanStore), OnActivated = nameof(FillAsync))]
			    [Singleton(typeof(Grinder), typeof(IGrinder))]
			    public static partial class CoffeeModule
			    {
			        internal static Task FillAsync(BeanStore store) => store.FillAsync(CancellationToken.None);
			    }
			}
			""",
			LanguageVersion.CSharp9,
			"""
			using System.Threading.Tasks;
			using Awaiten;

			namespace App
			{
			    [Container]
			    [Import(typeof(Lib.CoffeeModule))]
			    public static partial class AppContainer { }

			    public static class Usage
			    {
			        public static Task<Lib.IGrinder> GrinderAsync(AppContainer.Root root) => root.ResolveAsync<Lib.IGrinder>();
			    }
			}
			""");

		await That(result.Diagnostics).IsEmpty()
			.Because("the handles and factories a module generates compile at C# 9 in both assemblies");
	}
}
