using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Newsletter.Admin.Newsletter;

/// <summary>
/// Builds the newsletter's HTML.
///
/// Email clients are not browsers: Outlook renders through Word, and Gmail
/// strips anything in a &lt;style&gt; block. So the layout is tables with inline
/// styles and fixed pixel widths — the one combination that survives everywhere.
/// Flexbox, grid and class selectors are deliberately absent.
/// </summary>
public sealed class NewsletterRenderer
{
    private const int Width = 640;
    private const string Brand = "#1b3a6b";
    private const string Ink = "#1f2933";
    private const string Muted = "#6b7280";
    private const string Line = "#e5e7eb";

    /// <param name="images">
    /// Maps a media path to the content id used for its inline attachment, so
    /// the markup can reference an image that travels with the message.
    /// </param>
    public string Render(
        string edition,
        string intro,
        IReadOnlyList<NewsSectionModel> sections,
        string footerNote,
        IReadOnlyDictionary<string, string> images,
        bool rightToLeft,
        NewsletterBranding? branding = null)
    {
        var brand = branding ?? new NewsletterBranding();
        var accent = string.IsNullOrWhiteSpace(brand.AccentColour) ? Brand : brand.AccentColour!;
        var dir = rightToLeft ? "rtl" : "ltr";
        var align = rightToLeft ? "right" : "left";
        var sb = new StringBuilder(4096);

        sb.Append(
            $"""
            <div dir="{dir}" style="margin:0;padding:0;background:#f4f5f7">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"
                   style="background:#f4f5f7;padding:24px 12px">
            <tr><td align="center">
            <table role="presentation" width="{Width}" cellpadding="0" cellspacing="0" border="0"
                   style="width:{Width}px;max-width:100%;background:#ffffff;
                          font-family:Arial,Helvetica,sans-serif;color:{Ink}">
            """);

        // Masthead: logo on one side, one issue line on the other.
        var logo = brand.LogoCid is { Length: > 0 }
            ? $"""<img src="cid:{brand.LogoCid}" alt="{Esc(brand.BrandName)}" height="34" """
              + """style="height:34px;width:auto;border:0;display:block" />"""
            : $"""<span style="font-size:19px;font-weight:bold;color:{accent}">{Esc(brand.BrandName)}</span>""";

        // The accent rule is the one piece that always shows: it frames the email
        // even before any branding has been filled in.
        sb.Append(
            $"""
            <tr><td style="padding:0;line-height:0;font-size:0">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                <tr><td height="4" style="height:4px;background:{accent}"></td></tr>
              </table>
            </td></tr>
            """);

        // An empty masthead row reads as a broken email, so it is skipped until
        // there is a brand name, a logo or an edition line to put in it.
        if (!string.IsNullOrWhiteSpace(brand.BrandName) ||
            brand.LogoCid is { Length: > 0 } ||
            !string.IsNullOrWhiteSpace(edition))
        {
            sb.Append(
                $"""
                <tr><td style="padding:20px 24px;border-bottom:1px solid {Line}">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                         border="0" dir="{dir}"><tr>
                    <td align="{align}" valign="middle">{logo}</td>
                    <td align="{(align == "right" ? "left" : "right")}" valign="middle"
                        style="font-size:12px;letter-spacing:0.5px;color:{Muted};white-space:nowrap">
                      {Esc(edition)}
                    </td>
                  </tr></table>
                </td></tr>
                """);
        }

        if (!string.IsNullOrWhiteSpace(intro))
        {
            sb.Append(
                $"""
                <tr><td style="padding:20px 24px;font-size:15px;line-height:1.7" align="{align}">
                  {intro}
                </td></tr>
                """);
        }

        foreach (var section in sections)
        {
            RenderSection(sb, section, images, align, accent);
        }

        RenderFooter(sb, footerNote, brand, accent, align, dir);
        RenderBottomBar(sb, brand, dir);

        sb.Append("</table></td></tr></table></div>");

        return sb.ToString();
    }

