using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt238HandleProviderNotImported
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			public interface IRoaster { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }
			internal sealed class Roaster : IRoaster { public Roaster(BeanStore store) { } }

			[Module]
			[Singleton<BeanStore>]
			public static partial class StorageModule { }

			[Module]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }

			[Module]
			[Singleton<BeanStore>]
			[Singleton<Roaster, IRoaster>]
			public static partial class TeaModule { }
			""";

		private static string Consumer(params string[] modules) => $$"""
			using Awaiten;
			using Lib;

			namespace MyCode;

			[Container]
			{{string.Join("\n", modules.Select(module => $"[Import(typeof({module}))]"))}}
			public static partial class MyContainer;
			""";

		[Test]
		public async Task TheModuleThatDeclaresTheHandleNotImported_ReportsAwt238()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("CoffeeModule"));

			await That(result.Diagnostics).Contains("*AWT238*'Lib.CoffeeModule'*'Lib.BeanStore'*'Lib.StorageModule'*import 'Lib.StorageModule' too*").AsWildcard();
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard()
				.Because("the missing import is the one cause, so the missing handle is not reported again");
		}

		[Test]
		public async Task ADecoratorOfAServiceOnlyTheModuleThatDeclaresTheHandleRegisters_ReportsAwt238Alone()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				internal interface IKettle { }
				internal sealed class Kettle : IKettle { }
				internal sealed class Logging : IKettle { public Logging(IKettle inner) { } }

				[Module]
				[Singleton<Kettle, IKettle>]
				public static partial class KettleModule { }

				[Module]
				[Decorate<Logging, IKettle>]
				public static partial class LoggingModule { }
				""", Consumer("LoggingModule"));

			await That(result.Diagnostics).HasSingle().Which.Contains("*AWT238*'Lib.LoggingModule'*'Lib.IKettle'*'Lib.KettleModule'*").AsWildcard()
				.Because("the missing import is the one cause, so the decorated handle without a registration is not reported again (AWT123)");
		}

		[Test]
		[Arguments("same")]
		[Arguments("friend")]
		[Arguments("full")]
		[Arguments("reference")]
		public async Task ACompositeOfAServiceWithoutMembers_NeedsNoModuleForItsHandle(string consumer)
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IBarista { }
				internal interface IKettle { }
				internal sealed class Kettle : IKettle { }
				internal sealed class All : IKettle { public All(IKettle[] kettles) { } }
				internal sealed class Barista : IBarista { public Barista(IKettle kettle) { } }

				[Module]
				[Singleton<Kettle, IKettle>]
				public static partial class KettleModule { }

				[Module]
				[Composite<All, IKettle>]
				[Singleton<Barista, IBarista>]
				public static partial class AllModule { }
				""";
			const string container = """

				[Container]
				[Import(typeof(AllModule))]
				public static partial class {0};
				""";
			const string usings = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n";
			GeneratorResult result = consumer switch
			{
				"same" => Generator.Run(library + container.Replace("{0}", "SameContainer")),
				"friend" => Generator.RunWithGeneratedReferencedAssembly(
					library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\n\nnamespace Lib;"),
					usings + container.Replace("{0}", "MyContainer")),
				"reference" => Generator.RunWithGeneratedReferenceAssembly(library, usings + container.Replace("{0}", "MyContainer")),
				_ => Generator.RunWithGeneratedReferencedAssembly(library, usings + container.Replace("{0}", "MyContainer")),
			};

			await That(result.Diagnostics).IsEmpty()
				.Because("the imported module builds the composite into the handle itself, which fronts no members, as in the modules' own assembly");
		}

		[Test]
		public async Task ARelationshipOverTheHandleUnderImportServices_ReportsAwt238Alone()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using System;
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class BeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(Lazy<BeanStore> store) { } }

				[Module]
				[Singleton<BeanStore>]
				public static partial class StorageModule { }

				[Module]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[ImportServices]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.Contains("*AWT238*'Lib.CoffeeModule'*'Lib.BeanStore'*'Lib.StorageModule'*").AsWildcard()
				.Because("the missing import is the one cause, so the relationship over the handle without a registration is not reported again, whatever diagnostic it would get");
		}

		[Test]
		public async Task TheModuleThatDeclaresTheHandleImported_DoesNotReportAwt238()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("StorageModule", "CoffeeModule"));

			await That(result.Diagnostics).IsEmpty()
				.Because("the storage module registers the handle the coffee module's factory takes");
		}

		[Test]
		public async Task AnotherImportedModuleThatRegistersTheService_DoesNotReportAwt238()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("TeaModule", "CoffeeModule"));

			await That(result.Diagnostics).IsEmpty()
				.Because("the tea module registers the handle the coffee module's factory takes, though the storage module declares it");
		}

		[Test]
		public async Task AModuleThatRegistersTheServiceItself_DoesNotReportAwt238()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("TeaModule"));

			await That(result.Diagnostics).IsEmpty()
				.Because("the tea module registers the internal service itself, though the storage module declares its handle");
		}

		public class OnlyForMembersTheGraphCalls
		{
			private const string Storage = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class BeanStore { }

				[Module]
				[Singleton<BeanStore>]
				public static partial class StorageModule { }

				""";

			private const string ReplacingContainer = """
				public sealed class HandGrinder : IGrinder { }

				[Container]
				[Import(typeof(CoffeeModule))]
				[Singleton<HandGrinder, IGrinder>]
				public static partial class {0};
				""";

			private const string CoffeeContainer = """
				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class {0};
				""";

			private const string ScanLibrary = """
				using Awaiten;

				namespace Lib;

				public interface IAppliance { }
				internal sealed class TimeSource { }
				public sealed class Clock : IAppliance { internal Clock() { } }

				[Module]
				[Singleton<TimeSource>]
				public static partial class AModule;

				[Module]
				[Singleton<Clock>(Factory = nameof(Create))]
				public static partial class BModule
				{
				    public static Clock Create() => new();
				}

				[Module]
				[Scan<IAppliance>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton, OnActivated = nameof(Start))]
				public static partial class SModule
				{
				    internal static void Start(Clock clock, TimeSource source) { }
				}
				""";

			private const string FactoryModule = """
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<Grinder, IGrinder>(Fallback = Fallback.Warn)]
				public static partial class CoffeeModule { }
				""";

			private const string HookModule = """
				public sealed class Grinder : IGrinder { }

				[Module]
				[Singleton<Grinder, IGrinder>(Fallback = Fallback.Warn, OnActivated = nameof(Activate))]
				public static partial class CoffeeModule
				{
				    internal static void Activate(Grinder grinder, BeanStore store) { }
				}
				""";

			private static GeneratorResult Run(string library, string container, string consumer)
			{
				const string usings = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n";
				return consumer switch
				{
					"same" => Generator.Run(library + "\n" + container.Replace("{0}", "SameContainer")),
					"friend" => Generator.RunWithGeneratedReferencedAssembly(
						library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\n\nnamespace Lib;"),
						usings + container.Replace("{0}", "MyContainer")),
					"reference" => Generator.RunWithGeneratedReferenceAssembly(library, usings + container.Replace("{0}", "MyContainer")),
					_ => Generator.RunWithGeneratedReferencedAssembly(library, usings + container.Replace("{0}", "MyContainer")),
				};
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task AReplacedRegistration_NeedsNoModuleForTheServicesOfItsFactory(string consumer)
			{
				GeneratorResult result = Run(Storage + FactoryModule, ReplacingContainer, consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the container replaces the module's default, so it never calls the factory that takes the internal service, as in the modules' own assembly");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task AReplacedRegistration_NeedsNoModuleForTheServicesOfItsHook(string consumer)
			{
				GeneratorResult result = Run(Storage + HookModule, ReplacingContainer, consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the container replaces the module's default, so it never calls the hook wrapper that takes the internal service, as in the modules' own assembly");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task AScanMatchThatSharesARegistration_NeedsNoModuleForTheServicesOfItsHook(string consumer)
			{
				const string container = """
					[Container]
					[Import(typeof(BModule))]
					[Import(typeof(SModule))]
					public static partial class {0};
					""";
				GeneratorResult result = Run(ScanLibrary, container, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("error AWT166").And.DoesNotContain("AWT238")
					.Because("the match shares the instance of the module's registration, so the container never calls the scan's hook wrapper that takes the internal service, as in the modules' own assembly, and reports the hook that never runs instead");
			}

			[Test]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ARegistrationNotReplaced_NeedsTheModuleOfTheServicesOfItsFactory(string consumer)
			{
				GeneratorResult result = Run(Storage + FactoryModule, CoffeeContainer, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT238").And.Contains("'Lib.CoffeeModule' needs the internal service 'Lib.BeanStore'")
					.Because("the container calls the module's factory, which takes the internal service another module registers");
			}

			[Test]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ARegistrationNotReplaced_NeedsTheModuleOfTheServicesOfItsHook(string consumer)
			{
				GeneratorResult result = Run(Storage + HookModule, CoffeeContainer, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT238").And.Contains("'Lib.CoffeeModule' needs the internal service 'Lib.BeanStore'")
					.Because("the container calls the hook wrapper, which takes the internal service another module registers");
			}

			[Test]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task AScanMatchWithoutARegistration_NeedsTheModuleOfTheServicesOfItsHook(string consumer)
			{
				const string container = """
					[Container]
					[Import(typeof(SModule))]
					public static partial class {0};
					""";
				GeneratorResult result = Run(ScanLibrary, container, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT238").And.Contains("'Lib.SModule' needs the internal service 'Lib.TimeSource'")
					.Because("the scan's factory builds the match, so the container calls its hook wrapper, which takes the internal service another module registers");
			}

			[Test]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ADecorator_NeedsTheModuleOfTheServicesOfItsFactory(string consumer)
			{
				const string library = Storage + """
					public sealed class Grinder : IGrinder { }
					internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, BeanStore store) { } }

					[Module]
					[Singleton<Grinder, IGrinder>]
					[Decorate<LoggingGrinder, IGrinder>]
					public static partial class CoffeeModule { }
					""";
				GeneratorResult result = Run(library, CoffeeContainer, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT238").And.Contains("'Lib.CoffeeModule' needs the internal service 'Lib.BeanStore'")
					.Because("the container calls the decorator's factory, which takes the internal service another module registers");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ADecoratorOfAServiceWithoutARegistration_NeedsNoModuleForTheServicesOfItsFactory(string consumer)
			{
				const string library = Storage + """
					internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, BeanStore store) { } }

					[Module]
					[Decorate<LoggingGrinder, IGrinder>]
					public static partial class CoffeeModule { }
					""";
				GeneratorResult result = Run(library, CoffeeContainer, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT123")
					.Because("nothing registers the decorated service, so the container never calls the decorator's factory, and importing the storage module would not fix it");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			public async Task ADecoratorTheContainerBuildsItself_ReportsTheMissingServiceLikeTheModulesOwnAssembly(string consumer)
			{
				const string library = Storage + """
					public sealed class Grinder : IGrinder { }
					internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, BeanStore store) { } }

					[Module]
					[Singleton<Grinder, IGrinder>]
					[Decorate<LoggingGrinder, IGrinder>]
					public static partial class CoffeeModule { }
					""";
				GeneratorResult result = Run(library, CoffeeContainer, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT101").And.Contains("'Lib.BeanStore'")
					.Because("a container that can name the internal service builds the decorator itself and never calls the factory that takes its handle");
			}
		}
	}
}
