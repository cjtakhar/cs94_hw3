// System Namespaces
using System.Net.Http.Headers;
using System.Reflection;

// Third-Party Libraries
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.ApplicationInsights;
using Azure.Storage.Blobs;

// Project-Specific Namespaces
using NoteKeeper.Settings;
using NoteKeeper.Data;
using NoteKeeper.Services;
using NoteKeeper.Helpers;

var builder = WebApplication.CreateBuilder(args);

// Configure User Secrets (for local development)
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

// Configure database connection
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

// Register AISettings for dependency injection
builder.Services.Configure<AISettings>(builder.Configuration.GetSection("OpenAI"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AISettings>>().Value);

// Configure HTTP client for OpenAI API
builder.Services.AddHttpClient("OpenAI", (provider, client) =>
{
    var settings = provider.GetRequiredService<AISettings>();

    if (string.IsNullOrWhiteSpace(settings.Endpoint))
    {
        throw new InvalidOperationException("OpenAI Endpoint is missing. Ensure it is set in environment variables or appsettings.json.");
    }

    client.BaseAddress = new Uri(settings.Endpoint);
    client.DefaultRequestHeaders.Add("api-key", settings.ApiKey);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

// Configure services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Note Keeper API", Version = "v1" });
    // Include XML Comments for API Documentation
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
});

// Configure BlobStorageService
builder.Services.AddSingleton<BlobStorageService>();

// Configure NoteSettings
builder.Services.Configure<NoteSettings>(builder.Configuration.GetSection("NoteSettings"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<NoteSettings>>().Value);

// Configure Application Insights Telemetry
var aiConnectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(aiConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = aiConnectionString;
    });
}

// Configure Blob Storage Client
builder.Services.AddSingleton(new BlobServiceClient(builder.Configuration["Storage:ConnectionString"]));
builder.Services.AddSingleton<TelemetryClient>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp", policy =>
    {
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Build the application
var app = builder.Build();

// Enable Swagger at the Base URL
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Note Keeper API V1");
    c.RoutePrefix = string.Empty; // Serve the Swagger UI at the root URL
});

// Apply database migrations and seed the database
await StartupHelper.ApplyMigrationsAndSeedDatabase(app.Services);

// Configure middleware
app.UseAuthorization();
app.MapControllers();
app.UseCors("AllowReactApp");

// Run the application
app.Run();