    private static void RenderSection(
        StringBuilder sb,
        NewsSectionModel section,
        IReadOnlyDictionary<string, string> images,
        string align,
        string accent)
    {
        if (!string.IsNullOrWhiteSpace(section.Heading))
        {
            sb.Append(
                $"""
                <tr><td style="padding:14px 24px;background:{accent};color:#ffffff;
                               font-size:16px;font-weight:bold" align="center">
                  {Esc(section.Heading)}
                </td></tr>
                """);
        }

        foreach (var item in section.Items)
        {
            // dir on the row's table is what actually mirrors the cell order.
            // align alone only moves text inside a cell; the thumbnail column
            // stays put until the table itself is told which way it reads.
            var rowDir = align == "right" ? "rtl" : "ltr";
            sb.Append($"""<tr><td style="padding:0 24px"><table role="presentation" width="100%" dir="{rowDir}" """);
            sb.Append($"""cellpadding="0" cellspacing="0" border="0" style="border-bottom:1px solid {Line}">""");
            sb.Append("<tr>");

            // The thumbnail is a fixed-width cell so the row keeps its shape in
            // Outlook, which ignores max-width on images.
            var cid = item.ImagePath is not null && images.TryGetValue(item.ImagePath, out var value)
                ? value
                : null;

            // With dir on the table the client mirrors the cells itself, so the
            // thumbnail is always written first and lands on the reading side.
            if (cid is not null)
            {
                AppendThumbnail(sb, cid, align == "right" ? "14px 0 14px 12px" : "14px 12px 14px 0");
            }

            // dir="auto" per cell keeps each headline in its own script's
            // order: an English title inside an Arabic newsletter still reads
            // left to right.
            sb.Append($"""<td valign="top" style="padding:14px 0" align="{align}" dir="auto">""");

            var title = Esc(item.Title);
            const string TitleStyle = "font-size:15px;font-weight:bold;line-height:1.5";
            sb.Append(item.LinkUrl is { Length: > 0 }
                ? $"""<a href="{Esc(item.LinkUrl)}" style="color:{accent};text-decoration:none;{TitleStyle}">{title}</a>"""
                : $"""<span style="{TitleStyle}">{title}</span>""");

            if (!string.IsNullOrWhiteSpace(item.Summary))
            {
                sb.Append(
                    $"""
                    <div style="margin-top:6px;font-size:13px;line-height:1.6;color:{Ink}">
                      {Esc(item.Summary)}
                    </div>
                    """);
            }

            if (!string.IsNullOrWhiteSpace(item.Date))
            {
                sb.Append($"""<div style="margin-top:6px;font-size:12px;color:{Muted}">{Esc(item.Date)}</div>""");
            }

            sb.Append("</td></tr></table></td></tr>");
        }
    }

    private static void AppendThumbnail(StringBuilder sb, string cid, string padding) =>
        sb.Append(
            $"""
            <td width="110" valign="top" style="padding:{padding};width:110px">
              <img src="cid:{cid}" width="110" alt=""
                   style="width:110px;height:auto;display:block;border:0;border-radius:4px" />
            </td>
            """);

    /// <summary>
    /// The saved footer: contact details, the small print and an unsubscribe
    /// link. Every piece is optional, so a half-filled settings item still
    /// produces a tidy block rather than empty rows.
    /// </summary>
    private static void RenderFooter(
        StringBuilder sb,
        string campaignNote,
        NewsletterBranding brand,
        string accent,
        string align,
        string dir)
    {
        var contact = new List<string>();
        if (!string.IsNullOrWhiteSpace(brand.Email))
        {
            contact.Add($"""<a href="mailto:{Esc(brand.Email)}" style="color:{accent};text-decoration:none">{Esc(brand.Email)}</a>""");
        }

        if (!string.IsNullOrWhiteSpace(brand.Phone))
        {
            contact.Add(Esc(brand.Phone));
        }

        if (!string.IsNullOrWhiteSpace(brand.Address))
        {
            contact.Add(Esc(brand.Address));
        }

        if (!string.IsNullOrWhiteSpace(brand.WebsiteUrl))
        {
            contact.Add($"""<a href="{Esc(brand.WebsiteUrl)}" style="color:{accent};text-decoration:none">{Esc(brand.WebsiteUrl)}</a>""");
        }

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(campaignNote))
        {
            notes.Add(LineBreaks(campaignNote));
        }

