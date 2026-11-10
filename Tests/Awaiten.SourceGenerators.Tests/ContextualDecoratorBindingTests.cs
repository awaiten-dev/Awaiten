namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A <c>WhenInjectedInto</c> binding that names a decorator or a composite reaches the dependencies the decorator or
///     composite takes besides the decorated inner instance or the composed collection, in every chain link it builds.
/// </summary>
public class ContextualDecoratorBindingTests
{
	private const string Types = """
		using Awaiten;
		using System.Collections.Generic;

		namespace MyCode;

		public interface IFlavor { }
		public sealed class HouseFlavor : IFlavor { }
		public sealed class SpecialFlavor : IFlavor { }

		public interface ICup { }
		public sealed class Cup : ICup { }
		public sealed class Mug : ICup { }
		public sealed class SpecialCup : ICup { }
		public sealed class CupLogger : ICup { public CupLogger(ICup inner, IFlavor flavor) { } }
		public sealed class CupBlend : ICup { public CupBlend(IEnumerable<ICup> cups, IFlavor flavor) { } }
		public sealed class Counter { public Counter(ICup cup, IFlavor flavor) { } }

		""";

	private const string Special = "ResolveSpecialFlavor(__s)";

	private const string House = "ResolveHouseFlavor(__s)";

	private static string Source(string registrations) => Types + $$"""
		[Container]
		[Transient<HouseFlavor, IFlavor>]
		{{registrations}}
		public static partial class MyContainer;
		""";

	[Test]
	public async Task NamingADecorator_ReachesItsOwnDependencies()
	{
		GeneratorResult result = Generator.Run(Source("""
			[Transient<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof(CupLogger))]
			[Transient<Cup, ICup>]
			[Transient<Counter>]
			[Decorate<CupLogger, ICup>]
			"""));
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty()
			.Because("the decorator takes the bound service besides its inner instance, so the binding applies");
		await That(source).Contains($"new global::MyCode.CupLogger(ResolveCup(__s), {Special})")
			.And.Contains($"new global::MyCode.Counter(ResolveCupLogger(__s), {House})")
			.Because("only the decorator the binding names gets the contextual implementation");
	}

	[Test]
	public async Task NamingADecorator_ReachesEveryLinkOfEveryChain()
	{
		GeneratorResult result = Generator.Run(Source("""
			[Transient<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof(CupLogger))]
			[Transient<Cup, ICup>]
			[Transient<Mug, ICup>]
			[Transient<SpecialCup, ICup>(Key = "special")]
			[Decorate<CupLogger, ICup>(Keys = new object[] { null, "special" })]
			"""));
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty();
		await That(source).Contains($"new global::MyCode.CupLogger(ResolveCup(__s), {Special})")
			.And.Contains($"new global::MyCode.CupLogger(ResolveMug(__s), {Special})")
			.Because("each collection member is wrapped by a link of the same decorator");
		await That(source).Contains($"new global::MyCode.CupLogger(ResolveSpecialCup(__s), {Special})")
			.Because("a keyed decorator is the same consumer under its key");
		await That(source).DoesNotContain($", {House})")
			.Because("no link takes the unconditional implementation");
	}

	[Test]
	public async Task NamingADecoratorAlsoRegisteredAsItself_ReachesBoth()
	{
		GeneratorResult result = Generator.Run(Source("""
			[Transient<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof(CupLogger))]
			[Transient<Cup, ICup>]
			[Transient<CupLogger>]
			[Decorate<CupLogger, ICup>]
			"""));
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty();
		await That(source).Contains($"new global::MyCode.CupLogger(ResolveCup(__s), {Special})")
			.Because("the decorator link is built with the contextual implementation");
		await That(source).Contains($"new global::MyCode.CupLogger(ResolveCupLogger2(__s), {Special})")
			.Because("the plain registration of the same type is the same consumer, and its inner is the decorated public cup");
	}

