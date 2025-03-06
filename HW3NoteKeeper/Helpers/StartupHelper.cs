using NoteKeeper.Data;
using NoteKeeper.Settings;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;

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

            try
            {
                Console.WriteLine("Applying migrations and seeding the database...");

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

                    var requestBody = new
                    {
                        model = aiSettings.DeploymentModelName ?? "gpt-4o-mini",
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
                        Console.WriteLine("OpenAI API request failed.");
                        return new List<string> { "ErrorFetchingTags" };
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Received response: {responseContent}");

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
                            // Clean up the response by removing the code block syntax (```json) and whitespace
                            messageContent = messageContent
                                .Replace("```json", "")  // Remove the start of the code block
                                .Replace("```", "")      // Remove the end of the code block
                                .Trim();                 // Trim any extra spaces

                            if (messageContent.StartsWith("[") && messageContent.EndsWith("]"))
                            {
                                // Deserialize the cleaned-up response into a list of tags
                                var tags = JsonSerializer.Deserialize<List<string>>(messageContent);

                                if (tags == null || tags.Count == 0)
                                {
                                    return new List<string> { "NoTagsGenerated" };
                                }

                                // Return the successfully parsed tags
                                return tags;
                            }
                        }

                        return new List<string> { "InvalidTagsFormat" };
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing OpenAI response: {ex.Message}");
                        return new List<string> { "ErrorParsingTags" };
                    }
                };

                // Seed the database with initial data and pass AISettings to DbInitializer
                await DbInitializer.Seed(dbContext, generateTagsAsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during migration or seeding: {ex.Message}");
            }
        }
    }
}
