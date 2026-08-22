using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OrchardCore.ContentManagement;
using OrchardCore.Email;
using OrchardCore.Media;

namespace Newsletter.Admin.Newsletter;

/// <summary>
/// Resolves a campaign's audience and sends it one message per person.
///
/// Sending individually rather than by BCC is deliberate: a single message with
/// many recipients exposes the whole list to everyone on it, and providers treat
/// large BCC blocks as bulk mail. One failure also must not stop the run, so
/// each address is attempted independently and the outcome recorded per person.
/// </summary>
public sealed class CampaignSender
{
    private readonly IContentManager _contentManager;
    private readonly IEmailService _emailService;
    private readonly IMediaFileStore _mediaFileStore;
    private readonly NewsletterRenderer _renderer;
    private readonly ILogger<CampaignSender> _logger;

    public CampaignSender(
        IContentManager contentManager,
        IEmailService emailService,
        IMediaFileStore mediaFileStore,
        NewsletterRenderer renderer,
        ILogger<CampaignSender> logger)
    {
        _contentManager = contentManager;
        _emailService = emailService;
        _mediaFileStore = mediaFileStore;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<CampaignSendResult> SendAsync(string campaignContentItemId)
    {
        var campaign = await _contentManager.GetAsync(campaignContentItemId, VersionOptions.Latest);
        if (campaign is null)
        {
            return CampaignSendResult.Failed("That campaign no longer exists.");
        }

        JsonNode content = campaign.Content;
        if (content is not JsonObject root || root["Campaign"] is not JsonObject part)
        {
            return CampaignSendResult.Failed("That item is not a campaign.");
        }

        var subject = Text(part, "Subject");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return CampaignSendResult.Failed("The campaign has no subject.");
        }

        var body = Html(part, "Body");
        var attachmentPath = FirstMediaPath(part, "Attachment");

        var recipients = await ResolveRecipientsAsync(part);
        if (recipients.Count == 0)
        {
            return CampaignSendResult.Failed(
                "No subscribed recipients were selected. Pick people or groups first.");
        }

        // Files are read once; every message then gets its own stream over the
        // same bytes, because a stream is consumed by the first send using it.
        var attachments = new List<(string Filename, byte[] Bytes)>();
        var inlineImages = new Dictionary<string, string>(StringComparer.Ordinal);

        var sections = NewsletterModelReader.ReadSections(root);
        foreach (var path in sections
                     .SelectMany(s => s.Items)
                     .Select(i => i.ImagePath)
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.Ordinal))
        {
            var read = await ReadAttachmentAsync(path);
            if (read is not { } file)
            {
                continue;
            }

            attachments.Add(file);
            inlineImages[path!] = file.Filename;
        }

        var attachment = await ReadAttachmentAsync(attachmentPath);
        if (attachment is { } lead)
        {
            attachments.Add(lead);
        }

        var branding = await LoadBrandingAsync(attachments);

        // Every campaign goes through the renderer so the masthead and footer are
        // on every email, with or without sections. A campaign with no sections
        // is simply a newsletter whose body is the whole message - the lead
        // attachment is appended to that body the way it always was.
        var html = _renderer.Render(
            Text(part, "Edition"),
            BuildBody(body, attachment?.Filename),
            sections,
            Text(part, "FooterNote"),
            inlineImages,
            rightToLeft: ContainsArabic(subject) || ContainsArabic(body),
            branding: branding);

        var sent = 0;
        var failures = new List<string>();

        foreach (var (name, address) in recipients)
        {
            // HtmlBody, not Body + IsHtmlBody: the OC 3 SMTP provider reads
            // HtmlBody/TextBody straight off the message and never looks at
            // IsHtmlBody. The older pair still compiles, so setting them instead
            // sends a message with an empty body that the provider still reports
            // as delivered.
            var message = new MailMessage
            {
                To = address,
                // Placeholders are filled per person, so one campaign greets
                // each reader by their own name.
                Subject = Personalise(subject, name, address, asHtml: false),
                HtmlBody = Personalise(html, name, address, asHtml: true),
                // A text alternative alongside the HTML: clients set to plain
                // text show this instead of nothing, and spam filters score a
                // multipart message better than an HTML-only one.
                TextBody = Personalise(PlainText(html), name, address, asHtml: false),
            };

            foreach (var file in attachments)
            {
                message.Attachments.Add(new MailMessageAttachment
                {
                    Filename = file.Filename,
                    Stream = new MemoryStream(file.Bytes),
                });
            }

            // A body that is empty or whitespace-only gives some providers
            // nothing to deliver, so the send reports success and vanishes.
            if (string.IsNullOrWhiteSpace(message.HtmlBody))
            {
                message.HtmlBody = subject;
            }

            try
            {
                // The provider name is left null so Orchard uses the configured
                // default, which is whatever the admin set under Email settings.
                var result = await _emailService.SendAsync(message, null!);

                if (result.Succeeded)
                {
                    sent++;
                }
                else
                {
                    failures.Add($"{address}: {string.Join("; ", result.Errors.Select(e => e.Message))}");
                }
            }
            catch (Exception ex)
            {
                // One bad address must not abort the whole run.
                _logger.LogError(ex, "Campaign send failed for {Address}.", address);
                failures.Add($"{address}: {ex.Message}");
            }
        }

