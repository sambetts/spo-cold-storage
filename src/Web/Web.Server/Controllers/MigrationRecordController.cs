using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Entities;
using Entities.Configuration;
using Entities.DBEntities;

namespace Web.Controllers;

[Microsoft.AspNetCore.Authorization.Authorize]
[ApiController]
[Route("[controller]")]
public class MigrationRecordController(ILogger<MigrationRecordController> logger, SPOColdStorageDbContext context, Config config) : ControllerBase
{
    private readonly ILogger<MigrationRecordController> _logger = logger;
    private readonly SPOColdStorageDbContext _context = context;
    private readonly Config _config = config;

    // Search for migration log by keyword
    // GET: MigrationRecord
    [HttpGet]
    public async Task<ActionResult<IEnumerable<FileMigrationCompletedLog>>> GetSuccesfulMigrations(string keyWord)
    {
        if (string.IsNullOrEmpty(keyWord))
        {
            return BadRequest("No search term defined");
        }
        else
        {
            return await _context.FileMigrationsCompleted
                .Where(m => m.File.Url.Contains(keyWord))
                .Include(m => m.File)
                    .ThenInclude(f => f.Web)
                        .ThenInclude(w => w.Site)
                .ToListAsync();
        }
    }

}
