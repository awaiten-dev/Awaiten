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

	/// <summary>
	///     A module whose decorator and composites a container of another assembly cannot construct, so it builds them
	///     through the module's generated factories.
	/// </summary>
	private static string DecoratedLibrary(bool friend = false) => $$"""
		using Awaiten;
		using System.Collections.Generic;

		{{(friend ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

		namespace Lib;

		public interface IFlavor { }
		public sealed class HouseFlavor : IFlavor { }

		public interface ICup { }
		public sealed class Cup : ICup { }
		public sealed class CupLogger : ICup { internal CupLogger(ICup inner, IFlavor flavor) { } }

		public interface IGrinder { }
		public sealed class Grinder : IGrinder { }
		public sealed class GrinderBlend : IGrinder { internal GrinderBlend(IEnumerable<IGrinder> grinders, IFlavor flavor) { } }

		[Module]
		[Singleton<HouseFlavor, IFlavor>]
		[Singleton<Cup, ICup>]
		[Singleton<Cup, ICup>(Key = "to-go")]
		[Decorate<CupLogger, ICup>(Keys = new object[] { null, "to-go" })]
		[Singleton<Grinder, IGrinder>]
		[Singleton<Grinder, IGrinder>(Key = "fine")]
		[Composite<GrinderBlend, IGrinder>]
		[Composite<GrinderBlend, IGrinder>(Key = "fine")]
		public static partial class CoffeeModule;

		""";

	private const string DecoratorBindings = """
		public sealed class GuestFlavor : Lib.IFlavor { }

		[Awaiten.Container]
		[Awaiten.Import(typeof(Lib.CoffeeModule))]
		[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.CupLogger))]
		[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.GrinderBlend))]
		public static partial class MyContainer;
		""";

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ADecoratorOrCompositeOfTheModule_InTheModulesOwnAssemblyOrAFriend_IsAConsumer(bool friend)
	{
		GeneratorResult result = friend
			? Generator.RunWithGeneratedReferencedAssembly(DecoratedLibrary(friend: true), "namespace MyCode;\n\n" + DecoratorBindings)
			: Generator.Run(DecoratedLibrary() + DecoratorBindings);
		string source = result.Sources[friend ? "Awaiten.MyCode.MyContainer.g.cs" : "Awaiten.Lib.MyContainer.g.cs"];
		string[] constructions = source.Split('\n').Where(line => line.Contains("new global::Lib.CupLogger(") || line.Contains("new global::Lib.GrinderBlend(")).ToArray();

		await That(result.Diagnostics).IsEmpty()
			.Because("both bindings reach a dependency of the decorator or composite they name");
		await That(constructions).HasCount(4)
			.Because("the decorator wraps the unkeyed and the keyed cup, and a composite fronts the unkeyed and the keyed grinders");
		await That(constructions).All().Satisfy(line => line.TrimEnd().EndsWith($", {Guest});"))
			.Because("every link and every composite takes the contextual implementation, never for its inner instance or collection");
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ADecoratorOrCompositeOfTheModule_InAnotherAssembly_IsAConsumerThroughTheGeneratedFactory(bool referenceAssembly)
	{
		GeneratorResult result = referenceAssembly
			? Generator.RunWithGeneratedReferenceAssembly(DecoratedLibrary(), "namespace MyCode;\n\n" + DecoratorBindings)
			: Generator.RunWithGeneratedReferencedAssembly(DecoratedLibrary(), "namespace MyCode;\n\n" + DecoratorBindings);
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		string[] calls = source.Split('\n').Where(line => line.Contains("Internals.Decorator_") || line.Contains("Internals.Composite_")).ToArray();

		await That(result.Diagnostics).IsEmpty()
			.Because("both bindings reach a dependency of the decorator or composite they name, as in the module's own assembly");
		await That(calls).HasCount(4)
			.Because("the container builds each link and each composite through the module's generated factory");
		await That(calls).All().Satisfy(line => line.TrimEnd().EndsWith($", {Guest});"))
			.Because("the factory takes the decorator's and the composite's own dependencies besides the inner instance or collection");
	}

	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	public async Task AClosingOfAnOpenDecoratorOfTheModule_IsAConsumerInEveryAssembly(int assembly)
	{
		const string library = """
			using Awaiten;

			namespace Lib;

			public interface IFlavor { }
			public sealed class HouseFlavor : IFlavor { }
			public sealed class Order { }
			public sealed class Refund { }
			public interface IHandler<T> { }
			public sealed class Handler<T> : IHandler<T> { }
			public sealed class Logging<T> : IHandler<T> { internal Logging(IHandler<T> inner, IFlavor flavor) { } }

			[Module]
			[Singleton<HouseFlavor, IFlavor>]
			[Singleton(typeof(Handler<>), typeof(IHandler<>))]
			[Decorate(typeof(Logging<>), typeof(IHandler<>))]
			public static partial class CoffeeModule;

			""";
		const string bindings = """
			public sealed class GuestFlavor : Lib.IFlavor { }
			public sealed class Desk { public Desk(Lib.IHandler<Lib.Order> orders, Lib.IHandler<Lib.Refund> refunds) { } }

			[Awaiten.Container]
			[Awaiten.Import(typeof(Lib.CoffeeModule))]
			[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.Logging<Lib.Order>))]
			[Awaiten.Singleton<Desk>]
			public static partial class MyContainer;
			""";
		GeneratorResult result = assembly switch
		{
			0 => Generator.Run(library + bindings),
			1 => Generator.RunWithGeneratedReferencedAssembly(library, "namespace MyCode;\n\n" + bindings),
			_ => Generator.RunWithGeneratedReferenceAssembly(library, "namespace MyCode;\n\n" + bindings),
		};
		string source = result.Sources[assembly == 0 ? "Awaiten.Lib.MyContainer.g.cs" : "Awaiten.MyCode.MyContainer.g.cs"];
		string[] orders = source.Split('\n').Where(line => line.Contains("Logging") && line.Contains("Lib.Order>(")).Select(line => line.TrimEnd()).ToArray();
		string[] refunds = source.Split('\n').Where(line => line.Contains("Logging") && line.Contains("Lib.Refund>(")).Select(line => line.TrimEnd()).ToArray();

		await That(result.Diagnostics).IsEmpty()
			.Because("the binding names a closing of the decorator, which takes the service besides its inner instance");
		await That(orders).HasSingle().Which.EndsWith($", {Guest});")
			.Because("the closing the binding names takes the contextual implementation");
		await That(refunds).HasSingle().Which.DoesNotContain(Guest)
			.Because("every other closing takes the unconditional one");
	}

	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	public async Task ADecoratorOfAnInternalServiceOfTheModule_IsAConsumerInEveryAssembly(int assembly)
	{
		const string library = """
			using Awaiten;

			namespace Lib;

			public interface IFlavor { }
			public sealed class HouseFlavor : IFlavor { }
			internal interface IInternalCup { }
			public sealed class Cup : IInternalCup { }
			public sealed class CupLogger : IInternalCup { internal CupLogger(IInternalCup inner, IFlavor flavor) { } }
			public sealed class Counter { internal Counter(IInternalCup cup) { } }

			[Module]
			[Singleton<HouseFlavor, IFlavor>]
			[Singleton<Cup, IInternalCup>]
			[Singleton<Counter>]
			[Decorate<CupLogger, IInternalCup>]
			public static partial class CoffeeModule;

			""";
		const string bindings = """
			public sealed class GuestFlavor : Lib.IFlavor { }

			[Awaiten.Container]
			[Awaiten.Import(typeof(Lib.CoffeeModule))]
			[Awaiten.Singleton<GuestFlavor, Lib.IFlavor>(WhenInjectedInto = typeof(Lib.CupLogger))]
			public static partial class MyContainer;
			""";
		GeneratorResult result = assembly switch
		{
			0 => Generator.Run(library + bindings),
			1 => Generator.RunWithGeneratedReferencedAssembly(library, "namespace MyCode;\n\n" + bindings),
			_ => Generator.RunWithGeneratedReferenceAssembly(library, "namespace MyCode;\n\n" + bindings),
		};
		string source = result.Sources[assembly == 0 ? "Awaiten.Lib.MyContainer.g.cs" : "Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty()
			.Because("the decorator takes the service besides its inner instance, also when the module builds it into the service's handle");
		await That(source.Split('\n').Where(line => line.Contains("CupLogger") && line.Contains(Guest))).HasSingle();
	}
}
