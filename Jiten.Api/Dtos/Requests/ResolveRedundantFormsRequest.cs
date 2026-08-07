namespace Jiten.Api.Dtos.Requests;

public class ResolveRedundantFormsRequest
{
    public List<RedundantFormKey> Forms { get; set; } = [];
}

public class RedundantFormKey
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
}
