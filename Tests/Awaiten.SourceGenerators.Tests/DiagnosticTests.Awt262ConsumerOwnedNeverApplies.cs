namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt262ConsumerOwnedNeverApplies
	{
		private static GeneratorResult Run(string consumer, string registrations)
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;

			                   namespace MyCode;

			                   public sealed class Debouncer : IDisposable { public void Dispose() { } }
			                   {{consumer}}

			                   [Container]
			                   {{registrations}}
			                   [Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer)])]
			                   public static partial class MyContainer;
			                   """);

		[Test]
		public async Task ReportsAServiceTheConsumerDoesNotTakeThroughAFunc()
		{
			GeneratorResult result = Run("public sealed class StorageFactory { public StorageFactory(Lazy<Debouncer> debouncer) { } }", "[Transient<Debouncer>]");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(11,2): warning AWT262: 'MyCode.StorageFactory' lists 'MyCode.Debouncer' in ConsumerOwned, but takes no Func or dispatch dictionary over a transient or parameterized 'MyCode.Debouncer', so the entry has no effect; remove it")
				.Because("only a Func or a dispatch dictionary builds instances the consumer could own");
		}

		[Test]
		public async Task ReportsASharedService()
		{
			GeneratorResult result = Run("public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }", "[Singleton<Debouncer>]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*warning AWT262*").AsWildcard()
				.Because("a Func over a singleton returns the shared instance, which the container keeps owning");
		}

		[Test]
		public async Task DoesNotReportAFuncOverATransient()
		{
			GeneratorResult result = Run("public sealed class StorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }", "[Transient<Debouncer>]");

			await That(result.Diagnostics).IsEmpty()
				.Because("the consumer takes a Func over the transient debouncer");
		}

		[Test]
		public async Task ReportsConflictingConsumerOwnedOnTwoRegistrationsOfOneImplementation()
		{
			GeneratorResult result = Run(
				"public interface IStorageFactory;\npublic sealed class StorageFactory : IStorageFactory { public StorageFactory(Func<Debouncer> debouncers) { } }",
				"[Transient<Debouncer>]\n[Singleton<StorageFactory, IStorageFactory>]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT166*conflicting ConsumerOwned directives*").AsWildcard()
				.Because("coalescing keeps the first registration's directives, so a differing ConsumerOwned would be dropped silently");
		}
	}
}
