# Taste
- Prefers starting with a small proof-of-concept before committing to a full implementation; asks to "start with" a minimal, self-contained artifact first. Confidence: 0.75
- Prefers browser-only / serverless solutions: code should run entirely client-side with no server logic. Confidence: 0.7
- Prefers reusing existing packages/CDN bundles where they fit before building a custom equivalent, but will approve building a custom host when the existing one cannot meet the goal. Confidence: 0.5
- On Windows, prefers the user-local .NET SDK (`%USERPROFILE%\.dotnet\dotnet.exe`) over the one on `PATH`, because the PATH install lacks the needed `wasm-tools` workload. Confidence: 0.65
- Prefers consolidating shared dependencies into a single bundle/package rather than shipping duplicates; asks whether a new artifact can also replace an existing implementation to reuse its dependencies (Roslyn, runtime, reference assemblies). Confidence: 0.6
- Expects the agent to keep iterating autonomously until the feature actually works and the artifact is packaged/ready for use, rather than stopping at a blocker or a documented "deferred" finding; "keep iterating till X works". Confidence: 0.65
- Prefers finishing a feature in its own standalone project before wiring it into the consumer/main repo (e.g. "Do not change LiveCodes for now — let's complete the Blazor support first"); integration is a separate, later step. Confidence: 0.75
- Wants build artifacts (bin/obj/package/refs) committed to git rather than gitignored; edits `.gitignore` themselves to stop ignoring them and expects the agent to include artifacts in commits. Confidence: 0.65