        if (!string.IsNullOrWhiteSpace(brand.FooterText))
        {
            notes.Add(LineBreaks(brand.FooterText));
        }

        if (contact.Count == 0 && notes.Count == 0)
        {
            return;
        }

        sb.Append(
            $"""
            <tr><td dir="{dir}" align="{align}"
                    style="padding:22px 24px;background:#f9fafb;border-top:1px solid {Line};
                           font-size:12px;line-height:1.7;color:{Muted}">
            """);

        if (contact.Count > 0)
        {
            sb.Append($"""<div style="margin-bottom:8px">{string.Join(" &nbsp;&#183;&nbsp; ", contact)}</div>""");
        }

        foreach (var note in notes)
        {
            sb.Append($"""<div style="margin-bottom:6px">{note}</div>""");
        }

        sb.Append("</td></tr>");
    }

    /// <summary>
    /// The dark closing bar: copyright, the social links, and the unsubscribe link.
    ///
    /// Networks come from whatever the editor added in the settings — nothing is
    /// hardcoded, so a link only appears when it exists and always points at the
    /// URL that was entered.
    ///
    /// Each link is a text pill rather than an icon image: mail clients block
    /// remote images until the reader opts in, and an SVG icon does not render at
    /// all, so a coloured cell with the network's name always shows up.
    /// </summary>
    private static void RenderBottomBar(
        StringBuilder sb,
        NewsletterBranding brand,
        string dir)
    {
        // With no brand name and no explicit copyright there is nothing to put in
        // the bar but a bare year, which reads as a rendering fault - so the whole
        // bar waits until there is something real to show.
        var copyright = string.IsNullOrWhiteSpace(brand.Copyright)
            ? (string.IsNullOrWhiteSpace(brand.BrandName)
                ? string.Empty
                : $"© {DateTime.UtcNow.Year} {brand.BrandName}")
            : brand.Copyright!;

        if (string.IsNullOrWhiteSpace(copyright) &&
            brand.SocialLinks.Count == 0 &&
            string.IsNullOrWhiteSpace(brand.ComplianceNote) &&
            string.IsNullOrWhiteSpace(brand.UnsubscribeUrl))
        {
            return;
        }

        var links = brand.SocialLinks
            .Where(l => !string.IsNullOrWhiteSpace(l.Url) && l.Url != "#")
            .ToList();

        var pills = new StringBuilder();
        foreach (var link in links)
        {
            var label = string.IsNullOrWhiteSpace(link.Label) ? link.Url : link.Label;
            pills.Append(
                $"""
                <td style="padding:0 4px">
                  <a href="{Esc(link.Url)}" target="_blank"
                     style="display:inline-block;padding:6px 14px;border-radius:999px;
                            background:rgba(255,255,255,0.12);color:#ffffff;
                            font-family:Arial,Helvetica,sans-serif;font-size:12px;
                            text-decoration:none;white-space:nowrap">{Esc(label)}</a>
                </td>
                """);
        }

        var socialBlock = links.Count == 0
            ? string.Empty
            : $"""
               <tr><td align="center" style="padding-top:14px">
                 <table role="presentation" cellpadding="0" cellspacing="0" border="0" align="center">
                   <tr>{pills}</tr>
                 </table>
               </td></tr>
               """;

        var unsubscribe = string.IsNullOrWhiteSpace(brand.UnsubscribeUrl)
            ? string.Empty
            : $"""
               <tr><td align="center" style="padding-top:12px;
                        font-family:Arial,Helvetica,sans-serif;font-size:11px">
                 <a href="{Esc(brand.UnsubscribeUrl)}"
                    style="color:#8fa3cc;text-decoration:underline">
                   {(dir == "rtl" ? "إلغاء الاشتراك" : "Unsubscribe")}
                 </a>
               </td></tr>
               """;

        var compliance = string.IsNullOrWhiteSpace(brand.ComplianceNote)
            ? string.Empty
            : $"""
               <tr><td align="center" style="padding-top:8px;
                        font-family:Arial,Helvetica,sans-serif;font-size:11px;color:#8fa3cc">
                 {Esc(brand.ComplianceNote)}
               </td></tr>
               """;

        // Stacked and centred rather than three side-by-side columns: the columns
        // sized themselves differently per language, which is why the Arabic and
        // English versions looked unrelated.
        sb.Append(
            $"""
            <tr><td dir="{dir}" style="padding:22px 24px;background:#0b1220">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                <tr><td align="center"
                        style="font-family:Arial,Helvetica,sans-serif;font-size:11px;color:#8fa3cc">
                  {Esc(copyright)}
                </td></tr>
                {socialBlock}
                {compliance}
                {unsubscribe}
              </table>
            </td></tr>
            """);
    }

    /// <summary>
    /// Escapes the text, then turns its line breaks into markup so a multi-line
    /// note keeps its shape in the email.
    /// </summary>
    private static string LineBreaks(string? value) =>
        Esc(value).Replace("\r\n", "<br />").Replace("\n", "<br />");

    private static string Esc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

