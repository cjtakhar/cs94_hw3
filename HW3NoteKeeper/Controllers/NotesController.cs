// System Namespaces
using System.Text;
using System.Text.Json;

// Third-Party Libraries
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Azure.Storage.Blobs;

// Project-Specific Namespaces
using NoteKeeper.Data;
using NoteKeeper.Models;
using NoteKeeper.Settings;

namespace NoteKeeper.Controllers
{
    /// <summary>
    /// API Controller for managing notes.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class NotesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AISettings _aiSettings;
        private readonly ILogger<NotesController> _logger;
        private readonly NoteSettings _noteSettings;
        private readonly TelemetryClient _telemetryClient;
        private readonly BlobServiceClient _blobServiceClient;

        public NotesController(
            AppDbContext context,
            IHttpClientFactory httpClientFactory,
            IOptions<AISettings> aiSettings,
            IOptions<NoteSettings> noteSettings,
            ILogger<NotesController> logger,
            TelemetryClient telemetryClient,
            BlobServiceClient blobServiceClient
        )
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _aiSettings = aiSettings?.Value ?? throw new ArgumentNullException(nameof(aiSettings), "AISettings is null. Ensure it is registered in Program.cs.");
            _noteSettings = noteSettings?.Value ?? throw new ArgumentNullException(nameof(noteSettings), "NoteSettings is null. Ensure it is registered in Program.cs.");
            _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
            _blobServiceClient = blobServiceClient;  // Assign the injected BlobServiceClient
        }

        /// <summary>
        /// Retrieves all notes from database and searches by tag.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)] // Returns list of notes
        public async Task<IActionResult> GetNotes([FromQuery] string? tagName)
        {
            IQueryable<Note> query = _context.Notes.Include(n => n.Tags);

            // If tagName is provided and not empty, filter notes that contain this tag
            if (!string.IsNullOrWhiteSpace(tagName))
            {
                query = query.Where(n => n.Tags.Any(t => t.Name == tagName.Trim()));
            }

            var notes = await query.ToListAsync();

            // Modify response to return only tag names as a list
            var formattedNotes = notes.Select(note => new
            {
                note.NoteId,
                note.Summary,
                note.Details,
                note.CreatedDateUtc,
                note.ModifiedDateUtc,
                Tags = note.Tags.Select(t => t.Name).ToList() // Convert tag objects to tag name list
            });

            return Ok(formattedNotes); // 200 OK (returns empty array if no notes found)
        }

        /// <summary>
        /// Retrieves a note by its ID.
        /// </summary>
        [HttpGet("{noteId}")]
        [ProducesResponseType(StatusCodes.Status200OK)] // Returns the note if found
        [ProducesResponseType(StatusCodes.Status404NotFound)] // If the note does not exist
        public async Task<IActionResult> GetNoteById(Guid noteId)
        {
            var note = await _context.Notes.Include(n => n.Tags).FirstOrDefaultAsync(n => n.NoteId == noteId);
            if (note == null)
            {
                return NotFound();
            }

            // Modify response to return only tag names as a list
            var formattedNote = new
            {
                note.NoteId,
                note.Summary,
                note.Details,
                note.CreatedDateUtc,
                note.ModifiedDateUtc,
                Tags = note.Tags.Select(t => t.Name).ToList() // Convert tag objects to a simple list of tag names
            };

            return Ok(formattedNote);
        }

        /// <summary>
        /// Retrieves a unique list of all tags.
        /// </summary>
        [HttpGet("tags")]
        [ProducesResponseType(StatusCodes.Status200OK)] // Returns list of unique tags
        public async Task<IActionResult> GetAllTags()
        {
            // Query unique tag names from the Tag table
            var uniqueTags = await _context.Tags
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(name => name) // Optional: Order alphabetically
                .ToListAsync();

            // Format response as a list of objects with a "name" field
            var response = uniqueTags.Select(name => new { name });

            return Ok(response); // Return 200 OK with the list of unique tags
        }

        /// <summary>
        /// Creates a new note and saves it to the database.
        /// </summary>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> CreateNote([FromBody] Note newNote)
        {
            // Validate input: Check if the note is null or required fields are empty
            if (newNote == null || string.IsNullOrWhiteSpace(newNote.Summary) || string.IsNullOrWhiteSpace(newNote.Details))
            {
                var errorMessage = "Summary and details are required.";
                _logger.LogError(errorMessage);
                _telemetryClient.TrackTrace("Validation Error: " + errorMessage, SeverityLevel.Warning,
                    new Dictionary<string, string> { { "InputPayload", JsonSerializer.Serialize(newNote) } });
                return BadRequest(errorMessage);
            }

            try
            {
                // Check if the maximum number of notes has been reached
                int currentNoteCount = await _context.Notes.CountAsync();
                if (currentNoteCount >= _noteSettings.MaxNotes)
                {
                    _telemetryClient.TrackEvent("NoteLimitReached",
                        new Dictionary<string, string> { { "MaxNotes", _noteSettings.MaxNotes.ToString() } });

                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Title = "Note limit reached",
                        Status = 403,
                        Detail = $"You have reached the maximum of {_noteSettings.MaxNotes} notes."
                    });
                }

                // Prepare the new note
                newNote.NoteId = Guid.NewGuid();
                newNote.CreatedDateUtc = DateTime.UtcNow;
                newNote.ModifiedDateUtc = null;

                // Generate AI-powered tags
                var generatedTags = await GenerateTagsAsync(newNote.Details);
                newNote.Tags = generatedTags?.Select(tagName => new Tag
                {
                    Id = Guid.NewGuid(),
                    NoteId = newNote.NoteId,
                    Name = tagName
                }).ToList() ?? new List<Tag>();

                // Save to the database
                _context.Notes.Add(newNote);
                await _context.SaveChangesAsync();

                // Track note creation event
                _telemetryClient.TrackEvent("NoteCreated", new Dictionary<string, string>
                {
                    { "summary", newNote.Summary },
                    { "tagsCount", newNote.Tags.Count.ToString() }
                });

                var formattedResponse = new
                {
                    newNote.NoteId,
                    newNote.Summary,
                    newNote.Details,
                    newNote.CreatedDateUtc,
                    newNote.ModifiedDateUtc,
                    Tags = newNote.Tags.Select(t => t.Name).ToList()
                };

                return CreatedAtAction(nameof(GetNoteById), new { noteId = newNote.NoteId }, formattedResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while creating a note.");
                _telemetryClient.TrackException(ex, new Dictionary<string, string>
                {
                    { "InputPayload", JsonSerializer.Serialize(newNote) }
                });
                return StatusCode(500, "Internal Server Error");
            }
        }

        /// <summary>
        /// Updates an existing note by its ID.
        /// </summary>
        [HttpPatch("{noteId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)] // Successfully updated
        [ProducesResponseType(StatusCodes.Status400BadRequest)] // Invalid input
        [ProducesResponseType(StatusCodes.Status404NotFound)] // Note not found
        public async Task<IActionResult> UpdateNote(Guid noteId, [FromBody] NoteUpdateRequest updateRequest)
        {
            if (updateRequest == null)
            {
                return BadRequest("Request body cannot be null.");
            }

            // Retrieve the existing note with its tags 
            var note = await _context.Notes
                .Include(n => n.Tags)
                .FirstOrDefaultAsync(n => n.NoteId == noteId);

            if (note == null)
            {
                _logger.LogWarning("Update failed: Note with ID {NoteId} not found.", noteId);
                return NotFound("Note not found.");
            }

            bool isSummaryUpdated = !string.IsNullOrWhiteSpace(updateRequest.Summary);
            bool isDetailsUpdated = !string.IsNullOrWhiteSpace(updateRequest.Details);
            bool shouldUpdateModifiedDate = false;

            if (isSummaryUpdated)
            {
                note.Summary = updateRequest.Summary!.Trim();
                shouldUpdateModifiedDate = true;
            }

            if (isDetailsUpdated)
            {
                note.Details = updateRequest.Details!.Trim();
                shouldUpdateModifiedDate = true;

                // Remove old tags before adding new ones 
                _context.Tags.RemoveRange(note.Tags);
                await _context.SaveChangesAsync();

                // Generate new AI-powered tags 
                var newTags = await GenerateTagsAsync(note.Details);
                var tagEntities = newTags.Select(tagName => new Tag
                {
                    Id = Guid.NewGuid(),
                    NoteId = note.NoteId,
                    Name = tagName
                }).ToList();

                _context.Tags.AddRange(tagEntities);
            }

            if (shouldUpdateModifiedDate)
            {
                note.ModifiedDateUtc = DateTime.UtcNow;
            }
            else
            {
                return BadRequest("At least one field (summary or details) must be provided for an update.");
            }

            _context.Entry(note).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent(); // 204 No Content (Success)
        }

        /// <summary>
        /// Deletes a note and its associated attachments.
        /// </summary>
        [HttpDelete("{noteId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)] // Successfully deleted
        [ProducesResponseType(StatusCodes.Status404NotFound)] // Note not found
        [ProducesResponseType(StatusCodes.Status500InternalServerError)] // Server error
        public async Task<IActionResult> DeleteNote(Guid noteId)
        {
            var note = await _context.Notes.Include(n => n.Tags).FirstOrDefaultAsync(n => n.NoteId == noteId);
            if (note == null)
            {
                _logger.LogWarning($"Note {noteId} not found. Cannot delete.");
                return NotFound($"Note {noteId} does not exist.");
            }

            // Step 1: Delete associated tags
            try
            {
                _context.Tags.RemoveRange(note.Tags);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete tags for note {noteId}: {ex.Message}");
                return StatusCode(500, "Failed to delete tags. Note deletion aborted.");
            }

            // Step 2: Delete all attachments from blob storage
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId.ToString());

            if (await containerClient.ExistsAsync())
            {
                try
                {
                    await foreach (var blobItem in containerClient.GetBlobsAsync())
                    {
                        var blobClient = containerClient.GetBlobClient(blobItem.Name);
                        if (!await blobClient.DeleteIfExistsAsync())
                        {
                            _logger.LogError($"Failed to delete attachment {blobItem.Name} for note {noteId}.");
                        }
                    }

                    // Step 3: Delete the container itself
                    await containerClient.DeleteIfExistsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to delete blob container for note {noteId}: {ex.Message}");
                }
            }

            // Step 4: Delete the note from the database
            _context.Notes.Remove(note);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Note {noteId} and all associated attachments deleted successfully.");
            return NoContent();  // 204 No Content
        }

        /// <summary>
        /// Calls OpenAI API to generate AI-powered tags for a note's details.
        /// </summary>
        private async Task<List<string>> GenerateTagsAsync(string details)
        {
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

            var jsonRequest = JsonSerializer.Serialize(requestBody);
            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, _aiSettings.Endpoint)
            {
                Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("api-key", _aiSettings.ApiKey);
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI API request failed with status code: {StatusCode}", response.StatusCode);
                _telemetryClient.TrackTrace("OpenAI API request failed", SeverityLevel.Error,
                    new Dictionary<string, string> { { "StatusCode", response.StatusCode.ToString() } });

                return new List<string> { "ErrorFetchingTags" };
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("OpenAI response received successfully.");

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
                    messageContent = messageContent.Replace("```json", "").Replace("```", "").Trim();

                    if (messageContent.StartsWith("[") && messageContent.EndsWith("]"))
                    {
                        _telemetryClient.TrackEvent("TagsGenerated", new Dictionary<string, string>
                        {
                            { "tags", messageContent }
                        });

                        return JsonSerializer.Deserialize<List<string>>(messageContent) ?? new List<string> { "NoTagsGenerated" };
                    }
                }
                return new List<string> { "InvalidTagsFormat" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing OpenAI response.");
                _telemetryClient.TrackException(ex);
                return new List<string> { "ErrorParsingTags" };
            }
        }
    }
}