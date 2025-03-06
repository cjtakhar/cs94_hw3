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

            string connectionString = configuration.GetSection("Storage")["ConnectionString"]
                                      ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

            _maxAttachments = int.Parse(configuration["Storage:MaxAttachments"] ?? "3");

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
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot upload attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

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

                var blobClient = containerClient.GetBlobClient(attachmentId);
                var metadata = new Dictionary<string, string> { { "NoteId", noteId } };

                bool blobExists = await blobClient.ExistsAsync();

                using (var stream = fileData.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = fileData.ContentType },
                        Metadata = metadata
                    }, cancellationToken: default);
                }

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

        // 🔹 DELETE (Remove an Attachment)
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

        // 🔹 GET (Retrieve an Attachment)
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
    }
}
