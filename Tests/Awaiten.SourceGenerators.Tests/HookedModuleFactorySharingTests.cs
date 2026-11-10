using System.Collections.Generic;
using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A registration with a lifecycle hook of its own, of a public implementation that a module of another assembly builds
///     through its generated factory. It calls that factory like any other registration of the implementation, and its hook
///     binds on the container, module or base that declares it, as in the module's own assembly.
/// </summary>
public class HookedModuleFactorySharingTests
{
	private const string InternalConstructor = "internal Greeter() { }";
	private const string InternalProperty = "[Inject] internal IClock Clock { get; set; } = null!;";

	private static string Library(string members, string moduleRegistration = "[Singleton<Greeter, IGreeter>]")
		=> $$"""
		     using Awaiten;

		     namespace Lib;

		     public interface IGreeter { }
		     public interface IClock { }
		     public sealed class SystemClock : IClock { }
		     public sealed class Greeter : IGreeter { {{members}} }

		     [Module]
		     [Singleton<SystemClock, IClock>]
		     {{moduleRegistration}}
		     public static partial class GreetingModule
		     {
		         internal static void Warm(Greeter greeter) { }
		     }
		     """;

	private static string Consumer(string registration, string hook = "private static void Ready(Greeter greeter) { }")
		=> $$"""
		     using System.Threading.Tasks;
		     using Awaiten;
		     using Lib;

		     namespace App;

		     [Container]
		     [Import(typeof(GreetingModule))]
		     {{registration}}
		     public static partial class AppContainer
		     {
		         {{hook}}
		     }
		     """;

	private static string Granted(string library, string assembly = "TestAssembly")
		=> library.Replace("using Awaiten;", $"using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{assembly}\")]");

	private static string WithoutHeader(string source)
		=> source.Replace("using System.Threading.Tasks;", string.Empty)
			.Replace("using Awaiten;", string.Empty)
			.Replace("using Lib;", string.Empty)
			.Replace("namespace App;", string.Empty);

	private static string Container(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("AppContainer")).Value;

