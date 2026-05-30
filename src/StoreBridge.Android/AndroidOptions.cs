namespace StoreBridge.Android;

/// <summary>
/// Common configuration for all Google Play Developer API interactions.
/// </summary>
public abstract class AndroidOptions
{
    /// <summary>
    /// Base64-encoded Google service account JSON credentials.
    /// Encode the full service account .json file: <c>Convert.ToBase64String(File.ReadAllBytes("service-account.json"))</c>
    /// </summary>
    public string CredentialsBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Your app's package name (e.g. "com.example.app").
    /// </summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of attempts (including the initial call) when the Google Play API returns
    /// a transient error (5xx, 429, network failure). Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}
