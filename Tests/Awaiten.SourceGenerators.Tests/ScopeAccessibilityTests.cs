using System.Linq;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The generated <c>Scope</c> and <c>Root</c> of a public container are public types, so a <c>protected</c> member
///     on them would be part of the consumer's public API (PublicApiAnalyzers report it as RS0016). Nothing outside the
///     container's assembly can derive from the <c>Scope</c>, whose constructors are private protected and private.
/// </summary>
public class ScopeAccessibilityTests
{
	private const string Source = """
	                              #nullable enable
	                              using Awaiten;
	                              using System;
	                              using System.Threading;
	                              using System.Threading.Tasks;

	                              namespace MyCode;

	                              public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }
	                              public interface IStore { }
	                              public sealed class Store : IStore, IInitializable
	                              {
	                                  public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	                              }
	                              public interface IClock { }
	                              public sealed class Ledger { }
	                              public sealed class Register
	                              {
	                                  public Register(Owned<Ledger> owned, Func<Task<Owned<IStore>>> stores, Task<IStore> store, IClock clock) { }

	                                  [Inject(Deferred = true)]
	                                  public Ledger Ledger { get; set; } = null!;
	                              }

	                              [Container]
	                              [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	                              [ImportService<IClock>]
	                              [Scoped<Store, IStore>]
	                              [Transient<Ledger>(OnRelease = nameof(Close))]
	                              [Singleton<Register>]
	                              public static partial class MyContainer
	                              {
	                                  private static void Close(Ledger ledger) { }
	                              }
	                              """;

	[Test]
	[Arguments("Scope")]
	[Arguments("Root")]
	public async Task GeneratedMembers_AreNotVisibleOutsideTheAssembly(string type)
	{
		(Compilation output, _) = Generator.RunGenerator(Source, [], []);
		INamedTypeSymbol scope = output.GetTypeByMetadataName($"MyCode.MyContainer+{type}")!;

		string[] exposed = scope.GetMembers()
			.Where(member => !member.IsImplicitlyDeclared
			                 && member.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal)
			.Select(member => member.Name)
			.ToArray();

		await That(output.GetDiagnostics().Where(diagnostic => diagnostic.Id != "CS8019")).IsEmpty();
		await That(scope.DeclaredAccessibility).IsEqualTo(Accessibility.Public);
		await That(exposed).IsEmpty()
			.Because("only the Root derives from the Scope, so its shared members are private protected and stay out of the public API of a public container");
	}

	[Test]
	public async Task SharedMembers_ArePrivateProtected()
	{
		GeneratorResult result = Generator.Run(Source);

		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"])
			.Contains("private protected readonly Root __root;").And
			.Contains("private protected readonly object __gate = new object();").And
			.Contains("private protected volatile bool __disposed;").And
			.Contains("private protected global::System.Collections.Generic.List<object>? __disposables;").And
			.Contains("private protected int __wiring;").And
			.Contains("private protected global::Awaiten.Owned<T> __Owned<T>(").And
			.Contains("private protected async global::System.Threading.Tasks.Task<global::Awaiten.Owned<T>> __OwnedAsync<T>(").And
			.Contains("private protected object __ResolveExternal(").And
			.Contains("private protected static global::System.Threading.Tasks.Task<TService> __AsTask<TInstance, TService>(");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("private protected global::System.Collections.Generic.List<*>? __releases;").AsWildcard();
	}
}
