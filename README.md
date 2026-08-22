# Newsletter — Orchard Core CMS (Backend Only)

Headless Orchard Core 3.0 backend for a newsletter system, on .NET 10 and PostgreSQL.
Contacts, groups and campaigns with real SMTP sending, driven from the admin.

Modelled directly on the `Travotech.Cms` project (`Downloads/ترافو تيك/backend`), ported from
Orchard Core 2.1.4 / .NET 8 to 3.0.0 / .NET 10.

## Stack

| | |
|---|---|
| Orchard Core | 3.0.0 |
| .NET | 10.0 |
| Database | PostgreSQL 17 |

## Projects

| | |
|---|---|
| [src/Newsletter.Cms](src/Newsletter.Cms) | The host app: config, AutoSetup, recipe. |
| [src/Newsletter.Admin](src/Newsletter.Admin) | The module: content types, sidebar menu, sending. |

## Database

PostgreSQL 17 on this machine listens on **port 5433** (not the default 5432).

```
Host:     localhost
Port:     5433
Database: NewsletterDb
User:     postgres
Password: 123456
Prefix:   nl_        (all Orchard tables, e.g. nl__ContentItemIndex)
```

Created and provisioned automatically on first run. Settings live in
[appsettings.json](src/Newsletter.Cms/appsettings.json).

## Running

```bash
cd src/Newsletter.Cms
dotnet run
```

Port 5000 is used by the Travotech app, so this one is pinned to **5080** in
[launchSettings.json](src/Newsletter.Cms/Properties/launchSettings.json). It opens
<http://localhost:5080/admin> directly.

```
Username: admin
Password: Admin@123456
```

## Content model

Defined in code — [NewsletterMigrations.cs](src/Newsletter.Admin/Migrations/NewsletterMigrations.cs) —
not in the recipe. Migrations run on every start and are versioned per class, so adding a
field means appending an `UpdateFromN` method rather than rebuilding the database. Editor
content survives schema changes.

### Contact
`Email` (required), `Organisation`, `Subscribed` (default true).
Title is the person's name. Clearing `Subscribed` stops sending to them without deleting them.

### ContactGroup
`Members` → Contact (multiple). Title is the group name.

### Campaign
`Subject` (required), `Body` (WYSIWYG HTML), `Attachment` (media),
`Recipients` → Contact, `RecipientGroups` → ContactGroup, `Edition` (masthead line),
`FooterNote`, plus a **Sections** bag. Draftable + versionable.
Title is an internal name; the reader never sees it.

### NewsSection / NewsItem
A campaign holds sections (`Heading` — the coloured bar), and each section holds news items
(`Summary`, `Date`, `Link`, `Image` thumbnail). Both are **widgets**: they only ever exist
inside a campaign and never appear as standalone content.

This is what turns a campaign from a single message into a monthly newsletter.

### NewsletterSettings
Branding saved once and reused by every issue: `BrandName`, `Logo`, `AccentColour`, `Email`,
`Phone`, `Address`, `WebsiteUrl`, `FooterText`, `UnsubscribeUrl`, `Copyright`,
`ComplianceNote`, plus a bag of `SocialLink` items. Every field is optional — whatever is
left blank is simply not rendered.

Reachable at **Newsletter → Settings**.

Every newsletter type is **admin-only by construction**: none carries an AutoroutePart, so
they have no URL and cannot be reached from the site. They are also hidden from GraphQL — Orchard exposes
every type through the API by default and that endpoint is anonymously readable, so without
`GraphQLContentTypeSettings.Hidden` a visitor could query the contact list and read every
stored address.

## Sending

`Newsletter → Campaigns` → open a campaign → **Send email** at the bottom of the editor.

- One message **per person**, never BCC: a single message with many recipients exposes the
  whole list to everyone on it, and providers treat large BCC blocks as bulk mail.
- One failure does not stop the run; each address is attempted independently and the outcome
  reported per person.
- Unsubscribed contacts are skipped, duplicates collapsed across people + groups.
- Placeholders `{{Name}}`, `{{FirstName}}`, `{{Email}}` are substituted per recipient. They
  are HTML-encoded in the body (a contact called `Ben & Co` would otherwise break the markup)
  but not in the subject, which is plain text.
