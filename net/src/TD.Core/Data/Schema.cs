namespace TD.Core.Data;

public static partial class Schema
{
    static Schema()
    {
        // ours (Migrations.Ours), next to the Python program's tables
        Columns["td_net_session"] = new[] { ("id", "uuid"), ("digest", "character varying"), ("user_id", "uuid"), ("created", "timestamp with time zone"), ("seen", "timestamp with time zone"),
            ("remember", "boolean"), ("mfa", "boolean"), ("auth_version", "integer"), ("device", "character varying"), ("address", "character varying") };
    }
}
