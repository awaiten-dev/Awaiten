using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt126OpenGenericConstraint
	{
		[Test]
		public async Task ReportsWhenAClosedTypeArgumentViolatesAReferenceTypeConstraint()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IRepository<T> where T : class { }
			                                       // where T : class on the implementation: a value-type argument violates it.
			                                       public sealed class Repository<T> : IRepository<T> where T : class { }
			                                       public sealed class Root { public Root(IRepository<int> numbers) { } }

			                                       [Container]
			                                       [Transient(typeof(Repository<>), typeof(IRepository<>))]
			                                       [Transient<Root>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT126*").AsWildcard()
				.Because("Repository<int> violates the implementation's where T : class constraint");
		}

		[Test]
		public async Task DoesNotReportWhenTheClosedTypeArgumentSatisfiesTheConstraint()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public sealed class Order { }
			                                       public interface IRepository<T> where T : class { }
			                                       public sealed class Repository<T> : IRepository<T> where T : class { }
			                                       public sealed class Root { public Root(IRepository<Order> orders) { } }

			                                       [Container]
			                                       [Transient(typeof(Repository<>), typeof(IRepository<>))]
			                                       [Transient<Root>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT126*").AsWildcard()
				.Because("Order is a reference type, so it satisfies where T : class");
		}

		[Test]
		public async Task DoesNotReportForASelfReferentialConstraintTheArgumentSatisfies()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       // Order satisfies where T : IComparable<T>, but the constraint mentions the type parameter,
			                                       // so it cannot be checked without substitution - it must not be treated as a violation.
			                                       public sealed class Order : IComparable<Order> { public int CompareTo(Order other) => 0; }
			                                       public interface IRepository<T> where T : IComparable<T> { }
			                                       public sealed class Repository<T> : IRepository<T> where T : IComparable<T> { }
			                                       public sealed class Root { public Root(IRepository<Order> orders) { } }

			                                       [Container]
			                                       [Transient(typeof(Repository<>), typeof(IRepository<>))]
			                                       [Transient<Root>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).DoesNotContain("*AWT126*").AsWildcard()
				.Because("a constructed constraint mentioning the type parameter (IComparable<T>) is satisfied after substitution");
		}

		[Test]
		public async Task ReportsWhenAnImplementationOnlySelfReferentialConstraintIsViolated()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       // The service has no constraint, so IRepository<NotComparable> is legal in Root's signature.
			                                       // The implementation constrains T : IComparable<T>, which NotComparable does not satisfy.
			                                       // Without substituting the closed argument this would emit uncompilable code (CS0311); instead
			                                       // it must be reported cleanly as AWT126.
			                                       public sealed class NotComparable { }
			                                       public interface IRepository<T> { }
			                                       public sealed class Repository<T> : IRepository<T> where T : IComparable<T> { }
			                                       public sealed class Root { public Root(IRepository<NotComparable> repo) { } }

			                                       [Container]
			                                       [Transient(typeof(Repository<>), typeof(IRepository<>))]
			                                       [Transient<Root>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT126*").AsWildcard()
				.Because("the implementation's self-referential constraint where T : IComparable<T> is violated by NotComparable after substitution");
			await That(result.Diagnostics).DoesNotContain("*AWT101*").AsWildcard()
				.Because("the constraint violation is the single root cause; the consumer's now-unregistered IRepository<NotComparable> must not also raise AWT101");
		}

		[Test]
		public async Task ReportsEachService_WhenOneImplementationIsRegisteredForSeveral()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IA<T> { }
			                                       public interface IB<T> { }
			                                       public sealed class Both<T> : IA<T>, IB<T> where T : class { }
			                                       public sealed class Root { public Root(IA<int> a, IB<int> b) { } }

			                                       [Container]
			                                       [Transient(typeof(Both<>), typeof(IA<>))]
			                                       [Transient(typeof(Both<>), typeof(IB<>))]
			                                       [Transient<Root>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics).Contains("*AWT126*'MyCode.Both<int>'*no other registration of 'MyCode.IA<int>' accepts them*").AsWildcard()
				.Because("each unserved service is reported, although both closings share the implementation Both<int>");
			await That(result.Diagnostics).Contains("*AWT126*'MyCode.Both<int>'*no other registration of 'MyCode.IB<int>' accepts them*").AsWildcard();
		}

		public class SeveralOpenRegistrations
		{
			private const string Formatters = """
			                                  using System.Collections.Generic;
			                                  using Awaiten;

			                                  namespace MyCode;

			                                  public interface IText { }
			                                  public interface IBinary { }
			                                  public sealed class Receipt : IText { }
			                                  public sealed class Label : IBinary { }
			                                  public sealed class Sticker : IText, IBinary { }
			                                  public sealed class Plain { }

			                                  public interface IFormatter<T> { }
			                                  public sealed class TextFormatter<T> : IFormatter<T> where T : IText { }
			                                  public sealed class BinaryFormatter<T> : IFormatter<T> where T : IBinary { }

			                                  """;

			private static string ContainerSource(GeneratorResult result)
				=> result.Sources.Single(source => source.Key.Contains("Shop", StringComparison.Ordinal)).Value;

			[Test]
			public async Task EachImplementation_CoversTheClosingsItsConstraintsAllow()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Receipt> receipts, IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).IsEmpty()
					.Because("each closing has one registration whose constraints it satisfies, and the other is skipped for it");
				await That(ContainerSource(result)).Contains("new global::MyCode.TextFormatter<global::MyCode.Receipt>()").And
					.Contains("new global::MyCode.BinaryFormatter<global::MyCode.Label>()").And
					.DoesNotContain("TextFormatter<global::MyCode.Label>").And
					.DoesNotContain("BinaryFormatter<global::MyCode.Receipt>");
			}

			[Test]
			public async Task ReportsEveryCandidate_WhenNoneAcceptsTheClosing()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Plain> plain) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(2);
				await That(result.Diagnostics).Contains(
						"*AWT126*'MyCode.TextFormatter<MyCode.Plain>'*'MyCode.TextFormatter<T>'*no other registration of 'MyCode.IFormatter<MyCode.Plain>' accepts them (also rejected: 'MyCode.BinaryFormatter<T>')*")
					.AsWildcard()
					.Because("the message names the required closing and the other candidate that cannot build it either");
				await That(result.Diagnostics).Contains(
						"*AWT126*'MyCode.BinaryFormatter<MyCode.Plain>'*'MyCode.BinaryFormatter<T>'*no other registration of 'MyCode.IFormatter<MyCode.Plain>' accepts them (also rejected: 'MyCode.TextFormatter<T>')*")
					.AsWildcard();
			}

			[Test]
			public async Task ReportsTheSingleCandidate_WithoutAnAlsoRejectedList()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*no other registration of 'MyCode.IFormatter<MyCode.Label>' accepts them").AsWildcard()
					.Because("with one candidate there is nothing else to list");
			}

			[Test]
			public async Task DoesNotReport_WhenAClosedRegistrationServesTheClosing()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class LabelFormatter : IFormatter<Label> { }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Receipt> receipts, IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<LabelFormatter, IFormatter<Label>>]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the closed registration serves IFormatter<Label>, so the open one is skipped for the closing it cannot build");
			}

			[Test]
			public async Task DoesNotReport_WhenAClosedRegistrationServesTheClosingThroughVariance()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public class Base { }
				                                                    public sealed class Derived : Base { }
				                                                    public interface IHandler<in T> { }
				                                                    public sealed class BaseHandler : IHandler<Base> { }
				                                                    public sealed class TextHandler<T> : IHandler<T> where T : IText { }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IHandler<Derived> derived) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient<BaseHandler, IHandler<Base>>]
				                                                    [Transient(typeof(TextHandler<>), typeof(IHandler<>))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).IsEmpty()
					.Because("IHandler<Base> converts to IHandler<Derived> through contravariance, so the closing resolves without the open registration that rejects it");
				await That(ContainerSource(result)).Contains("new global::MyCode.BaseHandler()").And
					.DoesNotContain("TextHandler<global::MyCode.Derived>");
			}

			[Test]
			public async Task AGenericFactoryRejectingTheClosing_IsSkippedWhenAnotherRegistrationAcceptsIt()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Receipt> receipts, IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(IFormatter<>), Factory = nameof(CreateText))]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop
				                                                    {
				                                                        private static IFormatter<T> CreateText<T>() where T : IText => new TextFormatter<T>();
				                                                    }
				                                                    """);

				await That(result.Diagnostics).IsEmpty()
					.Because("a generic factory's constraints select its closings like an implementation's (no AWT126 or AWT209)");
			}

			[Test]
			public async Task SeveralAcceptingRegistrations_FollowTheRulesForSeveralRegistrations()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Sticker> sticker, IEnumerable<IFormatter<Sticker>> all, IFormatter<Receipt> receipt) { }
				                                                    }

				                                                    [Module]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    public static partial class TextModule { }

				                                                    [Module]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
				                                                    public static partial class BinaryModule { }

				                                                    [Container]
				                                                    [Import(typeof(TextModule))]
				                                                    [Import(typeof(BinaryModule))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT155*IFormatter<MyCode.Sticker>*").AsWildcard()
					.Because("only Sticker satisfies both modules' registrations, so only its closing is registered by both");
			}

			[Test]
			public async Task AKeyedRegistration_IsSkippedForAClosingAnotherKeyBuilds()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Receipt> receipts) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>), Key = "binary")]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).IsEmpty()
					.Because("nothing asks for the closing under the key 'binary', so its registration is skipped for Receipt without an error");
			}

			[Test]
			public async Task AClosingOnlyAnotherKeyBuilds_ReportsTheConstraintOfTheUnkeyedRegistration()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>), Key = "binary")]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.TextFormatter<MyCode.Label>'*").AsWildcard()
					.Because("a registration builds IFormatter<Label> only under a key, so the unkeyed request is unserved because the unkeyed registration rejects Label");
			}

			[Test]
			public async Task AnUnkeyedRequest_ReportsTheConstraint_WhenOnlyAKeyedClosedRegistrationBuildsTheClosing()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class LabelFormatter : IFormatter<Label> { }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<LabelFormatter, IFormatter<Label>>(Key = "x")]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.TextFormatter<MyCode.Label>'*").AsWildcard()
					.Because("the unkeyed request is unserved because the unkeyed open registration rejects Label, which is the precise cause instead of AWT101");
			}

			[Test]
			public async Task AnUnconditionalRequest_ReportsTheConstraint_WhenOnlyAContextualClosedRegistrationBuildsTheClosing()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class LabelFormatter : IFormatter<Label> { }

				                                                    public sealed class Scanner
				                                                    {
				                                                        public Scanner(IFormatter<Label> labels) { }
				                                                    }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<LabelFormatter, IFormatter<Label>>(WhenInjectedInto = typeof(Scanner))]
				                                                    [Transient<Scanner>]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.TextFormatter<MyCode.Label>'*").AsWildcard()
					.Because("Printer is served by no registration because the unconditional open registration rejects Label");
			}

			[Test]
			public async Task AKeyedRequest_ReportsTheConstraint_WhenOnlyTheSkippedRegistrationHasItsKey()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Receipt> receipts, [FromKey("binary")] IFormatter<Receipt> binary) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>), Key = "binary")]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.BinaryFormatter<MyCode.Receipt>'*no other registration of 'MyCode.IFormatter<MyCode.Receipt> (key: binary)' accepts them*").AsWildcard()
					.Because("only the registration under the key 'binary' could serve the keyed request, and its constraint rejects Receipt");
			}

			[Test]
			public async Task AKeyedRequest_ReportsTheConstraint_WhenTheImplementationIsAlsoRegisteredWithoutAKey()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class LabelFormatter : IFormatter<Label> { }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer([FromKey("x")] IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>), Key = "x")]
				                                                    [Transient<LabelFormatter, IFormatter<Label>>(Key = "y")]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.TextFormatter<MyCode.Label>'*'MyCode.IFormatter<MyCode.Label> (key: x)'*").AsWildcard()
					.Because("the registration under the key 'x' is reported for the keyed request, although the unkeyed registration of the same implementation rejects Label too");
			}

			[Test]
			public async Task AnInjectedProperty_ReportsTheConstraint_WhenOnlyAKeyedClosedRegistrationBuildsTheClosing()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class LabelFormatter : IFormatter<Label> { }

				                                                    public sealed class Printer
				                                                    {
				                                                        [Inject] public IFormatter<Label> Labels { get; set; }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<LabelFormatter, IFormatter<Label>>(Key = "x")]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.TextFormatter<MyCode.Label>'*").AsWildcard()
					.Because("an injected property reports the precise cause like a constructor parameter");
			}

			[Test]
			public async Task SeveralUnservedRequests_ReportTheConstraintOnce()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public sealed class LabelFormatter : IFormatter<Label> { }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(IFormatter<Label> labels) { }
				                                                    }

				                                                    public sealed class Scanner
				                                                    {
				                                                        public Scanner(IFormatter<Label> labels) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                    [Transient<LabelFormatter, IFormatter<Label>>(Key = "x")]
				                                                    [Transient<Printer>]
				                                                    [Transient<Scanner>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*").AsWildcard()
					.Because("the constraint violation is the one root cause of both unserved requests");
			}

			[Test]
			public async Task AConstraint_EndsARecursionAnotherRegistrationCloses()
			{
				GeneratorResult result = Generator.Run(Formatters + """
				                                                    public interface INode<T> { }
				                                                    public sealed class Node<T> : INode<T> where T : IText { public Node(INode<List<T>> next) { } }
				                                                    public sealed class Leaf<T> : INode<T> { }

				                                                    public sealed class Printer
				                                                    {
				                                                        public Printer(INode<Receipt> receipts) { }
				                                                    }

				                                                    [Container]
				                                                    [Transient(typeof(Node<>), typeof(INode<>))]
				                                                    [Transient(typeof(Leaf<>), typeof(INode<>))]
				                                                    [Transient<Printer>]
				                                                    public static partial class Shop { }
				                                                    """);

				await That(result.Diagnostics).IsEmpty()
					.Because("Node<T> rejects List<Receipt>, so Leaf<T> builds INode<List<Receipt>> and the expansion neither recurses (AWT129) nor fails (AWT126)");
				await That(ContainerSource(result)).Contains("new global::MyCode.Leaf<global::System.Collections.Generic.List<global::MyCode.Receipt>>()").And
					.DoesNotContain("new global::MyCode.Node<global::System.Collections.Generic.List<");
			}

			[Test]
			public async Task AModuleInAnotherAssembly_ClosesOnlyTheFactoriesWhoseConstraintsHold()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				                                                                      using Awaiten;

				                                                                      namespace Lib;

				                                                                      public interface IText { }
				                                                                      public interface IBinary { }
				                                                                      public interface IFormatter<T> { }
				                                                                      internal sealed class TextFormatter<T> : IFormatter<T> where T : IText { }
				                                                                      internal sealed class BinaryFormatter<T> : IFormatter<T> where T : IBinary { }

				                                                                      [Module]
				                                                                      [Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
				                                                                      [Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
				                                                                      public static partial class FormatterModule { }
				                                                                      """, """
				                                                                           using Awaiten;
				                                                                           using Lib;

				                                                                           namespace App;

				                                                                           public sealed class Receipt : IText { }
				                                                                           public sealed class Label : IBinary { }

				                                                                           public sealed class Printer
				                                                                           {
				                                                                               public Printer(IFormatter<Receipt> receipts, IFormatter<Label> labels) { }
				                                                                           }

				                                                                           [Container]
				                                                                           [Import(typeof(FormatterModule))]
				                                                                           [Transient<Printer>]
				                                                                           public static partial class Shop { }
				                                                                           """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the generated generic factories carry the implementations' constraints, which select the closings like in the module's assembly");
			}
		}

		public class ConstraintConversions
		{
			private const string Sequences = """
			                                 using System.Collections.Generic;
			                                 using Awaiten;

			                                 namespace MyCode;

			                                 public sealed class Plain { }
			                                 public interface IFoo<T> { }
			                                 public sealed class SeqFoo<T> : IFoo<T> where T : IEnumerable<object> { }
			                                 public sealed class ArrayFoo<T> : IFoo<T> where T : IEnumerable<int[]> { }
			                                 public sealed class AnyFoo<T> : IFoo<T> { }

			                                 """;

			private static string ContainerSource(GeneratorResult result)
				=> result.Sources.Single(source => source.Key.Contains("Shop", StringComparison.Ordinal)).Value;

			[Test]
			public async Task AVariantConstraint_SelectsTheImplementationCSharpAccepts()
			{
				GeneratorResult result = Generator.Run(Sequences + """
				                                                   public sealed class Root { public Root(IFoo<List<string>> strings) { } }

				                                                   [Container]
				                                                   [Transient(typeof(SeqFoo<>), typeof(IFoo<>))]
				                                                   [Transient(typeof(AnyFoo<>), typeof(IFoo<>))]
				                                                   [Transient<Root>]
				                                                   public static partial class Shop { }
				                                                   """);

				await That(result.Diagnostics).IsEmpty();
				await That(ContainerSource(result)).Contains("new global::MyCode.SeqFoo<global::System.Collections.Generic.List<string>>()")
					.Because("List<string> converts to IEnumerable<object> through covariance, so SeqFoo<List<string>> is legal and wins as the first registration");
			}

			[Test]
			public async Task AVariantConstraint_IsNoViolationForASingleRegistration()
			{
				GeneratorResult result = Generator.Run(Sequences + """
				                                                   public sealed class Root { public Root(IFoo<List<string>> strings) { } }

				                                                   [Container]
				                                                   [Transient(typeof(SeqFoo<>), typeof(IFoo<>))]
				                                                   [Transient<Root>]
				                                                   public static partial class Shop { }
				                                                   """);

				await That(result.Diagnostics).IsEmpty()
					.Because("List<string> satisfies where T : IEnumerable<object> through covariance, so there is no AWT126");
			}

			[Test]
			public async Task AVariantConstraintOfAnOpenDecorator_DecoratesTheClosing()
			{
				GeneratorResult result = Generator.Run(Sequences + """
				                                                   public interface IGen<T> { }
				                                                   public sealed class Gen<T> : IGen<T> { }
				                                                   public sealed class SeqDecorator<T> : IFoo<T> where T : IEnumerable<object> { public SeqDecorator(IFoo<T> inner, IGen<T> gen) { } }
				                                                   public sealed class Root { public Root(IFoo<List<string>> strings) { } }

				                                                   [Container]
				                                                   [Transient(typeof(Gen<>), typeof(IGen<>))]
				                                                   [Transient(typeof(AnyFoo<>), typeof(IFoo<>))]
				                                                   [Decorate(typeof(SeqDecorator<>), typeof(IFoo<>))]
				                                                   [Transient<Root>]
				                                                   public static partial class Shop { }
				                                                   """);

				await That(result.Diagnostics).IsEmpty()
					.Because("the decorator's constraint holds through covariance, so there is no AWT171 and its IGen<List<string>> is closed");
				await That(ContainerSource(result)).Contains("new global::MyCode.SeqDecorator<global::System.Collections.Generic.List<string>>(");
			}

			[Test]
			public async Task AConstraintWithAnArrayTypeArgument_IsChecked()
			{
				GeneratorResult result = Generator.Run(Sequences + """
				                                                   public sealed class Root { public Root(IFoo<Plain> plain) { } }

				                                                   [Container]
				                                                   [Transient(typeof(ArrayFoo<>), typeof(IFoo<>))]
				                                                   [Transient<Root>]
				                                                   public static partial class Shop { }
				                                                   """);

				await That(result.Diagnostics).HasCount(1).And.Contains("*AWT126*'MyCode.ArrayFoo<MyCode.Plain>'*").AsWildcard()
					.Because("Plain does not implement IEnumerable<int[]>, which is reported instead of a compiler error in the generated code");
			}

			[Test]
			public async Task AConstraintWithAnArrayTypeArgument_LeavesTheClosingToAnotherRegistration()
			{
				GeneratorResult result = Generator.Run(Sequences + """
				                                                   public sealed class Root { public Root(IFoo<Plain> plain, IFoo<List<int[]>> arrays) { } }

				                                                   [Container]
				                                                   [Transient(typeof(ArrayFoo<>), typeof(IFoo<>))]
				                                                   [Transient(typeof(AnyFoo<>), typeof(IFoo<>))]
				                                                   [Transient<Root>]
				                                                   public static partial class Shop { }
				                                                   """);

				await That(result.Diagnostics).IsEmpty();
				await That(ContainerSource(result)).Contains("new global::MyCode.AnyFoo<global::MyCode.Plain>()").And
					.Contains("new global::MyCode.ArrayFoo<global::System.Collections.Generic.List<int[]>>()").And
					.DoesNotContain("ArrayFoo<global::MyCode.Plain>")
					.Because("only List<int[]> satisfies where T : IEnumerable<int[]>");
			}
		}
	}
}
