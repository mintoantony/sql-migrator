namespace SqlMigrator.Api;

/// <summary>
/// The only place a connection string is constructed. Windows authentication always;
/// no branch here can produce one containing a password.
/// </summary>
public static class ConnectionStrings
{
    public static string For(string server, string database) =>
        $"Server={server};Database={database};Integrated Security=True;TrustServerCertificate=True";
}
