using Microsoft.Data.SqlClient;
using SqlMigrator.Api;

namespace SqlMigrator.Api.Tests;

/// <summary>
/// No database required: these exercise <see cref="ConnectionStrings.For"/> purely as a string
/// builder, proving the key-injection attack (a Server/Database value that smuggles in a second
/// "Server="/"Database=" key, which ADO.NET would resolve to the last occurrence and so redirect
/// the connection — and the Windows/NTLM handshake with it — to a host the caller chose) is
/// closed, while every legitimate server/database form is still accepted.
/// </summary>
public class ConnectionStringsTests
{
    [Fact]
    public void Rejects_a_server_value_that_smuggles_a_second_Server_key()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ConnectionStrings.For("localhost;Server=evil.host", "SomeDb"));

        Assert.Equal("server", ex.ParamName);
        Assert.DoesNotContain("evil.host", ex.Message);
    }

    [Fact]
    public void Rejects_a_database_value_that_smuggles_a_second_Database_key()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ConnectionStrings.For("localhost", "SomeDb;Database=evil"));

        Assert.Equal("database", ex.ParamName);
        Assert.DoesNotContain("evil", ex.Message);
    }

    [Fact]
    public void Rejects_a_server_value_containing_an_equals_sign()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ConnectionStrings.For("localhost=evil", "SomeDb"));

        Assert.Equal("server", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_or_whitespace_server(string server)
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectionStrings.For(server, "SomeDb"));
        Assert.Equal("server", ex.ParamName);
    }

    [Fact]
    public void Rejects_an_unreasonably_long_server_value()
    {
        var server = new string('a', 500);

        var ex = Assert.Throws<ArgumentException>(() => ConnectionStrings.For(server, "SomeDb"));
        Assert.Equal("server", ex.ParamName);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData(@"HOST\INSTANCE")]
    [InlineData("host,1433")]
    [InlineData("tcp:host,1433")]
    [InlineData("192.168.1.10")]
    public void Accepts_legitimate_server_forms_and_never_lets_them_be_overridden(string server)
    {
        var connectionString = ConnectionStrings.For(server, "SomeDb");

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(server, builder.DataSource);
        Assert.Equal("SomeDb", builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void Builder_alone_treats_a_semicolon_in_DataSource_as_part_of_the_value_not_a_new_key()
    {
        // Defense in depth: even bypassing ConnectionStrings.For's own validation, assigning
        // through SqlConnectionStringBuilder (rather than concatenating) escapes the malicious
        // value instead of letting it inject a second "Server=" key.
        var builder = new SqlConnectionStringBuilder { DataSource = "localhost;Server=evil.host" };

        var reparsed = new SqlConnectionStringBuilder(builder.ConnectionString);
        Assert.Equal("localhost;Server=evil.host", reparsed.DataSource);
    }
}
