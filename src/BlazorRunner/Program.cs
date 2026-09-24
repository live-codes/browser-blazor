using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// The single, fixed root component. Everything the user compiles is rendered *inside*
// it, by DynamicHost, using the app's own interactive renderer.
builder.RootComponents.Add<DynamicHost>("#blazor-app");

// Reference assemblies (BCL + ASP.NET Core) are embedded in this assembly's manifest
// resources; Roslyn needs them to compile the user's component or program.
ReferenceAssemblies.LoadFromAssemblyResources(typeof(ReferenceAssemblies).Assembly);

await builder.Build().RunAsync();
