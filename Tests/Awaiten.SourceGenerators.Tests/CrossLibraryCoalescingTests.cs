namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     Two libraries whose modules each register the same public implementation, declared in a third library or in
///     one of the two. A container that imports both modules builds one instance of it, as it does for two modules of
///     one assembly.
/// </summary>
public class CrossLibraryCoalescingTests
{
	private const string Clocks = """
		namespace Clocks;

		public interface IClock { }
		public interface ITimer { }
		public sealed class Clock : IClock, ITimer { }
		""";

	private static string Library(string name, string registration) => $$"""
		using Awaiten;
		using Clocks;

		namespace {{name}};

		[Module]
		[{{registration}}]
		public static partial class {{name}}Module { }
		""";

	private const string Consumer = """
		using Awaiten;
		using Clocks;

		namespace MyCode;

		[Container]
		[Import(typeof(LibA.LibAModule))]
		[Import(typeof(LibB.LibBModule))]
		public static partial class MyContainer { }
		""";

	[Test]
	[Arguments("Singleton<Clock, IClock>", "Singleton<Clock, IClock>")]
	[Arguments("Singleton<Clock, IClock>", "Singleton<Clock, ITimer>")]
	[Arguments("Scoped<Clock, IClock>", "Scoped<Clock, ITimer>")]
	public async Task ModulesOfTwoLibraries_ShareOneInstanceOfAnImplementationOfAThirdLibrary(string first, string second)
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[Clocks, Library("LibA", first), Library("LibB", second),],
			Consumer);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("new global::Clocks.Clock(").Exactly(1)
			.Because("both registrations name the same implementation, so the container coalesces them into one instance");
	}

	[Test]
	public async Task AModuleOfALibraryThatReferencesTheOther_SharesOneInstanceOfItsImplementation()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[Clocks.Replace("namespace Clocks;", "namespace Clocks;\n\n[Awaiten.Module]\n[Awaiten.Singleton<Clock, IClock>]\npublic static partial class LibAModule { }"), Library("LibB", "Singleton<Clock, IClock>"),],
			Consumer.Replace("LibA.LibAModule", "Clocks.LibAModule"));

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("new global::Clocks.Clock(").Exactly(1)
			.Because("the implementation declared beside the first module is the same type the second module registers");
	}

	private static string HiddenClockLibrary(string registrations) => $$"""
		using Awaiten;

		namespace LibA;

		internal interface IHiddenClock { }
		public sealed class Clock : IHiddenClock { }
		public interface IWatch { }
		internal sealed class Watch : IWatch { public Watch(IHiddenClock clock) { } }

		[Module]
		[Singleton<Clock, IHiddenClock>]
		{{registrations}}
		public static partial class LibAModule { }
		""";

	[Test]
	public async Task AModuleOfALibraryThatReferencesALibraryBuildingTheImplementationThroughAHandle_SharesTheHandle()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[HiddenClockLibrary("[Singleton<Watch, IWatch>]"), Library("LibB", "Singleton<LibA.Clock>").Replace("using Clocks;", ""),],
			Consumer.Replace("using Clocks;", ""));

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::LibA.LibAModule.Internals.Alias_Clock_")
			.And.DoesNotContain("new global::LibA.Clock(")
			.Because("the registration of the second library serves the instance the first library's handle holds");
	}

	[Test]
	public async Task AModuleOfALibraryThatReferencesALibraryRegisteringTheImplementationUnderAnInternalServiceOnly_BuildsOneInstance()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[HiddenClockLibrary(""), Library("LibB", "Singleton<LibA.Clock>").Replace("using Clocks;", ""),],
			Consumer.Replace("using Clocks;", ""));

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::LibA.LibAModule.Internals.Alias_Clock_")
			.And.DoesNotContain("new global::LibA.Clock(")
			.Because("the first library builds its public implementation through a handle, so the second library's registration serves that instance");
	}

	[Test]
	public async Task AnImplementationOnlyItsOwnLibraryCanBuild_ReportsAwt111InsteadOfBuildingASecondInstance()
	{
		const string libA = """
			using Awaiten;

			namespace LibA;

			public interface IClock { }
			public interface ITimer { }
			public sealed class Timer : ITimer { }
			public sealed class Clock : IClock
			{
			    [Inject]
			    internal ITimer Timer { get; set; } = null!;
			}

			[Module]
			[Singleton<Timer, ITimer>]
			[Singleton<Clock, IClock>]
			public static partial class LibAModule { }
			""";

		GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
			[libA, Library("LibB", "Singleton<LibA.Clock, LibA.IClock>").Replace("using Clocks;", ""),],
			Consumer.Replace("using Clocks;", ""));

		await That(result.Diagnostics).HasCount(1).And.Contains("*AWT111*'LibA.Clock'*").AsWildcard()
			.Because("only the first library's factory sets the internal property, so the second library's registration cannot share its instance and must not build another");
	}
}
