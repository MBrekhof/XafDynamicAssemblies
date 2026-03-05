using DevExpress.ExpressApp.Design;
using DevExpress.ExpressApp.EFCore.DesignTime;
using DevExpress.ExpressApp.EFCore.Updating;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    [TypesInfoInitializer(typeof(DbContextTypesInfoInitializer<XafDynamicAssembliesEFCoreDbContext>))]
    public class XafDynamicAssembliesEFCoreDbContext : DbContext
    {
        public XafDynamicAssembliesEFCoreDbContext(DbContextOptions<XafDynamicAssembliesEFCoreDbContext> options) : base(options)
        {
        }
        //public DbSet<ModuleInfo> ModulesInfo { get; set; }
        public DbSet<FileData> FileData { get; set; }
        public DbSet<ReportDataV2> ReportDataV2 { get; set; }
        public DbSet<DashboardData> DashboardData { get; set; }
        public DbSet<Event> Events { get; set; }
        public DbSet<HCategory> HCategories { get; set; }

        public DbSet<CustomClass> CustomClasses { get; set; }
        public DbSet<CustomField> CustomFields { get; set; }
        public DbSet<SchemaHistory> SchemaHistory { get; set; }

        /// <summary>
        /// Runtime entity types compiled by Roslyn. Set by AssemblyGenerationManager at startup/hot-load.
        /// Setting this property increments ModelVersion, which invalidates EF Core's cached model.
        /// </summary>
        private static Type[] _runtimeEntityTypes = Array.Empty<Type>();
        private static int _modelVersion;

        public static Type[] RuntimeEntityTypes
        {
            get => _runtimeEntityTypes;
            set
            {
                _runtimeEntityTypes = value;
                Interlocked.Increment(ref _modelVersion);
            }
        }

        /// <summary>
        /// Monotonically increasing version — changes whenever RuntimeEntityTypes is reassigned.
        /// Used by DynamicModelCacheKeyFactory to invalidate EF Core's model cache.
        /// </summary>
        public static int ModelVersion => _modelVersion;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Register runtime entity types BEFORE XAF extensions so they get
            // GCRecord, OptimisticLockField, and other BaseObject configuration
            foreach (var type in RuntimeEntityTypes)
            {
                modelBuilder.Entity(type).ToTable(type.Name);
            }

            modelBuilder.UseDeferredDeletion(this);
            modelBuilder.UseOptimisticLock();
            modelBuilder.SetOneToManyAssociationDeleteBehavior(DeleteBehavior.SetNull, DeleteBehavior.Cascade);
            modelBuilder.HasChangeTrackingStrategy(ChangeTrackingStrategy.ChangingAndChangedNotificationsWithOriginalValues);
            modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);

            // CustomClass configuration
            modelBuilder.Entity<CustomClass>(entity =>
            {
                entity.HasIndex(e => e.ClassName).IsUnique()
                    .HasFilter("\"GCRecord\" = 0");
                entity.Property(e => e.ClassName).HasMaxLength(128).IsRequired();
                entity.Property(e => e.NavigationGroup).HasMaxLength(128);
                entity.Property(e => e.Status)
                    .HasConversion<string>()
                    .HasMaxLength(20)
                    .HasDefaultValue(CustomClassStatus.Runtime);
                entity.Property(e => e.IsApiExposed).HasDefaultValue(false);
                entity.HasMany(e => e.Fields)
                    .WithOne(f => f.CustomClass)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // CustomField configuration
            modelBuilder.Entity<CustomField>(entity =>
            {
                entity.HasIndex(e => new { e.CustomClassId, e.FieldName }).IsUnique()
                    .HasFilter("\"GCRecord\" = 0");
                entity.Property(e => e.FieldName).HasMaxLength(128).IsRequired();
                entity.Property(e => e.TypeName).HasMaxLength(256).HasDefaultValue("System.String");
                entity.Property(e => e.ReferencedClassName).HasMaxLength(128);
                entity.Property(e => e.IsVisibleInListView).HasDefaultValue(true);
                entity.Property(e => e.IsVisibleInDetailView).HasDefaultValue(true);
                entity.Property(e => e.IsEditable).HasDefaultValue(true);
                entity.Property(e => e.ToolTip).HasMaxLength(512);
                entity.Property(e => e.DisplayName).HasMaxLength(256);
            });
        }
    }
}
