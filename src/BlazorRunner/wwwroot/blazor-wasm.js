/*!
 * blazor-wasm.js — load the @live-codes/blazor-wasm bundle and drive it from any page.
 *
 * The bundle loads the .NET WebAssembly runtime, Roslyn, and the Razor compiler into the page,
 * then compiles what you give it and renders it on Blazor's real renderer. Nothing is sent
 * anywhere; there is no server.
 *
 *   <script src="https://cdn.jsdelivr.net/npm/@live-codes/blazor-wasm/blazor-wasm.js"></script>
 *   <script>
 *     const runner = BlazorRunner.create();            // defaults to the script's own folder
 *
 *     // A project: any mix of .razor markup and C#, compiled together, so components can
 *     // reference each other and @page components register routes. Filenames may contain folders;
 *     // a Name.razor.css scopes Name.razor, and files under wwwroot/ are served as data URLs.
 *     const a = await runner.renderProject([
 *       { filename: 'App.razor', content: '<Router AppAssembly="typeof(App).Assembly">…</Router>' },
 *       { filename: 'Pages/Counter.razor', content: '@page "/counter"\n<button @onclick="Go">@count</button>\n@code { int count; void Go() => count++; }' },
 *       { filename: 'Pages/Counter.razor.css', content: 'button { color: red; }' },
 *     ], 'App', 'MyApp');   // optional: root component, root namespace
 *
 *     // or a single component:
 *     const b = await runner.renderRazor('<h1>Hello</h1>');
 *
 *     // or a console program:
 *     const c = await runner.run('using System; class P { static void Main() { Console.WriteLine("hi"); } }');
 *   </script>
 *
 * Every call returns a plain object:
 *   renderProject / render / renderRazor -> { success, type, bytes, routes[], errors[] }
 *   run                                  -> { success, output, errors[] }
 * and each diagnostic is { id, message, severity, line, column }.
 *
 * With @page components, renderProject's `routes` lists them; navigate with
 * `runner.navigateTo('/counter')`.
 *
 * Whatever a project renders into is the element with id `blazor-app`, so give the page one:
 *   <div id="blazor-app"></div>
 */
