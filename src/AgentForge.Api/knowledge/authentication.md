# Authentication

Users authenticate with Microsoft Entra ID using the OAuth 2.0 authorization-code flow with PKCE. Access tokens are validated by the API; client secrets are never placed in browser code.

Password-reset links remain valid for exactly 30 minutes and are single-use. A successful password reset revokes the link immediately. Five invalid reset attempts trigger a 15-minute cooldown for that account.

Administrative endpoints require the `AgentForge.Admin` application role in addition to a valid access token.
