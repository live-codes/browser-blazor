# @live-codes/blazor-wasm

Compile and run **C# and Blazor in the browser, with no server**. Multi-file projects of Razor markup
and C# — `@page` routing, layouts, scoped CSS and `wwwroot/` assets — compile in the page with the real
Razor and Roslyn compilers and render on Blazor's own interactive renderer. The same bundle runs console
programs, so it can back a C# playground and a Blazor one with one copy of the runtime.

Nothing is sent anywhere: the compiler, the .NET runtime and the reference assemblies all run in the
page.

```html
<div id="blazor-app"></div>
<script src="https://cdn.jsdelivr.net/npm/@live-codes/blazor-wasm/blazor-wasm.js"></script>
<script>
  const runner = BlazorRunner.create();          // or create({ baseUrl: '/vendor/blazor-wasm/' })

  await runner.renderRazor('<h1>Hello</h1>');    // a single component — that is the whole setup
</script>
```

Or install it: `npm i @live-codes/blazor-wasm`.

Whatever a project renders goes into an element with id `blazor-app`, which the loader places for you —
inside the container named by `root`, or `document.body`. A page that wants to position it itself can
just write `<div id="blazor-app"></div>` where it belongs, and that element is used as it stands.

## `baseUrl`

`create({ baseUrl })` is the folder the bundle is served from — the `_framework` runtime and the
`refs.zip` / `razor.zip` payloads are fetched relative to it.

Omit it and the loader uses the folder its own `<script>` came from, falling back to the jsDelivr URL
above when that cannot be determined. Pass it explicitly when you serve the files yourself, or when the
script is injected in a way that leaves `document.currentScript` null (a module, some bundlers) — it
also keeps the payloads on the same version as the loader, which the unversioned fallback does not.

`create` also takes `onProgress(count)`, called as boot resources are fetched.

## Where it renders

Blazor has no convention for the mount point — a project names the selector in `Program.cs`, as
`builder.RootComponents.Add<App>("#app")`, and pairs it with a matching element. So here the loader
decides, and `root` says where the element it renders into should live:

```js
BlazorRunner.create({ root: '#app' });                            // a container, by selector
BlazorRunner.create({ root: document.querySelector('#app') });    // or the element itself
```

The loader puts a `<div id="blazor-app">` inside that container — creating it if it is not already
there — and Blazor attaches to it at startup. If the page contains an element with that id anywhere, it
is used as it stands, which is how a page that places it itself keeps working. Omitting `root` uses
`document.body`.

That element is resolved before `Blazor.start()`, because a root component is attached at startup and
cannot be moved afterwards, and `runner.rootElement()` returns it once the DOM is ready. A `root`
selector that matches nothing fails the boot with a clear error rather than guessing.

## Usage

### A project

```js
const result = await runner.renderProject(
  [
    { filename: 'App.razor', content: '<Router AppAssembly="typeof(App).Assembly">…</Router>' },
    { filename: 'Pages/Home.razor', content: '@page "/"\n<h1>Home</h1>\n<img src="logo.svg" />' },
    { filename: 'Pages/Counter.razor', content: '@page "/counter"\n<button @onclick="Go">@count</button>\n@code { int count; void Go() => count++; }' },
    { filename: 'Pages/Counter.razor.css', content: 'button { color: red; }' },
    { filename: 'wwwroot/logo.svg', content: '<svg …/>' },
    { filename: 'Greeting.cs', content: 'public static class Greeting { public static string For(string n) => "Hi " + n; }' },
  ],
  'App',        // optional: the component to render
  'MyApp',      // optional: the project's namespace
);

result.routes;  // ['/', '/counter']
```

Filenames may contain folders, and `.razor`, `.razor.css`, `.cs` and `wwwroot/` assets can be mixed
freely: everything compiles into one assembly, so components in different files can reference each
other.

### A single component

```js
await runner.renderRazor('<h1>Hello</h1>');                    // .razor markup
await runner.render('public class App : ComponentBase { … }'); // C#
```

### A console program

```js
const { output } = await runner.run(
  'using System;\nclass P { static void Main() => Console.WriteLine("hi"); }',
);
```

The second argument is the program's stdin, read through the ordinary `Console.ReadLine()` /
`Console.Read()` / `Console.In` — `null` at end of input, as usual, and a fresh reader per call, so one
run cannot consume another's input.

```js
const { output } = await runner.run(
  'using System;\nConsole.WriteLine("name: " + Console.ReadLine());',
  'Ada\n',
);
```

One quirk worth knowing before you touch it: `Console.In`'s **getter** throws
`PlatformNotSupportedException` on browser-wasm until something has been set. That is why the runner
calls `Console.SetIn` and then never reads or restores `In` — saving the previous value is exactly what
would throw. Reading `Console.In` after that is fine.

### Routing

```js
await runner.navigateTo('/counter');   // real client-side routing, via the host's NavigationManager
```

## What a project can contain

A project is compiled the way a local one is, so what builds here builds locally:

- **folders become namespaces** — `Pages/Home.razor` is `UserRazor.Pages.Home` and
  `Layout/MainLayout.razor` is `UserRazor.Layout.MainLayout` — so a component in another folder is
  reached with a `@using`, not implicitly;
- **`@page` declares routes**, reported back in `result.routes`;
- **layouts** work: `@layout`, `LayoutComponentBase` and `@Body` behave as usual;
- **`Name.razor.css` scopes `Name.razor`**, the way CSS isolation does locally;
- **`wwwroot/` files are served** as data URLs, with the rendered `src`/`href`/`poster` attributes
  pointed at them;
