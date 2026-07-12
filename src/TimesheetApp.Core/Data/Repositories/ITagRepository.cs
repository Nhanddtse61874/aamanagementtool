using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// User-defined tags (TAG-01). Hard-delete: removing a tag also removes its BacklogTags links.
//
// M8.2 optimistic concurrency: UpdateAsync takes an optional expectedVersion, separate from
// tag.RowVersion -- supplied -> check-and-bump (throws ConcurrencyConflictException on mismatch);
// omitted -> bump-only. DeleteAsync is unaffected: a hard-delete has no future row to protect.
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetAllAsync();
    Task<int> InsertAsync(Tag tag);                 // returns new id
    Task UpdateAsync(Tag tag, long? expectedVersion = null);   // text / icon / color: check-and-bump
    Task DeleteAsync(int tagId);                     // deletes BacklogTags links then the tag (one tx)
}
