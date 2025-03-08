using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

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
        private readonly TelemetryClient _telemetryClient;
        private readonly int _maxAttachments;

        /// <summary>
        /// Initializes the AttachmentsController with necessary dependencies.
        /// </summary>
        /// <param name="configuration">Application configuration settings.</param>
        /// <param name="logger">Logger instance for logging errors and events.</param>
        /// <param name="telemetryClient">Telemetry client for Application Insights.</param>
        public AttachmentsController(IConfiguration configuration, ILogger<AttachmentsController> logger, TelemetryClient telemetryClient)
        {
            _logger = logger;

            // Retrieve Azure Storage connection string from configuration.
            string connectionString = configuration.GetSection("Storage")["ConnectionString"]
                                      ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

            // Retrieve maximum number of attachments allowed per note.
            _maxAttachments = int.Parse(configuration["Storage:MaxAttachments"] ?? "3");

            // Initialize the BlobServiceClient to interact with Azure Blob Storage.
            _blobServiceClient = new BlobServiceClient(connectionString);

            // Initialize Application Insights telemetry client.
            _telemetryClient = telemetryClient;
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
            {
                var errorDetails = "File is required.";
                LogValidationError(errorDetails, new { NoteId = noteId, AttachmentId = attachmentId });
                return BadRequest(errorDetails);
            }

            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogWarning($"Note {noteId} not found. Cannot upload attachment {attachmentId}.");
                    return NotFound($"Note {noteId} does not exist.");
                }

                // Count the number of existing attachments.
               int count = 0;
               await foreach (var _ in containerClient.GetBlobsAsync())
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
                bool blobExists = await blobClient.ExistsAsync();

                using (var stream = fileData.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = fileData.ContentType },
                        Metadata = new Dictionary<string, string> { { "NoteId", noteId } }
                    });
                }

                // Log telemetry event for attachment creation or update
                string eventName = blobExists ? "AttachmentUpdated" : "AttachmentCreated";
                var telemetry = new EventTelemetry(eventName);
                telemetry.Properties["AttachmentId"] = blobClient.Name;
                telemetry.Metrics["AttachmentSize"] = fileData.Length;
                _telemetryClient.TrackEvent(telemetry);

                return blobExists ? NoContent() : Created(blobClient.Uri.ToString(), new { AttachmentUrl = blobClient.Uri.ToString() });
            }
            catch (Exception ex)
            {
                LogException(ex, new { NoteId = noteId, AttachmentId = attachmentId });
                return StatusCode(500, "Internal Server Error");
            }
        }

        private void LogValidationError(string errorDetails, object additionalData)
        {
            // Log warning for local debugging
            _logger.LogWarning("Validation error: {ErrorDetails} with additional data: {AdditionalData}", errorDetails, additionalData);

            // Log validation errors in Application Insights
            var traceTelemetry = new TraceTelemetry(errorDetails, SeverityLevel.Warning);
            traceTelemetry.Properties["InputPayload"] = System.Text.Json.JsonSerializer.Serialize(additionalData);
            _telemetryClient.TrackTrace(traceTelemetry);
        }

        private void LogException(Exception ex, object additionalData)
        {
            // Log error for local debugging
            _logger.LogError(ex, "An error occurred with additional data: {AdditionalData}", additionalData);

            // Log exception details in Application Insights
            var exceptionTelemetry = new ExceptionTelemetry(ex);
            exceptionTelemetry.Properties["InputPayload"] = System.Text.Json.JsonSerializer.Serialize(additionalData);
            _telemetryClient.TrackException(exceptionTelemetry);
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
