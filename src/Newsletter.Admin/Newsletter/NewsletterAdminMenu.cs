using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using OrchardCore.Navigation;

namespace Newsletter.Admin.Newsletter;

/// <summary>
/// Gives the newsletter its own place in the admin sidebar.
///
/// Without this, campaigns and contacts land in the same Content Items list as
/// the site's pages, which mixes two unrelated jobs. Each entry links to the
/// content list pre-filtered to one type, so the screens stay separate.
/// </summary>
public sealed class NewsletterAdminMenu : INavigationProvider
{
    private readonly IStringLocalizer S;
    private readonly string _adminPrefix;

    public NewsletterAdminMenu(
        IStringLocalizer<NewsletterAdminMenu> localizer,
        IConfiguration configuration)
    {
        S = localizer;
        _adminPrefix = configuration["OrchardCore:OrchardCore_Admin:AdminUrlPrefix"] ?? "Admin";
    }

    public ValueTask BuildNavigationAsync(string name, NavigationBuilder builder)
    {
        if (!string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.CompletedTask;
        }

        builder.Add(S["Newsletter"], "5", newsletter => newsletter
            .AddClass("newsletter")
            .Id("newsletter")
            .Add(S["Campaigns"], "1", campaigns => campaigns
                .Url($"~/{_adminPrefix}/Contents/ContentItems?contentTypeId=Campaign")
                .LocalNav())
            .Add(S["Contacts"], "2", contacts => contacts
                .Url($"~/{_adminPrefix}/Contents/ContentItems?contentTypeId=Contact")
                .LocalNav())
            .Add(S["Groups"], "3", groups => groups
                .Url($"~/{_adminPrefix}/Contents/ContentItems?contentTypeId=ContactGroup")
                .LocalNav())
            .Add(S["Settings"], "4", settings => settings
                .Url($"~/{_adminPrefix}/Contents/ContentItems?contentTypeId=NewsletterSettings")
                .LocalNav()));

        return ValueTask.CompletedTask;
    }
}
