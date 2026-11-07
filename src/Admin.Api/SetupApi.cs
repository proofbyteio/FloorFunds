using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Admin.Api;

public sealed partial class LoginConfigApi
{
    public Task<IResult> PostClientAsync(HttpContext ctx, string partnerId) => Run(ctx, async who =>
    {
        if (!who.IsAdmin || !who.Grant.OrgCreateEdit)
        {
            var b0 = ParseObject(await TryReadBodyAsync(ctx));
            await DenyAsync(who, "not_admin", b0 is { } ob ? LoginConfigValidator.BodyPartner(ob) : null, null, []);
            throw Err(403, "forbidden");
        }
        var read = await ReadBodyAsync(ctx);
        if (read.TimedOut) throw BodyTimeout(ctx, who);
        if (read.TooLarge) throw Err(413, "payload_too_large");
        if (ParseObject(read.Bytes) is not { } o) throw Err(400, "malformed_body");
        if (!Guid.TryParseExact(partnerId, "D", out var urlPartner) || urlPartner == Guid.Empty)
            throw Validation([new("partner_id", "partner_id_format")]);
        var bodyPartner = LoginConfigValidator.BodyPartner(o);
        if (bodyPartner is { } bp && bp != urlPartner)
        {
            await DenyAsync(who, "forbidden_scope", bp, null, []);
            throw Err(404, "not_found");
        }
        if (who.IsPartnerAdmin && urlPartner != who.Id.PartnerId)
        {
            await DenyAsync(who, "forbidden_scope", urlPartner, null, []);
            throw Err(404, "not_found");
        }
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        await using var tx = await c.BeginTransactionAsync(ctx.RequestAborted);
        await SetScopeAsync(c, tx, who.Id.AdminType, who.Id.PartnerId);
        var errs = new List<FieldError>();
        var (partner, name, setup) = SetupValidator.ParseClient(o, errs);
        await SetupValidator.ApplyAsync(c, tx, setup, errs, ctx.RequestAborted);
        if (errs.Count > 0 || name is null || partner != urlPartner)
        {
            await tx.RollbackAsync(ctx.RequestAborted);
            if (partner != urlPartner && errs.All(e => e.Field != "partner_id")) errs.Add(new("partner_id", "partner_id_format"));
            throw Validation(errs);
        }
        var id = Guid.NewGuid();
        var now = Now();
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO auth.client (client_id, partner_id, name, address1, address2, city, region, country_code, postal_code,
                   region_unused_confirmed, postal_unused_confirmed, phone_e164, email, time_zone_id, created_by, created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """, c, tx))
        {
            ins.Parameters.Add(new() { Value = id });
            ins.Parameters.Add(new() { Value = urlPartner });
            ins.Parameters.Add(new() { Value = name });
            ins.Parameters.Add(new() { Value = setup.Address1 });
            ins.Parameters.Add(new() { Value = (object?)setup.Address2 ?? DBNull.Value });
            ins.Parameters.Add(new() { Value = setup.City });
            ins.Parameters.Add(new() { Value = (object?)setup.Region ?? DBNull.Value });
            ins.Parameters.Add(new() { Value = setup.CountryCode });
            ins.Parameters.Add(new() { Value = (object?)setup.PostalCode ?? DBNull.Value });
            ins.Parameters.Add(new() { Value = setup.RegionConfirmed });
            ins.Parameters.Add(new() { Value = setup.PostalConfirmed });
            ins.Parameters.Add(new() { Value = setup.PhoneE164 });
            ins.Parameters.Add(new() { Value = (object?)setup.Email ?? DBNull.Value });
            ins.Parameters.Add(new() { Value = setup.TimeZoneId });
            ins.Parameters.Add(new() { Value = who.Id.UserId });
            ins.Parameters.Add(new() { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            await ins.ExecuteNonQueryAsync(ctx.RequestAborted);
        }
        var details = ClientSetupEvent(id, setup);
        await outbox.InsertAsync(c, tx, AuditOutbox.SetupRecorded(Guid.CreateVersion7(), now, who.Id, who.RequestId, "client", id, urlPartner, null, details), ctx.RequestAborted);
        await tx.CommitAsync(ctx.RequestAborted);
        ctx.Response.Headers.Location = $"/api/orgs/partners/{urlPartner:D}/clients/{id:D}";
        return Json(201, ClientAddressJson(id, urlPartner, name, setup));
    });

    public Task<IResult> GetClientsAsync(HttpContext ctx, string partnerId) => Run(ctx, async who =>
    {
        if (!who.IsAdmin || !who.Grant.OrgCreateEdit) throw Err(403, "forbidden");
        if (!Guid.TryParseExact(partnerId, "D", out var partner) || partner == Guid.Empty) throw Err(404, "not_found");
        if (who.IsPartnerAdmin && partner != who.Id.PartnerId) throw Err(404, "not_found");
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        await using var tx = await c.BeginTransactionAsync(ctx.RequestAborted);
        await SetScopeAsync(c, tx, who.Id.AdminType, who.Id.PartnerId);
        var items = new JsonArray();
        await using (var cmd = new NpgsqlCommand("SELECT client_id, name FROM auth.client WHERE partner_id = $1 ORDER BY name COLLATE \"C\", client_id", c, tx))
        {
            cmd.Parameters.Add(new() { Value = partner });
            await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
            while (await r.ReadAsync(ctx.RequestAborted))
                items.Add(new JsonObject { ["client_id"] = r.GetGuid(0).ToString("D"), ["name"] = r.GetString(1) });
        }
        return Json(200, new JsonObject { ["clients"] = items });
    });

    public Task<IResult> GetClientAddressAsync(HttpContext ctx, string partnerId, string clientId) => Run(ctx, async who =>
    {
        if (!who.IsAdmin || !who.Grant.OrgCreateEdit) throw Err(403, "forbidden");
        if (!Guid.TryParseExact(partnerId, "D", out var partner) || partner == Guid.Empty
            || !Guid.TryParseExact(clientId, "D", out var id) || id == Guid.Empty)
            throw Err(404, "not_found");
        if (who.IsPartnerAdmin && partner != who.Id.PartnerId) throw Err(404, "not_found");
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        await using var tx = await c.BeginTransactionAsync(ctx.RequestAborted);
        await SetScopeAsync(c, tx, who.Id.AdminType, who.Id.PartnerId);
        await using var cmd = new NpgsqlCommand("""
            SELECT name, address1, address2, city, region, country_code, postal_code
            FROM auth.client WHERE client_id = $1 AND partner_id = $2
            """, c, tx);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = partner });
        await using var r = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
        if (!await r.ReadAsync(ctx.RequestAborted)) throw Err(404, "not_found");
        var body = new JsonObject
        {
            ["client_id"] = id.ToString("D"),
            ["partner_id"] = partner.ToString("D"),
            ["name"] = r.GetString(0),
            ["address1"] = r.GetString(1),
            ["address2"] = r.IsDBNull(2) ? null : r.GetString(2),
            ["city"] = r.GetString(3),
            ["region"] = r.IsDBNull(4) ? null : r.GetString(4),
            ["country_code"] = r.GetString(5).Trim(),
            ["postal_code"] = r.IsDBNull(6) ? null : r.GetString(6),
        };
        return Json(200, body);
    });

    async Task<Guid?> ClientPartnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id)
    {
        await using var cmd = new NpgsqlCommand("SELECT partner_id FROM auth.client WHERE client_id = $1", c, tx);
        cmd.Parameters.Add(new() { Value = id });
        var v = await cmd.ExecuteScalarAsync();
        return v is Guid g ? g : null;
    }

    static JsonObject SetupEvent(SetupValidator.Draft d) => new()
    {
        ["client_id"] = d.ClientId!.Value.ToString("D"),
        ["country_code"] = d.CountryCode,
        ["region_applicability"] = d.RegionApplicability,
        ["postal_applicability"] = d.PostalApplicability,
        ["region_unused_confirmed"] = d.RegionConfirmed,
        ["postal_unused_confirmed"] = d.PostalConfirmed,
        ["time_zone_id"] = d.TimeZoneId,
    };

    static JsonObject ClientSetupEvent(Guid id, SetupValidator.Draft d) => new()
    {
        ["entity"] = "client",
        ["client_id"] = id.ToString("D"),
        ["country_code"] = d.CountryCode,
        ["region_applicability"] = d.RegionApplicability,
        ["postal_applicability"] = d.PostalApplicability,
        ["region_unused_confirmed"] = d.RegionConfirmed,
        ["postal_unused_confirmed"] = d.PostalConfirmed,
        ["time_zone_id"] = d.TimeZoneId,
    };

    static JsonObject ClientAddressJson(Guid id, Guid partner, string name, SetupValidator.Draft d) => new()
    {
        ["client_id"] = id.ToString("D"),
        ["partner_id"] = partner.ToString("D"),
        ["name"] = name,
        ["address1"] = d.Address1,
        ["address2"] = d.Address2,
        ["city"] = d.City,
        ["region"] = d.Region,
        ["country_code"] = d.CountryCode,
        ["postal_code"] = d.PostalCode,
    };
}
