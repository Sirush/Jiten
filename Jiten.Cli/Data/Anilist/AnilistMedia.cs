namespace Jiten.Cli.Data.Anilist;

public class AnilistMedia
{
    public int Id { get; set; }
    public int? IdMal { get; set; }
    public AnilistTitle Title { get; set; }
    public AnilistDate StartDate { get; set; }
    public string BannerImage { get; set; }
    public AnilistImage CoverImage { get; set; }

    public DateTime ReleaseDate => new(
                                       StartDate.Year.GetValueOrDefault(1),
                                       StartDate.Month.GetValueOrDefault(1),
                                       StartDate.Day.GetValueOrDefault(1)
                                      );
}