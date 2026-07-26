using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Andy.Cli.Commands;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// The `andy-cli auth ...` verb and the TUI `/auth` command share this implementation, so these
/// tests cover both front ends' argument handling and output.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class AuthCommandTests
{
    private static AuthCommand CreateCommand(ICredentialStore store)
        => new(new AuthService(
            store,
            new ProviderCredentialResolver(store, catalogOverlayPath: AuthTestValues.NoOverlay),
            handlerFactory: null,
            clock: null,
            catalogOverlayPath: AuthTestValues.NoOverlay));

    [Fact]
    public async Task NoArguments_ListsProviders()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(Array.Empty<string>(), ScriptedAuthPrompt.Cancelling());

        Assert.True(result.Success);
        Assert.Contains("Provider authentication", result.Message);
        Assert.Contains("openai", result.Message);
    }

    [Fact]
    public async Task Login_StoresACredentialAndStatusThenReportsItRedacted()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        var command = CreateCommand(store);

        var login = await command.ExecuteAsync(
            new[] { "login", "openai" }, ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey, "team-a"));
        Assert.True(login.Success);

        var status = await command.ExecuteAsync(new[] { "status", "openai" }, ScriptedAuthPrompt.Cancelling());
        Assert.True(status.Success);
        Assert.DoesNotContain(AuthTestValues.ApiKey, status.Message);
        Assert.Contains(Redaction.Mask, status.Message);
        Assert.Contains("team-a", status.Message);
    }

    [Fact]
    public async Task Logout_RemovesTheStoredCredential()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        var command = CreateCommand(store);

        await command.ExecuteAsync(new[] { "login", "openai" }, ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey));
        var logout = await command.ExecuteAsync(new[] { "logout", "openai" }, ScriptedAuthPrompt.Cancelling());

        Assert.True(logout.Success);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task Login_WithoutAProviderExplainsTheUsage()
    {
        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(new[] { "login" }, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("Usage: auth login <provider>", result.Message);
    }

    [Fact]
    public async Task Login_RefusesAnyOptionThatCouldCarryASecretOnTheCommandLine()
    {
        // Process arguments are visible to other users and land in shell history, so a
        // credential must never be accepted as an argument.
        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(new[] { "login", "openai", "--api-key", AuthTestValues.ApiKey }, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("never be passed as an argument", result.Message);
        Assert.DoesNotContain(AuthTestValues.ApiKey, result.Message);
    }

    [Fact]
    public async Task Login_AcceptsBothMethodOptionForms()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var command = CreateCommand(new InMemoryCredentialStore());

        foreach (var args in new[]
        {
            new[] { "login", "openai", "--method", "device-code" },
            new[] { "login", "openai", "--method=device-code" },
            new[] { "login", "openai", "-m", "device-code" }
        })
        {
            var result = await command.ExecuteAsync(args, ScriptedAuthPrompt.Cancelling());

            // openai ships without OAuth, so the method is parsed and then correctly rejected.
            Assert.False(result.Success);
            Assert.Contains("Unsupported login method 'device-code'", result.Message);
        }
    }

    [Fact]
    public async Task Login_WithAMethodOptionMissingItsValueFails()
    {
        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(new[] { "login", "openai", "--method" }, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("--method needs a value", result.Message);
    }

    [Fact]
    public async Task Logout_WithoutAProviderExplainsTheUsage()
    {
        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(new[] { "logout" }, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("Usage: auth logout <provider>", result.Message);
    }

    [Fact]
    public async Task UnknownSubcommand_ShowsTheHelp()
    {
        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(new[] { "frobnicate" }, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("Unknown auth subcommand", result.Message);
        Assert.Contains("auth login <provider>", result.Message);
    }

    [Fact]
    public async Task Help_DocumentsPrecedenceAndAutomation()
    {
        var result = await CreateCommand(new InMemoryCredentialStore())
            .ExecuteAsync(new[] { "help" }, ScriptedAuthPrompt.Cancelling());

        Assert.True(result.Success);
        Assert.Contains("Credential precedence", result.Message);
        Assert.Contains("never persisted", result.Message);
        Assert.Contains("docs/provider-auth.md", result.Message);
    }

    [Fact]
    public void Command_IdentifiesItselfForTheDispatcher()
    {
        var command = CreateCommand(new InMemoryCredentialStore());

        Assert.Equal("auth", command.Name);
        Assert.NotEmpty(command.Description);
        Assert.Contains("/auth", command.Aliases);
    }

    [Fact]
    public void SlashCatalogAndHelp_ListTheAuthCommand()
    {
        Assert.Contains("auth", SlashCommandCatalog.CreateInlineHelpCommands().Select(c => c.Name));
        Assert.Contains("/auth login", HelpText.InteractiveHelpMarkdown());
        Assert.Contains("auth", HelpText.CommandLineHelp());
    }
}
