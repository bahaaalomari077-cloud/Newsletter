using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OrchardCore.Admin;
using OrchardCore.ContentManagement;
using OrchardCore.Contents;

namespace Newsletter.Admin.Newsletter;

/// <summary>
/// The Send action behind the campaign's send button.
///
/// Admin-only: the controller carries the Admin attribute and checks the same
/// content permission the editor screens use, so it is unreachable from the
/// public site even though it lives in the same application.
/// </summary>
[Admin]
[Authorize]
public sealed class CampaignAdminController : Controller
{
    private readonly CampaignSender _sender;
    private readonly IContentManager _contentManager;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<CampaignAdminController> _logger;

    public CampaignAdminController(
        CampaignSender sender,
        IContentManager contentManager,
        IAuthorizationService authorizationService,
        ILogger<CampaignAdminController> logger)
    {
        _sender = sender;
        _contentManager = contentManager;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string contentItemId)
    {
        var campaign = await _contentManager.GetAsync(contentItemId, VersionOptions.Latest);
        if (campaign is null)
        {
            return NotFound();
        }

        // Sending is an editing action on the campaign, so it takes the same
        // permission as publishing one.
        if (!await _authorizationService.AuthorizeAsync(
                User, CommonPermissions.PublishContent, campaign))
        {
            return Forbid();
        }

        var result = await _sender.SendAsync(contentItemId);

        _logger.LogInformation(
            "Campaign {Id}: sent={Sent} failed={Failed} error={Error}",
            contentItemId, result.Sent, result.Failures.Count, result.Error ?? "(none)");

        string message;
        var ok = false;

        if (result.IsFailure)
        {
            message = result.Error!;
        }
        else if (result.Failures.Count == 0)
        {
            message = $"Campaign sent to {result.Sent} recipient(s).";
            ok = true;
        }
        else
        {
            message = $"Sent to {result.Sent} recipient(s). "
                + $"{result.Failures.Count} failed: "
                + string.Join(" | ", result.Failures.Take(3));
        }

        // The button posts with fetch, so the outcome is returned to it rather
        // than raised as a notification — a notifier message is attached to the
        // redirect response, which the caller never renders.
        return Ok(new { ok, message, sent = result.Sent, failed = result.Failures.Count });
    }
}
