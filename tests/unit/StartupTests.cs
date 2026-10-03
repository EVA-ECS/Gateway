using System.Diagnostics;
using Gateway.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Swashbuckle.AspNetCore.SwaggerGen;
using MassTransit;
using IServer = Microsoft.AspNetCore.Hosting.Server.IServer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IsolatedStartup.Tests;

// Run the real composition root with in-memory web hosting and disconnected infrastructure.
// No application source, real credentials, network clients or listening ports are required.
public sealed class StartupTests
{
    [Fact]
    public async Task CompositionRootBuildsValidServicesWithoutStartingExternalInfrastructure()
    {
        var application = typeof(ChatController).Assembly;
        var observed = false;
        var database = new Mock<IDatabase>();
        var subscriber = new Mock<ISubscriber>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        redis.Setup(x => x.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        using var subscriptions = new Subscriptions();
        using var listeners = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name != "Microsoft.Extensions.Hosting") return;
            subscriptions.Add(listener.Subscribe(new Observer<KeyValuePair<string, object?>>(entry =>
            {
                if (entry.Key == "HostBuilding" && entry.Value is IHostBuilder builder)
                {
                    builder.ConfigureServices((_, services) =>
                    {
                        foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService)
                            && x.ImplementationType?.Name != "GenericWebHostService").ToArray()) services.Remove(service);
                        services.RemoveAll<IConnectionMultiplexer>();
                        services.AddSingleton(redis.Object);
                        services.RemoveAll<IServer>();
                        services.AddOptions<TestServerOptions>();
                        services.AddSingleton<IServer, TestServer>();
                    });
                }
                if (entry.Key == "HostBuilt" && entry.Value is IHost host)
                {
                    observed = true;
                    _ = host.Services.GetService<IBus>(); // Evaluate topology configuration without connecting.
                    foreach (var type in application.GetTypes().Where(x => x.IsClass && !x.IsAbstract && x.Name.EndsWith("Options")))
                    {
                        var option = host.Services.GetService(typeof(IOptions<>).MakeGenericType(type));
                        _ = option?.GetType().GetProperty("Value")?.GetValue(option);
                    }
                    foreach (var type in application.GetTypes().Where(x => x.IsInterface))
                        _ = host.Services.GetService(type);
                    _ = host.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
                    _ = host.Services.GetRequiredService<IOptions<SwaggerGenOptions>>().Value;
                    var jwt = host.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
                    Assert.Equal("https://example.invalid/auth/v1", jwt.Authority);
                    Assert.True(jwt.TokenValidationParameters.ValidateLifetime);
                    var scheme = new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler));
                    foreach (var path in new[] { "/ws", "/health" }) {
                        var context = new DefaultHttpContext(); context.Request.Path = path;
                        context.Request.QueryString = new QueryString("?access_token=test-token");
                        var received = new MessageReceivedContext(context, scheme, jwt);
                        jwt.Events.OnMessageReceived(received).GetAwaiter().GetResult();
                        Assert.Equal(path == "/ws" ? "test-token" : null, received.Token);
                    }
                    var failed = new AuthenticationFailedContext(new DefaultHttpContext(), scheme, jwt) { Exception = new InvalidOperationException("test failure") };
                    jwt.Events.OnAuthenticationFailed(failed).GetAwaiter().GetResult();
                    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                    lifetime.ApplicationStarted.Register(lifetime.StopApplication);
                }
            })));
        }));
        string[] args = ["--Supabase:Url=https://example.invalid", "--Supabase:PublishableKey=fake-publishable",
            "--Supabase:SecretKey=fake-backend", "--RabbitMQ:Host=example.invalid", "--Redis:ConnectionString=example.invalid:6379"];
        var result = application.EntryPoint!.Invoke(null, [args]);
        if (result is Task running) await running.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(observed, "The application's host was not exercised.");
        redis.Verify(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.AtLeastOnce);
    }

    private sealed class Subscriptions : List<IDisposable>, IDisposable
    {
        public void Dispose() { foreach (var subscription in this) subscription.Dispose(); }
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }
}
