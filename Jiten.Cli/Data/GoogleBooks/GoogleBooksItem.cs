namespace Jiten.Cli.Data.GoogleBooks;

public class GoogleBooksItem
{
    public string Id { get; set; }
    public string SelfLink { get; set; }
    public GoogleBooksVolumeInfo VolumeInfo { get; set; }
}