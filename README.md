# browser-blazor

Run **C# and live Blazor components in the browser with no server** — components written in
**Razor markup or C#**, compiled in the page and rendered on Blazor's real renderer.

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

The playground has a component editor on the left and the live result on the right. Pick **Razor** or
**C#**, edit, and press **Render** (or Ctrl/Cmd + Enter). The **component name** box names the
generated class (default `App`).

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

  const a = await runner.renderRazor('<h1>Hello</h1>');      // Razor markup
  const b = await runner.render('public class App : ComponentBase { /* ... */ }');   // C#
  const c = await runner.run('using System; class P { static void Main() => Console.WriteLine("hi"); }');
</script>
```

`render` / `renderRazor` resolve to `{ success, type, bytes, errors[] }` and `run` to
`{ success, output, errors[] }`; every diagnostic is `{ id, message, severity, line, column }`. Pass
`{ baseUrl, onProgress }` to `create` to point at a different copy or report download progress. The
playground (`wwwroot/index.html`) uses this same loader.

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
| `RenderRazor(source, componentName)` | `{ success, type, bytes, errors[] }` — compiles `.razor` markup and renders it. `componentName` names the generated class (default `App`). |
| `RenderComponent(source, rootType)` | `{ success, type, bytes, errors[] }` — same, for a component written in C#. `rootType` is an optional component name; when empty the component named `App` is used, else the first. |
| `RunCode(source, stdin)` | `{ success, output, errors[] }` — compiles and runs a console program, capturing stdout. |
| `ReferenceCount()` | number of embedded reference assemblies. |

A component that throws *while rendering* comes back as `success: false` with the exception in
`errors`. Diagnostics from generated Razor code are mapped back to the `.razor` source lines.

### Files

| File | Role |
| ---- | ---- |
| `Program.cs` | Builds the host; mounts the root component at `#blazor-app`; loads the embedded reference assemblies. |
| `DynamicHost.cs` | The fixed root component — renders whatever component was last compiled, via `RenderTreeBuilder.OpenComponent(int, Type)`, inside an error boundary. |
| `HostErrorBoundary.cs` | An `ErrorBoundary` that keeps the exception it caught so the host can report it. |
| `RazorCompiler.cs` | Drives the SDK's Razor source generator to turn `.razor` into C#. |
| `CSharpInProcess.cs` | Shared compile path: parse → `CSharpCompilation.Create` → `Emit` → `Assembly.Load`. |
| `ComponentCompiler.cs` | Razor/C# component compilation and root-component selection. |
| `ConsoleRunner.cs` | Console mode: entry point invocation with `Console` captured. |
| `ReferenceAssemblies.cs` | The embedded BCL + ASP.NET Core reference assemblies. |
| `Diagnostics.cs` | `DiagnosticInfo` / `CompileResult` / `RunResult`. |
| `BlazorBridge.cs` | The `[JSInvokable]` surface. |
| `wwwroot/blazor-wasm.js` | The loader shipped in the package. |
| `wwwroot/index.html` | The playground page. |
| `../../scripts/prepare-refs.ps1` | Copies the reference assemblies into `refs/`. |
| `../../scripts/make-package.ps1` | Publishes and assembles `package/`. |
| `../../prototype/RazorProto` | Desktop spike for the Razor generator — seconds per iteration instead of a wasm publish. |

### How it works

- **The user's component runs on the app's own renderer.** `DynamicHost` keeps the compiled `Type` and
  opens it as a child component, so it joins the real Blazor render tree — `@onclick` handlers,
  `StateHasChanged`, `[Inject]`, parameters and lifecycle all work.
- **Razor is compiled by the real Razor compiler.** There is no standalone Razor library any more, so
  `RazorCompiler` drives the SDK's incremental generator
  (`Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator`) through a `CSharpGeneratorDriver`,
  supplying what the Razor SDK would:
  - the `.razor` files as additional files, plus an `_Imports.razor`;
  - the compile-visible MSBuild properties (`RootNamespace`, `RazorLangVersion`, …);
  - and, on each additional file, `build_metadata.AdditionalFiles.TargetPath` — which the SDK
    **base64-encodes**. Omit it and the generator quietly skips component directives, so `@onclick`
    comes out as literal markup instead of an event handler.

  The generated C# then goes through the same Roslyn path as a hand-written component.
- **The Razor compiler is loaded from embedded bytes, not referenced.** Two reasons, both learned the
  hard way:
  1. **Roslyn 5.9 aborts the WebAssembly runtime.** It is what the Razor compiler asks for, and what
     the SDK ships — but any compilation with it dies with `Program terminated with exit(1)`. Roslyn
     4.14 works. So the app pins 4.14 and the Razor compiler binds against it by simple name (Mono
     ignores version mismatches for this).
  2. **An assembly reference drags Roslyn 5.9 back in.** Referencing the SDK's Razor compiler makes
     MSBuild resolve *its* Roslyn dependency from the SDK and publish it, undoing the pin. Embedding
     the DLLs and loading them with `Assembly.Load(bytes)` avoids that entirely.

  A byte-loaded assembly is not discoverable by name, so the compiler's own dependency on
  `Microsoft.AspNetCore.Razor.Utilities.Shared` is resolved through an
  `AssemblyLoadContext.Default.Resolving` hook.
