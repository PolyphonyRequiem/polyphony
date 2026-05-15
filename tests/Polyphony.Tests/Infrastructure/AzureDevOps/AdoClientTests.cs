using System.Net;
using System.Text;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

public sealed class AdoClientTests
{
    private static IPolyphonyAuthProvider TokenResolver(string? token) =>
        new PatAuthProvider(new AdoTokenResolver(envReader: _ => token, precedence: [AdoTokenResolver.AzureDevOpsExtPatVar]));

    private const string SuccessBody = """
        {
          "authenticatedUser": {
            "providerDisplayName": "Ada Lovelace",
            "id": "00000000-0000-0000-0000-000000000001"
          }
        }
        """;

    [Fact]
    public async Task GetAuthStatusAsync_NoPat_ReturnsUnauthenticatedWithRemediationHint()
    {
        var handler = StubHandler.AlwaysFail();
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver(null), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldContain("No PAT found");
        status.Detail.ShouldContain("AZURE_DEVOPS_EXT_PAT");
        status.OrganizationName.ShouldBeNull();
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAuthStatusAsync_HappyPath_ReturnsTrueWithDisplayName()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody);
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("real-pat"), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeTrue();
        status.Detail.ShouldContain("Ada Lovelace");
        status.OrganizationName.ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task GetAuthStatusAsync_SendsBasicAuthWithPatAsPassword()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody);
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("my-pat"), AdoClientPolicy.NoRetry);

        await client.GetAuthStatusAsync();

        handler.LastRequest.ShouldNotBeNull();
        var auth = handler.LastRequest!.Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Basic");
        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!));
        decoded.ShouldBe(":my-pat");
    }

    [Fact]
    public async Task GetAuthStatusAsync_HitsConnectionDataEndpoint()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, SuccessBody);
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("my-pat"), AdoClientPolicy.NoRetry);

        await client.GetAuthStatusAsync();

        handler.LastRequest!.RequestUri!.AbsoluteUri.ShouldBe(
            "https://dev.azure.com/_apis/connectionData?api-version=7.1");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task GetAuthStatusAsync_401_ReturnsPatRejected()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("bad-pat"), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldBe("Credentials rejected");
        status.OrganizationName.ShouldBeNull();
    }

    [Fact]
    public async Task GetAuthStatusAsync_403_ReturnsPatRejected()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Forbidden, "");
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("bad-pat"), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldBe("Credentials rejected");
    }

    [Fact]
    public async Task GetAuthStatusAsync_401_NotRetried()
    {
        var handler = StubHandler.Returns(HttpStatusCode.Unauthorized, "");
        using var http = new HttpClient(handler);
        // Use Default (3 attempts) to prove 401 short-circuits the loop.
        var client = new AdoClient(http, TokenResolver("bad-pat"), AdoClientPolicy.Default);

        await client.GetAuthStatusAsync();

        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAuthStatusAsync_5xx_ReturnsProbeFailedWithStatus()
    {
        var handler = StubHandler.Returns(HttpStatusCode.BadGateway, "");
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("pat"), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldContain("Probe failed");
        status.Detail.ShouldContain("502");
    }

    [Fact]
    public async Task GetAuthStatusAsync_MalformedJson_ReturnsProbeFailed()
    {
        var handler = StubHandler.Returns(HttpStatusCode.OK, "this is not json {{{");
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("pat"), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldContain("malformed JSON");
    }

    [Fact]
    public async Task GetAuthStatusAsync_HttpRequestException_ReturnsProbeFailed()
    {
        var handler = StubHandler.Throws(new HttpRequestException("name resolution failed"));
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("pat"), AdoClientPolicy.NoRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldContain("Probe failed");
        status.Detail.ShouldContain("name resolution");
    }

    [Fact]
    public async Task GetAuthStatusAsync_Timeout_ReturnsTimedOutDetail()
    {
        // Handler never completes — wait until the per-attempt timeout fires.
        var handler = StubHandler.Hangs();
        using var http = new HttpClient(handler);
        var fastTimeout = new AdoClientPolicy(
            maxAttempts: 1,
            perAttemptTimeout: TimeSpan.FromMilliseconds(75),
            initialBackoff: TimeSpan.Zero);
        var client = new AdoClient(http, TokenResolver("pat"), fastTimeout);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldBe("Timed out probing dev.azure.com");
    }

    [Fact]
    public async Task GetAuthStatusAsync_RetriesOnTimeout_UntilPolicyExhausted()
    {
        var handler = StubHandler.Hangs();
        using var http = new HttpClient(handler);
        var policy = new AdoClientPolicy(
            maxAttempts: 3,
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            initialBackoff: TimeSpan.Zero);
        var client = new AdoClient(http, TokenResolver("pat"), policy);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldBe("Timed out probing dev.azure.com");
        handler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task GetAuthStatusAsync_CallerCancellation_Propagates()
    {
        var handler = StubHandler.Hangs();
        using var http = new HttpClient(handler);
        var client = new AdoClient(http, TokenResolver("pat"), AdoClientPolicy.Default);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.GetAuthStatusAsync(cts.Token));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        using var http = new HttpClient();
        Should.Throw<ArgumentNullException>(() => new AdoClient(null!, TokenResolver("p")));
        Should.Throw<ArgumentNullException>(() => new AdoClient(http, null!));
        Should.Throw<ArgumentNullException>(() =>
            new AdoClient(http, TokenResolver("p"), policy: null!));
    }

    // ─── Test fake ──────────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public int RequestCount { get; private set; }

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        private StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public static StubHandler Returns(HttpStatusCode status, string body) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }));

        public static StubHandler Throws(Exception exception) =>
            new((_, _) => throw exception);

        public static StubHandler Hangs() =>
            new(async (_, ct) =>
            {
                // Delay until the linked CTS cancels (per-attempt timeout fires)
                // — enough to trigger the OperationCanceledException path.
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        public static StubHandler AlwaysFail() => new((_, _) =>
            throw new InvalidOperationException("handler should not be invoked"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCount++;
            return _respond(request, cancellationToken);
        }
    }
}
