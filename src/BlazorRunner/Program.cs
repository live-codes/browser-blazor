using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The single, fixed root component. Everything the user compiles is rendered *inside*
// it, by DynamicHost, using the app's own interactive renderer.
builder.RootComponents.Add<DynamicHost>("#blazor-app");

// The reference assemblies and the Razor compiler are fetched on demand (see Payload), which
// creates its own HttpClient: this app does not register one, and the first interop call can
// arrive before this line runs anyway.
await builder.Build().RunAsync();
