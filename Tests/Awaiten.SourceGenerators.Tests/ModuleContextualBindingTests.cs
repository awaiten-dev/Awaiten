using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A container's <c>WhenInjectedInto</c> binding names an implementation that a module of another assembly builds
///     through a generated member: an instance handle, a registration factory or a scan factory. The binding reaches the
///     member's parameters, its deferred setters and its hook wrappers exactly where a container of the module's own
///     assembly applies it to the constructor, the properties and the hooks.
/// </summary>
public class ModuleContextualBindingTests
{
	private static string Library(bool friend = false) => $$"""
		using Awaiten;

		{{(friend ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

		namespace Lib;

		public interface IFlavor { }
		public sealed class HouseFlavor : IFlavor { }

		public interface IMill { }
		internal interface IInternalMill { }
		public sealed class Mill : IMill, IInternalMill
		{
		    public Mill(IFlavor flavor) { }
		    [Inject(Deferred = true)] public IFlavor Late { get; set; } = null!;
		}

		public interface ICup { }
		public sealed class Cup : ICup
		{
		    internal Cup(IFlavor flavor) { }
		    [Inject(Deferred = true)] public IFlavor Late { get; set; } = null!;
		}

		public interface IPlugin { }
		public sealed class Plugin : IPlugin { public Plugin(IFlavor flavor) { } }

		[Module]
		[Singleton<HouseFlavor, IFlavor>]
		[Singleton<Mill, IMill>(OnActivated = nameof(GrindMill))]
		[Singleton<Mill, IInternalMill>]
		[Singleton<Cup, ICup>(OnActivated = nameof(FillCup))]
		[Scan<IPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		public static partial class CoffeeModule
		{
		    internal static void GrindMill(Mill mill, IFlavor flavor) { }
		    internal static void FillCup(Cup cup, IFlavor flavor) { }
		}

		""";

	private const string Bindings = """
		public sealed class GuestFlavor : Lib.IFlavor { }

		[Awaiten.Container]
		[Awaiten.Import(typeof(Lib.CoffeeModule))]
		[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.Mill))]
		[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.Cup))]
		[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.Plugin))]
		public static partial class MyContainer;
		""";

	private const string Consumer = "namespace MyCode;\n\n" + Bindings;

	private const string Guest = "Root.ResolveGuestFlavor(__s.__root)";

	/// <summary>The one generated line that calls a member whose name contains <paramref name="member" />.</summary>
	private static string CallOf(string source, string member)
		=> source.Split('\n').Single(line => line.Contains(member)).TrimEnd();

	[Test]
	public async Task InTheModulesOwnAssembly_AppliesEveryBinding()
	{
		GeneratorResult result = Generator.Run(Library() + Bindings);
		string source = result.Sources["Awaiten.Lib.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty();
		await That(source).Contains($"new global::Lib.Mill({Guest})")
			.And.Contains($".Late = {Guest};")
			.And.Contains($"global::Lib.CoffeeModule.GrindMill(__s._mill, {Guest});")
			.And.Contains($"new global::Lib.Cup({Guest})")
			.And.Contains($"global::Lib.CoffeeModule.FillCup(__s._cup, {Guest});")
			.And.Contains($"new global::Lib.Plugin({Guest})");
	}

	[Test]
	public async Task InAFriendAssembly_AppliesTheBindingToTheScanFactory()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(friend: true), Consumer);
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty()
			.Because("every binding reaches a parameter of its consumer, so none is AWT167");
		await That(source).Contains($"new global::Lib.Mill({Guest})")
			.And.Contains($"new global::Lib.Cup({Guest})")
			.Because("a friend constructs the registered implementations itself");
		await That(CallOf(source, "Internals.Scan_Plugin_")).EndsWith($"({Guest});")
			.Because("a friend calls the module's scan factory, which builds the match the binding names");
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task InAnotherAssembly_AppliesTheBindingToEveryGeneratedMember(bool referenceAssembly)
	{
		GeneratorResult result = referenceAssembly
			? Generator.RunWithGeneratedReferenceAssembly(Library(), Consumer)
			: Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer);
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty()
			.Because("every binding reaches a parameter of its consumer, so none is AWT167");
		await That(CallOf(source, "Internals.HiddenRegistration_Mill_")).EndsWith($"({Guest});")
			.Because("the instance handle's factory takes the constructor parameter of the implementation the binding names");
		await That(CallOf(source, "Internals.Deferred_HiddenRegistration_Mill_")).EndsWith($", {Guest});")
			.Because("the handle's deferred setter takes the property the binding reaches in the module's own assembly");
		await That(CallOf(source, "Internals.HiddenHook_OnActivated_Mill_")).EndsWith($", {Guest});")
			.Because("the handle's hook wrapper takes the hook parameter the binding reaches in the module's own assembly");
		await That(CallOf(source, "Internals.Registration_Cup_")).EndsWith($"({Guest});");
		await That(CallOf(source, "Internals.Deferred_Registration_Cup_")).EndsWith($"(__s._cup, {Guest});")
			.Because("the registration factory's deferred setter takes the property the binding reaches in the module's own assembly");
		await That(CallOf(source, "Internals.Hook_OnActivated_Cup_")).EndsWith($"(__s._cup, {Guest});");
		await That(CallOf(source, "Internals.Scan_Plugin_")).EndsWith($"({Guest});")
			.Because("the binding names the match the scan factory builds, not the scanned service");
	}

	[Test]
	public async Task NamingTheScannedService_IsNoBindingOfTheMatchInAnyAssembly()
	{
		string bindings = Bindings
			.Replace("typeof(Lib.Mill)", "typeof(Lib.IPlugin)")
			.Replace("[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.Cup))]", string.Empty)
			.Replace("[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.Plugin))]", string.Empty);
		const string expected = "warning AWT167: The registration of 'Lib.IFlavor' for 'Lib.IPlugin' is never applied: 'Lib.IPlugin' has no unkeyed direct 'Lib.IFlavor' constructor parameter or [Inject] property to redirect (a [FromKey] dependency, one deferred behind Func/Lazy, or a collection does not qualify)";

		GeneratorResult same = Generator.Run(Library() + bindings);
		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(Library(), "namespace MyCode;\n\n" + bindings);

		await That(same.Diagnostics).HasSingle().Which.EndsWith(expected);
		await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
			.Because("a container of the module's own assembly constructs the match, which only a binding naming it reaches");
	}

	[Test]
	public async Task ADecoratorOfTheModule_IsNoConsumerInAnyAssembly()
	{
		string library = Library()
			.Replace("public interface ICup { }", "public interface ICup { }\npublic sealed class CupLogger : ICup { internal CupLogger(ICup inner, IFlavor flavor) { } }")
			.Replace("[Scan<IPlugin>(", "[Decorate<CupLogger, ICup>]\n[Scan<IPlugin>(");
		string bindings = Bindings.Replace("typeof(Lib.Plugin)", "typeof(Lib.CupLogger)");
		const string expected = "warning AWT167: The registration of 'Lib.IFlavor' for 'Lib.CupLogger' is never applied: 'Lib.CupLogger' has no unkeyed direct 'Lib.IFlavor' constructor parameter or [Inject] property to redirect (a [FromKey] dependency, one deferred behind Func/Lazy, or a collection does not qualify)";

		GeneratorResult same = Generator.Run(library + bindings);
		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "namespace MyCode;\n\n" + bindings);

		await That(same.Diagnostics).HasSingle().Which.EndsWith(expected);
		await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
			.Because("a contextual binding does not reach a decorator's dependencies in the module's own assembly either");
	}
}
