using Microsoft.EntityFrameworkCore;

namespace BanteraApi.Database;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
}
