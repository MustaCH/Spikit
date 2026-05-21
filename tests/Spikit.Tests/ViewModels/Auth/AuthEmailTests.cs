using Spikit.ViewModels.Auth;

namespace Spikit.Tests.ViewModels.Auth;

public class AuthEmailTests
{
    [Theory]
    [InlineData("nacho@spikit.dev")]
    [InlineData("nacho.poletti@spikit.dev")]
    [InlineData("user+tag@example.com")]
    [InlineData("name.surname@sub.dom.tld")]
    [InlineData("a@b.co")]
    [InlineData("first-last@example.io")]
    [InlineData("123@dom.com")]
    public void Valid_emails_pass(string email)
    {
        Assert.True(AuthEmail.IsValid(email), $"esperaba que '{email}' fuera válido");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-arroba")]
    [InlineData("two@@signs.com")]
    [InlineData("trailing.dot@dom.")]   // TLD vacío
    [InlineData("@nodomain.com")]       // sin parte local
    [InlineData("nolocal@")]            // sin dominio
    [InlineData("a@b.x")]               // TLD < 2 chars
    [InlineData("spaces in@email.com")]
    [InlineData("space@dom .com")]
    public void Invalid_emails_fail(string? email)
    {
        Assert.False(AuthEmail.IsValid(email), $"esperaba que '{email ?? "(null)"}' fuera inválido");
    }

    [Fact]
    public void Email_over_254_chars_fails()
    {
        var local = new string('a', 250);
        var email = $"{local}@dom.com";   // > 254 chars total

        Assert.False(AuthEmail.IsValid(email));
    }
}
