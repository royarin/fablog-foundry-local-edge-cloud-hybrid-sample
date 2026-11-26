using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FabLog.FabPad;
using Microsoft.Extensions.AI;

namespace FabLog.DemoTests;

public class CloudClientTests
{
    sealed class Airlock(bool onNetwork) : IAirlock
    {
        public bool PadOnNetwork => onNetwork;
    }

    [Theory]
    [InlineData("FoundryLocal", "/v1", "/v1/chat/completions")]
    [InlineData("AzureOpenAI", "/openai/v1", "/openai/deployments/local-model/chat/completions")]
    public async Task Provider_uses_expected_endpoint_and_model(string provider, string endpointPath, string expectedPath)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        socket.Stop();
        listener.Start();

        using var client = new CloudClient(new T3Options
        {
            Provider = provider,
            Endpoint = $"http://127.0.0.1:{port}{endpointPath}",
            Deployment = "local-model",
            ApiKey = "not-needed",
        }, new Airlock(true));

        var call = client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(context.Request.InputStream);
        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
        var path = context.Request.Url!.AbsolutePath;
        var model = body.RootElement.TryGetProperty("model", out var modelProperty) ? modelProperty.GetString() : null;
        var response = Encoding.UTF8.GetBytes("""
            {"id":"test","object":"chat.completion","created":1,"model":"local-model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello back"},"finish_reason":"stop"}]}
            """);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = response.Length;
        await context.Response.OutputStream.WriteAsync(response);
        context.Response.Close();

        Assert.Equal(expectedPath, path);
        if (provider == "FoundryLocal") Assert.Equal("local-model", model);
        Assert.Equal("Hello back", (await call).Text);
    }

    [Fact]
    public async Task Foundry_local_still_obeys_the_airlock()
    {
        using var client = new CloudClient(new T3Options
        {
            Provider = "FoundryLocal",
            Endpoint = "http://127.0.0.1:12345/v1",
            Deployment = "local-model",
            ApiKey = "not-needed",
        }, new Airlock(false));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));
        Assert.False(client.CloudReachable);
    }
}
