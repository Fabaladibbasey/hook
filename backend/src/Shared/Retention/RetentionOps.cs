namespace Hook.Shared.Retention;

// Tag values for the `op` dimension on hook.retention.swept.total. Mirrors the
// RetentionTableKeys const-class pattern so the sweep table and metric callers
// reference a single source of truth.
public static class RetentionOps
{
    public const string Delete = "delete";
    public const string Update = "update";
}
