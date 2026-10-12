# Integration Tests

`Jiten.Tests/Integration/` contains API integration tests that hit the full HTTP pipeline against SQLite in-memory databases.

**Running:**
```bash
dotnet test Jiten.Tests --filter "FullyQualifiedName~Integration"
```

**Adding a new test class:**
```csharp
public class MyTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MyTest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/endpoint")
            .WithUser(TestUsers.UserA)              // authenticated as UserA
            .WithJsonContent(new { key = "value" }); // JSON body
        var response = await _client.SendAsync(request);
        // .WithAdmin() for admin-only endpoints
    }
}
```

**Infrastructure (`Integration/Infrastructure/`):**
- `JitenWebApplicationFactory` — replaces Postgres with SQLite, auth with header-based `TestAuthHandler`, CDN with `StubCdnService`, removes Hangfire/Redis/hosted services
- `TestUsers` — `UserA`, `UserB`, `Admin` (GUID constants)
- `HttpClientExtensions` — `.WithUser(id)`, `.WithAdmin()`, `.WithJsonContent(obj)`
- `StubCdnService` — records uploads/deletions, returns dummy URLs

**Provider-awareness:** `JitenDbContext` and `UserDbContext` guard Postgres-specific features (`text[]`, `jsonb`, computed columns, `IsDescending` indexes) behind `Database.ProviderName?.Contains("Npgsql")` checks. When adding new Postgres-specific model config, wrap it in the same `if (isNpgsql)` guard.

**Environment guard:** `Program.cs` skips Npgsql DbContext registration, role seeding, and Hangfire middleware when `ASPNETCORE_ENVIRONMENT=Testing`. If adding new startup-time infrastructure, guard it similarly with `builder.Environment.IsEnvironment("Testing")` or `app.Environment.IsEnvironment("Testing")`.