/// <summary>
/// Branding saved once and reused by every issue. Blank members are simply not
/// rendered.
/// </summary>
public sealed class NewsletterBranding
{
    public string? BrandName { get; init; }

    /// <summary>Content id of the inline logo attachment, when one is set.</summary>
    public string? LogoCid { get; init; }

    public string? AccentColour { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? FooterText { get; init; }
    public string? UnsubscribeUrl { get; init; }
    public string? Copyright { get; init; }
    public string? ComplianceNote { get; init; }

    public IReadOnlyList<NewsletterSocialLink> SocialLinks { get; init; } = [];
}

public sealed record NewsletterSocialLink(string Label, string Url);

public sealed record NewsSectionModel(string Heading, IReadOnlyList<NewsItemModel> Items);

public sealed record NewsItemModel(
    string Title,
    string? Summary,
    string? Date,
    string? LinkUrl,
    string? ImagePath);

/// <summary>Reads the campaign's section bag into the render models.</summary>
public static class NewsletterModelReader
{
    /// <param name="contentRoot">
    /// The content item's root object. The section bag is welded to the item
    /// itself rather than nested inside the Campaign part, so it has to be read
    /// from the root — looking under the part finds nothing.
    /// </param>
    public static List<NewsSectionModel> ReadSections(JsonObject contentRoot)
    {
        var sections = new List<NewsSectionModel>();

        if (contentRoot["Sections"]?["ContentItems"] is not JsonArray sectionNodes)
        {
            return sections;
        }

        foreach (var sectionNode in sectionNodes.OfType<JsonObject>())
        {
            var heading = sectionNode["NewsSection"]?["Heading"]?["Text"]?.GetValue<string>() ?? string.Empty;
            var items = new List<NewsItemModel>();

            if (sectionNode["Items"]?["ContentItems"] is JsonArray itemNodes)
            {
                foreach (var itemNode in itemNodes.OfType<JsonObject>())
                {
                    var part = itemNode["NewsItem"] as JsonObject;
                    items.Add(new NewsItemModel(
                        Title: itemNode["DisplayText"]?.GetValue<string>() ?? string.Empty,
                        Summary: part?["Summary"]?["Text"]?.GetValue<string>(),
                        Date: part?["Date"]?["Text"]?.GetValue<string>(),
                        LinkUrl: part?["Link"]?["Url"]?.GetValue<string>(),
                        ImagePath: FirstPath(part?["Image"])));
                }
            }

            sections.Add(new NewsSectionModel(heading, items));
        }

        return sections;
    }

    private static string? FirstPath(JsonNode? mediaField) =>
        mediaField?["Paths"] is JsonArray paths && paths.Count > 0
            ? paths[0]?.GetValue<string>()
            : null;
}
