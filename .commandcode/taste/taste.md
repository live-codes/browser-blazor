# Taste

## Workflow
- Prefers starting with a small proof-of-concept before committing to a full implementation; asks to "start with" a minimal, self-contained artifact first. Confidence: 0.75
- Prefers browser-only / serverless solutions: code should run entirely client-side with no server logic. Confidence: 0.7
- Prefers reusing existing packages/CDN bundles where they fit before building a custom equivalent, but will approve building a custom host when the existing one cannot meet the goal. Confidence: 0.5

## Tooling & Environment
- On Windows, prefers the user-local .NET SDK (`%USERPROFILE%\.dotnet\dotnet.exe`) over the one on `PATH`, because the PATH install lacks the needed `wasm-tools` workload. Confidence: 0.65
