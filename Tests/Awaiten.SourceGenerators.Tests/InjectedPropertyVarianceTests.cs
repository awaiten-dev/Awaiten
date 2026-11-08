using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     An <c>[Inject]</c> property honors generic variance like a constructor parameter, for a container in the
///     module's assembly, one in another assembly and one in an assembly the module grants <c>InternalsVisibleTo</c>.
/// </summary>
public class InjectedPropertyVarianceTests
{
	private const string Consumer = """
	                                using Awaiten;
	                                using Lib;

	                                namespace App;

	                                [Container]
	                                [Import(typeof(HandlerModule))]
	                                [Transient<AuditHandler, IHandler<DomainEvent>>]
	                                public static partial class AppContainer { }
	                                """;

	private static string Library(string property, string type)
		=> $$"""
		     using System;
		     using System.Collections.Generic;
		     using Awaiten;

		     namespace Lib;

		     public class DomainEvent { }
		     public sealed class OrderPlaced : DomainEvent { }
		     public interface IHandler<in T> { }
		     public sealed class AuditHandler : IHandler<DomainEvent> { }

		     public interface IOrderConsumer { }

		     {{type}} OrderConsumer : IOrderConsumer
		     {
		         {{property}}
		     }

		     [Module]
		     [Transient<OrderConsumer, IOrderConsumer>]
		     public static partial class HandlerModule { }
		     """;

	private static string Granted(string library)
		=> library.Replace("using Awaiten;", "using Awaiten;\n\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]");

	private static string WithLibraryContainer(string library)
		=> library + "\n[Container]\n[Import(typeof(HandlerModule))]\n[Transient<AuditHandler, IHandler<DomainEvent>>]\npublic static partial class LibContainer { }\n";

	[Test]
	[Arguments("[Inject] public IHandler<OrderPlaced> Handler { get; set; } = null!;", "public sealed class")]
	[Arguments("[Inject] public IHandler<OrderPlaced> Handler { get; set; } = null!;", "internal sealed class")]
	[Arguments("[Inject] public Func<IHandler<OrderPlaced>> Handler { get; set; } = null!;", "public sealed class")]
	[Arguments("[Inject] public Func<IHandler<OrderPlaced>> Handler { get; set; } = null!;", "internal sealed class")]
	[Arguments("[Inject(Deferred = true)] public IHandler<OrderPlaced> Handler { get; set; } = null!;", "public sealed class")]
	[Arguments("[Inject(Deferred = true)] public IHandler<OrderPlaced> Handler { get; set; } = null!;", "internal sealed class")]
	public async Task AVariantRegistration_FillsThePropertyEverywhere(string property, string type)
	{
		string library = Library(property, type);

		GeneratorResult same = Generator.Run(WithLibraryContainer(library));
		GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);
		GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Granted(library), Consumer);

		await That(same.Diagnostics).IsEmpty()
			.Because("a registered IHandler<DomainEvent> satisfies IHandler<OrderPlaced> through contravariance, as it does for a constructor parameter");
		await That(other.Diagnostics).IsEmpty();
		await That(friend.Diagnostics).IsEmpty();
	}

	[Test]
	[Arguments("public sealed class", "Handler = ")]
	[Arguments("internal sealed class", "Registration_OrderConsumer_*(*, true)")]
	public async Task AnOptionalProperty_IsFilledByAVariantRegistrationEverywhere(string type, string otherFill)
	{
		string library = Library("[Inject(Optional = true)] public IHandler<OrderPlaced> Handler { get; set; }", type);

		GeneratorResult same = Generator.Run(WithLibraryContainer(library));
		GeneratorResult other = Generator.RunWithGeneratedReferencedAssembly(library, Consumer);

		await That(same.Diagnostics).IsEmpty();
		await That(other.Diagnostics).IsEmpty();
		await That(same.Sources.Single(source => source.Key.Contains("LibContainer")).Value).Contains("Handler = ")
			.Because("a variant registration is not a missing one, so the optional property is filled rather than left at its default");
		await That(other.Sources.Single(source => source.Key.Contains("AppContainer")).Value).Contains(otherFill).AsWildcard()
			.Because("a container in another assembly fills the property, or marks it present for the module's factory, alike");
	}

	[Test]
	public async Task AScanMatch_IsNotSkippedForAPropertyAVariantRegistrationFills()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public class DomainEvent { }
		                                       public sealed class OrderPlaced : DomainEvent { }
		                                       public interface IHandler<in T> { }
		                                       public sealed class AuditHandler : IHandler<DomainEvent> { }
		                                       public interface IPlugin { }

		                                       public sealed class OrderPlugin : IPlugin
		                                       {
		                                           [Inject] public IHandler<OrderPlaced> Handler { get; set; } = null!;
		                                       }

		                                       [Container]
		                                       [Transient<AuditHandler, IHandler<DomainEvent>>]
		                                       [Scan(typeof(IPlugin), SkipUnconstructable = true)]
		                                       public static partial class MyContainer { }
		                                       """);

		await That(result.Diagnostics).IsEmpty()
			.Because("the scan judges an injected property by the variance redirect the container applies to it");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("new global::MyCode.OrderPlugin()");
	}

	[Test]
	public async Task ALazyProperty_StaysUnresolvedLikeALazyParameter()
	{
		GeneratorResult result = Generator.Run("""
		                                       using System;
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public class DomainEvent { }
		                                       public sealed class OrderPlaced : DomainEvent { }
		                                       public interface IHandler<in T> { }
		                                       public sealed class AuditHandler : IHandler<DomainEvent> { }

		                                       public sealed class OrderConsumer
		                                       {
		                                           public OrderConsumer(Lazy<IHandler<OrderPlaced>> handler) { }

		                                           [Inject] public Lazy<IHandler<OrderPlaced>> Handler { get; set; } = null!;
		                                       }

		                                       [Container]
		                                       [Transient<AuditHandler, IHandler<DomainEvent>>]
		                                       [Transient<OrderConsumer>]
		                                       public static partial class MyContainer { }
		                                       """);

		await That(result.Diagnostics.Count(diagnostic => diagnostic.Contains("AWT101"))).IsEqualTo(2)
			.Because("Lazy<T> is invariant, so neither the parameter nor the property converts from a Lazy<IHandler<DomainEvent>>");
	}
}