        return new CampaignSendResult(sent, failures);
    }

    /// <summary>
    /// Collects the individually picked people plus everyone in the picked
    /// groups, dropping unsubscribed and duplicate addresses.
    /// </summary>
    private async Task<List<(string Name, string Address)>> ResolveRecipientsAsync(JsonObject part)
    {
        var ids = new List<string>(PickedIds(part, "Recipients"));

        foreach (var groupId in PickedIds(part, "RecipientGroups"))
        {
            var group = await _contentManager.GetAsync(groupId, VersionOptions.Latest);
            if (group is null)
            {
                continue;
            }

            JsonNode groupContent = group.Content;
            if (groupContent is JsonObject groupRoot &&
                groupRoot["ContactGroup"] is JsonObject groupPart)
            {
                ids.AddRange(PickedIds(groupPart, "Members"));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recipients = new List<(string, string)>();

        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var contact = await _contentManager.GetAsync(id, VersionOptions.Latest);
            if (contact is null)
            {
                continue;
            }

            JsonNode contactContent = contact.Content;
            if (contactContent is not JsonObject contactRoot ||
                contactRoot["Contact"] is not JsonObject contactPart)
            {
                continue;
            }

            // An unsubscribed contact stays in the list but is never mailed.
            if (contactPart["Subscribed"]?["Value"]?.GetValue<bool>() == false)
            {
                continue;
            }

            var address = Text(contactPart, "Email");
            if (string.IsNullOrWhiteSpace(address) || !seen.Add(address))
            {
                continue;
            }

            recipients.Add((contact.DisplayText ?? address, address));
        }

        return recipients;
    }

    /// <summary>
    /// Builds the saved header and footer for every issue.
    ///
    /// Settings live in a single "Newsletter settings" item, so an editor is not
    /// retyping the brand details on every campaign. When no such item exists the
    /// newsletter still renders — every branding field is optional.
    /// </summary>
    private async Task<NewsletterBranding> LoadBrandingAsync(
        List<(string Filename, byte[] Bytes)> attachments)
    {
        var settings = await _contentManager.GetAsync(NewsletterSettingsId, VersionOptions.Latest);
        JsonObject? part = null;
        JsonObject? root = null;

        if (settings is not null)
        {
            JsonNode content = settings.Content;
            root = content as JsonObject;
            part = root?["NewsletterSettings"] as JsonObject;
        }

        if (part is null)
        {
            return new NewsletterBranding();
        }

        // The logo rides along as an inline attachment, like the news images.
        string? logoCid = null;
        if (await ReadAttachmentAsync(FirstMediaPath(part, "Logo")) is { } logo)
        {
            attachments.Add(logo);
            logoCid = logo.Filename;
        }

        return new NewsletterBranding
        {
            BrandName = Text(part, "BrandName"),
            LogoCid = logoCid,
            AccentColour = Text(part, "AccentColour"),
            Email = Text(part, "Email"),
            Phone = Text(part, "Phone"),
            Address = Text(part, "Address"),
            WebsiteUrl = Text(part, "WebsiteUrl"),
            FooterText = Text(part, "FooterText"),
            UnsubscribeUrl = Text(part, "UnsubscribeUrl"),
            Copyright = Text(part, "Copyright"),
            ComplianceNote = Text(part, "ComplianceNote"),
            SocialLinks = ReadSocialLinks(root),
        };
    }

    private static IReadOnlyList<NewsletterSocialLink> ReadSocialLinks(JsonObject? owner)
    {
        if (owner?["SocialLinks"]?["ContentItems"] is not JsonArray items)
        {
            return [];
        }

        var links = new List<NewsletterSocialLink>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var social = item["SocialLink"] as JsonObject;
            var url = social?["Url"]?["Text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            // The network name doubles as the label: an <img> per icon would add
            // an attachment for every link.
            var label = item["DisplayText"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(label))
            {
                label = url;
            }

            links.Add(new NewsletterSocialLink(label, url));
        }

        return links;
    }

    /// <summary>
    /// The single settings item holding the branding, created by the migration
    /// that defines the type.
    /// </summary>
    private const string NewsletterSettingsId =
        Migrations.NewsletterMigrations.NewsletterSettingsId;

    /// <summary>Any Arabic letter flips the newsletter to right-to-left.</summary>
    private static bool ContainsArabic(string? text) =>
        !string.IsNullOrEmpty(text) && text.Any(c => c >= '؀' && c <= 'ۿ');

    /// <summary>
    /// Reads the uploaded file so it can travel with the message.
    ///
    /// The file is attached rather than linked: a link needs the reader to
    /// click, and it breaks entirely once the site is unreachable from their
    /// network. Returns null when there is no attachment or it cannot be read.
    /// </summary>
    private async Task<(string Filename, byte[] Bytes)?> ReadAttachmentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            await using var stream = await _mediaFileStore.GetFileStreamAsync(path);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            return (Path.GetFileName(path), buffer.ToArray());
        }
        catch (Exception ex)
        {
            // A missing or unreadable file must not stop the campaign going out.
            _logger.LogError(ex, "Could not read campaign attachment {Path}.", path);
            return null;
        }
    }

    /// <summary>
    /// Shows an attached image inside the message body via its content id, so
    /// the picture appears in the email rather than only as a file to open.
    /// Non-image attachments are left to the client's attachment list.
    /// </summary>
    private static string BuildBody(string body, string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || !IsImage(filename))
        {
            return body;
        }

        return body +
            $"<p style=\"margin-top:16px\">" +
            $"<img src=\"cid:{filename}\" alt=\"\" style=\"max-width:100%;height:auto;border-radius:8px\" />" +
            $"</p>";
    }

    /// <summary>
    /// Substitutes the per-recipient placeholders an author can type into the
    /// subject or the message.
    ///
    /// The name is HTML-encoded because it lands inside markup: a contact
    /// called "Ben &amp; Co" would otherwise break the surrounding tags. First
    /// name is taken as the leading word, which suits "Dear {{FirstName}}".
    /// </summary>
    private static string Personalise(string? text, string name, string address, bool asHtml)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var full = (name ?? string.Empty).Trim();
        var first = full.Length == 0
            ? string.Empty
            : full.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        // Encoding belongs to the body only: a subject line is plain text, and
        // encoding it would show "&amp;" to the reader instead of "&".
        string Value(string raw) => asHtml ? WebUtility.HtmlEncode(raw) : raw;

        return text
            .Replace("{{Name}}", Value(full), StringComparison.OrdinalIgnoreCase)
            .Replace("{{FirstName}}", Value(first), StringComparison.OrdinalIgnoreCase)
            .Replace("{{Email}}", Value(address), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A readable plain-text version of the message: tags dropped, entities
    /// decoded, and the block boundaries kept as line breaks so the result is
    /// not one run-on paragraph.
    /// </summary>
    private static string PlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        // Block-level ends become newlines before the tags are stripped,
        // otherwise every line would run into the next.
        var text = Regex.Replace(
            html,
            @"</(p|div|tr|h[1-6]|li)\s*>|<br\s*/?>",
            "\n",
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);

        // Collapse the runs of blank lines the stripping leaves behind.
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"(\n\s*){3,}", "\n\n");

        return text.Trim();
    }

    private static bool IsImage(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant()
            is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private static IEnumerable<string> PickedIds(JsonObject part, string field) =>
        part[field]?["ContentItemIds"] is JsonArray array
            ? array.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s))!
            : [];

    private static string Text(JsonObject part, string field) =>
        part[field]?["Text"]?.GetValue<string>() ?? string.Empty;

    private static string Html(JsonObject part, string field) =>
        part[field]?["Html"]?.GetValue<string>() ?? string.Empty;

    private static string? FirstMediaPath(JsonObject part, string field) =>
        part[field]?["Paths"] is JsonArray paths && paths.Count > 0
            ? paths[0]?.GetValue<string>()
            : null;
}

/// <param name="Sent">How many messages the provider accepted.</param>
/// <param name="Failures">One entry per address that could not be sent.</param>
public sealed record CampaignSendResult(int Sent, IReadOnlyList<string> Failures)
{
    public string? Error { get; private init; }

    public bool IsFailure => Error is not null;

    public static CampaignSendResult Failed(string error) =>
        new(0, []) { Error = error };
}
