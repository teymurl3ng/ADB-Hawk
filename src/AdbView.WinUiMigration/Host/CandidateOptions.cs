namespace AdbView.WinUiMigration.Host;

// Parsed before any UI/instance/backend initialization. Fixtures stay the default (M04+); the
// explicit --live mode (M08 live-composition item) composes the real backend behind an isolated
// live identity and never touches the production instance or preferences.
internal sealed record CandidateOptions(string Fixture, string InstanceSuffix, string? ResultPath, bool Live) {
    public const string IdentityPrefix = "AdbView.WinUiMigration.Fixture.";
    public const string LiveIdentity = "AdbView.WinUiMigration.Live";

    /// <summary>Stable tray-icon identity for the live candidate: constant across launches so
    /// Explorer can re-associate the icon, yet distinct from every production/fixture identity.</summary>
    public static readonly Guid LiveTrayIdentity = Guid.Parse("a4d9c6e2-7b3f-4e8a-9c21-6d0b5a4f8e77");

    public string InstanceName => Live ? LiveIdentity : IdentityPrefix + InstanceSuffix;

    public static CandidateOptions Parse(string[] arguments) {
        string? fixture = null;
        string? suffix = null;
        string? result = null;
        bool live = false;
        foreach (string argument in arguments) {
            if (argument.StartsWith("--fixture=", StringComparison.Ordinal) && fixture is null) {
                fixture = argument["--fixture=".Length..];
            }
            else if (argument.StartsWith("--instance=", StringComparison.Ordinal) && suffix is null) {
                suffix = argument["--instance=".Length..];
            }
            else if (argument.StartsWith("--verify=", StringComparison.Ordinal) && result is null) {
                result = argument["--verify=".Length..];
            }
            else if (argument == "--live" && !live) {
                live = true;
            }
            else {
                throw new ArgumentException("Unknown or repeated candidate option.", nameof(arguments));
            }
        }
        if (live) {
            if (fixture is not null) {
                throw new ArgumentException("Live mode composes the real backend; a fixture snapshot cannot be combined with it.", nameof(arguments));
            }
            if (suffix is not null) {
                throw new ArgumentException("Live mode uses its fixed identity; a per-launch instance suffix cannot be combined with it.", nameof(arguments));
            }
        }
        else if (string.IsNullOrWhiteSpace(fixture) || !Guid.TryParseExact(suffix, "N", out _)) {
            throw new ArgumentException("Candidate requires an explicit fixture and a unique GUID instance suffix.", nameof(arguments));
        }
        if (result is not null && (!Path.IsPathFullyQualified(result) || string.IsNullOrWhiteSpace(result))) {
            throw new ArgumentException("Verification result path must be absolute.", nameof(arguments));
        }
        return new CandidateOptions(fixture ?? string.Empty, suffix ?? string.Empty, result, live);
    }
}
