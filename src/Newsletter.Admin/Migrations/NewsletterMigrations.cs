using OrchardCore.ContentFields.Settings;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata.Builders;
using OrchardCore.Flows.Models;
using OrchardCore.ContentManagement.GraphQL.Settings;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Settings;
using OrchardCore.Data.Migration;
using OrchardCore.Media.Settings;
using OrchardCore.Title.Models;

namespace Newsletter.Admin.Migrations;

/// <summary>
/// Defines the newsletter content types.
///
/// These run against whatever database is already there, on every start, and
/// Orchard records the version reached per migration class. That is the point:
/// adding a field means appending an UpdateFromN method, not rebuilding the
/// database — so editor content survives schema changes.
///
/// All three types are admin-only by construction. None carries an
/// AutoroutePart, so they have no URL and cannot be reached from the site.
/// Keeping them route-less makes that guarantee structural rather than a
/// matter of configuration.
/// </summary>
public sealed class NewsletterMigrations : DataMigration
{
    private readonly IContentDefinitionManager _contentDefinitionManager;
    private readonly IContentManager _contentManager;

    public NewsletterMigrations(
        IContentDefinitionManager contentDefinitionManager,
        IContentManager contentManager)
    {
        _contentDefinitionManager = contentDefinitionManager;
        _contentManager = contentManager;
    }

    public async Task<int> CreateAsync()
    {
        // ---- Contact: one person -------------------------------------------
        await _contentDefinitionManager.AlterPartDefinitionAsync("Contact", part => part
            .WithField("Email", f => f
                .OfType("TextField").WithDisplayName("Email").WithPosition("0")
                .WithSettings(new TextFieldSettings
                {
                    Required = true,
                    Hint = "The address this person receives campaigns at.",
                }))
            .WithField("Organisation", f => f
                .OfType("TextField").WithDisplayName("Organisation").WithPosition("1"))
            .WithField("Subscribed", f => f
                .OfType("BooleanField").WithDisplayName("Subscribed").WithPosition("2")
                .WithSettings(new BooleanFieldSettings
                {
                    DefaultValue = true,
                    Hint = "Clear this to stop sending to the person without deleting them.",
                })));

        await _contentDefinitionManager.AlterTypeDefinitionAsync("Contact", type => type
            .DisplayedAs("Contact")
            .Creatable()
            .Securable()
            .WithPart(nameof(TitlePart), p => p
                .WithDisplayName("Name")
                .WithPosition("0"))
            .WithPart("Contact", p => p.WithPosition("1"))
            // Orchard exposes every content type through the API by default, and the
            // endpoint is readable anonymously — so without this a visitor could query
            // the contact list and read every stored address.
            .WithSettings(new GraphQLContentTypeSettings { Hidden = true }));

        // ---- ContactGroup: a named set of contacts -------------------------
        await _contentDefinitionManager.AlterPartDefinitionAsync("ContactGroup", part => part
            .WithField("Members", f => f
                .OfType("ContentPickerField").WithDisplayName("Members").WithPosition("0")
                .WithSettings(new ContentPickerFieldSettings
                {
                    Multiple = true,
                    DisplayedContentTypes = ["Contact"],
                    Hint = "The people in this group.",
                })));

        await _contentDefinitionManager.AlterTypeDefinitionAsync("ContactGroup", type => type
            .DisplayedAs("Contact group")
            .Creatable()
            .Securable()
            .WithPart(nameof(TitlePart), p => p
                .WithDisplayName("Group name")
                .WithPosition("0"))
            .WithPart("ContactGroup", p => p.WithPosition("1"))
            .WithSettings(new GraphQLContentTypeSettings { Hidden = true }));

        // ---- Campaign: the message plus who it goes to ---------------------
        await _contentDefinitionManager.AlterPartDefinitionAsync("Campaign", part => part
            .WithField("Subject", f => f
                .OfType("TextField").WithDisplayName("Subject").WithPosition("0")
                .WithSettings(new TextFieldSettings
                {
                    Required = true,
                    Hint = "The email subject line.",
                }))
            .WithField("Body", f => f
                .OfType("HtmlField").WithDisplayName("Message").WithPosition("1")
                .WithEditor("Wysiwyg"))
            .WithField("Attachment", f => f
                .OfType("MediaField").WithDisplayName("Image or video").WithPosition("2")
                .WithSettings(new MediaFieldSettings
                {
                    Multiple = false,
                    Hint = "Shown in the email as a link to the uploaded file.",
                }))
            .WithField("Recipients", f => f
                .OfType("ContentPickerField").WithDisplayName("Send to people").WithPosition("3")
                .WithSettings(new ContentPickerFieldSettings
                {
                    Multiple = true,
                    DisplayedContentTypes = ["Contact"],
                    Hint = "Individual recipients. Combine with groups if you like.",
                }))
            .WithField("RecipientGroups", f => f
                .OfType("ContentPickerField").WithDisplayName("Send to groups").WithPosition("4")
                .WithSettings(new ContentPickerFieldSettings
                {
                    Multiple = true,
                    DisplayedContentTypes = ["ContactGroup"],
                    Hint = "Everyone in the chosen groups receives this.",
                })));

        await _contentDefinitionManager.AlterTypeDefinitionAsync("Campaign", type => type
            .DisplayedAs("Campaign")
            .Creatable()
            .Draftable()
            .Versionable()
            .Securable()
            .WithPart(nameof(TitlePart), p => p
                .WithDisplayName("Internal name")
                .WithPosition("0"))
            .WithPart("Campaign", p => p.WithPosition("1"))
            .WithSettings(new GraphQLContentTypeSettings { Hidden = true }));

        return 1;
    }

