using Microsoft.EntityFrameworkCore;
using Entities.DBEntities;
using Models;
using System.Text;

namespace Entities;

public static class DbExtensions
{

    public static async Task<SPFile> GetDbFileForFileInfo(this BaseSharePointFileInfo fileMigrated, SPOColdStorageDbContext db)
    {
        // Find/create web & site
        var fileSite = await db.Sites
            .Where(f => f.Url.ToLower() == fileMigrated.SiteUrl.ToLower()).FirstOrDefaultAsync();
        if (fileSite == null)
        {
            fileSite = new Site
            {
                Url = fileMigrated.SiteUrl.ToLower()
            };
            db.Sites.Append(fileSite);
        }

        var fileWeb = await db.Webs.Where(f => f.Url.ToLower() == fileMigrated.WebUrl.ToLower()).FirstOrDefaultAsync();
        if (fileWeb == null)
        {
            fileWeb = new Web
            {
                Url = fileMigrated.WebUrl.ToLower(),
                Site = fileSite
            };
            db.Webs.Append(fileWeb);
        }

        var author = await db.Users.Where(u => u.Email.ToLower() == fileMigrated.Author.ToLower()).SingleOrDefaultAsync();
        if (author == null)
        {
            author = new User { Email = fileMigrated.Author.ToLower() };
            db.Users.Append(author);
        }

        // Find/create file
        var migratedFileRecord = await db.Files.Where(f => f.Url.ToLower() == fileMigrated.FullSharePointUrl.ToLower()).FirstOrDefaultAsync();
        if (migratedFileRecord == null)
        {
            // Find or create directory if provided
            FileDirectory? directory = null;
            if (!string.IsNullOrEmpty(fileMigrated.DirectoryPath))
            {
                directory = await db.FileDirectories.Where(d => d.DirectoryPath == fileMigrated.DirectoryPath).FirstOrDefaultAsync();
                if (directory == null)
                {
                    directory = new FileDirectory { DirectoryPath = fileMigrated.DirectoryPath };
                    db.FileDirectories.Add(directory);
                }
            }

            migratedFileRecord = new SPFile
            {
                Url = fileMigrated.FullSharePointUrl.ToLower(),
                Web = fileWeb,
                Directory = directory,
                CreatedDate = fileMigrated.CreatedDate
            };
            db.Files.Append(migratedFileRecord);
        }
        migratedFileRecord.LastModified = fileMigrated.LastModified;
        migratedFileRecord.LastModifiedBy = author;

        return migratedFileRecord;
    }

}
