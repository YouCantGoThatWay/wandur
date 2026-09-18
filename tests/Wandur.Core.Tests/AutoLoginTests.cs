using Wandur.Core.Sessions;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

public sealed class AutoLoginTests
{
    [Theory]
    [InlineData("Name: ")]
    [InlineData("Username: ")]
    [InlineData("Account name: ")]
    [InlineData("(E)nter your character or account name, or type NEW: ")]
    [InlineData("By what name do you wish to be known? ")]
    public void SendsEachCredentialOnceAndOnlyInOrder(string prompt)
    {
        var login = new AutoLoginSequence(new ConnectionProfile());
        Assert.Equal(LoginStep.None, login.Next("Password: "));
        Assert.Equal(LoginStep.Username, login.Next(prompt));
        Assert.Equal(LoginStep.None, login.Next("Passw"));
        Assert.Equal(LoginStep.Password, login.Next("Password: "));
        Assert.Equal(LoginStep.None, login.Next("Password: "));
        Assert.Equal(LoginStep.None, login.Next(prompt));
    }

    [Theory]
    [InlineData("(P)assword: ")]
    [InlineData("(p)assword:\r\n")]
    [InlineData("Enter your (P)assword: ")]
    public void RecognizesParenthesizedPasswordShortcut(string prompt)
    {
        var login = new AutoLoginSequence(new ConnectionProfile());
        Assert.Equal(LoginStep.Username, login.Next("(E)nter your character or account name, or type NEW: "));
        Assert.Equal(LoginStep.Password, login.Next(prompt));
        Assert.Equal(LoginStep.None, login.Next(prompt));
    }

    [Fact]
    public void RetryPromptStopsAutomaticLogin()
    {
        var login = new AutoLoginSequence(new ConnectionProfile());
        Assert.Equal(LoginStep.Username, login.Next("Name: "));
        Assert.Equal(LoginStep.None, login.Next("Unknown player. Name: "));
        Assert.Equal(LoginStep.None, login.Next("Name: "));
        Assert.Equal(LoginStep.None, login.Next("Password: "));
    }

    [Fact]
    public void CustomPromptsAndExpiryAreRespected()
    {
        var profile = new ConnectionProfile { UsernamePrompt = @"^Who enters\?$", PasswordPrompt = @"^Secret\?$" };
        var login = new AutoLoginSequence(profile);
        Assert.Equal(LoginStep.None, login.Next("Name: "));
        Assert.Equal(LoginStep.Username, login.Next("Who enters?"));
        Assert.Equal(LoginStep.Password, login.Next("Secret?"));
        var expired = new AutoLoginSequence(profile, DateTimeOffset.UtcNow.AddMinutes(-3));
        Assert.Equal(LoginStep.None, expired.Next("Who enters?"));
    }

    [Theory]
    [InlineData("foo\rbar")]
    [InlineData("foo\nbar")]
    [InlineData("foo\u001bbar")]
    public void CredentialsCannotInjectCommands(string username)
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile { Host = "mud.example.org", Username = username }.Validate());
    }
}
