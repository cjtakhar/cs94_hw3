using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoteKeeper.Data;
using NoteKeeper.Settings;

namespace NoteKeeper.Helpers
{
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
            
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

            try
            {
                // Get the database context and apply any pending migrations
                var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.MigrateAsync();

                logger.LogInformation("Database migrations applied successfully.");

                // Retrieve AI settings and HttpClientFactory for tag generation
                var aiSettings = serviceProvider.GetRequiredService<AISettings>();
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

                // Define a function to generate tags using OpenAI API
                Func<string, Task<List<string>>> generateTagsAsync = async (details) =>
                {
                    using var httpClient = httpClientFactory.CreateClient("OpenAI");

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

                    httpClient.DefaultRequestHeaders.Add("api-key", aiSettings.ApiKey);
                    var response = await httpClient.PostAsync(aiSettings.Endpoint, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogError("Failed to fetch tags from OpenAI API. Status Code: {StatusCode}", response.StatusCode);
                        return new List<string> { "ErrorFetchingTags" };
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var jsonResponse = JsonDocument.Parse(responseContent);
                        
                        // Ensure "choices" property exists
                        if (!jsonResponse.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                        {
                            logger.LogWarning("Unexpected OpenAI API response format.");
                            return new List<string> { "InvalidTagsFormat" };
                        }

                        var messageContent = choices[0]
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
                    catch (Exception parseEx)
                    {
                        logger.LogError(parseEx, "Error parsing OpenAI API response.");
                        return new List<string> { "ErrorParsingTags" };
                    }
                };

                // Seed the database with initial data
                await DbInitializer.Seed(dbContext, generateTagsAsync);
                logger.LogInformation("Database seeding completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while applying migrations or seeding the database.");
            }
        }
    }
}
