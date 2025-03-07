using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NoteKeeper.Services
{
    /// <summary>
    /// Service for managing Azure Blob Storage interactions related to note attachments.
    /// </summary>
    public class BlobStorageService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly int _maxAttachments;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobStorageService"/> class.
        /// Retrieves storage connection details and max attachment limit from environment variables or configuration.
        /// </summary>
        /// <param name="configuration">The application configuration for retrieving settings.</param>
        /// <exception cref="InvalidOperationException">Thrown if the storage connection string is not configured.</exception>
        public BlobStorageService(IConfiguration configuration)
        {
            // Retrieve Azure Storage connection string from environment variables or app settings.
            string connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                                      ?? configuration["Storage:ConnectionString"]
                                      ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("Azure Storage connection string is not configured.");
            }

            // Retrieve the maximum number of attachments per note from environment variables or configuration.
            _maxAttachments = int.Parse(Environment.GetEnvironmentVariable("MAX_ATTACHMENTS")
                                      ?? configuration["Storage:MaxAttachments"] ?? "3");

            // Initialize the BlobServiceClient to interact with Azure Blob Storage.
            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        /// <summary>
        /// Ensures that a blob container exists for the specified note.
        /// If the container does not exist, it is created.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <returns>Returns <c>true</c> when the container exists or has been successfully created.</returns>
        public async Task<bool> EnsureContainerExistsAsync(string noteId)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

            // Create the container if it does not already exist.
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            return true;
        }

        /// <summary>
        /// Checks if an additional attachment can be uploaded to a specific note based on the configured attachment limit.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <returns><c>true</c> if another attachment can be uploaded, otherwise <c>false</c>.</returns>
        public async Task<bool> CanUploadAttachment(string noteId)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
            var blobs = containerClient.GetBlobsAsync();

            int count = 0;
            await foreach (var blobItem in blobs)
            {
                count++; // Count the number of existing attachments.
            }

            return count < _maxAttachments; // Return true if the limit has not been reached.
        }

        /// <summary>
        /// Uploads an attachment file to the Azure Blob Storage container associated with a specific note.
        /// </summary>
        /// <param name="noteId">The unique identifier of the note.</param>
        /// <param name="attachmentId">The unique identifier of the attachment (blob name).</param>
        /// <param name="fileStream">The file data stream to be uploaded.</param>
        /// <param name="contentType">The MIME type of the uploaded file.</param>
        /// <returns>The URI of the uploaded attachment.</returns>
        public async Task<string> UploadAttachmentAsync(string noteId, string attachmentId, Stream fileStream, string contentType)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);

            // Ensure the container exists before uploading.
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            var blobClient = containerClient.GetBlobClient(attachmentId);

            // Upload the file stream as a blob with appropriate metadata.
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            });

            return blobClient.Uri.ToString(); // Return the URL of the uploaded attachment.
        }
    }
}
