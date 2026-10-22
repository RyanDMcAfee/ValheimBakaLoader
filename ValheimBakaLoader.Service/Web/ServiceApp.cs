using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using ValheimBakaLoader.Service.Auth;
using ValheimBakaLoader.Service.Bridge;
using ValheimBakaLoader.Service.Catalog;
using ValheimBakaLoader.Service.Tls;
using ValheimBakaLoader.Service.Transport;

namespace ValheimBakaLoader.Service.Web;

/// <summary>
/// The whole web side in one place: the pipeline, the routes and what each one needs.
/// Built the same way for the real server and for the tests, which swap the listener for
/// an in-memory one through <c>configure</c>.
/// </summary>
public static class ServiceApp
{
    public const string CookieName = "bakaloader.session";
    public const string StampClaim = "pwstamp";

    public static WebApplication Build(ServiceOptions options, IPageBridge bridge, Action<WebApplicationBuilder>? configure = null)
    {
        // The file providers want absolute paths and a relative --data-dir on the command
        // line is the most natural thing to type, so every folder is made absolute once here.
        options.DataDir = Path.GetFullPath(options.DataDir);
        options.WebUiDir = Path.GetFullPath(options.WebUiDir);
        if (options.AtlasDir != null) options.AtlasDir = Path.GetFullPath(options.AtlasDir);
        if (options.LangDir != null) options.LangDir = Path.GetFullPath(options.LangDir);
        if (options.CertPath != null) options.CertPath = Path.GetFullPath(options.CertPath);
        if (options.KeyPath != null) options.KeyPath = Path.GetFullPath(options.KeyPath);

        Directory.CreateDirectory(options.DataDir);
        Directory.CreateDirectory(options.EffectiveAtlasDir);
        Directory.CreateDirectory(options.EffectiveLangDir);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "ValheimBakaLoader.Service",
        });

        // The journal already stamps every line; a second timestamp per line is noise.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = null; });
        // One line per request is a flood in a journal; the framework's warnings still come through.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(bridge);
        builder.Services.AddSingleton<CallSerializer>();
        builder.Services.AddSingleton<WebSocketTransport>();
        builder.Services.AddSingleton(new PasswordStore(options.ServiceFilePath));
        builder.Services.AddSingleton(new LoginThrottle(options.LoginFailuresAllowed, options.LoginLockout));
        builder.Services.AddSingleton<SetupToken>();
        builder.Services.AddSingleton(new LoginPage(LoginPage.DefaultTemplatePath));

        HostCatalog.Use(HostCatalog.Load(options.WebUiDir, options.EffectiveLangDir, options.Lang));

        // Cookie keys on disk, so a restart does not log every browser out.
        builder.Services.AddDataProtection()
            .SetApplicationName("bakaloader")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")));

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.Cookie.IsEssential = true;
                cookie.ExpireTimeSpan = options.SessionIdle;
                cookie.SlidingExpiration = true;
                cookie.LoginPath = "/login";
                cookie.LogoutPath = "/logout";
                // There is one page, so there is nothing to return to: a plain /login, no
                // ReturnUrl to validate.
                cookie.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.Redirect("/login");
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                cookie.Events.OnValidatePrincipal = ctx =>
                {
                    // A session issued under an older password is over.
                    var store = ctx.HttpContext.RequestServices.GetRequiredService<PasswordStore>();
                    var stamp = ctx.Principal?.FindFirst(StampClaim)?.Value;
                    if (stamp == null || stamp != store.Stamp)
                    {
                        ctx.RejectPrincipal();
                        return ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                    return Task.CompletedTask;
                };
            });

        ConfigureListening(builder, options);
        configure?.Invoke(builder);

        var app = builder.Build();

        app.UseRouting();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "same-origin";
            await next();
        });

        app.UseAuthentication();

        // Everything but the door and its type needs a session. The challenge is the cookie
        // handler's, which answers with the redirect above.
        app.Use(async (ctx, next) =>
        {
            if (IsPublic(ctx.Request.Path) || ctx.User.Identity?.IsAuthenticated == true)
            {
                await next();
                return;
            }
            await ctx.ChallengeAsync();
        });

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.PingInterval });

        app.MapGet("/login", GetLoginAsync);
        app.MapPost("/login", PostLoginAsync);
        app.MapGet("/logout", LogoutAsync);
        app.MapGet("/boot.js", BootJsAsync);
        app.Map("/ws", (HttpContext ctx, WebSocketTransport transport) => transport.ServeAsync(ctx));

        // The routes above run here, before the files, so a boot.js or a login.html that
        // one day lands in WebUI cannot shadow them.
        app.UseEndpoints(_ => { });

        MapFiles(app, options);

        return app;
    }

    private static bool IsPublic(PathString path) =>
        path.Equals("/login", StringComparison.Ordinal) ||
        path.Equals("/logout", StringComparison.Ordinal) ||
        path.StartsWithSegments("/fonts", StringComparison.Ordinal);

    private static void MapFiles(WebApplication app, ServiceOptions options)
    {
        if (!Directory.Exists(options.WebUiDir))
        {
            app.Logger.LogError("The WebUI folder is missing: {dir}", options.WebUiDir);
            return;
        }

        var web = new PhysicalFileProvider(options.WebUiDir);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".woff2"] = "font/woff2";
        types.Mappings[".json"] = "application/json; charset=utf-8";

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = web, DefaultFileNames = { "index.html" } });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = web,
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                // The page stamps its own assets with the version; the document itself must
                // be asked for every time or an update would show the old interface.
                if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
            },
        });

        MapFolder(app, "/atlas", options.EffectiveAtlasDir, types);
        MapFolder(app, "/lang", options.EffectiveLangDir, types);
    }

    private static void MapFolder(WebApplication app, string prefix, string folder, FileExtensionContentTypeProvider types)
    {
        Directory.CreateDirectory(folder);
        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = prefix,
            FileProvider = new PhysicalFileProvider(folder),
            ContentTypeProvider = types,
            ServeUnknownFileTypes = true,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });
    }

    private static void ConfigureListening(WebApplicationBuilder builder, ServiceOptions options)
    {
        var plan = ListenPlan.Resolve(options);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            if (!plan.UseTls)
            {
                kestrel.Listen(plan.EndPoint);
                return;
            }

            var certificate = plan.LoadCertificate(options)!;
            kestrel.Listen(plan.EndPoint, listen => listen.UseHttps(certificate));
        });
    }

    /// <summary>The log lines at start that say where the service is and how it is secured.</summary>
    public static void LogListening(ServiceOptions options, ILogger log)
    {
        var plan = ListenPlan.Resolve(options);
        var scheme = plan.UseTls ? "https" : "http";
        log.LogInformation("Listening on {scheme}://{endpoint}", scheme, plan.EndPoint);
        if (plan.CertificateSource == CertificateSource.SelfSigned)
        {
            using var certificate = plan.LoadCertificate(options)!;
            log.LogWarning(
                "Serving a self-signed certificate from {folder}. SHA256 fingerprint: {fingerprint}",
                plan.CertificateFolder, SelfSignedCertificate.Fingerprint(certificate));
        }
    }

    private static Task GetLoginAsync(HttpContext ctx, PasswordStore store, LoginPage page, ServiceOptions options)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            ctx.Response.Redirect("/");
            return Task.CompletedTask;
        }
        return WriteLoginAsync(ctx, page, !store.HasPassword, LoginNotice.None, options.Lang, StatusCodes.Status200OK);
    }

    private static async Task PostLoginAsync(
        HttpContext ctx, PasswordStore store, LoginPage page, LoginThrottle throttle,
        SetupToken setup, ServiceOptions options, ILogger<LoginPage> log)
    {
        var address = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // A cross-site POST cannot carry the Strict cookie, but a login needs no cookie, so
        // the Origin is the one thing standing between a page elsewhere and the form.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !WebSocketTransport.OriginMatchesHost(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var setupMode = !store.HasPassword;

        if (throttle.IsLocked(address))
        {
            await WriteLoginAsync(ctx, page, setupMode, LoginNotice.Locked, options.Lang, StatusCodes.Status429TooManyRequests);
            return;
        }

        var form = await ctx.Request.ReadFormAsync();
        var password = form["password"].ToString();

        if (setupMode)
        {
            if (!setup.TryRedeem(form["token"].ToString()))
            {
                var locked = throttle.RecordFailure(address);
                log.LogWarning("Wrong setup token from {address}", address);
                await WriteLoginAsync(ctx, page, true, locked ? LoginNotice.Locked : LoginNotice.WrongToken,
                    options.Lang, locked ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized);
                return;
            }

            if (password.Length < PasswordStore.MinimumLength)
            {
                // The token was right, so it stays spent; the person is at the console and
                // can issue another by restarting, and a token that survives a bad password
                // is a token somebody else can still find in the journal.
                log.LogWarning("The setup token was right but the password is too short; a new token follows");
                LogSetupToken(setup.Issue(), log);
                await WriteLoginAsync(ctx, page, true, LoginNotice.PasswordTooShort, options.Lang, StatusCodes.Status400BadRequest);
                return;
            }

            store.SetPassword(password);
            log.LogInformation("The password was set through the setup token from {address}", address);
            throttle.Reset(address);
            await SignInAsync(ctx, store);
            return;
        }

        if (!store.Verify(password))
        {
            var locked = throttle.RecordFailure(address);
            log.LogWarning("Wrong password from {address}{lock}", address, locked ? " - locked for a while" : "");
            await WriteLoginAsync(ctx, page, false, locked ? LoginNotice.Locked : LoginNotice.WrongPassword,
                options.Lang, locked ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized);
            return;
        }

        throttle.Reset(address);
        log.LogInformation("Signed in from {address}", address);
        await SignInAsync(ctx, store);
    }

    private static async Task SignInAsync(HttpContext ctx, PasswordStore store)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, "admin"));
        identity.AddClaim(new Claim(StampClaim, store.Stamp ?? ""));
        await ctx.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true });
        ctx.Response.Redirect("/");
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
    }

    private static async Task LogoutAsync(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        ctx.Response.Redirect("/login");
    }

    private static Task WriteLoginAsync(HttpContext ctx, LoginPage page, bool setupMode, LoginNotice notice, string lang, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(page.Render(setupMode, notice, lang), Encoding.UTF8);
    }

    /// <summary>
    /// The three globals index.html reads before any script runs. On Windows they are
    /// injected into the WebView2 core before navigation; here the page loads this file
    /// first instead. Never cached, so a service update is seen on the next load.
    /// </summary>
    private static Task BootJsAsync(HttpContext ctx, ServiceOptions options)
    {
        ctx.Response.ContentType = "text/javascript; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(BootJs(options), Encoding.UTF8);
    }

    public static string BootJs(ServiceOptions options) =>
        "window.BAKA_HOST=\"service\";" +
        $"window.BAKA_VERSION=\"{JsString(options.Version)}\";" +
        $"window.BAKA_LANG=\"{JsString(options.Lang)}\";";

    private static string JsString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '<': sb.Append("\\x3c"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Issues the one-time token when no password is set and writes it to the log, which
    /// under systemd is the journal. Called once at start by the host.
    /// </summary>
    public static void AnnounceSetupIfNeeded(WebApplication app)
    {
        var store = app.Services.GetRequiredService<PasswordStore>();
        if (store.HasPassword) return;
        var setup = app.Services.GetRequiredService<SetupToken>();
        LogSetupToken(setup.Issue(), app.Logger);
    }

    private static void LogSetupToken(string token, ILogger log)
    {
        // The token is logged on purpose: the journal is the one place only the box's
        // administrator can read, which makes it the proof of who is setting the password.
        log.LogWarning(
            "No password is set. Open /login and paste this one-time setup token together with the password to use: {token}",
            token);
    }
}
