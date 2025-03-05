using Microsoft.EntityFrameworkCore;
using NoteKeeper.Data;
using NoteKeeper.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Provides functionality to seed the database with default notes and AI-generated tags.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Seeds the database with predefined notes and generates AI-powered tags for each note.
    /// Ensures that duplicate entries are not added.
    /// </summary>
    /// <param name="context">The database context to interact with the database.</param>
    /// <param name="generateTagsAsync">A function to generate AI-powered tags based on note details.</param>
    public static async Task Seed(AppDbContext context, Func<string, Task<List<string>>> generateTagsAsync)
    {
        // Retrieve existing note summaries to avoid duplicate entries.
        var existingSummaries = await context.Notes.Select(n => n.Summary).ToListAsync();
        
        // Define a list of seed notes with default summaries and details.
        var seedNotes = new List<Note>
        {
            new Note { Summary = "Running grocery list", Details = "Milk, Eggs, Oranges" },
            new Note { Summary = "Gift supplies notes", Details = "Tape & Wrapping Paper" },
            new Note { Summary = "Valentine's Day gift ideas", Details = "Chocolate, Diamonds, New Car" },
            new Note { Summary = "Azure tips", Details = "portal.azure.com is a quick way to get to the portal" }
        };

        // Filter out notes that already exist in the database to prevent duplicates.
        var notesToAdd = seedNotes.Where(note => !existingSummaries.Contains(note.Summary)).ToList();
        
        if (notesToAdd.Any()) // Check if there are any new notes to add.
        {
            foreach (var note in notesToAdd)
            {
                // Assign a unique identifier (GUID) to each new note.
                note.NoteId = Guid.NewGuid();

                // Set the creation date to the current UTC time.
                note.CreatedDateUtc = DateTime.UtcNow;

                // Generate AI-powered tags for the note based on its details.
                note.Tags = (await generateTagsAsync(note.Details)).Select(tag => new Tag
                {
                    Id = Guid.NewGuid(),
                    Name = tag,
                    NoteId = note.NoteId
                }).ToList();
            }

            // Add the new notes to the database.
            context.Notes.AddRange(notesToAdd);

            // Save changes asynchronously to persist data in the Azure SQL database.
            await context.SaveChangesAsync();
        }
    }
}
