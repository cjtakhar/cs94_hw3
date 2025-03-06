using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NoteKeeper.Controllers
{
    [ApiController]
    [Route("notes/{noteId}/attachments")]
    public class AttachmentsController : ControllerBase
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly int _maxAttachments;

        public AttachmentsController(IConfiguration configuration)
        {
            // Fetch storage connection string from environment variables first, then fallback to appsettings.json
            string connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                                      ?? configuration["Storage:ConnectionString"]
                                        ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

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
                // 🔹 Step 1: Get the container reference (use noteId as container name)
                var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

                // 🔹 Step 2: Ensure the container exists and is private
                await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

                // 🔹 Step 3: Check if the note (container) actually exists, return 404 if not
                if (!await containerClient.ExistsAsync())
                {
                    return NotFound($"Note {noteId} does not exist.");
                }

                // 🔹 Step 4: Enforce max attachments limit
                var blobs = containerClient.GetBlobsAsync();
                int count = 0;
                await foreach (var blob in blobs)
                {
                    count++;
                }
                if (count >= _maxAttachments)
                {
                    return BadRequest($"Cannot upload more than {_maxAttachments} attachments for this note.");
                }

                // 🔹 Step 5: Get blob reference
                var blobClient = containerClient.GetBlobClient(attachmentId);

                // 🔹 Step 6: Set metadata (2.1.5)
                var metadata = new Dictionary<string, string>
                {
                    { "NoteId", noteId }
                };

                // 🔹 Step 7: Check if the blob exists
                bool blobExists = await blobClient.ExistsAsync();

                // 🔹 Step 8: Upload file stream
                using (var stream = fileData.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders
                        {
                            ContentType = fileData.ContentType  // ✅ (2.1.1) Set Content-Type
                        },
                        Metadata = metadata  // ✅ (2.1.5) Set metadata
                    }, cancellationToken: default);
                }

                // 🔹 Step 9: Return appropriate response
                if (blobExists)
                {
                    return NoContent();  // ✅ (2.1.6) File Updated → 204 No Content
                }

                return Created(blobClient.Uri.ToString(), new { Message = "Attachment created successfully.", AttachmentUrl = blobClient.Uri.ToString() });  // ✅ (2.1.7) File Created → 201 Created
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal Server Error: {ex.Message}");
            }
        }
    }
}