	[Test]
	[Arguments(InternalConstructor, "OnActivated", "private static void Ready(Greeter greeter) { }")]
	[Arguments(InternalConstructor, "OnRelease", "private static void Ready(Greeter greeter) { }")]
	[Arguments(InternalConstructor, "OnActivated", "private static Task Ready(Greeter greeter) => Task.CompletedTask;")]
	[Arguments(InternalConstructor, "OnRelease", "private static ValueTask Ready(Greeter greeter) => default;")]
	[Arguments(InternalProperty, "OnActivated", "private static void Ready(Greeter greeter) { }")]
	[Arguments(InternalProperty, "OnRelease", "private static void Ready(Greeter greeter) { }")]
	[Arguments(InternalProperty, "OnActivated", "private static ValueTask Ready(Greeter greeter) => default;")]
	[Arguments(InternalProperty, "OnRelease", "private static Task Ready(Greeter greeter) => Task.CompletedTask;")]
	public async Task AHookOfTheContainer_BindsOnTheContainerWhileTheRegistrationCallsTheModulesFactory(string members, string slot, string hook)
	{
		string library = Library(members);
		string consumer = Consumer($"[Singleton<Greeter>({slot} = nameof(Ready))]", hook);

		GeneratorResult same = Generator.Run("using System.Threading.Tasks;\n" + library + "\n" + WithoutHeader(consumer));
		GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), consumer);
		GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
		GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

		foreach (GeneratorResult result in new[] { same, friend, other, reference, })
		{
			await That(result.Diagnostics).IsEmpty()
				.Because("both registrations are one instance with the container's hook, as in the module's own assembly");
		}

		foreach (GeneratorResult result in new[] { other, reference, })
		{
			await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
				.And.DoesNotContain("new global::Lib.Greeter(")
				.And.Contains("Ready(")
				.And.DoesNotContain("global::Lib.GreetingModule.Ready")
				.Because("the registration calls the module's factory, but its hook stays the container's");
		}
	}

	[Test]
	[Arguments("[Singleton<Greeter, IGreeter>]", "[Singleton<Greeter>(Key = \"k\", OnActivated = nameof(Ready))]")]
	[Arguments("[Singleton<Greeter, IGreeter>(Key = \"m\")]", "[Singleton<Greeter>(OnActivated = nameof(Ready))]")]
	[Arguments("[Scoped<Greeter, IGreeter>]", "[Scoped<Greeter>(OnActivated = nameof(Ready), OnRelease = nameof(Ready))]")]
	[Arguments("[Transient<Greeter, IGreeter>]", "[Transient<Greeter>(OnActivated = nameof(Ready), OnRelease = nameof(Ready))]")]
	public async Task AHookOfTheContainer_BindsOnTheContainerForKeyedScopedAndTransientRegistrations(string moduleRegistration, string registration)
	{
		string library = Library(InternalConstructor, moduleRegistration);
		string consumer = Consumer(registration);

		GeneratorResult same = Generator.Run("using System.Threading.Tasks;\n" + library + "\n" + WithoutHeader(consumer));
		GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
		GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

		foreach (GeneratorResult result in new[] { same, other, reference, })
		{
			await That(result.Diagnostics).IsEmpty()
				.Because("both registrations are one implementation with the container's hook, as in the module's own assembly");
		}

		foreach (GeneratorResult result in new[] { other, reference, })
		{
			await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
				.And.DoesNotContain("new global::Lib.Greeter(")
				.And.Contains("Ready(created)")
				.Because("the registration calls the module's factory, and the container's hook runs on what it builds");
		}
	}

	[Test]
	[Arguments("[Singleton<Greeter, IGreeter>(OnActivated = nameof(Warm))]", "[Singleton<Greeter>(OnActivated = nameof(Ready))]",
		"OnActivated directives ('*AppContainer.Ready' and 'Lib.GreetingModule.Warm')")]
	[Arguments("[Singleton<Greeter, IGreeter>(OnRelease = nameof(Warm))]", "[Singleton<Greeter>(OnRelease = nameof(Ready))]",
		"OnRelease directives ('*AppContainer.Ready' and 'Lib.GreetingModule.Warm')")]
	[Arguments("[Singleton<Greeter, IGreeter>(OnActivated = nameof(Warm))]", "[Singleton<Greeter>(OnRelease = nameof(Ready))]",
		"OnActivated directives (unset and 'Lib.GreetingModule.Warm')")]
	public async Task AHookOfTheModule_ConflictsWithTheContainersRegistrationAsInTheModulesAssembly(string moduleRegistration, string registration, string expected)
	{
		string library = Library(InternalConstructor, moduleRegistration);
		string consumer = Consumer(registration);

		GeneratorResult same = Generator.Run(library + "\n" + WithoutHeader(consumer));
		GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), consumer);
		GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, consumer);
		GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, consumer);

		foreach (GeneratorResult result in new[] { same, friend, other, reference, })
		{
			await That(result.Diagnostics).HasSingle().Which.IsEqualTo($"*error AWT166: 'Lib.Greeter' is registered with conflicting {expected}*").AsWildcard()
				.Because("the container's registration comes first and keeps its own hooks, so the module's hook is dropped, wherever the module lives");
		}
	}

	[Test]
	[Arguments(InternalConstructor)]
	[Arguments(InternalProperty)]
	public async Task AnInheritedHook_BindsOnTheBaseWhileTheRegistrationCallsTheModulesFactory(string members)
	{
		const string baseContainer = """
		                             using Awaiten;
		                             using Lib;

		                             namespace Base;

		                             [Container]
		                             [Import(typeof(GreetingModule))]
		                             [Singleton<Greeter>(OnActivated = nameof(BaseReady))]
		                             public static partial class BaseContainer
		                             {
		                                 public static void BaseReady(Greeter greeter) { }
		                             }
		                             """;
		const string extending = """
		                         using Awaiten;

		                         namespace App;

		                         [Container(Extends = typeof(Base.BaseContainer))]
		                         public static partial class AppContainer;
		                         """;
		string library = Library(members);

		GeneratorResult same = Generator.Run(library + "\n" + baseContainer.Replace("using Awaiten;", string.Empty).Replace("using Lib;", string.Empty).Replace("namespace Base;", string.Empty) + "\n" + WithoutHeader(extending).Replace("Base.", string.Empty));
		GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, baseContainer + "\n" + WithoutHeader(extending));
		GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, baseContainer + "\n" + WithoutHeader(extending));
		List<GeneratorResult> results = [same, other, reference,];
		if (members == InternalProperty)
		{
			results.Add(Generator.RunWithGeneratedReferencedAssemblyChain([library, baseContainer,], extending));
		}

		foreach (GeneratorResult result in results)
		{
			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("BaseContainer.BaseReady(")
				.Because("the inherited hook binds on the base that declares it, wherever the base and the module live");
		}

		foreach (GeneratorResult result in results.Skip(1))
		{
			await That(Container(result)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
				.And.DoesNotContain("new global::Lib.Greeter(")
				.Because("the inherited registration calls the module's factory");
		}
	}

	[Test]
	[Arguments("[Import(typeof(LibB.OtherModule))]\n[Import(typeof(Lib.GreetingModule))]", null)]
	[Arguments("[Import(typeof(Lib.GreetingModule))]\n[Import(typeof(LibB.OtherModule))]", "OtherModule.Ready')")]
	public async Task AHookOfAModuleOfAnotherLibrary_BindsOnThatModuleWhileItsRegistrationCallsTheFirstModulesFactory(string imports, string? conflict)
	{
		const string otherLibrary = """
		                            using Awaiten;

		                            namespace LibB;

		                            [Module]
		                            [Singleton<Lib.Greeter>(OnActivated = nameof(Ready))]
		                            public static partial class OtherModule
		                            {
		                                internal static void Ready(Lib.Greeter greeter) { }
		                            }
		                            """;
		string consumer = $$"""
		                    using Awaiten;

		                    namespace App;

		                    [Container]
		                    {{imports}}
		                    public static partial class AppContainer;
		                    """;
		string library = Library(InternalProperty);

		GeneratorResult same = Generator.Run(
			library + "\n" + otherLibrary.Replace("using Awaiten;", string.Empty).Replace("namespace LibB;", string.Empty).Replace("Lib.", string.Empty)
			+ "\n" + consumer.Replace("using Awaiten;", string.Empty).Replace("namespace App;", string.Empty).Replace("LibB.", string.Empty).Replace("Lib.", string.Empty));
		GeneratorResult chain = Generator.RunWithGeneratedReferencedAssemblyChain([library, otherLibrary,], consumer);

		foreach (GeneratorResult result in new[] { same, chain, })
		{
			if (conflict is null)
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the other module's registration comes first, so its hook runs on the one instance");
				await That(Container(result)).Contains("OtherModule.");
			}
			else
			{
				await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("error AWT166: 'Lib.Greeter' is registered with conflicting OnActivated directives (unset and '") && diagnostic.Contains(conflict!))
					.Because("the first module's registration comes first and sets no hook, wherever the modules live");
			}
		}

		if (conflict is null)
		{
			await That(Container(chain)).Contains("global::Lib.GreetingModule.Internals.Registration_Greeter_")
				.And.Contains("global::LibB.OtherModule.Internals.Hook_OnActivated_Greeter_")
				.And.DoesNotContain("new global::Lib.Greeter(")
				.Because("the other module's registration calls the first module's factory and runs its own hook through its wrapper");
		}
	}
}
