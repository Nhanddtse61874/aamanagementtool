using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Tag data access (TAG-01). SQL + Dapper only; one short connection per method (XC-01).
public sealed class TagRepository : ITagRepository
{
    private const string Cols = "id, text, icon, color, created_at";

    private readonly IConnectionFactory _factory;

    public TagRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Tag>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TagRaw>($"SELECT {Cols} FROM Tags ORDER BY id;");
        return rows.Select(MapTag).ToList();
    }

    public async Task<int> InsertAsync(Tag tag)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tags(text, icon, color, created_at)
              VALUES(@Text, @Icon, @Color, @CreatedAt);
              SELECT last_insert_rowid();",
            new { tag.Text, tag.Icon, tag.Color, CreatedAt = Iso(tag.CreatedAt) });
    }

    public async Task UpdateAsync(Tag tag)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tags SET text = @Text, icon = @Icon, color = @Color WHERE id = @Id;",
            new { tag.Text, tag.Icon, tag.Color, tag.Id });
    }

    public async Task DeleteAsync(int tagId)
    {
        // Hard-delete: drop the N:N links first so no orphan BacklogTags remain, then the tag (one tx).
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM BacklogTags WHERE tag_id = @id;", new { id = tagId }, tx);
        await c.ExecuteAsync("DELETE FROM Tags WHERE id = @id;", new { id = tagId }, tx);
        tx.Commit();
    }

    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static Tag MapTag(TagRaw r) => new(
        (int)r.id, r.text, r.icon, r.color,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    private sealed class TagRaw
    {
        public long id { get; set; }
        public string text { get; set; } = "";
        public string icon { get; set; } = "";
        public string color { get; set; } = "";
        public string created_at { get; set; } = "";
    }
}
