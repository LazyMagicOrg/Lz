namespace Lz.Core.Config;

/// <summary>
/// Infrastructure backup configuration.
/// Maps to the "Backup:" section in systemconfig.{systemkey}.{env}.yaml.
/// Currently scoped to EFS. RDS backups are controlled by AwsRdsComponent
/// directly (BackupRetentionPeriod) — keep that there for now; this section
/// can grow to cover RDS overrides if/when needed.
/// </summary>
public class BackupConfig
{
    /// <summary>
    /// Whether AWS Backup automatic backups are enabled for the foundation
    /// EFS file system. When null (default), enabled in prod/staging,
    /// disabled elsewhere — matches the Protect-by-environment policy on
    /// the FileSystem resource itself.
    ///
    /// When enabled, AWS Backup uses its default EFS vault
    /// (aws/efs/automatic-backup-vault) on a daily schedule with 35-day
    /// retention. For custom schedules, retention, or cross-region copy,
    /// a dedicated AWS Backup plan would be needed — out of scope here.
    /// </summary>
    public bool? Enabled { get; set; }
}
