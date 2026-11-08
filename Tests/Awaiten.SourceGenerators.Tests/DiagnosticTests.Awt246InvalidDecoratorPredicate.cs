namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt246InvalidDecoratorPredicate
	{
		[Test]
		public async Task ReportsWhenNoMemberOfThatNameExists()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Decorate<MonitoringClient, IClient>(When = "Missing")]
			                                                 """, ""));

			await That(result.Diagnostics).Contains("*AWT246*'MyCode.MyContainer.Missing'*has no member 'Missing'*").AsWildcard();
		}

		[Test]
		public async Task ReportsWhenThePredicateDoesNotReturnBool()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                                 """, "private static int Enabled() => 1;"));

			await That(result.Diagnostics).Contains("*AWT246*returns 'int', not bool*").AsWildcard();
		}

		[Test]
		[Arguments("System.Threading.Tasks.Task<bool>", "System.Threading.Tasks.Task.FromResult(true)")]
		[Arguments("System.Threading.Tasks.ValueTask<bool>", "new System.Threading.Tasks.ValueTask<bool>(true)")]
		public async Task ReportsAnAsynchronousPredicate(string type, string value)
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                                 """, $"private static {type} Enabled() => {value};"));

			await That(result.Diagnostics).Contains("*AWT246*a predicate is evaluated synchronously; compute the value before creating the root*").AsWildcard();
		}

		[Test]
		public async Task ReportsAGenericMethod()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                                 """, "private static bool Enabled<T>() => true;"));

			await That(result.Diagnostics).Contains("*AWT246*'Enabled' is generic*").AsWildcard();
		}

		[Test]
		public async Task ReportsAPropertyWithoutGetter()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                                 """, "private static bool Enabled { set { } }"));

			await That(result.Diagnostics).Contains("*AWT246*'Enabled' has no getter*").AsWildcard();
		}

		[Test]
		public async Task ReportsTwoMethodsOfThatNameReturningBool()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Singleton<Settings>]
			                                                 [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                                 """, """
			                                                      private static bool Enabled() => true;
			                                                      private static bool Enabled(Settings settings) => true;
			                                                      """));

			await That(result.Diagnostics).Contains("*AWT246*several 'Enabled' methods returning bool*").AsWildcard()
				.Because("picking one overload would depend on declaration order");
		}

		[Test]
		public async Task ReportsOnTheOpenGenericForm()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IHandler<T> { }
			                                       public sealed class Handler<T> : IHandler<T> { }
			                                       public sealed class TracingHandler<T> : IHandler<T> { public TracingHandler(IHandler<T> inner) { } }

			                                       [Container]
			                                       [Transient<Handler<string>, IHandler<string>>]
			                                       [Decorate(typeof(TracingHandler<>), typeof(IHandler<>), When = "Missing")]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT246*").AsWildcard();
		}

		[Test]
		public async Task ReportsAPredicateOfAModuleDecoratorThatOnlyTheContainerDeclares()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                       [Module]
			                                       [Singleton<Client, IClient>]
			                                       [Decorate<MonitoringClient, IClient>(When = "Enabled")]
			                                       public static partial class ClientModule
			                                       {
			                                       }

			                                       [Container]
			                                       [Import(typeof(ClientModule))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled() => true;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT246*the module 'MyCode.ClientModule' has no member 'Enabled'*").AsWildcard()
				.Because("a predicate is looked up on the type that declares the [Decorate], never on the container");
		}

		[Test]
		[Arguments("private static bool Enabled() => true;")]
		[Arguments("private static bool Enabled => true;")]
		[Arguments("private static readonly bool Enabled = true;")]
		[Arguments("internal const bool Enabled = true;")]
		public async Task DoesNotReportAStaticBoolMethodPropertyOrField(string member)
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                                 """, member));

			await That(result.Diagnostics).IsEmpty();
		}

		private static string Container(string attributes, string members)
			=> $$"""
			     using Awaiten;

			     namespace MyCode;

			     public interface IClient { }
			     public sealed class Settings { }
			     public sealed class Client : IClient { }
			     public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			     [Container]
			     [Singleton<Client, IClient>]
			     {{attributes}}
			     public static partial class MyContainer
			     {
			     	{{members}}
			     }
			     """;
	}
}
