using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Chat.Contracts.Events;
using Gateway.Controllers;
using Gateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gateway.Tests;

public class ChatIntegrationTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid MessageId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly string Encrypted = JsonSerializer.Serialize(new {
        version = 1, senderId = Alice, recipientId = Bob, senderKeyId = "a", recipientKeyId = "b", iv = "test-iv", ciphertext = "test-ciphertext"
    });

    private static DefaultHttpContext Context(bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        if (authenticated) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Alice.ToString())], "test"));
        context.Request.Headers.Authorization = "Bearer test-user-token";
        return context;
    }

    [Fact]
    public async Task History_UsesUserTokenAndBothParticipantDirections()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("test-user-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("test-publishable", request.Headers.GetValues("apikey").Single());
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Assert.Contains($"and(sender_id.eq.{Alice},receiver_id.eq.{Bob})", uri);
            Assert.Contains($"and(sender_id.eq.{Bob},receiver_id.eq.{Alice})", uri);
            Assert.Contains("rooms.is_group=eq.false", uri);
            return Response([Row(MessageId)]);
        });
        var page = await Store(handler).ReadAsync(Alice, Bob, "test-user-token", 50, null, default);
        Assert.Single(page.Messages);
        Assert.Equal(Encrypted, page.Messages[0].Ciphertext);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task History_UsesTimestampAndIdCursor()
    {
        var calls = 0;
        var handler = new Handler(request =>
        {
            if (++calls == 1) return Response([Row(MessageId), Row(Guid.NewGuid())]);
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Assert.Contains("created_at.lt.", uri);
            Assert.Contains($"id.lt.{MessageId}", uri);
            return Response([]);
        });
        var store = Store(handler);
        var first = await store.ReadAsync(Alice, Bob, "test-user-token", 1, null, default);
        Assert.NotNull(first.NextCursor);
        var second = await store.ReadAsync(Alice, Bob, "test-user-token", 1, first.NextCursor, default);
        Assert.Empty(second.Messages);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(50, "not-a-cursor")]
    public async Task History_RejectsInvalidPaging(int limit, string? cursor)
    {
        var handler = new Handler(_ => throw new Exception("Must not send a request"));
        await Assert.ThrowsAsync<ArgumentException>(() => Store(handler).ReadAsync(Alice, Bob, "token", limit, cursor, default));
    }

    [Fact]
    public async Task History_RequiresAuthenticatedUser()
    {
        var controller = new ChatHistoryController(Store(new Handler(_ => throw new Exception("No request allowed"))))
            { ControllerContext = new ControllerContext { HttpContext = Context(false) } };
        Assert.IsType<UnauthorizedResult>(await controller.Get(Bob));
    }

    [Fact]
    public async Task History_DatabaseFailureIsNotAnEmptyConversation()
    {
        var controller = new ChatHistoryController(Store(new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            { ControllerContext = new ControllerContext { HttpContext = Context() } };
        Assert.Equal(502, Assert.IsType<ObjectResult>(await controller.Get(Bob)).StatusCode);
    }

    [Fact]
    public async Task Registry_ForwardsOnlyToMatchingUser()
    {
        var registry = new WebSocketConnectionRegistry();
        var a = new Socket(); var b = new Socket();
        await registry.RegisterAsync(Alice.ToString(), a, default);
        await registry.RegisterAsync(Bob.ToString(), b, default);
        Assert.True(await registry.SendAsync(Bob.ToString(), Encoding.UTF8.GetBytes("hello"), default));
        Assert.Empty(a.Sent); Assert.Single(b.Sent);
        Assert.False(await registry.SendAsync(Guid.NewGuid().ToString(), new byte[1], default));
    }

    [Fact]
    public async Task Registry_ReplacesOldTabWithExplicitCloseCode()
    {
        var registry = new WebSocketConnectionRegistry();
        var previous = new Socket(); var next = new Socket();
        await registry.RegisterAsync(Alice.ToString(), previous, default);
        await registry.RegisterAsync(Alice.ToString(), next, default);
        Assert.Equal((WebSocketCloseStatus)4001, previous.CloseStatus);
        Assert.False(registry.Unregister(Alice.ToString(), previous));
        Assert.False(await registry.SendAsync(Alice.ToString(), new byte[1], default, previous));
        Assert.True(await registry.SendAsync(Alice.ToString(), new byte[1], default, next));
    }

    [Fact]
    public async Task Registry_SerializesConcurrentSocketSends()
    {
        var registry = new WebSocketConnectionRegistry(); var socket = new Socket();
        await registry.RegisterAsync(Alice.ToString(), socket, default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => registry.SendAsync(Alice.ToString(), new byte[1], default)));
        Assert.Equal(1, socket.MaximumConcurrentSends);
        Assert.Equal(8, socket.Sent.Count);
    }

    [Fact]
    public async Task WebSocket_AcknowledgesOnlyAfterPublishing()
    {
        var published = new TaskCompletionSource<ChatMessageEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager((_, _, _, _) => { called.SetResult(); return published.Task; });
        var socket = new Socket([Frame(Encrypted)]);
        var controller = Controller(manager, socket);
        var running = controller.ConnectWebSocket();
        await called.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(socket.Sent);
        published.SetResult(new ChatMessageEvent(MessageId.ToString(), Alice.ToString(), Bob.ToString(), Encrypted, DateTime.UtcNow));
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        using var reply = JsonDocument.Parse(Assert.Single(socket.Sent));
        Assert.Equal("published", reply.RootElement.GetProperty("status").GetString());
        Assert.Equal("request-1", reply.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(MessageId.ToString(), reply.RootElement.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task WebSocket_BrokerFailureDoesNotSendSuccess()
    {
        var socket = new Socket([Frame(Encrypted)]);
        var controller = Controller(new Manager((_, _, _, _) => throw new IOException("broker unavailable")), socket);
        await controller.ConnectWebSocket();
        using var reply = JsonDocument.Parse(Assert.Single(socket.Sent));
        Assert.Equal("error", reply.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WebSocket_RejectsPayloadWithAnotherSender()
    {
        var socket = new Socket([Frame(Encrypted.Replace(Alice.ToString(), Guid.NewGuid().ToString()))]);
        var controller = Controller(new Manager((_, _, _, _) => throw new Exception("Must not publish")), socket);
        await controller.ConnectWebSocket();
        using var reply = JsonDocument.Parse(Assert.Single(socket.Sent));
        Assert.Equal("invalid_message", reply.RootElement.GetProperty("code").GetString());
    }

    private static ChatController Controller(IChatManagerService manager, Socket socket)
    {
        var context = Context();
        context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
        return new ChatController(manager, new Presence(), new WebSocketConnectionRegistry())
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }
    private static string Frame(string ciphertext) => JsonSerializer.Serialize(new { targetId = Bob, text = ciphertext, requestId = "request-1" });
    private static object Row(Guid id) => new { id, sender_id = Alice, receiver_id = Bob, content = Encrypted, created_at = "2026-09-18T12:00:00Z" };
    private static HttpResponseMessage Response(object[] rows) => new(HttpStatusCode.OK) { Content = JsonContent.Create(rows) };
    private static SupabaseChatHistoryStore Store(Handler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/rest/v1/") };
        client.DefaultRequestHeaders.Add("apikey", "test-publishable");
        return new SupabaseChatHistoryStore(client);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
    private sealed class Manager(Func<string, string, string, CancellationToken, Task<ChatMessageEvent>> publish) : IChatManagerService
    {
        public Task<ChatMessageEvent> ProcessAndSendAsync(string senderId, string targetId, string text, CancellationToken cancellationToken = default) => publish(senderId, targetId, text, cancellationToken);
    }
    private sealed class Presence : IUserPresenceStore
    {
        public Task SetOnlineAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class SocketFeature(Socket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult<WebSocket>(socket);
    }
    private sealed class Socket(IEnumerable<string>? frames = null) : WebSocket
    {
        private readonly Queue<string> _frames = new(frames ?? []);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private int _sends;
        public int MaximumConcurrentSends { get; private set; }
        public List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { _closeStatus = closeStatus; _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_frames.Count == 0) { _state = WebSocketState.CloseReceived; return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)); }
            var bytes = Encoding.UTF8.GetBytes(_frames.Dequeue());
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            MaximumConcurrentSends = Math.Max(MaximumConcurrentSends, Interlocked.Increment(ref _sends));
            await Task.Delay(5, cancellationToken);
            Sent.Add(Encoding.UTF8.GetString(buffer));
            Interlocked.Decrement(ref _sends);
        }
    }
}
