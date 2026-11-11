using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Awaiten.SourceGenerators;
using Microsoft.CodeAnalysis;

namespace Awaiten.Docs.Tests;

public sealed class DiagnosticsPageTests
{
	private static readonly string[] Lines = File.ReadAllLines(GetPagePath());

	/// <summary>
	///     The descriptors of <c>Awaiten.SourceGenerators.Diagnostics</c>, read by reflection because the class is internal.
	/// </summary>
	private static readonly DiagnosticDescriptor[] Descriptors = typeof(AwaitenGenerator).Assembly
		.GetType("Awaiten.SourceGenerators.Diagnostics", throwOnError: true)!
		.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(DiagnosticDescriptor))
		.Select(field => (DiagnosticDescriptor)field.GetValue(null)!)
		.ToArray();

	public static IEnumerable<string> Ids()
		=> Descriptors.Select(descriptor => descriptor.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal);

	[Test]
	public async Task Descriptors_ShouldBeFound()
	{
		await That(Ids().Count()).IsGreaterThan(100)
			.Because("the check must not pass because it no longer finds the descriptors");
	}

	[Test]
	[MethodDataSource(nameof(Ids))]
	public async Task Page_ShouldDocumentEachDiagnostic(string id)
	{
		DiagnosticDescriptor[] descriptors = Descriptors.Where(descriptor => descriptor.Id == id).ToArray();
		string anchor = id.ToLowerInvariant();
		string title = descriptors[0].Title.ToString();
		string[] severities = descriptors.Select(GetDocumentedSeverity).Distinct().ToArray();

		int heading = Array.FindIndex(Lines, line => line.StartsWith($"### {id}: "));
		await That(heading).IsGreaterThanOrEqualTo(0);
		await That(Lines[heading].Replace("`", "")).IsEqualTo($"### {id}: {title} \\{{#{anchor}\\}}")
			.Because("the IDE shows the descriptor title next to the help link");
		string admonition = Lines.Skip(heading + 1).First(line => line.Length > 0);
		await That(severities.Select(severity => $":::{(severity == "Error" ? "danger" : "warning")}[{severity}]"))
			.Contains(admonition);
		string[] row = (Lines.FirstOrDefault(line => line.StartsWith($"| [{id}](#{anchor}) |")) ?? "| | | |")
			.Replace("`", "").Split('|').Select(cell => cell.Trim()).ToArray();
		await That(row[3]).IsEqualTo(title);
		await That(severities).Contains(severity => row[2].StartsWith(severity))
			.Because("the index row may add when another severity applies, as for AWT118 under loose lifetime safety");
		await That(descriptors).All().Satisfy(descriptor => descriptor.HelpLinkUri.EndsWith($"#{anchor}"));
	}

	/// <summary>
	///     A not configurable descriptor is reported as an error, whatever its declared default severity.
	/// </summary>
	private static string GetDocumentedSeverity(DiagnosticDescriptor descriptor)
		=> descriptor.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable)
			? "Error"
			: descriptor.DefaultSeverity.ToString();

	private static string GetPagePath([CallerFilePath] string path = "")
		=> Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "Docs", "pages", "09-diagnostics.md"));
}
