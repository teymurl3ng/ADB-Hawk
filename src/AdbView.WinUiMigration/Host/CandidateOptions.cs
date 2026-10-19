namespace AdbView.WinUiMigration.Host;

// Parsed before any UI/instance/backend initialization. Live and fixtures are mutually exclusive.
internal sealed record CandidateOptions(string Fixture, string InstanceSuffix, string? ResultPath, bool Live = false, bool StartupSilent = false) {
    public const string IdentityPrefix = "AdbView.WinUiMigration.Fixture.";
    public const string LiveIdentity = "AdbView.WinUiMigration.Live";
    public static readonly Guid LiveTrayIdentity = new("a4d9c6e2-7b3f-4e8a-9c21-6d0b5a4f8e77");
    public string InstanceName => Live ? LiveIdentity : IdentityPrefix + InstanceSuffix;

    public static CandidateOptions Parse(string[] arguments) {
        string? fixture = null;
        string? suffix = null;
        string? result = null;
        bool live = false;
        bool startup = false;
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
            else if (argument == "--live" && !live) { live = true; }
            else if (argument == "--startup" && !startup) { startup = true; }
            else {
                throw new ArgumentException("Unknown or repeated candidate option.", nameof(arguments));
            }
        }
        if (live || startup) {
            if (fixture is not null || suffix is not null || result is not null) {
                throw new ArgumentException("Live mode cannot use fixture identity or verification options.", nameof(arguments));
            }
            return new CandidateOptions(string.Empty, string.Empty, null, Live: true, StartupSilent: startup);
        }
        if (string.IsNullOrWhiteSpace(fixture) || !Guid.TryParseExact(suffix, "N", out _)) {
            throw new ArgumentException("Candidate requires an explicit fixture and a unique GUID instance suffix.", nameof(arguments));
        }
        if (result is not null && (!Path.IsPathFullyQualified(result) || string.IsNullOrWhiteSpace(result))) {
            throw new ArgumentException("Verification result path must be absolute.", nameof(arguments));
        }
        return new CandidateOptions(fixture, suffix!, result);
    }
}
