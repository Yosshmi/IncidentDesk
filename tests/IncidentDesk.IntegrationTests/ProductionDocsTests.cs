using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace IncidentDesk.IntegrationTests;

public sealed class ProductionDocsTests
{
    [Fact]
    public async Task Production_documentation_is_hidden_by_default()
    {
        await using var factory = CreateFactory(false, false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicitly_enabled_production_docs_are_interactive_and_disclose_demo_accounts_only_in_demo_mode(bool demoEnabled)
    {
        await using var factory = CreateFactory(true, demoEnabled);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var root = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/docs", root.Headers.Location?.ToString());
        var docsRedirect = await client.GetAsync("/docs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, docsRedirect.StatusCode);
        Assert.NotNull(docsRedirect.Headers.Location);
        var docs = await client.GetAsync(docsRedirect.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
        Assert.Equal("text/html", docs.Content.Headers.ContentType?.MediaType);
        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(schema.RootElement.GetProperty("paths").TryGetProperty("/api/v1/auth/login", out _));
        Assert.True(schema.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
        var description = schema.RootElement.GetProperty("info").GetProperty("description").GetString()!;
        Assert.Equal(demoEnabled, description.Contains("reporter1@example.test", StringComparison.Ordinal));
        Assert.Equal(demoEnabled, description.Contains("engineer1@example.test", StringComparison.Ordinal));
        Assert.Equal(demoEnabled, description.Contains("IncidentDesk1!", StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Program> CreateFactory(bool docsEnabled, bool demoEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IncidentDesk"] = "Host=localhost;Database=unused_docs_test;Username=unused",
                    ["Docs:Enabled"] = docsEnabled.ToString(),
                    ["Demo:Enabled"] = demoEnabled.ToString(),
                    ["OpenAI:Enabled"] = "false",
                    ["Logging:LogLevel:Default"] = "Warning"
                }));
        });
}
