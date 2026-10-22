using System.Net;
using System.Text;
using ValheimBakaLoader.Service.Catalog;

namespace ValheimBakaLoader.Service.Auth;

/// <summary>What the login page is saying under the form, if anything.</summary>
public enum LoginNotice
{
    None,
    WrongPassword,
    WrongToken,
    PasswordTooShort,
    Locked,
}

/// <summary>
/// Renders Resources/login.html with the catalog's words in it. The template is read once;
/// the words are looked up per render so a language switch in service.json is honoured on
/// the next request rather than the next restart.
/// </summary>
public sealed class LoginPage
{
    private readonly string _template;

    public LoginPage(string templatePath)
    {
        _template = File.ReadAllText(templatePath, Encoding.UTF8);
    }

    public static string DefaultTemplatePath =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "login.html");

    public string Render(bool setupMode, LoginNotice notice, string lang)
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lang"] = lang,
            ["title"] = HostCatalog.T("service.login.title"),
            ["lead"] = setupMode
                ? HostCatalog.T("service.login.setup.lead")
                : HostCatalog.T("service.login.lead"),
            ["tokenLabel"] = HostCatalog.T("service.login.setup.token"),
            ["passwordLabel"] = setupMode
                ? HostCatalog.T("service.login.setup.password")
                : HostCatalog.T("service.login.password"),
            ["button"] = setupMode
                ? HostCatalog.T("service.login.setup.button")
                : HostCatalog.T("service.login.button"),
            ["message"] = notice switch
            {
                LoginNotice.WrongPassword => HostCatalog.T("service.login.wrong"),
                LoginNotice.WrongToken => HostCatalog.T("service.login.setup.token_wrong"),
                LoginNotice.PasswordTooShort => HostCatalog.T(
                    "service.login.setup.password_short", ("count", PasswordStore.MinimumLength)),
                LoginNotice.Locked => HostCatalog.T("service.login.locked"),
                _ => "",
            },
        };

        var html = _template;
        foreach (var (key, value) in words)
            html = html.Replace("{{" + key + "}}", WebUtility.HtmlEncode(value), StringComparison.Ordinal);

        // Attributes, not words: these never carry text and are never escaped.
        html = html.Replace("{{setupOnly}}", setupMode ? "" : "hidden", StringComparison.Ordinal);
        html = html.Replace("{{tokenFocus}}", setupMode ? "autofocus" : "", StringComparison.Ordinal);
        html = html.Replace("{{passwordFocus}}", setupMode ? "" : "autofocus", StringComparison.Ordinal);
        return html;
    }
}
