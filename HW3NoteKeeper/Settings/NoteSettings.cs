namespace NoteKeeper.Settings
{
    /// <summary>
    /// Configuration settings for notes and attachments.
    /// </summary>
    public class NoteSettings
    {
        /// <summary>
        /// Gets or sets the maximum number of notes that can be stored in the database.
        /// </summary>
        public int MaxNotes { get; set; } = 10; // Default: 10 notes

        /// <summary>
        /// Gets or sets the maximum number of attachments allowed per note.
        /// </summary>
        public int MaxAttachments { get; set; } = 3; // Default: 3 attachments
    }
}
