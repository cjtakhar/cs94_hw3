using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace NoteKeeper.Models
{
    /// <summary>
    /// Represents a tag entity stored in the database.
    /// </summary>
    public class Tag
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid(); // Unique identifier for the tag

        [Required]
        public Guid NoteId { get; set; } // Foreign key to associate the tag with a specific note

        [Required]
        [StringLength(30)]
        public string Name
        {
            get => _name;
            set => _name = value.Length > 30 ? value.Substring(0, 30) : value; // Ensure tag name doesn't exceed 30 characters
        }
        private string _name = string.Empty; // Backing field for Name property

        [JsonIgnore] // Exclude this property when serializing to JSON
        [ForeignKey("NoteId")] // Specify the foreign key relationship
        public Note? Note { get; set; } // Navigation property to the associated Note
    }
}
