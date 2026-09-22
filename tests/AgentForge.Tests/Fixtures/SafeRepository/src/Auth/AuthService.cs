namespace SampleApp.Auth;

public sealed class AuthService
{
    public bool Authenticate(string userName, string password)
    {
        return !string.IsNullOrWhiteSpace(userName) && password.Length >= 12;
    }
}
