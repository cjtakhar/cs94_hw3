using NoteKeeper.Data;
using NoteKeeper.Settings;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using NoteKeeper.Services;

namespace NoteKeeper.Helpers
{
    /// <summary>
    /// Helper class for handling database migrations and seeding data at application startup.
    /// </summary>
    public static class StartupHelper
    {
        /// <summary>
        /// Applies any pending database migrations and seeds default data into the database.
        /// </summary>
        /// <param name="services">The application's service provider.</param>
        public static async Task ApplyMigrationsAndSeedDatabase(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var serviceProvider = scope.ServiceProvider;

            try
            {
                Console.WriteLine("Applying database migrations and seeding the database...");

                // Retrieve the database context from the service provider.
                var dbContext = serviceProvider.GetRequiredService<AppDbContext>();

                // Apply any pending migrations to ensure the database schema is up to date.
                await dbContext.Database.MigrateAsync();

                // Retrieve AI settings and the HttpClientFactory for making API requests.
                var aiSettings = serviceProvider.GetRequiredService<AISettings>();
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();


                // Function to generate relevant tags for a given note's details using OpenAI API.
                Func<string, Task<List<string>>> generateTagsAsync = async (details) =>
                {
                    using var httpClient = httpClientFactory.CreateClient("OpenAI");

                    // Construct the request payload for the OpenAI API.
                    var requestBody = new
                    {
                        model = aiSettings.DeploymentModelName ?? "gpt-4o-mini", // Default to GPT-4o-mini if no model is specified.
                        messages = new[]
                        {
                            new { role = "system", content = "Generate 3-5 relevant one-word tags for the given note details. Always return a valid JSON array." },
                            new { role = "user", content = details }
                        },
                        temperature = 0.5, // Controls randomness in responses.
                        max_tokens = 50 // Limits the response length.
                    };

                    // Serialize the request body to JSON format.
                    var json = JsonSerializer.Serialize(requestBody);
                    var content = new StringContent(json, Encoding.UTF8);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                    // Attach API key for authentication.
                    httpClient.DefaultRequestHeaders.Add("api-key", aiSettings.ApiKey);

                    // Send the request to OpenAI API endpoint.
                    var response = await httpClient.PostAsync(aiSettings.Endpoint, content);

                    // Handle unsuccessful API responses.
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("OpenAI API request failed.");
                        return new List<string> { "ErrorFetchingTags" };
                    }

                    // Read the response content.
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Received response: {responseContent}");

                    try
                    {
                        // Parse the JSON response from OpenAI.
                        var jsonResponse = JsonDocument.Parse(responseContent);
                        var messageContent = jsonResponse.RootElement
                            .GetProperty("choices")[0]
                            .GetProperty("message")
                            .GetProperty("content")
                            .GetString();

                        if (!string.IsNullOrEmpty(messageContent))
                        {
                            // Clean up potential formatting issues in the response.
                            messageContent = messageContent
                                .Replace("```json", "")  // Remove code block markers.
                                .Replace("```", "")
                                .Trim();

                            // Ensure the response is a properly formatted JSON array.
                            if (messageContent.StartsWith("[") && messageContent.EndsWith("]"))
                            {
                                // Deserialize JSON array into a list of tags.
                                var tags = JsonSerializer.Deserialize<List<string>>(messageContent);

                                // If no valid tags are extracted, return a default value.
                                return tags?.Count > 0 ? tags : new List<string> { "NoTagsGenerated" };
                            }
                        }

                        // Return an error if the response format is incorrect.
                        return new List<string> { "InvalidTagsFormat" };
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing OpenAI response: {ex.Message}");
                        return new List<string> { "ErrorParsingTags" };
                    }
                };

                // Call the database seeding function, passing the tag generation function.
                var blobStorageService = serviceProvider.GetRequiredService<BlobStorageService>();
                await DbInitializer.Seed(dbContext, generateTagsAsync, blobStorageService, "SampleAttachments");

            }
            catch (Exception ex)
            {
                // Log any errors encountered during migration or seeding.
                Console.WriteLine($"An error occurred during migration or seeding: {ex.Message}");
            }
        }
    }
}
