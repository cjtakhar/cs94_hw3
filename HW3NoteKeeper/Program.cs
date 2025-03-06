// System Namespaces
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

// Third-Party Libraries
using Microsoft.OpenApi.Models;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Azure.Storage.Blobs;

// Project-Specific Namespaces
using NoteKeeper.Settings;
using NoteKeeper.Data;
using NoteKeeper.Services;

// Configure the application
var builder = WebApplication.CreateBuilder(args);

// Configure database connection
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

// Configure OpenAI settings
var aiSettings = builder.Configuration.GetSection("OpenAI").Get<AISettings>();

// Validate OpenAI settings
if (aiSettings == null || string.IsNullOrWhiteSpace(aiSettings.ApiKey) || string.IsNullOrWhiteSpace(aiSettings.Endpoint))
{
    throw new InvalidOperationException("OpenAI settings are missing. Ensure they are set in Azure Application Settings, appsettings.json, or secrets.json.");
}

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

    // Set up the HTTP client with the OpenAI API endpoint and authentication
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

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

// Enable Swagger
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Note Keeper API V1");
    c.RoutePrefix = string.Empty;
});

// Apply database migrations and seed the database
await StartupHelper.ApplyMigrationsAndSeedDatabase(app.Services);

// Configure middleware
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.UseCors("AllowReactApp");

// Run the application
app.Run();

/// <summary>
/// Helper class for applying database migrations and seeding data at application startup.
/// </summary>
public static class StartupHelper
{
    /// <summary>
    /// Applies database migrations and seeds default data.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    public static async Task ApplyMigrationsAndSeedDatabase(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        
        try
        {
            // Get the database context and apply any pending migrations
            var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Database.MigrateAsync();

            // Retrieve AI settings and HttpClientFactory for tag generation
            var aiSettings = serviceProvider.GetRequiredService<AISettings>();
            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

            // Define a function to generate tags using OpenAI API
            Func<string, Task<List<string>>> generateTagsAsync = async (details) =>
            {
                using var httpClient = httpClientFactory.CreateClient("OpenAI");

                // Prepare the request body for the OpenAI API
                var requestBody = new
                {
                    model = "gpt-4o-mini",
                    messages = new[]
                    {
                        new { role = "system", content = "Generate 3-5 relevant one-word tags for the given note details. Always return a valid JSON array." },
                        new { role = "user", content = details }
                    },
                    temperature = 0.5,
                    max_tokens = 50
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                // Send the request to the OpenAI API
                httpClient.DefaultRequestHeaders.Add("api-key", aiSettings.ApiKey);
                var response = await httpClient.PostAsync(aiSettings.Endpoint, content);

                if (!response.IsSuccessStatusCode)
                {
                    return new List<string> { "ErrorFetchingTags" };
                }

                // Process the API response
                var responseContent = await response.Content.ReadAsStringAsync();
                try
                {
                    var jsonResponse = JsonDocument.Parse(responseContent);
                    var messageContent = jsonResponse.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();

                    if (!string.IsNullOrEmpty(messageContent))
                    {
                        // Clean up the response and ensure it's a valid JSON array
                        messageContent = messageContent.Replace("``````", "").Trim();
                        if (messageContent.StartsWith("[") && messageContent.EndsWith("]"))
                        {
                            return JsonSerializer.Deserialize<List<string>>(messageContent) ?? new List<string> { "NoTagsGenerated" };
                        }
                    }
                    return new List<string> { "InvalidTagsFormat" };
                }
                catch
                {
                    return new List<string> { "ErrorParsingTags" };
                }
            };

            // Seed the database with initial data
            await DbInitializer.Seed(dbContext, generateTagsAsync);
        }
        catch (Exception ex)
        {
            // Log any errors that occur during migration or seeding
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred while applying migrations or seeding the database.");
        }
    }
}