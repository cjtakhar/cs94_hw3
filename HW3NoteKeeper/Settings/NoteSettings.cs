namespace NoteKeeper.Settings
{
    /// <summary>
    /// Gets or sets the maximum number of notes that can be stored in the database.
    /// </summary>
    public class NoteSettings
    {
        public int MaxNotes { get; set; } = 10; // Sets maximum number of notes to 10 by default
    }
}