    /// <summary>
    /// Adds the newsletter structure: a campaign can hold sections, and each
    /// section holds news items.
    ///
    /// This is what turns a campaign from a single message into a monthly
    /// newsletter - "Members news", "Sector news", "Announcements" - where each
    /// story carries its own image, date and link. Both types are widgets, so
    /// they are only ever created inside a campaign and never appear as
    /// standalone content.
    /// </summary>
    public async Task<int> UpdateFrom1Async()
    {
        await SimpleWidgetAsync("NewsItem", "News item", part => part
            .WithField("Summary", f => f
                .OfType("TextField").WithDisplayName("Summary").WithPosition("0")
                .WithEditor("TextArea")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Optional line under the headline.",
                }))
            .WithField("Date", f => f
                .OfType("TextField").WithDisplayName("Date").WithPosition("1")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Shown as written, e.g. Monday, July 27, 2026.",
                }))
            .WithField("Link", f => f
                .OfType("LinkField").WithDisplayName("Read more link").WithPosition("2"))
            .WithField("Image", f => f
                .OfType("MediaField").WithDisplayName("Thumbnail").WithPosition("3")
                .WithSettings(new MediaFieldSettings
                {
                    Multiple = false,
                    Hint = "Travels with the email, so keep it small - about 300px wide.",
                })));

        await SimpleWidgetAsync("NewsSection", "Newsletter section", part => part
            .WithField("Heading", f => f
                .OfType("TextField").WithDisplayName("Section heading").WithPosition("0")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "The blue bar, e.g. Members News.",
                })));

        await _contentDefinitionManager.AlterTypeDefinitionAsync("NewsSection", type => type
            .WithPart("Items", "BagPart", p => p
                .WithDisplayName("News items")
                .WithPosition("Parts:2#Items;0")
                .WithSettings(new BagPartSettings
                {
                    ContainedContentTypes = ["NewsItem"],
                    DisplayType = "Detail",
                })));

        await _contentDefinitionManager.AlterTypeDefinitionAsync("Campaign", type => type
            .WithPart("Sections", "BagPart", p => p
                .WithDisplayName("Newsletter sections")
                .WithPosition("Parts:5#Sections;0")
                .WithSettings(new BagPartSettings
                {
                    ContainedContentTypes = ["NewsSection"],
                    DisplayType = "Detail",
                })));

        // Header and footer text for the generated newsletter.
        await _contentDefinitionManager.AlterPartDefinitionAsync("Campaign", part => part
            .WithField("Edition", f => f
                .OfType("TextField").WithDisplayName("Edition").WithPosition("5")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Printed at the top, e.g. JULY 2026.",
                }))
            .WithField("FooterNote", f => f
                .OfType("TextField").WithDisplayName("Footer note").WithPosition("6")
                .WithEditor("TextArea")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Small print under the sections.",
                })));

        return 2;
    }

    /// <summary>
    /// Gives the newsletter a saved header and footer.
    ///
    /// A NewsletterSettings item holds the branding once - logo, colour, contact
    /// details, social links, the small print - so an editor is not retyping it
    /// on every issue. Every field is optional: whatever is left blank is simply
    /// not rendered.
    /// </summary>
    public async Task<int> UpdateFrom2Async()
    {
        await SimpleWidgetAsync("SocialLink", "Social link", part => part
            .WithField("Url", f => f
                .OfType("TextField").WithDisplayName("URL").WithPosition("0")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Full address, e.g. https://facebook.com/yourpage.",
                })));

        await _contentDefinitionManager.AlterPartDefinitionAsync("NewsletterSettings", part => part
            .Attachable()
            .WithField("BrandName", f => f
                .OfType("TextField").WithDisplayName("Brand name").WithPosition("0"))
            .WithField("Logo", f => f
                .OfType("MediaField").WithDisplayName("Logo").WithPosition("1")
                .WithSettings(new MediaFieldSettings
                {
                    Multiple = false,
                    Hint = "Travels with every email, so keep it small - about 34px tall.",
                }))
            .WithField("AccentColour", f => f
                .OfType("TextField").WithDisplayName("Accent colour").WithPosition("2")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Hex value for the header bar and section headings, e.g. #1b3a6b.",
                }))
            .WithField("Email", f => f
                .OfType("TextField").WithDisplayName("Contact email").WithPosition("3"))
            .WithField("Phone", f => f
                .OfType("TextField").WithDisplayName("Phone").WithPosition("4"))
            .WithField("Address", f => f
                .OfType("TextField").WithDisplayName("Address").WithPosition("5"))
            .WithField("WebsiteUrl", f => f
                .OfType("TextField").WithDisplayName("Website").WithPosition("6"))
            .WithField("FooterText", f => f
                .OfType("TextField").WithDisplayName("Footer text").WithPosition("7")
                .WithEditor("TextArea"))
            .WithField("UnsubscribeUrl", f => f
                .OfType("TextField").WithDisplayName("Unsubscribe URL").WithPosition("8"))
            .WithField("Copyright", f => f
                .OfType("TextField").WithDisplayName("Copyright").WithPosition("9")
                .WithSettings(new TextFieldSettings
                {
                    Hint = "Leave blank for the current year and brand name.",
                }))
            .WithField("ComplianceNote", f => f
                .OfType("TextField").WithDisplayName("Compliance note").WithPosition("10")
                .WithEditor("TextArea")));

        await _contentDefinitionManager.AlterTypeDefinitionAsync("NewsletterSettings", type => type
            .DisplayedAs("Newsletter settings")
            .Creatable()
            .Draftable()
            .Versionable()
            .Securable()
            .WithPart(nameof(TitlePart), p => p.WithPosition("0"))
            .WithPart("NewsletterSettings", p => p.WithPosition("1"))
            .WithPart("SocialLinks", "BagPart", p => p
                .WithDisplayName("Social links")
                .WithPosition("2")
                .WithSettings(new BagPartSettings
                {
                    ContainedContentTypes = ["SocialLink"],
                    DisplayType = "Detail",
                }))
            .WithSettings(new GraphQLContentTypeSettings { Hidden = true }));

        return 3;
    }

    /// <summary>
    /// Creates the single settings item the sender reads its branding from.
    ///
    /// The sender looks it up by a fixed id so it needs no query and an editor
    /// cannot end up with two competing settings items. That only works if the
    /// item actually carries that id, which an admin-created one would not - so
    /// it is created here instead, empty, ready to be filled in.
    /// </summary>
    public async Task<int> UpdateFrom3Async()
    {
        var existing = await _contentManager.GetAsync(
            NewsletterSettingsId, VersionOptions.Latest);

        if (existing is not null)
        {
            return 4;
        }

        var settings = await _contentManager.NewAsync("NewsletterSettings");
        settings.ContentItemId = NewsletterSettingsId;
        settings.DisplayText = "Newsletter settings";

        await _contentManager.CreateAsync(settings, VersionOptions.Published);

        return 4;
    }

    /// <summary>
    /// The id of that single settings item. Shared with the sender, which reads
    /// the branding back from it.
    /// </summary>
    public const string NewsletterSettingsId = "newslettersettings00000000";

    /// <summary>
    /// A widget type with a TitlePart and its own field part. Creatable matters:
    /// a type that is not creatable never registers in the GraphQL schema, and
    /// bags containing it fail to resolve.
    /// </summary>
    private async Task SimpleWidgetAsync(
        string name,
        string displayName,
        Action<ContentPartDefinitionBuilder>? fields)
    {
        if (fields is not null)
        {
            await _contentDefinitionManager.AlterPartDefinitionAsync(name, part =>
            {
                part.Attachable();
                fields(part);
            });
        }

        await _contentDefinitionManager.AlterTypeDefinitionAsync(name, type =>
        {
            type.DisplayedAs(displayName)
                .Creatable()
                .Draftable()
                .Versionable()
                .Stereotype("Widget")
                .WithPart(nameof(TitlePart), p => p.WithPosition("0"));

            if (fields is not null)
            {
                type.WithPart(name, p => p.WithPosition("1"));
            }
        });
    }
}
