namespace FinDLNA.Models;

// MARK: LoginRequest
public class LoginRequest
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; } = null;
}

// MARK: AuthResult
public class AuthResult
{
    public bool Success { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

// MARK: JellyfinConfig
public class JellyfinConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}