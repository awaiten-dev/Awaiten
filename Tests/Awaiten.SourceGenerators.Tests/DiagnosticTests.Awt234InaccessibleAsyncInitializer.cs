namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt234InaccessibleAsyncInitializer
	{
		[Test]
		public async Task ReportsAConventionParameterOfAnInternalTypeOnASelfCompiledRegistration()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       internal sealed class Settings { }
			                                       internal interface IStartable { Task StartAsync(Settings settings); }
			                                       public interface ITerminal { }
			                                       internal sealed class Terminal : ITerminal, IStartable { public Task StartAsync(Settings settings) => Task.CompletedTask; }

			                                       [Module]
			                                       [AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			                                       [Singleton<Terminal, ITerminal>]
			                                       public static partial class TerminalModule;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Terminal' is initialized by the async initializer 'Lib.IStartable.StartAsync', but its parameter 'settings' has the type 'Lib.Settings', which is not public, so the module can generate no wrapper through which a container in another assembly runs it; make the parameter type public or register it in the module")
				.Because("a consumer in another assembly runs the internal convention through the module's public wrapper, which cannot take an internal parameter the module does not register");
		}

		[Test]
		public async Task DoesNotReportAConventionParameterOfAnInternalServiceTheModuleRegisters()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       internal sealed class Settings { }
			                                       internal interface IStartable { Task StartAsync(Settings settings); }
			                                       public interface ITerminal { }
			                                       internal sealed class Terminal : ITerminal, IStartable { public Task StartAsync(Settings settings) => Task.CompletedTask; }

			                                       [Module]
			                                       [AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			                                       [Singleton<Settings>]
			                                       [Singleton<Terminal, ITerminal>]
			                                       public static partial class TerminalModule;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the module's wrapper takes the internal service through the handle the module generates for it");
			await That(result.Sources["Awaiten.ModuleScan.Lib.TerminalModule.g.cs"]).Contains("Initializer_IStartable_");
		}

		[Test]
		public async Task ReportsAConventionParameterThatTakesAnInternalServiceInAShapeTheWrapperCannotPassOn()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       internal sealed class Settings { }
			                                       internal interface IStartable { Task StartAsync(Lazy<Func<Settings>> settings); }
			                                       public interface ITerminal { }
			                                       internal sealed class Terminal : ITerminal, IStartable { public Task StartAsync(Lazy<Func<Settings>> settings) => Task.CompletedTask; }

			                                       [Module]
			                                       [AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			                                       [Singleton<Settings>]
			                                       [Singleton<Terminal, ITerminal>]
			                                       public static partial class TerminalModule;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
				"error AWT234: 'Lib.Terminal' is initialized by the async initializer 'Lib.IStartable.StartAsync', but its parameter 'settings' takes the internal service 'Lib.Settings' as 'System.Lazy<System.Func<Lib.Settings>>', which the wrapper cannot pass on, so the module can generate no wrapper through which a container in another assembly runs it; take 'Lib.Settings' directly, or as Func<T>, Lazy<T>, Owned<T>, Func<Owned<T>>, Func<TArg..., T>, Func<TArg..., Owned<T>> or a collection of T");
		}

		[Test]
		public async Task ReportsAConventionParameterOfAnInternalTypeOnAScanMatch()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       internal sealed class Settings { }
			                                       public abstract class Machine { internal virtual Task StartAsync(Settings settings) => Task.CompletedTask; }
			                                       public interface IPlugin { }
			                                       public interface IPrinter { }
			                                       internal sealed class Printer : Machine, IPlugin, IPrinter { }

			                                       [Module]
			                                       [AsyncInitializer<Machine>(nameof(Machine.StartAsync))]
			                                       [Scan<IPlugin>(As = ScanAs.MatchingInterface)]
			                                       public static partial class PrinterModule;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Printer' is initialized by the async initializer 'Lib.Machine.StartAsync', but its parameter 'settings' has the type 'Lib.Settings', which is not public, so the module can generate no wrapper through which a container in another assembly runs it; make the parameter type public or register it in the module");
		}

		[Test]
		public async Task DoesNotReportAnInternalConventionTheModuleWraps()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       internal interface IStartable { Task StartAsync(); }
			                                       public interface ITerminal { }
			                                       internal sealed class Terminal : ITerminal, IStartable { public Task StartAsync() => Task.CompletedTask; }

			                                       [Module]
			                                       [AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			                                       [Singleton<Terminal, ITerminal>]
			                                       public static partial class TerminalModule;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a consumer in another assembly runs the internal convention through the wrapper the module generates");
			await That(result.Sources["Awaiten.ModuleScan.Lib.TerminalModule.g.cs"]).Contains("Initializer_IStartable_");
		}

		[Test]
		public async Task NamesAConventionTypePrivateToAnInternalModule()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       public interface ITerminal { }

			                                       [Module]
			                                       [AsyncInitializer<TerminalModule.IStartable>(nameof(TerminalModule.IStartable.StartAsync))]
			                                       [Singleton<TerminalModule.Terminal, ITerminal>]
			                                       internal static partial class TerminalModule
			                                       {
			                                       	private interface IStartable { Task StartAsync(); }

			                                       	private sealed class Terminal : ITerminal, IStartable { public Task StartAsync() => Task.CompletedTask; }
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT234*Terminal*IStartable.StartAsync*'Lib.TerminalModule.IStartable' is private to the module*").AsWildcard()
				.Because("the type is hidden even from the module's own assembly, so being outside that assembly is not the reason");
			await That(result.Diagnostics).DoesNotContain("*outside the module's assembly*").AsWildcard();
		}

		[Test]
		public async Task DoesNotReportAConventionOfAModuleWithNothingSelfCompiled()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace Lib;

			                                       internal interface IStartable { Task StartAsync(); }
			                                       internal sealed class Terminal : IStartable { public Task StartAsync() => Task.CompletedTask; }

			                                       [Module]
			                                       [AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			                                       [Singleton<Terminal>]
			                                       internal static partial class TerminalModule;

			                                       [Container]
			                                       [Import(typeof(TerminalModule))]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("an internal module never reaches another assembly, so its internal convention is callable wherever it is imported");
		}

		[Test]
		public async Task ReportsAnInternalConventionOfAnotherAssemblyAtTheContainer()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly("""
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				internal interface IStartable { Task StartAsync(); }
				public sealed class Terminal : IStartable { public Task StartAsync() => Task.CompletedTask; }

				[Module]
				[AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
				[Singleton<Terminal>]
				public static class TerminalModule;
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(TerminalModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Terminal' is initialized by the async initializer 'Lib.IStartable.StartAsync', but 'Lib.IStartable' is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.TerminalModule' generated no wrapper for it; make it public, or grant this assembly InternalsVisibleTo in its assembly")
				.Because("the container constructs the public terminal itself but cannot cast it to the internal convention type, and a module built without the generator carries no wrapper");
		}

		[Test]
		public async Task ReportsAnInternalConventionMethodOfABaseContainerBuiltWithoutTheGenerator()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly("""
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				public sealed class Terminal { internal Task StartAsync() => Task.CompletedTask; }

				[Container]
				[AsyncInitializer<Terminal>(nameof(Terminal.StartAsync))]
				[Singleton<Terminal>]
				public static partial class Shop;
				""", """
				using Awaiten;

				namespace MyCode;

				[Container(Extends = typeof(Lib.Shop))]
				public static partial class TestShop;
				""");

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Terminal' is initialized by the async initializer 'Lib.Terminal.StartAsync', but the method is not accessible from the container 'MyCode.TestShop', and the container 'Lib.Shop' generated no wrapper for it; make it public, or grant this assembly InternalsVisibleTo in its assembly")
				.Because("a base built without the generator carries no wrapper, and the extending container must not leave the inherited convention out silently");
		}

		[Test]
		public async Task NamesTheRemedyAModuleRecordedForAConventionTypePrivateToIt()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				[Module]
				[AsyncInitializer<TerminalModule.IStartable>("StartAsync")]
				[Singleton<TerminalModule.Terminal>]
				public static partial class TerminalModule
				{
					private interface IStartable { Task StartAsync(); }

					public sealed class Terminal : IStartable { Task IStartable.StartAsync() => Task.CompletedTask; }
				}
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(TerminalModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.TerminalModule.Terminal' is initialized by the async initializer 'Lib.TerminalModule.IStartable.StartAsync', but 'Lib.TerminalModule.IStartable' is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.TerminalModule' generated no wrapper for it because 'Lib.TerminalModule.IStartable' is private to the module; make 'Lib.TerminalModule.IStartable' internal or public, or grant this assembly InternalsVisibleTo in the module's assembly")
				.Because("the remedy follows the cause the module recorded, and InternalsVisibleTo alone would not let the container cast to a private type");
		}

		[Test]
		public async Task AsksForAConventionTypePrivateToAModuleOfTheSameAssemblyToBeInternal()
		{
			GeneratorResult result = Generator.Run("""
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				[Module]
				[AsyncInitializer<TerminalModule.IStartable>("StartAsync")]
				[Singleton<TerminalModule.Terminal>]
				public static partial class TerminalModule
				{
					private interface IStartable { Task StartAsync(); }

					public sealed class Terminal : IStartable { Task IStartable.StartAsync() => Task.CompletedTask; }
				}

				[Container]
				[Import(typeof(TerminalModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.TerminalModule.Terminal' is initialized by the async initializer 'Lib.TerminalModule.IStartable.StartAsync', but 'Lib.TerminalModule.IStartable' is not accessible from the container 'Lib.MyContainer'; make it internal or public")
				.Because("a container in the module's own assembly cannot use a wrapper the module generates in the same run, and InternalsVisibleTo does not apply within one assembly");
		}

		[Test]
		public async Task ASkippedRegistrationNamesTheConventionInAwt231()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				AsyncInitializerConventionTests.CrossAssembly.FriendLibrary
					.Replace("\"TestAssembly\"", "\"SomeFriend\"")
					.Replace("Task StartAsync()", "Task StartAsync(Settings settings)")
					.Replace("public interface ITerminal { }", "public interface ITerminal { }\ninternal sealed class Settings { }"), """
					using Awaiten;
					using Lib;

					namespace MyCode;

					[Container]
					[Import(typeof(TerminalModule))]
					public static partial class MyContainer;
					""");

			await That(result.Diagnostics).Contains("*AWT231*Terminal*async initializer*IStartable.StartAsync*").AsWildcard()
				.Because("the module left the terminal to the assemblies granted InternalsVisibleTo, and a consumer without access learns why it has no factory");
		}
	}
}
