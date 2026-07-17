using System;
using System.IO;
using Microsoft.EntityFrameworkCore;

namespace QuickTranslate.Database
{
    /// <summary>
    /// 翻译历史数据库上下文
    /// </summary>
    public class TranslationDbContext : DbContext
    {
        /// <summary>
        /// 翻译历史记录表
        /// </summary>
        public DbSet<TranslationRecord> TranslationRecords { get; set; }

        /// <summary>
        /// 数据库文件路径
        /// </summary>
        private static readonly string DbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickTranslate",
            "history.db");

        /// <summary>
        /// 配置 SQLite 数据库连接
        /// </summary>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // 确保目录存在
            var directory = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            optionsBuilder.UseSqlite($"Data Source={DbPath}");
        }

        /// <summary>
        /// 配置模型
        /// </summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TranslationRecord>(entity =>
            {
                entity.ToTable("TranslationRecords");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceText).IsRequired().HasMaxLength(10000);
                entity.Property(e => e.Translation).IsRequired().HasMaxLength(10000);
                entity.Property(e => e.SourceLanguage).HasMaxLength(50);
                entity.Property(e => e.TargetLanguage).HasMaxLength(50);
                entity.Property(e => e.SourceApp).HasMaxLength(100);

                // 索引：按时间倒序查询优化
                entity.HasIndex(e => e.TranslatedAt);
                // 索引：按语言筛选
                entity.HasIndex(e => new { e.SourceLanguage, e.TargetLanguage });
            });
        }

        /// <summary>
        /// 确保数据库已创建（首次运行时自动建表）
        /// </summary>
        public void EnsureDatabaseCreated()
        {
            Database.EnsureCreated();
        }
    }
}
