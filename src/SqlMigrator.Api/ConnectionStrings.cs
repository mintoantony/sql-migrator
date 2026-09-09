using Microsoft.Data.SqlClient;

namespace SqlMigrator.Api;

/// <summary>
/// The only place a connection string is constructed. Windows authentication always;
/// no branch here can produce one containing a password.
///
/// Built with <see cref="SqlConnectionStringBuilder"/> rather than string concatenation so a
/// caller-supplied value containing ';' or '=' becomes part of the escaped value instead of
/// injecting a new key (ADO.NET resolves duplicate keys to the last occurrence, which would
/// otherwise let a crafted Server value redirect the connection — and the Windows/NTLM auth
/// handshake with it — to a host of the caller's choosing). Both inputs are also validated and
/// rejected outright before they ever reach the builder: belt and braces, so a future edit that
/// reintroduces concatenation doesn't silently reopen the hole.
/// </summary>
public static class ConnectionStrings
{
    private const int MaxLength = 128;

    public static string For(string server, string database)
    {
        ValidateComponent(server, nameof(server));
        ValidateComponent(database, nameof(database));

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Rejects rather than sanitises: a Server or Database value with no ';' or '=' can't smuggle
    /// an extra connection-string key no matter how the string is assembled downstream. Legitimate
    /// forms (localhost, HOST\INSTANCE, host,1433, tcp:host,1433, an IP, or an ordinary SQL Server
    /// identifier) never contain either character, so nothing valid is rejected.
    /// </summary>
    private static void ValidateComponent(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{paramName}' must not be empty or whitespace.", paramName);

        if (value.Length > MaxLength)
            throw new ArgumentException(
                $"'{paramName}' exceeds the maximum allowed length of {MaxLength} characters.", paramName);

        if (value.Contains(';') || value.Contains('='))
            throw new ArgumentException(
                $"'{paramName}' contains characters that are not permitted (';' and '=').", paramName);
    }
}
