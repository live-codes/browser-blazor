# Findings

The measurements and hard-won details behind this bundle, kept out of the [README](./README.md) so
that it can lead with usage. Everything here was measured rather than assumed; where a number is an
estimate it says so.

## 1. Why this exists: the step-1 probe

`bundle-poc.html` runs C# in the page with LiveCodes' existing
[`@seth0x41/csharp-wasm`](https://www.npmjs.com/package/@seth0x41/csharp-wasm) bundle. It works, but
probing it (from `_framework/blazor.boot.json` and the string table of `MyRunnyApp.*.wasm`) showed it
cannot host components:

- **It compiles and runs C# entirely in the browser** — compiled to a dynamic assembly, loaded into
  `AssemblyLoadContext`, `Console` I/O captured. Cold run ~2 s, warm ~25 ms.
- **The Roslyn reference set excludes Blazor.** `using Microsoft.AspNetCore.Components;` fails with
  *"The type or namespace name 'AspNetCore' does not exist in the namespace 'Microsoft'"*, so user
  code cannot reference Blazor types and cannot author a component.
- **Blazor's rendering types are trimmed.** `ComponentBase` resolves at runtime, but
  `Microsoft.AspNetCore.Components.Web.HtmlRenderer` and `RenderTreeBuilder` are **not found**.
- **The app has no main root component** (its only selector literal is `head::after`) and the npm
  package ships no `index.html`, so its own UI cannot be booted standalone.

It is a good Roslyn-in-the-browser engine, but not a component host. Hence a self-owned host.

## 2. Host and compilation

`src/BlazorRunner` is a `Microsoft.NET.Sdk.BlazorWebAssembly` app published as static files, with no
server logic. .NET 10 / ASP.NET Core 10.0.11, **Roslyn 4.14.0**.

- **Razor is compiled by the real Razor compiler.** There is no standalone Razor library any more, so
  `RazorCompiler` drives the SDK's incremental generator
  (`Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator`) through a `CSharpGeneratorDriver`,
  supplying what the Razor SDK would:
  - the `.razor` files as additional files, plus an `_Imports.razor`;
  - the compile-visible MSBuild properties (`RootNamespace`, `RazorLangVersion`, …);
  - and, on each additional file, `build_metadata.AdditionalFiles.TargetPath` — which the SDK
    **base64-encodes**. Omit it and the generator quietly skips component directives, so `@onclick`
    comes out as literal markup instead of an event handler.
- **Roslyn 5.9 aborts the WebAssembly runtime.** It is what the Razor compiler asks for, and what the
  SDK ships — but any compilation with it dies with `Program terminated with exit(1)`, repeatedly, in
  `Volatile.ReadBarrier`. Roslyn 4.14 runs. The app pins 4.14, and the Razor compiler binds against it
  by simple name (Mono ignores the version mismatch here).
- **The Razor compiler travels as bytes, not as a reference.** Referencing the SDK's Razor compiler
  makes MSBuild resolve *its* Roslyn 5.9 dependency out of the SDK and publish it, undoing the pin.
  Loading the DLLs with `Assembly.Load(bytes)` avoids that. A byte-loaded assembly is not discoverable
  by name, so the compiler's dependency on `Microsoft.AspNetCore.Razor.Utilities.Shared` is resolved
  through an `AssemblyLoadContext.Default.Resolving` hook.
- **A project compiles into one assembly.** Every file is fed to the Razor generator together and all
  the generated C# goes into a single `CSharpCompilation`, which is what lets components in different
  files reference each other.
- **Folders and imports behave as they do locally.** `Pages/Home.razor` compiles to
  `UserRazor.Pages.Home` — the generator derives the namespace from the folder, exactly as under
  `dotnet build` — so a component in another folder is reached with a `@using`, not implicitly. The
  template's root `_Imports.razor` is supplied only when the project has none. There is no
  `global using` shortcut: a project that compiles here compiles locally, and vice versa. (An early
  version injected global usings; it was removed for exactly this reason — a playground more
  permissive than `dotnet build` is a trap.)
- **`PublishTrimmed=false`.** The compiled user assembly resolves against the full BCL and ASP.NET
  Core at runtime, so nothing may be linked away.
- **A fresh assembly identity per compile** (`User_<guid>`) — two assemblies with the same name in the
  default load context would clash.
- **`WithConcurrentBuild(false)`** — Roslyn's parallel binding uses the thread pool, and the
  WebAssembly runtime is single-threaded.
- **Razor directives must start a line.** `@code` in the middle of a markup line is parsed as literal
  markup, so fields it declares do not exist (`CS0841`). Correct Razor behaviour, matching
  `dotnet build`, but easy to trip over from a single-line string in a browser test.

## 3. Rendering model

- **The user's component runs on the app's own renderer.** `DynamicHost` keeps the compiled `Type` and
  opens it as a child component (`RenderTreeBuilder.OpenComponent(int, Type)`), so it joins the real
  Blazor render tree — `@onclick` handlers, `StateHasChanged`, `[Inject]`, parameters and lifecycle
  all work.
- **Routing is Blazor's own.** `@page` becomes a `[Route]` attribute and the routes are reported back
  to the page. A project can bring its own `App.razor` with a `<Router>`; if it declares `@page`
  components and no `App`, `HostRouter` routes over the compiled assembly instead. Navigation goes
  through the host's `NavigationManager`, so a reload works (`serve.js` falls back to `index.html`).
- **Layouts work.** Routed pages render through `RouteView`, so a page's `@layout` is honoured and
  `LayoutComponentBase`/`@Body` behave as usual. When nothing names a layout, `HostRouter` supplies
  `HostLayout`.
- **CSS isolation works.** `<Name>.razor.css` is scoped to `<Name>.razor`: the component's elements
  carry a `b-xxxxxxxxxx` attribute (the generator applies the scope that the SDK's `CssScope` item
  metadata names) and `CssScoper` rewrites the stylesheet's selectors to match — the scope goes on the
  last compound selector, or on the last compound before `::deep`, with `@media` recursed into and
  `@keyframes` left alone.
- **Static assets are served.** A project's `wwwroot/` files come back as data URLs keyed by their path
  below it, and the loader points the rendered `src`/`href`/`poster` attributes at them. A file whose
  content is already a `data:` URL passes through, so binary assets can be supplied pre-encoded. A
  `MutationObserver` keeps them pointed as the DOM changes, because navigating between `@page`
  components rebuilds the markup — a one-off pass only held until the first navigation.
- **A throwing component cannot take the renderer down.** `DynamicHost` renders inside a
  `HostErrorBoundary`; rendering also waits for the pass to complete, so a render-time exception
  becomes a reported diagnostic instead of a blank area and a bogus success.
- **The mount point is the loader's decision.** Blazor has no convention of its own: a project names
  the selector in `Program.cs` (`builder.RootComponents.Add<App>("#app")`, paired with a matching
  element in the page), or declaratively for Server and Web App projects. Here the host fixes
  `#blazor-app`, and the loader's `root` option says where that element should be placed — creating it
  inside the chosen container if it is not already there, before `Blazor.start()`, because a root
  component is attached at startup and cannot be moved afterwards. A page that places the element
  itself is left alone.
- **RenderTreeBuilder frame rule.** `AddComponentReferenceCapture` inserts a frame, so `AddAttribute`
  must come immediately after `OpenComponent` — otherwise *"Attributes may only be added immediately
  after frames of type Element or Component."*

## 4. The payload split

The reference assemblies and the Razor compiler used to be `EmbeddedResource`s in the app assembly.
Embedded, they put ~16 MB into a file the browser cannot start the app without. They now travel as
`refs.zip` and `razor.zip` beside the app and are fetched on first use (`Payload.cs`):

| | before | after |
| --- | --- | --- |
| app assembly | ~16 MB | 50 KB |
| startup fetch | 50.1 MB / 207 files | **34.0 MB / 207 files** |
| first compile adds | — | 7.1 MB (`refs.zip` 5.3 + `razor.zip` 1.8) |
| page total | 50.2 MB | 41.1 MB |

`ComponentCompiler.EnsureLoadedAsync` decides what a call needs: references always, the Razor compiler
only when there is markup. So a console program never downloads `razor.zip`.

Two ordering assumptions had to go, both found by failing in the browser:

- **`WebAssemblyHostBuilder.CreateDefault` does not register `HttpClient`** — the Blazor template adds
  it in `Program.cs`. Resolving one threw `NoServiceRegistered` and the host silently never started
  (the symptom was payloads fetching fine while every render reported "the host is not ready").
  `Payload` creates its own client, which also covers the next point.
- **The first interop call can arrive before `Program.cs` finishes**, and the host instance only exists
  once the app has rendered. The bridge now waits briefly for the host instead of failing a render that
  would have worked.

## 5. Lazy loading: measured, then rejected

`<BlazorWebAssemblyLazyLoad>` does split the boot manifest — 63 lazy / 136 eager, and 144 files /
45.8 MB at startup instead of 207 / 50.1 MB — but it is unsafe in this host, for two independent
reasons:

1. **A type's fields and base types are resolved when the type *loads*, and static constructors run
   during type initialisation.** A lazy fetch is asynchronous and cannot service either, so deferring
   `System.Collections.Concurrent` breaks on `JSRuntime._pendingTasks`, and deferring
   `System.Runtime.Loader` breaks in `HotReloadManager..cctor`. Both are *runtime boot failures*, not
   build errors — the worst kind to discover.
2. **No fetch is ever attempted on demand.** Compiled user code is loaded with `Assembly.Load(bytes)`,
   and a deferred dependency of it fails with `FileNotFoundException` and **no network request at all**.
   Deferring anything user code might reference therefore breaks programs that used to work.

A conservative defer list saved only ~4 MB (~8%), and the bulk that remains — Roslyn (~9 MB), CoreLib
(~4.7 MB), the Blazor host — has to be eager anyway. Trimming is not an alternative either: user code
may reference any BCL type. The generated list was reverted; `BlazorWebAssemblyLazyLoad` entries must
be literal file names and a stale one is a hard build error (`BLAZORSDK1001`).

## 6. Download size, measured

Both measured in a real browser, running a console program:

| | files | decoded | downloaded (brotli) |
| --- | --- | --- | --- |
| `csharp-wasm@1.0.3` (jsDelivr) | 84 | 35.5 MB | **13.8 MB** |
| this bundle, console path | 208 | 39.2 MB | **16.4 MB** |

Method: for the old bundle, a real jsDelivr load, reading `transferSize` / `decodedBodySize` from
`performance.getEntriesByType('resource')`. This bundle's server sends no compression, so its fetched
set (`_framework/*` plus `refs.zip`) was brotli'd offline at quality 9 — the figure is therefore
slightly conservative against jsDelivr's own q11.

So ~19% more than the C#-only bundle for a console program, buying the untrimmed BCL + ASP.NET Core
and the Blazor host. Shipping both would cost ~13.8 + ~16.4 MB. The one avoidable part is `refs.zip`:
deflated it is 5.3 MB, where brotli over the raw DLLs reaches 4.9 MB — a CDN compresses better than a
zip that got there first. Shipping it uncompressed would cost a slower local dev server, since
`serve.js` does not compress.

## 7. Build notes and traps

- **Stale `obj/` silently keeps an old package.** A `PackageReference` version change does not always
  re-restore; the build then keeps publishing the previous assembly, which looks like "the change had
  no effect". Delete `obj/` when changing package versions and confirm the shipped
  `Microsoft.CodeAnalysis.CSharp.*.wasm` hash changed. (This is what hid the Roslyn 5.9 crash: an old
  Roslyn stayed on disk, so every build shipped a byte-identical compiler.)
- **Publish into a clean folder.** Publishing into a populated output directory leaves the previous
  content-hashed `BlazorRunner.<hash>.wasm` behind — ~12 MB of dead weight each time, with only one
  referenced. `make-package.ps1` publishes into a fresh staging directory and asserts a single
  assembly.
- **PowerShell 5.1 has no `GetRelativePath`.** A `Substring`-based copy silently flattened
  `_framework/`, which killed the boot manifest and left `window.Blazor` undefined. The script uses
  `robocopy` with exclusions instead.
- **`robocopy /PURGE`** is why `README.md` and `LICENSE` cannot simply live in `package/`: anything not
  in the publish output is removed, so `make-package.ps1` copies them in each build.
- **Ask MSBuild where the SDK is.** The Razor compiler ships inside the SDK, so the packaging script
  reads `MSBuildSDKsPath` via `dotnet msbuild -getProperty` rather than guessing at a version
  directory.
- **A killed build can wedge the next one.** An orphaned `obj/package-staging-*` directory from an
  interrupted publish made the following build sit for 20 minutes doing nothing; removing it made the
  next build finish in ~2 s.
- **Reference assemblies come from three places.** `prepare-refs.ps1` copies 308 DLLs (11.9 MB) from
  `Microsoft.NETCore.App.Ref`, `Microsoft.AspNetCore.App.Ref` and the
  `Microsoft.AspNetCore.Components.WebAssembly` package into `refs/`, earlier sources winning name
  overlaps. The Blazor WebAssembly assemblies have to come from the package because the ASP.NET Core
  ref pack does not contain them — without them a project cannot use namespaces such as
  `Microsoft.AspNetCore.Components.WebAssembly.Http`, which the template's `_Imports.razor` expects.
- **Two dotnet installs on Windows are not equivalent.** The one on `PATH` often lacks the `wasm-tools`
  workload; `%USERPROFILE%\.dotnet` has it. Both scripts default to the latter.

## 8. Verified in the browser

Checked against the packaged output, headless Chrome via CDP:

- **a template-shaped project**: eight files laid out like `dotnet new blazorwasm` — `_Imports.razor`,
  `App.razor`, `Layout/MainLayout.razor`, `Layout/PlainLayout.razor`, `Pages/Home.razor`,
  `Pages/Counter.razor`, `Pages/About.razor` and `Greeting.cs` — compile together
  (`rendered UserRazor.App`), with routes reported as `['/', '/about', '/counter']`;
- **folders and imports**: the layouts live in `Layout/` (`UserRazor.Layout`) and `App.razor` can name
  `MainLayout` only because the project's own `_Imports.razor` has `@using UserRazor.Layout`;
- **mixing**: `Pages/Home.razor` calls into the C# file in the same compilation;
- **layouts**: Home and About render inside `MainLayout`; Counter renders through its own
  `@layout PlainLayout`;
- **routing**: navigating to `/counter` renders that page (URL becomes `/counter`) and its counter
  increments; opening `/about` directly renders that page;
- **static assets**: `wwwroot/logo.svg` is resolved — the rendered `<img src="logo.svg">` points at its
  data URL, and navigating away and back leaves it a data URL (the `MutationObserver`);
- **scoped CSS**: the layout carries `b-3ba7bwx37v` and its computed `border-left` comes from the
  scoped rule; `::deep a` reaches the links `NavLink` renders;
- **namespace**: rendering with `MyCompany.MyApp` yields `rendered MyCompany.MyApp.App`;
- **console**: console programs run with `Console` captured, including code touching `System.Data`
  (`rows=1 val=42`) — which is also the case that proves deferred assemblies cannot be relied on;
- **single file**: `renderRazor('<h1>Hello</h1>')` needs no `_Imports.razor`, and a component's
  `@onclick` increments;
- a Razor error is reported against the **markup** line (`CS0029: … (line 4)`);
- a component that throws while rendering is contained — the page stays alive, the next render
  recovers, and the UI reports the exception;
- a component with `[Inject] IJSRuntime` calling `InvokeAsync<int>("eval", "40 + 2")` gets `42`;
- the payload split: a fresh load of the console demo reports `payloads: refs.zip`; switching to a
  Razor project adds `razor.zip`.

## 9. Publishing, and wiring into LiveCodes

The package root is `package/`, so publishing is `cd package && npm publish --access public` (scoped,
so `--access public` is required). No `main`/`exports` on purpose: this is a static asset bundle loaded
by URL, and `blazor-wasm.js` is a browser-only IIFE.

Wiring it into LiveCodes, when that is wanted:

- **Pin the base URL** in `vendors.ts`, alongside the other `getUrl(...)` entries.
- **Point the C# language at it.** Its script calls `DotNet.invokeMethodAsync('MyRunnyApp', 'RunCode',
  code, input)` and reads `{ output, errors }`. Two small changes: the assembly is `BlazorRunner`, and
  this bundle returns `errors` as an **array** of diagnostics where the current code expects a
  pre-joined string.
- **Use the loader, or call `SetBaseUrl`.** LiveCodes currently hand-rolls the boot
  (`loadBlazorScript()` + `Blazor.start()` + its own `patchFetch`). If it keeps doing that,
  `SetBaseUrl` never runs and the payload fetch fails with *"The bundle's base URL was not supplied"* —
  the app boots and then fails on the first compile. Loading `blazor-wasm.js` and calling
  `BlazorRunner.create({ baseUrl })` is the cleaner route; it patches `fetch` for `credentials: 'omit'`
  itself, so no LiveCodes-side patching is needed.
- **A `blazor-wasm` language** needs a spec (extensions `razor`/`blazor`, `editor: 'markup'`,
  `largeDownload: true`), registration in `languages.ts`, the language-info/i18n entries and a starter
  template.
- **Multi-file is an open question.** `LanguageSpecs` has no `files` field — a LiveCodes language is one
  editor pane — while `RenderProject` takes a list of `{ filename, content }`. Single-file Razor works
  today; a real multi-file project would have to go through LiveCodes' File Tree.
- **Smoke-test cross-origin before pinning.** Two things cannot fail locally because `serve.js` sends no
  CORS headers: that jsDelivr serves `refs.zip`/`razor.zip` with CORS, and that the loader's
  `document.currentScript`-derived base URL resolves (if the script is injected in a way that leaves
  `currentScript` null, it falls back to an *unversioned* jsDelivr URL, which could pair a pinned script
  with newer payloads). Passing `baseUrl` explicitly sidesteps both.

## 10. Open items

- **Route parameters** — `@page "/item/{id:int}"` transpiles; the matched values are untested.
- **Revisit Roslyn 5.9.** The pin to 4.14 is what makes Roslyn run under wasm; if that is fixed there,
  the embed-and-load dance could be replaced by a plain reference.
- **Shrink the bundle further** — the reference assemblies and the Razor compiler are fetched on demand
  now, but the untrimmed runtime and Roslyn still dominate what startup downloads.
- **`wwwroot/` files have no real path.** They are data URLs, so `url(...)` inside CSS, `fetch()` and
  `@font-face src` do not resolve; only `src`/`href`/`poster` attributes are rewritten. A service worker
  would fix it, at the cost of being invasive.
- **`Program.cs` is not editable**, so a project cannot vary DI or `HttpClient` — the host owns the
  entry point.
- **Per-render leakage is negligible.** Each render loads a new assembly into the default load context,
  but a compiled project is only a few KB.

## 11. Compiler in a worker — exploration

Not built. This records why it is the obvious next step for responsiveness, what it would cost, and
what it would not fix.

**The problem.** Compilation is synchronous Roslyn + Razor on the page's main thread:
`CSharpInProcess` parses, emits and `Assembly.Load`s, and `RazorCompiler` runs the generator — all in
the interop call. During the first Razor compile (~4–5 s) nothing driven by the main thread moves, so
the page looks hung. The console path blocks the same way, because `ConsoleRunner` invokes the user's
entry point in-process. The playground's status spinner is a compositor `transform` animation for
exactly this reason — it keeps moving while the thread is blocked (`wwwroot/index.html`).

**What would move.** Everything that compiles or runs user code and needs no DOM: `CSharpInProcess`,
`RazorCompiler`, `CssScoper`, route extraction, `ReferenceAssemblies` (`refs.zip`), `Payload`, and
`ConsoleRunner`. What stays on the main thread is the Blazor renderer — `DynamicHost`, `HostRouter`,
`HostLayout`, `HostErrorBoundary` — which owns the DOM and cannot leave it.

**Design sketch: a second, Blazor-free .NET WebAssembly module in a dedicated Worker.**

- The worker exports `SetBaseUrl`, `CompileProject(filesJson, rootNamespace)`, `RunConsole(source, stdin)`,
  `ReferenceCount` and `LoadedPayloads`, reusing the existing compiler code unchanged.
- The main-thread app drops Roslyn, the Razor compiler and the reference assemblies entirely; for a
  component it receives an assembly image and `Assembly.Load`s it, then renders the resolved `Type`. A
  compiled project is a few KB, so the bytes cross the boundary as a transferable `ArrayBuffer`.
- The worker fetches `refs.zip` / `razor.zip` itself (its own `HttpClient`; the same `baseUrl` the
  loader already pushes in), and can pre-warm the Razor generator with a throwaway compile so the
  user's first render is warm.
- Boot the worker lazily, on the first compile, so the initial paint stays light.

**Why the alternatives were rejected.**

- *Yield / chunk the work*: Roslyn's parse + emit and the generator run are single synchronous calls;
  there is no seam to yield at.
- *WebAssembly threads* (`WasmEnableThreads` + `SharedArrayBuffer`): needs cross-origin isolation
  (COOP/COEP) the bundle does not have, and `WithConcurrentBuild(false)` exists precisely because the
  thread pool is not available.
- *Host the renderer in the worker*: Blazor manipulates the DOM and JS interop; it cannot run off the
  main thread. Only the compiler can move.

**Costs and risks.**

- **A second .NET runtime instance.** The main thread keeps the Blazor runtime; the worker adds another
  (CoreLib + Mono). Roslyn (~9 MB), the reference assemblies (5.3 MB) and the Razor compiler (1.8 MB)
  move into the worker, so the main-thread *startup* shrinks, but the total download grows. This buys
  responsiveness, not size — the opposite trade to §5, which was rejected for safety.
- **Memory.** Two runtimes in one tab, on top of an untrimmed BCL + ASP.NET Core.
- **Trimming cannot offset it.** The worker runs console programs, which may reference any BCL/ASP.NET
  type, so its runtime must stay untrimmed too.
- **Plumbing, not novel problems.** The risks are the worker's `dotnet` boot, the message protocol and
  the error paths — plus keeping the Roslyn 4.14 pin working in the worker runtime. The compile itself
  is the same code that already runs here; only its host thread changes.

**Lighter variant, if a second runtime is too much: pre-warm only.** Run a throwaway compile at boot so
the Razor generator is warm before the user's first render; the freeze moves into the loading phase the
user already waits through. It does not remove the boot freeze, and because a code change currently
reloads the page (no live reload — the render tree is bound to the DOM), it would still re-warm on every
reload. Only worth doing alongside keeping the runtime alive across edits.

**Recommendation.** A spike is worth it if *"the page must stay interactive while the first compile
runs"* is a requirement; if *"the page must not look hung"* is enough, the compositor spinner already
covers it. Spike plan: (1) add `src/BlazorCompiler` as a Blazor-free wasm module exposing
`CompileProject` / `RunConsole`; (2) host it in a worker from `blazor-wasm.js` and `Assembly.Load` its
output on the main thread; (3) measure total bytes, main-thread boot, first-render time (cold and
pre-warmed), peak memory, and confirm no main-thread task exceeds ~50 ms during a compile.
