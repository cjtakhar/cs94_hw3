using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace NoteKeeper.Controllers
{
    [ApiController]
    [Route("notes/{noteId}/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<AttachmentsController> _logger;
        private readonly int _maxAttachments;

        public AttachmentsController(IConfiguration configuration, ILogger<AttachmentsController> logger)
        {
            _logger = logger;

            // Fetch storage connection string from environment variables first, then fallback to appsettings.json
            string connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                                      ?? configuration["Storage:ConnectionString"]
                                      ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

            _maxAttachments = int.Parse(Environment.GetEnvironmentVariable("MAX_ATTACHMENTS")
                                      ?? configuration["Storage:MaxAttachments"] ?? "3");

            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        // 🔹 PUT (Upload or Update an Attachment)
        [HttpPut("{attachmentId}")]
        public async Task<IActionResult> UploadAttachment(string noteId, string attachmentId, IFormFile fileData)
        {
            if (fileData == null || fileData.Length == 0)
                return BadRequest("File is required.");

            try
            {
                // Step 1: Get the container reference (noteId as container name)
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

                // Step 2: Ensure the container exists and is private
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                // Step 3: Check if the note (container) actually exists, return 404 if not
                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot upload attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                // Step 4: Enforce max attachments limit
                var blobs = containerClient.GetBlobsAsync();
                int count = 0;
                await foreach (var blob in blobs)
                {
                    count++;
                }
                if (count >= _maxAttachments)
                {
                    return Problem(
                        detail: $"Attachment limit reached. MaxAttachments [{_maxAttachments}]",
                        statusCode: 403,
                        title: "Attachment limit reached"
                    );
                }

                // Step 5: Get blob reference
                var blobClient = containerClient.GetBlobClient(attachmentId);

                // Step 6: Set metadata
                var metadata = new Dictionary<string, string>
                {
                    { "NoteId", noteId }
                };

                // Step 7: Check if the blob exists
                bool blobExists = await blobClient.ExistsAsync();

                // Step 8: Upload file stream
                using (var stream = fileData.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders
                        {
                            ContentType = fileData.ContentType
                        },
                        Metadata = metadata
                    }, cancellationToken: default);
                }

                // Step 9: Return appropriate response
                if (blobExists)
                {
                    return NoContent();  // ✅ (File updated) → 204 No Content
                }

                return Created(blobClient.Uri.ToString(), new { Message = "Attachment created successfully.", AttachmentUrl = blobClient.Uri.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading attachment {attachmentId} for note {noteId}: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }
        }

        // 🔹 DELETE (Remove an Attachment)
        [HttpDelete("{attachmentId}")]
        public async Task<IActionResult> DeleteAttachment(string noteId, string attachmentId)
        {
            try
            {
                // Step 1: Get container reference (noteId as container name)
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

                // Step 2: Ensure the note exists
                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot delete attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                // Step 3: Get blob reference
                var blobClient = containerClient.GetBlobClient(attachmentId);

                // Step 4: Attempt to delete the attachment
                bool deleted = await blobClient.DeleteIfExistsAsync();

                if (deleted)
                {
                    _logger.LogInformation($"Attachment {attachmentId} deleted successfully from note {noteId}.");
                    return NoContent();
                }
                else
                {
                    _logger.LogWarning($"Attachment {attachmentId} was not found for note {noteId}.");
                    return NoContent();  // ✅ Still return 204 if file doesn't exist (per assignment)
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error deleting attachment {attachmentId} from note {noteId}: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }
        }
    }
}
