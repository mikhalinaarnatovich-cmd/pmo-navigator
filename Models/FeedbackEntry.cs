namespace PmoNav.Models;

public class FeedbackEntry
{
    public long FeedbackId { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public string Section { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string? AuthorLogin { get; set; }
    public bool IsAnonymous { get; set; }
}
