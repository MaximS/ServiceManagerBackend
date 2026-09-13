using ServiceManagerBackend;
using ServiceManagerBackend.DBus;
using ServiceManagerBackend.Options;

var builder = WebApplication.CreateBuilder(args);

// Configuration: whitelist + start/stop sequences + job timeout, validated at startup.
builder.Services.AddOptions<ServicesOptions>()
    .Bind(builder.Configuration.GetSection(ServicesOptions.SectionName))
    .ValidateOnStart();

// One D-Bus connection for the whole app lifetime; connected lazily on first use.
builder.Services.AddSingleton<SystemdClient>();
builder.Services.AddSingleton<ServiceWhitelist>();
builder.Services.AddSingleton<ServicesOptionsValidator>();

// OpenAPI document (served at /openapi/v1.json) + SwaggerUI (served at /swagger).
builder.Services.AddOpenApi();

var app = builder.Build();

// Both are enabled in ALL environments by design (local ops tool; no secrets in the schema).
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "v1"));

app.MapAll();

app.Run();