	[Test]
	public async Task NamingADecoratorForTheDecoratedService_NeverReplacesItsInner()
	{
		GeneratorResult result = Generator.Run(Source("""
			[Transient<SpecialCup, ICup>(WhenInjectedInto = typeof(CupLogger))]
			[Transient<Cup, ICup>]
			[Decorate<CupLogger, ICup>]
			"""));

		await That(result.Diagnostics).HasSingle().Which
			.EndsWith("warning AWT167: The registration of 'MyCode.ICup' for 'MyCode.CupLogger' is never applied: 'MyCode.CupLogger' has no unkeyed direct 'MyCode.ICup' constructor parameter or [Inject] property to redirect (a [FromKey] dependency, one deferred behind Func/Lazy, or a collection does not qualify)")
			.Because("the inner parameter always receives the instance the decorator wraps");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"new global::MyCode.CupLogger(ResolveCup(__s), {House})");
	}

	private static string OpenGenericSource(string consumer) => $$"""
		using Awaiten;

		namespace MyCode;

		public interface IFlavor { }
		public sealed class HouseFlavor : IFlavor { }
		public sealed class SpecialFlavor : IFlavor { }
		public sealed class Order { }
		public sealed class Refund { }
		public interface IHandler<T> { }
		public sealed class Handler<T> : IHandler<T> { }
		public sealed class Logging<T> : IHandler<T> { public Logging(IHandler<T> inner, IFlavor flavor) { } }
		public sealed class Desk { public Desk(IHandler<Order> orders, IHandler<Refund> refunds) { } }

		[Container]
		[Transient<HouseFlavor, IFlavor>]
		[Transient<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof({{consumer}}))]
		[Transient(typeof(Handler<>), typeof(IHandler<>))]
		[Transient<Desk>]
		[Decorate(typeof(Logging<>), typeof(IHandler<>))]
		public static partial class MyContainer;
		""";

	[Test]
	public async Task NamingAClosingOfAnOpenGenericDecorator_ReachesThatClosing()
	{
		GeneratorResult result = Generator.Run(OpenGenericSource("Logging<Order>"));
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty();
		await That(source).Contains($"new global::MyCode.Logging<global::MyCode.Order>(ResolveHandler(__s), {Special})")
			.And.Contains($"new global::MyCode.Logging<global::MyCode.Refund>(ResolveHandler2(__s), {House})")
			.Because("the binding names one closing of the decorator, as it does for any open generic consumer");
	}

	[Test]
	public async Task NamingAnOpenGenericDecorator_ReachesNoClosing()
	{
		GeneratorResult result = Generator.Run(OpenGenericSource("Logging<>"));

		await That(result.Diagnostics).HasSingle().Which
			.EndsWith("warning AWT167: The registration of 'MyCode.IFlavor' for 'MyCode.Logging<>' is never applied: 'MyCode.Logging<>' has no unkeyed direct 'MyCode.IFlavor' constructor parameter or [Inject] property to redirect (a [FromKey] dependency, one deferred behind Func/Lazy, or a collection does not qualify)")
			.Because("a consumer is a closed type, so the binding names the closing it applies to");
	}

	[Test]
	public async Task NamingAnOpenGenericRegistration_ReachesNoClosingEither()
	{
		GeneratorResult result = Generator.Run(OpenGenericSource("Handler<>").Replace("public sealed class Handler<T> : IHandler<T> { }", "public sealed class Handler<T> : IHandler<T> { public Handler(IFlavor flavor) { } }"));

		await That(result.Diagnostics).HasSingle().Which.Contains("warning AWT167")
			.Because("a decorator follows the rule every open generic consumer follows");
	}

	[Test]
	[Arguments("", "ResolveCup(__s), ResolveMug(__s)")]
	[Arguments("(Key = \"special\")", "ResolveMug(__s)")]
	public async Task NamingAComposite_ReachesItsOwnDependencies(string key, string members)
	{
		GeneratorResult result = Generator.Run(Source($$"""
			[Transient<SpecialFlavor, IFlavor>(WhenInjectedInto = typeof(CupBlend))]
			[Transient<Cup, ICup>]
			[Transient<Mug, ICup>{{key}}]
			[Composite<CupBlend, ICup>{{key}}]
			"""));
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty()
			.Because("the composite takes the bound service besides its collection, so the binding applies");
		await That(source).Contains($"new global::MyCode.CupBlend(new global::MyCode.ICup[] {{ {members} }}, {Special})");
	}

	[Test]
	public async Task NamingACompositeForTheComposedService_NeverReachesItsCollection()
	{
		GeneratorResult result = Generator.Run(Source("""
			[Transient<SpecialCup, ICup>(WhenInjectedInto = typeof(CupBlend))]
			[Transient<Cup, ICup>]
			[Composite<CupBlend, ICup>]
			"""));

		await That(result.Diagnostics).HasSingle().Which
			.EndsWith("warning AWT167: The registration of 'MyCode.ICup' for 'MyCode.CupBlend' is never applied: 'MyCode.CupBlend' has no unkeyed direct 'MyCode.ICup' constructor parameter or [Inject] property to redirect (a [FromKey] dependency, one deferred behind Func/Lazy, or a collection does not qualify)")
			.Because("a collection parameter is no contextual dependency, and a contextual registration is no collection member");
	}
}
