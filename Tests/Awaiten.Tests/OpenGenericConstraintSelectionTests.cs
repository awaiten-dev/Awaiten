using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of several open generic registrations of one service whose implementations constrain their type
///     parameters differently: each closing is built only from the registrations whose constraints it satisfies.
/// </summary>
public sealed partial class OpenGenericConstraintSelectionTests
{
	public interface IText;

	public interface IBinary;

	public sealed class Receipt : IText;

	public sealed class Label : IBinary;

	public sealed class Sticker : IText, IBinary;

	public interface IFormatter<T>;

	public sealed class TextFormatter<T> : IFormatter<T>
		where T : IText;

	public sealed class BinaryFormatter<T> : IFormatter<T>
		where T : IBinary;

	public sealed class Printer(IFormatter<Receipt> receipts, IFormatter<Label> labels)
	{
		public IFormatter<Receipt> Receipts => receipts;

		public IFormatter<Label> Labels => labels;
	}

	public sealed class StickerPrinter(IFormatter<Sticker> sticker, IEnumerable<IFormatter<Sticker>> stickers, IEnumerable<IFormatter<Receipt>> receipts)
	{
		public IFormatter<Sticker> Sticker => sticker;

		public IEnumerable<IFormatter<Sticker>> Stickers => stickers;

		public IEnumerable<IFormatter<Receipt>> Receipts => receipts;
	}

	public sealed class Unconstrained
	{
		[Test]
		public async Task EachClosing_GetsTheImplementationWhoseConstraintsItSatisfies()
		{
			using TransientShop.Root container = new();

			Printer printer = container.Resolve<Printer>();

			await That(printer.Receipts).Is<TextFormatter<Receipt>>()
				.Because("Receipt satisfies only TextFormatter's where T : IText");
			await That(printer.Labels).Is<BinaryFormatter<Label>>()
				.Because("Label satisfies only BinaryFormatter's where T : IBinary");
		}

		[Test]
		public async Task ACollection_HoldsOnlyTheImplementationsWhoseConstraintsHold()
		{
			using TransientShop.Root container = new();

			StickerPrinter printer = container.Resolve<StickerPrinter>();

			await That(printer.Receipts.Select(formatter => formatter.GetType())).IsEqualTo([typeof(TextFormatter<Receipt>),])
				.Because("BinaryFormatter<Receipt> violates its constraint and is no member of the collection");
			await That(printer.Stickers.Select(formatter => formatter.GetType())).IsEqualTo([typeof(TextFormatter<Sticker>), typeof(BinaryFormatter<Sticker>),])
				.Because("Sticker satisfies both, so both are members in declaration order");
			await That(printer.Sticker).Is<TextFormatter<Sticker>>()
				.Because("when several registrations accept a closing, the first one wins single resolution as usual");
		}
	}

	public sealed class Lifetimes
	{
		[Test]
		public async Task ASingletonClosing_IsSharedPerClosing()
		{
			using SingletonShop.Root container = new();

			Printer first = container.Resolve<Printer>();
			Printer second = container.Resolve<Printer>();

			await That(first.Receipts).IsSameAs(second.Receipts);
			await That(first.Labels).IsSameAs(second.Labels);
			await That(first.Labels).Is<BinaryFormatter<Label>>();
		}

		[Test]
		public async Task AScopedClosing_IsSharedWithinAScopeOnly()
		{
			using ScopedShop.Root container = new();
			using IAwaitenScope firstScope = container.CreateScope();
			using IAwaitenScope secondScope = container.CreateScope();

			IFormatter<Label> first = firstScope.Resolve<IFormatter<Label>>();

			await That(firstScope.Resolve<IFormatter<Label>>()).IsSameAs(first);
			await That(secondScope.Resolve<IFormatter<Label>>()).IsNotSameAs(first);
			await That(firstScope.Resolve<IFormatter<Receipt>>()).Is<TextFormatter<Receipt>>();
		}
	}

	public sealed class Options
	{
		[Test]
		public async Task AKey_SelectsAmongTheImplementationsRegisteredUnderIt()
		{
			using KeyedShop.Root container = new();

			KeyedShop.KeyedPrinter printer = container.Resolve<KeyedShop.KeyedPrinter>();

			await That(printer.Receipts).Is<TextFormatter<Receipt>>();
			await That(printer.Labels).Is<BinaryFormatter<Label>>();
		}

		[Test]
		public async Task AFallback_ServesTheClosingsTheStrongRegistrationCannotBuild()
		{
			using FallbackShop.Root container = new();

			StickerPrinter stickers = container.Resolve<StickerPrinter>();
			Printer printer = container.Resolve<Printer>();

			await That(printer.Receipts).Is<TextFormatter<Receipt>>();
			await That(printer.Labels).Is<BinaryFormatter<Label>>()
				.Because("the strong TextFormatter cannot build IFormatter<Label>, so the overridable default serves it");
			await That(stickers.Stickers.Select(formatter => formatter.GetType())).IsEqualTo([typeof(TextFormatter<Sticker>),])
				.Because("the strong registration replaces the default for every closing it builds, collections included");
		}

