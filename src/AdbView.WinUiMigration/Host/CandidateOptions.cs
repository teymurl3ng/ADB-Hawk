namespace AdbView.WinUiMigration.Host;

// Parsed before any UI/instance/backend initialization. M04 is fixture-only.
internal sealed record CandidateOptions(string Fixture, string InstanceSuffix, string? ResultPath) {
    public const string IdentityPrefix = "AdbView.WinUiMigration.Fixture.";
    public string InstanceName => IdentityPrefix + InstanceSuffix;

    public static CandidateOptions Parse(string[] arguments) {
        string? fixture = null;
        string? suffix = null;
        string? result = null;
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
            else {
                throw new ArgumentException("Unknown or repeated candidate option.", nameof(arguments));
            }
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
