# browser-blazor

Run **C# and live Blazor projects in the browser with no server** — multi-file projects of Razor
markup and C#, with `@page` routing, compiled in the page and rendered on Blazor's real renderer.

- **`bundle-poc.html`** — step 1: runs C# in the page with the existing
  [`@seth0x41/csharp-wasm`](https://www.npmjs.com/package/@seth0x41/csharp-wasm) bundle. It works,
  but probing it showed that bundle **cannot render Blazor components**
  ([findings](#step-1--findings)).
- **`src/BlazorRunner`** — our own Blazor WebAssembly host. It compiles Razor markup (with the SDK's
  Razor compiler) and C# (with Roslyn) **in the page**, renders components on Blazor's real
  interactive renderer, **and** runs console programs — so one bundle can back both the C# language
  and Blazor.

## Quick start

```powershell
# One-time (re-run after upgrading the .NET SDK)
powershell -ExecutionPolicy Bypass -File scripts\prepare-refs.ps1

# Build the deployable package
powershell -ExecutionPolicy Bypass -File scripts\make-package.ps1 -Version 0.1.0

# Serve it
node serve.js package 8160          # http://localhost:8160/
```

The playground edits a **project**: the file tabs across the top of the editor hold any mix of
`.razor` and `.cs` (`+` / `×` to add and remove), the live result is on the right, and the routes the
project declares appear as chips in its header. Pick **Razor** or **C#**, edit, and press **Render**
(or Ctrl/Cmd + Enter).

For quick iteration, `dotnet publish src\BlazorRunner -c Release -o src\BlazorRunner\dist` then serve
`src\BlazorRunner\dist\wwwroot` — but always publish into a **clean** directory, and be aware that a
stale `obj/` can silently keep an old package version (see [limitations](#size-and-limitations)).

### Step 1 — bundle PoC

```sh
node serve.js                       # http://localhost:8130/bundle-poc.html
```

## Prerequisites

- **.NET SDK 10** with the **`wasm-tools` workload**. On a typical Windows box there are two installs
  and they are not equivalent — the one on `PATH` (`%ProgramFiles%\dotnet`) often lacks the workload,
  while `%USERPROFILE%\.dotnet` has it:

  ```powershell
  & "$env:USERPROFILE\.dotnet\dotnet.exe" workload list   # must list wasm-tools
  ```

  Both scripts default to `%USERPROFILE%\.dotnet` (`-SdkRoot` / `-DotnetRoot` to override).

- Node.js (only for the static file server).

## Using the package from your own page

The package ships `blazor-wasm.js`, a loader that hides the boot sequence (fetch patching,
`Blazor.start`, the app-assembly race) behind a few calls:

```html
<div id="blazor-app"></div>
<script src="https://cdn.jsdelivr.net/npm/@live-codes/blazor-wasm/blazor-wasm.js"></script>
<script>
  const runner = BlazorRunner.create();            // defaults to the script's own folder

  // A project: any mix of .razor and C#, compiled together.
  const result = await runner.renderProject(
    [
      { filename: 'App.razor', content: '<Router AppAssembly="typeof(App).Assembly">…</Router>' },
      { filename: 'Pages/Home.razor', content: '@page "/"\n<h1>Home</h1>\n<img src="logo.svg" />' },
      { filename: 'Pages/Counter.razor', content: '@page "/counter"\n<button @onclick="Go">@count</button>\n@code { int count; void Go() => count++; }' },
      { filename: 'Pages/Counter.razor.css', content: 'button { color: red; }' },
      { filename: 'wwwroot/logo.svg', content: '<svg …/>' },
      { filename: 'Greeting.cs', content: 'public static class Greeting { public static string For(string n) => "Hi " + n; }' },
    ],
    'App',                       // optional: the component to render
    'MyApp',                     // optional: the project's namespace
  );
  // result.routes -> ['/', '/counter']

  await runner.navigateTo('/counter');               // drive @page routing

  const single = await runner.renderRazor('<h1>Hello</h1>');        // one .razor component
  const asCSharp = await runner.render('public class App : ComponentBase { /* ... */ }');
  const console = await runner.run('using System; class P { static void Main() => Console.WriteLine("hi"); }');
</script>
```

`renderProject` / `render` / `renderRazor` resolve to `{ success, type, bytes, routes[], errors[] }`
and `run` to `{ success, output, errors[] }`; every diagnostic is
`{ id, message, severity, line, column }`. Pass `{ baseUrl, onProgress }` to `create` to point at a
different copy or report download progress. The playground uses this same loader.

A project is compiled the way a local one is, so what builds here builds locally: a file's folder
becomes part of its namespace (`Pages/Home.razor` is `UserRazor.Pages.Home`, `Layout/MainLayout.razor`
is `UserRazor.Layout.MainLayout`), `@page` declares routes, `Name.razor.css` scopes `Name.razor`, and
`@using`s come from the project's own `_Imports.razor` — a template-style one is supplied only when
the project has none. `UserRazor` is the project's root namespace unless you pass one.

A project can also be a single file. `renderRazor` (and `renderProject` with one entry) needs no
`_Imports.razor` — the template's equivalent is supplied when the project has none — so the shortest
usable call is `runner.renderRazor('<h1>Hello</h1>')`.

## Step 1 — findings

Measured against `@seth0x41/csharp-wasm@1.0.3` (from `_framework/blazor.boot.json` and the string
table of `MyRunnyApp.*.wasm`):

- **It compiles and runs C# entirely in the browser** — compiled to `DynamicAssembly`, loaded with
  `AssemblyLoadContext`, `Console` I/O captured. Cold run ~2 s, warm ~25 ms.
- **The Roslyn reference set excludes Blazor.** `using Microsoft.AspNetCore.Components;` fails with
  *"The type or namespace name 'AspNetCore' does not exist in the namespace 'Microsoft'"*, so user
  code cannot reference Blazor types and cannot author a component.
- **Blazor's rendering types are trimmed.** `ComponentBase` resolves at runtime, but
  `Microsoft.AspNetCore.Components.Web.HtmlRenderer` and `RenderTreeBuilder` are **not found**.
- **The app has no main root component** (its only selector literal is `head::after`) and the npm
  package ships no `index.html`, so its own UI cannot be booted standalone.

It is a good Roslyn-in-the-browser engine, but not a component host. Hence step 2.

## Step 2 — the host

`src/BlazorRunner` is a `Microsoft.NET.Sdk.BlazorWebAssembly` app published as static files (no
server logic). .NET 10 / ASP.NET Core 10.0.11, **Roslyn 4.14.0**.

### API

Called from the page with `DotNet.invokeMethodAsync('BlazorRunner', …)`:

| Method | Returns |
| ------ | ------- |
| `RenderProject(filesJson, rootType, rootNamespace)` | `{ success, type, bytes, routes[], styles, assets, errors[] }` — compiles a project of `{ filename, content }` files (`.razor`, `.razor.css`, `.cs` and `wwwroot/` assets, in any folders) and renders it. `rootType` optionally names the component to render; `rootNamespace` is the project's namespace (default `UserRazor`). |
| `RenderRazor(source, componentName)` | Same, for a single `.razor` file. `componentName` names the generated class (default `App`). |
| `RenderComponent(source, rootType)` | Same, for a single C# file. |
| `NavigateTo(url)` | Drives the host's `NavigationManager`, for `@page` routing. |
| `RunCode(source, stdin)` | `{ success, output, errors[] }` — compiles and runs a console program, capturing stdout. |
| `ReferenceCount()` | reference assemblies the compiler has loaded, fetching them if it has not yet. |
| `LoadedPayloads()` | which payloads have been fetched — `refs.zip`, plus `razor.zip` once markup has been compiled. |

A component that throws *while rendering* comes back as `success: false` with the exception in
`errors`. Diagnostics from generated Razor code are mapped back to the `.razor` source lines.

### Files

| File | Role |
| ---- | ---- |
| `Program.cs` | Builds the host and mounts the root component at `#blazor-app`. |
| `DynamicHost.cs` | The fixed root component — renders whatever component was last compiled, via `RenderTreeBuilder.OpenComponent(int, Type)`, inside an error boundary. Also exposes `NavigateTo`. |
| `HostErrorBoundary.cs` | An `ErrorBoundary` that keeps the exception it caught so the host can report it. |
| `HostRouter.cs` | Fallback root for projects with `@page` components but no component to host them: routes over the compiled assembly. |
| `HostLayout.cs` | The default layout for routed pages when neither the page nor the router names one. |
| `RazorCompiler.cs` | Drives the SDK's Razor source generator to turn `.razor` into C#. |
| `CSharpInProcess.cs` | Shared compile path: parse → `CSharpCompilation.Create` → `Emit` → `Assembly.Load`. |
| `ComponentCompiler.cs` | Project compilation and root-component resolution. |
| `ConsoleRunner.cs` | Console mode: entry point invocation with `Console` captured. |
| `ReferenceAssemblies.cs` | The BCL + ASP.NET Core reference assemblies, fetched as a payload on first compile. |
| `Payload.cs` | Fetches and unzips the compiler's payloads (`refs.zip`, `razor.zip`) from the bundle's base URL. |
| `Diagnostics.cs` | `DiagnosticInfo` / `SourceFile` / `CompileResult` / `RunResult`. |
| `BlazorBridge.cs` | The `[JSInvokable]` surface. |
| `wwwroot/blazor-wasm.js` | The loader shipped in the package. |
| `wwwroot/index.html` | The playground page. |
| `../../serve.js` | Static server with an SPA fallback, so routed paths such as `/counter` load the app. |
| `../../scripts/prepare-refs.ps1` | Copies the reference assemblies into `refs/` (BCL, ASP.NET Core, and the Blazor WebAssembly assemblies the ref pack lacks). |
| `../../scripts/make-package.ps1` | Publishes and assembles `package/`. |
| `../../prototype/RazorProto` | Desktop spike for the Razor generator — seconds per iteration instead of a wasm publish. |

### How it works

- **A project compiles into one assembly.** Every file is fed to the Razor generator together and all
  the generated C# goes into a single `CSharpCompilation`, which is what lets components in different
  files reference each other.
- **Folders and imports behave as they do locally.** `Pages/Home.razor` compiles to
  `UserRazor.Pages.Home` — the Razor generator derives the namespace from the folder, exactly as it
  does under `dotnet build` — so a component in another folder is reached with a `@using`, not
  implicitly. The template's root `_Imports.razor` is supplied only when the project has none; a
  project that brings its own is used as-is, folder usings and all. (There is no `global using`
  shortcut, so a project that compiles here compiles locally, and vice versa.)
- **The user's component runs on the app's own renderer.** `DynamicHost` keeps the compiled `Type` and
  opens it as a child component, so it joins the real Blazor render tree — `@onclick` handlers,
  `StateHasChanged`, `[Inject]`, parameters and lifecycle all work.
- **Routing is Blazor's own.** `@page` becomes a `[Route]` attribute, and the routes are reported back
  to the page. A project can bring its own `App.razor` with a `<Router>`; if it declares `@page`
  components and no `App`, `HostRouter` routes over the compiled assembly instead. Navigation goes
  through the host's `NavigationManager`, so it is real client-side routing (the URL changes, and a
  reload works because `serve.js` falls back to `index.html`).
- **Layouts work.** Routed pages render through `RouteView`, so a page's `@layout` is honoured (and
  `LayoutComponentBase`/`@Body` behave as usual); `RouteView` also takes a `DefaultLayout`, which a
  project sets in its `App.razor`. When nothing names a layout, `HostRouter` supplies `HostLayout`.
- **CSS isolation works.** `<Name>.razor.css` is scoped to `<Name>.razor`: the component's elements
  carry a `b-xxxxxxxxxx` attribute (the generator applies the scope that the SDK's `CssScope` item
  metadata names) and `CssScoper` rewrites the stylesheet's selectors to match — the scope goes on the
  last compound selector, or on the last compound before `::deep`, with `@media` recursed into and
  `@keyframes` left alone. The rewritten sheet is rendered alongside the component.
- **Static assets are served.** A project's `wwwroot/` files come back as data URLs keyed by their path
  below it, and the loader points the rendered `src`/`href`/`poster` attributes at them, so
  `<img src="logo.svg">` works as it does locally. A file whose content is already a `data:` URL is
  passed through, so binary assets can be supplied pre-encoded. A `MutationObserver` keeps them
  pointed as the DOM changes, because navigating between `@page` components rebuilds the markup — a
  one-off pass would only hold until the first navigation.
- **Razor is compiled by the real Razor compiler.** There is no standalone Razor library any more, so
  `RazorCompiler` drives the SDK's incremental generator
  (`Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator`) through a `CSharpGeneratorDriver`,
  supplying what the Razor SDK would:
  - the `.razor` files as additional files, plus an `_Imports.razor`;
  - the compile-visible MSBuild properties (`RootNamespace`, `RazorLangVersion`, …);
  - and, on each additional file, `build_metadata.AdditionalFiles.TargetPath` — which the SDK
    **base64-encodes**. Omit it and the generator quietly skips component directives, so `@onclick`
    comes out as literal markup instead of an event handler.
- **The Razor compiler is loaded from embedded bytes, not referenced.** Two reasons, both learned the
  hard way:
  1. **Roslyn 5.9 aborts the WebAssembly runtime.** It is what the Razor compiler asks for, and what
     the SDK ships — but any compilation with it dies with `Program terminated with exit(1)`. Roslyn
     4.14 works. So the app pins 4.14, and the Razor compiler binds against it by simple name (Mono
     ignores the version mismatch here).
  2. **An assembly reference drags Roslyn 5.9 back in.** Referencing the SDK's Razor compiler makes
     MSBuild resolve *its* Roslyn dependency from the SDK and publish it, undoing the pin. Embedding
     the DLLs and loading them with `Assembly.Load(bytes)` avoids that entirely.

  A byte-loaded assembly is not discoverable by name, so the compiler's own dependency on
  `Microsoft.AspNetCore.Razor.Utilities.Shared` is resolved through an
  `AssemblyLoadContext.Default.Resolving` hook.
- **A throwing component cannot take the renderer down.** `DynamicHost` renders inside a
  `HostErrorBoundary`; rendering also waits for the pass to complete, so a render-time exception
  becomes a reported diagnostic instead of a blank area and a bogus success.
- **The compiler's bulk payloads are fetched, not embedded.** `prepare-refs.ps1` copies 308 DLLs
  (11.9 MB) from `Microsoft.NETCore.App.Ref`, `Microsoft.AspNetCore.App.Ref` and the
  `Microsoft.AspNetCore.Components.WebAssembly` package into `refs/`; earlier sources win name
  overlaps. The Blazor WebAssembly assemblies have to come from the package because the ASP.NET Core
  ref pack does not contain them — without them a project cannot use namespaces such as
  `Microsoft.AspNetCore.Components.WebAssembly.Http`, which the template's `_Imports.razor` expects.
  `make-package.ps1` packs those plus the Razor compiler into `refs.zip` and `razor.zip` beside the
  app, and `Payload.cs` fetches them on first use (5.3 MB + 1.8 MB deflated). Embedded they were
  ~16 MB of the app assembly, which the browser cannot start without; split out, the app assembly is
  50 KB, the page paints sooner, a console-only session never downloads the Razor compiler, and the
  payloads keep their own URLs so rebuilding the app no longer invalidates them in the HTTP cache.
  The playground's **C# console** demo shows that directly: a fresh load runs a program and reports
  `payloads: refs.zip`, and only switching to a Razor project pulls `razor.zip` as well.
- **`PublishTrimmed=false`** — the compiled user assembly resolves against the full BCL and ASP.NET
  Core at runtime, so nothing may be linked away.
- **A fresh assembly identity per compile** (`User_<guid>`) — two assemblies with the same name in the
  default load context would clash.
- **`WithConcurrentBuild(false)`** — Roslyn's parallel binding uses the thread pool, and the
  WebAssembly runtime is single-threaded.

### Verified

Checked in a real browser (headless Chrome via CDP), against the packaged output:

- **a template-shaped project**: eight files laid out like `dotnet new blazorwasm` —
  `_Imports.razor`, `App.razor`, `Layout/MainLayout.razor`, `Layout/PlainLayout.razor`,
  `Pages/Home.razor`, `Pages/Counter.razor`, `Pages/About.razor` and a C# file `Greeting.cs` —
  compile together (`rendered UserRazor.App`), with routes reported as `['/', '/about', '/counter']`;
- **folders and imports**: the layouts live in `Layout/` (`UserRazor.Layout`) and `App.razor` can name
  `MainLayout` only because the project's own `_Imports.razor` has `@using UserRazor.Layout` — the
  same file a local project would carry, including the Blazor WebAssembly usings it expects;
- **mixing**: `Pages/Home.razor` calls into the C# file `Greeting.cs` in the same compilation;
- **layouts**: Home and About render inside `MainLayout` (its nav is present), while Counter renders
  through its own `@layout PlainLayout` (no nav);
- **routing**: navigating to `/counter` renders that page (URL becomes `/counter`) and its counter
  increments, and opening `/about` directly renders that page;
- **scoped CSS**: `Layout/MainLayout.razor.css` is applied to the layout's own elements (the layout
  carries `b-3ba7bwx37v` and its computed `border-left` comes from the scoped rule), and its
  `::deep a` rule reaches the links `NavLink` renders;
- **static assets**: `wwwroot/logo.svg` is resolved — the rendered `<img src="logo.svg">` is pointed at
  its data URL;
- **namespace**: rendering with `MyCompany.MyApp` yields `rendered MyCompany.MyApp.App`;
- **C# project**: three files (`App.cs`, `Counter.cs`, `Greeting.cs`) render together;
- **console**: `run` returns `{ success: true, output: "console works" }`;
- a Razor error is reported against the **markup** line (`CS0029: … (line 4)`);
- a component that throws while rendering is contained — the page stays alive, the next render
  recovers, and the UI reports `InvalidOperationException: boom from the component`;
- a component with `[Inject] IJSRuntime` calling `InvokeAsync<int>("eval", "40 + 2")` gets `42`.

### Size and limitations

- **Bundle size.** ~49 MB in `package/` (240 files): ~34 MB the .NET + ASP.NET Core runtime and ~9 MB
  Roslyn, both fetched at startup; ~5.3 MB `refs.zip` and ~1.8 MB `razor.zip`, fetched on first
  compile; and a 50 KB app assembly. Startup fetches **34.0 MB across 207 files**, down from 50.1 MB
  before the payload split, and the first compile adds 7.1 MB of payloads.
- **Lazy loading does not pay off here (measured).** `<BlazorWebAssemblyLazyLoad>` does split the boot
  manifest — 63 lazy / 136 eager, and 144 files / 45.8 MB fetched at startup instead of 207 / 50.1 MB —
  but it is unsafe in this host, for two independent reasons. First, a type's fields and base types are
  resolved when the type *loads*, and static constructors run during type initialisation; a lazy fetch
  cannot service either, so deferring `System.Collections.Concurrent` breaks on
  `JSRuntime._pendingTasks` and deferring `System.Runtime.Loader` breaks in `HotReloadManager..cctor`
  (both runtime boot failures, not build errors). Second, and decisively, **no fetch is ever attempted
  on demand**: code compiled here is loaded with `Assembly.Load(bytes)`, and a deferred dependency of it
  fails with `FileNotFoundException` and no network request at all — so deferring anything user code may
  reference silently breaks programs that used to work. A conservative defer list saves only ~4 MB
  (~8%), and the bulk that remains — Roslyn (~9 MB), CoreLib (~4.7 MB), the Blazor host — has to be
  eager anyway. Trimming is not an alternative either: user code may reference any BCL type.
- **First Razor compile is slow** (~4–5 s vs ~0.2 s for C#) while the Razor generator warms up. Warm
  renders are tens of milliseconds.
- **Stale `obj/` silently keeps an old package.** A `PackageReference` version change does not always
  re-restore; the build then keeps publishing the previous assembly, which looks like "the change had
  no effect". Delete `obj/` when changing package versions and confirm the shipped
  `Microsoft.CodeAnalysis.CSharp.*.wasm` hash changed.
- **Publish into a clean folder.** Publishing into a populated output directory leaves the previous
  content-hashed `BlazorRunner.<hash>.wasm` behind — ~12 MB of dead weight each time, with only one
  referenced. `make-package.ps1` always publishes into a fresh staging directory and asserts a single
  assembly.
- **One project per render.** There is no project-wide build step; nested `@page` parameters
  (`/{id:int}`) transpile but are untested here, and a `wwwroot/` file is exposed as a data URL rather
  than at a real path, so only `src`/`href`/`poster` attributes are rewritten (not, say, `url(...)`
  inside CSS). `bin/` and `obj/` are build output and are not tracked — `package/` and
  `src/BlazorRunner/refs/` are.
- **Per-render leakage is negligible.** Each render loads a new assembly into the default load
  context, but a compiled project is only a few KB.

## Reusing it for C#

**Yes.** `RunCode(source, stdin)` is already the capability LiveCodes' `csharp-wasm` language uses —
its script calls `DotNet.invokeMethodAsync('MyRunnyApp', 'RunCode', code, input)` and reads
`{ output, errors }`, which this bundle mirrors. So `@live-codes/blazor-wasm` can back both the C#
language and Blazor from one copy of Roslyn, the .NET runtime and the BCL reference assemblies.

What that costs, so it is a deliberate choice:

- **Size.** One ~49 MB bundle instead of `csharp-wasm`'s ~40 MB. Two bundles would be ~40 + ~49 MB, so
  consolidation wins as soon as Blazor is used at all.
- **LiveCodes glue, when we get there.** The `csharp-wasm` script hard-codes the `MyRunnyApp` assembly
  name and its bundle URL is pinned in `vendors.ts`; both would point here. The result shape also
  differs slightly — this bundle returns `errors` as an array of diagnostics where the current code
  expects a pre-joined string — so `lang-csharp-wasm-script.ts` needs a few lines to join them.
  Nothing changes in the language spec, editor support or starter template.

## Next steps

- **Wire into LiveCodes** (deliberately not done yet): point the C# language and a new `blazor-wasm`
  language at this package, per [Reusing it for C#](#reusing-it-for-c).
- **Route parameters** — `@page "/item/{id:int}"` transpiles; the matched values are untested.
- **Revisit Roslyn 5.9.** The pin to 4.14 is what makes Roslyn run under wasm; if that is fixed there,
  the embed-and-load dance could be replaced by a plain reference.
- **Shrink the bundle further** — the reference assemblies and the Razor compiler are fetched on demand
  now, but the untrimmed runtime and Roslyn still dominate what startup downloads.
