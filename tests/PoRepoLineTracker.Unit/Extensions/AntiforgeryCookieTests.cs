using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using PoRepoLineTracker.API.Extensions;

namespace PoRepoLineTracker.Unit;

/// <summary>
/// Guards the antiforgery cookie's naming/Secure pairing.
///
/// <para>Regression: the cookie was named <c>__Host-PoRepoLineTracker.Antiforgery</c> with
/// <c>SecurePolicy.SameAsRequest</c>. RFC 6265bis requires a <c>__Host-</c> cookie to carry
/// <c>Secure</c>, and browsers reject one that does not — so on the plain-HTTP launch profile the
/// Set-Cookie was emitted without Secure, silently dropped by the browser, and every
/// state-changing request failed antiforgery with "the required antiforgery cookie is not
/// present". Reads worked, so the app looked healthy while every write returned 400.</para>
///
/// <para>These assert the invariant rather than the specific names: a <c>__Host-</c> prefix is
/// only ever paired with a policy that guarantees Secure.</para>
///
/// <para>The choice is driven by <c>Security:RequireSecureCookies</c>, not by the environment
/// name. Keying it on <c>IsDevelopment()</c> looked equivalent and was not: the integration tier
/// runs under the environment name "Test" on plain http://localhost, so it was handed
/// <c>__Host-</c> + Secure and every state-changing test in that suite failed.</para>
/// </summary>
public class AntiforgeryCookieTests
{
    private const string HostPrefix = "__Host-";

    private static AntiforgeryOptions OptionsFor(string environmentName, bool? requireSecureCookies = null)
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var builder = new ConfigurationBuilder();
        if (requireSecureCookies.HasValue)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigKeys.Security.RequireSecureCookies] = requireSecureCookies.Value ? "true" : "false"
            });
        }

        var services = new ServiceCollection();
        services.AddInfrastructure(builder.Build(), environment);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
    }

    /// <summary>
    /// One row per way the choice is made. Development's plain-http profile cannot set Secure, so
    /// the prefix would make the browser drop the cookie. Staging sits beside Production because
    /// the default is "secure outside Development", not a check on any particular name. "Test"
    /// with the setting off is the escape hatch the integration tier uses, and whose absence broke
    /// every write in that suite; Development with it on shows configuration wins the other way.
    /// </summary>
    [Theory]
    [InlineData("Development", null, false)]
    [InlineData("Production", null, true)]
    [InlineData("Staging", null, true)]
    [InlineData("Test", false, false)]
    [InlineData("Development", true, true)]
    public void HostPrefix_ComesOnlyWithSecure_AndEveryVariantKeepsTheDoubleSubmitContract(
        string environmentName, bool? requireSecureCookies, bool expectHostPrefix)
    {
        var options = OptionsFor(environmentName, requireSecureCookies);

        options.Cookie.Name.Should().NotBeNullOrWhiteSpace();
        if (expectHostPrefix)
        {
            options.Cookie.Name.Should().StartWith(HostPrefix);
            options.Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.Always,
                "a __Host- cookie without Secure is rejected outright by the browser");
        }
        else
        {
            options.Cookie.Name.Should().NotStartWith(HostPrefix);
            options.Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.SameAsRequest);
        }

        // The header half is what makes antiforgery work for a JSON API at all — the default
        // validator reads a form field, and no endpoint here posts a form.
        options.HeaderName.Should().Be("X-CSRF-TOKEN");
        options.Cookie.HttpOnly.Should().BeTrue();
        options.Cookie.SameSite.Should().Be(SameSiteMode.Strict);
    }
}
