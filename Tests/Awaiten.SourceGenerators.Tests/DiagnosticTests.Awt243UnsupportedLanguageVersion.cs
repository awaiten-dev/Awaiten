using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt243UnsupportedLanguageVersion
	{
		public class Container
		{
			private const string Source = """
				using Awaiten;

				namespace MyCode
				{
				    public interface IGreeter { }
				    public sealed class Greeter : IGreeter { }

				    [Container]
				    [Singleton(typeof(Greeter), typeof(IGreeter))]
				    public static partial class MyContainer { }
				}
				""";

			[Test]
			[Arguments(LanguageVersion.CSharp7_3)]
			[Arguments(LanguageVersion.CSharp8)]
			public async Task BelowCSharp9_ReportsOnlyAwt243AndGeneratesNothing(LanguageVersion languageVersion)
			{
				GeneratorResult result = Generator.Run(languageVersion, Source);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*").AsWildcard()
					.Because("the generated code would not compile, so the build shows the cause instead of compiler errors in it");
				await That(result.Diagnostics).Contains("*MyCode.MyContainer*C# 9.0*").AsWildcard();
				await That(result.Sources).IsEmpty();
			}

			[Test]
			public async Task AtCSharp9_DoesNotReportAwt243()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp9, Source);

				await That(result.Diagnostics).IsEmpty()
					.Because("the generated container code compiles under C# 9");
				await That(result.Sources).IsNotEmpty();
			}

			[Test]
			[Arguments(LanguageVersion.Latest)]
			[Arguments(LanguageVersion.LatestMajor)]
			[Arguments(LanguageVersion.Preview)]
			[Arguments(LanguageVersion.Default)]
			public async Task ASymbolicVersion_DoesNotReportAwt243(LanguageVersion languageVersion)
			{
				GeneratorResult result = Generator.Run(languageVersion, Source);

				await That(result.Diagnostics).IsEmpty()
					.Because("the compiler maps a symbolic version to a concrete one far above C# 9");
			}

			[Test]
			public async Task RaisingTheLanguageVersion_GeneratesTheContainerOnTheNextRun()
			{
				CSharpParseOptions old = new(LanguageVersion.CSharp8);
				CSharpCompilation compilation = CSharpCompilation.Create(
					"TestAssembly",
					[CSharpSyntaxTree.ParseText(Source, old),],
					References.For([]),
					new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
				GeneratorDriver driver = CSharpGeneratorDriver.Create([new AwaitenGenerator().AsSourceGenerator(),], [], old, null)
					.RunGenerators(compilation);

				CSharpParseOptions raised = new(LanguageVersion.CSharp9);
				driver = driver.WithUpdatedParseOptions(raised).RunGenerators(compilation
					.RemoveAllSyntaxTrees()
					.AddSyntaxTrees(compilation.SyntaxTrees.Select(tree => tree.WithRootAndOptions(tree.GetRoot(), raised))));
				GeneratorRunResult result = driver.GetRunResult().Results.Single();

				await That(result.Diagnostics).IsEmpty()
					.Because("a changed language version re-parses every tree, so the cached AWT243 model is not reused");
				await That(result.GeneratedSources).IsNotEmpty();
			}

			[Test]
			public async Task AContainerWithRegistrationErrors_ReportsOnlyAwt243()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp8, """
					using Awaiten;

					namespace MyCode
					{
					    public interface IGreeter { }
					    public sealed class Greeter : IGreeter { public Greeter(string name) { } }

					    [Container]
					    [Singleton(typeof(Greeter), typeof(IGreeter))]
					    public static partial class MyContainer { }
					}
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*").AsWildcard()
					.Because("the graph is not analyzed until the generated code can compile, so AWT101 follows once the version is raised");
			}

			[Test]
			[Arguments(LanguageVersion.CSharp8, false)]
			[Arguments(LanguageVersion.CSharp9, true)]
			public async Task TheAnalyzer_ChecksOnlyAContainerThatIsGenerated(LanguageVersion languageVersion, bool reported)
			{
				string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>(languageVersion, """
					using System;
					using Awaiten;

					namespace MyCode
					{
					    public sealed class Tool : IDisposable { public void Dispose() { } }
					    public sealed class Depot { public Depot(Func<Tool> tools) { } }

					    [Container]
					    [Transient(typeof(Tool))]
					    [Singleton(typeof(Depot))]
					    public static partial class MyContainer { }
					}
					""");

				await That(diagnostics.Any(diagnostic => diagnostic.Contains("AWT118"))).IsEqualTo(reported)
					.Because("below C# 9 the container is not generated, so AWT243 is the only diagnostic it reports");
			}
		}

		public class Module
		{
			private const string ScanModule = """
				using Awaiten;

				namespace Lib
				{
				    public interface IHandler { }
				    internal sealed class OrderHandler : IHandler { }

				    [Module]
				    [Scan(typeof(IHandler), As = ScanAs.Marker)]
				    public static partial class HandlerModule { }
				}
				""";

			private const string RegistrationModule = """
				using Awaiten;

				namespace Lib
				{
				    public interface IGrinder { }
				    internal sealed class Grinder : IGrinder { }

				    [Module]
				    [Singleton(typeof(Grinder), typeof(IGrinder))]
				    public static partial class GrinderModule { }
				}
				""";

			[Test]
			[Arguments(LanguageVersion.CSharp9)]
			[Arguments(LanguageVersion.CSharp10)]
			public async Task AScanBelowCSharp11_ReportsOnlyAwt243AndGeneratesNothing(LanguageVersion languageVersion)
			{
				GeneratorResult result = Generator.Run(languageVersion, ScanModule);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*").AsWildcard()
					.Because("the registrations a module generates for its scan matches are generic attributes");
				await That(result.Diagnostics).Contains("*Lib.HandlerModule*C# 11.0*").AsWildcard();
				await That(result.Sources).IsEmpty();
			}

			[Test]
			public async Task AScanAtCSharp11_DoesNotReportAwt243()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp11, ScanModule);

				await That(result.Diagnostics).IsEmpty();
				await That(result.Sources).IsNotEmpty();
			}

			[Test]
			public async Task ARegistrationBelowCSharp9_ReportsAwt243()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp8, RegistrationModule);

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*").AsWildcard();
				await That(result.Diagnostics).Contains("*Lib.GrinderModule*C# 9.0*").AsWildcard();
				await That(result.Sources).IsEmpty();
			}

			[Test]
			public async Task ARegistrationAtCSharp9_DoesNotReportAwt243()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp9, RegistrationModule);

				await That(result.Diagnostics).IsEmpty()
					.Because("a module that declares no scan generates no generic attribute");
				await That(result.Sources).IsNotEmpty();
			}

			[Test]
			public async Task AModuleThatGeneratesNothing_DoesNotReportAwt243()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp7_3, """
					using Awaiten;

					namespace Lib
					{
					    public interface IGrinder { }
					    public sealed class Grinder : IGrinder { }

					    [Module]
					    [Singleton(typeof(Grinder), typeof(IGrinder))]
					    public static class GrinderModule { }
					}
					""");

				await That(result.Diagnostics).IsEmpty()
					.Because("a module whose registrations a consumer constructs itself has no generated code to compile");
			}

			[Test]
			public async Task AContainerImportingAScanModuleOfItsOwnAssemblyBelowCSharp11_ReportsAwt243OnlyOnTheModule()
			{
				GeneratorResult result = Generator.Run(LanguageVersion.CSharp10, """
					using System.Collections.Generic;
					using Awaiten;

					namespace Lib
					{
					    public interface IHandler { }
					    internal sealed class OrderHandler : IHandler { }

					    [Module]
					    [Scan(typeof(IHandler), As = ScanAs.Marker)]
					    public static partial class HandlerModule { }

					    [Container]
					    [Import(typeof(HandlerModule))]
					    public static partial class AppContainer { }

					    public static class Use
					    {
					        public static IEnumerable<IHandler> Handlers(AppContainer.Root root) => root.Resolve<IEnumerable<IHandler>>();
					    }
					}
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*Lib.HandlerModule*").AsWildcard()
					.Because("a container of the module's own assembly expands the scan itself and needs only C# 9");
			}

			[Test]
			[Arguments(LanguageVersion.CSharp9)]
			[Arguments(LanguageVersion.CSharp10)]
			public async Task AContainerImportingAScanModuleWithAPrivateRegistrationOfItsOwnAssemblyBelowCSharp11_ReportsOnlyAwt243(LanguageVersion languageVersion)
			{
				GeneratorResult result = Generator.Run(languageVersion, """
					using Awaiten;

					namespace Shop
					{
					    public interface IPrinter { }
					    internal sealed class Printer : IPrinter { }
					    public interface IClock { }

					    [Module]
					    [Scan(typeof(IPrinter))]
					    [Singleton(typeof(ShopModule.FakeClock), typeof(IClock))]
					    public static partial class ShopModule
					    {
					        private sealed class FakeClock : IClock { }
					    }

					    [Container]
					    [Import(typeof(ShopModule))]
					    public static partial class ShopContainer { }

					    public static class Use
					    {
					        public static IClock Clock(ShopContainer.Root root) => root.Resolve<IClock>();
					    }
					}
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*Shop.ShopModule*").AsWildcard()
					.Because("the container would build the private clock through the factory the skipped module does not generate, so it emits only its shell");
			}

			[Test]
			[Arguments(LanguageVersion.CSharp9)]
			[Arguments(LanguageVersion.CSharp10)]
			public async Task ModulesSharingHandlesWithAScanModuleOfTheirAssemblyBelowCSharp11_ReportOnlyAwt243(LanguageVersion languageVersion)
			{
				GeneratorResult result = Generator.Run(languageVersion, """
					using Awaiten;

					namespace Lib
					{
					    public interface ILamp { }
					    public interface IUser { }
					    internal interface IBulbSource { }
					    internal sealed class Bulb : ILamp, IBulbSource { }
					    internal sealed class User : IUser { public User(IBulbSource bulb) { } }

					    [Module]
					    [Singleton(typeof(Bulb), typeof(IBulbSource))]
					    [Singleton(typeof(User), typeof(IUser))]
					    public static partial class BulbModule { }

					    [Module]
					    [Scan(typeof(ILamp), As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
					    public static partial class LampScanModule { }

					    [Container]
					    [Import(typeof(BulbModule))]
					    [Import(typeof(LampScanModule))]
					    public static partial class AppContainer { }

					    public static class Use
					    {
					        public static IUser User(AppContainer.Root root) => root.Resolve<IUser>();
					    }
					}
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*Lib.LampScanModule*").AsWildcard()
					.Because("no generated code may refer to the members the skipped scan module does not generate");
			}

			[Test]
			[Arguments(LanguageVersion.CSharp9)]
			[Arguments(LanguageVersion.CSharp10)]
			public async Task AModuleTakingTheHandleOfAScanModuleOfItsAssemblyBelowCSharp11_ReportsOnlyAwt243(LanguageVersion languageVersion)
			{
				GeneratorResult result = Generator.Run(languageVersion, """
					using Awaiten;

					namespace Lib
					{
					    public interface IHandler { }
					    public interface IGrinder { }
					    internal sealed class OrderHandler : IHandler { }
					    internal sealed class BeanStore { }
					    internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

					    [Module]
					    [Scan(typeof(IHandler), As = ScanAs.Marker)]
					    [Singleton(typeof(BeanStore))]
					    public static partial class StorageModule { }

					    [Module]
					    [Singleton(typeof(Grinder), typeof(IGrinder))]
					    public static partial class CoffeeModule { }
					}
					""");

				await That(result.Diagnostics).HasSingle().Which.Contains("*AWT243*Lib.StorageModule*").AsWildcard()
					.Because("the module that takes the store's handle must not refer to the handle the skipped scan module does not generate");
			}

			[Test]
			public async Task AContainerReadingAScanModuleCompiledWithCSharp11_DoesNotNeedCSharp11()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
					LanguageVersion.CSharp11,
					ScanModule,
					LanguageVersion.CSharp9,
					"""
					using System.Collections.Generic;
					using Awaiten;

					namespace App
					{
					    [Container]
					    [Import(typeof(Lib.HandlerModule))]
					    public static partial class AppContainer { }

					    public static class Use
					    {
					        public static IEnumerable<Lib.IHandler> Handlers(AppContainer.Root root) => root.Resolve<IEnumerable<Lib.IHandler>>();
					    }
					}
					""");

				await That(result.Diagnostics).IsEmpty()
					.Because("the container reads the generic attributes from metadata but emits none itself");
			}
		}
	}
}