- **A throwing component cannot take the renderer down.** `DynamicHost` renders the user's component
  inside a `HostErrorBoundary`; rendering also waits for the pass to complete, so a render-time
  exception becomes a reported diagnostic instead of a blank area and a bogus success.
- **Reference assemblies are embedded**, not fetched: `prepare-refs.ps1` copies 307 DLLs (11.7 MB)
  from `Microsoft.NETCore.App.Ref` and `Microsoft.AspNetCore.App.Ref` into `refs/`, and the csproj
  embeds them as `lib.*` resources.
- **`PublishTrimmed=false`** — the compiled user assembly resolves against the full BCL and ASP.NET
  Core at runtime, so nothing may be linked away.
- **A fresh assembly identity per compile** (`User_<guid>`) — two assemblies with the same name in the
  default load context would clash.
- **`WithConcurrentBuild(false)`** — Roslyn's parallel binding uses the thread pool, and the
  WebAssembly runtime is single-threaded.

### Verified

Checked in a real browser (headless Chrome via CDP), against the packaged output:

- **Razor**: the sample markup compiles in-page (`rendered UserRazor.App`) and its `@onclick` handler
  increments a counter (0 → 1) — the markup really became a live component;
- **C#**: the same playground in C# mode renders (`rendered App`, `bytes: 2560`);
- **console**: `run` returns `{ success: true, output: "console works" }`;
- the loader drives all three entry points;
- a Razor error is reported against the **markup** line (`CS0029: … (line 4)`);
- a component that throws while rendering is contained — the page stays alive, the next render
  recovers, and the UI reports `InvalidOperationException: boom from the component`;
- a component with `[Inject] IJSRuntime` calling `InvokeAsync<int>("eval", "40 + 2")` gets `42`.

### Size and limitations

- **Bundle size.** ~58 MB in `package/` (238 files): ~36 MB the .NET + ASP.NET Core runtime, ~9 MB
  Roslyn, ~4 MB the Razor compiler, ~16 MB the app assembly with the embedded reference assemblies and
  the Razor compiler DLLs.
- **First Razor compile is slow** (~3.5–4.5 s vs ~1.1 s for C#) while the Razor generator warms up.
  Warm renders are tens of milliseconds.
- **Stale `obj/` silently keeps an old package.** A `PackageReference` version change does not always
  re-restore; the build then keeps publishing the previous assembly, which looks like "the change had
  no effect". Delete `obj/` when changing package versions and confirm the shipped
  `Microsoft.CodeAnalysis.CSharp.*.wasm` hash changed.
- **Publish into a clean folder.** Publishing into a populated output directory leaves the previous
  content-hashed `BlazorRunner.<hash>.wasm` behind — ~12 MB of dead weight each time, with only one
  referenced. `make-package.ps1` always publishes into a fresh staging directory and asserts a single
  assembly.
- **One component per render.** A single file (and a single generated class); there is no
  multi-file/multi-component project, and no `@page` routing.
- **Per-render leakage is negligible.** Each render loads a new assembly into the default load
  context, but a compiled component is only ~3 KB, so this is not worth an unloadable
  `AssemblyLoadContext` yet.

## Reusing it for C#

**Yes.** `RunCode(source, stdin)` is already the capability LiveCodes' `csharp-wasm` language uses —
its script calls `DotNet.invokeMethodAsync('MyRunnyApp', 'RunCode', code, input)` and reads
`{ output, errors }`, which this bundle mirrors. So `@live-codes/blazor-wasm` can back both the C#
language and Blazor from one copy of Roslyn, the .NET runtime and the BCL reference assemblies.

What that costs, so it is a deliberate choice:

- **Size.** One ~58 MB bundle instead of `csharp-wasm`'s ~40 MB. Two bundles would be ~40 + ~58 MB, so
  consolidation wins as soon as Blazor is used at all.
- **LiveCodes glue, when we get there.** The `csharp-wasm` script hard-codes the `MyRunnyApp` assembly
  name and its bundle URL is pinned in `vendors.ts`; both would point here. The result shape also
  differs slightly — this bundle returns `errors` as an array of diagnostics where the current code
  expects a pre-joined string — so `lang-csharp-wasm-script.ts` needs a few lines to join them.
  Nothing changes in the language spec, editor support or starter template.

## Next steps

- **Wire into LiveCodes** (deliberately not done yet): point the C# language and a new `blazor-wasm`
  language at this package, per [Reusing it for C#](#reusing-it-for-c).
- **Revisit Roslyn 5.9.** The pin to 4.14 is what makes Roslyn run under wasm; if 5.9 is fixed there,
  the embed-and-load dance could be replaced by a plain reference.
- **Multi-component files** for Razor — several `.razor` sources — just needs the generator to be fed
  more additional files (it already supports it).
- **Shrink the bundle** — the untrimmed runtime and the embedded reference assemblies dominate.
