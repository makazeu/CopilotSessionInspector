namespace CopilotSessionInspector.Services;

/// <summary>Resolves the locations of the current user's GitHub Copilot CLI data.</summary>
public sealed class CopilotPaths
{
    public string Root { get; }

    public CopilotPaths(IConfiguration config)
    {
        var configured = config["CopilotHome"];
        Root = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
    }

    public string SessionStoreDb => Path.Combine(Root, "session-store.db");
    public string LogsDir => Path.Combine(Root, "logs");
    public string SessionStateDir => Path.Combine(Root, "session-state");

    public bool Exists => Directory.Exists(Root);
}
