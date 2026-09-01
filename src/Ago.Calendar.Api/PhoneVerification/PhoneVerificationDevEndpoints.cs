using Ago.Calendar.Module.PhoneVerification;

namespace Ago.Calendar.Api.PhoneVerification;

/// <summary>
/// `20-10`'s own nice-to-have, named in its backlog file's Open questions: a way for a person to click
/// through the public booking widget by hand on the live demo cluster without a real SMS ever arriving.
/// <c>FakePhoneVerificationSender</c>'s own structured log line is the floor and is what the automated
/// Done-when tests rely on; this route is the cheap addition on top of it, mirroring
/// <see cref="Ago.Calendar.Api.Provisioning.DevProvisioningEndpoints"/>'s own gate (outside Production
/// only) rather than a permission - the identical reasoning that file already states applies here
/// unchanged: a stranger reading back a verification code they themselves just requested learns nothing
/// a real SMS would not have told them anyway, but the route still has no business existing where a real
/// deployment runs.
///
/// <para><b>Why this reads <see cref="FakePhoneVerificationSender"/>'s own concrete type rather than
/// widening <see cref="Ago.Calendar.Application.Abstractions.IPhoneVerificationSender"/> itself.</b> The
/// port is what <c>Application</c> depends on, and nothing about "read back the last code" belongs on a
/// port a real vendor client will one day implement instead - a real gateway has no "last code" to hand
/// back over HTTP. Resolving the concrete singleton type here keeps the dev-only capability entirely
/// inside <c>Ago.Calendar.Api</c>/<c>Ago.Calendar.Module</c>, with nothing for <c>Application</c> or a
/// real sender to even see.</para>
/// </summary>
public static class PhoneVerificationDevEndpoints
{
    public static IEndpointRouteBuilder MapPhoneVerificationDevEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/dev/phone-verifications/last-code", HandleLastCodeAsync)
            .WithName("GetLastFakePhoneVerificationCode")
            .AllowAnonymous();

        return app;
    }

    private static IResult HandleLastCodeAsync(string phone, FakePhoneVerificationSender sender)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return Results.BadRequest();
        }

        return sender.TryGetLastCode(phone, out var code)
            ? Results.Ok(new { code })
            : Results.NotFound();
    }
}
