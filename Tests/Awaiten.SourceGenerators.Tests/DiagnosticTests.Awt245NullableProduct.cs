namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt245NullableProduct
	{
		private const string Kettle = """
			public interface IKettle { }
			public sealed class Kettle : IKettle { }
			public sealed class Brewer
			{
			    public Brewer(IKettle kettle) { }
			}
			""";

		private static string Container(string registration, string members) => $$"""
			#nullable enable
			using Awaiten;
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
			GeneratorResult result = Generator.Run(Container(
				$"[{lifetime}<{registration}>(Factory = nameof(Make))]",
				$"private static {returnType} Make() => {body};"));

			await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT245*The Factory 'Make' may produce null*'MyCode.IKettle'*").AsWildcard()
				.Because("the declared nullable result is reported once at the registration instead of as nullable warnings in the generated code");
		}

		[Test]
		[Arguments("IKettle", "IKettle?")]
		[Arguments("Kettle, IKettle", "Kettle?")]
		public async Task AnInstanceThatMayBeNull_ReportsAwt245AndCompilesWithoutWarnings(string registration, string memberType)
		{
			GeneratorResult result = Generator.Run(Container(
				$"[Singleton<{registration}>(Instance = nameof(Shared))]",
				$"private static {memberType} Shared {{ get; }} = new Kettle();"));

			await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT245*The Instance 'Shared' may produce null*").AsWildcard()
				.Because("an Instance member of a nullable type is held to the same rule as a factory");
		}

		[Test]
		[Arguments("Singleton", "Kettle, IKettle", "Kettle? Make() => new Kettle()")]
		[Arguments("Scoped", "IKettle", "IKettle? Make() => new Kettle()")]
		[Arguments("Transient", "Kettle, IKettle", "Task<Kettle?> Make() => Task.FromResult<Kettle?>(new Kettle())")]
		public async Task AFactoryOfAnInternalImplementation_ReportsAwt245AndCompilesWithoutWarnings(string lifetime, string registration, string factory)
		{
			GeneratorResult result = Generator.Run(Container($"[{lifetime}<{registration}>(Factory = nameof(Make))]", $"private static {factory};")
				.Replace("public sealed class Kettle", "internal sealed class Kettle"));

			await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT245*'Make'*").AsWildcard()
				.Because("the resolvers of an internal implementation return an accessible type and test the value for disposal at run time, which must not warn either");
		}

		[Test]
		public async Task AFactoryOfAModuleInTheSameAssembly_ReportsAwt245AndCompilesWithoutWarnings()
		{
			GeneratorResult result = Generator.Run($$"""
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
				""");

			await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT245*'Make'*").AsWildcard();
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
			public async Task AFactoryOfAModuleInAnotherAssembly_ReportsAwt245AndCompilesWithoutWarnings(string implementation, string registration, string factory, bool grant)
			{
				string source = Library(implementation, registration, factory, grant);
				GeneratorResult library = Generator.Run(source);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(source, Consumer);

				await That(library.Diagnostics).IsEmpty()
					.Because("the module's own generated code compiles without warnings");
				await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT245*'Make'*").AsWildcard()
					.Because("the container that hands the result to its consumers reports the possible null once");
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
				[{{registration}}]
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
			[Arguments("Singleton<IKettle>(Factory = nameof(Make))", "private static IKettle? Make() => new Kettle();", "Factory 'Make'")]
			[Arguments("Singleton<IKettle>(Factory = nameof(Make))", "private static Task<IKettle?> Make() => Task.FromResult<IKettle?>(new Kettle());", "Factory 'Make'")]
			[Arguments("Singleton<IKettle>(Instance = nameof(Shared))", "private static IKettle? Shared { get; } = new Kettle();", "Instance 'Shared'")]
			public async Task AMemberOfAnInternalServiceTheModuleBuildsThroughAHandle_IsReportedByTheModulesBuild(string registration, string member, string subject)
			{
				string library = HandleLibrary(registration, member);
				GeneratorResult libraryResult = Generator.Run(library);
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, HandleConsumer);

				await That(libraryResult.Diagnostics).HasCount(1).And.Contains($"*warning AWT245*The {subject} may produce null*").AsWildcard()
					.Because("a container in another assembly reaches the member only through the handle, so the module's build reports it, and its handle wrapper compiles without nullable warnings");
				await That(result.Diagnostics).IsEmpty()
					.Because("the handle carries a non-nullable value, so the container has nothing to report again");
			}

			[Test]
			public async Task AMemberOfAnInternalServiceInTheContainersAssembly_IsReportedOnce()
			{
				GeneratorResult result = Generator.Run(HandleLibrary("Singleton<IKettle>(Factory = nameof(Make))", "private static IKettle? Make() => new Kettle();")
				                                       + HandleConsumer.Replace("#nullable enable", "").Replace("using Awaiten;", "").Replace("namespace MyCode;", ""));

				await That(result.Diagnostics).HasCount(1).And.Contains("*warning AWT245*").AsWildcard()
					.Because("the module leaves the report to the container of its assembly that imports it, which reports it at the same place");
			}

			[Test]
			public async Task AMemberOfAnInternalServiceWithANestedAnnotation_CompilesWithoutWarnings()
			{
				GeneratorResult result = Generator.Run("""
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
					""");

				await That(result.Diagnostics).IsEmpty()
					.Because("the handle wrapper forgives the nested annotation the registration cannot spell, like a container does");
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

				await That(result.Diagnostics).HasCount(2).And.All().Satisfy(diagnostic => diagnostic!.Contains("warning AWT245"))
					.Because("the base reports its two nullable factories, and the extending container compiles its inherited calls of them without warnings");
			}

			[Test]
			public async Task AnExtendingContainerInAnotherAssembly_CompilesWithoutWarnings()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Base, """
					#nullable enable
					using Awaiten;
					using Lib;

					namespace MyCode;

					""" + Extending);

				await That(result.Diagnostics).IsEmpty()
					.Because("the base reported the nullable factories in its own build, and the extending container calls them without nullable warnings");
			}
		}

		[Test]
		[Arguments("IKettle", "new Kettle()")]
		[Arguments("Task<IKettle>", "Task.FromResult<IKettle>(new Kettle())")]
		public async Task AFactoryThatCannotReturnNull_DoesNotReportAwt245(string returnType, string body)
		{
			GeneratorResult result = Generator.Run(Container(
				"[Singleton<IKettle>(Factory = nameof(Make))]",
				$"private static {returnType} Make() => {body};"));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AFactoryInAnObliviousContext_DoesNotReportAwt245()
		{
			GeneratorResult result = Generator.Run(Container(
				"[Singleton<IKettle>(Factory = nameof(Make))]",
				"""
				#nullable disable
				    private static IKettle Make() => new Kettle();
				#nullable restore
				"""));

			await That(result.Diagnostics).IsEmpty()
				.Because("an oblivious result says nothing about null");
		}
	}
}
