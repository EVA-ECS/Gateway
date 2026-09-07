using Chat.Contracts.Events;
using FluentAssertions;
using Gateway.Controllers;
using Gateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Gateway.Tests.Controllers;

public class ChatControllerTests
{
    private readonly Mock<IChatManagerService> _chatManagerMock;
    private readonly Mock<IUserPresenceStore> _presenceStoreMock;
    private readonly ChatController _sut;

    public ChatControllerTests()
    {
        _chatManagerMock = new Mock<IChatManagerService>();
        _presenceStoreMock = new Mock<IUserPresenceStore>();

        _sut = new ChatController(_chatManagerMock.Object, _presenceStoreMock.Object);
    }

    private void SetupControllerContext(string? userId = "user123", bool isWebSocketRequest = false, WebSocket? mockWebSocket = null)
    {
        var claims = string.IsNullOrEmpty(userId) ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, string.IsNullOrEmpty(userId) ? null : "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        var mockHttpContext = new Mock<HttpContext>();
        var mockResponse = new Mock<HttpResponse>();
        var mockRequest = new Mock<HttpRequest>();
        var mockWsManager = new Mock<WebSocketManager>();

        mockResponse.SetupProperty(r => r.StatusCode, 200);
        mockHttpContext.Setup(c => c.Response).Returns(mockResponse.Object);

        var headerDict = new HeaderDictionary { { "Authorization", "Bearer test-token" } };
        mockRequest.Setup(r => r.Headers).Returns(headerDict);
        mockHttpContext.Setup(c => c.Request).Returns(mockRequest.Object);

        mockHttpContext.Setup(c => c.User).Returns(claimsPrincipal);
        mockHttpContext.Setup(c => c.RequestAborted).Returns(CancellationToken.None);

        mockWsManager.Setup(w => w.IsWebSocketRequest).Returns(isWebSocketRequest);
        if (mockWebSocket != null)
        {
            mockWsManager.Setup(w => w.AcceptWebSocketAsync()).ReturnsAsync(mockWebSocket);
            mockWsManager.Setup(w => w.AcceptWebSocketAsync(It.IsAny<string>())).ReturnsAsync(mockWebSocket);
        }
        mockHttpContext.Setup(c => c.WebSockets).Returns(mockWsManager.Object);

        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = mockHttpContext.Object
        };
    }

    [Fact]
    public void HealthCheck_ShouldReturnOk()
    {
        var result = _sut.HealthCheck() as OkObjectResult;
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
    }

    [Fact]
    public void TestAuth_ShouldReturnTokenData()
    {
        SetupControllerContext("test-user");
        var result = _sut.TestAuth() as OkObjectResult;
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendMessage_WithValidUser_ShouldReturnOk()
    {
        SetupControllerContext("user-1");
        var request = new ChatMessageRequest("user-1", "target-1", "Hello");

        var result = await _sut.SendMessage(request) as OkObjectResult;

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
        _chatManagerMock.Verify(x => x.ProcessAndSendAsync("user-1", "target-1", "Hello"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_WithoutUser_ShouldReturnUnauthorized()
    {
        SetupControllerContext(userId: null);
        var request = new ChatMessageRequest("", "target-1", "Hello");

        var result = await _sut.SendMessage(request);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task ConnectWebSocket_NotAWebSocketRequest_Returns400()
    {
        SetupControllerContext(isWebSocketRequest: false);

        await _sut.ConnectWebSocket();

        _sut.HttpContext.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ConnectWebSocket_Unauthorized_Returns401()
    {
        SetupControllerContext(userId: null, isWebSocketRequest: true);

        await _sut.ConnectWebSocket();

        _sut.HttpContext.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task ConnectWebSocket_ValidRequest_HandlesMessagesAndCloses()
    {
        var mockWebSocket = new Mock<WebSocket>();
        mockWebSocket.SetupGet(x => x.State).Returns(WebSocketState.Open);

        SetupControllerContext(userId: "user123", isWebSocketRequest: true, mockWebSocket: mockWebSocket.Object);

        var heartbeatJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Type = "presence.heartbeat" }));
        var chatJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { TargetId = "target1", Text = "Hello" }));

        var callCount = 0;
        mockWebSocket.Setup(x => x.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<ArraySegment<byte>, CancellationToken>((buffer, token) =>
            {
                if (callCount == 0) Array.Copy(heartbeatJson, buffer.Array!, heartbeatJson.Length);
                else if (callCount == 1) Array.Copy(chatJson, buffer.Array!, chatJson.Length);
                callCount++;
            })
            .ReturnsAsync(() =>
            {
                if (callCount == 1) return new WebSocketReceiveResult(heartbeatJson.Length, WebSocketMessageType.Text, true);
                if (callCount == 2) return new WebSocketReceiveResult(chatJson.Length, WebSocketMessageType.Text, true);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true); // Schleife beenden
            });

        await _sut.ConnectWebSocket();

        _presenceStoreMock.Verify(x => x.SetOnlineAsync("user123", It.IsAny<CancellationToken>()), Times.Once);
        _presenceStoreMock.Verify(x => x.RefreshAsync("user123", It.IsAny<CancellationToken>()), Times.Once);
        _chatManagerMock.Verify(x => x.ProcessAndSendAsync("user123", "target1", "Hello"), Times.Once);

        mockWebSocket.Verify(x => x.CloseAsync(WebSocketCloseStatus.NormalClosure, "Verbindung beendet", It.IsAny<CancellationToken>()), Times.Once);
    }
}