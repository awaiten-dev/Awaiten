# Writing the documentation

The user documentation in `Docs/pages` is published on [awaiten.dev](https://awaiten.dev). It serves two readers: someone who wants to understand quickly what Awaiten is and how to use it, and an experienced user who wants to look one thing up. This guide says where content goes, how a page is built and what to leave out. The commands that build the site and compile the code blocks are in [workflows.md](workflows.md#documentation).

## Structure

The sidebar follows the `NN-` prefixes of the files and folders. Keep the prefixes unique and in order; the prefix never appears in a URL.

| Section | Holds |
|---|---|
| `01-getting-started.md` | Install, the first container, async warm-up, the first diagnostic. |
| `02-complete-example/` | One realistic container that combines the main features (`index.md`), and a walkthrough of the code the generator writes for exactly that container (`01-generated-code.md`). |
| `03-design-principles.md` | The design the mechanics serve: composition root, volatile and stable dependencies, the anti-patterns, keeping Awaiten out of the domain. Feature pages link here from their "When not to reach for this" tip. |
| `04-core-concepts/` | Lifetimes, scopes, async initialization, lifetime safety, disposal. |
| `05-registration/` | How to tell the container what to build. `07-modules/` holds the user page (`index.md`) and the library-author page (`01-library-modules.md`). |
| `06-resolution/` | How a consumer asks for services: the resolve surface, relationships, collections, property injection, runtime arguments. |
| `07-lifetime/` | `Owned<T>` and lifecycle hooks. |
| `08-hosting/` | The MS.DI bridge, imported services, migrating from MS.DI. |
| `09-diagnostics.md` | The catalog of every `AWT` id. |
| `12-reference/` | Lookup pages: attributes, runtime API, glossary, troubleshooting, with an `index.md` that says what each holds. |

A page with subpages is a folder with an `index.md` (the page itself) and a `_category_.json` that sets only the `label`. A folder without an `index.md` gets `"link": {"type": "generated-index", "description": "..."}` in its `_category_.json`. Never set `collapsed`.

When a page moves, update every relative link to it and to the pages it links to, and rename the matching folder under `Tests/Awaiten.Docs.Tests/Scaffold/`. A URL that was published needs a redirect in `Docs/site/docusaurus.config.ts`.

## Where content goes

- **One owner per topic.** Each topic is explained on one page. Other pages say it in a sentence and link there. Do not repeat the same paragraph, tip or example on several pages.
- **User pages stay user-facing.** How a library module bridges an assembly boundary (generated factories, wrappers, handles, `Internals`, `InternalsVisibleTo` rules) belongs on `05-registration/07-modules/01-library-modules.md`. A feature page states the rule for a container in its own project and links to the library page for the rest.
- **Reference pages list, feature pages explain.** A reference entry is a table row or a few sentences with a link to the feature page. Keep the attribute and runtime API reference in step with the public API (`Tests/Awaiten.Api.Tests` snapshots) and with the defaults in `Source/Awaiten`.
- **Describe the current behavior.** No history, no "used to", "no longer", "since version", "v1 limitation" or "predates". A check that depends on how another assembly was built is described as it works now.
- **No internals of the repository.** Do not name test classes, internal types or member names of the generated code as if they were API. The generated-code walkthrough says its names are not a contract.

## Feature pages

A feature page follows this order, leaving out what does not apply:

1. What it is and when you need it, in a short paragraph.
2. A minimal example.
3. The options, as a table when there are several.
4. The rules, as a bulleted list, each with its diagnostic linked (`[AWT123](../09-diagnostics.md#awt123)`).
5. A `:::tip[When not to reach for this]` admonition when the feature can hide a design problem. Use exactly that title, at most once per page, and link to the design principles.
6. `## Where to go next` with two or three links.

Write rules as lists or tables, not as paragraphs. A paragraph longer than about five sentences is a sign that it holds several rules; split it. Fine print that most readers skip goes into a `<details>` block with a `<summary>`, with blank lines around the Markdown inside it.

A form that puts an Awaiten attribute on a domain type (`[FromKey]`, `[Arg]`, `[Inject]` on the consumer) is shown after the container-side form and described once as an accepted trade, without a separate "last resort" tip.

## Examples

- The coffee shop is the running example; reuse the types in `Tests/Awaiten.Docs.Tests/Scaffold/Scaffold.cs` and `Tests/Support/CoffeeShop.Domain/CoffeeShopDomain.cs` before declaring new ones.
- An example must work when copied. Top-level statements come before the type declarations in a block, as C# requires. A complete program gets `title="Program.cs"`; give other blocks a file title where it helps.
- Mark the line that matters with `// highlight-next-line`: the registration a section introduces, or the line that triggers a diagnostic.
- A block that shows a mistake carries `expect=AWTnnn`. A fragment that cannot compile carries `no-compile`; anything else compiles.
- An `Output` block shows only output that the code really prints. Run it before you document it.
- Use `Tabs` (`import Tabs from '@theme/Tabs'`) only for real alternatives, such as before and after a migration or the container-side and consumer-side form. Do not put identical behavior in two tabs.
- Use a Mermaid diagram where a picture shows a structure faster than prose: a chain of decorators, the scopes of a root, the graph of an example.

## Diagnostics

Every id has one section in `09-diagnostics.md`, under the topic it belongs to:

- The heading is `### AWTnnn: Short title \{#awtnnn\}`. The title is the descriptor title from `Diagnostics.cs` in sentence case, with code names in backticks. Escape the braces of the heading id: the site enables `future.v4`, which turns off the unescaped MDX 1 syntax.
- An admonition with the severity (`:::danger[Error]` or `:::warning[Warning]`) and a one-sentence summary.
- An `expect=AWTnnn` example that triggers it, with `// highlight-next-line` on the trigger.
- A `**Fix:**` paragraph, followed by a fixed example where that helps.
- A row in the `## Index` table at the top of the page, sorted by id.

Every descriptor in `Diagnostics.cs` passes `helpLinkUri: HelpLink("AWTnnn")`, so the id in the IDE links to its section. An id that is no longer reported moves to `## Reserved ids` and is never reused.

## Before you push

- Run the slow code-block test of `Tests/Awaiten.Docs.Tests` and build the site (`npm run build` in `Docs/site`); the build fails on a broken link or anchor.
- Read the changed pages on the local site (`npm start`), including the sidebar order and the previous and next links.
