using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt111ConflictingProduction
	{
		[Test]
		public async Task ReportsWhenAnImplementationIsRegisteredWithDifferentProductionStrategies()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(Factory = nameof(Make))]
			                                       [Singleton<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static Service Make() => new Service();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT111*").AsWildcard()
				.Because("coalescing keeps the first production strategy, so the contradicting one must not be silently dropped");
		}

		[Test]
		public async Task ReportsWhenTheSameImplementationNamesDifferentFactoryMembers()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(Factory = nameof(MakeA))]
			                                       [Singleton<Service>(Factory = nameof(MakeB))]
			                                       [Singleton<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static Service MakeA() => new Service();
			                                       	private static Service MakeB() => new Service();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT111*").AsWildcard()
				.Because("the constructor registration makes Service an implementation, so every registration of it is that one implementation and the factories contradict it");
		}

		[Test]
		public async Task ReportsWhenAnExplicitImplementationTypeIsConstructedInAModuleAndProducedByAFactoryInTheContainer()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }

			                                       [Module]
			                                       [Singleton<SystemClock, IClock>]
			                                       public static class ClockModule { }

			                                       [Container]
			                                       [Import(typeof(ClockModule))]
			                                       [Singleton<SystemClock, IClock>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                       	public static SystemClock Create() => new SystemClock();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT111*").AsWildcard()
				.Because("both registrations name SystemClock as the implementation, and the losing module registration stays a collection member, so there is no single way to produce it");
		}

		[Test]
		public async Task QualifiesEachMemberWithItsDeclaringType()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }

			                                       [Module]
			                                       [Singleton<SystemClock, IClock>(Factory = nameof(Create))]
			                                       public static class ClockModule
			                                       {
			                                       	public static SystemClock Create() => new SystemClock();
			                                       }

			                                       [Container]
			                                       [Import(typeof(ClockModule))]
			                                       [Singleton<SystemClock, IClock>(Factory = nameof(Create))]
			                                       public static partial class MyContainer
			                                       {
			                                       	public static SystemClock Create() => new SystemClock();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT111*factory 'MyCode.MyContainer.Create' and factory 'MyCode.ClockModule.Create'*").AsWildcard()
				.Because("two members of the same name on different owners are different factories, so the message must tell them apart");
		}

		public class WithoutAnImplementationType
		{
			[Test]
			public async Task DoesNotReportWhenTheContainerRegistersAnInstanceOverAModuleFactory()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }

				                                       [Module]
				                                       [Singleton<IClock>(Factory = nameof(Create))]
				                                       public static class ClockModule
				                                       {
				                                       	public static IClock Create() => new SystemClock();
				                                       }

				                                       [Container]
				                                       [Import(typeof(ClockModule))]
				                                       [Singleton<IClock>(Instance = nameof(Clock))]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock Clock { get; } = new FixedClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("a Factory or Instance registration that names only the service is identified by its member, so the two are different implementations and the container's registration takes precedence");
			}

			[Test]
			public async Task DoesNotReportWhenTheContainerAndAModuleNameFactoriesOfTheSameName()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }

				                                       [Module]
				                                       [Singleton<IClock>(Factory = nameof(Create))]
				                                       public static class ClockModule
				                                       {
				                                       	public static IClock Create() => new SystemClock();
				                                       }

				                                       [Container]
				                                       [Import(typeof(ClockModule))]
				                                       [Singleton<IClock>(Factory = nameof(Create))]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock Create() => new FixedClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the module's Create and the container's Create are different members, so they produce different implementations");
			}

			[Test]
			public async Task DoesNotReportForTwoFactoriesInOneContainer()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }

				                                       [Container]
				                                       [Singleton<IClock>(Factory = nameof(CreateA))]
				                                       [Singleton<IClock>(Factory = nameof(CreateB))]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock CreateA() => new SystemClock();
				                                       	public static IClock CreateB() => new FixedClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("two factories of one service are two implementations, like two constructed classes: the first wins single resolution and both are collection members");
			}

			[Test]
			public async Task DoesNotReportForTwoFactoriesOfAConcreteServiceThatIsNotRegisteredAsAnImplementation()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public sealed class Service { }

				                                       [Container]
				                                       [Singleton<Service>(Factory = nameof(MakeA))]
				                                       [Singleton<Service>(Factory = nameof(MakeB))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static Service MakeA() => new Service();
				                                       	private static Service MakeB() => new Service();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("neither registration names an implementation type, so each factory is its own implementation");
			}

			[Test]
			public async Task DoesNotReportForKeyedFactoriesNamingDifferentMembers()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }

				                                       [Container]
				                                       [Singleton<IClock>(Factory = nameof(CreateA), Key = "a")]
				                                       [Singleton<IClock>(Factory = nameof(CreateB), Key = "b")]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock CreateA() => new SystemClock();
				                                       	public static IClock CreateB() => new FixedClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("each key is produced by its own factory, so the keyed registrations are different implementations");
			}

			[Test]
			public async Task DoesNotReportForAContextualFactoryBesideTheUnconditionalOne()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }
				                                       public sealed class Report
				                                       {
				                                       	public Report(IClock clock) { }
				                                       }

				                                       [Container]
				                                       [Singleton<IClock>(Factory = nameof(CreateA))]
				                                       [Singleton<IClock>(Factory = nameof(CreateB), WhenInjectedInto = typeof(Report))]
				                                       [Transient<Report>]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock CreateA() => new SystemClock();
				                                       	public static IClock CreateB() => new FixedClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the contextual registration names a different factory, so it is a different implementation");
			}

			[Test]
			public async Task ReportsDuplicateKeyInsteadForTwoFactoriesUnderTheSameKey()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }

				                                       [Container]
				                                       [Singleton<IClock>(Factory = nameof(CreateA), Key = "a")]
				                                       [Singleton<IClock>(Factory = nameof(CreateB), Key = "a")]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock CreateA() => new SystemClock();
				                                       	public static IClock CreateB() => new FixedClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).Contains("*AWT117*").AsWildcard()
					.Because("two different implementations claim one key, which is the ambiguity AWT117 names");
				await That(result.Diagnostics).DoesNotContain("*AWT111*").AsWildcard()
					.Because("the two factories are different implementations, not one implementation produced two ways");
			}

			[Test]
			public async Task ReportsCrossModuleDuplicateInsteadForFactoriesInTwoModules()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }
				                                       public sealed class FixedClock : IClock { }
				                                       public sealed class Consumer(IClock clock) { }

				                                       [Module]
				                                       [Singleton<IClock>(Factory = nameof(Create))]
				                                       public static class ModuleA
				                                       {
				                                       	public static IClock Create() => new SystemClock();
				                                       }

				                                       [Module]
				                                       [Singleton<IClock>(Factory = nameof(Create))]
				                                       public static class ModuleB
				                                       {
				                                       	public static IClock Create() => new FixedClock();
				                                       }

				                                       [Container]
				                                       [Import(typeof(ModuleA))]
				                                       [Import(typeof(ModuleB))]
				                                       [Singleton<Consumer>]
				                                       public static partial class MyContainer
				                                       {
				                                       }
				                                       """);

				await That(result.Diagnostics).Contains("*AWT155*IClock*ModuleA*ModuleB*").AsWildcard()
					.Because("two modules registering different implementations of one service at the same tier is the import-order ambiguity AWT155 names");
				await That(result.Diagnostics).DoesNotContain("*AWT111*").AsWildcard()
					.Because("the two modules' factories are different implementations");
			}

			[Test]
			public async Task ReportsForTheSameMemberNamedAsBothFactoryAndInstance()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IClock { }
				                                       public sealed class SystemClock : IClock { }

				                                       [Container]
				                                       [Singleton<IClock>(Factory = nameof(Clock))]
				                                       [Singleton<IClock>(Instance = nameof(Clock), Key = "k")]
				                                       public static partial class MyContainer
				                                       {
				                                       	public static IClock Clock() => new SystemClock();
				                                       }
				                                       """);

				await That(result.Diagnostics).Contains("*AWT111*").AsWildcard()
					.Because("both registrations name the same member, so they are one implementation produced two ways");
			}

			public class OpenGeneric
			{
				[Test]
				public async Task DoesNotReportForTwoOpenFactoriesInOneContainer()
				{
					GeneratorResult result = Generator.Run("""
					                                       using Awaiten;

					                                       namespace MyCode;

					                                       public interface IRepo<T> { }
					                                       public sealed class Repo<T> : IRepo<T> { }
					                                       public sealed class Order { }
					                                       public sealed class Consumer
					                                       {
					                                       	public Consumer(IRepo<Order>[] repos) { }
					                                       }

					                                       [Container]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeA))]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeB))]
					                                       [Transient<Consumer>]
					                                       public static partial class MyContainer
					                                       {
					                                       	public static IRepo<T> MakeA<T>() => new Repo<T>();
					                                       	public static IRepo<T> MakeB<T>() => new Repo<T>();
					                                       }
					                                       """);

					await That(result.Diagnostics).IsEmpty()
						.Because("each open factory's closing is its own implementation, as for the closed form");
					string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
					await That(source).Contains("MakeA<global::MyCode.Order>()");
					await That(source).Contains("MakeB<global::MyCode.Order>()")
						.Because("the second open factory is not dropped from the closing's collection");
				}

				[Test]
				public async Task DoesNotReportForKeyedOpenFactoriesNamingDifferentMembers()
				{
					GeneratorResult result = Generator.Run("""
					                                       using Awaiten;

					                                       namespace MyCode;

					                                       public interface IRepo<T> { }
					                                       public sealed class Repo<T> : IRepo<T> { }
					                                       public sealed class Order { }
					                                       public sealed class Consumer
					                                       {
					                                       	public Consumer([FromKey("a")] IRepo<Order> a, [FromKey("b")] IRepo<Order> b) { }
					                                       }

					                                       [Container]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeA), Key = "a")]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeB), Key = "b")]
					                                       [Transient<Consumer>]
					                                       public static partial class MyContainer
					                                       {
					                                       	public static IRepo<T> MakeA<T>() => new Repo<T>();
					                                       	public static IRepo<T> MakeB<T>() => new Repo<T>();
					                                       }
					                                       """);

					await That(result.Diagnostics).IsEmpty()
						.Because("each key's closing is produced by its own open factory, so they are different implementations");
				}

				[Test]
				public async Task DoesNotReportForAContextualOpenFactoryBesideTheUnconditionalOne()
				{
					GeneratorResult result = Generator.Run("""
					                                       using Awaiten;

					                                       namespace MyCode;

					                                       public interface IRepo<T> { }
					                                       public sealed class Repo<T> : IRepo<T> { }
					                                       public sealed class Order { }
					                                       public sealed class Report
					                                       {
					                                       	public Report(IRepo<Order> repo) { }
					                                       }
					                                       public sealed class Consumer
					                                       {
					                                       	public Consumer(IRepo<Order> repo) { }
					                                       }

					                                       [Container]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeA))]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeB), WhenInjectedInto = typeof(Report))]
					                                       [Transient<Report>]
					                                       [Transient<Consumer>]
					                                       public static partial class MyContainer
					                                       {
					                                       	public static IRepo<T> MakeA<T>() => new Repo<T>();
					                                       	public static IRepo<T> MakeB<T>() => new Repo<T>();
					                                       }
					                                       """);

					await That(result.Diagnostics).IsEmpty()
						.Because("the contextual open factory names a different member, so its closing is a different implementation");
				}

				[Test]
				public async Task ReportsDuplicateKeyInsteadForTwoOpenFactoriesUnderTheSameKey()
				{
					GeneratorResult result = Generator.Run("""
					                                       using Awaiten;

					                                       namespace MyCode;

					                                       public interface IRepo<T> { }
					                                       public sealed class Repo<T> : IRepo<T> { }
					                                       public sealed class Order { }
					                                       public sealed class Consumer
					                                       {
					                                       	public Consumer([FromKey("a")] IRepo<Order> repo) { }
					                                       }

					                                       [Container]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeA), Key = "a")]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(MakeB), Key = "a")]
					                                       [Transient<Consumer>]
					                                       public static partial class MyContainer
					                                       {
					                                       	public static IRepo<T> MakeA<T>() => new Repo<T>();
					                                       	public static IRepo<T> MakeB<T>() => new Repo<T>();
					                                       }
					                                       """);

					await That(result.Diagnostics).Contains("*AWT117*").AsWildcard()
						.Because("two open factories claim one key of the closing, which is the ambiguity AWT117 names, as for the closed form");
					await That(result.Diagnostics).DoesNotContain("*AWT111*").AsWildcard()
						.Because("the two closings are different implementations, not one implementation produced two ways");
				}

				[Test]
				public async Task ReportsCrossModuleDuplicateInsteadForOpenFactoriesInTwoModules()
				{
					GeneratorResult result = Generator.Run("""
					                                       using Awaiten;

					                                       namespace MyCode;

					                                       public interface IRepo<T> { }
					                                       public sealed class Repo<T> : IRepo<T> { }
					                                       public sealed class Order { }
					                                       public sealed class Consumer
					                                       {
					                                       	public Consumer(IRepo<Order> repo) { }
					                                       }

					                                       [Module]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(Make))]
					                                       public static class ModuleA
					                                       {
					                                       	public static IRepo<T> Make<T>() => new Repo<T>();
					                                       }

					                                       [Module]
					                                       [Singleton(typeof(IRepo<>), Factory = nameof(Make))]
					                                       public static class ModuleB
					                                       {
					                                       	public static IRepo<T> Make<T>() => new Repo<T>();
					                                       }

					                                       [Container]
					                                       [Import(typeof(ModuleA))]
					                                       [Import(typeof(ModuleB))]
					                                       [Transient<Consumer>]
					                                       public static partial class MyContainer
					                                       {
					                                       }
					                                       """);

					await That(result.Diagnostics).Contains("*AWT155*IRepo*ModuleA*ModuleB*").AsWildcard()
						.Because("two modules' open factories of the same name are different implementations of the closing, decided only by [Import] order");
				}

				[Test]
				public async Task ReportsForAnOpenFactoryAndAnOpenConstructorOfTheSameImplementation()
				{
					GeneratorResult result = Generator.Run("""
					                                       using Awaiten;

					                                       namespace MyCode;

					                                       public sealed class Repo<T> { }
					                                       public sealed class Order { }
					                                       public sealed class Consumer
					                                       {
					                                       	public Consumer(Repo<Order> repo) { }
					                                       }

					                                       [Container]
					                                       [Singleton(typeof(Repo<>))]
					                                       [Singleton(typeof(Repo<>), Factory = nameof(Make))]
					                                       [Transient<Consumer>]
					                                       public static partial class MyContainer
					                                       {
					                                       	public static Repo<T> Make<T>() => new Repo<T>();
					                                       }
					                                       """);

					await That(result.Diagnostics).Contains("*AWT111*'MyCode.Repo<MyCode.Order>'*a constructor*factory 'MyCode.MyContainer.Make'*").AsWildcard()
						.Because("the constructor registration names Repo<Order> as its implementation, so the factory's closing is that one implementation produced a second way, as for the closed form");
				}
			}
		}

		[Test]
		public async Task ReportsWhenTheSameImplementationUnderDifferentServicesNamesDifferentFactoryMembers()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRead { }
			                                       public interface IWrite { }
			                                       public sealed class Store : IRead, IWrite { }

			                                       [Container]
			                                       [Singleton<Store, IRead>(Factory = nameof(MakeA))]
			                                       [Singleton<Store, IWrite>(Factory = nameof(MakeB))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static Store MakeA() => new Store();
			                                       	private static Store MakeB() => new Store();
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT111*").AsWildcard()
				.Because("coalescing one implementation across services keeps the first factory, so a second, different one must not be silently dropped");
		}

		private const string KettleLibrary = """
			using Awaiten;

			namespace Lib;

			public interface IKettle { }
			public sealed class Kettle : IKettle { internal Kettle() { } }

			[Module]
			[Transient<Kettle, IKettle>]
			public static partial class KettleModule { }
			""";

		private static string KettleContainer(string registration)
			=> $$"""
			     using Awaiten;
			     using Lib;

			     namespace MyCode;

			     [Container]
			     [Import(typeof(KettleModule))]
			     {{registration}}
			     public static partial class MyContainer;
			     """;

		[Test]
		[Arguments("[Transient<Kettle>]")]
		[Arguments("[Transient<Kettle, IKettle>]")]
		public async Task AContainerOfAnotherAssemblyConstructingWhatAModuleConstructsThroughItsGeneratedFactory_SharesTheFactory(string registration)
		{
			GeneratorResult same = Generator.Run(KettleLibrary + "\n[Container]\n[Import(typeof(KettleModule))]\n" + registration + "\npublic static partial class SameContainer;");
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(KettleLibrary, KettleContainer(registration));
			GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(KettleLibrary, KettleContainer(registration));

			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the container's registration calls the module's generated factory for the constructor it cannot reach, so both registrations are one production");
			await That(reference.Diagnostics).IsEmpty();
		}

		private static GeneratorResult RunWithFriendModule(string registration, string setup)
		{
			const string hook = "internal static void Wire(Kettle kettle) { }";
			if (setup == "same")
			{
				return Generator.Run(KettleLibrary + $"\n[Module]\n{registration}\npublic static partial class FriendModule {{ {hook} }}\n\n[Container]\n[Import(typeof(KettleModule))]\n[Import(typeof(FriendModule))]\npublic static partial class SameContainer;");
			}

			string grants = setup == "friend" ? "\"Library2\")]\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "\"Library2\")]";
			string library = KettleLibrary.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(" + grants);
			string friendModule = $"using Awaiten;\nusing Lib;\n\nnamespace Friend;\n\n[Module]\n{registration}\npublic static partial class FriendModule {{ {hook} }}";
			return Generator.RunWithGeneratedReferencedAssemblyChain([library, friendModule,], KettleContainer("[Import(typeof(Friend.FriendModule))]"), setup == "reference");
		}

		[Test]
		[Arguments("friend")]
		[Arguments("full")]
		[Arguments("reference")]
		public async Task ModulesOfTwoOtherAssembliesConstructingAnImplementationThroughTheirGeneratedFactories_ShareTheFirstFactory(string setup)
		{
			GeneratorResult result = RunWithFriendModule("[Transient<Kettle>]", setup);

			await That(RunWithFriendModule("[Transient<Kettle>]", "same").Diagnostics).IsEmpty();
			await That(result.Diagnostics).IsEmpty()
				.Because("both modules' generated factories mirror the one constructor, so the container calls the first module's factory for both registrations, as a container in the implementation's assembly constructs one instance");
			if (setup != "friend")
			{
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.KettleModule.Internals.Registration_")
					.And.DoesNotContain("global::Friend.FriendModule.Internals.Registration_")
					.Because("the friend module's registration shares the factory of the module that comes first");
			}
		}

		[Test]
		[Arguments("[Singleton<Kettle>]", "friend")]
		[Arguments("[Singleton<Kettle>]", "full")]
		[Arguments("[Singleton<Kettle>]", "reference")]
		[Arguments("[Transient<Kettle>(OnActivated = nameof(Wire))]", "friend")]
		[Arguments("[Transient<Kettle>(OnActivated = nameof(Wire))]", "full")]
		[Arguments("[Transient<Kettle>(OnActivated = nameof(Wire))]", "reference")]
		public async Task ModulesOfTwoOtherAssembliesSharingAGeneratedFactory_ReportAsInTheImplementationsAssembly(string registration, string setup)
		{
			string[] expected = RunWithFriendModule(registration, "same").Diagnostics.Select(Message).ToArray();

			string[] result = RunWithFriendModule(registration, setup).Diagnostics.Select(Message).ToArray();

			await That(result).IsEqualTo(expected)
				.Because("sharing the factory keeps the friend module's lifetime and hooks, so they meet the other module's registration as in the implementation's assembly");

			static string Message(string diagnostic)
				=> diagnostic.Substring(diagnostic.IndexOf("AWT", StringComparison.Ordinal)).Replace("Friend.FriendModule", "Lib.FriendModule");
		}

		public class BesideTheHandleOfAModuleOfAnotherAssembly
		{
			private const string OneFactory = """
				using Awaiten;

				namespace Lib;

				internal sealed class TimeSource { }
				public sealed class Clock { public Clock() { } }

				[Module]
				[Singleton<TimeSource>]
				[Singleton<Clock>(Factory = nameof(CreateA))]{0}
				public static partial class ClockModule
				{
				    internal static Clock CreateA(TimeSource source) => new();
				    internal static Clock CreateB(TimeSource source) => new();
				}

				[Module]
				[Singleton<Clock>]
				public static partial class OtherModule;
				""";

			private const string ThreeModules = """
				using Awaiten;

				namespace Lib;

				internal sealed class TimeSource { }
				public sealed class Clock { {0} Clock() { } }

				[Module]
				[Singleton<TimeSource>]
				[Singleton<Clock>(Factory = nameof(CreateA){1})]
				public static partial class AModule
				{
				    internal static Clock CreateA(TimeSource source) => new();
				}

				[Module]
				[{2}<Clock>(Factory = nameof(CreateB))]
				public static partial class BModule
				{
				    internal static Clock CreateB(TimeSource source) => new();
				}

				[Module]
				[Singleton<Clock>]
				public static partial class CModule;
				""";

			private static GeneratorResult Run(string library, string container, string setup)
			{
				const string usings = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n";
				return setup switch
				{
					"same" => Generator.Run(library + "\n" + container.Replace("{0}", "SameContainer")),
					"friend" => Generator.RunWithGeneratedReferencedAssembly(
						library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\n\nnamespace Lib;"),
						usings + container.Replace("{0}", "MyContainer")),
					"reference" => Generator.RunWithGeneratedReferenceAssembly(library, usings + container.Replace("{0}", "MyContainer")),
					_ => Generator.RunWithGeneratedReferencedAssembly(library, usings + container.Replace("{0}", "MyContainer")),
				};
			}

			/// <summary>The message of <paramref name="diagnostic" />, naming the container as the container of the module's own assembly is named.</summary>
			private static string Message(string diagnostic)
				=> diagnostic.Substring(diagnostic.IndexOf("AWT", StringComparison.Ordinal)).Replace("MyCode.MyContainer", "Lib.SameContainer");

			/// <summary>
			///     Asserts that <paramref name="setup" /> reports what a container in the module's own assembly reports, at the
			///     registration a container granted <c>InternalsVisibleTo</c> reports it at, and returns those diagnostics.
			/// </summary>
			private static async Task<string[]> ReportsAsInTheModulesOwnAssembly(string library, string container, string setup)
			{
				string[] same = Run(library, container, "same").Diagnostics;
				string[] friend = Run(library, container, "friend").Diagnostics;
				string[] result = Run(library, container, setup).Diagnostics;

				await That(result.Select(Message)).IsEqualTo(same.Select(Message))
					.Because("the handle's instance is one instance with every registration of the implementation, so its producers conflict once and between the same pair as in the module's own assembly");
				await That(result).IsEqualTo(friend)
					.Because("the conflict is reported at the registration that a container granted InternalsVisibleTo reports it at");
				return result;
			}

			[Test]
			[Arguments("", "friend")]
			[Arguments("", "full")]
			[Arguments("", "reference")]
			[Arguments("\n[Singleton<Clock>(Factory = nameof(CreateB))]", "friend")]
			[Arguments("\n[Singleton<Clock>(Factory = nameof(CreateB))]", "full")]
			[Arguments("\n[Singleton<Clock>(Factory = nameof(CreateB))]", "reference")]
			public async Task AHookOfARegistrationThatProducesTheImplementationOtherwise_IsNotReportedAsUnapplied(string second, string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<Clock>(OnActivated = nameof(Act))]
					public static partial class {0}
					{
					    private static void Act(Clock clock) { }
					}
					""";

				string[] result = await ReportsAsInTheModulesOwnAssembly(OneFactory.Replace("{0}", second), container, setup);

				await That(result).HasSingle().Which.Contains("AWT111").And.Contains("a constructor and factory 'Lib.ClockModule.CreateA'")
					.Because("the registration constructs what the module's factory produces, so AWT111 asks to produce it one way first, and the hook the handle would not run (AWT241) does not matter until then");
			}

			[Test]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task TwoFactoriesOfOneModule_BesideAModuleThatConstructsTheImplementation_NameBothFactories(string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Import(typeof(OtherModule))]
					public static partial class {0};
					""";

				string[] result = await ReportsAsInTheModulesOwnAssembly(OneFactory.Replace("{0}", "\n[Singleton<Clock>(Factory = nameof(CreateB))]"), container, setup);

				await That(result).HasSingle().Which.Contains("AWT111").And.Contains("factory 'Lib.ClockModule.CreateA' and factory 'Lib.ClockModule.CreateB'");
			}

			[Test]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task TwoRegistrationsOfTheContainer_ConflictWithEachOtherFirst(string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<Clock>]
					[Singleton<Clock>(Factory = nameof(Make))]
					public static partial class {0}
					{
					    private static Clock Make() => new();
					}
					""";

				string[] result = await ReportsAsInTheModulesOwnAssembly(OneFactory.Replace("{0}", ""), container, setup);

				await That(result).HasSingle().Which.Contains("AWT111").And.Contains("a constructor and factory 'MyCode.MyContainer.Make'")
					.Because("the container's registrations precede the module's, so their own conflict is the first");
			}

			[Test]
			[Arguments("public", "friend")]
			[Arguments("public", "full")]
			[Arguments("public", "reference")]
			[Arguments("internal", "friend")]
			[Arguments("internal", "full")]
			[Arguments("internal", "reference")]
			public async Task AFallbackFactory_YieldsToTheFactoryOfAnotherModule(string constructor, string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(BModule))]
					[Import(typeof(CModule))]
					public static partial class {0};
					""";
				string library = ThreeModules.Replace("{0}", constructor).Replace("{1}", ", Fallback = Fallback.Warn").Replace("{2}", "Singleton");

				string[] result = await ReportsAsInTheModulesOwnAssembly(library, container, setup);

				await That(result).HasSingle().Which.Contains("AWT111").And.Contains("factory 'Lib.BModule.CreateB' and a constructor")
					.Because("the other modules replace the Fallback factory, so CModule's constructor shares the handle of BModule's factory and conflicts with it, even when the container can call the constructor itself");
			}

			[Test]
			[Arguments("public", "friend")]
			[Arguments("public", "full")]
			[Arguments("public", "reference")]
			[Arguments("internal", "friend")]
			[Arguments("internal", "full")]
			[Arguments("internal", "reference")]
			public async Task ARegistrationOfTheContainer_ConflictsWithEveryProducerOfTheImplementation(string constructor, string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(BModule))]
					[Singleton<Clock>]
					public static partial class {0};
					""";
				string library = ThreeModules.Replace("{0}", constructor).Replace("{1}", "").Replace("{2}", "Scoped");

				string[] result = await ReportsAsInTheModulesOwnAssembly(library, container, setup);

				await That(result).HasCount(2)
					.And.Contains("*AWT111*a constructor and factory 'Lib.AModule.CreateA'*").AsWildcard()
					.And.Contains("*AWT107*Singleton and Scoped*").AsWildcard()
					.Because("the container's registration names the implementation, so both modules' factories are productions of its one instance and BModule's lifetime contradicts it too");
			}

			[Test]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task AModuleThatConstructsAnImplementationTheContainerCanConstruct_ReportsTheConflictOnce(string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(BModule))]
					[Import(typeof(CModule))]
					public static partial class {0};
					""";
				string library = ThreeModules.Replace("{0}", "public").Replace("{1}", "").Replace("{2}", "Singleton");

				string[] result = await ReportsAsInTheModulesOwnAssembly(library, container, setup);

				await That(result).HasSingle().Which.Contains("AWT111").And.Contains("factory 'Lib.AModule.CreateA' and factory 'Lib.BModule.CreateB'")
					.Because("CModule's constructor shares the handle the modules' factories coalesce into, so the one conflict of the implementation is the first pair in precedence order");
			}

			[Test]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task FactoriesWithOtherLifetimesAndHooks_BesideAModuleThatConstructsTheImplementation_ReportEachConflictOnce(string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(BModule))]
					[Import(typeof(CModule))]
					public static partial class {0};
					""";
				string library = ThreeModules.Replace("{0}", "public").Replace("{1}", ", OnActivated = nameof(ReadyA)").Replace("{2}", "Scoped")
					.Replace("Factory = nameof(CreateB))]", "Factory = nameof(CreateB), OnActivated = nameof(ReadyB))]")
					.Replace("internal static Clock CreateA(TimeSource source) => new();", "internal static Clock CreateA(TimeSource source) => new();\n    internal static void ReadyA(Clock clock) { }")
					.Replace("internal static Clock CreateB(TimeSource source) => new();", "internal static Clock CreateB(TimeSource source) => new();\n    internal static void ReadyB(Clock clock) { }");

				string[] result = await ReportsAsInTheModulesOwnAssembly(library, container, setup);

				await That(result).HasCount(3)
					.And.Contains("*AWT107*Singleton and Scoped*").AsWildcard()
					.And.Contains("*AWT111*factory 'Lib.AModule.CreateA' and factory 'Lib.BModule.CreateB'*").AsWildcard()
					.And.Contains("*AWT166*'Lib.AModule.ReadyA' and 'Lib.BModule.ReadyB'*").AsWildcard()
					.Because("the modules' factories and CModule's constructor produce one instance, so each of BModule's contradictions of AModule is reported once, as in the module's own assembly");
			}

			[Test]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ARegistrationOfTheContainerThatComesFirst_IsTheProducerTheHandlesAreComparedWith(string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(BModule))]
					[Import(typeof(CModule))]
					[Singleton<Clock>(Key = "c", Eager = true)]
					public static partial class {0};
					""";
				string library = ThreeModules.Replace("{0}", "public").Replace("{1}", "").Replace("{2}", "Singleton")
					.Replace("Factory = nameof(CreateB))]", "Factory = nameof(CreateB), Key = \"b\", Eager = true)]");

				string[] result = await ReportsAsInTheModulesOwnAssembly(library, container, setup);

				await That(result).HasSingle().Which.Contains("AWT111").And.Contains("a constructor and factory 'Lib.AModule.CreateA'")
					.Because("the container's eager registration comes first, so BModule's eager factory agrees with it, even though AModule's factory is not eager");
			}

			[Test]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ADroppedFallbackDefault_DoesNotConflictWithTheHandle(string setup)
			{
				const string container = """
					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(CModule))]
					public static partial class {0};
					""";
				string library = ThreeModules.Replace("{0}", "public").Replace("{1}", "").Replace("{2}", "Singleton")
					.Replace("[Singleton<Clock>]\npublic static partial class CModule;", "[Scoped<Clock>(Fallback = Fallback.Warn)]\npublic static partial class CModule;")
					.Replace("[Singleton<Clock>]\r\npublic static partial class CModule;", "[Scoped<Clock>(Fallback = Fallback.Warn)]\r\npublic static partial class CModule;");

				string[] result = await ReportsAsInTheModulesOwnAssembly(library, container, setup);

				await That(result).IsEmpty()
					.Because("AModule's registration replaces CModule's Fallback default, which is dropped and so produces nothing that could conflict");
			}
		}
	}
}
