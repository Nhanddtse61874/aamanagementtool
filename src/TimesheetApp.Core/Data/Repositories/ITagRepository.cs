using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// User-defined tags (TAG-01). Hard-delete: removing a tag also removes its BacklogTags links.
//
// M8.2 optimistic concurrency (see IBacklogRepository for the full rationale): UpdateAsync is
// BUMP-ONLY; UpdateCheckedAsync is CHECK-AND-BUMP and returns the new row_version. expectedVersion is
// an explicit argument and is never read off tag.RowVersion -- a caller that builds a Tag from editor
// fields rather than from a read carries the default 0, and a write that trusted the record would
// reject every edit. DeleteAsync is unaffected: a hard-delete has no future row to protect.
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetAllAsync();
    Task<int> InsertAsync(Tag tag);                 // returns new id
    Task UpdateAsync(Tag tag);                      // text / icon / color: bump-only
    Task<long> UpdateCheckedAsync(Tag tag, long expectedVersion);
    Task DeleteAsync(int tagId);                     // deletes BacklogTags links then the tag (one tx)
}