- **`@using`s come from the project's own `_Imports.razor`** — a template-style one is supplied only
  when the project has none. `UserRazor` is the root namespace unless you pass one.

A project can also be a single file: the shortest usable call is `runner.renderRazor('<h1>Hello</h1>')`,
no `_Imports.razor` needed.

## API

`BlazorRunner.create(options)` returns:

| Member | Description |
| --- | --- |
| `ready()` | resolves once the runtime is up; every call boots on demand, so this is optional |
| `rootElement()` | the element the app renders into, once the DOM is ready |
| `renderProject(files, rootComponent?, rootNamespace?)` | compiles and renders a project of `{ filename, content }` files |
| `renderRazor(source, componentName?)` | compiles and renders a single `.razor` file |
| `render(source, componentName?)` | compiles and renders a single C# file |
| `run(source, stdin?)` | compiles and runs a console program, capturing stdout |
| `navigateTo(url)` | drives the host's `NavigationManager` |
| `payloads()` | which compiler payloads have been fetched: `refs.zip`, plus `razor.zip` once markup has been compiled |
| `referenceCount()` | how many reference assemblies are loaded, fetching them if not yet |
| `baseUrl` | the resolved base URL |

Result shapes:

- `renderProject` / `renderRazor` / `render` → `{ success, type, bytes, routes[], styles, assets, errors[] }`
- `run` → `{ success, output, errors[] }`
- every diagnostic is `{ id, message, severity, line, column }`, with lines mapped back to the `.razor`
  source

A component that throws *while rendering* comes back as `success: false` with the exception in `errors`,
rather than taking the renderer down with it.

## Size

| | files | decoded | downloaded (brotli) |
| --- | --- | --- | --- |
| the C#-only bundle this replaces (`csharp-wasm@1.0.3`) | 84 | 35.5 MB | 13.8 MB |
| this bundle, running a console program | 208 | 39.2 MB | 16.4 MB |

Measured in a browser while running a console program. A console session never fetches `razor.zip` —
only a project with `.razor` markup does. That is ~19% more than the C#-only bundle, which buys the
untrimmed BCL + ASP.NET Core and the Blazor host: the same download renders components, so consolidating
costs less than shipping both. [FINDINGS.md](./FINDINGS.md#6-download-size-measured) has the method.

## Development

Requirements: **.NET SDK 10** with the **`wasm-tools`** workload, and Node.js for the static server. On
Windows the dotnet on `PATH` often lacks the workload while `%USERPROFILE%\.dotnet` has it:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" workload list    # must list wasm-tools

# One-time (re-run after upgrading the .NET SDK)
powershell -ExecutionPolicy Bypass -File scripts\prepare-refs.ps1

# Build the deployable package
powershell -ExecutionPolicy Bypass -File scripts\make-package.ps1 -Version 0.1.0

# Serve it, playground included
node serve.js package 8160                               # http://localhost:8160/
```

The playground edits a project: file tabs across the top (any mix of `.razor` and `.cs`, `+` and `×` to
add and remove), the live result on the right, and the project's routes as chips in its header. Pick a
mode, edit, and press **Run** / **Render** (or Ctrl/Cmd + Enter).

For a faster loop, publish and serve the output directly — but always into a **clean** directory:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish src\BlazorRunner -c Release -o src\BlazorRunner\dist
node serve.js src/BlazorRunner/dist/wwwroot 8160
```

`bundle-poc.html` is the step-1 record — it runs C# against the older bundle, and is kept because it
shows why that one could not host components. `node serve.js` serves it at
http://localhost:8130/bundle-poc.html.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/BlazorRunner` | The Blazor WebAssembly host — the app itself |
| `src/BlazorRunner/wwwroot/blazor-wasm.js` | The loader shipped in the package |
| `src/BlazorRunner/wwwroot/index.html` | The playground page |
| `src/BlazorRunner/refs/` | Reference assemblies, installed by `prepare-refs.ps1` |
| `package/` | The publishable package — this is what goes to npm |
| `scripts/prepare-refs.ps1` | Installs the reference assemblies into `refs/` |
| `scripts/make-package.ps1` | Publishes the app and assembles `package/` |
| `serve.js` | Static server with an SPA fallback, so a routed path such as `/counter` loads the app |
| `prototype/RazorProto` | Desktop spike for the Razor generator — seconds per iteration instead of a wasm publish |
| `bundle-poc.html` | Step-1 proof of concept, against the older C# bundle |
| `FINDINGS.md` | Measurements, internals, traps |

## Limitations

- **The first Razor compile is slow** (~4–5 s, while the Razor generator warms up; C# is ~0.2 s). Warm
  renders are tens of milliseconds.
- **One project per render** — there is no project-wide build step. `@page` route parameters
  (`/{id:int}`) transpile but are untested.
- **`wwwroot/` files have no real path.** They are data URLs, so `url(...)` inside CSS and `fetch()` do
  not resolve; only `src`/`href`/`poster` attributes are rewritten.
- **`Program.cs` is not editable**, so a project cannot vary DI or `HttpClient` — the host owns the
  entry point.

[FINDINGS.md](./FINDINGS.md) has the rest, including why `PublishTrimmed=false` is required and why
lazy-loading the framework was measured and rejected.

## License

MIT — see [LICENSE](./LICENSE).
