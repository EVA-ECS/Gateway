using MassTransit;
using Gateway.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// 1. RabbitMQ Konfiguration auslesen
var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq";
var rabbitUser = builder.Configuration["RabbitMQ:Username"] ?? "admin";
var rabbitPass = builder.Configuration["RabbitMQ:Password"] ?? "secret";

// 2. Services registrieren
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- WICHTIG: MassTransit MUSS VOR deinem Service registriert werden, 
// damit IPublishEndpoint für den DI-Container bekannt ist! ---

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });
        
        cfg.ConfigureEndpoints(context);
    });
});

// --- Dependency Injection ---
// Scoped ist hier korrekt, da IPublishEndpoint einen Scope erfordert
builder.Services.AddScoped<IChatManagerService, ChatManagerService>();

// 3. YARP Reverse Proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Später in Service umbauen
app.MapGet("/health", () => Results.Ok(new 
{ 
    Status = "Healthy", 
    Service = "API-Gateway", 
    Timestamp = DateTime.UtcNow 
}));

// ... Rest wie gehabt ...
app.MapControllers();
app.MapReverseProxy();

app.Run();