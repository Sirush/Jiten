using System.Net;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>
/// Stubs outbound HTTP so reCAPTCHA siteverify (called by register/forgot-password/resend-confirmation)
/// returns success without hitting Google. Any other outbound request returns 200 with an empty body.
/// Wired as the primary handler for the default <see cref="IHttpClientFactory"/> client in the test factory.
/// </summary>
public class StubRecaptchaHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        if (url.Contains("recaptcha", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"score\":0.9}")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
