using System.Linq;
using System.Text.RegularExpressions;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A container in another assembly serves every registration that shares the instance of a module's generated handle
///     through an alias, so a collection, a keyed dictionary and the checks between registrations of a service treat two
///     aliases of one handle as one implementation, as a container in the module's own assembly treats one implementation.
/// </summary>
public class SharedHandleCollectionTests
{
	private const string ScanLibrary = """
		using Awaiten;

		namespace Lib;

		public interface IPlugin { }
		public interface IRoaster { }
		public sealed class Roaster : IPlugin, IRoaster { }

		[Module]
		[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
		public static partial class PluginModule;

		""";

	private const string ConstructorLibrary = """
		using Awaiten;

		namespace Lib;

		public interface IPlugin { }
		public interface IRoaster { }
		internal interface ISlot { }
		public sealed class Roaster : IPlugin, IRoaster, ISlot { }
		public sealed class User { public User(IPlugin plugin) { } }

		[Module]
		[Singleton<Roaster, IPlugin>]
		[Singleton<Roaster, IRoaster>]
		[Singleton<Roaster, ISlot>]
		public static partial class PluginModule;

		""";

	private const string ProducerLibrary = """
		using Awaiten;

		namespace Lib;

		public interface IPlugin { }
		internal sealed class TimeSource { }
		public sealed class Roaster : IPlugin { internal Roaster(TimeSource source) { } }

		[Module]
		[Singleton<TimeSource>]
		[Singleton<Roaster>(Factory = nameof(Create))]
		public static partial class ProducerModule
		{
		    internal static Roaster Create(TimeSource source) => new(source);
		}

		[Module]
		[Singleton<TimeSource>]
		[Singleton<IPlugin>(Factory = nameof(CreateFirst))]
		[Singleton<IPlugin>(Factory = nameof(CreateSecond))]
		public static partial class TwoProducersModule
		{
		    internal static Roaster CreateFirst(TimeSource source) => new(source);

		    internal static Roaster CreateSecond(TimeSource source) => new(source);
		}

		[Module]
		[Scan<IPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
		public static partial class ScanModule;

		""";

	private const string ContainerScan = "[Scan<IPlugin>(InAssembliesOf = new[] { typeof(IPlugin) }, As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]";

	private static string Container(string imports, string registrations) => $$"""
		[Container]
		{{imports}}
		{{registrations}}
		public static partial class MyContainer;
		""";

	private static (string Consumer, GeneratorResult Result)[] RunEverywhere(string library, string container)
	{
		string external = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + container;
		string friend = library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\n\nnamespace Lib;");
		return
		[
			("same", Generator.Run(library + container)),
			("friend", Generator.RunWithGeneratedReferencedAssembly(friend, external)),
			("full", Generator.RunWithGeneratedReferencedAssembly(library, external)),
			("reference", Generator.RunWithGeneratedReferenceAssembly(library, external)),
		];
	}

	private static string[] Members(GeneratorResult result, string collection)
	{
		string source = result.Sources.Single(source => source.Key.EndsWith(".MyContainer.g.cs", StringComparison.Ordinal)).Value;
		Match match = Regex.Match(source, $@"=> new {Regex.Escape(collection)} \{{ (?<members>[^}}]*) \}};");
		return match.Success ? match.Groups["members"].Value.Split([", ",], StringSplitOptions.None) : [];
	}

	public class OfAScanInstanceHandle
	{
		[Test]
		[Arguments(ContainerScan)]
		[Arguments("[Singleton<Roaster, IPlugin>]")]
		[Arguments("[Singleton<Roaster, IPlugin>(Fallback = Fallback.Silent)]")]
		public async Task AndARegistrationOfTheContainer_IsOneCollectionMember(string registration)
		{
			foreach ((string consumer, GeneratorResult result) in RunEverywhere(ScanLibrary, Container("[Import(typeof(PluginModule))]", registration)))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IPlugin[]")).HasSingle()
					.Because($"the container's registration and the module's scan share one instance in the {consumer} assembly, which the collection holds once, as in the module's own assembly");
				await That(Members(result, "global::Lib.IRoaster[]")).HasSingle().Because(consumer);
			}
		}

