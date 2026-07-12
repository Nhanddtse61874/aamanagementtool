using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Tag data access (TAG-01). SQL + Dapper only; one short connection per method (XC-01).
public sealed class TagRepository : ITagRepository
{
    private const string Cols = "id, text, icon, color, created_at, row_version";

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

    // Check-and-bump when the caller supplies expectedVersion (throws on a stale version or a
    // deleted row); bump-only when it doesn't. expectedVersion is deliberately a separate parameter
    // rather than reading tag.RowVersion: an existing caller building a Tag positionally without
    // naming RowVersion gets the record's default (1), which would falsely claim "still at v1" on
    // every edit past the first and start throwing spurious conflicts.
    public async Task UpdateAsync(Tag tag, long? expectedVersion = null)
    {
        using var c = _factory.Create();
        if (expectedVersion is null)
        {
            await c.ExecuteAsync(
                "UPDATE Tags SET text = @Text, icon = @Icon, color = @Color, row_version = row_version + 1 WHERE id = @Id;",
                new { tag.Text, tag.Icon, tag.Color, tag.Id });
            return;
        }

        var rows = await c.ExecuteAsync(
            @"UPDATE Tags SET text = @Text, icon = @Icon, color = @Color, row_version = row_version + 1
              WHERE id = @Id AND row_version = @expected;",
            new { tag.Text, tag.Icon, tag.Color, tag.Id, expected = expectedVersion.Value });
        if (rows > 0) return;

        var exists = await c.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM Tags WHERE id = @Id;", new { tag.Id });
        throw new ConcurrencyConflictException("Tags", tag.Id, expectedVersion, deleted: exists == 0);
    }

    public async Task DeleteAsync(int tagId)
    {
        // Hard-delete: drop both N:N links first so no orphan BacklogTags/TaskTags (v9) remain, then the
        // tag (one tx). Neither link table declares an FK, so the cascade is manual.
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM BacklogTags WHERE tag_id = @id;", new { id = tagId }, tx);
        await c.ExecuteAsync("DELETE FROM TaskTags WHERE tag_id = @id;", new { id = tagId }, tx);
        await c.ExecuteAsync("DELETE FROM Tags WHERE id = @id;", new { id = tagId }, tx);
        tx.Commit();
    }

    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static Tag MapTag(TagRaw r) => new(
        (int)r.id, r.text, r.icon, r.color,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        r.row_version);

    private sealed class TagRaw
    {
        public long id { get; set; }
        public string text { get; set; } = "";
        public string icon { get; set; } = "";
        public string color { get; set; } = "";
        public string created_at { get; set; } = "";
        public long row_version { get; set; }
    }
}
