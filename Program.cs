using MassTransit;
using Gateway.Configuration;
using Gateway.Services;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using EVA_ECS.Chat.Contracts.Events;
using RabbitMQ.Client;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// 1. RabbitMQ Konfiguration auslesen
var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq";
var rabbitUser = builder.Configuration["RabbitMQ:Username"] ?? "admin";
var rabbitPass = builder.Configuration["RabbitMQ:Password"] ?? "secret";
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";

var supabaseUrl = builder.Configuration["Supabase:Url"]?.TrimEnd('/');

if (string.IsNullOrWhiteSpace(supabaseUrl))
{
    throw new InvalidOperationException(
        "Die Konfiguration Supabase:Url fehlt."
    );
}

// 2. Services registrieren
builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:8081",
                "http://127.0.0.1:8081"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddScoped<IChatManagerService, ChatManagerService>();
builder.Services.AddScoped<IUserPresenceStore, RedisUserPresenceStore>();
builder.Services.AddSingleton<IWebSocketConnectionRegistry, WebSocketConnectionRegistry>();
builder.Services.AddHostedService<RedisDeliverySubscriber>();

builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.Id))
        {
            options.Id = Environment.GetEnvironmentVariable("HOSTNAME")
                ?? Environment.MachineName;
        }
    })
    .Validate(options => !string.IsNullOrWhiteSpace(options.Id),
        "Gateway:Id is required.")
    .Validate(options => options.PresenceTtlSeconds > 0,
        "Gateway:PresenceTtlSeconds must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddOptions<RedisRoutingOptions>()
    .Bind(builder.Configuration.GetSection(RedisRoutingOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "Redis:ConnectionString is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.PresenceKeyPrefix),
        "Redis:PresenceKeyPrefix is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.GatewayMappingKeyPrefix),
        "Redis:GatewayMappingKeyPrefix is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DeliveryChannelPrefix),
        "Redis:DeliveryChannelPrefix is required.")
    .ValidateOnStart();

builder.Services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
{
    var gatewayOptions = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>()
        .Value;
    var configuration = ConfigurationOptions.Parse(redisConnectionString);
    configuration.AbortOnConnectFail = false;
    configuration.ClientName = $"gateway-{gatewayOptions.Id}";
    return ConnectionMultiplexer.Connect(configuration);
});
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
    options.InstanceName = "eva-chat:";
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Chat Gateway API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var supabaseIssuer = $"{supabaseUrl}/auth/v1";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Supabase signiert die Access-Tokens dieses Projekts mit ES256.
        // Authority lädt die öffentlichen Signaturschlüssel automatisch über JWKS.
        options.Authority = supabaseIssuer;
        options.Audience = "authenticated";
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidIssuer = supabaseIssuer,
            ValidateAudience = true,
            ValidAudience = "authenticated",
            ValidateLifetime = true
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];

                if (!string.IsNullOrWhiteSpace(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/ws"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                Console.Error.WriteLine(
                    $"JWT validation failed: {context.Exception.GetType().Name}: {context.Exception.Message}"
                );

                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();


builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });

        cfg.Message<ChatMessagePublishedEvent>(message =>
        {
            message.SetEntityName("chat_events");
        });

        cfg.Publish<ChatMessagePublishedEvent>(publish =>
        {
            publish.ExchangeType = ExchangeType.Topic;
        });

        cfg.ConfigureEndpoints(context);
    });
});

// 3. YARP Reverse Proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Chat Gateway API v1");
    });
}

app.UseWebSockets();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapReverseProxy();

app.Run();
