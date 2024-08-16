using Microsoft.EntityFrameworkCore;

namespace ocr.Models
{
    public class FilesContext : DbContext
    {
        public FilesContext(DbContextOptions<FilesContext> options)
            :base(options) { }
        public DbSet<Files> Files { get; set; }=null!;

}
}
