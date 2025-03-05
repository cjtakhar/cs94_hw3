/// <summary>
/// Updates existing note in database
/// </summary>
public class NoteUpdateRequest
{
    public string? Summary { get; set; } // Update summary
    public string? Details { get; set; } // Update details
}
