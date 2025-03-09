using Microsoft.EntityFrameworkCore;
using NoteKeeper.Data;
using NoteKeeper.Models;
using NoteKeeper.Services;

public static class DbInitializer
{
    public static async Task Seed(AppDbContext context, Func<string, Task<List<string>>> generateTagsAsync, BlobStorageService blobStorageService, string sampleAttachmentsPath)
    {
        var existingSummaries = await context.Notes.Select(n => n.Summary).ToListAsync();

        var seedNotes = new List<Note>
        {
            new Note { Summary = "Running grocery list", Details = "Milk, Eggs, Oranges" },
            new Note { Summary = "Gift supplies notes", Details = "Tape & Wrapping Paper" },
            new Note { Summary = "Valentine's Day gift ideas", Details = "Chocolate, Diamonds, New Car" },
            new Note { Summary = "Azure tips", Details = "portal.azure.com is a quick way to get to the portal" }
        };

        var notesToAdd = seedNotes.Where(note => !existingSummaries.Contains(note.Summary)).ToList();
        if (!notesToAdd.Any()) return;

        foreach (var note in notesToAdd)
        {
            note.NoteId = Guid.NewGuid();
            note.CreatedDateUtc = DateTime.UtcNow;
            note.Tags = (await generateTagsAsync(note.Details)).Select(tag => new Tag
            {
                Id = Guid.NewGuid(),
                Name = tag,
                NoteId = note.NoteId
            }).ToList();

            context.Notes.Add(note);

            // Ensure the container exists for this note
            await blobStorageService.EnsureContainerExistsAsync(note.NoteId.ToString());

            // Upload attachments
            var attachments = GetAttachmentsForNote(note.Summary);
            foreach (var attachment in attachments)
            {
                var filePath = Path.Combine(sampleAttachmentsPath, attachment);
                if (!File.Exists(filePath)) continue;

                using var fileStream = File.OpenRead(filePath);
                await blobStorageService.UploadAttachmentAsync(
                    note.NoteId.ToString(),
                    attachment,
                    fileStream,
                    GetContentType(attachment)
                );
            }
        }

        await context.SaveChangesAsync();
    }

    private static Dictionary<string, List<string>> noteAttachments = new()
    {
        { "Running grocery list", new List<string> { "MilkAndEggs.png", "Oranges.png" } },
        { "Gift supplies notes", new List<string> { "WrappingPaper.png", "Tape.png" } },
        { "Valentine's Day gift ideas", new List<string> { "Chocolate.png", "Diamonds.png", "NewCar.png" } },
        { "Azure tips", new List<string> { "AzureLogo.png", "AzureTipsAndTricks.pdf" } }
    };

    private static List<string> GetAttachmentsForNote(string summary)
        => noteAttachments.TryGetValue(summary, out var attachments) ? attachments : new List<string>();

    private static string GetContentType(string filename)
    {
        return Path.GetExtension(filename).ToLower() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
