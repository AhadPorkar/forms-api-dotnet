using Forms.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Forms.Api.IntegrationTests.ApiFactory))]

namespace Forms.Api.IntegrationTests;

/// <summary>
/// Hosts the real application against a real PostgreSQL 16 in a container, shared by all tests.
/// Tests isolate themselves by using their own form keys.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public TestClock Clock { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Building the host applies the migrations (Forms:ApplyMigrationsOnStartup).
        _ = Services;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public FormsDbContext CreateDbContext() => Services.CreateScope().ServiceProvider.GetRequiredService<FormsDbContext>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Forms", _postgres.GetConnectionString());
        builder.UseSetting("Forms:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Forms:DraftRetention", "30.00:00:00");
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }
}

/// <summary>A clock that tests can move forward.</summary>
public sealed class TestClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}