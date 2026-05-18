namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public sealed record SlugSimilarity(string Slug, double Similarity);

public interface IServiceRepository
{
    Task<Service?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<SlugSimilarity>> FindSimilarAsync(string slug, int take, CancellationToken ct = default);
    Task AddAsync(Service service, CancellationToken ct = default);
}
