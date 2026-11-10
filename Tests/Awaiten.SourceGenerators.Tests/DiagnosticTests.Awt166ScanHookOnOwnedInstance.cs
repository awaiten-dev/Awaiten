namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt166ScanHookOnOwnedInstance
	{
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

		public class InOneContainer
		{
			[Test]
			public async Task RegistrationWithAnotherHook_Reports()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }

				                                       [Container]
				                                       [Singleton<AlphaPlugin, IPlugin>(OnActivated = nameof(Special))]
				                                       [Scan(typeof(IPlugin), OnActivated = nameof(FromScan))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void Special(IPlugin plugin) { }
				                                       	private static void FromScan(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which
					.Contains("AWT166").And.Contains("'MyCode.AlphaPlugin' is matched by a scan whose OnActivated hook is 'MyCode.MyContainer.FromScan'")
					.And.Contains("whose OnActivated hook is 'MyCode.MyContainer.Special'")
					.Because("the registration owns the instance the match shares, so the scan's hook would never run");
			}

			[Test]
			public async Task RegistrationWithoutAHook_Reports()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }

				                                       [Container]
				                                       [Singleton<AlphaPlugin, IPlugin>]
				                                       [Scan(typeof(IPlugin), OnRelease = nameof(FromScan))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void FromScan(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which
					.Contains("AWT166").And.Contains("scan whose OnRelease hook is 'MyCode.MyContainer.FromScan'").And.Contains("whose OnRelease hook is unset")
					.Because("unlike a second registration, the scan does not lend its hook to the registration, which runs none");
			}

			[Test]
			public async Task RegistrationOfTheConcreteTypeOnly_Reports()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }

				                                       [Container]
				                                       [Transient<AlphaPlugin>]
				                                       [Scan(typeof(IPlugin), As = ScanAs.Marker, OnActivated = nameof(FromScan))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void FromScan(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT166").And.Contains("'MyCode.AlphaPlugin'")
					.Because("the registration owns the instance of the implementation, which the scan's IPlugin mapping serves too");
			}

			[Test]
			public async Task KeyedRegistration_Reports()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }

				                                       [Container]
				                                       [Singleton<AlphaPlugin, IPlugin>(Key = "alpha")]
				                                       [Scan(typeof(IPlugin), Lifetime = Lifetime.Singleton, OnActivated = nameof(FromScan))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void FromScan(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT166").And.Contains("whose OnActivated hook is unset")
					.Because("the keyed registration owns the one instance of the implementation as well");
			}

			[Test]
			public async Task RegistrationNamingTheSameHook_DoesNotReport()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }

				                                       [Container]
				                                       [Singleton<AlphaPlugin, IPlugin>(OnActivated = nameof(Started))]
				                                       [Scan(typeof(IPlugin), OnActivated = nameof(Started))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void Started(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the scan and the registration agree on the method, which runs on the shared instance");
			}

			[Test]
			public async Task ScanWithoutAHook_DoesNotReport()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }
				                                       public sealed class BetaPlugin : IPlugin { }

				                                       [Container]
				                                       [Singleton<AlphaPlugin, IPlugin>(OnActivated = nameof(Special))]
				                                       [Scan(typeof(IPlugin))]
				                                       [Scan(typeof(IPlugin), NamePatterns = new[] { "Beta*" }, OnActivated = nameof(FromScan))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void Special(IPlugin plugin) { }
				                                       	private static void FromScan(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).IsEmpty()
					.Because("a scan without a hook asks for nothing the registration drops, and the hooked scan matches only BetaPlugin, which no registration owns");
			}

			[Test]
			public async Task SameNameOnAModule_Reports()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public sealed class AlphaPlugin : IPlugin { }

				                                       [Module]
				                                       [Singleton<AlphaPlugin, IPlugin>(OnActivated = nameof(Started))]
				                                       public static class PluginModule
				                                       {
				                                       	public static void Started(IPlugin plugin) { }
				                                       }

				                                       [Container]
				                                       [Import(typeof(PluginModule))]
				                                       [Scan(typeof(IPlugin), OnActivated = nameof(Started))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void Started(IPlugin plugin) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT166")
					.And.Contains("'MyCode.MyContainer.Started'").And.Contains("'MyCode.PluginModule.Started'")
					.Because("a hook name is owner-relative, so the module's Started is another method than the container's");
			}

			[Test]
			public async Task TwoScansWithDifferentHooks_ReportTheFirstOnly()
			{
				GeneratorResult result = Generator.Run("""
				                                       using Awaiten;

				                                       namespace MyCode;

				                                       public interface IPlugin { }
				                                       public interface IHandler { }
				                                       public sealed class Widget : IPlugin, IHandler { }

				                                       [Container]
				                                       [Singleton<Widget>]
				                                       [Scan(typeof(IPlugin), OnActivated = nameof(PluginStarted))]
				                                       [Scan(typeof(IHandler), OnActivated = nameof(HandlerStarted))]
				                                       public static partial class MyContainer
				                                       {
				                                       	private static void PluginStarted(object instance) { }
				                                       	private static void HandlerStarted(object instance) { }
				                                       }
				                                       """);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT166").And.Contains("'MyCode.MyContainer.PluginStarted'")
					.Because("the registration owns the instance, so the scans do not merge their hooks and AWT199 does not apply, while each slot is reported once");
			}
		}

		public class ThroughExtends
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IPlugin { }
				public sealed class AlphaPlugin : IPlugin { }

				[Container]
				[Scan<IPlugin>(Lifetime = Lifetime.Singleton, As = ScanAs.Marker, OnActivated = nameof(Started))]
				public static partial class BaseContainer
				{
				    public static void Started(IPlugin plugin) { }
				}
				""";

			[Test]
			[Arguments("same")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task RegistrationOfTheExtendingContainer_DoesNotReportTheBasesScanHook(string consumer)
			{
				GeneratorResult result = Run(Library, """
					[Container(Extends = typeof(BaseContainer))]
					[Singleton<AlphaPlugin>]
					public static partial class {0};
					""", consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the extending container can neither name the base's hook nor exclude the type from the base's scan, so its registration replaces the inherited scan's hook");
			}

			[Test]
			[Arguments("same")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ScanOfTheExtendingContainer_ReportsARegistrationOfTheBase(string consumer)
			{
				GeneratorResult result = Run("""
					using Awaiten;

					namespace Lib;

					public interface IPlugin { }
					public sealed class AlphaPlugin : IPlugin { }

					[Container]
					[Singleton<AlphaPlugin, IPlugin>]
					public static partial class BaseContainer;
					""", """
					[Container(Extends = typeof(BaseContainer))]
					[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, Lifetime = Lifetime.Singleton, As = ScanAs.Marker, OnActivated = nameof(Started))]
					public static partial class {0}
					{
					    private static void Started(IPlugin plugin) { }
					}
					""", consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT166").And.Contains("whose OnActivated hook is unset")
					.Because("the extending container's own scan can still name its hook on a registration that replaces the base's");
			}
		}

		public class AcrossAssemblies
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IPlugin { }
				public sealed class Roaster : IPlugin { }

				[Module]
				[Singleton<Roaster, IPlugin>{1}]{2}
				public static partial class PluginModule
				{
				    public static void Owned(IPlugin plugin) { }
				    public static void Scanned(IPlugin plugin) { }
				}
				""";

			private const string ModuleScan = "\n[Scan<IPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton, OnActivated = nameof(Scanned))]";

			private const string ImportingContainer = """
				[Container]
				[Import(typeof(PluginModule))]
				public static partial class {0};
				""";

			private const string ScanningContainer = """
				[Container]
				[Import(typeof(PluginModule))]
				[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, As = ScanAs.Marker, Lifetime = Lifetime.Singleton, OnActivated = nameof(Scanned))]
				public static partial class {0}
				{
				    public static void Scanned(IPlugin plugin) { }
				}
				""";

			private static string LibraryWith(string registrationHook, bool moduleScan)
				=> Library.Replace("{1}", registrationHook).Replace("{2}", moduleScan ? ModuleScan : "");

			[Test]
			[Arguments("same", "")]
			[Arguments("same", "(OnActivated = nameof(Owned))")]
			[Arguments("friend", "")]
			[Arguments("friend", "(OnActivated = nameof(Owned))")]
			[Arguments("full", "")]
			[Arguments("full", "(OnActivated = nameof(Owned))")]
			[Arguments("reference", "")]
			[Arguments("reference", "(OnActivated = nameof(Owned))")]
			public async Task ModuleScanOfATypeTheModuleRegisters_Reports(string consumer, string registrationHook)
			{
				GeneratorResult result = Run(LibraryWith(registrationHook, moduleScan: true), ImportingContainer, consumer);

				await That(result.Diagnostics).HasSingle().Which.Contains("AWT166")
					.And.Contains("'Lib.Roaster' is matched by a scan whose OnActivated hook is 'Lib.PluginModule.Scanned'")
					.And.Contains(registrationHook == "" ? "whose OnActivated hook is unset" : "whose OnActivated hook is 'Lib.PluginModule.Owned'")
					.Because("the module's registration owns the instance its scan matches in every assembly, and the message names the hooks the module declares rather than their generated wrappers");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ModuleScanNamingTheHookOfTheModulesRegistration_DoesNotReport(string consumer)
			{
				GeneratorResult result = Run(LibraryWith("(OnActivated = nameof(Scanned))", moduleScan: true), ImportingContainer, consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the scan and the registration name the same method of the module, also where a container calls it through generated wrappers");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ContainerScanOfATypeAModuleRegisters_Reports(string consumer)
			{
				GeneratorResult result = Run(LibraryWith("(OnActivated = nameof(Scanned))", moduleScan: false), ScanningContainer, consumer);

				await That(result.Diagnostics).HasCount(1);
				await That(result.Diagnostics).Contains("*AWT166*'Lib.Roaster' is matched by a scan whose OnActivated hook is '*Container.Scanned'*whose OnActivated hook is 'Lib.PluginModule.Scanned'*").AsWildcard()
					.Because("the container's Scanned is another method than the module's, whose registration owns the instance");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ContainerScanOfATypeAModuleBuildsThroughAHandle_Reports(string consumer)
			{
				const string library = """
					using Awaiten;

					namespace Lib;

					public interface IPlugin { }
					internal interface IStore { }
					public sealed class Roaster : IPlugin, IStore { }

					[Module]
					[Singleton<Roaster, IStore>]
					public static partial class PluginModule;
					""";

				GeneratorResult result = Run(library, ScanningContainer, consumer);

				await That(result.Diagnostics).HasCount(1);
				await That(result.Diagnostics).Contains("*AWT166*'Lib.Roaster' is matched by a scan whose OnActivated hook is '*Container.Scanned'*whose OnActivated hook is unset*").AsWildcard()
					.Because("in another assembly the module builds the instance through a generated handle, which the match shares without its hook, as a container of the module's assembly does");
			}
		}
	}
}
