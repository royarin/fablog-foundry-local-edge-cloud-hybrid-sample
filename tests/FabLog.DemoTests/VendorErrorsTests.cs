using System.Net;
using System.Net.Sockets;

namespace FabLog.DemoTests;

public class VendorErrorsTests
{
    [Theory]
    [InlineData(DemoMode.CloudOnly)]
    [InlineData(DemoMode.Hybrid)]
    public async Task Retry_failures_show_attempt_count_without_raw_exception_details(DemoMode mode)
    {
        var error = new AggregateException("Retry failed after 4 tries.",
            Enumerable.Range(0, 4).Select(_ => new HttpRequestException(
                "No such host is known. (vendor.example:443)",
                new SocketException((int)SocketError.HostNotFound))));
        var rig = new Rig(cloud: new FakeChat((_, _) => throw error));

        var result = await rig.LogAsync(DeviceReplies.SeedNote, mode);

        Assert.Equal("Vendor unreachable after 4 attempts. Check the connection and try again.", result.Error);
        Assert.Equal(mode == DemoMode.CloudOnly, result.Failed);
        Assert.Equal(mode == DemoMode.Hybrid, result.Degraded);
    }

    [Fact]
    public void Wrapped_timeout_is_not_reported_as_a_raw_transport_error()
    {
        var error = new HttpRequestException("Transport failed.",
            new TimeoutException("Internal timeout details."));

        Assert.Equal("Vendor request timed out or was canceled. Try again.", VendorErrors.Describe(error));
    }

    [Fact]
    public void Configuration_instructions_are_preserved()
    {
        var error = new InvalidOperationException(NotConfiguredChatClient.Message);

        Assert.Equal(NotConfiguredChatClient.Message, VendorErrors.Describe(error));
    }

    [Fact]
    public void Http_error_response_is_not_reported_as_an_unreachable_vendor()
    {
        var error = new HttpRequestException("Raw HTTP response body.", null, HttpStatusCode.TooManyRequests);

        Assert.Equal("Vendor request failed. Check the service configuration and try again.",
            VendorErrors.Describe(error));
    }
}