- An image attachment is embedded in the body via `cid:`; other files ride along as
  attachments. Section thumbnails travel the same way.

### The masthead and footer are on every email

Every campaign goes through
[NewsletterRenderer](src/Newsletter.Admin/Newsletter/NewsletterRenderer.cs) — a plain message
with no sections still gets the masthead (logo/brand + edition line) and the closing bar
(copyright, social pills, compliance note). Sections just add the coloured section bars and
the thumbnail/headline/summary/date rows between them.

Branding comes from the single **Newsletter → Settings** item, so it is typed once rather
than on every issue. That item is created automatically by a migration with a fixed id
(`newslettersettings00000000`) — the sender looks it up by that id, so an admin-created
second item would carry a random id and never be read.

Each piece is skipped when it has nothing to show: no brand name, logo or edition means no
masthead row, rather than an empty bar that reads as a broken email. The accent rule at the
very top always renders.

Arabic anywhere in the subject or body flips the whole layout to **RTL** — including
mirroring the thumbnail column, since `dir` on the row table is what actually reorders
cells (`align` alone only moves text inside a cell).

The markup is tables with inline styles and fixed pixel widths. Email clients are not
browsers: Outlook renders through Word and Gmail strips `<style>` blocks, so flexbox, grid
and class selectors are deliberately absent.

### SMTP

Configured and **verified sending** — a test campaign delivered successfully
(`sent: 1, failed: 0`) through Gmail SMTP.

Settings live under **Configuration → Email**. Gmail needs the full address as the user name
(not a display name) and a 16-character **App Password** with the spaces removed — an
account password returns `535 BadCredentials`.

## Admin menu

[NewsletterAdminMenu.cs](src/Newsletter.Admin/Newsletter/NewsletterAdminMenu.cs) adds the
**Newsletter** group to the sidebar with Campaigns / Contacts / Groups / Settings. Each entry
links to the content list pre-filtered to one type.

The types are deliberately **not** `Listable()`, which keeps them out of the shared
Content Items list — they are a separate tool, not site content. A type-filtered list works
regardless of that flag, so the menu still reaches them.

## AutoSetup

The tenant provisions itself headlessly — no setup wizard. Two pieces make this work:

1. `.AddSetupFeatures("OrchardCore.AutoSetup")` in [Program.cs](src/Newsletter.Cms/Program.cs) —
   without this the module never activates and you get the setup screen.
2. The `OrchardCore:OrchardCore_AutoSetup` section in `appsettings.json`.

Setup fires on the **first HTTP request** after startup, not at startup itself.

### Re-running setup from scratch

```bash
# stop the app first
rm -rf src/Newsletter.Cms/App_Data
psql -h localhost -p 5433 -U postgres -c 'DROP DATABASE "NewsletterDb";'
psql -h localhost -p 5433 -U postgres -c 'CREATE DATABASE "NewsletterDb" WITH ENCODING '"'"'UTF8'"'"';'
```

## Notes for changing this

- **Themes are required even headless.** Without `OrchardCore.Themes` + `TheAdmin` the admin
  renders as an unstyled fragment (no `<html>`, no CSS) — every page looks broken in the
  browser while still returning HTTP 200. The recipe step name is `"Themes"` (capital T) with
  an `"Admin"` key; a lowercase `"themes"` step is silently ignored. The value lands in
  `SiteSettings.Properties.CurrentAdminThemeName`.
- **The send view is a shape, not a form.** It renders inside the editor's own `<form>` and
  HTML forbids nested forms, so the button posts via `fetch` and reuses the editor's
  antiforgery token.
- **`ResultError` changed in OC 3.** It exposes `Name` / `Message`; the 2.1.4 code that did
  `Errors.SelectMany(e => e.Value)` does not compile against 3.0.

## Known warnings

`dotnet build` reports NU1902/NU1903 advisories for `AngleSharp` 1.4.0 and
`SQLitePCLRaw.lib.e_sqlite3` 2.1.10, both transitive from Orchard Core 3.0.0. SQLite is not
used by this configuration.
