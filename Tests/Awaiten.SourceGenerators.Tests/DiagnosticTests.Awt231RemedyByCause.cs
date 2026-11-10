namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     AWT231 ends with the remedy for its cause: a grant only where one would help, what a grant does not reach for a
	///     container that has one, and the module's own fix for a fault no container gets past.
	/// </summary>
	public class Awt231RemedyByCause
	{
		private const string OpenConsumer = """
			using Awaiten;
			using Lib;

			namespace App;

			public sealed class Order { }
			public sealed class Checkout { public Checkout(IRepository<Order> orders) { } }

			[Container]
			[Import(typeof(DataModule))]
			[Transient<Checkout>]
			public static partial class AppContainer;
			""";

		private const string PlainConsumer = """
			using Awaiten;
			using Lib;

			namespace App;

			[Container]
			[Import(typeof(DataModule))]
			public static partial class AppContainer;
			""";

		private static string Granted(string library, string friend = "TestAssembly")
			=> library.Replace("namespace Lib;", $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{friend}\")]\nnamespace Lib;");

		public class OpenHookNoContainerCanRun
		{
			private const string Library = """
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
					internal static Task<int> Activate<T>(Repository<T> repository) => Task.FromResult(0);
				}
				""";

			[Test]
			[Arguments(false)]
			[Arguments(true)]
			public async Task LibraryBuild_ReportsTheHook(bool granted)
			{
				GeneratorResult result = Generator.Run(granted ? Granted(Library, "Friend") : Library);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT232*'Lib.Repository<T>'*'Activate'*return Task or ValueTask instead").AsWildcard()
					.Because("every container fails to run the hook for every closing, so the library's build reports it as for a closed registration");
			}

			[Test]
			public async Task Friend_ReportsTheHookItself()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library), OpenConsumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT232*'Lib.IRepository<App.Order>'*'Activate'*").AsWildcard();
			}

			[Test]
			public async Task External_NamesTheHookFixInsteadOfAGrant()
			{
				GeneratorResult full = Generator.RunWithGeneratedReferencedAssembly(Granted(Library, "Other"), OpenConsumer);
				GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Granted(Library, "Other"), OpenConsumer);

				foreach (GeneratorResult result in new[] { full, reference, })
				{
					await That(result.Diagnostics).HasSingle().Which
						.Contains("*AWT231*'Lib.Repository<T>'*its OnActivated hook 'Activate' cannot be run through a generated wrapper*Task<int>*; fix the OnActivated hook 'Activate' in the module's assembly, since no container can run it as it is").AsWildcard()
						.And.DoesNotContain("grant this assembly").Because("a friend fails to run the hook too, so a grant cannot help");
				}
			}
		}

		public class GenericFactoryMemberOfAThirdAssemblyType
		{
			[Test]
			public async Task External_IsOfferedTheModulesGrant()
			{
				const string third = """
					namespace Third;

					public sealed class Repository<T>
					{
						internal Repository() { }
						public static Repository<T> Create() => new Repository<T>();
					}
					""";
				const string library = """
					using Awaiten;

					namespace Lib;

					[Module]
					[Singleton(typeof(Third.Repository<>), Factory = nameof(Make))]
					public static partial class DataModule
					{
						internal static Third.Repository<T> Make<T>() => Third.Repository<T>.Create();
					}
					""";
				const string consumer = """
					using Awaiten;
					using Lib;

					namespace App;

					public sealed class Order { }
					public sealed class Checkout { public Checkout(Third.Repository<Order> orders) { } }

					[Container]
					[Import(typeof(DataModule))]
					[Transient<Checkout>]
					public static partial class AppContainer;
					""";

				GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([third, library,], consumer);

				await That(result.Diagnostics).HasSingle().Which
					.Contains("*AWT231*through its Factory member 'Make'*; grant this assembly InternalsVisibleTo in the module's assembly, or make 'Make' and the types in its signature public").AsWildcard()
					.Because("the container calls the member rather than the constructor, so only the module's grant helps");
			}
		}

		public class OpenHookOfAPublicType
		{
			private const string Library = """
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				public interface IRepository<T> { }
				public sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
					internal static Task<int> Activate<T>(Repository<T> repository) => Task.FromResult(0);
				}
				""";

			[Test]
			public async Task LibraryBuild_ReportsTheHook()
			{
				await That(Generator.Run(Library).Diagnostics).HasSingle().Which.Contains("*AWT232*'Lib.Repository<T>'*'Activate'*").AsWildcard();
			}

			[Test]
			public async Task LibraryBuild_LeavesItToAContainerOfItsAssembly()
			{
				GeneratorResult result = Generator.Run(Library + """

					public sealed class Order { }
					public sealed class Checkout { public Checkout(IRepository<Order> orders) { } }

					[Container]
					[Import(typeof(DataModule))]
					[Transient<Checkout>]
					public static partial class SameContainer;
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT232*'Lib.IRepository<Lib.Order>'*'Activate'*").AsWildcard()
					.Because("the container constructs the public type and reports the hook itself, as for a closed registration of a public type");
			}
		}

		public class OpenHookSomeClosingsBind
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
					internal static void Activate<T>(Repository<T> repository) where T : struct { }
				}
				""";

			[Test]
			public async Task LibraryBuild_StaysSilent()
			{
				await That(Generator.Run(Library).Diagnostics).IsEmpty()
					.Because("a container in the library's assembly runs the hook for a closing its constraints admit");
				await That(Generator.Run(Granted(Library, "Friend")).Diagnostics).IsEmpty();
			}

			[Test]
			public async Task External_NamesTheConstraintFixAndTheGrant()
			{
				string library = Library.Replace("where T : struct", "where T : class");

				GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), OpenConsumer);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, OpenConsumer);

				await That(friend.Diagnostics).IsEmpty()
					.Because("a friend binds the hook for a closing its constraints admit");
				await That(result.Diagnostics).HasSingle().Which
					.Contains("*AWT231*'Lib.Repository<T>'*the constraints of its generic overload do not hold for every closing of 'Lib.Repository<T>'; constrain the type parameters of 'Lib.Repository<T>' alike; grant this assembly InternalsVisibleTo in the module's assembly, or make*").AsWildcard()
					.Because("the grant lets this container build the closings the constraints admit");
			}
		}

		public class OpenHookOverloadForOneClosing
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IRepository<T> { }
				internal sealed class Repository<T> : IRepository<T> { }

				[Module]
				[Singleton(typeof(Repository<>), typeof(IRepository<>), OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
					internal static void Activate(Repository<int> repository) { }
				}
				""";

			[Test]
			public async Task LibraryBuild_StaysSilent_AndExternalIsOfferedTheGrant()
			{
				GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library, OpenConsumer);

				await That(Generator.Run(Library).Diagnostics).IsEmpty()
					.Because("a container in the library's assembly runs the hook for the closing over int");
				await That(external.Diagnostics).HasSingle().Which.Contains("*AWT231*'Lib.Repository<T>'*; grant this assembly InternalsVisibleTo in the module's assembly, or make*").AsWildcard();
			}
		}

		public class PrivateStartedSingleton
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				[Module]
				[Singleton<DataModule.Warmer, DataModule.IWarmer>(Eager = true)]
				public static partial class DataModule
				{
					private interface IWarmer { }
					private sealed class Warmer : IWarmer { }
				}
				""";

			[Test]
			[Arguments(false)]
			[Arguments(true)]
			public async Task LibraryBuild_ReportsAWT216(bool granted)
			{
				GeneratorResult result = Generator.Run(granted ? Granted(Library, "Friend") : Library);

				await That(result.Diagnostics).HasSingle().Which
					.Contains("*AWT216*'Lib.DataModule.Warmer'*registered as the private service 'Lib.DataModule.IWarmer'; register it under an accessible service, or make 'Lib.DataModule.Warmer' internal or public").AsWildcard()
					.Because("no container can name the service to hold the singleton it starts");
			}

			[Test]
			public async Task LibraryBuild_LeavesItToAContainerOfItsAssembly()
			{
				GeneratorResult result = Generator.Run(Library + """

					[Container]
					[Import(typeof(DataModule))]
					public static partial class SameContainer;
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT216*").AsWildcard()
					.Because("the container reports the registration itself, so the module does not report it twice");
			}

			[Test]
			public async Task Friend_IsToldWhatTheGrantDoesNotReach()
			{
				GeneratorResult full = Generator.RunWithGeneratedReferencedAssembly(Granted(Library), PlainConsumer);
				GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Granted(Library), PlainConsumer);

				foreach (GeneratorResult result in new[] { full, reference, })
				{
					await That(result.Diagnostics).HasSingle().Which
						.Contains("*AWT231*'Lib.DataModule.Warmer'*generated no handle for it because the service it is registered under, 'Lib.DataModule.IWarmer', is private to the module, so no container can name it; the InternalsVisibleTo grant to this assembly reaches only internal members, not 'Lib.DataModule.IWarmer', which is private to the module; make it internal or public").AsWildcard();
				}
			}

			[Test]
			public async Task External_IsNotOfferedAGrant()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library, "Other"), PlainConsumer);

				await That(result.Diagnostics).HasSingle().Which
					.Contains("*AWT231*'Lib.DataModule.IWarmer', is private to the module, so no container can name it; make 'Lib.DataModule.IWarmer', which is private to the module, internal or public").AsWildcard()
					.And.DoesNotContain("grant this assembly");
			}
		}

		public class ScanMatchWithSkippedHook
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IPlugin { }
				internal sealed class Clock { }
				public sealed class Roaster : IPlugin { }

				[Module]
				[Scan<IPlugin>(As = ScanAs.Marker, OnActivated = nameof(Activate))]
				public static partial class DataModule
				{
					internal static void Activate(Roaster roaster, Clock clock) { }
				}
				""";

			[Test]
			public async Task External_NamesTheRecordedReasonInsteadOfAWT164()
			{
				GeneratorResult full = Generator.RunWithGeneratedReferencedAssembly(Granted(Library, "Other"), PlainConsumer);
				GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(Granted(Library, "Other"), PlainConsumer);

				foreach (GeneratorResult result in new[] { full, reference, })
				{
					await That(result.Diagnostics).HasSingle().Which
						.Contains("*AWT231*'Lib.DataModule' registers 'Lib.Roaster' with the OnActivated hook 'Activate', which this container cannot call*because its OnActivated hook's parameter of type 'Lib.Clock' is not accessible outside the module's assembly; grant this assembly InternalsVisibleTo in the module's assembly, or make the hook and its parameter types public").AsWildcard()
						.Because("the container constructs the public match, but only the module's wrapper could run its internal hook");
				}
			}

			[Test]
			public async Task Friend_CallsTheHookItself()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Granted(Library), PlainConsumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT101*'Lib.Clock'*").AsWildcard()
					.Because("the friend calls the hook directly and only misses its dependency");
			}
		}
	}
}
