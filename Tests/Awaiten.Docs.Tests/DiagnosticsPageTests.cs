using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Awaiten.SourceGenerators;
using Microsoft.CodeAnalysis;

namespace Awaiten.Docs.Tests;

public sealed class DiagnosticsPageTests
{
	private static readonly string[] Lines = File.ReadAllLines(GetPagePath());
	private static readonly Regex DiagnosticId = new(@"AWT\d+");
	private static readonly Regex InlineCode = new("`[^`]*`");
	private static readonly Regex SentenceBreak = new(@"(?<!\be\.g|\bi\.e)[.!?] +[A-Z\[]");

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
		await That(heading).IsGreaterThanOrEqualTo(0).Because($"{id} needs a section on the page");
		await That(Lines[heading].Replace("`", "")).IsEqualTo($"### {id}: {title} \\{{#{anchor}\\}}")
			.Because("the IDE shows the descriptor title next to the help link");
		string? admonition = Lines.Skip(heading + 1).FirstOrDefault(line => line.Length > 0);
		await That(admonition).IsNotNull().Because($"the section of {id} starts with its severity admonition");
		await That(severities.Select(severity => $":::{(severity == "Error" ? "danger" : "warning")}[{severity}]"))
			.Contains(admonition!);
		string[] summary = Lines.Skip(Array.IndexOf(Lines, admonition, heading) + 1).TakeWhile(line => line != ":::").ToArray();
		await That(summary).HasCount(1).Because("the admonition summary is one line");
		await That(SentenceBreak.IsMatch(InlineCode.Replace(summary[0], "Code"))).IsFalse()
			.Because("the admonition summarizes the diagnostic in one sentence, and the details follow it");
		string? indexRow = Lines.FirstOrDefault(line => line.StartsWith($"| [{id}](#{anchor}) |"));
		await That(indexRow).IsNotNull().Because($"{id} needs a row in the index");
		string[] row = indexRow!.Replace("`", "").Split('|').Select(cell => cell.Trim()).ToArray();
		await That(row[3]).IsEqualTo(title);
		await That(severities).Contains(severity => row[2].StartsWith(severity))
			.Because("the index row may add when another severity applies, as for AWT118 under loose lifetime safety");
		await That(descriptors).All().Satisfy(descriptor => descriptor.HelpLinkUri == $"https://awaiten.dev/docs/diagnostics#{anchor}")
			.Because("the help link leads to the section of the diagnostic");
	}

	[Test]
	public async Task Page_ShouldOnlyDocumentDiagnosticsWithDescriptor()
	{
		string[] headingIds = Lines.Where(line => line.StartsWith("### AWT")).Select(line => DiagnosticId.Match(line).Value).ToArray();
		string[] indexIds = Lines.Where(line => line.StartsWith("| [AWT")).Select(line => DiagnosticId.Match(line).Value).ToArray();

		await That(headingIds).IsNotEmpty();
		await That(headingIds.Except(Ids())).IsEmpty().Because("every section belongs to a diagnostic");
		await That(indexIds.Except(Ids())).IsEmpty().Because("every index row belongs to a diagnostic");
		await That(headingIds.Distinct()).HasCount(headingIds.Length).Because("each diagnostic has one section");
		await That(indexIds).IsEqualTo(indexIds.OrderBy(id => id, StringComparer.Ordinal)).Because("the index is sorted by id");
	}

	/// <summary>
	///     A not configurable descriptor is documented as an error, whatever its declared default severity, because each
	///     one is reported as an error today: AWT118's strict form is raised to an error per report, AWT255 is declared one.
	/// </summary>
	private static string GetDocumentedSeverity(DiagnosticDescriptor descriptor)
		=> descriptor.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable)
			? "Error"
			: descriptor.DefaultSeverity.ToString();

	private static string GetPagePath([CallerFilePath] string path = "")
		=> Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "Docs", "pages", "09-diagnostics.md"));
}
