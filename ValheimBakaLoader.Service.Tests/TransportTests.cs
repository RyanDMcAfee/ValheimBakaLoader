using System.Net;
using System.Net.WebSockets;
using Newtonsoft.Json.Linq;

namespace ValheimBakaLoader.Service.Tests;

/// <summary>
/// The WebSocket is the page's Native block with the window taken out, so what these hold
/// is the exact envelope the page's pending map and listener map read.
/// </summary>
public class TransportTests
{
    [Fact]
    public async Task A_call_round_trips_through_the_bridge_in_the_reply_shape()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var socket = await host.OpenSocketAsync(await host.SessionCookieAsync());

        await ServiceHost.SendAsync(socket, """{"id":7,"method":"echo","params":{"a":1,"b":"two"}}""");
        var reply = await ServiceHost.ReceiveAsync(socket);

        Assert.Equal(
            new[] { "id", "ok", "result", "error", "errorId", "errorParams" },
            reply.Properties().Select(p => p.Name).ToArray());
        Assert.Equal(7, reply.Value<long>("id"));
        Assert.True(reply.Value<bool>("ok"));
        Assert.Equal(1, reply["result"]!.Value<int>("a"));
        Assert.Equal("two", reply["result"]!.Value<string>("b"));
        Assert.Equal(JTokenType.Null, reply["error"]!.Type);
        Assert.Equal(JTokenType.Null, reply["errorId"]!.Type);
        Assert.Equal(JTokenType.Null, reply["errorParams"]!.Type);
    }

    [Fact]
    public async Task An_unknown_method_answers_the_sentence_the_window_answers()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var socket = await host.OpenSocketAsync(await host.SessionCookieAsync());

        await ServiceHost.SendAsync(socket, """{"id":1,"method":"nope.nothing","params":{}}""");
        var reply = await ServiceHost.ReceiveAsync(socket);

        Assert.Equal(1, reply.Value<long>("id"));
        Assert.False(reply.Value<bool>("ok"));
        Assert.Equal("Unknown method: nope.nothing", reply.Value<string>("error"));
        Assert.Equal(JTokenType.Null, reply["errorId"]!.Type);
    }

    [Fact]
    public async Task A_throwing_method_answers_the_message_and_its_id()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var socket = await host.OpenSocketAsync(await host.SessionCookieAsync());

        await ServiceHost.SendAsync(socket, """{"id":2,"method":"throws","params":{}}""");
        var reply = await ServiceHost.ReceiveAsync(socket);

        Assert.False(reply.Value<bool>("ok"));
        Assert.Equal("The thing went wrong: x.", reply.Value<string>("error"));
        Assert.Equal("fake.boom", reply.Value<string>("errorId"));
        Assert.Equal("x", reply["errorParams"]!.Value<string>("name"));

        // A throw with no id travels as the sentence alone, errorId null, as it always has.
        await ServiceHost.SendAsync(socket, """{"id":3,"method":"throwsPlain","params":{}}""");
        var plain = await ServiceHost.ReceiveAsync(socket);
        Assert.False(plain.Value<bool>("ok"));
        Assert.Equal("plain failure", plain.Value<string>("error"));
        Assert.Equal(JTokenType.Null, plain["errorId"]!.Type);
        Assert.Equal(JTokenType.Null, plain["errorParams"]!.Type);
    }

    [Fact]
    public async Task A_push_reaches_every_open_socket()
    {
        await using var host = await new ServiceHost().StartAsync();
        var cookie = await host.SessionCookieAsync();
        using var one = await host.OpenSocketAsync(cookie);
        using var two = await host.OpenSocketAsync(cookie);

        host.Bridge.Push("hearth.state", new { state = "running", pid = 42 });

        foreach (var socket in new[] { one, two })
        {
            var push = await ServiceHost.ReceiveAsync(socket);
            Assert.Equal(new[] { "event", "data" }, push.Properties().Select(p => p.Name).ToArray());
            Assert.Equal("hearth.state", push.Value<string>("event"));
            Assert.Equal("running", push["data"]!.Value<string>("state"));
            Assert.Equal(42, push["data"]!.Value<int>("pid"));
        }
    }

    [Fact]
    public async Task Calls_from_two_sockets_run_one_at_a_time()
    {
        await using var host = await new ServiceHost().StartAsync();
        var cookie = await host.SessionCookieAsync();
        using var one = await host.OpenSocketAsync(cookie);
        using var two = await host.OpenSocketAsync(cookie);

        for (var n = 0; n < 3; n++)
        {
            await ServiceHost.SendAsync(one, "{\"id\":" + n + ",\"method\":\"slow\",\"params\":{\"n\":" + n + "}}");
            await ServiceHost.SendAsync(two, "{\"id\":" + (n + 10) + ",\"method\":\"slow\",\"params\":{\"n\":" + (n + 10) + "}}");
        }

        var ids = new List<long>();
        for (var n = 0; n < 3; n++)
        {
            ids.Add((await ServiceHost.ReceiveAsync(one)).Value<long>("id"));
            ids.Add((await ServiceHost.ReceiveAsync(two)).Value<long>("id"));
        }

        Assert.Equal(6, host.Bridge.Calls);
        Assert.Equal(1, host.Bridge.MaxInFlight);
        Assert.Equal(new long[] { 0, 1, 2, 10, 11, 12 }, ids.OrderBy(i => i).ToArray());
    }

    [Fact]
    public async Task A_window_message_is_dropped_and_a_call_without_an_id_is_ignored()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var socket = await host.OpenSocketAsync(await host.SessionCookieAsync());

        await ServiceHost.SendAsync(socket, """{"id":1,"method":"win.minimize","params":{}}""");
        await ServiceHost.SendAsync(socket, """{"method":"echo","params":{}}""");
        await ServiceHost.SendAsync(socket, """{"id":2,"method":"echo","params":{"after":true}}""");

        var reply = await ServiceHost.ReceiveAsync(socket);
        Assert.Equal(2, reply.Value<long>("id"));
        Assert.Equal(1, host.Bridge.Calls);
    }

    [Fact]
    public async Task A_socket_from_another_origin_is_refused()
    {
        await using var host = await new ServiceHost().StartAsync();
        var cookie = await host.SessionCookieAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => host.OpenSocketAsync(cookie, "http://evil.example"));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task A_socket_without_a_session_is_sent_to_the_door()
    {
        await using var host = await new ServiceHost().StartAsync();
        using var client = host.Client();

        using var response = await client.GetAsync("/ws");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.ToString());
    }
}
