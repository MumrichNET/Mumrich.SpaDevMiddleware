# Copilot Instructions for Mumrich.SpaDevMiddleware

## What This Project Does

ASP.NET Core middleware that integrates SPAs into a .NET web host. It automatically installs Node dependencies, builds SPAs on `dotnet publish`, and reverse-proxies SPA dev servers via YARP during development. Process lifecycle is managed by **Akka.NET FSM actors**.

## Build & Run

```powershell
# Build (Windows) — delegates to Cake.Frosting in build/Build.csproj
./build.ps1

# Build and publish to NuGet
./build.ps1 -t nuget-publish --NugetOrgApiKey <key>

# Run the demo
dotnet run --project Mumrich.SpaDevMiddleware.Demo.WebHost
```

The build script wraps `dotnet run --project build/Build.csproj -- $args`. The CI pipeline runs `./build.sh -t nuget-publish`.

There are no automated test projects. Validation is done by running the demo.

## Project Layout

| Project | Role |
|---|---|
| `Mumrich.SpaDevMiddleware` | Main middleware library (NuGet package) |
| `Mumrich.SpaDevMiddleware.Domain` | Contracts, models, enums — no ASP.NET dependencies |
| `Mumrich.SpaDevMiddleware.MsBuild` | MSBuild tasks for SPA install/build integration |
| `Mumrich.DDD` | Reusable Akka.NET / DDD base classes (aggregates, events, commands) |
| `Mumrich.SpaDevMiddleware.Demo.WebHost` | Working demo with Vue.js + VitePlus |
| `build/` | Cake.Frosting automation scripts |

Solution file: `Mumrich.SpaDevMiddleware.slnx` (modern `.slnx` format, not `.sln`).

## Architecture

1. **Registration** — Consumer calls `builder.SetupSpaMiddleware(appSettings)`, which registers YARP and starts an Akka `ActorSystem` via Akka.Hosting.
2. **Route mapping** — Consumer calls `app.MapSinglePageApps(appSettings)`, which maps each SPA either to the YARP reverse proxy (development) or to static files (production).
3. **Process management** — `SpaDevelopmentService` (IHostedService) starts `SpaDevServerActor` instances — one per SPA. Each actor is an Akka FSM that spawns and monitors the Node.js dev server process.
4. **MSBuild integration** — Consumer imports `Mumrich.SpaDevMiddleware.targets` in their `.csproj`, which hooks `npm install` (BeforeTargets="Build") and `npm run build` (AfterTargets="ComputeFilesToPublish").

## Key Conventions

**C# style:**
- File-scoped namespaces (`namespace Foo.Bar;`)
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Implicit usings **disabled** — all `using` directives must be explicit

**Domain / DDD:**
- Domain models and contracts live in `Mumrich.SpaDevMiddleware.Domain` with no ASP.NET Core references
- `ISpaMiddlewareSettings` is the single integration contract consumers must implement
- `SpaSettings` holds all per-SPA configuration; new settings belong here

**Actors (Akka.NET):**
- Actors extend base classes from `Mumrich.DDD` (e.g., `AggregateBase<TState, TAggregate, TModel, TCommand>`)
- `SpaDevServerActor` is the canonical example of the FSM pattern used in this repo
- Actor messages follow the naming convention: `*Command` (write), `*Query` (read), `*Event` (notification)

**YARP proxy models:**
- `SpaProxyConfig`, `Route`, `Cluster`, `Destination` in the Domain project are custom wrappers around YARP config — do not use YARP types directly in `SpaSettings`

**Versioning:**
- GitVersion in `ContinuousDeployment` mode; `main` branch increments patch
- Tag format: `v1.2.3` or `1.2.3`

## Consuming the Middleware (How It's Used)

```csharp
// 1. Implement ISpaMiddlewareSettings
var appSettings = new DefaultAppSettings
{
    SinglePageApps = new Dictionary<string, SpaSettings>
    {
        ["/"] = new SpaSettings
        {
            DevServerAddress = "http://localhost:5173/",
            SpaRootPath = "Apps/vue-app",
            NodePackageManager = NodePackageManager.VitePlus,
        }
    }
};

// 2. Register services
builder.SetupSpaMiddleware(appSettings);

// 3. Map routes
app.MapSinglePageApps(appSettings);
```

```xml
<!-- 4. In .csproj, declare SPA roots and import targets -->
<ItemGroup>
  <SpaRoot Include="Apps\vue-app\">
    <InstallCommand>vp install</InstallCommand>
    <BuildCommand>vp run build</BuildCommand>
    <BuildOutputPath>Apps\vue-app\dist\**</BuildOutputPath>
  </SpaRoot>
</ItemGroup>
<Import Project="../Mumrich.SpaDevMiddleware/MSBuild/Mumrich.SpaDevMiddleware.targets" />
```

## Vue Demo App (Apps/vue-app)

Uses **VitePlus** (`vp` CLI). See `Apps/vue-app/.github/copilot-instructions.md` for full VitePlus-specific instructions. Key commands:

```bash
vp install       # install dependencies
vp dev           # start dev server
vp run build     # production build
vp check         # format + lint + type-check
vp test          # run tests
```

Do **not** invoke `pnpm`/`npm`/`yarn` directly in this app — use `vp` instead.