(function (global) {
    'use strict';

    var DEFAULT_BASE_URL = 'https://cdn.jsdelivr.net/npm/@live-codes/blazor-wasm/';

    // Captured while this script evaluates, so a plain <script src=".../blazor-wasm.js"> finds the
    // bundle with no configuration.
    var scriptBaseUrl = null;
    if (typeof document !== 'undefined' && document.currentScript && document.currentScript.src) {
        scriptBaseUrl = document.currentScript.src.replace(/[^/]*$/, '');
    }

    function toBaseUrl(url) {
        var href = typeof document !== 'undefined' ? document.baseURI : null;
        if (href) {
            try {
                url = new URL(url, href).href;
            } catch (err) {
                // Not resolvable (a custom scheme); use it verbatim.
            }
        }
        return String(url).replace(/\/+$/, '') + '/';
    }

    function parse(json) {
        return typeof json === 'string' ? JSON.parse(json) : json;
    }

    function create(options) {
        var opts = options || {};
        var baseUrl = toBaseUrl(opts.baseUrl || global.BlazorWasmBaseUrl || scriptBaseUrl || DEFAULT_BASE_URL);
        var resourceCount = 0;
        var bootPromise = null;

        // CDNs reject credentialed requests, and Blazor's fetches must not carry any.
        var originalFetch = global.fetch;
        global.fetch = function (resource, init) {
            var url = typeof resource === 'string' ? resource : 'url' in resource ? resource.url : resource.href;
            if (url.indexOf(baseUrl) === 0) {
                return originalFetch(resource, Object.assign({}, init, { credentials: 'omit' }));
            }
            return originalFetch(resource, init);
        };

        function loadScript() {
            return new Promise(function (resolve, reject) {
                var script = document.createElement('script');
                script.src = baseUrl + '_framework/blazor.webassembly.js';
                script.setAttribute('autostart', 'false');
                script.onload = function () {
                    resolve();
                };
                script.onerror = function () {
                    reject(new Error('Could not load ' + script.src));
                };
                document.head.appendChild(script);
            });
        }

        function boot() {
            if (bootPromise) return bootPromise;
            bootPromise = loadScript()
                .then(function () {
                    return global.Blazor.start({
                        loadBootResource: function (_type, name) {
                            resourceCount++;
                            if (opts.onProgress) opts.onProgress(resourceCount);
                            return baseUrl + '_framework/' + name;
                        },
                    });
                })
                .catch(function (err) {
                    bootPromise = null;
                    throw err;
                });
            return bootPromise;
        }

        // The app assembly is loaded just after Blazor.start() resolves, so the first interop
        // call can race it. Retry until it is there.
        function invoke(method, args) {
            var attempt = 0;
            function tryInvoke() {
                return global.DotNet
                    .invokeMethodAsync.apply(null, ['BlazorRunner', method].concat(args))
                    .catch(function (err) {
                        var message = String((err && err.message) || err);
                        if (message.indexOf('no loaded assembly') >= 0 && attempt++ < 240) {
                            return new Promise(function (resolve) {
                                setTimeout(resolve, 250);
                            }).then(tryInvoke);
                        }
                        throw err;
                    });
            }
            return tryInvoke();
        }

        function call(method, args) {
            return boot()
                .then(function () {
                    return invoke(method, args);
                })
                .then(function (json) {
                    var result = parse(json);
                    resolveAssets(result);
                    return result;
                });
        }

        // A project's wwwroot/ files arrive as data URLs, so point the rendered markup at them. A
        // MutationObserver keeps that true as the DOM changes: navigating between @page components
        // rebuilds the markup, so a one-off pass would only fix the first render.
        var assetMap = {};
        var assetObserver = null;

        function resolveAssets(result) {
            if (!result || !result.assets) return;

            assetMap = result.assets;

            var root = document.getElementById('blazor-app');
            if (!root) return;

            applyAssets(root, assetMap);
            observeAssets(root);
        }

        function observeAssets(root) {
            if (assetObserver || typeof MutationObserver === 'undefined') return;

            assetObserver = new MutationObserver(function (records) {
                for (var i = 0; i < records.length; i++) {
                    if (records[i].type === 'attributes') {
                        rewriteAsset(records[i].target, assetMap);
                        continue;
                    }

                    var added = records[i].addedNodes;
                    for (var j = 0; j < added.length; j++) {
                        applyAssets(added[j], assetMap);
                    }
                }
            });

            assetObserver.observe(root, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['src', 'href', 'poster'],
            });
        }

        function applyAssets(node, map) {
            if (!node || node.nodeType !== 1) return;

            rewriteAsset(node, map);

            var descendants = node.querySelectorAll('[src], [href], [poster]');
            for (var i = 0; i < descendants.length; i++) {
                rewriteAsset(descendants[i], map);
            }
        }

        function rewriteAsset(element, map) {
            var attributes = ['src', 'href', 'poster'];

            for (var i = 0; i < attributes.length; i++) {
                var attribute = attributes[i];
                var value = element.getAttribute(attribute);
                if (!value) continue;

                var key = value.replace(/^\.\//, '').replace(/^\//, '');
                var resolved = map[key];

                // Setting it only when it actually changes keeps the observer from looping on itself.
                if (resolved && resolved !== value) element.setAttribute(attribute, resolved);
            }
        }

        return {
            baseUrl: baseUrl,
            /** Resolves once the runtime is up. Optional: every call boots on demand. */
            ready: function () {
                return boot();
            },
            /** Compiles and renders a project: a JSON-able array of { filename, content }.
             *  Files under wwwroot/ are served as data URLs (see resolveAssets). */
            renderProject: function (files, rootComponent, rootNamespace) {
                return call('RenderProject', [
                    JSON.stringify(files || []),
                    rootComponent || '',
                    rootNamespace || '',
                ]);
            },
            /** Compiles and renders a component written as .razor markup. */
            renderRazor: function (source, componentName) {
                return call('RenderRazor', [source || '', componentName || '']);
            },
            /** Compiles and renders a component written as C#. */
            render: function (source, componentName) {
                return call('RenderComponent', [source || '', componentName || '']);
            },
            /** Drives the host's navigation, for @page routing. */
            navigateTo: function (url) {
                return call('NavigateTo', [url || '/']);
            },
            /** Compiles and runs a console program, capturing stdout. */
            run: function (source, stdin) {
                return call('RunCode', [source || '', stdin || '']);
            },
            /** Number of embedded reference assemblies (a quick readiness check). */
            referenceCount: function () {
                return call('ReferenceCount', []);
            },
        };
    }

    var api = { create: create };
    global.BlazorRunner = global.BlazorRunner || api;
})(typeof globalThis !== 'undefined' ? globalThis : self);
