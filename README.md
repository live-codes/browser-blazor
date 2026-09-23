# browser-blazor

Run **C# and render live Blazor components in the browser with no server** — proof of concept.

Two steps, in order:

1. **`bundle-poc.html`** — runs C# in the page using the existing
   [`@seth0x41/csharp-wasm`](https://www.npmjs.com/package/@seth0x41/csharp-wasm) bundle. It works,
   but probing it showed the bundle **cannot render Blazor components** (see
   [Findings](#step-1-findings)).
2. **`src/BlazorRunner`** — our own Blazor WebAssembly host that compiles a user-authored
   component with Roslyn **in the page** and renders it on Blazor's real, interactive renderer.

## Quick start

### Step 1 — bundle PoC

```sh
node serve.js                       # http://localhost:8130/bundle-poc.html
```

### Step 2 — component playground (host app)

```powershell
# One-time (re-run after upgrading the .NET SDK)
powershell -ExecutionPolicy Bypass -File scripts\prepare-refs.ps1

# Publish the host app (use the SDK that has wasm-tools — see "Prerequisites")
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish src\BlazorRunner -c Release -o src\BlazorRunner\dist

# Serve it
node serve.js src/BlazorRunner/dist/wwwroot 8140
# open http://localhost:8140/
```

The playground has a C# editor on the left and the live component on the right. Edit the code,
press **Render** (or Ctrl/Cmd + Enter), and the component is recompiled and re-rendered.

## Prerequisites

- **.NET SDK 10** with the **`wasm-tools` workload**. On a typical Windows box there are two
  installs and they are not equivalent — the one on `PATH` (`%ProgramFiles%\dotnet`) often lacks
  the workload, while `%USERPROFILE%\.dotnet` has it. Use the user-local one:

  ```powershell
  & "$env:USERPROFILE\.dotnet\dotnet.exe" workload list   # must list wasm-tools
  ```

  Both `prepare-refs.ps1` and the commands above default to `%USERPROFILE%\.dotnet`.

- Node.js (only for the static file server).

## Step 1 — findings

Measured against `@seth0x41/csharp-wasm@1.0.3` (from `_framework/blazor.boot.json` and the string
table of `MyRunnyApp.*.wasm`):

- **It compiles and runs C# entirely in the browser.** User code is compiled to `DynamicAssembly`
  and loaded with `AssemblyLoadContext`; `Console` I/O is captured. Cold run ~2 s, warm ~25 ms.
- **The Roslyn reference set excludes Blazor.** Compiling `using Microsoft.AspNetCore.Components;`
  fails with *"The type or namespace name 'AspNetCore' does not exist in the namespace 'Microsoft'"*,
  so user code cannot reference Blazor types and cannot author a component.
- **Blazor's rendering types are trimmed.** At runtime `ComponentBase` resolves, but
  `Microsoft.AspNetCore.Components.Web.HtmlRenderer` and `RenderTreeBuilder` are **not found**.
- **The app has no main root component** (its only selector literal is `head::after`) and the npm
  package ships no `index.html`, so its own UI cannot be booted standalone.
- **Dynamic root components are disabled** — `Blazor.rootComponents.add(...)` reports *"Dynamic root
  components have not been enabled in this application."*

The bundle is a good Roslyn-in-the-browser engine, but not a component host. Hence step 2.

## Step 2 — how the host works

`src/BlazorRunner` is a `Microsoft.NET.Sdk.BlazorWebAssembly` app published as static files
(no server logic).

| File | Role |
| ---- | ---- |
| `Program.cs` | Builds the host; mounts the single root component at `#blazor-app`; loads the embedded reference assemblies. |
| `DynamicHost.cs` | The fixed root component. Renders whatever component was last compiled, using `RenderTreeBuilder.OpenComponent(int, Type)`. |
| `ComponentCompiler.cs` | Roslyn: parse → `CSharpCompilation.Create` → `Emit` → `Assembly.Load` → find the `IComponent` type. |
| `BlazorBridge.cs` | The interop surface: `[JSInvokable] RenderComponent(source)`, returning `{ success, type, errors[] }`. |
| `wwwroot/index.html` | The playground page (editor + render stage). |
| `../../scripts/prepare-refs.ps1` | Copies BCL + ASP.NET Core reference assemblies into `refs/`, embedded as resources. |

Key points:

- **The user's component runs on the app's own renderer.** `DynamicHost` keeps the compiled `Type`
  and opens it as a child component, so the user's component participates in the real Blazor render
  tree — event handlers (`onclick`), `StateHasChanged`, parameters and lifecycle all work.
- **Reference assemblies are embedded**, not fetched: `scripts/prepare-refs.ps1` copies 307 DLLs
  (11.7 MB) from `Microsoft.NETCore.App.Ref` and `Microsoft.AspNetCore.App.Ref` into
  `src/BlazorRunner/refs/`, and the csproj embeds them as `lib.*` resources. The BCL is copied first
  so it wins any name overlap with the ASP.NET Core pack.
- **`PublishTrimmed=false`.** The user's compiled assembly resolves against the full BCL and ASP.NET
  Core at runtime, so nothing may be linked away.
- **A fresh assembly identity per compile** (`UserComponent_<guid>`) — loading two assemblies with
  the same name into the default load context would clash.
- **`WithConcurrentBuild(false)`** — Roslyn's parallel binding schedules on the thread pool, and the
  WebAssembly runtime is single-threaded.

### Verified

Checked in a real browser (headless Chrome via CDP):

- a `Counter` component compiles and renders (`Count: 0` + a button); clicking it three times gives
  `Count: 3` — i.e. the compiled component is genuinely interactive;
- re-rendering swaps in a different component (`Greeting`) in ~66 ms (warm);
- a bad program reports diagnostics: `CS0246: The type or namespace name 'NonexistentType' could not
  be found (line 2)`.

### Size and limitations

- **Bundle size.** The publish is ~87 MB (~78 MB of `_framework`), including the untrimmed BCL +
  ASP.NET Core runtime, Roslyn, and the embedded reference assemblies. Comparable to
  `csharp-wasm`/`vb-wasm`; a CDN should drop the `.br`/`.gz` siblings (it compresses itself).
- **C# only, no `.razor`.** Components are written as C# classes (`BuildRenderTree`). Supporting
  `.razor` markup needs the Razor compiler (`Microsoft.AspNetCore.Razor.Language`) in the bundle.
- **Assemblies accumulate.** Each render loads a new assembly into the default load context; a long
  session grows memory. A collectible `AssemblyLoadContext` would fix this.
- **One component per render** — the first `IComponent` type in the compiled source is rendered.

## Next steps

- **Wire into LiveCodes** the way `vb-wasm` is: publish this bundle as `@live-codes/blazor-wasm`,
  pin it in `src/livecodes/vendors.ts`, and add a language spec + starter template under
  `src/livecodes/languages/blazor-wasm/` (the host page's editor becomes LiveCodes' result frame).
- **Add `.razor` support** by embedding the Razor compiler and compiling markup to C# before Roslyn.
- **Shrink the bundle** — the trimming restrictions and duplicated ref assemblies are the main cost.
