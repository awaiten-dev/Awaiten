using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     AWT245 is reported by <see cref="AwaitenAnalyzer" /> rather than the generator, so these tests drive the analyzer
	///     for the report and the generator for warnings in the generated code.
	/// </summary>
	public class Awt245NullableProduct
	{
		private const string Kettle = """
			public interface IKettle { }
			public sealed class Kettle : IKettle { }
			public struct Grind : IKettle { }
			public sealed class Brewer
			{
			    public Brewer(IKettle kettle) { }
			}
			""";

		private static string Container(string registration, string members) => $$"""
			#nullable enable
			using Awaiten;
			using System.Diagnostics.CodeAnalysis;
			using System.Threading.Tasks;

			namespace MyCode;

			{{Kettle}}

			[Container]
			{{registration}}
			[Transient<Brewer>]
			public static partial class MyContainer
			{
			    {{members}}
			}
			""";

		[Test]
		[Arguments("Singleton", "IKettle", "IKettle?", "new Kettle()")]
		[Arguments("Scoped", "IKettle", "IKettle?", "new Kettle()")]
		[Arguments("Transient", "IKettle", "IKettle?", "new Kettle()")]
		[Arguments("Singleton", "Kettle, IKettle", "Kettle?", "new Kettle()")]
		[Arguments("Transient", "Kettle, IKettle", "Kettle?", "new Kettle()")]
		[Arguments("Singleton", "IKettle", "Task<IKettle?>", "Task.FromResult<IKettle?>(new Kettle())")]
		[Arguments("Transient", "Kettle, IKettle", "Task<Kettle?>", "Task.FromResult<Kettle?>(new Kettle())")]
		public async Task AFactoryThatMayReturnNull_ReportsAwt245AndCompilesWithoutWarnings(string lifetime, string registration, string returnType, string body)
		{
			string source = Container(
				$"[{lifetime}<{registration}>(Factory = nameof(Make))]",
				$"private static {returnType} Make() => {body};");

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty()
				.Because("the generated code compiles without nullable warnings and the generator leaves the report to the analyzer");
			await That(reports).HasCount(1).And.Contains("*warning AWT245*The Factory 'Make' may produce null*'MyCode.IKettle'*").AsWildcard()
				.Because("the declared nullable result is reported once at the registration instead of as nullable warnings in the generated code");
			await That(reports[0]).StartsWith($"({LineOf(source, "Factory = nameof(Make)")},")
				.Because("the warning points at the registration");
		}

		[Test]
		[Arguments("IKettle", "IKettle?")]
		[Arguments("Kettle, IKettle", "Kettle?")]
		public async Task AnInstanceThatMayBeNull_ReportsAwt245AndCompilesWithoutWarnings(string registration, string memberType)
		{
			string source = Container(
				$"[Singleton<{registration}>(Instance = nameof(Shared))]",
				$"private static {memberType} Shared {{ get; }} = new Kettle();");

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty();
			await That(reports).HasCount(1).And.Contains("*warning AWT245*The Instance 'Shared' may produce null*").AsWildcard()
				.Because("an Instance member of a nullable type is held to the same rule as a factory");
		}

		[Test]
		[Arguments("Singleton", "Kettle, IKettle", "Kettle? Make() => new Kettle()")]
		[Arguments("Scoped", "IKettle", "IKettle? Make() => new Kettle()")]
		[Arguments("Transient", "Kettle, IKettle", "Task<Kettle?> Make() => Task.FromResult<Kettle?>(new Kettle())")]
		public async Task AFactoryOfAnInternalImplementation_ReportsAwt245AndCompilesWithoutWarnings(string lifetime, string registration, string factory)
		{
			string source = Container($"[{lifetime}<{registration}>(Factory = nameof(Make))]", $"private static {factory};")
				.Replace("public sealed class Kettle", "internal sealed class Kettle");

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty()
				.Because("the resolvers of an internal implementation return an accessible type and test the value for disposal at run time, which must not warn either");
			await That(reports).HasCount(1).And.Contains("*warning AWT245*'Make'*").AsWildcard();
		}

		[Test]
		[Arguments("[Singleton<IKettle>(Factory = nameof(Make))]", "[return: MaybeNull] private static IKettle Make() => new Kettle();", "Factory 'Make'*'[MaybeNull] MyCode.IKettle'")]
		[Arguments("[Transient<Kettle, IKettle>(Factory = nameof(Make))]", "[return: MaybeNull] private static Kettle Make() => new Kettle();", "Factory 'Make'*'[MaybeNull] MyCode.Kettle'")]
		[Arguments("[Singleton<IKettle>(Instance = nameof(Shared))]", "[MaybeNull] private static Kettle Shared { get; } = new Kettle();", "Instance 'Shared'*'[MaybeNull] MyCode.Kettle'")]
		[Arguments("[Singleton<IKettle>(Instance = nameof(Shared))]", "[MaybeNull] private static readonly IKettle Shared = new Kettle();", "Instance 'Shared'*'[MaybeNull] MyCode.IKettle'")]
		public async Task AMemberMarkedMaybeNull_ReportsAwt245AndCompilesWithoutWarnings(string registration, string member, string expected)
		{
			string source = Container(registration, member);

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty()
				.Because("[MaybeNull] makes the result nullable to the compiler like a ?, so the generated code forgives it too");
			await That(reports).HasCount(1).And.Contains($"*warning AWT245*The {expected}*").AsWildcard();
		}

		[Test]
		[Arguments("[Singleton<IKettle>(Factory = nameof(Make))]", "private static Grind? Make() => new Grind();", "Factory 'Make'")]
		[Arguments("[Transient<IKettle>(Factory = nameof(Make))]", "private static Task<Grind?> Make() => Task.FromResult<Grind?>(new Grind());", "Factory 'Make'")]
		[Arguments("[Singleton<IKettle>(Instance = nameof(Shared))]", "private static Grind? Shared { get; } = new Grind();", "Instance 'Shared'")]
		public async Task ANullableValueTypeBoxedToTheService_ReportsAwt245AndCompilesWithoutWarnings(string registration, string member, string subject)
		{
			string source = Container(registration, member);

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty()
				.Because("a Nullable<Grind> without a value boxes to a null IKettle, so the generated code forgives it like a nullable reference");
			await That(reports).HasCount(1).And.Contains($"*warning AWT245*The {subject} may produce null, since it is declared to produce 'MyCode.Grind?'*a 'MyCode.Grind' that is never null*").AsWildcard();
		}

		[Test]
		public async Task AnOpenGenericFactory_ReportsOnceAtTheOpenRegistration()
		{
			string source = """
			                #nullable enable
			                using Awaiten;

			                namespace MyCode;

			                public interface IRepo<T> { }
			                public sealed class Repo<T> : IRepo<T> { }
			                public sealed class Brewer
			                {
			                    public Brewer(IRepo<int> numbers, IRepo<string> names) { }
			                }

			                [Container]
			                [Singleton(typeof(Repo<>), typeof(IRepo<>), Factory = nameof(Make))]
			                [Transient<Brewer>]
			                public static partial class MyContainer
			                {
			                    private static Repo<T>? Make<T>() => new Repo<T>();
			                }
			                """;

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty()
				.Because("every closing forgives the nullable result of the generic factory");
			await That(reports).HasCount(1).And.Contains("*warning AWT245*The Factory 'Make' may produce null, since it is declared to produce 'MyCode.Repo<T>?'*'MyCode.IRepo<T>'*").AsWildcard()
				.Because("the open registration declares the factory once, whatever closings the graph injects");
			await That(reports[0]).StartsWith($"({LineOf(source, "typeof(Repo<>)")},")
				.Because("the warning points at the open registration");
		}

		[Test]
		public async Task TheMessage_SaysWhatANullResultDoesAndHowToAvoidIt()
		{
			string source = Container("[Singleton<IKettle>(Factory = nameof(Make))]", "private static IKettle? Make() => new Kettle();");

			string[] reports = await Awt245Reports(source);

			await That(reports).HasCount(1).And.Contains("*never checks for null*receive null*runs again on the next synchronous resolve*declare it to produce a 'MyCode.IKettle' that is never null*null object*").AsWildcard()
				.Because("the container hands a null result on unchecked and treats a null cached instance as not built yet, so the message names the consequence and both remedies");
		}

		[Test]
		public async Task PragmaAroundTheRegistration_SuppressesIt()
		{
			string source = Container(
				"""
				#pragma warning disable AWT245
				[Singleton<IKettle>(Factory = nameof(Make))]
				#pragma warning restore AWT245
				""",
				"private static IKettle? Make() => new Kettle();");

			string[] reports = await Awt245Reports(source);

			await That(reports).IsEmpty()
				.Because("the diagnostic carries a source location, so #pragma warning disable silences it at this registration alone");
		}

		[Test]
		public async Task AFactoryOfAModuleInTheSameAssembly_ReportsAwt245AndCompilesWithoutWarnings()
		{
			string source = $$"""
				#nullable enable
				using Awaiten;

				namespace MyCode;

				{{Kettle}}

				[Module]
				[Singleton<IKettle>(Factory = nameof(Make))]
				public static partial class KettleModule
				{
				    internal static IKettle? Make() => new Kettle();
				}

				[Container]
				[Import(typeof(KettleModule))]
				[Transient<Brewer>]
				public static partial class MyContainer { }

				[Container]
				[Import(typeof(KettleModule))]
				[Transient<Brewer>]
				public static partial class OtherContainer { }
				""";

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty();
			await That(reports).HasCount(1).And.Contains("*warning AWT245*'Make'*").AsWildcard()
				.Because("both containers report the same registration of the module, which is reported once");
			await That(reports[0]).StartsWith($"({LineOf(source, "Factory = nameof(Make)")},")
				.Because("the warning points at the module's registration");
		}

		public sealed class AcrossAssemblies
		{
			private static string Library(string implementation, string registration, string factory, bool grant) => $$"""
				#nullable enable
				using Awaiten;

				{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

				namespace Lib;

				public interface IKettle { }
				{{implementation}} sealed class Kettle : IKettle { }

				[Module]
				[Singleton<{{registration}}>(Factory = nameof(Make))]
				public static partial class KettleModule
				{
				    {{factory}} Make() => new Kettle();
				}
				""";

			private const string Consumer = """
				#nullable enable
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class Brewer
				{
				    public Brewer(IKettle kettle) { }
				}

				[Container]
				[Import(typeof(KettleModule))]
				[Transient<Brewer>]
				public static partial class MyContainer { }
				""";

			[Test]
			[Arguments("public", "Kettle, IKettle", "public static Kettle?", false)]
			[Arguments("public", "IKettle", "public static IKettle?", false)]
			[Arguments("internal", "IKettle", "public static IKettle?", false)]
			[Arguments("internal", "Kettle, IKettle", "internal static Kettle?", true)]
			[Arguments("internal", "IKettle", "internal static IKettle?", true)]
			[Arguments("internal", "IKettle", "[return: System.Diagnostics.CodeAnalysis.MaybeNull] internal static IKettle", true)]
			public async Task AFactoryOfAModuleInAnotherAssembly_ReportsAwt245AndCompilesWithoutWarnings(string implementation, string registration, string factory, bool grant)
			{
				string source = Library(implementation, registration, factory, grant);
				GeneratorResult library = Generator.Run(source);
				string[] libraryReports = await Awt245Reports(source);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(source, Consumer);
				string[] reports = await Awt245Reports(source, Consumer);

				await That(library.Diagnostics).IsEmpty()
					.Because("the module's own generated code compiles without warnings");
				await That(libraryReports).IsEmpty()
					.Because("the module builds no handle over the member, so the containers that call it report it");
				await That(result.Diagnostics).IsEmpty();
				await That(reports).HasCount(1).And.Contains("*warning AWT245*'Make'*").AsWildcard()
					.Because("the container that hands the result to its consumers reports the possible null once");
			}

			[Test]
			public async Task AMaybeNullFactoryOfAModuleInAnotherAssemblyCalledThroughItsWrapper_ReportsAwt245AndCompilesWithoutWarnings()
			{
				string source = Library("public", "IKettle", "[return: System.Diagnostics.CodeAnalysis.MaybeNull] internal static IKettle", false);
				GeneratorResult library = Generator.Run(source);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(source, Consumer);
				string[] reports = await Awt245Reports(source, Consumer);

				await That(library.Diagnostics).IsEmpty()
					.Because("the public wrapper over the internal factory spells [MaybeNull] as a nullable return");
				await That(result.Diagnostics).IsEmpty();
				await That(reports).HasCount(1)
					.Because("the container sees the possible null through the wrapper");
			}
			[Test]
			public async Task AnOpenGenericFactoryOfAModuleInAnotherAssembly_ReportsOnceAndCompilesWithoutWarnings()
			{
				const string library = """
				                       #nullable enable
				                       using Awaiten;

				                       namespace Lib;

				                       public interface IRepo<T> { }
				                       public sealed class Repo<T> : IRepo<T> { }

				                       [Module]
				                       [Singleton(typeof(Repo<>), typeof(IRepo<>), Factory = nameof(Make))]
				                       public static partial class RepoModule
				                       {
				                           public static Repo<T>? Make<T>() => new Repo<T>();
				                       }
				                       """;
				const string consumer = """
				                        #nullable enable
				                        using Awaiten;
				                        using Lib;

				                        namespace MyCode;

				                        public sealed class Brewer
				                        {
				                            public Brewer(IRepo<int> numbers, IRepo<string> names) { }
				                        }

				                        [Container]
				                        [Import(typeof(RepoModule))]
				                        [Transient<Brewer>]
				                        public static partial class MyContainer { }
				                        """;

				GeneratorResult libraryResult = Generator.Run(library);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
				string[] reports = await Awt245Reports(library, consumer);

				await That(libraryResult.Diagnostics).IsEmpty();
				await That(result.Diagnostics).IsEmpty()
					.Because("every closing forgives the nullable result of the module's generic factory");
				await That(reports).HasCount(1).And.Contains("*warning AWT245*'Lib.Repo<T>?'*'Lib.IRepo<T>'*").AsWildcard()
					.Because("the container reports the open registration once, whatever closings it injects");
			}

			[Test]
			[Arguments("private static Secret? Make() => new Secret();", "Factory = nameof(Make)")]
			[Arguments("[return: System.Diagnostics.CodeAnalysis.MaybeNull] private static Secret Make() => new Secret();", "Factory = nameof(Make)")]
			[Arguments("private static Secret? Shared { get; } = new Secret();", "Instance = nameof(Shared)")]
			public async Task AMemberOfAModuleReturningAPrivateNullableType_CompilesWithoutWarnings(string member, string production)
			{
				string library = $$"""
				                   #nullable enable
				                   using Awaiten;

				                   [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

				                   namespace Lib;

				                   public interface IKettle { }

				                   [Module]
				                   [Singleton<IKettle>({{production}})]
				                   internal static partial class KettleModule
				                   {
				                       private sealed class Secret : IKettle { }

				                       {{member}}
				                   }
				                   """;

				GeneratorResult libraryResult = Generator.Run(library);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);
				string[] reports = await Awt245Reports(library, Consumer);

				await That(libraryResult.Diagnostics).IsEmpty()
					.Because("the wrapper exposes the private type as the nullable registered type");
				await That(result.Diagnostics).IsEmpty();
				await That(reports).HasCount(1)
					.Because("the container sees the possible null through the wrapper");
			}

			private static string HandleLibrary(string registration, string member) => $$"""
				#nullable enable
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				internal interface IKettle { }
				internal sealed class Kettle : IKettle { }
				public interface IBrewer { }
				internal sealed class Brewer : IBrewer { public Brewer(IKettle kettle) { } }

				[Module]
				{{registration}}
				[Singleton<Brewer, IBrewer>]
				public static partial class KettleModule
				{
				    {{member}}
				}
				""";

			private const string HandleConsumer = """
				#nullable enable
				using Awaiten;

				namespace MyCode;

				[Container]
				[Import(typeof(Lib.KettleModule))]
				public static partial class MyContainer { }
				""";

			[Test]
			[Arguments("[Singleton<IKettle>(Factory = nameof(Make))]", "private static IKettle? Make() => new Kettle();", "Factory 'Make'")]
			[Arguments("[Singleton<IKettle>(Factory = nameof(Make))]", "private static Task<IKettle?> Make() => Task.FromResult<IKettle?>(new Kettle());", "Factory 'Make'")]
			[Arguments("[Singleton<IKettle>(Instance = nameof(Shared))]", "private static IKettle? Shared { get; } = new Kettle();", "Instance 'Shared'")]
			[Arguments("[Singleton<IKettle>(Factory = nameof(Make))]", "[return: System.Diagnostics.CodeAnalysis.MaybeNull] private static IKettle Make() => new Kettle();", "Factory 'Make'")]
			[Arguments("[Singleton<IKettle>(Instance = nameof(Shared))]", "[System.Diagnostics.CodeAnalysis.MaybeNull] private static IKettle Shared { get; } = new Kettle();", "Instance 'Shared'")]
			public async Task AMemberOfAnInternalServiceTheModuleBuildsThroughAHandle_IsReportedByTheModulesBuild(string registration, string member, string subject)
			{
				string library = HandleLibrary(registration, member);
				GeneratorResult libraryResult = Generator.Run(library);
				string[] libraryReports = await Awt245Reports(library);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, HandleConsumer);
				string[] reports = await Awt245Reports(library, HandleConsumer);

				await That(libraryResult.Diagnostics).IsEmpty()
					.Because("the module's handle wrapper compiles without nullable warnings and the generator leaves the report to the analyzer");
				await That(libraryReports).HasCount(1).And.Contains($"*warning AWT245*The {subject} may produce null*").AsWildcard()
					.Because("a container in another assembly reaches the member only through the handle, so the module's build reports it");
				await That(libraryReports[0]).StartsWith($"({LineOf(library, registration)},")
					.Because("the warning points at the module's registration");
				await That(result.Diagnostics).IsEmpty();
				await That(reports).IsEmpty()
					.Because("the handle carries a non-nullable value, so the container has nothing to report again");
			}

			[Test]
			public async Task PragmaAroundTheRegistrationOfAMemberTheModuleBuildsThroughAHandle_SuppressesIt()
			{
				string library = HandleLibrary(
					"""
					#pragma warning disable AWT245
					[Singleton<IKettle>(Factory = nameof(Make))]
					#pragma warning restore AWT245
					""",
					"private static IKettle? Make() => new Kettle();");

				string[] reports = await Awt245Reports(library);

				await That(reports).IsEmpty()
					.Because("the module's build reports it through the analyzer too, so #pragma warning disable silences it");
			}

			[Test]
			public async Task AMemberOfAnInternalServiceInTheContainersAssembly_IsReportedOnce()
			{
				string source = HandleLibrary("[Singleton<IKettle>(Factory = nameof(Make))]", "private static IKettle? Make() => new Kettle();")
				                + HandleConsumer.Replace("#nullable enable", "").Replace("using Awaiten;", "").Replace("namespace MyCode;", "");

				GeneratorResult result = Generator.Run(source);
				string[] reports = await Awt245Reports(source);

				await That(result.Diagnostics).IsEmpty();
				await That(reports).HasCount(1).And.Contains("*warning AWT245*").AsWildcard()
					.Because("the module leaves the report to the container of its assembly that imports it, which reports it at the same place");
			}

			[Test]
			public async Task AMemberOfAnInternalServiceWithANestedAnnotation_CompilesWithoutWarnings()
			{
				string source = """
					#nullable enable
					using Awaiten;

					namespace Lib;

					internal interface IRepo<T> { }
					internal sealed class Repo<T> : IRepo<T> { }
					public interface IBrewer { }
					internal sealed class Brewer : IBrewer { public Brewer(IRepo<string> repo) { } }

					[Module]
					[Singleton<IRepo<string>>(Factory = nameof(Make))]
					[Singleton<Brewer, IBrewer>]
					public static partial class RepoModule
					{
					    private static IRepo<string?> Make() => new Repo<string?>();
					}
					""";

				GeneratorResult result = Generator.Run(source);
				string[] reports = await Awt245Reports(source);

				await That(result.Diagnostics).IsEmpty()
					.Because("the handle wrapper forgives the nested annotation the registration cannot spell, like a container does");
				await That(reports).IsEmpty();
			}
		}

		public sealed class ExtendingContainers
		{
			private const string Base = """
				#nullable enable
				using Awaiten;

				namespace Lib;

				public interface IKettle { }
				public sealed class Kettle : IKettle { }
				public sealed class Brewer
				{
				    public Brewer(IKettle kettle) { }
				}

				[Container]
				[Singleton<IKettle>(Factory = nameof(Make))]
				[Singleton<Kettle, IKettle>(Key = "spare", Factory = nameof(MakeSpare))]
				[Transient<Brewer>]
				public static partial class Shop
				{
				    public static IKettle? Make() => new Kettle();
				    public static Kettle? MakeSpare() => new Kettle();
				}
				""";

			private const string Extending = """
				[Container(Extends = typeof(Shop))]
				public static partial class TestShop { }
				""";

			[Test]
			public async Task AnExtendingContainerInTheSameAssembly_CompilesWithoutWarnings()
			{
				GeneratorResult result = Generator.Run(Base + Extending);
				string[] reports = await Awt245Reports(Base + Extending);

				await That(result.Diagnostics).IsEmpty()
					.Because("the extending container compiles its inherited calls of the nullable factories without warnings");
				await That(reports).HasCount(2)
					.Because("the base reports its two nullable factories, and the extending container does not report them again");
			}

			[Test]
			public async Task AnExtendingContainerInAnotherAssembly_CompilesWithoutWarnings()
			{
				const string consumer = """
				                        #nullable enable
				                        using Awaiten;
				                        using Lib;

				                        namespace MyCode;

				                        """ + Extending;

				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Base, consumer);
				string[] reports = await Awt245Reports(Base, consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("the extending container calls the nullable factories without nullable warnings");
				await That(reports).IsEmpty()
					.Because("the base reported the nullable factories in its own build");
			}

			[Test]
			public async Task AnExtendingContainerInAnotherAssemblyOverAPrivateMaybeNullFactory_CompilesWithoutWarnings()
			{
				string baseSource = Base
					.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;")
					.Replace("public static IKettle? Make()", "[return: System.Diagnostics.CodeAnalysis.MaybeNull] private static IKettle Make()");
				const string consumer = """
				                        #nullable enable
				                        using Awaiten;
				                        using Lib;

				                        namespace MyCode;

				                        """ + Extending;

				GeneratorResult baseResult = Generator.Run(baseSource);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(baseSource, consumer);

				await That(baseResult.Diagnostics).IsEmpty()
					.Because("the wrapper the base generates over its private factory spells [MaybeNull] as a nullable return");
				await That(result.Diagnostics).IsEmpty()
					.Because("the extending container calls the wrapper and forgives its nullable result");
			}
		}

		[Test]
		[Arguments("IKettle", "new Kettle()")]
		[Arguments("Task<IKettle>", "Task.FromResult<IKettle>(new Kettle())")]
		[Arguments("Grind", "new Grind()")]
		public async Task AFactoryThatCannotReturnNull_DoesNotReportAwt245(string returnType, string body)
		{
			string source = Container(
				"[Singleton<IKettle>(Factory = nameof(Make))]",
				$"private static {returnType} Make() => {body};");

			GeneratorResult result = Generator.Run(source);
			string[] reports = await Awt245Reports(source);

			await That(result.Diagnostics).IsEmpty();
			await That(reports).IsEmpty();
		}

		[Test]
		public async Task AFactoryInAnObliviousContext_DoesNotReportAwt245()
		{
			string source = Container(
				"[Singleton<IKettle>(Factory = nameof(Make))]",
				"""
				#nullable disable
				    private static IKettle Make() => new Kettle();
				#nullable restore
				""");

			string[] reports = await Awt245Reports(source);

			await That(reports).IsEmpty()
				.Because("an oblivious result says nothing about null");
		}

		private static async Task<string[]> Awt245Reports(string source)
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>(source);
			return diagnostics.Where(diagnostic => diagnostic.Contains("AWT245")).ToArray();
		}

		private static async Task<string[]> Awt245Reports(string referencedSource, string source)
		{
			string[] diagnostics = await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(referencedSource, source);
			return diagnostics.Where(diagnostic => diagnostic.Contains("AWT245")).ToArray();
		}

		private static int LineOf(string source, string fragment)
			=> Array.FindIndex(source.Split('\n'), line => line.Contains(fragment)) + 1;
	}
}
