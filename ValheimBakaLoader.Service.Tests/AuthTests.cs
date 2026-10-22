using System.Net;
using ValheimBakaLoader.Service.Auth;

namespace ValheimBakaLoader.Service.Tests;

public class AuthTests
{
    [Fact]
    public async Task The_page_and_the_socket_send_a_stranger_to_the_door()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var client = host.Client();

        foreach (var path in new[] { "/", "/index.html", "/ws", "/boot.js", "/app.js" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/login", response.Headers.Location?.ToString());
        }

        using var door = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, door.StatusCode);
        var html = await door.Content.ReadAsStringAsync();
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("Valheim BakaLoader", html);
        Assert.Contains("Sign in", html);
    }

    [Fact]
    public async Task The_right_password_sets_a_cookie_with_the_right_flags()
    {
        await using var host = await new ServiceHost().StartAsync();

        var plain = await host.LoginSetCookieAsync(https: false);
        Assert.Contains("httponly", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", plain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", plain, StringComparison.OrdinalIgnoreCase);

        var secure = await host.LoginSetCookieAsync(https: true);
        Assert.Contains("httponly", secure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", secure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; secure", secure, StringComparison.OrdinalIgnoreCase);

        // And the cookie opens the page.
        using var client = host.Client();
        client.DefaultRequestHeaders.Add("Cookie", ServiceHost.CookieHeader(plain));
        using var page = await client.GetAsync("/index.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<!DOCTYPE html>", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_address()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var client = host.Client();

        for (var n = 0; n < 5; n++)
        {
            using var wrong = await client.PostAsync("/login", ServiceHost.Form(("password", "not it " + n)));
            var expected = n == 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized;
            Assert.Equal(expected, wrong.StatusCode);
        }

        using var right = await client.PostAsync("/login", ServiceHost.Form(("password", ServiceHost.Password)));
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.False(right.Headers.Contains("Set-Cookie"));
        Assert.Contains("Too many tries", await right.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var host = await new ServiceHost().StartAsync();
        var cookie = await host.SessionCookieAsync();

        using var client = host.Client();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var logout = await client.GetAsync("/logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location?.ToString());

        // The browser drops the cookie on the Set-Cookie it just got; a client that keeps
        // sending the old one is still told to sign in, because its stamp no longer matches
        // once the password moves. Here the password did not move, so the proof is the
        // expiring Set-Cookie on the logout.
        Assert.True(logout.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.StartsWith(Web.ServiceApp.CookieName + "=", StringComparison.Ordinal)
                                       && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task With_no_password_the_setup_token_sets_one_once()
    {
        await using var host = await new ServiceHost(withPassword: false).StartAsync();
        var setup = host.App.Services.GetRequiredService<SetupToken>();
        var token = setup.Issue();
        using var client = host.Client();

        using var door = await client.GetAsync("/login");
        Assert.Contains("name=\"token\"", await door.Content.ReadAsStringAsync());
        Assert.Contains("Setup token", await door.Content.ReadAsStringAsync());

        using var wrongToken = await client.PostAsync("/login", ServiceHost.Form(("token", "nope"), ("password", "a fine password")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);

        using var done = await client.PostAsync("/login", ServiceHost.Form(("token", token), ("password", "a fine password")));
        Assert.Equal(HttpStatusCode.SeeOther, done.StatusCode);
        Assert.True(done.Headers.Contains("Set-Cookie"));

        var store = new PasswordStore(host.Options.ServiceFilePath);
        Assert.True(store.Verify("a fine password"));
        Assert.False(setup.IsIssued);

        // Spent: the same token again is a wrong password now that one is set.
        using var again = await client.PostAsync("/login", ServiceHost.Form(("token", token), ("password", "another one")));
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public void The_throttle_counts_five_and_forgets_after_the_lockout()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var throttle = new LoginThrottle(5, TimeSpan.FromSeconds(60), clock);

        for (var n = 0; n < 4; n++) Assert.False(throttle.RecordFailure("10.0.0.1"));
        Assert.False(throttle.IsLocked("10.0.0.1"));
        Assert.True(throttle.RecordFailure("10.0.0.1"));
        Assert.True(throttle.IsLocked("10.0.0.1"));
        Assert.False(throttle.IsLocked("10.0.0.2"));

        clock.Now += TimeSpan.FromSeconds(61);
        Assert.False(throttle.IsLocked("10.0.0.1"));
    }

    private sealed class FakeClock : TimeProvider
    {
        public FakeClock(DateTimeOffset now) { Now = now; }
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
