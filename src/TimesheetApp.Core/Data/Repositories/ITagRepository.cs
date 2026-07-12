using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// User-defined tags (TAG-01). Hard-delete: removing a tag also removes its BacklogTags links.
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetAllAsync();
    Task<int> InsertAsync(Tag tag);                 // returns new id
    Task UpdateAsync(Tag tag);                       // text / icon / color
    Task DeleteAsync(int tagId);                     // deletes BacklogTags links then the tag (one tx)
}
