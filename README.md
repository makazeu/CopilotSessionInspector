# Copilot Session Inspector

Copilot Session Inspector is a .NET 10 ASP.NET Core Blazor Web App for browsing and analyzing local GitHub Copilot CLI sessions.

The app reads session data from the current user's local `.copilot` folder and shows reconstructed conversations, tool calls, token usage, AIU/cost data, charts, and usage-reduction suggestions.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A GitHub Copilot CLI session store under `%USERPROFILE%\.copilot`

## Build

From the repository root:

```powershell
dotnet restore .\CopilotSessionInspector\CopilotSessionInspector.csproj
dotnet build .\CopilotSessionInspector\CopilotSessionInspector.csproj
```

## Run

From the repository root:

```powershell
dotnet run --project .\CopilotSessionInspector\CopilotSessionInspector.csproj
```

Open the URL printed by `dotnet run`, then go to **Sessions**.

The development launch settings use:

- HTTP: `http://localhost:5067`
- HTTPS: `https://localhost:7268`

To force a specific launch profile:

```powershell
dotnet run --project .\CopilotSessionInspector\CopilotSessionInspector.csproj --launch-profile https
```

## Configure the Copilot data folder

By default, the app reads from `%USERPROFILE%\.copilot`.

To inspect a different Copilot CLI data folder, set `CopilotHome` before running:

```powershell
$env:CopilotHome = "D:\some\.copilot"
dotnet run --project .\CopilotSessionInspector\CopilotSessionInspector.csproj
```

## Publish

To create a release build:

```powershell
dotnet publish .\CopilotSessionInspector\CopilotSessionInspector.csproj -c Release -o .\artifacts\publish
```

Run the published app:

```powershell
.\artifacts\publish\CopilotSessionInspector.exe
```

## Project layout

- `CopilotSessionInspector\Components` - Blazor UI components and pages.
- `CopilotSessionInspector\Services` - session store, telemetry parsing, and analysis services.
- `CopilotSessionInspector\Models` - application models.
- `CopilotSessionInspector\wwwroot` - static assets and JavaScript interop.
