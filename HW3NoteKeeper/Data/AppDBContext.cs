using Microsoft.EntityFrameworkCore;
using NoteKeeper.Models;

namespace NoteKeeper.Data
{
    /// <summary>
    /// Represents the database context for the NoteKeeper application.
    /// This class is responsible for managing database interactions 
    /// using Entity Framework Core.
    /// </summary>
    public class AppDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="AppDbContext"/> class.
        /// </summary>
        /// <param name="options">The database context options.</param>
        /// <param name="configuration">The application configuration service for retrieving settings.</param>
        public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration) : base(options)
        {
            _configuration = configuration;

            // Retrieve the maximum number of notes allowed from configuration, defaulting to 10 if not specified.
            MaxNotes = _configuration.GetValue<int>("MaxNotes", 10);
        }

        /// <summary>
        /// Gets or sets the collection of notes stored in the database.
        /// Represents the "Note" table.
        /// </summary>
        public DbSet<Note> Notes { get; set; }

        /// <summary>
        /// Gets or sets the collection of tags stored in the database.
        /// Represents the "Tag" table.
        /// </summary>
        public DbSet<Tag> Tags { get; set; }

        /// <summary>
        /// Gets the maximum number of notes allowed in the application.
        /// This value is configurable via application settings.
        /// </summary>
        public int MaxNotes { get; private set; }

        /// <summary>
        /// Configures the database model and entity relationships using Fluent API.
        /// This method is called when the database model is being created.
        /// </summary>
        /// <param name="modelBuilder">The model builder used to configure database schema and relationships.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Map the Note entity to the "Note" table in the database.
            modelBuilder.Entity<Note>().ToTable("Note");

            // Map the Tag entity to the "Tag" table in the database.
            modelBuilder.Entity<Tag>().ToTable("Tag");

            // Define primary key for the Note entity.
            modelBuilder.Entity<Note>().HasKey(n => n.NoteId);

            // Define primary key for the Tag entity.
            modelBuilder.Entity<Tag>().HasKey(t => t.Id);

            // Establish a one-to-many relationship between Note and Tag entities.
            // Each Tag is associated with a single Note.
            // When a Note is deleted, all associated Tags will also be deleted (cascading delete).
            modelBuilder.Entity<Tag>()
                .HasOne(t => t.Note)
                .WithMany(n => n.Tags)
                .HasForeignKey(t => t.NoteId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