		[Test]
		public async Task AContextualRegistration_LeavesTheClosingsItCannotBuildToTheOthers()
		{
			using ContextualShop.Root container = new();

			Printer printer = container.Resolve<Printer>();

			await That(printer.Receipts).Is<TextFormatter<Receipt>>()
				.Because("the registration for Printer builds IFormatter<Receipt>, so Printer receives it");
			await That(printer.Labels).Is<BinaryFormatter<Label>>()
				.Because("the registration for Printer cannot build IFormatter<Label>, so the unconditional one serves it");
		}
	}

	public sealed class Conversions
	{
		[Test]
		public async Task AConstraintSatisfiedThroughVariance_SelectsItsImplementation()
		{
			using VariantConstraintShop.Root container = new();

			VariantConstraintShop.Consumer consumer = container.Resolve<VariantConstraintShop.Consumer>();

			await That(consumer.Strings).Is<VariantConstraintShop.SeqFoo<List<string>>>()
				.Because("List<string> converts to IEnumerable<object> through covariance, so the first registration builds the closing");
			await That(consumer.Numbers).Is<VariantConstraintShop.AnyFoo<int>>();
		}
	}

	public sealed class Modules
	{
		[Test]
		public async Task AModuleInTheSameAssembly_ClosesEachImplementationForTheTypesItAllows()
		{
			using ModuleShop.Root container = new();

			Printer printer = container.Resolve<Printer>();

			await That(printer.Receipts).Is<TextFormatter<Receipt>>();
			await That(printer.Labels).Is<BinaryFormatter<Label>>();
		}

		[Test]
		public async Task AModuleInAnotherAssembly_ClosesEachGeneratedFactoryForTheTypesItAllows()
		{
			using CrossAssemblyShop.Root container = new();

			CrossAssemblyShop.DocumentPrinter printer = container.Resolve<CrossAssemblyShop.DocumentPrinter>();

			await That(printer.Invoice.Kind).IsEqualTo("text");
			await That(printer.Barcode.Kind).IsEqualTo("binary");
			await That(printer.Invoices.Select(formatter => formatter.Kind)).IsEqualTo(["text",])
				.Because("the binary formatter's constraint excludes Invoice, so the collection holds the text formatter only");
		}
	}

	[Container]
	[Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
	[Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
	[Transient<Printer>]
	[Transient<StickerPrinter>]
	public static partial class TransientShop;

	[Container]
	[Singleton(typeof(TextFormatter<>), typeof(IFormatter<>))]
	[Singleton(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
	[Transient<Printer>]
	public static partial class SingletonShop;

	[Container]
	[Scoped(typeof(TextFormatter<>), typeof(IFormatter<>))]
	[Scoped(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
	[Scoped<Printer>]
	public static partial class ScopedShop;

	[Container]
	[Transient(typeof(TextFormatter<>), typeof(IFormatter<>), Key = "format")]
	[Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>), Key = "format")]
	[Transient<KeyedPrinter>]
	public static partial class KeyedShop
	{
		public sealed class KeyedPrinter([FromKey("format")] IFormatter<Receipt> receipts, [FromKey("format")] IFormatter<Label> labels)
		{
			public IFormatter<Receipt> Receipts => receipts;

			public IFormatter<Label> Labels => labels;
		}
	}

	[Container]
	[Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
	[Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>), Fallback = Fallback.Silent)]
	[Transient<Printer>]
	[Transient<StickerPrinter>]
	public static partial class FallbackShop;

	[Container]
	[Transient(typeof(TextFormatter<>), typeof(IFormatter<>), WhenInjectedInto = typeof(Printer))]
	[Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
	[Transient<Printer>]
	public static partial class ContextualShop;

	[Container]
	[Transient(typeof(SeqFoo<>), typeof(IFoo<>))]
	[Transient(typeof(AnyFoo<>), typeof(IFoo<>))]
	[Transient<VariantConstraintShop.Consumer>]
	public static partial class VariantConstraintShop
	{
		public interface IFoo<T>;

		public sealed class SeqFoo<T> : IFoo<T>
			where T : IEnumerable<object>;

		public sealed class AnyFoo<T> : IFoo<T>;

		public sealed class Consumer(IFoo<List<string>> strings, IFoo<int> numbers)
		{
			public IFoo<List<string>> Strings => strings;

			public IFoo<int> Numbers => numbers;
		}
	}

	[Module]
	[Transient(typeof(TextFormatter<>), typeof(IFormatter<>))]
	[Transient(typeof(BinaryFormatter<>), typeof(IFormatter<>))]
	public static partial class FormatterModule;

	[Container]
	[Import(typeof(FormatterModule))]
	[Transient<Printer>]
	public static partial class ModuleShop;

	[Container]
	[Import(typeof(ConstrainedFormatterModule))]
	[Transient<DocumentPrinter>]
	public static partial class CrossAssemblyShop
	{
		public sealed class Invoice : ITextDocument;

		public sealed class Barcode : IBinaryDocument;

		public sealed class DocumentPrinter(
			IDocumentFormatter<Invoice> invoice,
			IDocumentFormatter<Barcode> barcode,
			IEnumerable<IDocumentFormatter<Invoice>> invoices)
		{
			public IDocumentFormatter<Invoice> Invoice => invoice;

			public IDocumentFormatter<Barcode> Barcode => barcode;

			public IEnumerable<IDocumentFormatter<Invoice>> Invoices => invoices;
		}
	}
}
