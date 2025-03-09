using Microsoft.EntityFrameworkCore;
using NoteKeeper.Data;
using NoteKeeper.Models;
using NoteKeeper.Services;

public static class DbInitializer
{
    /// <summary>
    /// Seeds the database with initial notes and their corresponding attachments.
    /// </summary>
    /// <param name="context">Database context for interacting with the database.</param>
    /// <param name="generateTagsAsync">Function to generate tags based on note details using AI.</param>
    /// <param name="blobStorageService">Service for interacting with Azure Blob Storage.</param>
    /// <param name="sampleAttachmentsPath">Path to the directory containing sample attachments.</param>
    public static async Task Seed(AppDbContext context, Func<string, Task<List<string>>> generateTagsAsync, BlobStorageService blobStorageService, string sampleAttachmentsPath)
    {
        // Retrieve existing note summaries from the database to avoid duplicate seed data.
        var existingSummaries = await context.Notes.Select(n => n.Summary).ToListAsync();

        // Define a list of seed notes to be inserted if they do not already exist in the database.
        var seedNotes = new List<Note>
        {
            new Note { Summary = "Running grocery list", Details = "Milk, Eggs, Oranges" },
            new Note { Summary = "Gift supplies notes", Details = "Tape & Wrapping Paper" },
            new Note { Summary = "Valentine's Day gift ideas", Details = "Chocolate, Diamonds, New Car" },
            new Note { Summary = "Azure tips", Details = "portal.azure.com is a quick way to get to the portal" }
        };

        // Filter out notes that already exist in the database.
        var notesToAdd = seedNotes.Where(note => !existingSummaries.Contains(note.Summary)).ToList();
        if (!notesToAdd.Any()) return; // If there are no new notes to add, exit the method.

        foreach (var note in notesToAdd)
        {
            // Assign a unique identifier to the new note.
            note.NoteId = Guid.NewGuid();
            note.CreatedDateUtc = DateTime.UtcNow;

            // Generate tags for the note asynchronously based on its details.
            note.Tags = (await generateTagsAsync(note.Details)).Select(tag => new Tag
            {
                Id = Guid.NewGuid(),
                Name = tag,
                NoteId = note.NoteId
            }).ToList();

            // Add the note (with generated tags) to the database context.
            context.Notes.Add(note);

            // Ensure a corresponding blob storage container exists for storing attachments related to this note.
            await blobStorageService.EnsureContainerExistsAsync(note.NoteId.ToString());

            // Retrieve the list of sample attachments related to this note.
            var attachments = GetAttachmentsForNote(note.Summary);
            foreach (var attachment in attachments)
            {
                // Construct the file path for the attachment.
                var filePath = Path.Combine(sampleAttachmentsPath, attachment);
                if (!File.Exists(filePath)) continue; // Skip if the file does not exist.

                // Open a stream for reading the file and upload it to Azure Blob Storage.
                using var fileStream = File.OpenRead(filePath);
                await blobStorageService.UploadAttachmentAsync(
                    note.NoteId.ToString(),
                    attachment,
                    fileStream,
                    GetContentType(attachment) // Determine the correct content type for the file.
                );
            }
        }

        // Save all new notes and their generated tags to the database.
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Dictionary mapping note summaries to their respective attachment file names.
    /// </summary>
    private static Dictionary<string, List<string>> noteAttachments = new()
    {
        { "Running grocery list", new List<string> { "MilkAndEggs.png", "Oranges.png" } },
        { "Gift supplies notes", new List<string> { "WrappingPaper.png", "Tape.png" } },
        { "Valentine's Day gift ideas", new List<string> { "Chocolate.png", "Diamonds.png", "NewCar.png" } },
        { "Azure tips", new List<string> { "AzureLogo.png", "AzureTipsAndTricks.pdf" } }
    };

    /// <summary>
    /// Retrieves the list of attachment file names associated with a given note summary.
    /// </summary>
    /// <param name="summary">The summary of the note.</param>
    /// <returns>A list of attachment file names.</returns>
    private static List<string> GetAttachmentsForNote(string summary)
        => noteAttachments.TryGetValue(summary, out var attachments) ? attachments : new List<string>();

    /// <summary>
    /// Determines the appropriate content type for a given file based on its extension.
    /// </summary>
    /// <param name="filename">The name of the file.</param>
    /// <returns>The MIME type corresponding to the file extension.</returns>
    private static string GetContentType(string filename)
    {
        return Path.GetExtension(filename).ToLower() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream" // Default MIME type for unknown file types.
        };
    }
}
