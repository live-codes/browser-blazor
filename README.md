# browser-blazor

Run **C# and live Blazor components in the browser with no server**.

- **`bundle-poc.html`** — step 1: runs C# in the page with the existing
  [`@seth0x41/csharp-wasm`](https://www.npmjs.com/package/@seth0x41/csharp-wasm) bundle. It works,
  but probing it showed that bundle **cannot render Blazor components**
  ([findings](#step-1--findings)).
- **`src/BlazorRunner`** — our own Blazor WebAssembly host. It compiles user C# with Roslyn **in the
  page**, renders components on Blazor's real interactive renderer, **and** runs console programs —
  so one bundle can back both the C# language and Blazor.

## Quick start

### Step 1 — bundle PoC

```sh
node serve.js                       # http://localhost:8130/bundle-poc.html
```

### Step 2 — build and run the host app

```powershell
# One-time (re-run after upgrading the .NET SDK)
powershell -ExecutionPolicy Bypass -File scripts\prepare-refs.ps1

# Build the deployable package
powershell -ExecutionPolicy Bypass -File scripts\make-package.ps1

# Serve it
node serve.js package 8160          # http://localhost:8160/
```

The playground has a C# editor on the left and the live component on the right. Edit the code, press
**Render** (or Ctrl/Cmd + Enter), and the component is recompiled and re-rendered. The **root
component** box selects which component renders when a file declares several.

`make-package.ps1` publishes into a fresh staging directory, copies only the assets a CDN needs
(dropping the `.br`/`.gz` siblings a .NET publish emits — jsDelivr compresses responses itself) and
writes `package.json`. For quick iteration you can instead `dotnet publish src\BlazorRunner -c
Release -o src\BlazorRunner\dist` and serve `src/BlazorRunner/dist/wwwroot`.

## Prerequisites

- **.NET SDK 10** with the **`wasm-tools` workload**. On a typical Windows box there are two installs
  and they are not equivalent — the one on `PATH` (`%ProgramFiles%\dotnet`) often lacks the workload,
  while `%USERPROFILE%\.dotnet` has it. Use the user-local one:

  ```powershell
  & "$env:USERPROFILE\.dotnet\dotnet.exe" workload list   # must list wasm-tools
  ```

  Both scripts default to `%USERPROFILE%\.dotnet` (`-SdkRoot` / `-DotnetRoot` to override).

- Node.js (only for the static file server).

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
- **Dynamic root components are disabled** — `Blazor.rootComponents.add(...)` reports *"Dynamic root
  components have not been enabled in this application."*

It is a good Roslyn-in-the-browser engine, but not a component host. Hence step 2.

## Step 2 — the host

`src/BlazorRunner` is a `Microsoft.NET.Sdk.BlazorWebAssembly` app published as static files (no
server logic). .NET 10 / ASP.NET Core 10.0.11, Roslyn 4.14.0.

### API

Called from the page with `DotNet.invokeMethodAsync('BlazorRunner', …)`:

| Method | Returns |
| ------ | ------- |
| `RenderComponent(source, rootType)` | `{ success, type, bytes, errors[] }` — compiles and renders a component. `rootType` is an optional component name; when empty the component named `App` is used, else the first. A component that throws *while rendering* comes back as `success: false` with the exception in `errors`. |
| `RunCode(source, stdin)` | `{ success, output, errors[] }` — compiles and runs a console program, capturing stdout. |
| `ReferenceCount()` | number of embedded reference assemblies. |

Each diagnostic is `{ id, message, severity, line, column }`.

### Files

| File | Role |
| ---- | ---- |
| `Program.cs` | Builds the host; mounts the root component at `#blazor-app`; loads the embedded reference assemblies. |
| `DynamicHost.cs` | The fixed root component — renders whatever component was last compiled, via `RenderTreeBuilder.OpenComponent(int, Type)`, inside an error boundary. |
| `HostErrorBoundary.cs` | An `ErrorBoundary` that keeps the exception it caught so the host can report it. |
| `CSharpInProcess.cs` | Shared compile path: parse → `CSharpCompilation.Create` → `Emit` → `Assembly.Load`. |
| `ComponentCompiler.cs` | Picks the root component out of the compiled assembly (named `App`, else first, else explicit). |
| `ConsoleRunner.cs` | Console mode: entry point invocation with `Console` captured. |
| `ReferenceAssemblies.cs` | The embedded BCL + ASP.NET Core reference assemblies. |
| `Diagnostics.cs` | `DiagnosticInfo` / `CompileResult` / `RunResult`. |
| `BlazorBridge.cs` | The `[JSInvokable]` surface. |
| `wwwroot/index.html` | The playground page. |
| `../../scripts/prepare-refs.ps1` | Copies the reference assemblies into `refs/`. |
| `../../scripts/make-package.ps1` | Publishes and assembles `package/`. |

### How it works

- **The user's component runs on the app's own renderer.** `DynamicHost` keeps the compiled `Type`
  and opens it as a child component, so it joins the real Blazor render tree — event handlers
  (`onclick`), `StateHasChanged`, `[Inject]`, parameters and lifecycle all work.
- **Several components per compile.** A source file may declare many components; the root is the one
  named `App` (or an explicit name) and the others are usable as children.
- **A throwing component cannot take the renderer down.** `DynamicHost` renders the user's component
  inside a `HostErrorBoundary`; rendering also waits for the pass to complete, so a render-time
  exception becomes a reported diagnostic instead of a blank area and a bogus success.
- **Reference assemblies are embedded**, not fetched: `prepare-refs.ps1` copies 307 DLLs (11.7 MB)
  from `Microsoft.NETCore.App.Ref` and `Microsoft.AspNetCore.App.Ref` into `refs/`, and the csproj
  embeds them as `lib.*` resources. The BCL is copied first so it wins any name overlap.
- **`PublishTrimmed=false`** — the compiled user assembly resolves against the full BCL and ASP.NET
  Core at runtime, so nothing may be linked away.
- **A fresh assembly identity per compile** (`User_<guid>`) — two assemblies with the same name in
  the default load context would clash.
- **`WithConcurrentBuild(false)`** — Roslyn's parallel binding uses the thread pool, and the
  WebAssembly runtime is single-threaded.

### Verified

Checked in a real browser (headless Chrome via CDP), against the packaged output:

- a parent component rendering a child (`OpenComponent<Counter>`) compiles and renders; clicking the
  child's button increments it — genuinely interactive;
- the `root component` box selects which component renders (`Counter` instead of `App`);
- an unknown root reports `BLAZOR0002: Component 'Nope' was not found. Available: Counter, App.`;
- a compile error reports `CS0246: … (line 2)`;
- a component that throws while rendering is contained by the boundary — the page stays alive, the
  next render recovers, and the UI reports `InvalidOperationException: boom from the component`;
- a component with `[Inject] IJSRuntime` calling `InvokeAsync<int>("eval", "40 + 2")` gets `42`, so
  user components can talk to the page;
- the console runner returns `Hello from the console runner` plus `stdin was: hello stdin`.

### Size and limitations

- **Bundle size.** ~54 MB in `package/` (237 files): ~37 MB the .NET + ASP.NET Core runtime, ~12 MB
  the app assembly carrying Roslyn and the embedded reference assemblies.
- **Publish into a clean folder.** Publishing into a populated output directory leaves the previous
  content-hashed `BlazorRunner.<hash>.wasm` behind — ~12 MB of dead weight each time, with only one
  referenced by `blazor.boot.json`. `make-package.ps1` always publishes into a fresh staging
  directory and asserts a single assembly; a repeated `dotnet publish -o <same folder>` does not.
- **C# only, no `.razor`.** Components are C# classes (`BuildRenderTree`). See below.
- **Per-render leakage is negligible.** Each render loads a new assembly into the default load
  context, but a compiled component is only ~2.6–4.6 KB, so this is not worth an unloadable
  `AssemblyLoadContext` yet.

### Why there is no `.razor` yet

`.razor` markup is the obvious next step, but the pieces do not line up on .NET 10:

- The Razor compiler is no longer a standalone library — it ships as the **source generator**
  `Microsoft.CodeAnalysis.Razor.Compiler.dll` inside the SDK, so it would have to be driven through
  `CSharpGeneratorDriver` with a hand-built `AnalyzerConfigOptionsProvider`.
- That assembly is built against the SDK's **Roslyn 5.9** (5.900.26.38015), while this app pins
  **`Microsoft.CodeAnalysis.CSharp` 4.14.0**; the reference cannot bind in-process. Supporting
  `.razor` therefore means moving the whole host to the SDK's Roslyn 5.9 assemblies first.
- The old standalone `Microsoft.AspNetCore.Razor.Language` API is EOL (last release 6.0.36), and the
  NuGet `Microsoft.CodeAnalysis.Razor.Compiler` package is preview-only (10.0.0-preview.25277.114).

So: a real, scoped piece of work, deliberately deferred rather than half-done.

## Reusing it for C#

**Yes.** `RunCode(source, stdin)` is already the capability LiveCodes' `csharp-wasm` language uses —
its script calls `DotNet.invokeMethodAsync('MyRunnyApp', 'RunCode', code, input)` and reads
`{ output, errors }`, which this bundle mirrors. So `@live-codes/blazor-wasm` can back both the C#
language and Blazor from **one** copy of Roslyn, the .NET runtime and the BCL reference assemblies,
instead of shipping `csharp-wasm` and a separate Blazor bundle.

What that costs, so it is a deliberate choice:

- **Size.** One ~54 MB bundle instead of `csharp-wasm`'s ~40 MB. A C#-only session pays ~14 MB more
  (the ASP.NET Core runtime and its reference assemblies) for capability it does not use. Two
  bundles would be ~40 MB + ~54 MB, so consolidation wins as soon as Blazor is used at all.
- **LiveCodes glue, when we get there.** The `csharp-wasm` script hard-codes the `MyRunnyApp`
  assembly name and its bundle URL is pinned in `vendors.ts`; both would point here. The result shape
  also differs slightly — this bundle returns `errors` as an array of diagnostics where the current
  code expects a pre-joined string — so `lang-csharp-wasm-script.ts` needs a few lines to join them.
  Nothing changes in the language spec, editor support or starter template.
- **Further consolidation.** `vb-wasm` uses a separate Roslyn package
  (`Microsoft.CodeAnalysis.VisualBasic`), so folding it in would mean one bundle carrying both
  compilers (~+5 MB) to serve C#, VB and Blazor together.

## Next steps

- **Move to the SDK's Roslyn 5.9**, which unblocks `.razor` (see above).
- **Wire into LiveCodes** (deliberately not done yet): point the C# language and a new `blazor-wasm`
  language at this package, per [Reusing it for C#](#reusing-it-for-c).
- **Shrink the bundle** — the untrimmed runtime and the embedded reference assemblies are the main
  cost.
