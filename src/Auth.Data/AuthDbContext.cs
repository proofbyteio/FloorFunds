// Hand-written (no design-time packages, TLD 2026-11-04 7:56). Keep equal to Migrations/AuthDbContextModelSnapshot.cs.
using Auth.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Auth.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public const string Schema = "auth";

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<LocationLoginConfigRow> LocationLoginConfigRows => Set<LocationLoginConfigRow>();
    public DbSet<LocationLoginConfigChange> LocationLoginConfigChanges => Set<LocationLoginConfigChange>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<UserLocation> UserLocations => Set<UserLocation>();
    public DbSet<AuditOutbox> AuditOutboxs => Set<AuditOutbox>();
    public DbSet<OidcReplay> OidcReplays => Set<OidcReplay>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<PartnerReasonBounds> PartnerReasonBounds => Set<PartnerReasonBounds>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(b =>
        {
            b.ToTable("location", Schema);
            b.HasKey(x => x.LocationId).HasName("location_pkey");
            b.Property(x => x.LocationId).HasColumnName("location_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.PartnerId).HasColumnName("partner_id").HasColumnType("uuid");
            b.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
            b.Property(x => x.AddressLine1).HasColumnName("address_line_1").HasColumnType("text");
            b.Property(x => x.AddressLine2).HasColumnName("address_line_2").HasColumnType("text");
            b.Property(x => x.City).HasColumnName("city").HasColumnType("text");
            b.Property(x => x.Country).HasColumnName("country").HasColumnType("text");
            b.Property(x => x.Region).HasColumnName("region").HasColumnType("text");
            b.Property(x => x.PostalCode).HasColumnName("postal_code").HasColumnType("text");
            b.Property(x => x.Phone).HasColumnName("phone").HasColumnType("text");
            b.Property(x => x.Email).HasColumnName("email").HasColumnType("text");
            b.Property(x => x.TimeZone).HasColumnName("time_zone").HasColumnType("text");
        });
        modelBuilder.Entity<LocationLoginConfigRow>(b =>
        {
            b.ToTable("location_login_config", Schema);
            b.HasKey(x => x.LocationId).HasName("location_login_config_pkey");
            b.Property(x => x.LocationId).HasColumnName("location_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.ProviderKind).HasColumnName("provider_kind").HasColumnType("text").IsRequired();
            b.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnType("text").IsRequired();
            b.PrimitiveCollection(x => x.EntraAllowedTenantIds).HasColumnName("entra_allowed_tenant_ids").HasColumnType("uuid[]").IsRequired();
            b.Property(x => x.OidcAuthority).HasColumnName("oidc_authority").HasColumnType("text");
            b.Property(x => x.OidcClientId).HasColumnName("oidc_client_id").HasColumnType("text");
            b.Property(x => x.SecretRef).HasColumnName("secret_ref").HasColumnType("text");
            b.Property(x => x.AllowDbFallback).HasColumnName("allow_db_fallback").HasColumnType("boolean");
            b.Property(x => x.MfaRequired).HasColumnName("mfa_required").HasColumnType("boolean");
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasColumnType("uuid");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.RowVersion).HasColumnName("row_version").HasColumnType("bigint").IsConcurrencyToken();
        });
        modelBuilder.Entity<LocationLoginConfigChange>(b =>
        {
            b.ToTable("location_login_config_change", Schema);
            b.HasKey(x => x.ChangeId).HasName("location_login_config_change_pkey");
            b.Property(x => x.ChangeId).HasColumnName("change_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.LocationId).HasColumnName("location_id").HasColumnType("uuid");
            b.Property(x => x.ActorUserId).HasColumnName("actor_user_id").HasColumnType("uuid");
            b.Property(x => x.AdminType).HasColumnName("admin_type").HasColumnType("text").IsRequired();
            b.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text");
            b.Property(x => x.At).HasColumnName("at").HasColumnType("timestamp with time zone");
            b.Property(x => x.ChangeKind).HasColumnName("change_kind").HasColumnType("text").IsRequired();
            b.PrimitiveCollection(x => x.ChangedFields).HasColumnName("changed_fields").HasColumnType("text[]").IsRequired();
            b.Property(x => x.Before).HasColumnName("before").HasColumnType("jsonb");
            b.Property(x => x.After).HasColumnName("after").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.RowVersion).HasColumnName("row_version").HasColumnType("bigint");
        });
        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("app_user", Schema);
            b.HasKey(x => x.UserId).HasName("app_user_pkey");
            b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.PartnerId).HasColumnName("partner_id").HasColumnType("uuid");
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("text").IsRequired();
            b.Property(x => x.LockedUntil).HasColumnName("locked_until").HasColumnType("timestamp with time zone");
        });
        modelBuilder.Entity<ExternalLogin>(b =>
        {
            b.ToTable("external_login", Schema);
            b.HasKey(x => new { x.ProviderKind, x.Issuer, x.TenantId, x.Subject }).HasName("external_login_pkey");
            b.Property(x => x.ProviderKind).HasColumnName("provider_kind").HasColumnType("text").IsRequired();
            b.Property(x => x.Issuer).HasColumnName("issuer").HasColumnType("text").IsRequired();
            b.Property(x => x.TenantId).HasColumnName("tenant_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.Subject).HasColumnName("subject").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid");
            b.Property(x => x.LinkedAt).HasColumnName("linked_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.LinkedBy).HasColumnName("linked_by").HasColumnType("uuid");
        });
        modelBuilder.Entity<UserLocation>(b =>
        {
            b.ToTable("user_location", Schema);
            b.HasKey(x => new { x.UserId, x.LocationId }).HasName("user_location_pkey");
            b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.LocationId).HasColumnName("location_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        });
        modelBuilder.Entity<AuditOutbox>(b =>
        {
            b.ToTable("audit_outbox", Schema);
            b.HasKey(x => x.EventId).HasName("audit_outbox_pkey");
            b.Property(x => x.EventId).HasColumnName("event_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.EventType).HasColumnName("event_type").HasColumnType("text").IsRequired();
            b.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
        });
        modelBuilder.Entity<OidcReplay>(b =>
        {
            b.ToTable("oidc_replay", Schema);
            b.HasKey(x => new { x.Kind, x.KeyHash }).HasName("oidc_replay_pkey");
            b.Property(x => x.Kind).HasColumnName("kind").HasColumnType("text").IsRequired();
            b.Property(x => x.KeyHash).HasColumnName("key_hash").HasColumnType("bytea").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        });
        modelBuilder.Entity<Session>(b =>
        {
            b.ToTable("session", Schema);
            b.HasKey(x => x.SessionId).HasName("session_pkey");
            b.Property(x => x.SessionId).HasColumnName("session_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.CookieKeyHash).HasColumnName("cookie_key_hash").HasColumnType("bytea").IsRequired();
            b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid");
            b.Property(x => x.PartnerId).HasColumnName("partner_id").HasColumnType("uuid");
            b.Property(x => x.LocationId).HasColumnName("location_id").HasColumnType("uuid");
            b.Property(x => x.AdminType).HasColumnName("admin_type").HasColumnType("text");
            b.Property(x => x.SessionKind).HasColumnName("session_kind").HasColumnType("text").IsRequired();
            b.Property(x => x.LoginProvider).HasColumnName("login_provider").HasColumnType("text").IsRequired();
            b.Property(x => x.EntraTenantId).HasColumnName("entra_tenant_id").HasColumnType("uuid");
            b.Property(x => x.ConfigRowVersion).HasColumnName("config_row_version").HasColumnType("bigint");
            b.PrimitiveCollection(x => x.Amr).HasColumnName("amr").HasColumnType("text[]").IsRequired();
            b.Property(x => x.AuthTime).HasColumnName("auth_time").HasColumnType("timestamp with time zone");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.Interactive).HasColumnName("interactive").HasColumnType("boolean");
            b.Property(x => x.ViaReturn).HasColumnName("via_return").HasColumnType("boolean");
            b.Property(x => x.CodeIssued).HasColumnName("code_issued").HasColumnType("boolean");
            b.Property(x => x.InteractiveUsedAt).HasColumnName("interactive_used_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.HandoffUsedAt).HasColumnName("handoff_used_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.CageConsumedAt).HasColumnName("cage_consumed_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.RevokedReason).HasColumnName("revoked_reason").HasColumnType("text");
        });
        // Slice A (migration 20261105080000_S2aPartnerReasonBounds).
        modelBuilder.Entity<PartnerReasonBounds>(b =>
        {
            b.ToTable("partner_reason_bounds", Schema);
            b.HasKey(x => x.PartnerId).HasName("partner_reason_bounds_pkey");
            b.Property(x => x.PartnerId).HasColumnName("partner_id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.ReasonMinLength).HasColumnName("reason_min_length").HasColumnType("integer");
            b.Property(x => x.ReasonMaxLength).HasColumnName("reason_max_length").HasColumnType("integer");
            b.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasColumnType("uuid");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
            b.Property(x => x.RowVersion).HasColumnName("row_version").HasColumnType("bigint").IsConcurrencyToken();
        });
    }
}
