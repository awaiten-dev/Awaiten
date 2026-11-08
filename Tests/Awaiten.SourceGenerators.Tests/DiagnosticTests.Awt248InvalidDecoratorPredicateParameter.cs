using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt248InvalidDecoratorPredicateParameter
	{
		[Test]
		[Arguments("[Arg] Settings settings", "it is a runtime [Arg], which no caller supplies")]
		[Arguments("in Settings settings", "it is passed by reference")]
		[Arguments("System.Func<Settings> settings", "it is a relationship or a collection")]
		[Arguments("System.Lazy<Settings> settings", "it is a relationship or a collection")]
		[Arguments("Settings[] settings", "it is a relationship or a collection")]
		public async Task ReportsAParameterTheRootCannotSupply(string parameter, string reason)
		{
			GeneratorResult result = Generator.Run(Container("[Singleton<Settings>]", parameter));

			await That(result.Diagnostics).Contains($"*AWT248*'MyCode.MyContainer.Enabled' cannot take*{reason}*").AsWildcard();
		}

		[Test]
		[Arguments("[Scoped<Settings>]", "it is scoped")]
		[Arguments("[Transient<Settings>]", "it is transient")]
		public async Task ReportsAServiceTheRootDoesNotShare(string registration, string reason)
		{
			GeneratorResult result = Generator.Run(Container(registration, "Settings settings"));

			await That(result.Diagnostics).Contains($"*AWT248*cannot take 'MyCode.Settings': {reason}*").AsWildcard();
		}

		[Test]
		public async Task ReportsAnAsyncInitializedSingleton()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                                 [Singleton<Settings>]
			                                                 """, "Settings settings", "Settings : IInitializable { public System.Threading.Tasks.Task InitializeAsync() => System.Threading.Tasks.Task.CompletedTask; }"));

			await That(result.Diagnostics).Contains("*AWT248*cannot take 'MyCode.Settings': it is initialized asynchronously, but a predicate is evaluated synchronously*").AsWildcard();
		}

		[Test]
		public async Task NamesTheAsyncInitializedServiceASingletonDependsOn()
		{
			GeneratorResult result = Generator.Run(Container("""
			                                                 [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                                 [Singleton<Store>]
			                                                 [Singleton<Settings>]
			                                                 """, "Settings settings", """
			                                                                          Settings { public Settings(Store store) { } }
			                                                                          public sealed class Store : IInitializable { public System.Threading.Tasks.Task InitializeAsync() => System.Threading.Tasks.Task.CompletedTask; }
			                                                                          """));

			await That(result.Diagnostics).Contains("*AWT248*cannot take 'MyCode.Settings': it depends on the async-initialized service 'MyCode.Store', but a predicate is evaluated synchronously*").AsWildcard()
				.Because("the singleton itself is not initialized asynchronously, so the message names the service that is");
		}

		[Test]
		public async Task NamesTheDeclaredTypeOfAParameterAVarianceRedirectServes()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface ISource<out T> { }
			                                       public class Settings { }
			                                       public sealed class RegionSettings : Settings { }
			                                       public sealed class RegionSource : ISource<RegionSettings> { }
			                                       public interface IClient { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                       [Container]
			                                       [Scoped<RegionSource, ISource<RegionSettings>>]
			                                       [Scoped<Client, IClient>]
			                                       [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled(ISource<Settings> source) => true;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT248*cannot take 'MyCode.ISource<MyCode.Settings>': it is scoped*").AsWildcard()
				.Because("the diagnostic names the parameter as the predicate declares it, not the registration the variance redirect found");
		}

		[Test]
		[Arguments("[Scoped<Settings>]", "")]
		[Arguments("[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))] [Singleton<Settings>]", " : IInitializable")]
		public async Task IsTheOnlyDiagnosticOfAnUnsharedServiceOfAnEagerSingleton(string registration, string settingsBase)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System.Threading.Tasks;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public interface IInitializable { Task InitializeAsync() => Task.CompletedTask; }
			                                         public interface IClient { }
			                                         public sealed class Settings{{settingsBase}} { }
			                                         public sealed class Client : IClient { }
			                                         public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                         [Container]
			                                         {{registration}}
			                                         [Singleton<Client, IClient>(Eager = true)]
			                                         [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                         public static partial class MyContainer
			                                         {
			                                         	private static bool Enabled(Settings settings) => true;
			                                         }
			                                         """);

			await That(result.Diagnostics).HasCount(1)
				.Because("a predicate service is no captive dependency or async taint of the link, so the one cause is reported once");
			await That(result.Diagnostics[0]).Contains("AWT248");
		}

		[Test]
		public async Task ReportsOncePerPredicate_WhenSeveralDecoratorsShareIt()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public interface IGateway { }
			                                       public sealed class Settings { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class Gateway : IGateway { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }
			                                       public sealed class TracedGateway : IGateway { public TracedGateway(IGateway inner) { } }

			                                       [Container]
			                                       [Scoped<Settings>]
			                                       [Scoped<Client, IClient>]
			                                       [Scoped<Gateway, IGateway>]
			                                       [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                       [Decorate<TracedGateway, IGateway>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled(Settings settings) => true;
			                                       }
			                                       """);

			await That(result.Diagnostics.Count(diagnostic => diagnostic.Contains("AWT248"))).IsEqualTo(1);
		}

		[Test]
		[Arguments("[Singleton<Settings>]", "Settings settings")]
		[Arguments("[Singleton<Settings>(Instance = nameof(Current))]", "Settings settings")]
		[Arguments("[Singleton<Settings>(Key = \"eu\")]", "[FromKey(\"eu\")] Settings settings")]
		[Arguments("[ImportService<Settings>]", "Settings settings")]
		public async Task DoesNotReportASharedSynchronousService(string registration, string parameter)
		{
			GeneratorResult result = Generator.Run(Container(registration, parameter));

			await That(result.Diagnostics).IsEmpty();
		}

		private static string Container(string registrations, string parameter, string settings = "Settings { }")
			=> $$"""
			     using Awaiten;

			     namespace MyCode;

			     public interface IInitializable { System.Threading.Tasks.Task InitializeAsync(); }
			     public interface IClient { }
			     public sealed class {{settings}}
			     public sealed class Client : IClient { }
			     public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			     [Container]
			     {{registrations}}
			     [Scoped<Client, IClient>]
			     [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			     public static partial class MyContainer
			     {
			     	internal static Settings Current { get; } = new();

			     	private static bool Enabled({{parameter}}) => true;
			     }
			     """;
	}
}
