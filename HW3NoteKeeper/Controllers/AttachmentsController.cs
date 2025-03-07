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
    /// <summary>
    /// Controller for managing attachments associated with notes using Azure Blob Storage.
    /// </summary>
    [ApiController]
    [Route("notes/{noteId}/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<AttachmentsController> _logger;
        private readonly int _maxAttachments;

        /// <summary>
        /// Initializes the AttachmentsController with necessary dependencies.
        /// </summary>
        /// <param name="configuration">Application configuration settings.</param>
        /// <param name="logger">Logger instance for logging errors and events.</param>
        public AttachmentsController(IConfiguration configuration, ILogger<AttachmentsController> logger)
        {
            _logger = logger;

            // Retrieve Azure Storage connection string from configuration.
            string connectionString = configuration.GetSection("Storage")["ConnectionString"]
                                      ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

            // Retrieve maximum number of attachments allowed per note.
            _maxAttachments = int.Parse(configuration["Storage:MaxAttachments"] ?? "3");

            // Initialize the BlobServiceClient to interact with Azure Blob Storage.
            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        /// <summary>
        /// Uploads or updates an attachment for a specific note.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <param name="attachmentId">The unique identifier of the attachment.</param>
        /// <param name="fileData">The file to be uploaded.</param>
        /// <returns>A response indicating success or failure.</returns>
        [HttpPut("{attachmentId}")]
        public async Task<IActionResult> UploadAttachment(string noteId, string attachmentId, IFormFile fileData)
        {
            if (fileData == null || fileData.Length == 0)
                return BadRequest("File is required.");

            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                // Check if the note (container) exists.
                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot upload attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                // Count the number of existing attachments.
                var blobs = containerClient.GetBlobsAsync();
                int count = 0;
                await foreach (var blob in blobs)
                {
                    count++;
                }
                
                // Enforce attachment limit per note.
                if (count >= _maxAttachments)
                {
                    return Problem(
                        detail: $"Attachment limit reached. MaxAttachments [{_maxAttachments}]",
                        statusCode: 403,
                        title: "Attachment limit reached"
                    );
                }

                var blobClient = containerClient.GetBlobClient(attachmentId);
                var metadata = new Dictionary<string, string> { { "NoteId", noteId } };

                bool blobExists = await blobClient.ExistsAsync();

                // Upload the file data to blob storage.
                using (var stream = fileData.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = fileData.ContentType },
                        Metadata = metadata
                    }, cancellationToken: default);
                }

                // Return appropriate response based on whether the attachment was updated or newly created.
                if (blobExists)
                {
                    return NoContent();
                }

                return Created(blobClient.Uri.ToString(), new { Message = "Attachment created successfully.", AttachmentUrl = blobClient.Uri.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading attachment {attachmentId} for note {noteId}: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }
        }

        /// <summary>
        /// Deletes an attachment from a specific note.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <param name="attachmentId">The unique identifier of the attachment.</param>
        /// <returns>A response indicating success or failure.</returns>
        [HttpDelete("{attachmentId}")]
        public async Task<IActionResult> DeleteAttachment(string noteId, string attachmentId)
        {
            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot delete attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                var blobClient = containerClient.GetBlobClient(attachmentId);
                bool deleted = await blobClient.DeleteIfExistsAsync();

                if (deleted)
                {
                    _logger.LogInformation($"Attachment {attachmentId} deleted successfully from note {noteId}.");
                    return NoContent();
                }
                else
                {
                    _logger.LogWarning($"Attachment {attachmentId} was not found for note {noteId}.");
                    return NoContent();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error deleting attachment {attachmentId} from note {noteId}: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }
        }

        /// <summary>
        /// Retrieves an attachment for a specific note.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <param name="attachmentId">The unique identifier of the attachment.</param>
        /// <returns>The requested file if found.</returns>
        [HttpGet("{attachmentId}")]
        public async Task<IActionResult> GetAttachment(string noteId, string attachmentId)
        {
            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot retrieve attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                var blobClient = containerClient.GetBlobClient(attachmentId);

                if (!await blobClient.ExistsAsync())
                {
                    _logger.LogWarning($"Attachment {attachmentId} not found in note {noteId}.");
                    return NotFound($"Attachment {attachmentId} does not exist.");
                }

                var downloadResponse = await blobClient.DownloadStreamingAsync();
                var stream = downloadResponse.Value.Content;
                string contentType = downloadResponse.Value.Details.ContentType ?? "application/octet-stream";

                return File(stream, contentType, attachmentId);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving attachment {attachmentId} from note {noteId}: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }
        }

        /// <summary>
        /// Retrieves all attachments for a specific note.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <returns>A list of attachments with metadata.</returns>
        [HttpGet]
        public async Task<IActionResult> GetAllAttachments(string noteId)
        {
            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot retrieve attachments.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                var attachments = new List<object>();

                await foreach (var blobItem in containerClient.GetBlobsAsync())
                {
                    attachments.Add(new
                    {
                        attachmentId = blobItem.Name,
                        contentType = blobItem.Properties.ContentType ?? "unknown",
                        createdDate = blobItem.Properties.CreatedOn?.ToString("o"),  // ISO 8601 format
                        lastModifiedDate = blobItem.Properties.LastModified?.ToString("o"),
                        length = blobItem.Properties.ContentLength
                    });
                }

                return Ok(new { noteId, attachments });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving attachments for note {noteId}: {ex.Message}");
                return StatusCode(500, "Internal Server Error");
            }
        }
    }
}
