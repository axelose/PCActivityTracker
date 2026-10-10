namespace PCActivityTracker.Domain;

public sealed class AppSettings
{
    public const int CurrentPrivacyConsentVersion = 1;
    public const string DefaultGameProcessNames =
        "VALORANT-Win64-Shipping,League of Legends,LeagueClient,EscapeFromTarkov,cs2,Overwatch,FortniteClient-Win64-Shipping,r5apex,RainbowSix,RocketLeague,eldenring,ffxiv_dx11,GTA5,RDR2";

    public int PrivacyConsentVersion { get; set; }
    public bool RecordingEnabled { get; set; }
    public bool RecordUrls { get; set; }
    public bool RecordPageTitles { get; set; }
    public bool RecordWindowTitles { get; set; }
    public bool RecordCreatorNames { get; set; }
    public int IdleThresholdMinutes { get; set; } = 5;
    public int RetentionDays { get; set; } = 90;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutomaticBackupsEnabled { get; set; } = true;
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModel { get; set; } = string.Empty;
    public bool UnloadOllamaAfterGeneration { get; set; } = true;
    public int OllamaTimeoutMinutes { get; set; } = 10;
    public bool GameSafeModeEnabled { get; set; } = true;
    public bool RequireIdleOllama { get; set; } = true;
    public string GameProcessNames { get; set; } = DefaultGameProcessNames;
    public string ThemeMode { get; set; } = "System";
    public string BrowserBridgeToken { get; set; } = string.Empty;
    public string BrowserBridgeAllowedOrigins { get; set; } = string.Empty;
}

public sealed class ExclusionRule
{
    public long Id { get; set; }
    public ExclusionType Type { get; set; }
    public string Value { get; set; } = string.Empty;
}

public enum ExclusionType
{
    Application,
    Domain
}

public sealed class CategoryRule
{
    public long Id { get; set; }
    public CategoryRuleTarget Target { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public CategoryMatchMode MatchMode { get; set; } = CategoryMatchMode.Wildcard;
    public string Color { get; set; } = "#64748B";
    public int Priority { get; set; } = 100;
}

public enum CategoryRuleTarget
{
    Application,
    Domain,
    Creator
}

public enum CategoryMatchMode
{
    Wildcard,
    Regex
}
