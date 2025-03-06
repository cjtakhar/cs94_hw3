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
    public class BlobStorageService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly int _maxAttachments;

        public BlobStorageService(IConfiguration configuration)
        {
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

        public async Task<bool> EnsureContainerExistsAsync(string noteId)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            return true;
        }

        public async Task<bool> CanUploadAttachment(string noteId)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
            var blobs = containerClient.GetBlobsAsync();
            int count = 0;
            await foreach (var blobItem in blobs)
            {
                count++;
            }
            return count < _maxAttachments;
        }

        public async Task<string> UploadAttachmentAsync(string noteId, string attachmentId, Stream fileStream, string contentType)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(noteId);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

            var blobClient = containerClient.GetBlobClient(attachmentId);
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            });

            return blobClient.Uri.ToString();
        }
    }
}