		[Test]
		public async Task AndARegistrationOfAnExtendingContainer_IsOneCollectionMember()
		{
			string container = """
				[Container]
				[Import(typeof(PluginModule))]
				public static partial class BaseContainer;

				[Container(Extends = typeof(BaseContainer))]
				[Singleton<Roaster, IPlugin>]
				public static partial class MyContainer;
				""";

			foreach ((string consumer, GeneratorResult result) in RunEverywhere(ScanLibrary, container))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IPlugin[]")).HasSingle().Because(consumer);
			}
		}
	}

	public class OfAConstructorInstanceHandle
	{
		[Test]
		[Arguments(ContainerScan)]
		[Arguments("[Singleton<Roaster, IPlugin>]")]
		[Arguments("[Singleton<Roaster, IPlugin>(Fallback = Fallback.Silent)]")]
		public async Task AndARegistrationOfTheContainer_IsOneCollectionMember(string registration)
		{
			foreach ((string consumer, GeneratorResult result) in RunEverywhere(ConstructorLibrary, Container("[Import(typeof(PluginModule))]", registration)))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IPlugin[]")).HasSingle()
					.Because($"the container's registration and the module's registration share one instance in the {consumer} assembly, which the collection holds once, as in the module's own assembly");
			}
		}

		[Test]
		public async Task UnderTheSameKey_IsOneEntryWithoutAwt117()
		{
			string library = ConstructorLibrary.Replace("[Singleton<Roaster, IPlugin>]", "[Singleton<Roaster, IPlugin>(Key = \"k\")]");

			foreach ((string consumer, GeneratorResult result) in RunEverywhere(library, Container("[Import(typeof(PluginModule))]", "[Singleton<Roaster, IPlugin>(Key = \"k\")]")))
			{
				await That(result.Diagnostics).IsEmpty()
					.Because($"both keyed registrations share one instance in the {consumer} assembly, so a keyed resolution is not ambiguous");
				await That(Members(result, "global::System.Collections.Generic.Dictionary<string, global::Lib.IPlugin>")).HasSingle().Because(consumer);
			}
		}

		[Test]
		public async Task AsTwoOverridableDefaults_IsOneCollectionMemberWithoutAwt148()
		{
			string library = ConstructorLibrary.Replace("[Singleton<Roaster, IPlugin>]", "[Singleton<Roaster, IPlugin>(Fallback = Fallback.Warn)]");

			foreach ((string consumer, GeneratorResult result) in RunEverywhere(library, Container("[Import(typeof(PluginModule))]", "[Singleton<Roaster, IPlugin>(Fallback = Fallback.Warn)]")))
			{
				await That(result.Diagnostics).IsEmpty()
					.Because($"both defaults share one instance in the {consumer} assembly, so neither is ambiguous");
				await That(Members(result, "global::Lib.IPlugin[]")).HasSingle().Because(consumer);
			}
		}

		[Test]
		public async Task UnderTwoVariantClosings_IsOneMemberOfTheirCollection()
		{
			const string library = """
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IHandler<in T> { }
				public class Order { }
				public sealed class Refund : Order { }
				internal interface ISlot { }
				public sealed class Ledger : IHandler<Order>, IHandler<Refund>, ISlot { }
				public sealed class Desk { public Desk(IEnumerable<IHandler<Refund>> handlers) { } }

				[Module]
				[Singleton<Ledger, IHandler<Order>>]
				[Singleton<Ledger, IHandler<Refund>>]
				[Singleton<Ledger, ISlot>]
				[Singleton<Desk>]
				public static partial class LedgerModule;

				""";

			foreach ((string consumer, GeneratorResult result) in RunEverywhere(library, Container("[Import(typeof(LedgerModule))]", "")))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IHandler<global::Lib.Refund>[]")).HasSingle()
					.Because($"the exact closing and the variant closing serve one instance in the {consumer} assembly, which the collection holds once, as in the module's own assembly");
			}
		}

		[Test]
		public async Task AsTwoContextualBindings_IsNoAwt169()
		{
			string library = ConstructorLibrary.Replace("[Singleton<Roaster, IPlugin>]", "[Singleton<Roaster, IPlugin>(WhenInjectedInto = typeof(User))]");

			foreach ((string consumer, GeneratorResult result) in RunEverywhere(library, Container("[Import(typeof(PluginModule))]", "[Singleton<Roaster, IPlugin>(WhenInjectedInto = typeof(User))]\n[Singleton<User>]")))
			{
				await That(result.Diagnostics).IsEmpty()
					.Because($"both contextual bindings share one instance in the {consumer} assembly, so the resolution into the consumer is not ambiguous");
			}
		}
	}

	public class OfAProducerHandle
	{
		[Test]
		public async Task AndAScanOfTheContainer_IsOneCollectionMember()
		{
			foreach ((string consumer, GeneratorResult result) in RunEverywhere(ProducerLibrary, Container("[Import(typeof(ProducerModule))]", ContainerScan)))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IPlugin[]")).HasSingle().Because(consumer);
			}
		}

		[Test]
		public async Task AndAScanOfAnotherModule_IsOneCollectionMember()
		{
			foreach ((string consumer, GeneratorResult result) in RunEverywhere(ProducerLibrary, Container("[Import(typeof(ProducerModule))]\n[Import(typeof(ScanModule))]", "")))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IPlugin[]")).HasSingle().Because(consumer);
			}
		}

		[Test]
		public async Task OfTwoProducersAndAScanOfAnotherModule_AreThreeCollectionMembers()
		{
			foreach ((string consumer, GeneratorResult result) in RunEverywhere(ProducerLibrary, Container("[Import(typeof(TwoProducersModule))]\n[Import(typeof(ScanModule))]", "")))
			{
				await That(result.Diagnostics).IsEmpty().Because(consumer);
				await That(Members(result, "global::Lib.IPlugin[]")).HasCount(3)
					.Because($"each producer and the scan build an instance of their own in the {consumer} assembly, as in the module's own assembly");
			}
		}
	}
}
