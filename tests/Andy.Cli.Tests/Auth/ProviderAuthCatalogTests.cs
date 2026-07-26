using System;
using System.IO;
using System.Linq;
using Andy.Cli.Auth;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// The catalog is what keeps provider knowledge out of the UI: every registry provider gains an
/// API-key login automatically, and OAuth is enabled from data rather than from code.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class ProviderAuthCatalogTests
{
    [Fact]
    public void EveryRegistryProvider_HasADescriptor()
    {
        var catalog = ProviderAuthCatalog.All(AuthTestValues.NoOverlay);

        Assert.Equal(
            ProviderRegistry.Ids.ToArray(),
            catalog.Select(d => d.ProviderId).ToArray());
    }

    [Fact]
    public void ApiKeyProviders_ExposeAMaskedSecretFieldAndTheirEnvironmentVariables()
    {
        var descriptor = ProviderAuthCatalog.Find("anthropic", AuthTestValues.NoOverlay);

        Assert.NotNull(descriptor);
        Assert.Contains(AuthMethodKind.ApiKey, descriptor!.SupportedMethods);
        Assert.Contains("ANTHROPIC_API_KEY", descriptor.EnvironmentVariables);

        var secretField = descriptor.SecretField;
        Assert.NotNull(secretField);
        Assert.True(secretField!.IsSecret);
        Assert.True(secretField.Required);
        Assert.Contains("ANTHROPIC_API_KEY", secretField.Hint);
    }

    [Fact]
    public void LocalProvider_OffersNoLoginMethods()
    {
        var descriptor = ProviderAuthCatalog.Find("ollama", AuthTestValues.NoOverlay);

        Assert.NotNull(descriptor);
        Assert.False(descriptor!.RequiresCredential);
        Assert.Empty(descriptor.SupportedMethods);
    }

    [Fact]
    public void Aliases_ResolveToTheCanonicalProvider()
    {
        var descriptor = ProviderAuthCatalog.Find("gemini", AuthTestValues.NoOverlay);

        Assert.NotNull(descriptor);
        Assert.Equal("google", descriptor!.ProviderId);
    }

    [Fact]
    public void UnknownProvider_HasNoDescriptor()
    {
        Assert.Null(ProviderAuthCatalog.Find("not-a-provider", AuthTestValues.NoOverlay));
        Assert.Null(ProviderAuthCatalog.Find(null, AuthTestValues.NoOverlay));
    }

    [Fact]
    public void Overlay_EnablesOAuthMethodsWithoutACodeChange()
    {
        using var directory = new TempDirectory();
        var overlayPath = directory.File("provider-auth.json");
        File.WriteAllText(overlayPath, AuthServiceTests.OverlayJson);

        var descriptor = ProviderAuthCatalog.Find("openai", overlayPath);

        Assert.NotNull(descriptor);
        Assert.Equal(
            new[] { AuthMethodKind.ApiKey, AuthMethodKind.OAuthLoopback, AuthMethodKind.OAuthDeviceCode },
            descriptor!.SupportedMethods.ToArray());
        Assert.Equal("test-client-id", descriptor.OAuth!.ClientId);

        // Providers not named in the overlay are unaffected.
        Assert.Null(ProviderAuthCatalog.Find("groq", overlayPath)!.OAuth);
    }

    [Fact]
    public void MalformedOverlay_FallsBackToTheBuiltInDescriptors()
    {
        using var directory = new TempDirectory();
        var overlayPath = directory.File("provider-auth.json");
        File.WriteAllText(overlayPath, "{ this is not json");

        var descriptor = ProviderAuthCatalog.Find("openai", overlayPath);

        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.OAuth);
        Assert.Contains(AuthMethodKind.ApiKey, descriptor.SupportedMethods);
    }

    [Fact]
    public void IncompleteOAuthOverlay_IsIgnored()
    {
        using var directory = new TempDirectory();
        var overlayPath = directory.File("provider-auth.json");
        File.WriteAllText(overlayPath, """
            { "providers": { "openai": { "oauth": { "clientId": "test-client-id" } } } }
            """);

        var descriptor = ProviderAuthCatalog.Find("openai", overlayPath);

        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.OAuth);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("tooshortvalue", false)]
    [InlineData("has whitespace inside it", false)]
    [InlineData("unit-test-credential-value-one", true)]
    public void SecretField_ValidatesLengthAndCharacterShape(string value, bool expected)
    {
        var field = ProviderAuthCatalog.Find("openai", AuthTestValues.NoOverlay)!.SecretField!;

        var valid = field.TryValidate(value, out var error);

        Assert.Equal(expected, valid);
        if (!expected)
        {
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void SecretField_ValidationErrorNeverEchoesTheValue()
    {
        var field = ProviderAuthCatalog.Find("openai", AuthTestValues.NoOverlay)!.SecretField!;

        const string pastedWithANewline = "unit-test-credential\nvalue-one";

        Assert.False(field.TryValidate(pastedWithANewline, out var error));
        Assert.DoesNotContain("unit-test-credential", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalField_AcceptsAnEmptyValue()
    {
        var descriptor = ProviderAuthCatalog.Find("openai", AuthTestValues.NoOverlay)!;
        var optional = descriptor.ApiKeyFields.Single(f => f.Name == "account_label");

        Assert.True(optional.TryValidate(null, out _));
        Assert.True(optional.TryValidate(string.Empty, out _));
        Assert.False(optional.IsSecret);
    }

    [Fact]
    public void MethodNames_RoundTripThroughParseAndDescribe()
    {
        foreach (var method in Enum.GetValues<AuthMethodKind>())
        {
            Assert.Equal(method, ProviderAuthHandler.Parse(ProviderAuthHandler.Describe(method)));
        }

        Assert.Null(ProviderAuthHandler.Parse("nonsense"));
        Assert.Null(ProviderAuthHandler.Parse(null));
    }
}
