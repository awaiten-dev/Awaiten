namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt263EagerWithEagerWhen
	{
		[Test]
		public async Task ReportsEagerTogetherWithEagerWhen()
		{
			GeneratorResult result = Generator.Run(Container("[Singleton<Client, IClient>(Eager = true, EagerWhen = nameof(Enabled))]"));

			await That(result.Diagnostics).HasSingle().Which.EndsWith("error AWT263: 'MyCode.Client' sets both Eager = true and EagerWhen, but Eager builds it at startup in every run, so the predicate never decides; keep EagerWhen to build it only when the predicate allows it, or Eager to build it always");
		}

		[Test]
		public async Task ReportsEagerTogetherWithEagerWhenOnAModuleRegistration()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public sealed class Client : IClient { }

			                                       [Module]
			                                       [Singleton<Client, IClient>(Eager = true, EagerWhen = nameof(Enabled))]
			                                       public static partial class ClientModule
			                                       {
			                                       	internal static bool Enabled => true;
			                                       }

			                                       [Container]
			                                       [Import(typeof(ClientModule))]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Contains("*error AWT263*'MyCode.Client'*").AsWildcard();
		}

		[Test]
		[Arguments("[Singleton<Client, IClient>(EagerWhen = nameof(Enabled))]")]
		[Arguments("[Singleton<Client, IClient>(Eager = true)]")]
		[Arguments("[Singleton<Client, IClient>(Eager = false, EagerWhen = nameof(Enabled))]")]
		public async Task DoesNotReportEitherAlone(string registration)
		{
			GeneratorResult result = Generator.Run(Container(registration));

			await That(result.Diagnostics).IsEmpty();
		}

		private static string Container(string registration)
			=> $$"""
			     using Awaiten;

			     namespace MyCode;

			     public interface IClient { }
			     public sealed class Client : IClient { }

			     [Container]
			     {{registration}}
			     public static partial class MyContainer
			     {
			     	private static bool Enabled => true;
			     }
			     """;
	}
}
