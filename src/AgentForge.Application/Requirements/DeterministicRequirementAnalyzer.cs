using AgentForge.Domain.Requirements;

namespace AgentForge.Application.Requirements;

public sealed class DeterministicRequirementAnalyzer : IRequirementAnalyzer
{
    public Task<RequirementAnalysis> AnalyzeAsync(
        string requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        cancellationToken.ThrowIfCancellationRequested();

        var analysis = new RequirementAnalysis(
            Summary: "Add a secure forgot-password flow that emails a time-limited reset link to the user.",
            FunctionalRequirements:
            [
                "Accept a password-reset request for a registered account.",
                "Generate and persist a cryptographically secure, single-use reset token.",
                "Send an email containing a password-reset link.",
                "Validate the token and allow the user to set a new password.",
                "Invalidate the token after successful use or expiration."
            ],
            NonFunctionalRequirements:
            [
                "Return a consistent response regardless of whether the account exists.",
                "Rate-limit reset requests by account and request source.",
                "Record security-relevant events without logging tokens or passwords."
            ],
            TechnicalConsiderations:
            [
                "Define token lifetime and secure token storage or hashing strategy.",
                "Integrate a transactional email provider with retry and delivery monitoring.",
                "Use an HTTPS reset URL and invalidate existing sessions when policy requires it."
            ],
            SecurityRisks:
            [
                "User enumeration through response content or timing differences.",
                "Token theft, reuse, prediction, or excessive lifetime.",
                "Request flooding and email abuse.",
                "Sensitive data exposure through logs, analytics, or URL referrers."
            ],
            Questions:
            [
                "What is the required reset-token lifetime?",
                "Which email provider and branded template should be used?",
                "Should a successful password reset revoke all active sessions?",
                "What password policy and rate limits apply?"
            ],
            Complexity: RequirementComplexity.Medium);

        return Task.FromResult(analysis);
    }
}
