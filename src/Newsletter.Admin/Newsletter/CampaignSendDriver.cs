using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Display.ContentDisplay;
using OrchardCore.DisplayManagement.Handlers;
using OrchardCore.DisplayManagement.Views;

namespace Newsletter.Admin.Newsletter;

/// <summary>
/// Puts the Send button on the campaign editor.
///
/// The button posts to <see cref="CampaignAdminController.Send"/>. It is only
/// rendered for a saved campaign, since an unsaved one has no id to send.
/// </summary>
public sealed class CampaignSendDriver : ContentDisplayDriver
{
    public override IDisplayResult? Edit(ContentItem model, BuildEditorContext context)
    {
        if (!string.Equals(model.ContentType, "Campaign", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrEmpty(model.ContentItemId))
        {
            return null;
        }

        return Initialize<CampaignSendViewModel>("CampaignSend_Edit", m =>
            {
                m.ContentItemId = model.ContentItemId;
            })
            .Location("Content:after");
    }
}

/// <remarks>
/// Deliberately not sealed and with virtual members: shapes are wrapped in a
/// Castle DynamicProxy subclass, which cannot derive from a sealed type.
/// </remarks>
public class CampaignSendViewModel
{
    public virtual string ContentItemId { get; set; } = string.Empty;
}
