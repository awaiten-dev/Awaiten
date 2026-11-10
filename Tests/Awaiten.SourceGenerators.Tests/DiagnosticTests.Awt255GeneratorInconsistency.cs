using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Awaiten.SourceGenerators.Internals;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt255GeneratorInconsistency
	{
		private const string ReportIt = "; this is a bug in Awaiten, please report it at https://github.com/awaiten-dev/Awaiten/issues";

		[Test]
		public async Task DoesNotReportADependencyThatAnErrorAlreadyExplains()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly("""
			                                                             using Awaiten;

			                                                             namespace Production;

			                                                             public interface IClock { }
			                                                             public sealed class Barista { public Barista(IClock clock) { } }

			                                                             [Container]
			                                                             [Singleton<Barista>]
			                                                             public static partial class Shop { }
			                                                             """, """
			                                                                  using Awaiten;

			                                                                  namespace MyCode;

			                                                                  [Container(Extends = typeof(Production.Shop))]
			                                                                  public static partial class TestShop { }
			                                                                  """);

			await That(result.Diagnostics).HasSingle().Which.StartsWith("(5,12): error AWT101: 'Production.Barista' cannot be resolved")
				.Because("the missing dependency of a base in another assembly, whose build did not fail on it, is a mistake of the user, not of the generator");
			await That(result.Sources.Single().Value).Contains("has registration errors");
		}

		[Test]
		public async Task DoesNotReportAContainerWhoseDependenciesResolve()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System;
			                                       using System.Collections.Generic;
			                                       using System.Threading.Tasks;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Clock : IClock { }
			                                       public sealed class UtcClock : IClock { }

			                                       public sealed class Consumer
			                                       {
			                                           public Consumer(IClock clock, Func<IClock> func, Lazy<IClock> lazy, Task<IClock> task, IEnumerable<IClock> all, [FromKey("utc")] IClock utc) { }
			                                       }

			                                       [Container]
			                                       [Singleton<Clock, IClock>]
			                                       [Singleton<UtcClock, IClock>(Key = "utc")]
			                                       [Transient<Consumer>]
			                                       public static partial class MyContainer { }
			                                       """);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources.Single().Value).DoesNotContain("has registration errors");
		}

		/// <summary>
		///     A model without errors whose wiring cannot be emitted, which no valid graph produces, built by hand through
		///     reflection, since the generator's models are internal.
		/// </summary>
		public class HandBuiltModel
		{
			private static readonly Assembly GeneratorAssembly = typeof(AwaitenGenerator).Assembly;

			private static Type Entity(string name) => GeneratorAssembly.GetType("Awaiten.SourceGenerators.Entities." + name, true)!;

			private static object New(string type, params (string Name, object? Value)[] values)
			{
				ConstructorInfo constructor = Entity(type).GetConstructors().Single();
				return constructor.Invoke(constructor.GetParameters()
					.Select(parameter => values.Any(value => value.Name == parameter.Name)
						? values.Single(value => value.Name == parameter.Name).Value
						: parameter.HasDefaultValue ? parameter.DefaultValue : null)
					.ToArray());
			}

			private static object Array(string type, IEnumerable<object> items)
			{
				Type element = Entity(type);
				object[] source = items.ToArray();
				System.Array array = System.Array.CreateInstance(element, source.Length);
				System.Array.Copy(source, array, source.Length);
				return Activator.CreateInstance(typeof(EquatableArray<>).MakeGenericType(element), array)!;
			}

			private static object Kind(string kind) => Enum.Parse(Entity("DependencyKind"), kind);

			private static object Dependency(string service, string kind, string? key = null, params (string KeyType, string Service)[] dispatch)
				=> New("ParameterModel", ("ServiceType", service), ("Kind", Kind(kind)), ("Key", key),
					("DispatchMembers", Array("DispatchMember", dispatch.Select(member => New("DispatchMember", ("KeyType", member.KeyType), ("Service", member.Service))))));

			private static object Instance(string type, object[] dependencies, params string[] services)
				=> New("InstanceModel", ("ImplementationType", type), ("Name", type.Substring(type.LastIndexOf('.') + 1)),
					("Lifetime", Enum.Parse(Entity("Lifetime"), "Transient")),
					("Services", Array("ServiceKey", services.Prepend(type).Select(service => New("ServiceKey", ("Service", service), ("Key", null))))),
					("ConstructorParameters", Array("ParameterModel", dependencies)), ("IsReferenceType", true));

			private static object ConsumerOf(params object[] dependencies)
				=> New("ContainerModel", ("Namespace", "MyCode"), ("TypeName", "MyContainer"), ("HintName", "Awaiten.MyCode.MyContainer.g.cs"),
					("Instances", Array("InstanceModel", [Instance("global::MyCode.Consumer", dependencies), Instance("global::MyCode.Clock", [], "global::MyCode.IClock"),])),
					("Strict", true), ("HasAsyncDisposable", true), ("HasNotNullWhen", true));

			private static object Invoke(Type type, string method, params object?[] arguments)
				=> type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!.Invoke(null, arguments)!;

			private static object Verify(object model) => Invoke(typeof(AwaitenGenerator), "VerifyDependencies", model, null);

			private static string Emit(object model) => (string)Invoke(GeneratorAssembly.GetType("Awaiten.SourceGenerators.Sources", true)!, "Emit", model);

			private static string[] Reported(object model)
				=> ((IEnumerable<object>)model.GetType().GetProperty("Diagnostics")!.GetValue(model)!)
					.Select(diagnostic => diagnostic.GetType().GetMethod("ToDiagnostic", Type.EmptyTypes)!.Invoke(diagnostic, null)!.ToString()!)
					.ToArray();

			[Test]
			[Arguments("Direct")]
			[Arguments("Func")]
			[Arguments("Lazy")]
			[Arguments("Owned")]
			[Arguments("Task")]
			[Arguments("FuncTask")]
			[Arguments("LazyTask")]
			public async Task ReportsADependencyThatResolvesToNoInstance(string kind)
			{
				object verified = Verify(ConsumerOf(Dependency("global::MyCode.IGrinder", kind)));

				await That(Reported(verified)).HasSingle().Which.IsEqualTo(
					"error AWT255: Awaiten cannot generate the container 'MyCode.MyContainer': the dependency 'MyCode.IGrinder' of 'MyCode.Consumer' resolves to no registration" + ReportIt);
				await That(Emit(verified)).Contains("has registration errors")
					.Because("a container the generator cannot wire emits only its shell, like a container with errors");
			}

			[Test]
			public async Task NamesTheKeyOfAKeyedDependency()
			{
				object verified = Verify(ConsumerOf(Dependency("global::MyCode.IClock", "Direct", "str|utc")));

				await That(Reported(verified)).HasSingle().Which.Contains("*the dependency 'MyCode.IClock (key: utc)' of 'MyCode.Consumer'*").AsWildcard()
					.Because("the unkeyed registration of the service does not serve the keyed dependency");
			}

			[Test]
			public async Task ReportsAnEntryOfATypeDispatchThatResolvesToNoInstance()
			{
				object verified = Verify(ConsumerOf(Dependency("global::MyCode.IHandler", "TypeDispatch", null,
					("global::MyCode.Clock", "global::MyCode.IClock"), ("global::MyCode.Order", "global::MyCode.IHandler<global::MyCode.Order>"))));

				await That(Reported(verified)).HasSingle().Which.Contains("*the dependency 'MyCode.IHandler<MyCode.Order>' of 'MyCode.Consumer'*").AsWildcard();
			}

			[Test]
			public async Task DoesNotReportDependenciesThatResolve()
			{
				object model = ConsumerOf(Dependency("global::MyCode.IClock", "Direct"), Dependency("global::MyCode.IClock", "Func"), Dependency("global::MyCode.IClock", "Task"));

				await That(Verify(model)).IsSameAs(model);
			}

			[Test]
			[Arguments("External")]
			[Arguments("Enumerable")]
			[Arguments("AsyncEnumerable")]
			[Arguments("Arg")]
			[Arguments("Absent")]
			public async Task DoesNotReportADependencyTheEmissionSuppliesWithoutAnInstance(string kind)
			{
				object model = ConsumerOf(Dependency("global::MyCode.IGrinder", kind));

				await That(Verify(model)).IsSameAs(model)
					.Because("an import, a collection, a runtime argument or an absent optional property needs no registration");
			}

			[Test]
			public async Task ReportsAnEmissionThatThrows_InsteadOfFailingTheGenerator()
			{
				List<Diagnostic> reported = [];

				string source = (string)Invoke(typeof(AwaitenGenerator), "EmitContainer", ConsumerOf(Dependency("global::MyCode.IGrinder", "Direct")), new Action<Diagnostic>(reported.Add));

				await That(reported.Select(diagnostic => diagnostic.ToString())).HasSingle().Which.IsEqualTo(
						"error AWT255: Awaiten cannot generate the container 'MyCode.MyContainer': emitting it threw System.Collections.Generic.KeyNotFoundException: *" + ReportIt).AsWildcard()
					.Because("the compiler would turn the exception into warning CS8785 and leave the container out");
				await That(source).Contains("has registration errors");
			}
		}
	}
}
