using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HW3NoteKeeper.Controllers
{
    [ApiController]
    [Route("notes/{noteId}/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly int _maxAttachments;

        public AttachmentsController(IConfiguration configuration)
        {
            // Fetch storage connection string from Azure environment variables first, then fallback to appsettings.json
            string connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                                      ?? configuration["Storage:ConnectionString"]
                                        ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("Azure Storage connection string is not configured.");
            }

            _maxAttachments = int.Parse(Environment.GetEnvironmentVariable("MAX_ATTACHMENTS")
                                      ?? configuration["Storage:MaxAttachments"] ?? "3");

            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        [HttpPut("{attachmentId}")]
        public async Task<IActionResult> UploadAttachment(string noteId, string attachmentId, IFormFile fileData)
        {
            if (fileData == null || fileData.Length == 0)
                return BadRequest("File is required.");

            try
            {
                // Get container reference (noteId as container name)
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                // Enforce max attachments
                var blobs = containerClient.GetBlobsAsync();
                int count = 0;
                await foreach (var blob in blobs)
                {
                    count++;
                }
                if (count >= _maxAttachments)
                    return BadRequest($"Cannot upload more than {_maxAttachments} attachments for this note.");

                // Get blob reference
                var blobClient = containerClient.GetBlobClient(attachmentId);

                // Upload file stream
                using (var stream = fileData.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = fileData.ContentType }
                    }, cancellationToken: default);

                }

                return Ok(new { Message = "Attachment uploaded successfully.", AttachmentUrl = blobClient.Uri.ToString() });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal Server Error: {ex.Message}");
            }
        }
    }
}