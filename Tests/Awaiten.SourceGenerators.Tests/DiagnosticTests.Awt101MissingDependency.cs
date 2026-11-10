using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt101MissingDependency
	{
		[Test]
		public async Task ReportsTheFullTypeForANestedRelationship()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public sealed class Leaf { }
			                                       public sealed class Service { public Service(Func<Func<Leaf>> nested) { } }

			                                       [Container]
			                                       [Transient<Leaf>]
			                                       [Transient<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.And.Contains("*System.Func<System.Func<MyCode.Leaf>>*").AsWildcard()
				.Because("a relationship over another relationship is reported as the unregistered service type it is");
			await That(result.Diagnostics).DoesNotContain("*global::*").AsWildcard()
				.Because("diagnostics strip the global:: alias, including nested generic arguments");
		}

		[Test]
		public async Task ReportsWhenAConstructorParameterIsNotRegistered()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public sealed class Service { public Service(IMissing missing) { } }

			                                       [Container]
			                                       [Transient<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard();
		}

		[Test]
		public async Task ReportsWhenAFromKeyParameterHasNoMatchingKeyedRegistration()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class FastClock : IClock { }
			                                       public sealed class Consumer { public Consumer([FromKey("missing")] IClock clock) { } }

			                                       [Container]
			                                       [Singleton<FastClock, IClock>(Key = "fast")]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.Because("a [FromKey] with no registration under that key is a missing dependency");
			await That(result.Diagnostics).Contains("*AWT101*key: missing*").AsWildcard()
				.Because("the missing dependency names the requested key");
		}

		[Test]
		public async Task ReportsWhenAFuncTargetIsNotRegistered()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public sealed class Service { public Service(Func<IMissing> missing) { } }

			                                       [Container]
			                                       [Transient<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.Because("a Func<T> still requires its target T to be registered");
		}

		[Test]
		public async Task ReportsWhenALazyTargetIsNotRegistered()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public sealed class Service { public Service(Lazy<IMissing> missing) { } }

			                                       [Container]
			                                       [Transient<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*").AsWildcard()
				.Because("a Lazy<T> does not make its target optional");
		}

		[Test]
		public async Task ReportsWhenAConstructorParameterWithADefaultValueIsNotRegistered()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public sealed class Service { public Service(IMissing missing = null) { } }

			                                       [Container]
			                                       [Transient<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("error AWT101") && diagnostic.Contains("'MyCode.IMissing'"))
				.Because("a C# default value does not make a constructor parameter optional");
		}

		[Test]
		public async Task ReportsWhenAHookParameterWithADefaultValueIsNotRegistered()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public sealed class Service { }

			                                       [Container]
			                                       [Singleton<Service>(OnActivated = nameof(Activate))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Activate(Service service, IMissing missing = null) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("error AWT101") && diagnostic.Contains("'MyCode.IMissing'"))
				.Because("a C# default value does not make a hook parameter optional");
		}

		[Test]
		public async Task ReportsWhenAConventionParameterWithADefaultValueIsNotRegistered()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public interface IWarmUp { Task WarmUpAsync(IMissing missing = null); }
			                                       public sealed class Service : IWarmUp { public Task WarmUpAsync(IMissing missing = null) => Task.CompletedTask; }

			                                       [Container]
			                                       [AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
			                                       [Singleton<Service>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("error AWT101") && diagnostic.Contains("'MyCode.IMissing'"))
				.Because("a C# default value does not make an async initializer convention parameter optional");
		}

		[Test]
		public async Task DoesNotReportAHookOrConventionCollectionOfAnUnregisteredService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System.Collections.Generic;
			                                       using System.Threading.Tasks;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public interface IWarmUp { Task WarmUpAsync(IEnumerable<IMissing> missing); }
			                                       public sealed class Service : IWarmUp { public Task WarmUpAsync(IEnumerable<IMissing> missing) => Task.CompletedTask; }

			                                       [Container]
			                                       [AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
			                                       [Singleton<Service>(OnActivated = nameof(Activate), OnRelease = nameof(Release))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static void Activate(Service service, IEnumerable<IMissing> missing) { }
			                                       	private static void Release(Service service, IReadOnlyList<IMissing> missing) { }
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a collection of an unregistered service is empty, so a hook or convention can take a dependency it does without");
		}

		[Test]
		public async Task ReportsWhenADefaultLosingToAnotherDefaultHasAnUnregisteredDependency()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IMissing { }
			                                       public interface IClock { }
			                                       public sealed class ClockA : IClock { }
			                                       public sealed class ClockB(IMissing missing) : IClock { }
			                                       public sealed class Consumer(IClock clock) { }

			                                       [Module]
			                                       [Singleton<ClockA, IClock>(Fallback = Fallback.Silent)]
			                                       public static class ModuleA { }

			                                       [Module]
			                                       [Singleton<ClockB, IClock>(Fallback = Fallback.Silent)]
			                                       public static class ModuleB { }

			                                       [Container]
			                                       [Import(typeof(ModuleA))]
			                                       [Import(typeof(ModuleB))]
			                                       [Singleton<Consumer>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*ClockB*IMissing*").AsWildcard()
				.Because("a default losing single resolution to another default is still a collection member, so it is built and checked like any registration");
		}
	}
}
